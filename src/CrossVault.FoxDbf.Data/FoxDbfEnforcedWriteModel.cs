using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// The opt-in (<c>EnforceRules=on</c>) VFP write-model: an ADO.NET INSERT / UPDATE / DELETE runs through the
/// DBC's DEFAULT / RULE / TRIGGER chain via the shared <see cref="VfpInterpreter"/>, exactly as VFP9 enforces
/// it. The per-row order matches the authoritative model (analysis/MICROVFP_SEMANTICS.md "Nachtrag"):
/// <list type="bullet">
///   <item><b>INSERT</b>: field DEFAULTs (missing fields, may call UDFs) → field RULEs (set fields) →
///   record/table RULE → the bound <c>__RI_INSERT_*</c> trigger (RI), the whole row op atomic.</item>
///   <item><b>UPDATE</b>: field RULEs (changed fields) → record RULE → the bound <c>__RI_UPDATE_*</c>
///   trigger (key-change cascade / restrict), atomic.</item>
///   <item><b>DELETE</b>: the bound <c>__RI_DELETE_*</c> trigger (cascade / restrict), atomic.</item>
/// </list>
/// DEFAULT / RULE evaluation and the trigger cascade all go through microVFP (UDF-capable). A rule failure
/// raises a <see cref="FoxDbfException"/> carrying the rule's error text; a trigger returning <c>.F.</c>
/// aborts the row op (and its cascade) and leaves the data unchanged.
/// </summary>
internal sealed class FoxDbfEnforcedWriteModel
{
    private readonly FoxDbfConnection _connection;
    private readonly VfpInterpreter _interp;

    internal FoxDbfEnforcedWriteModel(FoxDbfConnection connection, VfpInterpreter interp)
    {
        _connection = connection;
        _interp = interp;
        _interp.EnforceReferentialIntegrity = true;   // the bound RI triggers must auto-fire (== VFP9).
    }

    /// <summary>Run the parsed write statement <paramref name="sql"/> through the VFP write-model.
    /// Returns the affected-record count (best effort; -1 when not tracked).</summary>
    internal int Execute(string sql)
    {
        try { return ExecuteCore(sql); }
        catch (Exception ex) when (ex is not FoxDbfException && ex is IVfpErrorCode or ExpressionException)
        {
            // Rules and RI failures originate in microVFP; keep the provider's DbException contract.
            // The independent expression assembly exposes its error number without IVfpErrorCode.
            int? number = ex is IVfpErrorCode coded ? coded.VfpErrorNumber : ((ExpressionException)ex).VfpErrorNumber;
            throw number is { } code
                ? new FoxDbfException(ex.Message, code, ex) : new FoxDbfException(ex.Message, ex);
        }
    }

    private int ExecuteCore(string sql)
    {
        var stmt = SqlParser.Parse(sql);
        // INSERT … FROM ARRAY | MEMVAR: expand into concrete per-row VALUES inserts (reading the active
        // session's memvar store) so each row flows through the SAME DEFAULT/RULE/TRIGGER enforcement below.
        if (stmt is InsertStatement { SourceKind: InsertSourceKind.Select })
        {
            // The interpreter binds DEFAULT/UDF evaluation and the same per-row trigger
            // route used by VALUES; the ordinary SELECT import still uses the core batch.
            _interp.Execute(sql);
            return _interp.Memory.Get("_TALLY").AsInteger;
        }
        if (stmt is InsertStatement { SourceKind: not InsertSourceKind.Values } insFrom)
            return InsertFrom(insFrom);
        return stmt switch
        {
            InsertStatement ins => Insert(ins),
            UpdateStatement upd => Update(upd),
            DeleteStatement del => Delete(del),
            // Not a writing DML after all (shouldn't happen — IsWriteDml gated it): fall back to the raw path.
            _ => _connection.Session.Execute(sql)?.AffectedRecords ?? -1,
        };
    }

    // ─────────────────────────── INSERT … FROM ARRAY | MEMVAR (enforced) ───────────────────────────

    /// <summary>Expand an <c>INSERT … FROM ARRAY | MEMVAR</c> into concrete per-row VALUES inserts and run each
    /// through <see cref="Insert"/>, preserving blank unreached fields for FROM ARRAY and DEFAULTs for MEMVAR;
    /// field/record RULEs and the bound RI trigger all fire exactly as for the VALUES form. FROM ARRAY appends
    /// one VALUES insert per array row (position-mapped; excess ignored); FROM MEMVAR one insert whose columns
    /// are the fields that have a matching <c>m.&lt;field&gt;</c> memvar (missing fields fall to their DEFAULT).</summary>
    private int InsertFrom(InsertStatement ins)
    {
        var bridge = _connection.Session.MemoryBridge
            ?? throw new FoxDbfException("INSERT … FROM ARRAY/MEMVAR requires the microVFP memory store.");
        var fields = FieldNames(ins.Table);
        int total = 0;

        if (ins.SourceKind == InsertSourceKind.Memvar)
        {
            var cols = new List<string>();
            var vals = new List<VfpValue>();
            foreach (var f in fields)
                if (bridge.TryReadMemvar(f, out var v)) { cols.Add(f); vals.Add(VfpValue.FromClr(v)); }
            total += Insert(BuildValuesInsert(ins.Table, cols, vals));
        }
        else // Array
        {
            var rows = bridge.ReadArrayRows(ins.SourceName!)
                ?? throw new FoxDbfException($"INSERT … FROM ARRAY: '{ins.SourceName}' is not a memory array.");
            foreach (var row in rows)
            {
                int k = Math.Min(row.Count, fields.Count);   // VFP leaves a short array's tail blank, even with DEFAULTs.
                var cols = new List<string>(k);
                var vals = new List<VfpValue>(k);
                for (int i = 0; i < k; i++) { cols.Add(fields[i]); vals.Add(VfpValue.FromClr(row[i])); }
                total += Insert(BuildValuesInsert(ins.Table, cols, vals), arrayRow: true);
            }
        }
        return total;
    }

    /// <summary>A concrete <c>INSERT INTO table (cols) VALUES (literals)</c> statement whose VALUES are literal
    /// renderings of the already-resolved memvar/array values — the shape <see cref="Insert"/>
    /// enforces.</summary>
    private static InsertStatement BuildValuesInsert(string table, List<string> cols, List<VfpValue> vals)
    {
        var exprs = new List<VfpExpression>(vals.Count);
        foreach (var v in vals) exprs.Add(VfpExpression.Parse(ToVfpLiteral(v)));
        return new InsertStatement(null, table, cols, exprs);
    }

    /// <summary>Read DBC rules when the target is a member table. Free tables are not present in the DBC
    /// metadata and therefore use the physical-schema fallback in <see cref="FieldNames"/>.</summary>
    private DbcTableRules? TableRules(string table)
    {
        var database = _connection.Session.Database;
        if (database is null) return null;
        try { return database.GetTableRules(table); }
        catch (DbfFileNotFoundException) { return null; }
    }

    /// <summary>The target table's field names in physical order: the DBC rules metadata when present, else a
    /// zero-row schema SELECT.</summary>
    private List<string> FieldNames(string table)
    {
        var rules = TableRules(table);
        if (rules is not null && rules.Fields.Count > 0)
            return rules.Fields.Select(f => f.FieldName).ToList();
        var res = _connection.Session.Execute($"SELECT * FROM {table} WHERE 1 = 0");
        return res?.Columns is { } cols
            ? cols.Select(c => c.Name).ToList()
            : new List<string>();
    }

    // ─────────────────────────── INSERT: DEFAULT → field RULE → record RULE → trigger ───────────────────────────

    private int Insert(InsertStatement ins, bool arrayRow = false)
    {
        var rules = TableRules(ins.Table);

        // Column / value lists. An omitted column list means "all columns positionally" — recover the
        // physical field order from DBC rules when present, else from the target schema for free tables.
        var cols = new List<string>(
            ins.Columns ?? (rules?.Fields.Select(f => f.FieldName) ?? Enumerable.Empty<string>()));
        if (ins.Columns is null && cols.Count == 0 && ins.Values.Count > 0)
            cols = FieldNames(ins.Table);
        var valTexts = new List<string>(ins.Values.Select(v => v.Text));

        // Open the target so a DEFAULT UDF that reads the table state (e.g. createid()'s RECCOUNT())
        // evaluates in the right work area; harmless when the default is a constant / DATETIME().
        int area = EnsureWriteArea(ins.Table);
        _interp.Execute($"SELECT {area}");

        var provided = new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, VfpValue>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cols.Count && i < valTexts.Count; i++)
            candidates[cols[i]] = _interp.EvalExpression(valTexts[i]);

        // Re-inject each PROVIDED value as a literal of its already-evaluated result, so a side-effecting
        // value expression (e.g. createid() / a counter UDF in the VALUES list) fires EXACTLY ONCE — the
        // augmented INSERT below replays literals, never the source text a second time (== VFP9). DEFAULTs
        // are likewise injected as literals.
        for (int i = 0; i < cols.Count && i < valTexts.Count; i++)
            if (candidates.TryGetValue(cols[i], out var pv)) valTexts[i] = ToVfpLiteral(pv);

        // 1) Field DEFAULTs for the columns the INSERT omitted (each may call a UDF → microVFP).
        if (rules is not null)
        {
            foreach (var f in rules.Fields)
            {
                if (arrayRow || string.IsNullOrEmpty(f.DefaultValue) || provided.Contains(f.FieldName)) continue;
                var dv = _interp.EvalExpression(f.DefaultValue!);
                candidates[f.FieldName] = dv;
                cols.Add(f.FieldName);
                valTexts.Add(ToVfpLiteral(dv));
                provided.Add(f.FieldName);
            }

            // Field and record RULEs — a failure rejects the whole INSERT,
            //    BEFORE any row lands, so the table is left byte-for-byte unchanged (atomic).
            // Validation belongs to the interpreter and sees the converted, stored candidate.
        }

        // 4) the row lands + its bound __RI_INSERT_* trigger auto-fires atomically (orphan child → rollback).
        string augmented = $"INSERT INTO {ins.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", valTexts)})";
        _interp.LastDmlTriggerAborted = false;
        bool previousArrayRow = _connection.Session.InsertArrayRow;
        _connection.Session.InsertArrayRow = arrayRow;
        try { _interp.Execute(augmented); }
        finally { _connection.Session.InsertArrayRow = previousArrayRow; }
        ThrowIfTriggerAborted(ins.Table);   // a .F. INSERT trigger rolled the row back ⇒ VFP err 1539, not a phantom success.
        return 1;
    }

    // ─────────────────────────── UPDATE: field RULE → record RULE → update trigger (cascade) ───────────────────────────

    private int Update(UpdateStatement upd)
    {
        var recs = MatchingRecords(upd.Table, upd.Where?.Text);
        int area = EnsureWriteArea(upd.Table);

        foreach (int rec in recs)
        {
            // Position on the row, then evaluate each SET expression in ITS context (it may reference the
            // row's own fields, e.g. SET qty = qty + 1) BEFORE any SELECT that could move the current area.
            // Each assignment is evaluated EXACTLY ONCE and re-injected as a literal in the REPLACE (so a
            // side-effecting value UDF fires once, == VFP9 / mirrors Insert()).
            _interp.Execute($"SELECT {area}\nGO {rec}");
            var setLits = new List<string>(upd.Assignments.Count);
            foreach (var a in upd.Assignments)
            {
                var v = _interp.EvalExpression(a.Value.Text);
                setLits.Add($"{a.Column} WITH {ToVfpLiteral(v)}");
            }

            // 3) the matched-row REPLACE auto-fires the bound __RI_UPDATE_* trigger (key-change cascade /
            //    restrict, atomic) — exactly the proven GO/REPLACE auto-fire path. A .F. return rolled the
            //    parent key back ⇒ raise VFP err 1539 (don't report a phantom success); the multi-row loop
            //    stops on the first blocked row (rows before it already committed — matching VFP's behaviour).
            _interp.LastDmlTriggerAborted = false;
            _interp.Execute($"SELECT {area}\nGO {rec}\nREPLACE {string.Join(", ", setLits)}");
            ThrowIfTriggerAborted(upd.Table);
        }
        return recs.Count;
    }

    // ─────────────────────────── DELETE: delete trigger (cascade / restrict) ───────────────────────────

    private int Delete(DeleteStatement del)
    {
        var recs = MatchingRecords(del.Table, del.Where?.Text);
        int area = EnsureWriteArea(del.Table);
        foreach (int rec in recs)
        {
            // Each matched-row DELETE auto-fires the bound __RI_DELETE_* trigger (cascade to children, or a
            // RESTRICT abort that rolls the whole op back) — exactly the proven GO/DELETE auto-fire path. A
            // .F. return ⇒ raise VFP err 1539 (don't report a phantom success); the multi-row loop stops on
            // the first blocked row.
            _interp.LastDmlTriggerAborted = false;
            _interp.Execute($"SELECT {area}\nGO {rec}\nDELETE");
            ThrowIfTriggerAborted(del.Table);
        }
        return recs.Count;
    }

    /// <summary>The 1-based physical record numbers of <paramref name="table"/> whose live record satisfies
    /// the optional <paramref name="where"/> predicate — located through the SQL engine's <c>RECNO()</c> so
    /// the matching follows VFP's WHERE semantics EXACTLY (and stays byte-consistent with the raw DML path).
    /// Collected up front (before any mutation) so the subsequent per-row GO/DELETE|REPLACE — which logically
    /// deletes / rewrites in place and never renumbers — applies to a stable set.</summary>
    private List<int> MatchingRecords(string table, string? where)
    {
        string sql = where is null
            ? $"SELECT RECNO() AS __rn FROM {table}"
            : $"SELECT RECNO() AS __rn FROM {table} WHERE {where}";
        var result = _connection.Session.Execute(sql);
        var recs = new List<int>();
        if (result?.Rows is not null)
            foreach (var row in result.Rows)
                if (row.Length > 0 && row[0] is not null)
                    recs.Add(Convert.ToInt32(row[0]));
        recs.Sort();   // apply in ascending physical order (deterministic; numbers are stable across the op).
        return recs;
    }

    // ─────────────────────────── rule evaluation ───────────────────────────

    /// <summary>After a row op, raise VFP error 1539 when its bound trigger returned <c>.F.</c> (a RESTRICT
    /// abort / a propagated child-cascade restrict). The interpreter already rolled the row + its cascade
    /// back; this turns that silent rollback into the error the ADO.NET caller must see (no phantom success).</summary>
    private void ThrowIfTriggerAborted(string table)
    {
        if (!_interp.LastDmlTriggerAborted) return;
        _interp.LastDmlTriggerAborted = false;
        throw new FoxDbfException($"Trigger failed in {table}. (Visual FoxPro error 1539)", 1539); // VFP err 1539 trigger-.F. (oracle-pinned).
    }

    private int EnsureWriteArea(string table)
    {
        var area = _connection.Session.FindInsertArea(table);
        if (area is not null) return area.Area;
        _interp.Execute($"USE {table} IN 0");
        return _connection.Session.FindInsertArea(table)!.Area;
    }

    private void TryExecute(string prg)
    {
        try { _interp.Execute(prg); } catch { /* best-effort setup (USE/SELECT) — failures surface downstream */ }
    }

    // Render a microVFP value as a VFP literal for the augmented INSERT statement. Reuses the provider's
    // round-trip-safe quoting (delimiter the value does not contain / CHR()-concat fallback / typed forms).
    private static string ToVfpLiteral(VfpValue v) => FoxDbfCommand.ToVfpLiteral(v.ToClr());
}
