using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// The SELECT executor. The single base-table path resolves the FROM table via the
/// <see cref="VfpSession"/> work-area model (reusing an open alias, else auto-opening a scratch
/// area it closes afterwards), drives <see cref="QueryOptimizer.FindRecords"/> for the WHERE
/// (full scan + residual — Rushmore index acceleration is intentionally NOT used here because the
/// SQL/ANSI <c>=</c> char-prefix semantics differ from the index's Xbase prefix model), then
/// projects, aggregates / groups, orders, de-duplicates and tops the rows.
/// <para>
/// The Phase-2 multi-table path (comma cross-join + INNER / LEFT JOIN, any number of joined tables)
/// runs a chain of nested loops over a COMPOSITE row context (<see cref="CompositeRowContext"/>) and
/// reuses the SAME post-processing pipeline (WHERE / GROUP BY / aggregates / HAVING / ORDER BY /
/// DISTINCT / TOP). TOP … PERCENT and SELECT … INTO TABLE/CURSOR materialize the result (see
/// <see cref="RunMaterializing"/>); only INTO ARRAY stays <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// Not thread-safe: use one instance per session/connection; do not share across threads.
/// </para>
/// </summary>
internal sealed class SelectExecutor
{
    private static readonly Regex AggRx =
        new(@"^\s*(SUM|AVG|COUNT|MIN|MAX)\s*\((?<arg>.*)\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SimpleFieldRx =
        new(@"^[\p{L}_][\p{L}\p{Nd}_]*(\s*\.\s*[\p{L}_][\p{L}\p{Nd}_]*)*$", RegexOptions.Compiled);

    private static readonly Regex SimpleQualifiedRx =
        new(@"^[\p{L}_][\p{L}\p{Nd}_]*\.[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.Compiled);

    private static readonly Regex HavingCmpRx =
        new(@"^\s*(?<l>.+?)\s*(?<op><=|>=|<>|!=|==|=|<|>)\s*(?<r>.+?)\s*$", RegexOptions.Singleline);

    private readonly VfpSession _session;

    /// <summary>When this executor runs a CORRELATED subquery, the outer query's current row is bound
    /// here: the inner query's column resolution falls back to it for references the inner sources do
    /// not own (the parent row context for correlation). <see langword="null"/> for a top-level query.</summary>
    private readonly IRowContext? _outer;

    /// <summary>The microVFP interpreter when it runs this query (its row, bound as <see cref="_outer"/>, is
    /// an <see cref="IVfpFunctionHost"/>); <see langword="null"/> on the plain SQL / ADO.NET path. Every row
    /// context the query evaluates on is then wrapped in a <see cref="HostedRow"/> (see <see cref="Bind"/>).</summary>
    private readonly IVfpFunctionHost? _host;

    // Per-query subquery memoization (valid for one outer-query execution). An UNCORRELATED subquery's
    // result set is computed once and reused for every outer row; correlation is detected once per node.
    private readonly Dictionary<SelectStatement, List<object?[]>> _uncorrelatedRows = new();
    private readonly Dictionary<SelectStatement, bool> _correlation = new();

    /// <summary>The WHERE conjuncts this executor actually pushed down onto the driving source before
    /// running the join chain — empty when nothing was pushed (no WHERE, or nothing in it read only the
    /// driving source, or the chain contains a RIGHT/FULL step). Test seam: proves the pushdown was
    /// WIRED UP, which a result-equivalence test alone cannot (a no-op pushdown would still match the
    /// oracle).</summary>
    internal IReadOnlyList<string> PushdownFilters { get; private set; } = Array.Empty<string>();

    public SelectExecutor(VfpSession session) : this(session, null) { }

    public SelectExecutor(VfpSession session, IRowContext? outer) : this(session, outer, outer as IVfpFunctionHost) { }

    /// <summary>A subquery executor: <paramref name="host"/> is the enclosing query's host, so an uncorrelated
    /// subquery (no outer row) still evaluates with the microVFP interpreter.</summary>
    private SelectExecutor(VfpSession session, IRowContext? outer, IVfpFunctionHost? host)
    {
        _session = session;
        // SQL's missing source columns fall back to memvars, never to the current work area's fields.
        _outer = outer is not null && ReferenceEquals(outer, host)
            ? new SqlHostRow(outer, session.MemoryBridge) : outer;
        _host = host;
    }

    /// <summary>The row context an expression of this query evaluates on: unchanged on the plain path, wrapped
    /// so the microVFP host answers the functions the engine does not implement when microVFP runs the query.</summary>
    private IRowContext Bind(IRowContext row) => _host is null ? row : new HostedRow(row, _host);

    /// <summary>The bind-time qualifier check for this query level: its own sources, plus whatever the
    /// enclosing scope (<see cref="_outer"/>: a correlated parent row and/or the interpreter) resolves.</summary>
    private QualifierBinder Binder(IReadOnlyList<QualifierBinder.Source> sources)
    {
        if (_outer is null && _host is null) return new QualifierBinder(_session, sources, null, hostBound: false);
        return new QualifierBinder(_session, sources, q => OuterKnows(q), hostBound: _host is not null)
            { OuterQueryKnows = q => OuterKnows(q, queryOnly: true) };
    }

    private bool OuterKnows(string q, bool queryOnly = false)
    {
        for (IRowContext? o = _outer; o is not null; )
        {
            switch (o)
            {
                case RowContext rc:
                    if (rc.HasAlias(q)) return true;
                    o = rc.Outer;
                    break;
                case CompositeRowContext cc:
                    if (cc.HasAlias(q)) return true;
                    o = cc.Outer;
                    break;
                case HostedRow hr:
                    o = hr.Inner;          // a microVFP-hosted outer row: look through the wrapper.
                    break;
                default:
                    // The interpreter: an open work area (read at its current record), or a defined
                    // variable such as an object reference. m. is handled by the binder itself.
                    return !queryOnly && _host is not null && HostKnows(q);
            }
        }
        // An uncorrelated hosted subquery has no outer row: the interpreter itself still resolves names.
        return !queryOnly && _outer is null && _host is not null && HostKnows(q);
    }

    private bool HostKnows(string qualifier)
        // m. remains the memory prefix even when a scalar variable happens to be named m.
        => !qualifier.Equals("m", StringComparison.OrdinalIgnoreCase)
            && (_session.FindAreaByAlias(qualifier) is not null
                || _session.MemoryBridge?.IsObjectQualifier(qualifier) == true);

    /// <summary>SQL can explicitly name another work area, but a bare name only falls back to memory.
    /// Undefined bare variables raise 12 when evaluated (not behind a short-circuited AND), as VFP9 does.</summary>
    private sealed class SqlHostRow : IRowContext, IVfpValueRow
    {
        private readonly IRowContext _host;
        private readonly IVfpMemoryBridge? _memory;

        public SqlHostRow(IRowContext host, IVfpMemoryBridge? memory)
        {
            _host = host; _memory = memory;
        }

        public int RecNo => _host.RecNo;
        public int RecCount => _host.RecCount;
        public bool Deleted => _host.Deleted;

        public VfpValue GetValue(string name) => name.Contains('.')
            ? VfpRuntime.GetField(_host, name) : VfpValue.FromClr(GetField(name));

        public object? GetField(string name)
        {
            if (name.Contains('.')) return _host.GetField(name);
            if (name.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return null;
            if (_memory is not null && _memory.TryReadMemvar(name, out var value)) return value;
            throw new FoxDbfSqlException($"Variable '{name.ToUpperInvariant()}' is not found.") { VfpErrorNumber = 12 };
        }
    }

    /// <summary>
    /// VFP9 field-read semantics (set by the microVFP runtime): a BLANK field — null bit clear, or a
    /// non-NULL-able column — reads as its typed empty (0 / {} / "") everywhere the query reads a row
    /// (WHERE, projections, aggregates, ORDER BY, subqueries), and only a set <c>_NullFlags</c> bit is
    /// <c>.NULL.</c>; an INTO CURSOR/TABLE column that receives a <c>.NULL.</c> is created NULL-able so the
    /// value survives. Off (the default) keeps the public <see cref="VfpSession.Execute"/> / ADO.NET
    /// contract: a blank N/F/D/T/M field reads as <see langword="null"/> (IsDBNull).
    /// </summary>
    internal bool VfpBlankFields { get; init; }

    /// <summary>The session's SQL evaluation context, carrying <see cref="VfpBlankFields"/> down to the
    /// optimizer's residual row reads.</summary>
    private EvaluationContext SqlContext()
    {
        var ctx = _session.SqlContext();
        ctx.VfpBlankFields = VfpBlankFields;
        return ctx;
    }

    /// <summary>One field read from a source record under this executor's blank semantics.</summary>
    private static object? ReadField(DbfRecord rec, string field, bool vfpBlanks)
        => vfpBlanks ? rec.GetVfpValue(field) : rec.GetExpressionValue(field);

    public SqlResult Run(SelectStatement sel)
    {
        try { return RunCore(sel); }
        catch (ExpressionException ex) when (ex.VfpErrorNumber is not null)
        {
            throw new FoxDbfSqlException(ex.Message, ex) { VfpErrorNumber = ex.VfpErrorNumber };
        }
    }

    private SqlResult RunCore(SelectStatement sel)
    {
        PushdownFilters = Array.Empty<string>(); // reset: RunSingle / RunUnion never push

        // UNION owns its overall ORDER BY / TOP / INTO modifiers. VFP's grammar attaches a
        // trailing INTO to the final SELECT branch rather than to the root statement.
        if (sel.Union is not null)
            return RunUnion(sel);

        // TOP … PERCENT and SELECT … INTO TABLE/CURSOR both need the FULLY materialized (ORDER BY-applied)
        // result before they can act, so they run the plain query first, then post-process. Plain SELECT
        // (no PERCENT, no INTO) keeps its original, unchanged path.
        if (sel.TopPercent || sel.Into is not null)
            return RunMaterializing(sel);

        bool multiTable = sel.Joins.Count > 0 || sel.From.Count > 1;
        return multiTable ? RunJoin(sel) : RunSingle(sel);
    }

    // =======================================================================================
    //  TOP n PERCENT + SELECT … INTO TABLE | CURSOR materialization.
    // =======================================================================================

    /// <summary>
    /// Runs the query for a statement that carries TOP … PERCENT and/or an INTO clause. The core query
    /// (this same statement with PERCENT and INTO stripped) is executed through the normal pipeline so
    /// ORDER BY / DISTINCT / GROUP BY all apply unchanged; then TOP … PERCENT keeps the first
    /// CEIL(n/100 · total) rows of that ordered result, and finally an INTO clause materializes the rows
    /// into a new <c>.dbf</c> (INTO TABLE) or a session-registered temp cursor (INTO CURSOR).
    /// </summary>
    private SqlResult RunMaterializing(SelectStatement sel)
    {
        if (sel.Into is { } destination)
            _session.ValidateSelectInto(destination, sel.From.Concat(sel.Joins.Select(j => j.Source)));

        // The core query: drop PERCENT (an integer TOP is kept verbatim — only PERCENT must be applied
        // post-materialization) and drop INTO. With both stripped, Run dispatches down the normal path.
        var core = sel with
        {
            Top = sel.TopPercent ? null : sel.Top,
            TopPercent = false,
            Into = null,
        };

        var baseResult = Run(core);
        var columns = baseResult.Columns;
        var rows = baseResult.Rows.ToList();

        // TOP n PERCENT — applied AFTER ORDER BY against the materialized total (VFP rounds UP).
        if (sel.TopPercent && sel.Top is int pct)
            rows = ApplyTopPercent(rows, pct);

        if (sel.Into is { } into)
            return MaterializeInto(into, columns, rows);

        return new SqlResult(columns, rows);
    }

    /// <summary>VFP's TOP n PERCENT count rule: keep CEIL(n/100 · total) rows (rounding UP), clamped to
    /// [0, total]. n &lt;= 0 (and an empty source) → 0 rows; n &gt;= 100 → all rows; ties already follow
    /// the materialized ORDER BY order, so the kept rows are simply the ordered prefix.</summary>
    private static List<object?[]> ApplyTopPercent(List<object?[]> rows, int percent)
    {
        int total = rows.Count;
        int keep;
        if (percent <= 0 || total == 0) keep = 0;
        else if (percent >= 100) keep = total;
        else keep = (int)Math.Ceiling(percent / 100.0 * total);
        if (keep >= total) return rows;
        if (keep <= 0) return new List<object?[]>();
        return rows.Take(keep).ToList();
    }

    /// <summary>Materializes the result rows into a NEW <c>.dbf</c>. INTO TABLE/DBF creates a free table
    /// in the session's data directory; INTO CURSOR creates a TEMP <c>.dbf</c> and registers it as an
    /// open work area (alias = the cursor name) so a follow-up SELECT against the name works. Either way
    /// the result is a DML-style row-count (the materialized record count, available as _TALLY).</summary>
    private SqlResult MaterializeInto(IntoClause into, IReadOnlyList<SqlColumn> columns, List<object?[]> rows)
    {
        if (into.Kind == IntoKind.Array)
        {
            // SELECT … INTO ARRAY lands the result grid in the ACTIVE data session's memvar array store via
            // the bound interpreter's bridge; the row count doubles as _TALLY (returned by ExecuteNonQuery).
            // Per VFP the array is 2-D even for a single column (an N×1 array); ZERO rows leave the array
            // untouched (the bridge handles that) and set _TALLY=0. No interpreter bound ⇒ no memvar store.
            var bridge = _session.MemoryBridge
                ?? throw new FoxDbfSqlException(
                    "SELECT … INTO ARRAY requires the microVFP memory store; none is bound to this session.");
            bridge.StoreQueryIntoArray(into.Name, rows.Count, columns.Count, rows);
            return SqlResult.Dml(rows.Count);
        }

        var defs = BuildColumnDefs(columns);

        // Preserve result NULLs on every SQL entry point, including a character expression such
        // as STR(nullable_number): writing a blank would turn it into a non-NULL string.
        for (int i = 0; i < defs.Count; i++)
        {
            int col = i;
            if (rows.Any(r => col < r.Length && r[col] is null))
                defs[i] = defs[i] with { Nullable = true };
        }

        if (into.Kind == IntoKind.Table)
        {
            string path = _session.ResolveIntoTablePath(into.Name);
            // VFP9 closes an unrelated open destination before replacing its file (SET SAFETY OFF).
            _session.CloseAreasForPath(Path.GetFullPath(path));
            using (var w = DbfWriter.Create(path, defs, _session.NewTableOptions with { Overwrite = true }))
                w.AppendRecords(rows);
            return SqlResult.Dml(rows.Count);
        }

        // INTO CURSOR — we ALWAYS materialize a real temp table (effectively NOFILTER semantics);
        // READWRITE is implicit (it is a real, updatable table). Register it in the session so the
        // cursor alias is queryable until the session is disposed or the name is reused.
        string temp = _session.NewCursorTempPath(into.Name);
        using (var w = DbfWriter.Create(temp, defs, _session.NewTableOptions with { Overwrite = true }))
            w.AppendRecords(rows);
        _session.RegisterCursor(into.Name, temp);
        return SqlResult.Dml(rows.Count);
    }

    /// <summary>microVFP: VFP9 types a result column from its evaluated value. A UDF or microVFP function has no
    /// static type (the inference falls back to an unsized character column), so such a column takes the type of
    /// its first non-<c>.NULL.</c> value when that is a number, logical, date or datetime.</summary>
    private static IReadOnlyList<SqlColumn> TypeUntypedColumnsByValue(
        IReadOnlyList<SqlColumn> columns, List<(object?[] row, IRowContext rep)> rows)
    {
        SqlColumn[]? typed = null;
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.VfpType != 'C' || col.Length > 0) continue;
            object? first = null;
            foreach (var (r, _) in rows)
                if (i < r.Length && r[i] is { } v) { first = v; break; }
            (char Vfp, Type Clr)? byValue = first switch
            {
                decimal or double or float or int or long => ('N', typeof(decimal)),
                bool => ('L', typeof(bool)),
                DateOnly => ('D', typeof(DateOnly)),
                DateTime => ('T', typeof(DateTime)),
                _ => null,
            };
            if (byValue is not { } t) continue;
            typed ??= columns.ToArray();
            typed[i] = new SqlColumn(col.Name, t.Vfp, 0, 0, t.Clr);
        }
        return typed ?? columns;
    }

    private static IReadOnlyList<SqlColumn> SizeRuntimeColumns(
        IReadOnlyList<SqlColumn> columns, IReadOnlyList<SelectItem> items,
        Func<SelectItem, int> starWidth, Func<IRowContext?> firstSourceRow, EvaluationContext ctx, ISchema schema)
    {
        SqlColumn[]? sized = null;
        IRowContext? sample = null;
        int position = 0;
        foreach (var item in items)
        {
            if (item.IsStar) { position += starWidth(item); continue; }
            int i = position++;
            var col = columns[i];
            if (!ContainsAggregate(item) && item.Expression is { } expression
                && expression.ContainsFunction(static (n, a) => (n == "IIF" && a == 3) || (n == "VAL" && a == 1)))
            {
                sample ??= firstSourceRow();
                if (sample is not null)
                {
                    var info = expression.InferSqlType(schema, sample, ctx);
                    var (type, clr) = MapSqlInfer(info);
                    sized ??= columns.ToArray();
                    col = sized[i] = new SqlColumn(col.Name, type, info.Length, info.Decimals, clr);
                }
            }
            if (col.VfpType != 'C' || col.Length > 0 || ContainsAggregate(item)) continue;
            sample ??= firstSourceRow();
            if (sample is null) break;
            var value = item.Expression!.Evaluate(sample, ctx);
            if (value.Type != VfpType.Character) continue;
            // VFP9 sizes an otherwise unknown string from the first source record, before WHERE
            // or ORDER BY. Later strings do not widen the cursor (TRANSFORM and string UDFs).
            sized ??= columns.ToArray();
            sized[i] = new SqlColumn(col.Name, 'C', Math.Max(1, value.AsString.Length), 0, typeof(string));
        }
        return sized ?? columns;
    }

    /// <summary>Derives the output <see cref="DbfColumnDef"/> set from the result schema: each column's
    /// VFP type drives a sensible on-disk field (fixed-width types take their canonical length; N/F clamp
    /// to a valid 1–20 width; character types keep their inferred width and reject values above 254),
    /// with field names made DBF-legal (≤ 10 chars, unique, never the reserved <c>_NullFlags</c>).</summary>
    private static List<DbfColumnDef> BuildColumnDefs(IReadOnlyList<SqlColumn> columns)
    {
        var defs = new List<DbfColumnDef>(columns.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_NullFlags" };
        foreach (var col in columns)
        {
            string name = SafeFieldName(col.Name, used);
            used.Add(name);
            defs.Add(ToColumnDef(col, name) with { Binary = col.IsBinary });
        }
        return defs;
    }

    private static DbfColumnDef ToColumnDef(SqlColumn col, string name)
    {
        switch (char.ToUpperInvariant(col.VfpType))
        {
            case 'I': return new DbfColumnDef(name, 'I');             // Integer (fixed 4)
            case 'Y': return new DbfColumnDef(name, 'Y');             // Currency (fixed 8)
            case 'B': return new DbfColumnDef(name, 'B', 8, col.Decimals); // Double (fixed 8)
            case 'D': return new DbfColumnDef(name, 'D');             // Date (fixed 8)
            case 'T': case '@': return new DbfColumnDef(name, 'T');   // DateTime (fixed 8)
            case 'L': return new DbfColumnDef(name, 'L');             // Logical (fixed 1)
            case 'V': case 'Q': return new DbfColumnDef(name, col.VfpType, col.Length);
            case 'M': case 'G': case 'P': case 'W':                   // memo / binary blob (FPT-backed)
                return new DbfColumnDef(name, 'M');
            case 'N':
            case 'F':
            {
                int len = col.Length is >= 1 and <= 20 ? col.Length : 18;
                // When the width came from an expression Infer that yielded NO size (Length<=0) and NO
                // decimals, the value is a computed result (e.g. amount/3, amount*1.5) whose fractional
                // part must survive the round-trip — emit a non-zero decimal default rather than N(18,0),
                // which would silently truncate every quotient/product to an integer.
                int dec = (col.Length <= 0 && col.Decimals <= 0)
                    ? Math.Min(4, Math.Max(0, len - 2))
                    : (col.Decimals < 0 ? 0 : Math.Min(col.Decimals, Math.Max(0, len - 1)));
                return new DbfColumnDef(name, col.VfpType, len, dec);
            }
            default: // C / V / Q and anything else → character
            {
                if (col.Length > 254)
                    throw new FoxDbfSqlException(
                        $"Character column '{name}' requires length {col.Length}, exceeding the VFP maximum of 254; target was not created.");
                int len = col.Length >= 1 ? col.Length : 10;
                return new DbfColumnDef(name, 'C', len);
            }
        }
    }

    /// <summary>Coerces a result-column name into a DBF-legal field name: trimmed to ≤ 10 chars, made
    /// unique against <paramref name="used"/> (case-insensitive) and never the reserved system name.</summary>
    private static string SafeFieldName(string raw, HashSet<string> used)
    {
        string name = string.IsNullOrWhiteSpace(raw) ? "COL" : raw.Trim();
        if (name.Length > 10) name = name[..10];

        string baseName = name;
        int k = 1;
        while (used.Contains(name) || string.Equals(name, "_NullFlags", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = "_" + (++k);
            int keep = Math.Max(1, Math.Min(baseName.Length, 10 - suffix.Length));
            name = baseName[..keep] + suffix;
        }
        return name;
    }

    // =======================================================================================
    //  Single base table (Phase-1b) — behaviour preserved verbatim.
    // =======================================================================================

    private SqlResult RunSingle(SelectStatement sel)
    {
        var from = sel.From[0];
        var ctx = SqlContext();

        // Resolve FROM: reuse an open work area whose alias matches, else auto-open a scratch handle.
        var existing = _session.FindAreaByAlias(from.Alias ?? from.Table);
        DbfTable table;
        CdxFile? scratchCdx = null;
        bool ownsHandle;
        if (existing is not null)
        {
            table = existing.Table;
            ownsHandle = false;
        }
        else
        {
            (table, scratchCdx) = _session.OpenNamedTable(from.Table);
            ownsHandle = true;
        }

        try
        {
            string rowAlias = from.Alias ?? existing?.Alias ?? from.Table;
            Binder(new[] { new QualifierBinder.Source(rowAlias, from.Table,
                new HashSet<string>(table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase)) })
                .CheckSelect(sel);
            return Execute(table, existing?.Cdx ?? scratchCdx, ctx, sel, rowAlias);
        }
        finally
        {
            if (ownsHandle)
            {
                scratchCdx?.Dispose();
                table.Dispose();
            }
        }
    }

    private SqlResult Execute(DbfTable table, CdxFile? cdx, EvaluationContext ctx, SelectStatement sel, string alias)
    {
        var schema = new TableSchema(table, alias);

        // Size unknown character expressions before filtering or sorting the source rows.
        var columns = BuildColumns(table, alias, new SqlInferenceSchema(schema, null, ctx), sel.Items);
        Func<SelectItem, int> starWidth = _ => table.Columns.Count;
        Func<string, bool> isSourceField = f => FindColumn(table, f) is not null;
        var order = new OrderScope(sel.Items, starWidth, isSourceField, columns.Count);
        var groupBy = ValidateClauses(sel, order, schema, _ => table.Columns.Select(c => alias + "." + c.Name),
            table.Columns.Select(c => alias + "." + c.Name));
        columns = SizeRuntimeColumns(columns, sel.Items, _ => table.Columns.Count, () =>
        {
            var first = table.EnumerateAll(includeDeleted: true).Select(r => (DbfRecord?)r).FirstOrDefault();
            return first is null ? null : Bind(new RowContext(first.Value, 1, table.RecordCount, alias,
                _outer, new HashSet<string>(table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase),
                VfpBlankFields));
        }, ctx, schema);

        if (sel.WherePredicate is not null) AddInfixIndexes(ctx, cdx, alias);
        var rows = CollectSingleRows(table, cdx, ctx, sel, alias);
        ctx.SqlIndexedFields = null; // HAVING is evaluated after Rushmore, without index validation.

        // (3) aggregate vs row projection. Each output row is carried together with a representative
        // row context so ORDER BY / TOP can re-evaluate row-level keys after grouping.
        bool aggregate = sel.GroupBy.Count > 0 || sel.Items.Any(ContainsAggregate);

        List<(object?[] row, IRowContext rep)> pairs;
        if (!aggregate)
        {
            pairs = new List<(object?[], IRowContext)>(rows.Count);
            foreach (var r in rows)
                pairs.Add((ProjectRow(table, r, ctx, sel.Items), r.Ctx));
        }
        else
        {
            pairs = Aggregate(ctx, sel with { GroupBy = groupBy }, order, rows.ConvertAll(r => r.Ctx), columns);
        }

        // (4) HAVING without grouping, DISTINCT, ORDER BY, TOP.
        return Materialize(columns, PostProcess(pairs, sel, order, aggregate, ctx, columns));
    }

    /// <summary>
    /// Resolves the WHERE for a single base table into surviving rows (recno order).
    /// <para>
    /// FAST PATH (no subquery predicate, outer/correlation binding or interpreter host):
    /// <see cref="QueryOptimizer.FindRecords"/> drives the WHERE text (structural CDX + residual; SET DELETED
    /// honoured by ctx), and the matched recnos are materialized through
    /// <see cref="DbfTable.EnumerateAll(bool)"/> so SET DELETED OFF keeps matched deleted rows.
    /// </para>
    /// <para>
    /// ROW-BY-ROW PATH (a WHERE predicate tree with subquery atoms, and/or a bound outer row for a
    /// correlated subquery): each candidate row is bound in an alias-/outer-aware <see cref="RowContext"/>
    /// and the predicate (or plain expression, with the outer row in scope) is evaluated per row.
    /// </para>
    /// </summary>
    private List<Row> CollectSingleRows(DbfTable table, CdxFile? cdx, EvaluationContext ctx, SelectStatement sel, string alias)
    {
        // The xBase optimizer would rebind ISBLANK(field) to raw field-reference semantics.
        if (sel.WherePredicate is null && _outer is null && _host is null
            && sel.Where?.ContainsFunction(static (name, _) => name == "ISBLANK") != true)
        {
            // The optimizer's residual resolves BARE field names only, so a qualifier naming THIS
            // query's own source (alias.field / table.field) is dropped first — as the join pushdown
            // does for its driving source. With no outer row bound, any other qualifier cannot be a
            // correlated reference and is left untouched.
            string? filter = sel.Where?.Text;
            filter = string.IsNullOrWhiteSpace(filter) ? ".T." : StripDrivingQualifier(filter, alias);
            var qr = _session.FindRecords(table, cdx, filter, ctx);
            var hits = new HashSet<int>(qr.RecordNumbers);
            var rows = new List<Row>(hits.Count);
            int recno = 0;
            foreach (var rec in table.EnumerateAll(includeDeleted: true))
            {
                recno++;
                if (!hits.Contains(recno)) continue;
                rows.Add(new Row(rec, new RowContext(rec, recno, table.RecordCount, alias, VfpBlankFields)));
            }
            return rows;
        }

        var colNames = new HashSet<string>(
            table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var outRows = new List<Row>();
        int rn = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            rn++;
            if (ctx.Deleted && rec.IsDeleted) continue; // SET DELETED ON excludes deleted rows.
            var rc = Bind(new RowContext(rec, rn, table.RecordCount, alias, _outer, colNames, VfpBlankFields));
            bool keep;
            if (sel.WherePredicate is not null)
                keep = IsPredicateCandidate(sel.WherePredicate, rc, ctx) && EvalPredicate(sel.WherePredicate, rc, ctx) == true;
            else if (sel.Where is not null)
            {
                var v = sel.Where.Evaluate(rc, ctx);
                keep = v.Type == VfpType.Logical && v.AsLogical;
            }
            else keep = true;
            if (keep) outRows.Add(new Row(rec, rc));
        }
        return outRows;
    }

    // =======================================================================================
    //  Subquery predicates (IN / NOT IN / EXISTS / NOT EXISTS / scalar) — three-valued logic.
    // =======================================================================================

    private static bool IsPredicateCandidate(SqlPredicate predicate, IRowContext row, EvaluationContext context, bool negated = false) => predicate switch
    {
        AndPredicate conjunction when !negated => IsPredicateCandidate(conjunction.Left, row, context) && IsPredicateCandidate(conjunction.Right, row, context),
        OrPredicate disjunction when negated => IsPredicateCandidate(disjunction.Left, row, context, true) && IsPredicateCandidate(disjunction.Right, row, context, true),
        NotPredicate negation => IsPredicateCandidate(negation.Operand, row, context, !negated),
        ExprPredicate expr => expr.Expression.IsSqlCandidate(row, context, negated),
        _ => true,
    };

    internal static void AddInfixIndexes(EvaluationContext context, CdxFile? cdx, string alias)
    {
        // VFP9 SQL retains these index-dependent error rules even with SET OPTIMIZE OFF.
        if (cdx is null) return;
        foreach (string name in cdx.TagNames)
        {
            var tag = cdx.Tag(name)!;
            string field = tag.KeyExpression.Trim();
            if (tag.IsUnique || !string.IsNullOrWhiteSpace(tag.ForExpression) ||
                !SimpleFieldRx.IsMatch(field) || field.Contains('.')) continue;
            context.SqlIndexedFields ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            context.SqlIndexedFields.Add(field);
            context.SqlIndexedFields.Add(alias + "." + field);
        }
    }

    /// <summary>Evaluates a WHERE predicate tree against one outer/candidate row, in VFP three-valued
    /// logic (<see langword="true"/> / <see langword="false"/> / <see langword="null"/>=unknown). A row
    /// is kept by the caller only when the result is exactly <see langword="true"/>.</summary>
    private bool? EvalPredicate(SqlPredicate pred, IRowContext row, EvaluationContext ctx)
    {
        switch (pred)
        {
            case AndPredicate a: return And3(EvalPredicate(a.Left, row, ctx), EvalPredicate(a.Right, row, ctx));
            case OrPredicate o: return Or3(EvalPredicate(o.Left, row, ctx), EvalPredicate(o.Right, row, ctx));
            case NotPredicate n: return Not3(EvalPredicate(n.Operand, row, ctx));
            case ExprPredicate e:
            {
                var v = e.Expression.Evaluate(row, ctx);
                if (v.IsNull) return null;
                return v.Type == VfpType.Logical ? v.AsLogical : false;
            }
            case InSubqueryPredicate inp:
            {
                var x = inp.Left.Evaluate(row, ctx);
                var membership = InMembership(x, RunSubqueryValues(inp.Subquery, row), ctx);
                return inp.Negated ? Not3(membership) : membership;
            }
            case ExistsSubqueryPredicate ex:
                return RunSubqueryRows(ex.Subquery, row).Count > 0;
            case ScalarSubqueryPredicate sc:
                return EvalScalarSubquery(sc, row, ctx);
            default:
                throw new NotSupportedException($"Unsupported WHERE predicate '{pred.GetType().Name}'.");
        }
    }

    private static bool? And3(bool? a, bool? b)
        => a == false || b == false ? false : (a is null || b is null ? (bool?)null : true);

    private static bool? Or3(bool? a, bool? b)
        => a == true || b == true ? true : (a is null || b is null ? (bool?)null : false);

    private static bool? Not3(bool? a) => a is null ? null : !a.Value;

    /// <summary><c>x IN set</c> in three-valued logic, as VFP9 evaluates it (native VFP9 tables, with
    /// and without a CDX): a NULL <paramref name="x"/> is unknown; a match is true; no match is false
    /// unless the set contains a NULL, which makes it unknown. <c>x NOT IN set</c> (the caller negates)
    /// is therefore empty whenever the set contains a NULL. An empty set ⇒ false.</summary>
    private static bool? InMembership(VfpValue x, IReadOnlyList<VfpValue> set, EvaluationContext ctx)
        => VfpRuntime.InMembership(x, set, ctx);

    private bool? EvalScalarSubquery(ScalarSubqueryPredicate sc, IRowContext row, EvaluationContext ctx)
    {
        var rows = RunSubqueryRows(sc.Subquery, row);
        if (rows.Count > 1)
            throw new FoxDbfSqlException("SQL: Subquery returned more than one record.");
        var left = sc.Left.Evaluate(row, ctx);
        var rv = rows.Count == 0 ? VfpValue.Null : VfpValue.FromClr(rows[0].Length > 0 ? rows[0][0] : null);
        if (left.IsNull || rv.IsNull) return null; // a missing row (or NULL operand) ⇒ unknown.
        return CompareScalar(sc.Op, left, rv, ctx);
    }

    private static bool CompareScalar(string op, VfpValue l, VfpValue r, EvaluationContext ctx) => op switch
    {
        "=" => ValueEqSql(l, r, ctx),
        "==" => CompareVfp(l, r, ctx) == 0,
        "<>" or "!=" or "#" => !ValueEqSql(l, r, ctx),
        "<" => CompareVfp(l, r, ctx) < 0,
        "<=" => CompareVfp(l, r, ctx) <= 0,
        ">" => CompareVfp(l, r, ctx) > 0,
        ">=" => CompareVfp(l, r, ctx) >= 0,
        _ => throw new FoxDbfSqlException($"Unsupported comparison operator '{op}' in a scalar subquery."),
    };

    /// <summary>Runs a subquery and returns its rows. An UNCORRELATED subquery is executed ONCE and its
    /// rows cached (reused for every outer row); a CORRELATED subquery is executed per outer row with
    /// <paramref name="outerRow"/> bound as the parent row context.</summary>
    private IReadOnlyList<object?[]> RunSubqueryRows(SelectStatement sub, IRowContext outerRow)
    {
        if (!IsCorrelated(sub))
        {
            if (!_uncorrelatedRows.TryGetValue(sub, out var cached))
            {
                // Under microVFP the host IS the interpreter's row: binding it as the outer row resolves a memvar
                // inside the subquery exactly as the top-level query does (plain path: no host, no outer row).
                cached = new SelectExecutor(_session, _host as IRowContext, _host) { VfpBlankFields = VfpBlankFields }.Run(sub).Rows.ToList();
                _uncorrelatedRows[sub] = cached;
            }
            return cached;
        }
        return new SelectExecutor(_session, outerRow, _host) { VfpBlankFields = VfpBlankFields }.Run(sub).Rows.ToList();
    }

    /// <summary>The first-column values of a subquery's rows (for IN membership).</summary>
    private IReadOnlyList<VfpValue> RunSubqueryValues(SelectStatement sub, IRowContext outerRow)
    {
        var rows = RunSubqueryRows(sub, outerRow);
        var vals = new List<VfpValue>(rows.Count);
        foreach (var r in rows) vals.Add(VfpValue.FromClr(r.Length > 0 ? r[0] : null));
        return vals;
    }

    private static readonly Regex QualifiedRefRx =
        new(@"([\p{L}_][\p{L}\p{Nd}_]*)\s*\.\s*[\p{L}_]", RegexOptions.Compiled);

    /// <summary>A subquery is CORRELATED when one of its fragments references an alias its own FROM/JOIN
    /// sources do not own (i.e. an outer-query source). Computed once per node.</summary>
    private bool IsCorrelated(SelectStatement sub)
    {
        if (_correlation.TryGetValue(sub, out var c)) return c;
        c = ComputeCorrelated(sub);
        _correlation[sub] = c;
        return c;
    }

    private static bool ComputeCorrelated(SelectStatement sub)
    {
        var inner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fs in sub.From) inner.Add(fs.Alias ?? fs.Table);
        foreach (var j in sub.Joins) inner.Add(j.Source.Alias ?? j.Source.Table);

        foreach (var text in SubqueryRefTexts(sub))
            foreach (Match m in QualifiedRefRx.Matches(text))
                if (!inner.Contains(m.Groups[1].Value))
                    return true;

        // A nested sub-predicate could itself reference the outer row → never cache it.
        return sub.WherePredicate is not null;
    }

    private static IEnumerable<string> SubqueryRefTexts(SelectStatement sub)
    {
        if (sub.Where is not null) yield return sub.Where.Text;
        if (sub.Having is not null) yield return sub.Having.Text;
        foreach (var j in sub.Joins) yield return j.On.Text;
        foreach (var g in sub.GroupBy) yield return g.Text;
        foreach (var it in sub.Items) if (it.Expression is not null) yield return it.Expression.Text;
        foreach (var o in sub.OrderBy) if (o.Expression is not null) yield return o.Expression.Text;
    }

    // =======================================================================================
    //  UNION [ALL] (Phase-2).
    // =======================================================================================

    private SqlResult RunUnion(SelectStatement root)
    {
        // Flatten the UNION chain into branch SELECTs and ALL flags (allFlags[i-1] = how branch i combines).
        var selects = new List<SelectStatement>();
        var allFlags = new List<bool>();
        var cur = root;
        selects.Add(cur);
        while (cur.Union is not null)
        {
            allFlags.Add(cur.Union.All);
            cur = cur.Union.Query;
            selects.Add(cur);
        }

        // Prefer the VFP-style trailing destination. Retain the historical leading destination
        // extension as a fallback, but never materialize a middle branch independently.
        IntoClause? into = selects[^1].Into ?? root.Into;
        if (into is not null)
            _session.ValidateSelectInto(into, selects.SelectMany(s => s.From.Concat(s.Joins.Select(j => j.Source))));

        // Execute every branch's core (without union-level ORDER BY / TOP / INTO) first. UNION
        // compatibility is a schema-wide decision, so no rows may be accumulated or deduplicated
        // until all branch schemas are known.
        var branchResults = new List<SqlResult>(selects.Count);
        var branchColumns = new List<IReadOnlyList<SqlColumn>>();
        int firstCount = -1;

        for (int i = 0; i < selects.Count; i++)
        {
            var core = selects[i] with
            {
                OrderBy = Array.Empty<OrderItem>(),
                Top = null,
                TopPercent = false,
                Into = null,
                Union = null,
            };

            var result = Execute(core);

            // The branches union BY POSITION: every branch must have the SAME column count as the first.
            if (i == 0)
            {
                firstCount = result.Columns.Count;
            }
            else if (result.Columns.Count != firstCount)
                throw new FoxDbfSqlException(
                    $"UNION column count mismatch: first SELECT has {firstCount} columns, branch {i + 1} has {result.Columns.Count}.");

            branchResults.Add(result);
            branchColumns.Add(result.Columns);
        }

        // The result schema: per-column WIDENED type across every branch (names from the FIRST select).
        // All branch rows are coerced to this schema before any UNION DISTINCT operation, matching VFP's
        // comparison domain (for example DateOnly and midnight DateTime compare as equal).
        var accSchema = WidenUnionSchema(branchColumns);

        // Fold each UNION link from left to right. Coerce one branch at a time and immediately add it to
        // the running accumulator; this avoids retaining a second complete set of coerced branch rows.
        var acc = new List<object?[]>();
        for (int i = 0; i < branchResults.Count; i++)
        {
            foreach (var row in branchResults[i].Rows)
                acc.Add(CoerceRow(row, accSchema));
            if (i > 0 && !allFlags[i - 1])
                acc = Distinct(acc.Select((r, _) =>
                    (r, new DummyRowContext() as IRowContext)).ToList(), SqlContext())
                    .ConvertAll(p => p.row);
        }

        // Resolve each overall ORDER BY entry to a 0-based output-column index ONCE — explicit 1-based
        // ordinal, else by matching the column NAME against the (first-select) result schema. The SAME
        // resolved index is reused by the sort and all final UNION post-processing.
        var orderBy = selects[^1].OrderBy;
        var orderCols = new int[orderBy.Count];
        for (int k = 0; k < orderBy.Count; k++)
            orderCols[k] = ResolveUnionOrderCol(orderBy[k], accSchema);

        if (orderBy.Count > 0)
        {
            var ctx = SqlContext();
            IOrderedEnumerable<object?[]>? ordered = null;
            for (int k = 0; k < orderBy.Count; k++)
            {
                var ob = orderBy[k];
                var keysel = UnionColKey(orderCols[k]);
                var cmp = Comparer<object?[]>.Create((a, b) => CompareVfp(keysel(a), keysel(b), ctx));
                ordered = k == 0
                    ? (ob.Descending ? acc.OrderByDescending(x => x, cmp) : acc.OrderBy(x => x, cmp))
                    : (ob.Descending ? ordered!.ThenByDescending(x => x, cmp) : ordered!.ThenBy(x => x, cmp));
            }
            acc = ordered!.ToList();
        }

        // Overall TOP. VFP keeps the exact ordered prefix for both integer TOP and TOP PERCENT;
        // neither form extends the result across equal ORDER BY keys at the boundary.
        if (selects[0].Top is int top)
        {
            if (selects[0].TopPercent)
            {
                acc = ApplyTopPercent(acc, top);
            }
            else
            {
                top = Math.Max(0, top);
                if (top < acc.Count)
                    acc = acc.Take(top).ToList();
            }
        }

        var finalResult = MaterializeAlreadyCoerced(accSchema, acc);
        return into is null
            ? finalResult
            : MaterializeInto(into, finalResult.Columns, finalResult.Rows.ToList());
    }

    /// <summary>A union ORDER BY / TOP key selector reading the resolved 0-based output column.</summary>
    private static Func<object?[], VfpValue> UnionColKey(int colIdx) =>
        row => VfpValue.FromClr(colIdx >= 0 && colIdx < row.Length ? row[colIdx] : null);

    /// <summary>Resolves a union-level ORDER BY entry to a 0-based output-column index: an explicit
    /// 1-based ordinal, else the column NAME matched (whitespace-insensitive) against the result schema
    /// (names come from the first SELECT). Throws when neither resolves.</summary>
    private static int ResolveUnionOrderCol(OrderItem ob, IReadOnlyList<SqlColumn> accSchema)
    {
        if (ob.Ordinal.HasValue)
        {
            int ordinal = ob.Ordinal.Value;
            if (ordinal < 1 || ordinal > accSchema.Count)
                throw new FoxDbfSqlException(
                    $"UNION ORDER BY ordinal {ordinal} is outside the valid range 1..{accSchema.Count}.");
            return ordinal - 1;
        }
        if (ob.Expression is not null)
        {
            string exprText = StripWs(ob.Expression.Text).ToUpperInvariant();
            for (int i = 0; i < accSchema.Count; i++)
                if (string.Equals(exprText, StripWs(accSchema[i].Name).ToUpperInvariant(), StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        throw new FoxDbfSqlException(
            $"UNION ORDER BY column '{(ob.Expression?.Text ?? "?")}' not found in the first SELECT.");
    }

    // ---- UNION column-type widening -------------------------------------------------------

    private enum UnionCat { Numeric, Character, Date, DateTime, Logical, Other }

    private static UnionCat CatOf(char vfp) => char.ToUpperInvariant(vfp) switch
    {
        'N' or 'F' or 'B' or 'I' or 'Y' => UnionCat.Numeric,
        'C' or 'M' or 'V' => UnionCat.Character,
        'D' => UnionCat.Date,
        'T' or '@' => UnionCat.DateTime,
        'L' => UnionCat.Logical,
        _ => UnionCat.Other,
    };

    /// <summary>Builds the union result schema by widening each column position across all branches:
    /// the NAME comes from the first SELECT; the TYPE is the common/widened type (integers/currency
    /// promote to numeric/decimal, character lengths widen to the max, and dates widen to datetime).
    /// Other mixed categories are rejected with VFP error 1851.</summary>
    private static IReadOnlyList<SqlColumn> WidenUnionSchema(List<IReadOnlyList<SqlColumn>> branchColumns)
    {
        var first = branchColumns[0];
        var outCols = new List<SqlColumn>(first.Count);
        for (int c = 0; c < first.Count; c++)
        {
            var col = new List<SqlColumn>(branchColumns.Count);
            for (int b = 0; b < branchColumns.Count; b++)
                col.Add(branchColumns[b][c]);
            outCols.Add(WidenUnionColumn(first[c].Name, col));
        }
        return outCols;
    }

    private static SqlColumn WidenUnionColumn(string name, List<SqlColumn> cols)
    {
        char first = char.ToUpperInvariant(cols[0].VfpType);

        // All branches share the type char → keep the type/ClrType, widen length & decimals to the max.
        if (cols.All(x => char.ToUpperInvariant(x.VfpType) == first))
            return new SqlColumn(name, first, cols.Max(x => x.Length), cols.Max(x => x.Decimals), cols[0].ClrType);

        var cats = cols.Select(x => CatOf(x.VfpType)).Distinct().ToList();

        // Mixed numeric family → widened numeric (decimal unless every branch is double-backed).
        if (cats.Count == 1 && cats[0] == UnionCat.Numeric)
        {
            int len = cols.Max(x => x.Length);
            int dec = cols.Max(x => x.Decimals);
            return cols.All(x => x.ClrType == typeof(double))
                ? new SqlColumn(name, 'B', len, dec, typeof(double))
                : new SqlColumn(name, 'N', len, dec, typeof(decimal));
        }

        // Date + DateTime → DateTime (a date widens to datetime).
        if (cats.All(x => x is UnionCat.Date or UnionCat.DateTime))
            return new SqlColumn(name, 'T', 8, 0, typeof(DateTime));

        if (cats.Count == 1 && cats[0] == UnionCat.Character)
            return new SqlColumn(name, 'C', Math.Max(cols.Max(x => x.Length), 1), 0, typeof(string));

        SqlColumn incompatible = cols.First(x => char.ToUpperInvariant(x.VfpType) != first);
        throw new FoxDbfSqlException(
            $"SELECTs are not UNION compatible. Fields {cols[0].Name} and {incompatible.Name} are incompatible.")
        { VfpErrorNumber = 1851 };
    }

    private SqlResult Execute(SelectStatement sel)
    {
        bool multiTable = sel.Joins.Count > 0 || sel.From.Count > 1;
        return multiTable ? RunJoin(sel) : RunSingle(sel);
    }

    // =======================================================================================
    //  Multi-table / JOIN (Phase-2).
    // =======================================================================================

    private SqlResult RunJoin(SelectStatement sel)
    {
        var ctx = SqlContext();
        var owned = new List<JoinSource>();
        try
        {
            // Resolve every FROM source then every JOIN source, in textual order.
            var sources = new List<JoinSource>();
            foreach (var fs in sel.From) sources.Add(OpenSource(fs, ctx, owned));
            foreach (var j in sel.Joins) sources.Add(OpenSource(j.Source, ctx, owned));
            var src = sources.ToArray();

            // A FROM/JOIN list MUST give each source a DISTINCT alias: a duplicate alias would make
            // `alias.field` resolve to an arbitrary one of them (a silently-wrong self-join). Reject it.
            var seenAlias = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in src)
                if (!seenAlias.Add(s.Alias))
                    throw new FoxDbfSqlException(
                        $"Duplicate source alias '{s.Alias}' in FROM/JOIN; give each table a distinct alias.");

            var fromTables = sel.From.Select(f => f.Table).Concat(sel.Joins.Select(j => j.Source.Table)).ToArray();
            Binder(src.Select((s, i) => new QualifierBinder.Source(s.Alias, fromTables[i], s.ColNames)).ToArray())
                .CheckSelect(sel);

            var schema = new CompositeSchema(src);
            var columns = BuildJoinColumns(src, new SqlInferenceSchema(schema, null, ctx), sel.Items);
            Func<SelectItem, int> starWidth = it => StarWidthJoin(it, src);
            Func<string, bool> isSourceField = f => Array.Exists(src, s => s.ColNames.Contains(f));
            var order = new OrderScope(sel.Items, starWidth, isSourceField, columns.Count);
            var groupBy = ValidateClauses(sel, order, schema, it => src
                .Where(s => it.StarAlias is null || string.Equals(it.StarAlias, s.Alias, StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s.Columns.Select(c => s.Alias + "." + c.Name)),
                src.SelectMany(s => s.Columns.Select(c => s.Alias + "." + c.Name)));
            columns = SizeRuntimeColumns(columns, sel.Items, it => StarWidthJoin(it, src), () =>
            {
                var first = src.Select(s => s.Table.EnumerateAll(includeDeleted: true)
                    .Select(r => (DbfRecord?)r).FirstOrDefault()).ToArray();
                return first.Any(r => r is null) ? null
                    : Bind(new CompositeRowContext(src, first, _outer, VfpBlankFields));
            }, ctx, schema);

            if (sel.WherePredicate is not null || sel.Joins.Any(j => j.On.HasSqlInfix))
                foreach (var source in src) AddInfixIndexes(ctx, source.Cdx, source.Alias);

            // Per-step descriptor for sources[1..]: comma-FROM entries are an inner cross join
            // (ON is implicitly .T.); each explicit JOIN carries its own type + ON predicate.
            var steps = new JoinStep[src.Length];
            int idx = 1;
            for (int i = 1; i < sel.From.Count; i++)
                steps[idx++] = new JoinStep(JoinType.Inner, null, null);
            for (int j = 0; j < sel.Joins.Count; j++)
            {
                var inner = src[idx];
                var outerAliases = new HashSet<string>(
                    src.Take(idx).Select(s => s.Alias), StringComparer.OrdinalIgnoreCase);
                var equi = DetectEqui(sel.Joins[j].On.Text, inner, outerAliases);
                steps[idx++] = new JoinStep(sel.Joins[j].JoinType, sel.Joins[j].On, equi);
            }

            // ---- predicate pushdown on the driving source (BEFORE the join chain) -------------
            // A WHERE conjunct that reads only src[0] is applied to it first, so a selective WHERE
            // no longer pays one inner-side lookup (and one composite clone) per driving row. The
            // FULL WHERE below is UNCHANGED and still runs over the composites, so every conjunct
            // that could not be pushed keeps its exact semantics, and a conjunct that was pushed is
            // simply re-confirmed on a much smaller set. No pushdown at all when the chain can
            // synthesize a NULL driving row (RIGHT / FULL).
            var seed = src[0].Survivors;
            PushdownFilters = ChainPreservesDriving(sel)
                ? CollectPushdownFilters(sel, src, hosted: _host is not null)
                : new List<string>();
            if (PushdownFilters.Count > 0)
                seed = RestrictDriving(src[0], PushdownFilters, ctx);

            // Seed the composite rowset with the driving source's surviving rows.
            var composites = new List<DbfRecord?[]>(seed.Count);
            foreach (var rec in seed)
            {
                var arr = new DbfRecord?[src.Length];
                arr[0] = rec;
                composites.Add(arr);
            }

            // Chain the nested loops, one inner source per step.
            for (int i = 1; i < src.Length; i++)
                composites = JoinStepExpand(composites, src, i, steps[i], ctx);

            // WHERE over the composite rows (non-pushable / cross-source predicates). A WHERE that
            // contains a sub-SELECT is an SQL-level predicate tree evaluated per composite row.
            if (sel.WherePredicate is not null)
            {
                var kept = new List<DbfRecord?[]>(composites.Count);
                foreach (var c in composites)
                {
                    var row = Bind(new CompositeRowContext(src, c, _outer, VfpBlankFields));
                    if (IsPredicateCandidate(sel.WherePredicate, row, ctx) && EvalPredicate(sel.WherePredicate, row, ctx) == true)
                        kept.Add(c);
                }
                composites = kept;
            }
            else if (sel.Where is not null)
            {
                var kept = new List<DbfRecord?[]>(composites.Count);
                foreach (var c in composites)
                {
                    var v = sel.Where.Evaluate(Bind(new CompositeRowContext(src, c, _outer, VfpBlankFields)), ctx);
                    if (v.Type == VfpType.Logical && v.AsLogical) kept.Add(c);
                }
                composites = kept;
            }

            ctx.SqlIndexedFields = null;
            bool aggregate = sel.GroupBy.Count > 0 || sel.Items.Any(ContainsAggregate);

            List<(object?[] row, IRowContext rep)> pairs;
            if (!aggregate)
            {
                pairs = new List<(object?[], IRowContext)>(composites.Count);
                foreach (var c in composites)
                {
                    var rc = Bind(new CompositeRowContext(src, c, _outer, VfpBlankFields));
                    pairs.Add((ProjectJoinRow(rc, src, sel.Items, ctx), rc));
                }
            }
            else
            {
                var rcs = composites.ConvertAll(c => Bind(new CompositeRowContext(src, c, _outer, VfpBlankFields)));
                pairs = Aggregate(ctx, sel with { GroupBy = groupBy }, order, rcs, columns);
            }

            return Materialize(columns, PostProcess(pairs, sel, order, aggregate, ctx, columns));
        }
        finally
        {
            foreach (var s in owned)
            {
                s.ScratchCdx?.Dispose();
                s.Table.Dispose();
            }
        }
    }

    private JoinSource OpenSource(FromSource fs, EvaluationContext ctx, List<JoinSource> owned)
    {
        // Reuse an already-open work area: by the SQL alias first, then by the FROM table NAME — so an
        // open cursor given a fresh query alias (VFP `FROM openCursor qAlias`) is reused, not re-opened.
        var existing = _session.FindAreaByAlias(fs.Alias ?? fs.Table)
                       ?? (fs.Alias is not null ? _session.FindAreaByAlias(fs.Table) : null);
        DbfTable table;
        CdxFile? cdx;
        CdxFile? scratch = null;
        bool owns;
        if (existing is not null)
        {
            table = existing.Table;
            cdx = existing.Cdx;
            owns = false;
        }
        else
        {
            (table, scratch) = _session.OpenNamedTable(fs.Table);
            cdx = scratch;
            owns = true;
        }

        string alias = fs.Alias ?? existing?.Alias ?? fs.Table;
        var cols = table.Columns.ToArray();
        var names = new HashSet<string>(cols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        // Materialize a 1-based recno → record map ONCE (including deleted physical rows), so that
        // matched recnos can be resolved without GetRecords dropping rows. This is what makes SET
        // DELETED OFF correct: FindRecords already honours SET DELETED in its RecordNumbers (deleted
        // recnos appear only when DELETED is OFF), but QueryResult.GetRecords silently SKIPS every
        // deleted physical record (GetRecord returns null for a 0x2A row), which would drop the very
        // rows SET DELETED OFF is meant to expose — unlike the single-table path, which materializes
        // via EnumerateAll(includeDeleted: true). We mirror that here.
        var byRecno = new Dictionary<int, DbfRecord>();
        int rn = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
            byRecno[++rn] = rec;

        // Surviving rows once (SET DELETED honoured by ctx) — the scan-fallback inner candidate set
        // and the driving rowset. Mapped through byRecno so deleted survivors (DELETED OFF) are kept.
        // The parallel recno list records each survivor's stable identity for RIGHT/FULL match tracking.
        var survivorRecnos = new List<int>();
        var survivors = MapRecnos(_session.FindRecords(table, cdx, ".T.", ctx).RecordNumbers, byRecno, survivorRecnos);

        var source = new JoinSource(alias, table, cdx, cols, names, survivors, survivorRecnos, byRecno, scratch);
        if (owns) owned.Add(source);
        return source;
    }

    /// <summary>Maps 1-based recnos through the source's recno→record map (which includes deleted rows),
    /// preserving recno order and skipping any recno absent from the map (defensive — should not happen).
    /// When <paramref name="keptRecnos"/> is supplied it receives the recno of each kept record, parallel
    /// to the returned list (so callers can track records by their stable recno identity).</summary>
    private static List<DbfRecord> MapRecnos(
        IReadOnlyList<int> recnos, Dictionary<int, DbfRecord> byRecno, List<int>? keptRecnos = null)
    {
        var list = new List<DbfRecord>(recnos.Count);
        foreach (int r in recnos)
            if (byRecno.TryGetValue(r, out var rec)) { list.Add(rec); keptRecnos?.Add(r); }
        return list;
    }

    // =======================================================================================
    //  WHERE pushdown on the DRIVING source (src[0]) — evaluated BEFORE the join chain runs.
    //
    //  A WHERE conjunct that reads only the driving source is applied to that source first, so a
    //  selective WHERE no longer pays one inner-side lookup (and one composite clone) per driving
    //  row. The FULL WHERE below is UNCHANGED and still runs over the composites, so every conjunct
    //  that could not be pushed keeps its exact semantics, and a conjunct that WAS pushed is simply
    //  re-confirmed on a much smaller set.
    //
    //  WHY IT IS SAFE: for a chain of INNER / LEFT steps the driving row is never NULL in any
    //  composite, so a conjunct reads the same value before and after the join — dropping the
    //  driving rows it rejects removes exactly the composites the post-join WHERE would have
    //  removed. Chains containing RIGHT / FULL can SYNTHESIZE a composite whose driving row is
    //  NULL, so those get no pushdown at all (see ChainPreservesDriving).
    // =======================================================================================

    /// <summary>Splits a text on its TOP-LEVEL <c>AND</c> keywords only — never descending into
    /// parentheses, string literals (<c>'</c>/<c>"</c>) or bracket literals (<c>[</c>) — so each
    /// piece stays an independent conjunct. Same character scan as <see cref="HasTopLevelBoolean"/>,
    /// extended for bracket strings. Unbalanced text simply yields one untrimmed piece (the caller's
    /// <see cref="VfpExpression.Parse"/> then rejects it).</summary>
    internal static List<string> SplitTopLevelAnd(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') { quote = c; continue; }
            if (c == '[') { quote = ']'; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0) continue;
            if (!IsKeywordAt(text, i, "AND")) continue;

            string piece = text[start..i].Trim();
            if (piece.Length > 0) parts.Add(piece);
            i += 2;      // skip 'AND'; the loop's i++ lands on the character after it
            start = i + 1;
        }
        string tail = text[start..].Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts;
    }

    /// <summary>Collects the top-level AND-conjuncts of a predicate TREE: <see cref="AndPredicate"/>
    /// links are descended, <see cref="ExprPredicate"/> leaves are collected, and every other node
    /// (OR / NOT / subquery atoms) is left entirely to the post-join residual — re-rendering a NOT
    /// from its operand's text would risk a precedence change.</summary>
    internal static void CollectAndLeaves(SqlPredicate predicate, List<string> into)
    {
        switch (predicate)
        {
            case AndPredicate a:
                CollectAndLeaves(a.Left, into);
                CollectAndLeaves(a.Right, into);
                break;
            case ExprPredicate e:
                into.Add(e.Expression.Text);
                break;
        }
    }

    /// <summary>True when a RIGHT / FULL step anywhere in the chain can synthesize a composite whose
    /// DRIVING row is <see langword="null"/>. Comma-FROM entries are always INNER
    /// (<see cref="JoinStep"/> is built as Inner for them), so only the explicit JOINs matter.</summary>
    internal static bool ChainPreservesDriving(SelectStatement sel)
    {
        foreach (var j in sel.Joins)
            if (j.JoinType is JoinType.Right or JoinType.Full)
                return false;
        return true;
    }

    /// <summary>Mirrors <see cref="CompositeRowContext.GetField"/> for the driving source: a dotted
    /// reference must carry the DRIVING alias plus a column that source owns; a bare reference must be
    /// owned by the driving source ALONE (a name owned by two sources is ambiguous and is REJECTED at
    /// evaluation time — never resolve it silently here, or a pushdown would succeed where the real
    /// evaluation would have thrown).</summary>
    internal static bool ResolvesToDriving(string reference, JoinSource[] src)
    {
        var drive = src[0];
        int dot = reference.IndexOf('.');
        if (dot >= 0)
        {
            string alias = reference[..dot];
            string field = reference[(dot + 1)..];
            return string.Equals(alias, drive.Alias, StringComparison.OrdinalIgnoreCase)
                && drive.ColNames.Contains(field);
        }

        if (!drive.ColNames.Contains(reference)) return false;
        for (int i = 1; i < src.Length; i++)
            if (src[i].ColNames.Contains(reference))
                return false;                       // ambiguous bare name → reject
        return true;
    }

    /// <summary>True when the fragment reads at least one field, every field it reads resolves to the
    /// DRIVING source, and it calls no function from <see cref="IsUnpushableFunction"/>. A fragment
    /// without any field reference (<c>.T.</c>, <c>RAND() &lt; 0.5</c>, a bare memvar) is never pushed:
    /// it is either useless to push or would be evaluated twice in two different contexts. When microVFP runs
    /// the query (<paramref name="hosted"/>) a fragment calling a function the engine does not implement (a
    /// UDF, a microVFP function, an unknown name) stays in the full WHERE: the pushdown evaluates without the
    /// host, so such a call would read <c>.NULL.</c> there and silently drop the driving rows.</summary>
    private static bool IsDrivingOnly(string text, JoinSource[] src, bool hosted)
    {
        VfpExpression expr;
        try { expr = SqlParser.ParseExpression(text, xbase: true); }
        catch { return false; }      // unparseable here ⇒ let the post-join path report it

        // An abbreviation (RECC, DELE, EVAL, SECO …) is the function it names.
        if (expr.ContainsFunction(static (name, _) => IsUnpushableFunction(VfpFunctionAbbreviations.Canonical(name))))
            return false;
        if (hosted && expr.ContainsFunction(static (name, _) => !VfpRuntime.EngineFunctionNames.Contains(name)))
            return false;

        var references = expr.ReferencedFields();
        if (references.Count == 0) return false;
        foreach (var reference in references)
            if (!ResolvesToDriving(reference, src))
                return false;
        return true;
    }

    /// <summary>Functions whose result depends on something other than the driving row's field values,
    /// so evaluating them once against the driving table and again over the composites can disagree:
    /// the ROW CONTEXT (RECNO / RECCOUNT / DELETED — RowAdapter reports the real recno/flag,
    /// CompositeRowContext reports 0 / 0 / false), work-area or session STATE (EOF / BOF / FOUND / SEEK /
    /// ALIAS / SELECT / USED / EVALUATE / TYPE / SYS, which a microVFP host may answer), or a fresh value
    /// per call (RAND / SECONDS / DATETIME / TIME / DATE). ISBLANK must retain SQL value semantics
    /// instead of being rebound by the optimizer's xBase parser.</summary>
    private static bool IsUnpushableFunction(string name) => name is
        "RECNO" or "RECCOUNT" or "DELETED" or
        "EOF" or "BOF" or "FOUND" or "SEEK" or "ALIAS" or "SELECT" or "USED" or
        "EVALUATE" or "TYPE" or "SYS" or "ISBLANK" or
        "RAND" or "SECONDS" or "DATETIME" or "TIME" or "DATE";

    /// <summary>The WHERE conjuncts that read ONLY the driving source and are therefore safe to
    /// evaluate against it before the join chain runs. Empty when there is no WHERE.</summary>
    private static List<string> CollectPushdownFilters(SelectStatement sel, JoinSource[] src, bool hosted)
    {
        List<string> conjuncts;
        if (sel.WherePredicate is not null)
        {
            conjuncts = new List<string>();
            CollectAndLeaves(sel.WherePredicate, conjuncts);
        }
        else if (sel.Where is not null)
        {
            conjuncts = SplitTopLevelAnd(sel.Where.Text);
        }
        else
        {
            return new List<string>();
        }

        conjuncts.RemoveAll(t => !IsDrivingOnly(t, src, hosted));
        return conjuncts;
    }

    /// <summary>Drops every <c>alias.</c> qualifier OUTSIDE string/bracket literals, so the
    /// optimizer's residual row adapter (which resolves BARE field names only — see
    /// QueryOptimizer.RowAdapter.GetField) can read the text. Every dotted reference in an accepted
    /// conjunct is known to be <c>alias.field</c> with exactly this driving alias. Without this the
    /// residual would look up a physical column literally named "alias.field", get null back
    /// (DbfRecord's string indexer returns null for an unknown column, it does not throw) and silently
    /// report zero matching rows. A match preceded by '.' is never a qualifier: under a single-letter
    /// alias T / F it is the inside of a <c>.T.</c> / <c>.F.</c> literal (likewise <c>.AND.</c> /
    /// <c>.OR.</c> / <c>.NOT.</c>), and stripping it would leave an unparseable "." behind.</summary>
    internal static string StripDrivingQualifier(string text, string alias)
    {
        if (alias.Length == 0 || text.IndexOf(alias, StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
            if (c == '[') { quote = ']'; sb.Append(c); continue; }

            if (i + alias.Length + 1 <= text.Length
                && text[i + alias.Length] == '.'
                && (i == 0 || (!IsWordChar(text[i - 1]) && text[i - 1] != '.'))
                && string.Compare(text, i, alias, 0, alias.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
            {
                i += alias.Length;   // skip "alias."; the loop's i++ lands after the '.'
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Evaluates the pushed conjuncts against the driving source through the SAME
    /// <see cref="VfpSession.FindRecords"/> machinery the single-table WHERE path uses (index
    /// candidates + residual; SET DELETED honoured by ctx), returning the surviving records in recno
    /// order — mapped through the source's ByRecno map exactly like <see cref="OpenSource"/> does, so
    /// deleted rows survive SET DELETED OFF.</summary>
    private List<DbfRecord> RestrictDriving(
        JoinSource drive, IReadOnlyList<string> conjuncts, EvaluationContext ctx)
    {
        var filter = new System.Text.StringBuilder();
        foreach (var c in conjuncts)
        {
            if (filter.Length > 0) filter.Append(" AND ");
            filter.Append('(').Append(StripDrivingQualifier(c, drive.Alias)).Append(')');
        }

        return MapRecnos(
            _session.FindRecords(drive.Table, drive.Cdx, filter.ToString(), ctx).RecordNumbers,
            drive.ByRecno);
    }

    /// <summary>One nested-loop step: extend each current composite with the matching rows of the
    /// inner source at <paramref name="innerIdx"/>. INNER emits only matches; LEFT emits one all-NULL
    /// inner row when an outer row has no match; RIGHT emits every unmatched inner row with all prior sides NULL;
    /// FULL emits both. The inner candidate set is index-accelerated through
    /// <see cref="QueryOptimizer.FindRecords"/> when a numeric equi-join key was detected, else it is a
    /// full scan; the FULL ON predicate is re-evaluated on every candidate either way.</summary>
    private List<DbfRecord?[]> JoinStepExpand(
        List<DbfRecord?[]> current, JoinSource[] src, int innerIdx, JoinStep step, EvaluationContext ctx)
    {
        var inner = src[innerIdx];
        var next = new List<DbfRecord?[]>(current.Count);

        // Only RIGHT / FULL need the unmatched-inner side, so ONLY they pay for match tracking — the
        // INNER / LEFT fast path skips it entirely (keeps its original cost). Matches are recorded by the
        // inner row's stable RECNO identity, never by raw bytes (byte-identical rows share bytes but have
        // distinct recnos, so identity-by-bytes would mis-mark duplicates as unmatched).
        bool trackUnmatched = step.Type is JoinType.Right or JoinType.Full;
        var innerMatched = trackUnmatched ? new HashSet<int>() : null;

        foreach (var combo in current)
        {
            bool matched = false;
            foreach (var (recno, irec) in InnerCandidates(inner, step, src, combo, ctx))
            {
                var cand = (DbfRecord?[])combo.Clone();
                cand[innerIdx] = irec;

                bool ok = step.On is null; // comma cross-join: ON is implicitly true.
                if (!ok)
                {
                    var row = Bind(new CompositeRowContext(src, cand, _outer, VfpBlankFields));
                    var v = step.On!.HasSqlInfix && !step.On.IsSqlCandidate(row, ctx)
                        ? VfpValue.Logical(false) : step.On.Evaluate(row, ctx);
                    ok = v.Type == VfpType.Logical && v.AsLogical;
                }
                if (ok)
                {
                    next.Add(cand);
                    matched = true;
                    innerMatched?.Add(recno);
                }
            }
            if (!matched && (step.Type == JoinType.Left || step.Type == JoinType.Full))
                next.Add((DbfRecord?[])combo.Clone()); // inner side stays all-NULL.
        }

        // RIGHT / FULL: emit every inner survivor that matched NO outer row, with all prior sources NULL.
        if (trackUnmatched)
        {
            for (int i = 0; i < inner.Survivors.Count; i++)
            {
                if (!innerMatched!.Contains(inner.SurvivorRecnos[i]))
                {
                    var cand = new DbfRecord?[src.Length]; // all prior (and later) sources start NULL.
                    cand[innerIdx] = inner.Survivors[i];
                    next.Add(cand);
                }
            }
        }

        return next;
    }

    /// <summary>The inner-side candidate rows for one step, paired with each row's 1-based recno (its
    /// stable identity for RIGHT/FULL match tracking). Index-accelerated through
    /// <see cref="QueryOptimizer.FindRecords"/> when a numeric equi-join key was detected, else the full
    /// survivor set. Both paths resolve records through the source's byRecno map (NOT
    /// <see cref="QueryResult.GetRecords"/>) so a deleted inner row matched under SET DELETED OFF is kept.</summary>
    private IEnumerable<(int Recno, DbfRecord Rec)> InnerCandidates(
        JoinSource inner, JoinStep step, JoinSource[] src, DbfRecord?[] combo, EvaluationContext ctx)
    {
        if (step.Equi is { } equi)
        {
            var v = equi.OuterExpr.Evaluate(Bind(new CompositeRowContext(src, combo, _outer, VfpBlankFields)), ctx);
            if (!v.IsNull && IsNumericType(v.Type))
            {
                string lit = v.AsNumber.ToString(CultureInfo.InvariantCulture);
                string filter = equi.InnerField + " = " + lit;
                var recnos = _session.FindRecords(inner.Table, inner.Cdx, filter, ctx).RecordNumbers;
                var list = new List<(int, DbfRecord)>(recnos.Count);
                foreach (int r in recnos)
                    if (inner.ByRecno.TryGetValue(r, out var rec)) list.Add((r, rec));
                return list;
            }
        }
        return ZipSurvivors(inner);
    }

    /// <summary>The inner source's survivor rows paired with their parallel recnos (scan-fallback path).</summary>
    private static IEnumerable<(int Recno, DbfRecord Rec)> ZipSurvivors(JoinSource inner)
    {
        for (int i = 0; i < inner.Survivors.Count; i++)
            yield return (inner.SurvivorRecnos[i], inner.Survivors[i]);
    }

    /// <summary>Detects a single top-level <c>innerAlias.field = outerAlias.field</c> equality in the
    /// ON text — the index-accelerable equi-join key. Returns null for compound / non-equality /
    /// non-qualified ON (those fall back to a scan + full ON re-check, which is always correct).</summary>
    private static EquiKey? DetectEqui(string onText, JoinSource inner, HashSet<string> outerAliases)
    {
        if (HasTopLevelBoolean(onText)) return null;
        var m = HavingCmpRx.Match(onText);
        if (!m.Success) return null;
        if (m.Groups["op"].Value is not ("=" or "==")) return null;
        string l = m.Groups["l"].Value.Trim();
        string r = m.Groups["r"].Value.Trim();
        return TryEqui(l, r, inner, outerAliases) ?? TryEqui(r, l, inner, outerAliases);
    }

    private static EquiKey? TryEqui(string innerSide, string outerSide, JoinSource inner, HashSet<string> outerAliases)
    {
        if (!SimpleQualifiedRx.IsMatch(innerSide) || !SimpleQualifiedRx.IsMatch(outerSide)) return null;
        int di = innerSide.IndexOf('.');
        string ia = innerSide[..di], ifield = innerSide[(di + 1)..];
        if (!string.Equals(ia, inner.Alias, StringComparison.OrdinalIgnoreCase)) return null;
        if (!inner.ColNames.Contains(ifield)) return null;

        int dox = outerSide.IndexOf('.');
        string oa = outerSide[..dox];
        if (!outerAliases.Contains(oa)) return null;

        return new EquiKey(ifield, SqlParser.ParseExpression(outerSide, xbase: true));
    }

    // ---- projection (single table) --------------------------------------------------------

    private static object?[] ProjectRow(DbfTable table, Row r, EvaluationContext ctx, IReadOnlyList<SelectItem> items)
    {
        var outv = new List<object?>();
        foreach (var it in items)
        {
            if (it.IsStar)
            {
                foreach (var c in table.Columns)
                    outv.Add(ReadField(r.Rec, c.Name, ctx.VfpBlankFields));
            }
            else
            {
                outv.Add(ToClr(it.Expression!.Evaluate(r.Ctx, ctx)));
            }
        }
        return outv.ToArray();
    }

    // ---- projection (join) ----------------------------------------------------------------

    private static object?[] ProjectJoinRow(
        IRowContext rc, JoinSource[] src, IReadOnlyList<SelectItem> items, EvaluationContext ctx)
    {
        var outv = new List<object?>();
        foreach (var it in items)
        {
            if (it.IsStar)
            {
                foreach (var s in src)
                {
                    if (it.StarAlias is not null &&
                        !string.Equals(it.StarAlias, s.Alias, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var c in s.Columns)
                        outv.Add(rc.GetField(s.Alias + "." + c.Name));
                }
            }
            else
            {
                outv.Add(ToClr(it.Expression!.Evaluate(rc, ctx)));
            }
        }
        return outv.ToArray();
    }

    // ---- aggregates / GROUP BY / HAVING (source-agnostic) ---------------------------------

    // These are binding errors, including for empty inputs: validate before sampling or projecting rows.
    private IReadOnlyList<VfpExpression> ValidateClauses(
        SelectStatement sel, OrderScope scope, ISchema schema,
        Func<SelectItem, IEnumerable<string>> starFields, IEnumerable<string> sourceFields)
    {
        // VFP9 rejects direct array references here; an alias/ordinal can still name their projection.
        bool ReadsArray(VfpExpression expression) => expression.ContainsFunction((name, _) =>
            VfpFunctionAbbreviations.Canonical(name) == "ALEN" || _session.MemoryBridge?.IsArray(name) == true);
        // DISTINCT retains its existing selected-column check (error 1808) below.
        if (sel.GroupBy.Any(ReadsArray) || (!sel.Distinct && sel.OrderBy.Any(o => o.Expression is { } key && ReadsArray(key))))
            throw new FoxDbfSqlException("SQL: Column is not found.") { VfpErrorNumber = 1806 };
        var groups = ResolveGroupBy(sel, scope, starFields);
        if (sel.Having is null && !sel.Distinct) return groups;
        var fields = sourceFields.ToArray();
        string FieldKey(string name)
        {
            string key = StripWs(name).ToUpperInvariant();
            if (key.Contains('.')) return key;
            var matches = fields.Where(f => f.EndsWith("." + key, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0].ToUpperInvariant() : key;
        }

        bool IsExternalReference(string name)
        {
            // QualifierBinder has already checked qualified outer fields and m.vars. They are
            // constants of this query, not local fields that must appear in GROUP BY.
            if (name.Contains('.')) return !fields.Contains(StripWs(name), StringComparer.OrdinalIgnoreCase);
            return !schema.TryGetColumn(name, out _, out _, out _) && _host is not null
                && _session.MemoryBridge?.TryReadMemvar(name, out _) == true;
        }

        var aliases = AliasColumns(scope);
        if (sel.Having is { } having)
        {
            foreach (string field in having.ReferencedFields())
                if (!IsExternalReference(field) && !schema.TryGetColumn(field, out _, out _, out _) && !aliases.ContainsKey(field))
                    throw new FoxDbfSqlException($"Column '{field}' is not found in HAVING.") { VfpErrorNumber = 1806 };
            bool aggregate = groups.Count > 0 || sel.Items.Any(ContainsAggregate);
            if (aggregate)
            {
                // GROUP BY an expression does not group its individual input fields in HAVING.
                var grouped = groups.Where(g => SimpleFieldRx.IsMatch(g.Text.Trim()))
                    .SelectMany(g => g.ReferencedFields()).Select(FieldKey).ToHashSet();
                foreach (string field in having.ReferencedFields(IsSqlAggregateCall))
                {
                    if (IsExternalReference(field)) continue;
                    if (!schema.TryGetColumn(field, out _, out _, out _) && aliases.ContainsKey(field)) continue;
                    if (!grouped.Contains(FieldKey(field)))
                        throw new FoxDbfSqlException($"HAVING field '{field}' is not grouped.") { VfpErrorNumber = 1803 };
                }
            }
        }

        if (sel.Distinct)
        {
            var selectedFields = sel.Items.SelectMany(it => it.IsStar ? starFields(it)
                : it.Expression is { } ex && SimpleFieldRx.IsMatch(ex.Text.Trim())
                    ? new[] { ex.Text } : Array.Empty<string>()).Select(FieldKey).ToHashSet();
            foreach (var ob in sel.OrderBy)
            {
                if (ob.Ordinal is int ordinal && ordinal >= 1 && ordinal <= scope.ColumnCount) continue;
                if (ob.Expression is { } aliasExpression && SimpleFieldRx.IsMatch(aliasExpression.Text.Trim())
                    && ResolveOrderColumn(ob, sel.Items, scope.StarWidth) >= 0) continue;
                if (ob.Expression is { } ex && SimpleFieldRx.IsMatch(ex.Text.Trim()) && selectedFields.Contains(FieldKey(ex.Text))) continue;
                throw new FoxDbfSqlException("ORDER BY must name a selected column with DISTINCT.") { VfpErrorNumber = 1808 };
            }
        }
        return groups;
    }

    private static List<(object?[] row, IRowContext rep)> Aggregate(
        EvaluationContext ctx, SelectStatement sel, OrderScope scope, IReadOnlyList<IRowContext> rows, IReadOnlyList<SqlColumn> columns)
    {
        // Group by the tuple of GROUP BY expression values (normalized). VFP9 returns the groups SORTED by
        // that tuple in GROUP BY list order (collation-aware, .NULL. first) even without ORDER BY: a stable
        // sort on the key, then each run of equal keys is one group (members keep their source order).
        var groups = new List<List<IRowContext>>();
        if (sel.GroupBy.Count == 0)
        {
            groups.Add(rows.ToList()); // whole-table aggregate: exactly one group.
        }
        else
        {
            var keyed = new List<(object?[] Key, IRowContext Row)>(rows.Count);
            foreach (var r in rows)
            {
                var key = new object?[sel.GroupBy.Count];
                for (int g = 0; g < key.Length; g++) key[g] = Norm(ToClr(sel.GroupBy[g].Evaluate(r, ctx)));
                keyed.Add((key, r));
            }
            var byKey = Comparer<object?[]>.Create((a, b) => CompareRow(a, b, ctx));
            object?[]? current = null;
            foreach (var (key, r) in keyed.OrderBy(x => x.Key, byKey))
            {
                if (current is null || CompareRow(current, key, ctx) != 0)
                {
                    groups.Add(new List<IRowContext>());
                    current = key;
                }
                groups[^1].Add(r);
            }
        }

        var aliases = AliasColumns(scope);
        var result = new List<(object?[] row, IRowContext rep)>();
        foreach (var members in groups)
        {
            // A whole-table aggregate over an EMPTY set still emits one row (COUNT→0,
            // SUM/AVG/MIN/MAX→null). Use a synthetic context when there is no member.
            // VFP9 takes non-aggregated values from the LAST member in source order, including
            // differently cased GROUP BY keys that collate equal under GENERAL.
            IRowContext rep = members.Count > 0 ? members[^1] : EmptyRowContext.Instance;
            var row = sel.Items.Select(it => ProjectAggregateItem(it, members, rep, ctx)).ToArray();
            // VFP9's bare COUNT(*) shortcut leaves HAVING aggregate slots empty. Aliases still
            // see the projected count; COUNT(*)+0 and other aggregate projections use the group.
            var havingMembers = sel.Where is null && sel.WherePredicate is null
                && sel.GroupBy.Count == 0 && sel.Items.Count == 1
                && sel.Items[0].AggregateStarFunction == "COUNT" ? new List<IRowContext>() : members;
            if (sel.Having is not null
                && !EvalHaving(sel.Having, havingMembers, AliasRow(rep, row, aliases, scope.IsSourceField, columns), ctx))
                continue;
            result.Add((row, rep));
        }
        return result;
    }

    /// <summary>The steps after projection / grouping, in VFP9's order: a HAVING in an ungrouped query
    /// filters the projected rows (a WHERE that also sees the select aliases); DISTINCT dedups and sorts
    /// by the full row; ORDER BY re-sorts stably (rows tied on the ORDER BY key keep their group-key /
    /// DISTINCT order, as VFP9 returns them); TOP n last.</summary>
    private List<(object?[] row, IRowContext rep)> PostProcess(
        List<(object?[] row, IRowContext rep)> pairs, SelectStatement sel, OrderScope scope, bool aggregate,
        EvaluationContext ctx, IReadOnlyList<SqlColumn> columns)
    {
        if (!aggregate && sel.Having is { } having)
        {
            // An aggregate in the HAVING of a query whose columns are neither grouped nor aggregated
            // (SELECT id FROM person HAVING COUNT(*) > 1) is VFP9 error 1807.
            if (ContainsAggregate(having))
                throw new FoxDbfSqlException(
                    "HAVING uses an aggregate but the query has no GROUP BY.") { VfpErrorNumber = 1807 };
            var aliases = AliasColumns(scope);
            pairs = pairs.FindAll(p =>
            {
                var v = having.Evaluate(AliasRow(p.rep, p.row, aliases, scope.IsSourceField, columns), ctx);
                return v.Type == VfpType.Logical && v.AsLogical;
            });
        }

        // DISTINCT and ORDER BY compare stored cursor values, including character truncation
        // and numeric rounding. Preserve source contexts for ORDER BY unselected source fields.
        if (_host is not null) columns = TypeUntypedColumnsByValue(columns, pairs);
        pairs = pairs.ConvertAll(p => (CoerceRow(p.row, columns), p.rep));
        if (sel.Distinct)
            pairs = Distinct(pairs, ctx);

        if (sel.OrderBy.Count > 0)
            pairs = ApplyOrderBy(pairs, sel.OrderBy, scope, aggregate, ctx);

        // VFP9 (ENGINEBEHAVIOR 90) returns at most n rows, even at an ORDER BY tie.
        if (sel.Top is int top)
            pairs = pairs.Take(Math.Max(0, top)).ToList();

        return pairs;
    }

    /// <summary>Select-list alias → 0-based output column (the first item carrying it wins), accounting for
    /// <c>*</c> widths. Generated names (<c>CNT</c>, <c>EXP_1</c>) are no aliases: VFP9 rejects them in
    /// HAVING (1806).</summary>
    private static Dictionary<string, int> AliasColumns(OrderScope scope)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int col = 0;
        foreach (var it in scope.Items)
        {
            if (it.IsStar) { col += scope.StarWidth(it); continue; }
            if (it.Alias is not null) map.TryAdd(it.Alias.Trim(), col);
            col++;
        }
        return map;
    }

    private static object? ProjectAggregateItem(SelectItem it, List<IRowContext> members, IRowContext rep, EvaluationContext ctx)
    {
        if (it.AggregateStarFunction is not null)
        {
            // COUNT(*) is the representable FUNC(*) aggregate.
            if (string.Equals(it.AggregateStarFunction, "COUNT", StringComparison.OrdinalIgnoreCase))
                return (decimal)members.Count;
            throw new NotSupportedException($"{it.AggregateStarFunction}(*) is not supported.");
        }
        if (it.Expression is not null && ContainsAggregate(it.Expression))
            return EvaluateAggregateExpression(it.Expression, members, rep, ctx);

        // non-aggregate item in an aggregate query (a GROUP BY column) → value of the last member;
        // an empty group has no member to read → null.
        return members.Count == 0 ? null : ToClr(it.Expression!.Evaluate(rep, ctx));
    }

    private static object? EvaluateAggregateExpression(
        VfpExpression expression, List<IRowContext> members, IRowContext rep, EvaluationContext ctx)
        => Norm(ToClr(EvaluateWithAggregates(expression, members, rep, ctx)));

    /// <summary>Evaluates <paramref name="expression"/> against <paramref name="rep"/>, computing every SQL
    /// aggregate call in it over <paramref name="members"/>.</summary>
    private static VfpValue EvaluateWithAggregates(
        VfpExpression expression, List<IRowContext> members, IRowContext rep, EvaluationContext ctx)
    {
        return expression.EvaluateWithFunctionResolver(rep, ctx,
            (string upperName, IReadOnlyList<ExpressionArgument> arguments, out VfpValue result) =>
            {
                if (!IsSqlAggregateCall(upperName, arguments.Count))
                {
                    result = VfpValue.Null;
                    return false;
                }

                result = VfpValue.FromClr(ComputeAggregate(upperName, arguments[0], members, ctx));
                return true;
            });
    }

    private static object? ComputeAggregate(
        string func, ExpressionArgument arg, List<IRowContext> members, EvaluationContext ctx)
    {
        if (func == "COUNT" && !arg.IsDistinct)
            return (decimal)members.Count(m => !arg.Evaluate(m, ctx).IsNull);

        var vals = members.Select(m => arg.Evaluate(m, ctx)).Where(v => !v.IsNull).ToList();
        if (arg.IsDistinct)
        {
            vals.Sort((left, right) => CompareVfp(left, right, ctx));
            int write = 0;
            for (int read = 0; read < vals.Count; read++)
                if (write == 0 || CompareVfp(vals[write - 1], vals[read], ctx) != 0) vals[write++] = vals[read];
            vals.RemoveRange(write, vals.Count - write);
        }
        switch (func)
        {
            case "COUNT": return (decimal)vals.Count;
            case "SUM": return vals.Count == 0 ? null : vals.Aggregate(0m, (acc, v) => acc + v.AsNumber);
            case "AVG": return vals.Count == 0 ? (decimal?)null : vals.Aggregate(0m, (acc, v) => acc + v.AsNumber) / vals.Count;
            case "MIN": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfp(a, b, ctx) <= 0 ? a : b)));
            case "MAX": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfp(a, b, ctx) >= 0 ? a : b)));
            default: throw new NotSupportedException($"Aggregate '{func}' is not supported.");
        }
    }

    /// <summary>Evaluates a HAVING over one group as a single expression: aggregate calls are computed over
    /// the group's <paramref name="members"/>, and <paramref name="row"/> (the group's representative
    /// with its projected output row) resolves the select aliases — <c>HAVING k &gt; 3</c>,
    /// <c>HAVING n &gt; 1</c> for <c>COUNT(*) AS n</c>, compound <c>AND</c>/<c>OR</c> and functions over
    /// both, as in VFP9. <c>=</c> follows the WHERE path's SQL semantics (the context carries
    /// <see cref="EvaluationContext.SqlSemantics"/>); a <c>.NULL.</c> result rejects the group.</summary>
    private static bool EvalHaving(VfpExpression having, List<IRowContext> members, IRowContext row, EvaluationContext ctx)
    {
        var v = EvaluateWithAggregates(having, members, row, ctx);
        return v.Type == VfpType.Logical && v.AsLogical;
    }

    /// <summary>SQL <c>=</c> equality (governed by SET ANSI when SqlSemantics is on) used by the
    /// subquery predicates, mirroring the WHERE path's runtime operator. Strings route through the canonical
    /// <see cref="VfpRuntime.StrEqSql"/>; other types use the collation-aware VFP comparison.</summary>
    private static bool ValueEqSql(VfpValue a, VfpValue b, EvaluationContext ctx)
        => VfpRuntime.ValueEqSql(a, b, ctx);

    /// <summary>True when <paramref name="s"/> contains a top-level <c>AND</c>/<c>OR</c> (outside
    /// parentheses and string literals) — the marker of a compound boolean we do not yet support.</summary>
    private static bool HasTopLevelBoolean(string s)
    {
        int depth = 0; bool inStr = false; char q = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == q) inStr = false; continue; }
            if (c is '\'' or '"') { inStr = true; q = c; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0) continue;
            if (IsKeywordAt(s, i, "AND") || IsKeywordAt(s, i, "OR")) return true;
        }
        return false;
    }

    private static bool IsKeywordAt(string s, int i, string kw)
    {
        if (i + kw.Length > s.Length) return false;
        if (i > 0 && IsWordChar(s[i - 1])) return false;
        for (int j = 0; j < kw.Length; j++)
            if (char.ToUpperInvariant(s[i + j]) != kw[j]) return false;
        int after = i + kw.Length;
        return after >= s.Length || !IsWordChar(s[after]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ---- ORDER BY / DISTINCT / TOP (source-agnostic) --------------------------------------

    /// <summary>What ORDER BY / GROUP BY resolution needs to know about the query: its select items, the
    /// width of each <c>*</c> item, whether a bare name is a field of one of the query's sources, and the
    /// number of result columns.</summary>
    private sealed record OrderScope(
        IReadOnlyList<SelectItem> Items, Func<SelectItem, int> StarWidth, Func<string, bool> IsSourceField,
        int ColumnCount);

    private static List<(object?[] row, IRowContext rep)> ApplyOrderBy(
        List<(object?[] row, IRowContext rep)> pairs, IReadOnlyList<OrderItem> orderBy,
        OrderScope scope, bool aggregate, EvaluationContext ctx)
    {
        IOrderedEnumerable<(object?[] row, IRowContext rep)>? ordered = null;
        for (int k = 0; k < orderBy.Count; k++)
        {
            var ob = orderBy[k];
            var keySel = OrderKeySelector(ob, scope, aggregate, ctx);
            var cmp = Comparer<(object?[] row, IRowContext rep)>.Create((a, b) => CompareVfp(keySel(a), keySel(b), ctx));
            ordered = k == 0
                ? (ob.Descending ? pairs.OrderByDescending(x => x, cmp) : pairs.OrderBy(x => x, cmp))
                : (ob.Descending ? ordered!.ThenByDescending(x => x, cmp) : ordered!.ThenBy(x => x, cmp));
        }
        return ordered!.ToList();
    }

    /// <summary>DISTINCT as VFP9 does it: the rows come back SORTED by the full row in column order
    /// (collation-aware, .NULL. first) even without ORDER BY, and of rows that collate equal the first in
    /// source order is kept (a stable sort, then the first row of each equal run).</summary>
    private static List<(object?[] row, IRowContext rep)> Distinct(
        List<(object?[] row, IRowContext rep)> pairs, EvaluationContext ctx)
    {
        var byRow = Comparer<object?[]>.Create((a, b) => CompareRow(a, b, ctx));
        var kept = new List<(object?[] row, IRowContext rep)>();
        foreach (var p in pairs.OrderBy(x => x.row, byRow))
            if (kept.Count == 0 || CompareRow(kept[^1].row, p.row, ctx) != 0) kept.Add(p);
        return kept;
    }

    /// <summary>Builds the ORDER BY key selector for one entry: a 1-based ordinal reads the output
    /// column (outside 1..column count is VFP error 1808); in an aggregate query an expression matching a
    /// select item (by alias/text) ALSO reads the already-computed output column (so <c>ORDER BY COUNT(*)</c>
    /// is not re-evaluated per row); in a plain query a bare name that is a select-list alias and no source
    /// field reads that output column (<c>SELECT id AS k … ORDER BY k DESC</c>; a source field of the same
    /// name wins, as in VFP9); otherwise the expression is evaluated against the representative row.</summary>
    private static Func<(object?[] row, IRowContext rep), VfpValue> OrderKeySelector(
        OrderItem ob, OrderScope scope, bool aggregate, EvaluationContext ctx)
    {
        int colIdx = -1;
        if (ob.Ordinal is int ord)
        {
            if (ord < 1 || ord > scope.ColumnCount)
                throw new FoxDbfSqlException(
                    $"ORDER BY ordinal {ord} is outside the valid range 1..{scope.ColumnCount}.") { VfpErrorNumber = 1808 };
            colIdx = ord - 1;
        }
        else if (aggregate) colIdx = ResolveOrderColumn(ob, scope.Items, scope.StarWidth);
        else if (ob.Expression is not null && AliasOnlyName(ob.Expression.Text, scope) is string name)
            colIdx = ResolveAliasColumn(name, scope.Items, scope.StarWidth);

        if (colIdx >= 0)
        {
            int c = colIdx;
            return t => VfpValue.FromClr(c < t.row.Length ? t.row[c] : null);
        }
        return t => ob.Expression!.Evaluate(t.rep, ctx);
    }

    /// <summary>The name when <paramref name="text"/> is a bare (unqualified) identifier that is NOT a field
    /// of the query's sources — the only shape VFP9 resolves to a select-list alias; otherwise null.</summary>
    private static string? AliasOnlyName(string text, OrderScope scope)
    {
        string t = text.Trim();
        if (t.Contains('.') || !SimpleFieldRx.IsMatch(t) || scope.IsSourceField(t)) return null;
        return t;
    }

    /// <summary>The output column index of the select item aliased <paramref name="name"/>, accounting for
    /// <c>*</c> widths; -1 if no item carries that alias.</summary>
    private static int ResolveAliasColumn(string name, IReadOnlyList<SelectItem> items, Func<SelectItem, int> starWidth)
    {
        int col = 0;
        foreach (var it in items)
        {
            if (it.IsStar) { col += starWidth(it); continue; }
            if (it.Alias is not null && string.Equals(it.Alias.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return col;
            col++;
        }
        return -1;
    }

    /// <summary>Resolves the GROUP BY list as VFP9 does: a column ordinal (<c>GROUP BY 1</c>) and a bare
    /// select-list alias that is no source field (<c>GROUP BY k</c> for <c>p.id % 2 AS k</c>) group by that
    /// select item's expression, evaluated per source row (a <c>*</c> column by its field); anything else
    /// is grouped as written. An ordinal outside 1..column count is error 1807, one naming an aggregate
    /// column 1846 (both probed against VFP9).</summary>
    private static IReadOnlyList<VfpExpression> ResolveGroupBy(
        SelectStatement sel, OrderScope scope, Func<SelectItem, IEnumerable<string>> starFields)
    {
        if (sel.GroupBy.Count == 0) return sel.GroupBy;
        var resolved = new List<VfpExpression>(sel.GroupBy.Count);
        bool changed = false;
        foreach (var g in sel.GroupBy)
        {
            string t = g.Text.Trim();
            VfpExpression? target = null;
            if (t.Length > 0 && t.All(char.IsAsciiDigit))
            {
                if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int ord)
                    || ord < 1 || ord > scope.ColumnCount)
                    throw new FoxDbfSqlException(
                        $"GROUP BY ordinal {t} is outside the valid range 1..{scope.ColumnCount}.") { VfpErrorNumber = 1807 };
                target = GroupTargetAt(ord - 1, scope, starFields);
            }
            else if (AliasOnlyName(t, scope) is string name)
            {
                int col = ResolveAliasColumn(name, scope.Items, scope.StarWidth);
                if (col >= 0) target = GroupTargetAt(col, scope, starFields);
            }

            if (target is null) { resolved.Add(g); continue; }
            resolved.Add(target);
            changed = true;
        }
        return changed ? resolved : sel.GroupBy;
    }

    /// <summary>The expression that produces output column <paramref name="col"/> per source row.</summary>
    private static VfpExpression GroupTargetAt(int col, OrderScope scope, Func<SelectItem, IEnumerable<string>> starFields)
    {
        foreach (var it in scope.Items)
        {
            if (it.IsStar)
            {
                int w = scope.StarWidth(it);
                if (col < w) return SqlParser.ParseExpression(starFields(it).ElementAt(col), xbase: true);
                col -= w;
                continue;
            }
            if (col == 0)
            {
                if (ContainsAggregate(it))
                    throw new FoxDbfSqlException(
                        "GROUP BY cannot name an aggregate column.") { VfpErrorNumber = 1846 };
                return it.Expression!;
            }
            col--;
        }
        throw new InvalidOperationException("GROUP BY column outside the select list.");
    }

    /// <summary>Maps an ORDER BY expression onto a select-item output column index (by alias or by
    /// expression text, whitespace-insensitive), accounting for <c>*</c> / <c>alias.*</c> expansion
    /// widths; -1 if no match.</summary>
    private static int ResolveOrderColumn(OrderItem ob, IReadOnlyList<SelectItem> items, Func<SelectItem, int> starWidth)
    {
        if (ob.Expression is null) return -1;
        string t = StripWs(ob.Expression.Text);
        int col = 0;
        foreach (var it in items)
        {
            if (it.IsStar) { col += starWidth(it); continue; }
            if (it.Alias is not null && Eq(StripWs(it.Alias), t)) return col;
            if (it.AggregateStarFunction is not null)
            {
                // COUNT(*) the select item vs. the ORDER BY fragment, which BuildExpr rewrites the
                // bare '*' of to '1' (COUNT(*) → COUNT(1)); accept both renderings.
                string fn = it.AggregateStarFunction;
                if (Eq(StripWs(fn + "(*)"), t) || Eq(StripWs(fn + "(1)"), t)) return col;
            }
            else if (it.Expression is not null && Eq(StripWs(it.Expression.Text), t))
            {
                return col;
            }
            col++;
        }
        return -1;

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripWs(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    // ---- materialization ------------------------------------------------------------------

    /// <summary>Creates a result from rows that were already coerced to <paramref name="columns"/>.
    /// UNION uses this after its per-branch coercion pass so each row is cloned only once.</summary>
    private static SqlResult MaterializeAlreadyCoerced(
        IReadOnlyList<SqlColumn> columns, List<object?[]> rows) => new(columns, rows);

    private SqlResult Materialize(IReadOnlyList<SqlColumn> columns, List<(object?[] row, IRowContext rep)> pairs)
    {
        if (_host is not null) columns = TypeUntypedColumnsByValue(columns, pairs);
        // Coerce each cell to its SqlColumn.ClrType so the DataReader's typed getters never see a
        // mismatched box.
        var outRows = new List<object?[]>(pairs.Count);
        foreach (var p in pairs)
            outRows.Add(CoerceRow(p.row, columns));
        return new SqlResult(columns, outRows);
    }

    // ---- schema (single table) ------------------------------------------------------------

    private static IReadOnlyList<SqlColumn> BuildColumns(
        DbfTable table, string alias, ISchema schema, IReadOnlyList<SelectItem> items)
    {
        var cols = new List<SqlColumn>();
        foreach (var it in items)
        {
            int position = cols.Count + 1;
            if (it.IsStar)
            {
                foreach (var c in table.Columns)
                    cols.Add(new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)) { IsBinary = c.IsBinary });
                continue;
            }

            if (it.AggregateStarFunction is not null)
            {
                cols.Add(new SqlColumn(it.Alias ?? AggregateName(it.AggregateStarFunction, "*", position), 'N', 10, 0, typeof(decimal)));
                continue;
            }

            if (IsAggregate(it, out var func, out var arg))
            {
                string aggName = it.Alias ?? AggregateName(func, arg, position, HasDistinctArgument(it.Expression!.Text));
                var info = func == "COUNT" ? new VfpTypeInfo(VfpType.Numeric, 10) : SafeInfer(arg!, schema);
                if (func is "SUM" or "AVG" && info.Type == VfpType.Numeric && info.SqlStorageType != 'B')
                    info = new VfpTypeInfo(info.Type, Math.Max(16, info.Length), func == "AVG" ? Math.Max(2, info.Decimals) : info.Decimals, info.SqlStorageType);
                var (vc, clr) = MapSqlInfer(info);
                cols.Add(new SqlColumn(aggName, vc, info.Length, info.Decimals, clr));
                continue;
            }

            // plain expression: a simple (possibly alias-qualified) field keeps the column's name/type.
            string text = it.Expression!.Text.Trim();
            if (it.Alias is null && SimpleFieldRx.IsMatch(text))
            {
                string field = StripAlias(text);
                var c = FindColumn(table, field);
                if (c is not null)
                {
                    cols.Add(new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)) { IsBinary = c.IsBinary });
                    continue;
                }
            }

            var ti = it.Expression!.InferSqlType(schema);
            var (typeChar, clrType) = MapSqlInfer(ti);
            cols.Add(new SqlColumn(it.Alias ?? ExpName(position), typeChar, ti.Length, ti.Decimals, clrType));
        }
        return ApplyVfpColumnNames(cols);
    }

    // ---- schema (join) --------------------------------------------------------------------

    private static IReadOnlyList<SqlColumn> BuildJoinColumns(
        JoinSource[] src, ISchema schema, IReadOnlyList<SelectItem> items)
    {
        var cols = new List<SqlColumn>();
        foreach (var it in items)
        {
            int position = cols.Count + 1;
            if (it.IsStar)
            {
                bool any = false;
                foreach (var s in src)
                {
                    if (it.StarAlias is not null &&
                        !string.Equals(it.StarAlias, s.Alias, StringComparison.OrdinalIgnoreCase))
                        continue;
                    any = true;
                    foreach (var c in s.Columns)
                        cols.Add(new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)) { IsBinary = c.IsBinary });
                }
                if (it.StarAlias is not null && !any)
                    throw new FoxDbfSqlException($"Unknown alias '{it.StarAlias}' in '{it.StarAlias}.*'.");
                continue;
            }

            if (it.AggregateStarFunction is not null)
            {
                cols.Add(new SqlColumn(it.Alias ?? AggregateName(it.AggregateStarFunction, "*", position), 'N', 10, 0, typeof(decimal)));
                continue;
            }

            if (IsAggregate(it, out var func, out var arg))
            {
                string aggName = it.Alias ?? AggregateName(func, arg, position, HasDistinctArgument(it.Expression!.Text));
                var info = func == "COUNT" ? new VfpTypeInfo(VfpType.Numeric, 10) : SafeInfer(arg!, schema);
                if (func is "SUM" or "AVG" && info.Type == VfpType.Numeric && info.SqlStorageType != 'B')
                    info = new VfpTypeInfo(info.Type, Math.Max(16, info.Length), func == "AVG" ? Math.Max(2, info.Decimals) : info.Decimals, info.SqlStorageType);
                var (vc, clr) = MapSqlInfer(info);
                cols.Add(new SqlColumn(aggName, vc, info.Length, info.Decimals, clr));
                continue;
            }

            // plain expression: a simple (qualified or unqualified) field keeps the column's name/type;
            // an ambiguous unqualified field is rejected.
            string text = it.Expression!.Text.Trim();
            if (it.Alias is null && SimpleFieldRx.IsMatch(text))
            {
                var c = ResolveJoinField(text, src, out _);
                if (c is not null)
                {
                    cols.Add(new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)) { IsBinary = c.IsBinary });
                    continue;
                }
            }

            var ti = it.Expression!.InferSqlType(schema);
            var (typeChar, clrType) = MapSqlInfer(ti);
            cols.Add(new SqlColumn(it.Alias ?? ExpName(position), typeChar, ti.Length, ti.Decimals, clrType));
        }
        return ApplyVfpColumnNames(cols);
    }

    /// <summary>VFP9's name for an unaliased computed column: <c>EXP_n</c>, n = its 1-based position in
    /// the result (after <c>*</c> expansion), not a count of the expressions.</summary>
    private static string ExpName(int position) => "EXP_" + position.ToString(CultureInfo.InvariantCulture);

    /// <summary>VFP9's name for an unaliased aggregate column: <c>COUNT(*)</c> → <c>CNT</c>; over a field
    /// <c>FN_FIELD</c> (qualifier dropped, <c>COUNT</c> abbreviated to <c>CNT</c>); over any other
    /// expression <c>FN_EXP_n</c> with n the column's position (<c>SUM(amount * 2)</c> in column 2 →
    /// <c>SUM_EXP_2</c>). A compound item the aggregate pattern only spans textually
    /// (<c>MAX(a) - MIN(a)</c>: "argument" <c>a) - MIN(a</c>) is a plain expression, <c>EXP_n</c>.</summary>
    private static string AggregateName(string func, string? arg, int position, bool distinct = false)
    {
        string fn = func.ToUpperInvariant();
        string prefix = fn == "COUNT" ? "CNT" : fn;
        if (distinct) prefix = "D" + prefix;
        if (arg is null || arg.Trim() == "*") return prefix;
        string a = arg.Trim();
        if (!IsParenBalanced(a)) return ExpName(position);
        return prefix + "_" + (SimpleFieldRx.IsMatch(a) ? StripAlias(a) : ExpName(position));
    }

    /// <summary>True when no prefix of <paramref name="s"/> closes more parentheses than it opens and the
    /// whole text closes all it opens.</summary>
    private static bool IsParenBalanced(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    /// <summary>Gives the result columns VFP9's names: every name upper-case, and a name that occurs more
    /// than once (case-insensitive) — a source field, a user alias and a generated name alike — becomes
    /// <c>NAME_A</c>, <c>NAME_B</c>, <c>NAME_C</c>, … in column order (<c>ca.id, ao.id</c> → <c>ID_A, ID_B</c>;
    /// <c>id, name AS id</c> → <c>ID_A, ID_B</c>). Should a suffixed name still collide (an explicit
    /// <c>id_a</c> beside two <c>id</c>), a numeric suffix keeps it unique. Values are positional, so
    /// renaming a column never disturbs the row data.</summary>
    private static IReadOnlyList<SqlColumn> ApplyVfpColumnNames(List<SqlColumn> cols)
    {
        var upper = cols.ConvertAll(c => c.Name.ToUpperInvariant());
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in upper)
            counts[n] = counts.TryGetValue(n, out int k) ? k + 1 : 1;

        var used = new HashSet<string>(upper.Where(n => counts[n] == 1), StringComparer.Ordinal);
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        var outCols = new List<SqlColumn>(cols.Count);
        for (int i = 0; i < cols.Count; i++)
        {
            string name = upper[i];
            if (counts[name] > 1)
            {
                int k = next.TryGetValue(name, out int seen) ? seen : 0;
                next[name] = k + 1;
                string suffix = k < 26 ? ((char)('A' + k)).ToString() : (k + 1).ToString(CultureInfo.InvariantCulture);
                string baseName = name + "_" + suffix;
                name = baseName;
                for (int n = 2; !used.Add(name); n++)
                    name = baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
            }
            var col = cols[i];
            outCols.Add(string.Equals(name, col.Name, StringComparison.Ordinal)
                ? col
                : new SqlColumn(name, col.VfpType, col.Length, col.Decimals, col.ClrType) { IsBinary = col.IsBinary });
        }
        return outCols;
    }

    /// <summary>Resolves a simple field reference (<c>alias.field</c> or bare <c>field</c>) to its
    /// source column. A bare field present in more than one source is AMBIGUOUS and rejected; an
    /// unknown field returns null (the caller treats it as a general expression).</summary>
    private static DbfColumn? ResolveJoinField(string text, JoinSource[] src, out string? ownerAlias)
    {
        ownerAlias = null;
        text = text.Replace(" ", "");
        int dot = text.IndexOf('.');
        if (dot >= 0)
        {
            string alias = text[..dot], field = text[(dot + 1)..];
            foreach (var s in src)
                if (string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    var c = FindColumn(s, field);
                    if (c is not null) ownerAlias = s.Alias;
                    return c;
                }
            return null;
        }

        DbfColumn? found = null; int count = 0;
        foreach (var s in src)
        {
            var c = FindColumn(s, text);
            if (c is not null) { found = c; ownerAlias = s.Alias; count++; }
        }
        if (count > 1)
            throw new FoxDbfSqlException($"Column '{text}' is ambiguous; qualify it with an alias.");
        return found;
    }

    private static int StarWidthJoin(SelectItem it, JoinSource[] src)
    {
        if (!it.IsStar) return 1;
        int w = 0;
        foreach (var s in src)
            if (it.StarAlias is null || string.Equals(it.StarAlias, s.Alias, StringComparison.OrdinalIgnoreCase))
                w += s.Columns.Length;
        return w;
    }

    private static DbfColumn? FindColumn(DbfTable table, string field)
    {
        foreach (var c in table.Columns)
            if (string.Equals(c.Name, field, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    private static DbfColumn? FindColumn(JoinSource source, string field)
    {
        foreach (var c in source.Columns)
            if (string.Equals(c.Name, field, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    // ---- aggregate / value helpers --------------------------------------------------------

    private static bool IsAggregate(SelectItem it, out string func, out string? arg)
    {
        func = ""; arg = null;
        if (it.AggregateStarFunction is not null) { func = it.AggregateStarFunction.ToUpperInvariant(); arg = "*"; return true; }
        if (it.Expression is null) return false;
        return IsAggregateText(it.Expression.Text, out func, out arg);
    }

    private static bool ContainsAggregate(SelectItem item)
        => item.AggregateStarFunction is not null ||
           item.Expression is not null && ContainsAggregate(item.Expression);

    private static bool ContainsAggregate(VfpExpression expression)
        => expression.ContainsFunction(IsSqlAggregateCall);

    private static bool IsSqlAggregateCall(string upperName, int arity)
        => arity == 1 && upperName is "SUM" or "AVG" or "COUNT" or "MIN" or "MAX";

    private static bool IsAggregateText(string text, out string func, out string? arg)
    {
        func = ""; arg = null;
        var m = AggRx.Match(text);
        if (!m.Success) return false;
        string a = m.Groups["arg"].Value;
        if (HasTopLevelComma(a)) return false; // exclude scalar MIN(a,b)/MAX(a,b)
        func = m.Groups[1].Value.ToUpperInvariant();
        arg = a.Trim();
        if (StartsWithDistinct(arg)) arg = arg[8..].TrimStart();
        return true;
    }

    private static bool StartsWithDistinct(string argument)
        => argument.Length > 8 && argument.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase)
            && (char.IsWhiteSpace(argument[8]) || argument[8] == '(');

    private static bool HasDistinctArgument(string text)
    {
        var match = AggRx.Match(text);
        return match.Success && StartsWithDistinct(match.Groups["arg"].Value.TrimStart());
    }

    private static bool HasTopLevelComma(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return true;
        }
        return false;
    }

    private static (char, Type) MapSqlInfer(VfpTypeInfo info) => info.SqlStorageType switch
    {
        'F' => ('F', typeof(double)),
        'B' => ('B', typeof(double)),
        _ => MapInfer(info)
    };
    private static VfpTypeInfo SafeInfer(string arg, ISchema schema)
    {
        try { return SqlParser.ParseExpression(arg, xbase: true).InferSqlType(schema); }
        catch { return new VfpTypeInfo(VfpType.Numeric); }
    }

    private static string StripAlias(string text)
    {
        text = text.Replace(" ", "");
        int dot = text.LastIndexOf('.');
        return dot >= 0 ? text[(dot + 1)..] : text;
    }

    private static bool IsNumericType(VfpType t) =>
        t is VfpType.Numeric or VfpType.Integer or VfpType.Currency;

    internal static object? ToClr(VfpValue v) => v.Type switch
    {
        VfpType.Null => null,
        VfpType.Character => v.IsBinary ? v.ToClr() : v.AsString,
        VfpType.Logical => v.AsLogical,
        VfpType.Date => v.AsDate,
        VfpType.DateTime => v.AsDateTime,
        VfpType.Integer => v.AsInteger,    // I → int (matches SqlColumn.ClrType typeof(int))
        VfpType.Currency => v.AsNumber,    // Y → decimal
        VfpType.Numeric => double.IsFinite(v.AsDouble) ? (object)v.AsNumber : v.AsDouble,
        _ => v.AsNumber,
    };

    /// <summary>Coerces a projected/raw boxed value to its target <see cref="SqlColumn.ClrType"/> so
    /// the boxed runtime type matches the declared schema (e.g. an N(w,0) field that decodes to
    /// <see cref="long"/>, or an F/B field that decodes to <see cref="double"/>, becomes the schema's
    /// <see cref="decimal"/>/<see cref="double"/>/<see cref="int"/>). Non-numeric values pass through.</summary>
    private static object?[] CoerceRow(object?[] row, IReadOnlyList<SqlColumn> cols)
    {
        var outr = new object?[row.Length];
        for (int i = 0; i < row.Length; i++)
            outr[i] = i < cols.Count ? StoreCell(row[i], cols[i]) : row[i];
        return outr;
    }

    private static object? StoreCell(object? value, SqlColumn column)
    {
        if (value is double nonFinite && !double.IsFinite(nonFinite)) return value;
        if (value is not null && IsNumericBox(value))
        {
            if (column.VfpType == 'I') value = decimal.Truncate(Convert.ToDecimal(value));
            else if (column.VfpType == 'Y') value = Math.Round(Convert.ToDecimal(value), 4, MidpointRounding.AwayFromZero);
            else if (column.VfpType is 'N' or 'F' && column.Length > 0)
            {
                decimal original = Convert.ToDecimal(value);
                int decimals = VfpRuntime.NumericStorageDecimals(original, column.Length,
                    Math.Clamp(column.Decimals, 0, 28));
                value = Math.Round(original, decimals, MidpointRounding.AwayFromZero);
                // Positive scales already fit; at scale zero only integer overflow remains.
                if (decimals == 0 && !VfpRuntime.NumericIntegerFits((decimal)value, column.Length))
                    return double.PositiveInfinity;
            }
        }
        value = CoerceCell(value, column.ClrType);
        // A SELECT result is a VFP cursor: both direct results and INTO must expose its stored
        // width and scale. Do this after evaluating the whole expression, not its operands.
        if (column.VfpType == 'C' && column.Length is > 0 and <= 254 && value is string text)
            return text.Length > column.Length ? text[..column.Length] : text.PadRight(column.Length);
        return value;
    }

    private static object? CoerceCell(object? v, Type target)
    {
        if (v is null) return null;
        if (v.GetType() == target) return v;
        if (target == typeof(DateTime) && v is DateOnly date)
            return date.ToDateTime(TimeOnly.MinValue);
        if (!IsNumericBox(v)) return v;
        if (target == typeof(decimal)) return Convert.ToDecimal(v);
        if (target == typeof(double)) return Convert.ToDouble(v);
        if (target == typeof(int)) return Convert.ToInt32(v);
        if (target == typeof(long)) return Convert.ToInt64(v);
        // A numeric value flowing into a CHARACTER column (UNION incompatible-mix widening) → render it.
        if (target == typeof(string)) return Convert.ToString(v, CultureInfo.InvariantCulture);
        return v;
    }

    private static bool IsNumericBox(object v) =>
        v is decimal or double or float or int or long or short or byte or sbyte or uint or ushort or ulong;

    private static object? Norm(object? o) => o switch
    {
        null => null,
        decimal m => m,
        double d => double.IsFinite(d) ? (object)(decimal)d : d,
        float f => (decimal)f,
        int i => (decimal)i,
        long l => (decimal)l,
        short sh => (decimal)sh,
        byte by => (decimal)by,
        _ => o,
    };

    private static int CompareVfp(VfpValue a, VfpValue b, EvaluationContext ctx)
    {
        if (a.IsNull && b.IsNull) return 0;
        if (a.IsNull) return -1;
        if (b.IsNull) return 1;
        if (a.Type == VfpType.Character && b.Type == VfpType.Character)
            return ctx.Collation.Compare(a.AsString.AsSpan(), b.AsString.AsSpan());
        if (a.Type == VfpType.Logical && b.Type == VfpType.Logical)
            return a.AsLogical.CompareTo(b.AsLogical);
        if (a.Type is VfpType.Date or VfpType.DateTime && b.Type is VfpType.Date or VfpType.DateTime)
            return a.AsDateTime.CompareTo(b.AsDateTime);
        if (!double.IsFinite(a.AsDouble) || !double.IsFinite(b.AsDouble)) return a.AsDouble.CompareTo(b.AsDouble);
        return a.AsNumber.CompareTo(b.AsNumber);
    }

    /// <summary>Orders two result rows / GROUP BY key tuples cell by cell (see <see cref="CompareCell"/>);
    /// 0 means they are one DISTINCT row / one group.</summary>
    private static int CompareRow(object?[] a, object?[] b, EvaluationContext ctx)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int c = CompareCell(a[i], b[i], ctx);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>VFP's sort order of two cells: .NULL. first; strings by the session collation (under
    /// GENERAL 'Berlin' and 'BERLIN' collate equal — one group, one DISTINCT row; under MACHINE ordinal);
    /// numbers, dates (a Date equals the midnight DateTime) and logicals (.F. first) by value. Cells of
    /// unrelated types (no VFP column mixes them) order by a fixed type rank so the order stays total.</summary>
    private static int CompareCell(object? a, object? b, EvaluationContext ctx)
    {
        a = Norm(a); b = Norm(b);
        if (a is null || b is null) return a is null ? (b is null ? 0 : -1) : 1;
        switch (a, b)
        {
            case (string sa, string sb): return ctx.Collation.Compare(sa.AsSpan(), sb.AsSpan());
            case (decimal da, decimal db): return da.CompareTo(db);
            case (double da, double db): return da.CompareTo(db);
            case (double da, decimal db): return da.CompareTo((double)db);
            case (decimal da, double db): return ((double)da).CompareTo(db);
            case (bool ba, bool bb): return ba.CompareTo(bb);
            case (DateOnly or DateTime, DateOnly or DateTime): return AsDateTime(a).CompareTo(AsDateTime(b));
            case (byte[] xa, byte[] xb): return xa.AsSpan().SequenceCompareTo(xb);
        }
        int ra = Rank(a), rb = Rank(b);
        return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a.ToString(), b.ToString());

        static DateTime AsDateTime(object o) => o is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : (DateTime)o;
        static int Rank(object o) => o switch
        {
            bool => 0, decimal => 1, DateOnly or DateTime => 2, string => 3, byte[] => 4, _ => 5,
        };
    }

    // ---- type mapping ---------------------------------------------------------------------

    private static char NormalizeTypeChar(char t) => char.ToUpperInvariant(t);

    private static (char vfp, Type clr) MapInfer(VfpTypeInfo info) => info.Type switch
    {
        VfpType.Character => ('C', typeof(string)),
        VfpType.Numeric => ('N', typeof(decimal)),
        VfpType.Integer => ('I', typeof(int)),
        VfpType.Currency => ('Y', typeof(decimal)),
        VfpType.Date => ('D', typeof(DateOnly)),
        VfpType.DateTime => ('T', typeof(DateTime)),
        VfpType.Logical => ('L', typeof(bool)),
        _ => ('C', typeof(string)),
    };

    private static Type ClrForDbfType(char t) => char.ToUpperInvariant(t) switch
    {
        'C' or 'M' or 'V' => typeof(string),
        'N' or 'F' => typeof(decimal),
        'B' => typeof(double),
        'I' => typeof(int),
        'Y' => typeof(decimal),
        'D' => typeof(DateOnly),
        'T' or '@' => typeof(DateTime),
        'L' => typeof(bool),
        'G' or 'P' or 'Q' or 'W' => typeof(byte[]),
        _ => typeof(string),
    };

    // ---- per-record row state -------------------------------------------------------------

    private readonly struct Row
    {
        public readonly DbfRecord Rec;
        public readonly IRowContext Ctx;
        public Row(DbfRecord rec, IRowContext ctx) { Rec = rec; Ctx = ctx; }
    }

    /// <summary>Adapts a <see cref="DbfRecord"/> to <see cref="IRowContext"/>, stripping a leading
    /// <c>alias.</c> qualifier so <c>SELECT a.id FROM person a</c> resolves field <c>id</c>. Character fields read at
    /// full width (<see cref="DbfRecord.GetExpressionValue(string)"/>), as in a VFP cursor.</summary>
    private sealed class RowContext : IRowContext, IVfpValueRow
    {
        private readonly DbfRecord _rec;
        private readonly string _alias;
        private readonly IRowContext? _outer;
        private readonly HashSet<string>? _cols; // non-null ⇒ alias-/outer-aware resolution
        private readonly bool _vfpBlanks;        // VfpBlankFields: a blank field reads as its typed empty

        /// <summary>The legacy context (no correlation): resolves a field by stripping any
        /// <c>alias.</c> qualifier — behaviour preserved verbatim for the fast WHERE path.</summary>
        public RowContext(DbfRecord rec, int recNo, int recCount, string alias, bool vfpBlanks)
        {
            _rec = rec; RecNo = recNo; RecCount = recCount; _alias = alias; _vfpBlanks = vfpBlanks;
        }

        /// <summary>The alias-/outer-aware context used by the row-by-row WHERE path: a qualified
        /// <c>alias.field</c> resolves here only when <paramref name="alias"/> matches and the field is
        /// one of <paramref name="cols"/>; anything else falls back to <paramref name="outer"/> (the
        /// correlated parent row) — which is <see langword="null"/> for a top-level query.</summary>
        public RowContext(DbfRecord rec, int recNo, int recCount, string alias, IRowContext? outer, HashSet<string> cols,
            bool vfpBlanks)
        {
            _rec = rec; RecNo = recNo; RecCount = recCount; _alias = alias; _outer = outer; _cols = cols;
            _vfpBlanks = vfpBlanks;
        }

        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public IRowContext? Outer => _outer;
        public bool HasAlias(string q) => string.Equals(q, _alias, StringComparison.OrdinalIgnoreCase);

        public object? GetField(string name)
        {
            if (_cols is null)
            {
                int d = name.LastIndexOf('.');
                return ReadField(_rec, d >= 0 ? name[(d + 1)..] : name, _vfpBlanks);
            }

            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string alias = name[..dot], field = name[(dot + 1)..];
                if (string.Equals(alias, _alias, StringComparison.OrdinalIgnoreCase) && _cols.Contains(field))
                    return ReadField(_rec, field, _vfpBlanks);
                return _outer?.GetField(name); // correlated reference to an outer source
            }
            if (_cols.Contains(name)) return ReadField(_rec, name, _vfpBlanks);
            return _outer?.GetField(name);
        }
        public VfpValue GetValue(string name)
        {
            if (_cols is null)
            {
                int d = name.LastIndexOf('.');
                return _rec.GetScaledValue(d >= 0 ? name[(d + 1)..] : name, _vfpBlanks);
            }

            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string alias = name[..dot], field = name[(dot + 1)..];
                if (string.Equals(alias, _alias, StringComparison.OrdinalIgnoreCase) && _cols.Contains(field))
                    return _rec.GetScaledValue(field, _vfpBlanks);
                return _outer is null ? VfpValue.Null : VfpRuntime.GetField(_outer, name); // correlated reference to an outer source
            }
            if (_cols.Contains(name)) return _rec.GetScaledValue(name, _vfpBlanks);
            return _outer is null ? VfpValue.Null : VfpRuntime.GetField(_outer, name);
        }
    }

    /// <summary>A row of a query microVFP runs: the scan row answers fields, <c>RECNO()</c>, <c>RECCOUNT()</c>,
    /// <c>DELETED()</c> and every other function the engine implements, exactly as on the plain path; any
    /// other name — a UDF, a memvar array, a microVFP function, an unknown name — goes to the interpreter, so
    /// a UDF runs and an unknown function raises VFP's error 1 instead of reading <c>.NULL.</c>. Functions the
    /// interpreter answers see its work areas, not the scan row (VFP9 scans through hidden work areas too).</summary>
    private sealed class HostedRow : IRowContext, IVfpFunctionHost, IVfpValueRow
    {
        private readonly IRowContext _row;
        private readonly IVfpFunctionHost _host;
        public HostedRow(IRowContext row, IVfpFunctionHost host) { _row = row; _host = host; }
        public IRowContext Inner => _row;
        public int RecNo => _row.RecNo;
        public int RecCount => _row.RecCount;
        public bool Deleted => _row.Deleted;
        public object? GetField(string name) => _row.GetField(name);
        public VfpValue GetValue(string name) => VfpRuntime.GetField(_row, name);

        public bool TryInvoke(string upperName, VfpValue[] args, EvaluationContext ctx, out VfpValue result)
        {
            if (VfpRuntime.EngineFunctionNames.Contains(upperName)) { result = default; return false; }
            return _host.TryInvoke(upperName, args, ctx, out result);
        }

        public VfpValue UnknownFunction(string upperName, VfpValue[] args) => _host.UnknownFunction(upperName, args);
    }

    /// <summary>The HAVING row over <paramref name="rep"/>: an <see cref="AliasRowContext"/>, still answered by
    /// the microVFP host when the representative row is a hosted one (a UDF in HAVING runs).</summary>
    private static IRowContext AliasRow(IRowContext rep, object?[] row, Dictionary<string, int> aliases, Func<string, bool> isSourceField, IReadOnlyList<SqlColumn> columns)
    {
        var alias = new AliasRowContext(rep, row, aliases, isSourceField, columns);
        return rep is IVfpFunctionHost host ? new HostedRow(alias, host) : alias;
    }

    /// <summary>The row context a HAVING sees: a bare name that is a select-list alias (and no field of the
    /// query's sources — a source field of the same name wins, as in VFP9) reads that column of the
    /// projected output row; everything else resolves against the representative source row.</summary>
    private sealed class AliasRowContext : IRowContext, IVfpValueRow
    {
        private readonly IRowContext _rep;
        private readonly object?[] _row;
        private readonly Dictionary<string, int> _aliases;
        private readonly Func<string, bool> _isSourceField;
        private readonly IReadOnlyList<SqlColumn> _columns;

        public AliasRowContext(IRowContext rep, object?[] row, Dictionary<string, int> aliases, Func<string, bool> isSourceField, IReadOnlyList<SqlColumn> columns)
        {
            _rep = rep; _row = row; _aliases = aliases; _isSourceField = isSourceField; _columns = columns;
        }

        public int RecNo => _rep.RecNo;
        public int RecCount => _rep.RecCount;
        public bool Deleted => _rep.Deleted;

        public VfpValue GetValue(string name)
        {
            if (_aliases.Count > 0 && !name.Contains('.') && _aliases.TryGetValue(name, out int col)
                && col < _row.Length && !_isSourceField(name))
                return VfpValue.FromField(_row[col], _columns[col].VfpType, _columns[col].Decimals);
            return VfpRuntime.GetField(_rep, name);
        }

        public object? GetField(string name)
        {
            if (_aliases.Count > 0 && !name.Contains('.') && _aliases.TryGetValue(name, out int col)
                && col < _row.Length && !_isSourceField(name))
                return _row[col];
            return _rep.GetField(name);
        }
    }

    /// <summary>An empty row context (all fields <see langword="null"/>) used as the representative
    /// of an EMPTY aggregate group, so whole-table aggregates over no rows never index members[0].</summary>
    private sealed class EmptyRowContext : IRowContext
    {
        public static readonly EmptyRowContext Instance = new();
        public int RecNo => 0;
        public int RecCount => 0;
        public bool Deleted => false;
        public object? GetField(string name) => null;
    }

    /// <summary>A dummy row context used for UNION result rows, where expressions cannot be re-evaluated.
    /// ORDER BY on a union must resolve by 1-based ordinal only, never by expression.</summary>
    private sealed class DummyRowContext : IRowContext
    {
        public int RecNo => 0;
        public int RecCount => 0;
        public bool Deleted => false;
        public object? GetField(string name) => null;
    }

    /// <summary>Adapts a <see cref="DbfTable"/> to the expression-engine static schema contract,
    /// stripping a leading <c>alias.</c> qualifier.</summary>
    internal sealed class TableSchema : ISchema
    {
        private readonly DbfTable _table;
        public TableSchema(DbfTable table, string alias) { _table = table; }
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            int dot = name.LastIndexOf('.');
            string field = dot >= 0 ? name[(dot + 1)..] : name;
            foreach (var c in _table.Columns)
                if (string.Equals(c.Name, field, StringComparison.OrdinalIgnoreCase))
                {
                    type = c.Type; length = c.Length; decimals = c.Decimal;
                    return true;
                }
            type = '\0'; length = 0; decimals = 0;
            return false;
        }
    }

    // ---- join source / composite contracts ------------------------------------------------

    /// <summary>One resolved JOIN/FROM source: its alias, open table + structural CDX, column set,
    /// surviving rows (for the scan-fallback inner candidate set / driving rowset) and the scratch
    /// CDX handle to dispose when the executor owns the table.</summary>
    internal sealed class JoinSource
    {
        public string Alias { get; }
        public DbfTable Table { get; }
        public CdxFile? Cdx { get; }
        public DbfColumn[] Columns { get; }
        public HashSet<string> ColNames { get; }
        public List<DbfRecord> Survivors { get; }
        /// <summary>The 1-based recno of each entry in <see cref="Survivors"/> (parallel list), so a
        /// RIGHT/FULL step can mark matched inner rows by their stable recno identity (NOT by raw bytes,
        /// which collide for byte-identical rows in an unconstrained DBF).</summary>
        public List<int> SurvivorRecnos { get; }
        /// <summary>1-based recno → physical record (INCLUDING deleted rows), so matched recnos resolve
        /// without <see cref="QueryResult.GetRecords"/> dropping deleted rows under SET DELETED OFF.</summary>
        public Dictionary<int, DbfRecord> ByRecno { get; }
        public CdxFile? ScratchCdx { get; }

        public JoinSource(string alias, DbfTable table, CdxFile? cdx, DbfColumn[] columns,
            HashSet<string> colNames, List<DbfRecord> survivors, List<int> survivorRecnos,
            Dictionary<int, DbfRecord> byRecno, CdxFile? scratchCdx)
        {
            Alias = alias; Table = table; Cdx = cdx; Columns = columns;
            ColNames = colNames; Survivors = survivors; SurvivorRecnos = survivorRecnos;
            ByRecno = byRecno; ScratchCdx = scratchCdx;
        }
    }

    /// <summary>A detected equi-join key: the inner source's field name (for the index filter) and the
    /// outer-side expression whose value drives the seek.</summary>
    private readonly record struct EquiKey(string InnerField, VfpExpression OuterExpr);

    /// <summary>One nested-loop step: the join type, the ON predicate (null = comma cross-join), and
    /// the optional detected equi-join key for index acceleration.</summary>
    private readonly record struct JoinStep(JoinType Type, VfpExpression? On, EquiKey? Equi);

    /// <summary>A COMPOSITE row context spanning every source's current row. Resolves
    /// <c>alias.field</c> (qualified) and bare <c>field</c> (the single source owning it — an
    /// AMBIGUOUS bare name is rejected). A LEFT-unmatched source's row is null → all its fields read
    /// as <see langword="null"/>.</summary>
    private sealed class CompositeRowContext : IRowContext, IVfpValueRow
    {
        private readonly JoinSource[] _src;
        private readonly DbfRecord?[] _recs;
        private readonly IRowContext? _outer;
        private readonly bool _vfpBlanks; // VfpBlankFields: a blank field reads as its typed empty
        public CompositeRowContext(JoinSource[] src, DbfRecord?[] recs, IRowContext? outer, bool vfpBlanks)
        { _src = src; _recs = recs; _outer = outer; _vfpBlanks = vfpBlanks; }
        public IRowContext? Outer => _outer;
        public bool HasAlias(string q)
            => Array.Exists(_src, s => string.Equals(s.Alias, q, StringComparison.OrdinalIgnoreCase));
        public int RecNo => 0;
        public int RecCount => 0;
        public bool Deleted => false;

        public object? GetField(string name)
        {
            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string alias = name[..dot], field = name[(dot + 1)..];
                for (int i = 0; i < _src.Length; i++)
                    if (string.Equals(_src[i].Alias, alias, StringComparison.OrdinalIgnoreCase))
                        return _recs[i] is { } r && _src[i].ColNames.Contains(field) ? ReadField(r, field, _vfpBlanks) : null;
                return _outer?.GetField(name); // correlated reference to an outer source
            }

            int found = -1, count = 0;
            for (int i = 0; i < _src.Length; i++)
                if (_src[i].ColNames.Contains(name)) { found = i; count++; }
            if (count == 0) return _outer?.GetField(name);
            if (count > 1)
                throw new FoxDbfSqlException($"Column '{name}' is ambiguous; qualify it with an alias.");
            return _recs[found] is { } rec ? ReadField(rec, name, _vfpBlanks) : null;
        }
        public VfpValue GetValue(string name)
        {
            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string alias = name[..dot], field = name[(dot + 1)..];
                for (int i = 0; i < _src.Length; i++)
                    if (string.Equals(_src[i].Alias, alias, StringComparison.OrdinalIgnoreCase))
                        return _recs[i] is { } r && _src[i].ColNames.Contains(field) ? r.GetScaledValue(field, _vfpBlanks) : VfpValue.Null;
                return _outer is null ? VfpValue.Null : VfpRuntime.GetField(_outer, name); // correlated reference to an outer source
            }

            int found = -1, count = 0;
            for (int i = 0; i < _src.Length; i++)
                if (_src[i].ColNames.Contains(name)) { found = i; count++; }
            if (count == 0) return _outer is null ? VfpValue.Null : VfpRuntime.GetField(_outer, name);
            if (count > 1)
                throw new FoxDbfSqlException($"Column '{name}' is ambiguous; qualify it with an alias.");
            return _recs[found] is { } rec ? rec.GetScaledValue(name, _vfpBlanks) : VfpValue.Null;
        }
    }

    /// <summary>The composite static schema (for type inference): resolves <c>alias.field</c> and a
    /// bare <c>field</c> across all sources, rejecting an ambiguous bare name.</summary>
    private sealed class CompositeSchema : ISchema
    {
        private readonly JoinSource[] _src;
        public CompositeSchema(JoinSource[] src) { _src = src; }

        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            type = '\0'; length = 0; decimals = 0;
            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string alias = name[..dot], field = name[(dot + 1)..];
                foreach (var s in _src)
                    if (string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase))
                        return Take(FindColumn(s, field), out type, out length, out decimals);
                return false;
            }

            DbfColumn? found = null; int count = 0;
            foreach (var s in _src)
            {
                var c = FindColumn(s, name);
                if (c is not null) { found = c; count++; }
            }
            if (count > 1)
                throw new FoxDbfSqlException($"Column '{name}' is ambiguous; qualify it with an alias.");
            return Take(found, out type, out length, out decimals);
        }

        private static bool Take(DbfColumn? c, out char type, out int length, out int decimals)
        {
            if (c is null) { type = '\0'; length = 0; decimals = 0; return false; }
            type = c.Type; length = c.Length; decimals = c.Decimal;
            return true;
        }
    }
}
