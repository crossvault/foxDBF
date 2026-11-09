using System;
using System.Collections.Generic;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// The Phase-1c DML executor (INSERT / UPDATE / DELETE over a single base table). It mirrors
/// <see cref="SelectExecutor"/>'s shape: it resolves the TARGET table via the <see cref="VfpSession"/>
/// work-area model (reusing an open alias, else auto-opening it WRITABLE), drives
/// <see cref="QueryOptimizer.FindRecords"/> for the WHERE candidate set, and uses
/// <see cref="CrossVault.FoxDbf.Expressions.VfpExpression.Evaluate"/> for VALUES / SET expressions.
/// <para>
/// VFP semantics: DELETE is a SOFT delete (deletion flag only — never a pack); UPDATE/DELETE with no
/// WHERE affect ALL live rows; <c>SET DELETED ON</c> excludes already-deleted rows from the candidate
/// set; INSERT supports VALUES, memory sources and SELECT; <c>=</c> in WHERE follows SQL/ANSI semantics like SELECT.
/// </para>
/// <para>
/// Each handler resolves the target table via <see cref="VfpSession.OpenWritableTarget"/> (reusing an
/// open work area's file, else auto-opening SHARED), evaluates VALUES / SET via
/// <see cref="CrossVault.FoxDbf.Expressions.VfpExpression.Evaluate"/>, drives
/// <see cref="QueryOptimizer.FindRecords"/> for the WHERE candidate set, and writes through the
/// <see cref="DbfWriter"/>. Affected-record counts come back via <see cref="SqlResult.Dml"/>.
/// </para>
/// </summary>
internal sealed partial class DmlExecutor
{
    private readonly VfpSession _session;

    public DmlExecutor(VfpSession session) => _session = session;

    /// <summary>Executes one DML statement and returns its affected-record count via
    /// <see cref="SqlResult.Dml"/>.</summary>
    public SqlResult Run(SqlStatement statement) => statement switch
    {
        InsertStatement ins => Insert(ins),
        UpdateStatement upd => Update(upd),
        DeleteStatement del => Delete(del),
        _ => throw new NotSupportedException($"'{statement.GetType().Name}' is not a DML statement."),
    };

    // ---- INSERT ---------------------------------------------------------------------------

    private SqlResult Insert(InsertStatement st)
    {
        if (_session.DbcDefaultEvaluator is not null && _session.FindInsertArea(st.Table) is { } area)
            st = st with { Table = area.Alias };
        // Memory-source forms (VFP idioms heavily used in real SPs) map through the ACTIVE session's memvar
        // store via the bound interpreter's bridge, then append through the SAME writer path as VALUES — so
        // index maintenance, the COW transaction seam and buffering all apply unchanged.
        if (st.SourceKind == InsertSourceKind.Select) return InsertSelect(st);
        if (st.SourceKind == InsertSourceKind.Array) return InsertFromArray(st);
        if (st.SourceKind == InsertSourceKind.Memvar) return InsertFromMemvar(st);

        var target = _session.OpenWritableTarget(st.Table);
        try
        {
            var columns = target.Writer.Schema.Columns;
            var values = EvaluateValues(_session, st);
            if (st.Columns is null) { AppendAllColumns(target.Writer, columns, values); return SqlResult.Dml(1); }
            var map = MapValues(st, columns, values);
            FillDbcDefaults(_session, st.Table, ref target, map);
            target.Writer.AppendRecord(map, OmittedColumnsBlankMask(_session, st.Table, columns, map, honourSetNull: true));
        }
        finally { target.Dispose(); }
        return SqlResult.Dml(1);
    }

    /// <summary>
    /// Evaluate an <c>INSERT … VALUES</c> row and append it through <paramref name="writer"/> — the microVFP
    /// interpreter's direct-append fast path (Batch 4), which appends through its OWN cached writer with
    /// byte-identical value semantics AND identical error numbers/text (err 12 for an unknown column, the
    /// column/value count mismatch) as <see cref="Insert"/>. The caller owns the writer's lifetime (this
    /// neither opens nor disposes it). The target's work area stays open on this path, so the DBC DEFAULTs
    /// of omitted columns evaluate in place (see <see cref="FillDbcDefaults"/>).
    /// </summary>
    internal static void AppendInsertValuesRow(VfpSession session, DbfWriter writer, InsertStatement st)
    {
        var columns = writer.Schema.Columns;
        var values = EvaluateValues(session, st);
        if (st.Columns is null) { AppendAllColumns(writer, columns, values); return; }
        var map = MapValues(st, columns, values);
        EvaluateDbcDefaults(session, st.Table, columns, map);
        writer.AppendRecord(map, OmittedColumnsBlankMask(session, st.Table, columns, map, honourSetNull: true));
    }

    /// <summary>Evaluate every VALUES expression to a CONSTANT against <see cref="ConstantRow.Instance"/>
    /// through <paramref name="session"/>'s SQL evaluation context: a bare field reference throws, while
    /// constant functions (DATE() / STR() …) evaluate fine.</summary>
    private static object?[] EvaluateValues(VfpSession session, InsertStatement st)
    {
        var ctx = session.SqlContext();
        var values = new object?[st.Values.Count];
        for (int i = 0; i < st.Values.Count; i++)
            values[i] = SelectExecutor.ToClr(session.InsertValueEvaluator is { } evaluate
                ? evaluate(st.Values[i]) : st.Values[i].Evaluate(ConstantRow.Instance, ctx));
        return values;
    }

    /// <summary>An explicit column list's column name → value map (omitted columns follow DEFAULT / SET NULL),
    /// validating the count and every name.</summary>
    private static Dictionary<string, object?> MapValues(InsertStatement st, IReadOnlyList<DbfColumn> columns, object?[] values)
    {
        var cols = st.Columns!;
        if (cols.Count != values.Length)
            // VFP9 distinguishes too few values (94) from too many (1230) for an explicit column list.
            throw new FoxDbfSqlException(
                $"INSERT column/value count mismatch: {cols.Count} column(s) but {values.Length} value(s).")
                { VfpErrorNumber = values.Length < cols.Count ? 94 : 1230 };

        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cols.Count; i++)
        {
            int index = IndexOfColumn(columns, cols[i]);
            if (index < 0)
                // VFP treats an unknown name in the INSERT column-list as an undefined VARIABLE, not an
                // SQL column: err 12 "Variable '...' is not found." (oracle-pinned against the VFP9
                // runtime, project-review 5.3). Typed so the interpreter's ErrorNumberOf carries the
                // exact number WITHOUT the last-resort "does not exist" text heuristic (which would
                // mis-map it to err 1 "File does not exist").
                throw new FoxDbfSqlException($"Column '{cols[i]}' does not exist in table '{st.Table}'.")
                    { VfpErrorNumber = 12 };
            // Keyed by the schema's name, so the omitted-column checks see the column as supplied however the
            // list spelled it.
            map[columns[index].Name] = values[i];
        }
        return map;
    }

    /// <summary>No column list: values map to the physical field order; VFP requires one per field.</summary>
    private static void AppendAllColumns(DbfWriter writer, IReadOnlyList<DbfColumn> columns, object?[] values)
    {
        if (values.Length != columns.Count)
            throw new FoxDbfSqlException(
                $"INSERT value count {values.Length} does not match the table's {columns.Count} column(s) (no column list given).");
        writer.AppendRecord(values);
    }

    // ---- INSERT ... FROM ARRAY | FROM MEMVAR ----------------------------------------------

    /// <summary><c>INSERT INTO tbl FROM ARRAY arr</c>: one appended row per array row (a 1-D array is a single
    /// row), each mapped to the table's fields by POSITION — excess array columns ignored, fields the row does
    /// not reach left blank, even a DBC field with a DEFAULT (VFP-pinned). Returns the appended-row count as
    /// _TALLY.</summary>
    private SqlResult InsertFromArray(InsertStatement st)
    {
        var bridge = _session.MemoryBridge
            ?? throw new FoxDbfSqlException(
                "INSERT … FROM ARRAY requires the microVFP memory store; none is bound to this session.");
        var rows = bridge.ReadArrayRows(st.SourceName!)
            ?? throw new FoxDbfSqlException($"INSERT … FROM ARRAY: '{st.SourceName}' is not a memory array.")
                { VfpErrorNumber = 12 };

        using var target = _session.OpenWritableTarget(st.Table);
        var writer = target.Writer;
        var columns = writer.Schema.Columns;

        int count = 0;
        foreach (var row in rows)
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            int k = Math.Min(row.Count, columns.Count);   // excess elements ignored; short row → blanks.
            for (int i = 0; i < k; i++) map[columns[i].Name] = row[i];
            writer.AppendRecord(map, OmittedColumnsBlankMask(_session, st.Table, columns, map, honourSetNull: false));
            count++;
        }
        return SqlResult.Dml(count);
    }

    /// <summary><c>INSERT INTO tbl FROM MEMVAR</c>: a single row whose fields map by NAME from the same-named
    /// <c>m.&lt;field&gt;</c> memvars; a field with no matching memvar is an omitted column — its DBC DEFAULT
    /// under the microVFP interpreter, else blank under <c>SET NULL OFF</c>, <c>.NULL.</c> / error 1581 under
    /// <c>SET NULL ON</c> (VFP-pinned).</summary>
    private SqlResult InsertFromMemvar(InsertStatement st)
    {
        var bridge = _session.MemoryBridge
            ?? throw new FoxDbfSqlException(
                "INSERT … FROM MEMVAR requires the microVFP memory store; none is bound to this session.");

        var target = _session.OpenWritableTarget(st.Table);
        try
        {
            var columns = target.Writer.Schema.Columns;
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in columns)
                if (bridge.TryReadMemvar(col.Name, out var v)) map[col.Name] = v;
            FillDbcDefaults(_session, st.Table, ref target, map);
            target.Writer.AppendRecord(map, OmittedColumnsBlankMask(_session, st.Table, columns, map, honourSetNull: true));
        }
        finally { target.Dispose(); }
        return SqlResult.Dml(1);
    }

    // ---- omitted INSERT columns -----------------------------------------------------------

    /// <summary>
    /// The positional blank mask for an INSERT row that does not supply every column (the keys of
    /// <paramref name="provided"/> are the supplied ones), per VFP9 (oracle-pinned by
    /// SqlInsertOmittedColumnsVfp9OracleTests, free tables and DBC members alike):
    /// <list type="bullet">
    /// <item><description><c>SET NULL OFF</c> (the default), or <paramref name="honourSetNull"/> false
    /// (<c>FROM ARRAY</c> with a short row): every omitted column is written BLANK — a NULL-able one too,
    /// with its null bit clear.</description></item>
    /// <item><description><c>SET NULL ON</c>: an omitted NULL-able column is <c>.NULL.</c>; an omitted
    /// NOT NULL column raises error 1581 before anything is written — except an AUTOINC column (the writer
    /// assigns it) and a DBC member's column with a DEFAULT. Under the microVFP interpreter
    /// <see cref="FillDbcDefaults"/> has already filled such a column, so it is not omitted here; on the raw
    /// SQL / ADO.NET path, which does not evaluate DEFAULTs, it is written blank.</description></item>
    /// </list>
    /// </summary>
    internal static bool[] OmittedColumnsBlankMask(VfpSession session, string table, IReadOnlyList<DbfColumn> columns,
        IReadOnlyDictionary<string, object?> provided, bool honourSetNull)
    {
        // Session.Context, not SqlContext(): the microVFP interpreter's SET NULL writes Context.NullSetting.
        bool nullOn = honourSetNull && !session.InsertArrayRow && session.Context.NullSetting;
        var blank = new bool[columns.Count];
        DbcTableRules? rules = null;
        bool rulesRead = false;
        int field = -1;   // the column's position among the non-system columns (= its DBC Field record).
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.IsSystem) continue;
            field++;
            if (provided.ContainsKey(col.Name)) continue;
            if (!nullOn) { blank[i] = true; continue; }
            if (col.IsNullable || col.IsAutoIncrement) continue;

            if (!rulesRead) { rules = TableRules(session, table); rulesRead = true; }
            var fieldRules = rules is null ? null : FieldRules(rules, columns, col, field);
            if (!string.IsNullOrEmpty(fieldRules?.DefaultValue)) { blank[i] = true; continue; }

            throw new FoxDbfSqlException($"Field {col.Name.ToUpperInvariant()} does not accept null values.")
                { VfpErrorNumber = 1581 };
        }
        return blank;
    }

    /// <summary>The DBC rules of <paramref name="table"/> when it is a member of the session's open database,
    /// else <see langword="null"/> (a free table).</summary>
    private static DbcTableRules? TableRules(VfpSession session, string table, string? sourcePath = null)
    {
        if (session.Database is not { } database) return null;
        // An alias can even shadow a different DBC member's name: its file determines the defaults.
        sourcePath ??= session.FindAreaByAlias(table)?.Table.SourcePath;
        if (sourcePath is not null)
        {
            if (session.TxLivePath is { } toLive) sourcePath = toLive(sourcePath);
            foreach (string member in database.TableNames)
                if (string.Equals(database.GetTablePath(member), sourcePath, StringComparison.OrdinalIgnoreCase))
                    return database.GetTableRules(member);
            return null;
        }
        return database.TryGetTableRules(table, out var rules) ? rules : null;
    }

    /// <summary>The DBC Field record of <paramref name="col"/>, the <paramref name="field"/>-th non-system
    /// column: by position when the DBC lists one Field per column (a long field name need not match the
    /// truncated .dbf name), else by name.</summary>
    private static DbcFieldRules? FieldRules(DbcTableRules rules, IReadOnlyList<DbfColumn> columns, DbfColumn col, int field)
        => field < rules.Fields.Count && rules.Fields.Count == CountUserColumns(columns)
            ? rules.Fields[field]
            : rules.Field(col.Name);

    /// <summary>
    /// Fill the columns an INSERT row omits (not keys of <paramref name="map"/>) from their DBC field
    /// <c>DEFAULT</c>, whatever <c>SET NULL</c> says, as VFP9 does (oracle: SqlInsertOmittedColumnsVfp9OracleTests;
    /// <c>C C(3) NOT NULL DEFAULT 'zz'</c> and <c>N N(4) NULL DEFAULT 7</c> come out <c>'zz'</c> / 7 under
    /// <c>SET NULL OFF</c> and <c>ON</c>). The DEFAULTs run once per row, in field order, after the VALUES,
    /// through the session's <see cref="VfpSession.DbcDefaultEvaluator"/> — only the microVFP interpreter sets
    /// it, so the raw SQL / ADO.NET path leaves the row as given. VFP evaluates them with the target table as
    /// the current work area (a UDF such as <c>newid()</c> / <c>createid()</c> reads <c>ALIAS()</c> and
    /// <c>RECCOUNT()</c>), and <paramref name="target"/> has released that area for the write, so the write is
    /// closed while they run (restoring the area) and reopened after. The interpreter exposes the pending
    /// row during evaluation without writing it. A DEFAULT that errors propagates and nothing is written.
    /// </summary>
    private static void FillDbcDefaults(VfpSession session, string table, ref VfpSession.WritableTarget target,
        Dictionary<string, object?> map)
    {
        var columns = target.Writer.Schema.Columns;
        if (PendingDbcDefaults(session, table, columns, map, target.SourcePath) is not { } pending) return;
        target.Dispose();
        session.DbcDefaultEvaluator!(table, pending, map);
        target = session.OpenWritableTarget(table);
    }

    /// <summary>Shared field-order evaluation for direct and buffered INSERTs, before either appends a row.</summary>
    internal static void EvaluateDbcDefaults(VfpSession session, string table, IReadOnlyList<DbfColumn> columns,
        Dictionary<string, object?> map, bool honourSetNull = true)
    {
        if (PendingDbcDefaults(session, table, columns, map, honourSetNull: honourSetNull) is { } pending)
            session.DbcDefaultEvaluator!(table, pending, map);
    }

    /// <summary>The omitted columns of <paramref name="map"/>'s row that have a DBC DEFAULT, when the session
    /// evaluates DEFAULTs (<see cref="VfpSession.DbcDefaultEvaluator"/> set) and <paramref name="table"/> is a
    /// member of its open database; else <see langword="null"/>. VFP9 walks the fields in order and, under
    /// <c>SET NULL ON</c>, stops at the first omitted NOT NULL field without a DEFAULT (error 1581, raised by
    /// <see cref="OmittedColumnsBlankMask"/>), so the DEFAULTs after it never run (probed with a counting UDF).</summary>
    private static List<(string Field, string Expression)>? PendingDbcDefaults(VfpSession session, string table, IReadOnlyList<DbfColumn> columns,
        Dictionary<string, object?> map, string? sourcePath = null, bool honourSetNull = true)
    {
        if (session.InsertArrayRow || session.DbcDefaultEvaluator is null || DbcDefaultExpressions(session, table, columns, sourcePath) is not { } defaults)
            return null;
        bool nullOn = honourSetNull && session.Context.NullSetting;
        List<(string Field, string Expression)>? pending = null;
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.IsSystem || map.ContainsKey(col.Name)) continue;
            if (defaults[i] is not { } expression)
            {
                if (nullOn && !col.IsNullable && !col.IsAutoIncrement) break;
                continue;
            }
            (pending ??= new()).Add((col.Name, expression));
        }
        return pending;
    }

    /// <summary>The DBC field <c>DEFAULT</c> expression of each of <paramref name="columns"/> (null where it has
    /// none), or <see langword="null"/> when <paramref name="table"/> is not a member of the session's open
    /// database or no field has a DEFAULT.</summary>
    internal static string?[]? DbcDefaultExpressions(VfpSession session, string table, IReadOnlyList<DbfColumn> columns,
        string? sourcePath = null)
    {
        if (TableRules(session, table, sourcePath) is not { } rules) return null;
        string?[]? defaults = null;
        int field = -1;
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.IsSystem) continue;
            field++;
            if (FieldRules(rules, columns, col, field)?.DefaultValue is { Length: > 0 } expression)
                (defaults ??= new string?[columns.Count])[i] = expression;
        }
        return defaults;
    }

    private static int CountUserColumns(IReadOnlyList<DbfColumn> columns)
    {
        int n = 0;
        foreach (var c in columns) if (!c.IsSystem) n++;
        return n;
    }

    // ---- UPDATE ---------------------------------------------------------------------------

    private SqlResult Update(UpdateStatement st)
    {
        var ctx = _session.SqlContext();
        using var target = _session.OpenWritableTarget(st.Table);
        var writer = target.Writer;
        var table = target.Table;
        var columns = writer.Schema.Columns;

        // Resolve each SET column to a physical index up front (clear error for an unknown column).
        var assigns = new (int Index, CrossVault.FoxDbf.Expressions.VfpExpression Value)[st.Assignments.Count];
        var setColumns = new HashSet<int>();
        for (int i = 0; i < st.Assignments.Count; i++)
        {
            string assignmentTarget = st.Assignments[i].Column;
            string columnName = assignmentTarget;
            int dot = assignmentTarget.IndexOf('.');
            if (dot > 0 && dot == assignmentTarget.LastIndexOf('.'))
            {
                string qualifier = assignmentTarget[..dot];
                if (!string.Equals(qualifier, st.Table, StringComparison.OrdinalIgnoreCase))
                    throw new FoxDbfSqlException(
                        $"SQL: Invalid SET expression in UPDATE, '{qualifier}' is not a target table.")
                        { VfpErrorNumber = 2149 };
                columnName = assignmentTarget[(dot + 1)..];
            }

            int idx = IndexOfColumn(columns, columnName);
            if (idx < 0)
                // An unknown SET column is the SQL-column class: err 1806 "SQL: Column '...' is not found."
                // (oracle-pinned against the VFP9 runtime, project-review 5.3 — same number SELECT reports
                // for an unknown projected column). Typed so ErrorNumberOf carries the exact number WITHOUT
                // the last-resort "does not exist" text heuristic (which would mis-map it to err 1).
                throw new FoxDbfSqlException(
                    $"Column '{st.Assignments[i].Column}' does not exist in table '{st.Table}'.")
                    { VfpErrorNumber = 1806 };
            assigns[i] = (idx, st.Assignments[i].Value);
            setColumns.Add(idx);
        }

        var binder = TargetBinder(st.Table, columns);
        binder.Check(st.Where);
        foreach (var (_, val) in assigns) binder.Check(val);

        // §D3: ONE whole-file lock spans candidate selection AND every write, so a concurrent shared
        // writer cannot slip a change between the match and the recno-targeted write (lost update).
        // No-op when the target was opened EXCLUSIVE; each per-record UpdateRecord/Delete re-enters
        // the held lock (WithLock's covered-range guard) rather than re-locking.
        using var lockScope = writer.LockFileScope();

        var matched = MatchedRecords(target, st.Table, st.Where?.Text, ctx);
        foreach (var (recNo, rec) in matched)
        {
            // Rewrite the FULL row: seed every field from the record's CURRENT value, then apply each
            // SET expression evaluated against THIS row (so amount = amount + 1 reads its own value).
            // EXCEPTION: FPT-backed columns (M/W/G/P) the SET list does not touch carry the KeepValue
            // sentinel so their existing .fpt block pointer is preserved verbatim — round-tripping
            // their read-back value (decoded memo content, or a raw G/P pointer) back through the
            // memo-append path would grow the .fpt unboundedly and corrupt General/Picture fields.
            var positional = new object?[columns.Count];
            for (int i = 0; i < columns.Count; i++)
                positional[i] = columns[i].Type is 'M' or 'W' or 'G' or 'P' && !setColumns.Contains(i)
                    ? DbfWriter.KeepValue
                    : rec.GetExpressionValue(i);   // full width: an untouched C field keeps its leading blanks

            var rowCtx = new RecordRow(rec, recNo, table.RecordCount);
            foreach (var (idx, val) in assigns)
                positional[idx] = SelectExecutor.ToClr(val.Evaluate(rowCtx, ctx));

            writer.UpdateRecord(recNo - 1, positional);
        }
        return SqlResult.Dml(matched.Count);
    }

    // ---- DELETE ---------------------------------------------------------------------------

    private SqlResult Delete(DeleteStatement st)
    {
        var ctx = _session.SqlContext();
        using var target = _session.OpenWritableTarget(st.Table);
        var writer = target.Writer;

        TargetBinder(st.Table, writer.Schema.Columns).Check(st.Where);

        // §D3: ONE whole-file lock spans candidate selection AND the deletes so they are atomic with
        // respect to other shared writers (no-op under EXCLUSIVE).
        using var lockScope = writer.LockFileScope();

        // VFP soft delete: set the deletion flag only — never a pack.
        var matched = MatchedRecords(target, st.Table, st.Where?.Text, ctx);
        foreach (var (recNo, _) in matched)
            writer.Delete(recNo - 1);
        return SqlResult.Dml(matched.Count);
    }

    // ---- candidate-set helpers ------------------------------------------------------------

    /// <summary>Bind-time check of the names in an UPDATE/DELETE: only the target itself is a source
    /// (VFP9: an unknown alias in WHERE or SET is error 13, an unknown column — qualified or bare — 1806),
    /// so neither can read null any more and silently match nothing / write .NULL.. The check runs against
    /// the WRITER's columns, the names the WHERE/SET row adapters actually resolve: for a DBC member those
    /// are its long field names (<see cref="VfpSession.OpenWritableTarget"/>), so — as in VFP9 — the long
    /// name works and the 10-char physical name of a longer field is unknown (1806).</summary>
    private QualifierBinder TargetBinder(string table, IReadOnlyList<DbfColumn> columns)
        => new(_session,
            new[] { new QualifierBinder.Source(table, table,
                new HashSet<string>(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase)) },
            null, hostBound: false, checkBare: true);

    /// <summary>The records (1-based recno + the record) matching <paramref name="whereText"/> — no
    /// WHERE means ALL rows (<c>.T.</c>). <c>SET DELETED ON</c> excludes already-deleted rows via the
    /// evaluation context, mirroring the SELECT path. Records are returned in ascending recno order,
    /// read from the writable target's live view so just-written state is seen.</summary>
    private List<(int RecNo, DbfRecord Rec)> MatchedRecords(
        VfpSession.WritableTarget target, string targetAlias, string? whereText, EvaluationContext ctx)
    {
        var table = target.Table;
        // Select and materialize under the caller's file lock, then close the index BEFORE any
        // writes. Moving a key must not revisit a row or invalidate a live index traversal (VFP9).
        using var cdx = target.OpenCdx();
        // Drop the target's own qualifier (table.field / openAlias.field) — the optimizer's residual
        // resolves bare field names only and would otherwise read null and match nothing.
        string filter = string.IsNullOrWhiteSpace(whereText)
            ? ".T."
            : SelectExecutor.StripDrivingQualifier(whereText!, targetAlias);
        var qr = _session.FindRecords(table, cdx, filter, ctx);
        var hits = new HashSet<int>(qr.RecordNumbers);

        var list = new List<(int, DbfRecord)>(hits.Count);
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            if (hits.Contains(recno)) list.Add((recno, rec));
        }
        return list;
    }

    private static int IndexOfColumn(IReadOnlyList<DbfColumn> cols, string name)
    {
        for (int i = 0; i < cols.Count; i++)
            if (string.Equals(cols[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // ---- per-record row state (SET / WHERE evaluation) ------------------------------------

    /// <summary>Adapts a <see cref="DbfRecord"/> to <see cref="IRowContext"/> so a SET expression
    /// reads THIS row's own current values (e.g. <c>amount = amount + 1</c>); character fields at full width, so
    /// <c>name = name + 'x'</c> overflows the field exactly as in VFP.</summary>
    private sealed class RecordRow : IRowContext
    {
        private readonly DbfRecord _rec;
        public RecordRow(DbfRecord rec, int recNo, int recCount)
        {
            _rec = rec; RecNo = recNo; RecCount = recCount;
        }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name)
        {
            int dot = name.LastIndexOf('.');
            string field = dot >= 0 ? name[(dot + 1)..] : name;
            return _rec.GetExpressionValue(field);
        }
    }

    /// <summary>The row context used to evaluate a constant INSERT VALUES expression: it has NO row,
    /// so a bare field reference is an error (it throws), while constant functions like
    /// <c>DATE()</c> / <c>STR()</c> evaluate fine.</summary>
    private sealed class ConstantRow : IRowContext
    {
        public static readonly ConstantRow Instance = new();
        public int RecNo => 0;
        public int RecCount => 0;
        public bool Deleted => false;
        public object? GetField(string name)
            => throw new FoxDbfSqlException(
                $"Field reference '{name}' is not allowed in an INSERT ... VALUES expression (no row context).");
    }
}
