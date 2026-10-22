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

    public SelectExecutor(VfpSession session, IRowContext? outer)
    {
        _session = session;
        _outer = outer;
    }

    public SqlResult Run(SelectStatement sel)
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

        if (into.Kind == IntoKind.Table)
        {
            string path = _session.ResolveIntoTablePath(into.Name);
            using (var w = DbfWriter.Create(path, defs, new DbfCreateOptions { Overwrite = true }))
                w.AppendRecords(rows);
            return SqlResult.Dml(rows.Count);
        }

        // INTO CURSOR — we ALWAYS materialize a real temp table (effectively NOFILTER semantics);
        // READWRITE is implicit (it is a real, updatable table). Register it in the session so the
        // cursor alias is queryable until the session is disposed or the name is reused.
        string temp = _session.NewCursorTempPath(into.Name);
        using (var w = DbfWriter.Create(temp, defs, new DbfCreateOptions { Overwrite = true }))
            w.AppendRecords(rows);
        _session.RegisterCursor(into.Name, temp);
        return SqlResult.Dml(rows.Count);
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
            defs.Add(ToColumnDef(col, name));
        }
        return defs;
    }

    private static DbfColumnDef ToColumnDef(SqlColumn col, string name)
    {
        switch (char.ToUpperInvariant(col.VfpType))
        {
            case 'I': return new DbfColumnDef(name, 'I');             // Integer (fixed 4)
            case 'Y': return new DbfColumnDef(name, 'Y');             // Currency (fixed 8)
            case 'B': return new DbfColumnDef(name, 'B');             // Double (fixed 8)
            case 'D': return new DbfColumnDef(name, 'D');             // Date (fixed 8)
            case 'T': case '@': return new DbfColumnDef(name, 'T');   // DateTime (fixed 8)
            case 'L': return new DbfColumnDef(name, 'L');             // Logical (fixed 1)
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
                return new DbfColumnDef(name, 'N', len, dec);
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
        var ctx = _session.SqlContext();

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
            return Execute(table, ctx, sel, rowAlias);
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

    private SqlResult Execute(DbfTable table, EvaluationContext ctx, SelectStatement sel, string alias)
    {
        var schema = new TableSchema(table, alias);

        // (1) WHERE → the surviving rows (recno order).
        var rows = CollectSingleRows(table, ctx, sel, alias);

        // (2) column schema (independent of the rows).
        var columns = BuildColumns(table, alias, schema, sel.Items);

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
            pairs = Aggregate(ctx, sel, rows.ConvertAll(r => (IRowContext)r.Ctx));
        }

        Func<SelectItem, int> starWidth = _ => table.Columns.Count;

        // (4) ORDER BY (collation-aware via ctx).
        if (sel.OrderBy.Count > 0)
            pairs = ApplyOrderBy(pairs, sel.OrderBy, sel.Items, starWidth, aggregate, ctx);

        // (5) DISTINCT (collation-aware dedup).
        if (sel.Distinct)
            pairs = Distinct(pairs, ctx);

        // (6) TOP n — VFP keeps ALL rows whose full ORDER BY key ties the nth row's key.
        if (sel.Top is int top)
            pairs = ApplyTop(pairs, top, sel.OrderBy, sel.Items, starWidth, aggregate, ctx);

        return Materialize(columns, pairs);
    }

    /// <summary>
    /// Resolves the WHERE for a single base table into surviving rows (recno order).
    /// <para>
    /// FAST PATH (no subquery predicate AND no outer/correlation binding) — UNCHANGED from before:
    /// <see cref="QueryOptimizer.FindRecords"/> drives the WHERE text (full scan + residual; SET DELETED
    /// honoured by ctx), and the matched recnos are materialized through
    /// <see cref="DbfTable.EnumerateAll(bool)"/> so SET DELETED OFF keeps matched deleted rows.
    /// </para>
    /// <para>
    /// ROW-BY-ROW PATH (a WHERE predicate tree with subquery atoms, and/or a bound outer row for a
    /// correlated subquery): each candidate row is bound in an alias-/outer-aware <see cref="RowContext"/>
    /// and the predicate (or plain expression, with the outer row in scope) is evaluated per row.
    /// </para>
    /// </summary>
    private List<Row> CollectSingleRows(DbfTable table, EvaluationContext ctx, SelectStatement sel, string alias)
    {
        if (sel.WherePredicate is null && _outer is null)
        {
            // The optimizer's residual resolves BARE field names only, so a qualifier naming THIS
            // query's own source (alias.field / table.field) is dropped first — as the join pushdown
            // does for its driving source. With no outer row bound, any other qualifier cannot be a
            // correlated reference and is left untouched.
            string? filter = sel.Where?.Text;
            filter = string.IsNullOrWhiteSpace(filter) ? ".T." : StripDrivingQualifier(filter, alias);
            var qr = _session.FindRecords(table, null, filter, ctx);
            var hits = new HashSet<int>(qr.RecordNumbers);
            var rows = new List<Row>(hits.Count);
            int recno = 0;
            foreach (var rec in table.EnumerateAll(includeDeleted: true))
            {
                recno++;
                if (!hits.Contains(recno)) continue;
                rows.Add(new Row(rec, new RowContext(rec, recno, table.RecordCount, alias)));
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
            var rc = new RowContext(rec, rn, table.RecordCount, alias, _outer, colNames);
            bool keep;
            if (sel.WherePredicate is not null)
                keep = EvalPredicate(sel.WherePredicate, rc, ctx) == true;
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

    /// <summary><c>x IN set</c>, matching what VFP9 actually does (cross-checked against the local
    /// the VFP9 oracle): a NULL <paramref name="x"/> is unknown (never IN, never NOT IN); a NULL set
    /// member is simply IGNORED — it does NOT poison the result. So <c>x IN set</c> is true on a match,
    /// else false; <c>x NOT IN set</c> (the caller negates) is therefore true when x matches no non-null
    /// member, even when the set contains a NULL. An empty set ⇒ false.
    /// <para>
    /// This differs from textbook ANSI three-valued logic (where a NULL in the set would make NOT IN
    /// unknown / empty), but it is VFP's observed behaviour for an indexed subquery — and the executor
    /// is deliberately index-INDEPENDENT, so it gives this same answer with or without a CDX.
    /// </para></summary>
    private static bool? InMembership(VfpValue x, IReadOnlyList<VfpValue> set, EvaluationContext ctx)
    {
        if (x.IsNull) return null;
        foreach (var v in set)
            if (!v.IsNull && ValueEqSql(x, v, ctx)) return true;
        return false;
    }

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
                cached = new SelectExecutor(_session).Run(sub).Rows.ToList();
                _uncorrelatedRows[sub] = cached;
            }
            return cached;
        }
        return new SelectExecutor(_session, outerRow).Run(sub).Rows.ToList();
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
                    (r, new DummyRowContext() as IRowContext)).ToList(), _session.SqlContext())
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
            var ctx = _session.SqlContext();
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
        var ctx = _session.SqlContext();
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
                ? CollectPushdownFilters(sel, src)
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
                    if (EvalPredicate(sel.WherePredicate, new CompositeRowContext(src, c, _outer), ctx) == true)
                        kept.Add(c);
                composites = kept;
            }
            else if (sel.Where is not null)
            {
                var kept = new List<DbfRecord?[]>(composites.Count);
                foreach (var c in composites)
                {
                    var v = sel.Where.Evaluate(new CompositeRowContext(src, c, _outer), ctx);
                    if (v.Type == VfpType.Logical && v.AsLogical) kept.Add(c);
                }
                composites = kept;
            }

            var schema = new CompositeSchema(src);
            var columns = BuildJoinColumns(src, schema, sel.Items);

            bool aggregate = sel.GroupBy.Count > 0 || sel.Items.Any(ContainsAggregate);

            List<(object?[] row, IRowContext rep)> pairs;
            if (!aggregate)
            {
                pairs = new List<(object?[], IRowContext)>(composites.Count);
                foreach (var c in composites)
                {
                    var rc = new CompositeRowContext(src, c, _outer);
                    pairs.Add((ProjectJoinRow(rc, src, sel.Items, ctx), rc));
                }
            }
            else
            {
                var rcs = composites.ConvertAll(c => (IRowContext)new CompositeRowContext(src, c, _outer));
                pairs = Aggregate(ctx, sel, rcs);
            }

            Func<SelectItem, int> starWidth = it => StarWidthJoin(it, src);

            if (sel.OrderBy.Count > 0)
                pairs = ApplyOrderBy(pairs, sel.OrderBy, sel.Items, starWidth, aggregate, ctx);

            if (sel.Distinct)
                pairs = Distinct(pairs, ctx);

            if (sel.Top is int top)
                pairs = ApplyTop(pairs, top, sel.OrderBy, sel.Items, starWidth, aggregate, ctx);

            return Materialize(columns, pairs);
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
    /// it is either useless to push or would be evaluated twice in two different contexts.</summary>
    private static bool IsDrivingOnly(string text, JoinSource[] src)
    {
        VfpExpression expr;
        try { expr = VfpExpression.Parse(text); }
        catch { return false; }      // unparseable here ⇒ let the post-join path report it

        if (expr.ContainsFunction(static (name, _) => IsUnpushableFunction(name)))
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
    /// per call (RAND / SECONDS / DATETIME / TIME / DATE).</summary>
    private static bool IsUnpushableFunction(string name) => name is
        "RECNO" or "RECCOUNT" or "DELETED" or
        "EOF" or "BOF" or "FOUND" or "SEEK" or "ALIAS" or "SELECT" or "USED" or
        "EVALUATE" or "TYPE" or "SYS" or
        "RAND" or "SECONDS" or "DATETIME" or "TIME" or "DATE";

    /// <summary>The WHERE conjuncts that read ONLY the driving source and are therefore safe to
    /// evaluate against it before the join chain runs. Empty when there is no WHERE.</summary>
    private static List<string> CollectPushdownFilters(SelectStatement sel, JoinSource[] src)
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

        conjuncts.RemoveAll(t => !IsDrivingOnly(t, src));
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
                    var v = step.On!.Evaluate(new CompositeRowContext(src, cand, _outer), ctx);
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
            var v = equi.OuterExpr.Evaluate(new CompositeRowContext(src, combo, _outer), ctx);
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

        return new EquiKey(ifield, VfpExpression.Parse(outerSide));
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
                    outv.Add(r.Rec[c.Name]);
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
        CompositeRowContext rc, JoinSource[] src, IReadOnlyList<SelectItem> items, EvaluationContext ctx)
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

    private static List<(object?[] row, IRowContext rep)> Aggregate(
        EvaluationContext ctx, SelectStatement sel, IReadOnlyList<IRowContext> rows)
    {
        // group by the tuple of GROUP BY expression values (normalized), preserving first-seen order.
        var groups = new List<(List<object?> Key, List<IRowContext> Members)>();
        if (sel.GroupBy.Count == 0)
        {
            groups.Add((new List<object?>(), rows.ToList())); // whole-table aggregate: exactly one group.
        }
        else
        {
            foreach (var r in rows)
            {
                var key = sel.GroupBy.Select(g => Norm(ToClr(g.Evaluate(r, ctx)))).ToList();
                int idx = groups.FindIndex(x => KeyEq(x.Key, key, ctx));
                if (idx < 0) { groups.Add((key, new List<IRowContext> { r })); }
                else groups[idx].Members.Add(r);
            }
        }

        var result = new List<(object?[] row, IRowContext rep)>();
        foreach (var (_, members) in groups)
        {
            // A whole-table aggregate over an EMPTY set still emits one row (COUNT→0, SUM→0,
            // AVG/MIN/MAX→null). NEVER index members[0] unconditionally — use a synthetic context.
            IRowContext rep = members.Count > 0 ? members[0] : EmptyRowContext.Instance;
            if (sel.Having is not null && !EvalHaving(sel.Having, members, rep, ctx)) continue;
            var row = sel.Items.Select(it => ProjectAggregateItem(it, members, rep, ctx)).ToArray();
            result.Add((row, rep));
        }
        return result;
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

        // non-aggregate item in an aggregate query (a GROUP BY column) → value of the first member;
        // an empty group has no member to read → null.
        return members.Count == 0 ? null : ToClr(it.Expression!.Evaluate(rep, ctx));
    }

    private static object? EvaluateAggregateExpression(
        VfpExpression expression, List<IRowContext> members, IRowContext rep, EvaluationContext ctx)
    {
        var value = expression.EvaluateWithFunctionResolver(rep, ctx,
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
        return Norm(ToClr(value));
    }

    private static object? ComputeAggregate(
        string func, ExpressionArgument arg, List<IRowContext> members, EvaluationContext ctx)
    {
        if (func == "COUNT")
            return (decimal)members.Count(m => !arg.Evaluate(m, ctx).IsNull);

        var vals = members.Select(m => arg.Evaluate(m, ctx)).Where(v => !v.IsNull).ToList();
        switch (func)
        {
            case "SUM": return vals.Aggregate(0m, (acc, v) => acc + v.AsNumber);
            case "AVG": return vals.Count == 0 ? (decimal?)null : vals.Aggregate(0m, (acc, v) => acc + v.AsNumber) / vals.Count;
            case "MIN": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfp(a, b, ctx) <= 0 ? a : b)));
            case "MAX": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfp(a, b, ctx) >= 0 ? a : b)));
            default: throw new NotSupportedException($"Aggregate '{func}' is not supported.");
        }
    }

    private static bool EvalHaving(VfpExpression having, List<IRowContext> members, IRowContext rep, EvaluationContext ctx)
    {
        // A compound HAVING (top-level AND/OR) is not handled by the single-comparison split below;
        // surface it cleanly instead of silently mis-evaluating one side.
        if (HasTopLevelBoolean(having.Text))
            throw new NotSupportedException("Compound HAVING is not supported yet — Phase 2.");

        var m = HavingCmpRx.Match(having.Text);
        if (!m.Success)
        {
            var v = members.Count == 0 ? VfpValue.Logical(false) : having.Evaluate(rep, ctx);
            return v.Type == VfpType.Logical && v.AsLogical;
        }

        VfpValue Side(string s)
        {
            var parsed = SafeParse(s);
            if (parsed is not null && ContainsAggregate(parsed))
                return VfpValue.FromClr(EvaluateAggregateExpression(parsed, members, rep, ctx));
            return VfpExpression.Parse(s).Evaluate(rep, ctx);
        }

        var lv = Side(m.Groups["l"].Value);
        var rv = Side(m.Groups["r"].Value);
        string op = m.Groups["op"].Value;

        // '=' / '<>' follow SQL/ANSI '=' semantics (same as the WHERE path); '==' stays exact;
        // the ordering operators use the collation-aware VFP comparison.
        if (op is "=" or "<>" or "!=")
        {
            bool eq = ValueEqSql(lv, rv, ctx);
            return op == "=" ? eq : !eq;
        }
        int c = CompareVfp(lv, rv, ctx);
        return op switch
        {
            "==" => c == 0,
            "<" => c < 0,
            "<=" => c <= 0,
            ">" => c > 0,
            ">=" => c >= 0,
            _ => false,
        };
    }

    /// <summary>SQL <c>=</c> equality (governed by SET ANSI when SqlSemantics is on) used by the
    /// HAVING path, mirroring the WHERE path's runtime operator. Strings route through the canonical
    /// <see cref="VfpRuntime.StrEqSql"/>; other types use the collation-aware VFP comparison.</summary>
    private static bool ValueEqSql(VfpValue a, VfpValue b, EvaluationContext ctx)
    {
        if (a.Type == VfpType.Character && b.Type == VfpType.Character)
            return VfpRuntime.StrEqSql(a.AsString, b.AsString, ctx);
        return CompareVfp(a, b, ctx) == 0;
    }

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

    private static List<(object?[] row, IRowContext rep)> ApplyOrderBy(
        List<(object?[] row, IRowContext rep)> pairs, IReadOnlyList<OrderItem> orderBy,
        IReadOnlyList<SelectItem> items, Func<SelectItem, int> starWidth, bool aggregate, EvaluationContext ctx)
    {
        IOrderedEnumerable<(object?[] row, IRowContext rep)>? ordered = null;
        for (int k = 0; k < orderBy.Count; k++)
        {
            var ob = orderBy[k];
            var keySel = OrderKeySelector(ob, items, starWidth, aggregate, ctx);
            var cmp = Comparer<(object?[] row, IRowContext rep)>.Create((a, b) => CompareVfp(keySel(a), keySel(b), ctx));
            ordered = k == 0
                ? (ob.Descending ? pairs.OrderByDescending(x => x, cmp) : pairs.OrderBy(x => x, cmp))
                : (ob.Descending ? ordered!.ThenByDescending(x => x, cmp) : ordered!.ThenBy(x => x, cmp));
        }
        return ordered!.ToList();
    }

    private static List<(object?[] row, IRowContext rep)> Distinct(
        List<(object?[] row, IRowContext rep)> pairs, EvaluationContext ctx)
    {
        var seen = new List<(object?[] row, IRowContext rep)>();
        foreach (var p in pairs)
            if (!seen.Any(s => RowEq(s.row, p.row, ctx))) seen.Add(p);
        return seen;
    }

    /// <summary>TOP n that honours ORDER BY ties: keeps the first <paramref name="top"/> rows PLUS any
    /// trailing rows whose full ORDER BY key equals the nth row's key (VFP semantics). Without an
    /// ORDER BY it is a plain take.</summary>
    private static List<(object?[] row, IRowContext rep)> ApplyTop(
        List<(object?[] row, IRowContext rep)> pairs, int top, IReadOnlyList<OrderItem> orderBy,
        IReadOnlyList<SelectItem> items, Func<SelectItem, int> starWidth, bool aggregate, EvaluationContext ctx)
    {
        top = Math.Max(0, top);
        if (top == 0) return new List<(object?[], IRowContext)>();
        if (top >= pairs.Count) return pairs;
        if (orderBy.Count == 0) return pairs.Take(top).ToList();

        var keySels = orderBy.Select(ob => OrderKeySelector(ob, items, starWidth, aggregate, ctx)).ToList();
        bool KeyTies(int i, int j) => keySels.All(ks => CompareVfp(ks(pairs[i]), ks(pairs[j]), ctx) == 0);

        int end = top; // boundary row is at index top-1; extend while subsequent rows tie its key.
        while (end < pairs.Count && KeyTies(end, top - 1)) end++;
        return pairs.Take(end).ToList();
    }

    /// <summary>Builds the ORDER BY key selector for one entry: a 1-based ordinal reads the output
    /// column; in an aggregate query an expression matching a select item (by alias/text) ALSO reads
    /// the already-computed output column (so <c>ORDER BY COUNT(*)</c> is not re-evaluated per row);
    /// otherwise the expression is evaluated against the representative row.</summary>
    private static Func<(object?[] row, IRowContext rep), VfpValue> OrderKeySelector(
        OrderItem ob, IReadOnlyList<SelectItem> items, Func<SelectItem, int> starWidth, bool aggregate, EvaluationContext ctx)
    {
        int colIdx = -1;
        if (ob.Ordinal is int ord) colIdx = ord - 1;
        else if (aggregate) colIdx = ResolveOrderColumn(ob, items, starWidth);

        if (colIdx >= 0)
        {
            int c = colIdx;
            return t => VfpValue.FromClr(c < t.row.Length ? t.row[c] : null);
        }
        return t => ob.Expression!.Evaluate(t.rep, ctx);
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

    private static SqlResult Materialize(IReadOnlyList<SqlColumn> columns, List<(object?[] row, IRowContext rep)> pairs)
    {
        // Coerce each cell to its SqlColumn.ClrType so the DataReader's typed getters never see a
        // mismatched box.
        var outRows = new List<object?[]>(pairs.Count);
        foreach (var p in pairs)
            outRows.Add(CoerceRow(p.row, columns));
        return new SqlResult(columns, outRows);
    }

    // ---- schema (single table) ------------------------------------------------------------

    private static IReadOnlyList<SqlColumn> BuildColumns(
        DbfTable table, string alias, TableSchema schema, IReadOnlyList<SelectItem> items)
    {
        var cols = new List<SqlColumn>();
        int exp = 0;
        foreach (var it in items)
        {
            if (it.IsStar)
            {
                foreach (var c in table.Columns)
                    cols.Add(new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)));
                continue;
            }

            if (it.AggregateStarFunction is not null)
            {
                cols.Add(new SqlColumn(it.Alias ?? it.AggregateStarFunction.ToUpperInvariant(), 'N', 10, 0, typeof(decimal)));
                continue;
            }

            if (IsAggregate(it, out var func, out var arg))
            {
                if (func is "MIN" or "MAX" && arg is not null)
                {
                    var info = SafeInfer(arg, schema);
                    var (vc, clr) = MapInfer(info);
                    cols.Add(new SqlColumn(it.Alias ?? func, vc, info.Length, info.Decimals, clr));
                }
                else
                {
                    cols.Add(new SqlColumn(it.Alias ?? func, 'N', 18, func == "AVG" ? 6 : 2, typeof(decimal)));
                }
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
                    cols.Add(new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)));
                    continue;
                }
            }

            var ti = it.Expression!.InferType(schema);
            var (typeChar, clrType) = MapInfer(ti);
            string name = it.Alias ?? ("EXP_" + (++exp));
            cols.Add(new SqlColumn(name, typeChar, ti.Length, ti.Decimals, clrType));
        }
        return cols;
    }

    // ---- schema (join) --------------------------------------------------------------------

    private static IReadOnlyList<SqlColumn> BuildJoinColumns(
        JoinSource[] src, CompositeSchema schema, IReadOnlyList<SelectItem> items)
    {
        // Track each column with its owning source alias (null = expression/aggregate) so duplicate
        // names — common across joined tables (e.g. emp.DEPTID and dept.DEPTID, or two SELECT * sources
        // both with a column of the same name) — can be alias-qualified afterwards, keeping every
        // column reachable by a UNIQUE name through the DataReader.
        var built = new List<(SqlColumn col, string? alias)>();
        int exp = 0;
        foreach (var it in items)
        {
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
                        built.Add((new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)), s.Alias));
                }
                if (it.StarAlias is not null && !any)
                    throw new FoxDbfSqlException($"Unknown alias '{it.StarAlias}' in '{it.StarAlias}.*'.");
                continue;
            }

            if (it.AggregateStarFunction is not null)
            {
                built.Add((new SqlColumn(it.Alias ?? it.AggregateStarFunction.ToUpperInvariant(), 'N', 10, 0, typeof(decimal)), null));
                continue;
            }

            if (IsAggregate(it, out var func, out var arg))
            {
                if (func is "MIN" or "MAX" && arg is not null)
                {
                    var info = SafeInfer(arg, schema);
                    var (vc, clr) = MapInfer(info);
                    built.Add((new SqlColumn(it.Alias ?? func, vc, info.Length, info.Decimals, clr), null));
                }
                else
                {
                    built.Add((new SqlColumn(it.Alias ?? func, 'N', 18, func == "AVG" ? 6 : 2, typeof(decimal)), null));
                }
                continue;
            }

            // plain expression: a simple (qualified or unqualified) field keeps the column's name/type;
            // an ambiguous unqualified field is rejected.
            string text = it.Expression!.Text.Trim();
            if (it.Alias is null && SimpleFieldRx.IsMatch(text))
            {
                var c = ResolveJoinField(text, src, out var ownerAlias);
                if (c is not null)
                {
                    built.Add((new SqlColumn(c.Name, NormalizeTypeChar(c.Type), c.Length, c.Decimal, ClrForDbfType(c.Type)), ownerAlias));
                    continue;
                }
            }

            var ti = it.Expression!.InferType(schema);
            var (typeChar, clrType) = MapInfer(ti);
            // A user-supplied alias is authoritative — never auto-qualify it.
            built.Add((new SqlColumn(it.Alias ?? ("EXP_" + (++exp)), typeChar, ti.Length, ti.Decimals, clrType), null));
        }
        return UniquifyColumnNames(built);
    }

    /// <summary>Resolves the final result column names so no two collide (case-insensitive). A name that
    /// appears more than once is qualified with its source alias (<c>ALIAS_FIELD</c>); if that still
    /// collides, a numeric suffix (<c>_2</c>, <c>_3</c>, …) is appended. A unique name is kept verbatim.
    /// Values are positional, so renaming a column's NAME never disturbs the row data.</summary>
    private static IReadOnlyList<SqlColumn> UniquifyColumnNames(List<(SqlColumn col, string? alias)> built)
    {
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (col, _) in built)
            nameCounts[col.Name] = nameCounts.TryGetValue(col.Name, out int n) ? n + 1 : 1;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outCols = new List<SqlColumn>(built.Count);
        foreach (var (col, alias) in built)
        {
            string baseName = nameCounts[col.Name] > 1 && alias is not null ? alias + "_" + col.Name : col.Name;
            string finalName = baseName;
            for (int k = 2; !used.Add(finalName); k++)
                finalName = baseName + "_" + k;
            outCols.Add(string.Equals(finalName, col.Name, StringComparison.Ordinal)
                ? col
                : new SqlColumn(finalName, col.VfpType, col.Length, col.Decimals, col.ClrType));
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
        return true;
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

    private static VfpExpression? SafeParse(string s)
    {
        try { return VfpExpression.Parse(s); } catch { return null; }
    }

    private static VfpTypeInfo SafeInfer(string arg, ISchema schema)
    {
        try { return VfpExpression.Parse(arg).InferType(schema); }
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
        VfpType.Character => v.AsString,
        VfpType.Logical => v.AsLogical,
        VfpType.Date => v.AsDate,
        VfpType.DateTime => v.AsDateTime,
        VfpType.Integer => v.AsInteger,    // I → int (matches SqlColumn.ClrType typeof(int))
        VfpType.Currency => v.AsNumber,    // Y → decimal
        VfpType.Numeric => v.AsNumber,     // N / F / B → decimal (B coerced to double by CoerceCell)
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
            outr[i] = i < cols.Count ? CoerceCell(row[i], cols[i].ClrType) : row[i];
        return outr;
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
        double d => (decimal)d,
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
        return a.AsNumber.CompareTo(b.AsNumber);
    }

    private static bool KeyEq(List<object?> a, List<object?> b, EvaluationContext ctx)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!CellEq(a[i], b[i], ctx)) return false;
        return true;
    }

    private static bool RowEq(object?[] a, object?[] b, EvaluationContext ctx)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!CellEq(a[i], b[i], ctx)) return false;
        return true;
    }

    private static bool CellEq(object? a, object? b, EvaluationContext ctx)
    {
        a = Norm(a); b = Norm(b);
        if (a is null || b is null) return a is null && b is null;
        if (a is decimal da && b is decimal db) return da == db;
        if (a is DateOnly oa && b is DateOnly ob) return oa == ob;
        if (a is DateTime ta && b is DateTime tb) return ta == tb;
        // String GROUP BY key / DISTINCT dedup must honour the session collation: under GENERAL
        // 'Berlin' and 'BERLIN' collate equal (one group, one DISTINCT row); under MACHINE ordinal.
        if (a is string sa && b is string sb) return ctx.Collation.Compare(sa.AsSpan(), sb.AsSpan()) == 0;
        if (a is bool ba && b is bool bb) return ba == bb;
        return Equals(a, b);
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
        public readonly RowContext Ctx;
        public Row(DbfRecord rec, RowContext ctx) { Rec = rec; Ctx = ctx; }
    }

    /// <summary>Adapts a <see cref="DbfRecord"/> to <see cref="IRowContext"/>, stripping a leading
    /// <c>alias.</c> qualifier so <c>SELECT a.id FROM person a</c> resolves field <c>id</c>.</summary>
    private sealed class RowContext : IRowContext
    {
        private readonly DbfRecord _rec;
        private readonly string _alias;
        private readonly IRowContext? _outer;
        private readonly HashSet<string>? _cols; // non-null ⇒ alias-/outer-aware resolution

        /// <summary>The legacy context (no correlation): resolves a field by stripping any
        /// <c>alias.</c> qualifier — behaviour preserved verbatim for the fast WHERE path.</summary>
        public RowContext(DbfRecord rec, int recNo, int recCount, string alias)
        {
            _rec = rec; RecNo = recNo; RecCount = recCount; _alias = alias;
        }

        /// <summary>The alias-/outer-aware context used by the row-by-row WHERE path: a qualified
        /// <c>alias.field</c> resolves here only when <paramref name="alias"/> matches and the field is
        /// one of <paramref name="cols"/>; anything else falls back to <paramref name="outer"/> (the
        /// correlated parent row) — which is <see langword="null"/> for a top-level query.</summary>
        public RowContext(DbfRecord rec, int recNo, int recCount, string alias, IRowContext? outer, HashSet<string> cols)
        {
            _rec = rec; RecNo = recNo; RecCount = recCount; _alias = alias; _outer = outer; _cols = cols;
        }

        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;

        public object? GetField(string name)
        {
            if (_cols is null)
            {
                int d = name.LastIndexOf('.');
                return _rec[d >= 0 ? name[(d + 1)..] : name];
            }

            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string alias = name[..dot], field = name[(dot + 1)..];
                if (string.Equals(alias, _alias, StringComparison.OrdinalIgnoreCase) && _cols.Contains(field))
                    return _rec[field];
                return _outer?.GetField(name); // correlated reference to an outer source
            }
            if (_cols.Contains(name)) return _rec[name];
            return _outer?.GetField(name);
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
    private sealed class CompositeRowContext : IRowContext
    {
        private readonly JoinSource[] _src;
        private readonly DbfRecord?[] _recs;
        private readonly IRowContext? _outer;
        public CompositeRowContext(JoinSource[] src, DbfRecord?[] recs, IRowContext? outer = null)
        { _src = src; _recs = recs; _outer = outer; }
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
                        return _recs[i] is { } r && _src[i].ColNames.Contains(field) ? r[field] : null;
                return _outer?.GetField(name); // correlated reference to an outer source
            }

            int found = -1, count = 0;
            for (int i = 0; i < _src.Length; i++)
                if (_src[i].ColNames.Contains(name)) { found = i; count++; }
            if (count == 0) return _outer?.GetField(name);
            if (count > 1)
                throw new FoxDbfSqlException($"Column '{name}' is ambiguous; qualify it with an alias.");
            return _recs[found] is { } rec ? rec[name] : null;
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
