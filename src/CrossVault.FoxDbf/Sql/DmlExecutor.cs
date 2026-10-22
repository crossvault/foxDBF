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
/// set; INSERT is single-row VALUES only; <c>=</c> in WHERE follows SQL/ANSI semantics like SELECT.
/// </para>
/// <para>
/// Each handler resolves the target table via <see cref="VfpSession.OpenWritableTarget"/> (reusing an
/// open work area's file, else auto-opening SHARED), evaluates VALUES / SET via
/// <see cref="CrossVault.FoxDbf.Expressions.VfpExpression.Evaluate"/>, drives
/// <see cref="QueryOptimizer.FindRecords"/> for the WHERE candidate set, and writes through the
/// <see cref="DbfWriter"/>. Affected-record counts come back via <see cref="SqlResult.Dml"/>.
/// </para>
/// </summary>
internal sealed class DmlExecutor
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
        // Memory-source forms (VFP idioms heavily used in real SPs) map through the ACTIVE session's memvar
        // store via the bound interpreter's bridge, then append through the SAME writer path as VALUES — so
        // index maintenance, the COW transaction seam and buffering all apply unchanged.
        if (st.SourceKind == InsertSourceKind.Array) return InsertFromArray(st);
        if (st.SourceKind == InsertSourceKind.Memvar) return InsertFromMemvar(st);

        using var target = _session.OpenWritableTarget(st.Table);
        AppendInsertValuesRow(_session, target.Writer, st);
        return SqlResult.Dml(1);
    }

    /// <summary>
    /// Evaluate an <c>INSERT … VALUES</c> row and append it through <paramref name="writer"/> — the shared
    /// core of the single-row VALUES insert, extracted so the microVFP interpreter's direct-append fast path
    /// (Batch 4) appends through its OWN cached writer with byte-identical value semantics AND identical error
    /// numbers/text (err 12 for an unknown column, the column/value count mismatch) as this ADO.NET path. The
    /// caller owns the writer's lifetime (this neither opens nor disposes it); the ADO.NET path passes the
    /// <see cref="VfpSession.OpenWritableTarget"/> writer, the interpreter passes its persistent cached writer.
    /// Values are evaluated against <see cref="ConstantRow.Instance"/> (a bare field reference throws — VALUES
    /// are constant) through <paramref name="session"/>'s SQL evaluation context, exactly as before.
    /// </summary>
    internal static void AppendInsertValuesRow(VfpSession session, DbfWriter writer, InsertStatement st)
    {
        var ctx = session.SqlContext();
        var columns = writer.Schema.Columns;

        // Evaluate every VALUES expression to a CONSTANT: ConstantRow has no row, so a bare field
        // reference throws, while constant functions (DATE() / STR() …) evaluate fine.
        var values = new object?[st.Values.Count];
        for (int i = 0; i < st.Values.Count; i++)
            values[i] = SelectExecutor.ToClr(st.Values[i].Evaluate(ConstantRow.Instance, ctx));

        if (st.Columns is { } cols)
        {
            // Explicit column list: map name → value (missing columns stay blank / NULL); validate count.
            if (cols.Count != values.Length)
                throw new FoxDbfSqlException(
                    $"INSERT column/value count mismatch: {cols.Count} column(s) but {values.Length} value(s).");

            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cols.Count; i++)
            {
                if (IndexOfColumn(columns, cols[i]) < 0)
                    // VFP treats an unknown name in the INSERT column-list as an undefined VARIABLE, not an
                    // SQL column: err 12 "Variable '...' is not found." (oracle-pinned against the VFP9
                    // runtime, project-review 5.3). Typed so the interpreter's ErrorNumberOf carries the
                    // exact number WITHOUT the last-resort "does not exist" text heuristic (which would
                    // mis-map it to err 1 "File does not exist").
                    throw new FoxDbfSqlException($"Column '{cols[i]}' does not exist in table '{st.Table}'.")
                        { VfpErrorNumber = 12 };
                map[cols[i]] = values[i];
            }
            writer.AppendRecord(map);
        }
        else
        {
            // No column list: values map to the physical field order; VFP requires one per field.
            if (values.Length != columns.Count)
                throw new FoxDbfSqlException(
                    $"INSERT value count {values.Length} does not match the table's {columns.Count} column(s) (no column list given).");
            writer.AppendRecord(values);
        }
    }

    // ---- INSERT ... FROM ARRAY | FROM MEMVAR ----------------------------------------------

    /// <summary><c>INSERT INTO tbl FROM ARRAY arr</c>: one appended row per array row (a 1-D array is a single
    /// row), each mapped to the table's fields by POSITION — excess array columns ignored, fields the row does
    /// not reach left blank (VFP-pinned). Returns the appended-row count as _TALLY.</summary>
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
            writer.AppendRecord(map);
            count++;
        }
        return SqlResult.Dml(count);
    }

    /// <summary><c>INSERT INTO tbl FROM MEMVAR</c>: a single row whose fields map by NAME from the same-named
    /// <c>m.&lt;field&gt;</c> memvars; a field with no matching memvar is left blank (VFP-pinned).</summary>
    private SqlResult InsertFromMemvar(InsertStatement st)
    {
        var bridge = _session.MemoryBridge
            ?? throw new FoxDbfSqlException(
                "INSERT … FROM MEMVAR requires the microVFP memory store; none is bound to this session.");

        using var target = _session.OpenWritableTarget(st.Table);
        var writer = target.Writer;
        var columns = writer.Schema.Columns;

        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
            if (bridge.TryReadMemvar(col.Name, out var v)) map[col.Name] = v;
        writer.AppendRecord(map);
        return SqlResult.Dml(1);
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

        // §D3: ONE whole-file lock spans candidate selection AND every write, so a concurrent shared
        // writer cannot slip a change between the match and the recno-targeted write (lost update).
        // No-op when the target was opened EXCLUSIVE; each per-record UpdateRecord/Delete re-enters
        // the held lock (WithLock's covered-range guard) rather than re-locking.
        using var lockScope = writer.LockFileScope();

        var matched = MatchedRecords(table, st.Table, st.Where?.Text, ctx);
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
                    : rec[columns[i].Name];

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

        // §D3: ONE whole-file lock spans candidate selection AND the deletes so they are atomic with
        // respect to other shared writers (no-op under EXCLUSIVE).
        using var lockScope = writer.LockFileScope();

        // VFP soft delete: set the deletion flag only — never a pack.
        var matched = MatchedRecords(target.Table, st.Table, st.Where?.Text, ctx);
        foreach (var (recNo, _) in matched)
            writer.Delete(recNo - 1);
        return SqlResult.Dml(matched.Count);
    }

    // ---- candidate-set helpers ------------------------------------------------------------

    /// <summary>The records (1-based recno + the record) matching <paramref name="whereText"/> — no
    /// WHERE means ALL rows (<c>.T.</c>). <c>SET DELETED ON</c> excludes already-deleted rows via the
    /// evaluation context, mirroring the SELECT path. Records are returned in ascending recno order,
    /// read from the writable target's live view so just-written state is seen.</summary>
    private List<(int RecNo, DbfRecord Rec)> MatchedRecords(
        DbfTable table, string targetAlias, string? whereText, EvaluationContext ctx)
    {
        // Drop the target's own qualifier (table.field / openAlias.field) — the optimizer's residual
        // resolves bare field names only and would otherwise read null and match nothing.
        string filter = string.IsNullOrWhiteSpace(whereText)
            ? ".T."
            : SelectExecutor.StripDrivingQualifier(whereText!, targetAlias);
        var qr = _session.FindRecords(table, null, filter, ctx);
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
    /// reads THIS row's own current values (e.g. <c>amount = amount + 1</c>).</summary>
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
            return _rec[field];
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
