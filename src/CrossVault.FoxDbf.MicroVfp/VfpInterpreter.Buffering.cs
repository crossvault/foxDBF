using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>microVFP row/table buffering — TABLEUPDATE/TABLEREVERT/CURSORSETPROP/CURSORGETPROP, OLDVAL/CURVAL/GETFLDSTATE, and the TableBuffer model.</summary>
public sealed partial class VfpInterpreter
{
    // OLDVAL() per field. ALWAYS overwrites: each entry holds the value at the start of the CURRENT change
    // (captured by the REPLACE just before it writes the field). A pointer move clears the whole map
    // (GoTop/GoBottom/GoRecord/Skip), so per-record buffering is preserved; consecutive REPLACEs of the
    // SAME record each re-base from the value the previous one committed.
    private static void RememberOldVal(AreaMeta m, string field, object? value)
    {
        m.OldVals ??= new(StringComparer.OrdinalIgnoreCase);
        m.OldVals[field] = value;
    }

    // ─────────────────────────── buffered write routing (microVFP P1 gap #4) ───────────────────────────

    /// <summary>Buffer a REPLACE for the current record instead of writing through. For an EXISTING row the
    /// pre-change on-disk value is captured once (OLDVAL's buffer-start baseline); for a buffered APPENDED
    /// row the edit lands directly on the append entry (which starts as its own buffer state).</summary>
    private void BufferReplace(ReplaceStmt rp, int area, VfpSession.WorkArea wa, AreaMeta m)
    {
        int rcTable = wa.Table.RecordCount;
        int recno = m.RecNo;
        var buf = m.Buf ??= new TableBuffer();

        RowEdit? edit = null;
        Dictionary<int, object?> target;
        if (recno > rcTable)                                   // editing a buffered appended row.
        {
            int ai = recno - rcTable - 1;
            if (ai < 0 || ai >= buf.Appends.Count) { if (buf.IsEmpty) m.Buf = null; return; }
            target = buf.Appends[ai].Fields;
        }
        else
        {
            if (recno < 1 || recno > rcTable) { if (buf.IsEmpty) m.Buf = null; return; }
            if (!buf.Rows.TryGetValue(recno, out edit)) { edit = new RowEdit(); buf.Rows[recno] = edit; }
            target = edit.Fields;
        }

        foreach (var clause in rp.Clauses)
        {
            string field = StripQualifier(NameOf(clause.Field));
            int idx = ColumnIndex(wa.Table, field);
            if (idx < 0) continue;
            if (edit is not null && !edit.Old.ContainsKey(field))
                edit.Old[field] = ReadFieldOnDisk(wa, field);   // capture the buffer-start (on-disk) value once.
            var v = Eval(clause.Value);
            var col = wa.Table.Columns[idx];
            target[idx] = v.IsNull ? null : (col.Type is 'C' or 'M' or 'V' ? FitToField(col, v.AsString) : v.ToClr());
        }
    }

    /// <summary>Buffer a DELETE/RECALL mark for the current record (existing row or buffered append).</summary>
    private void BufferDeleteFlag(int area, VfpSession.WorkArea wa, AreaMeta m, bool deleted)
    {
        int rcTable = wa.Table.RecordCount;
        int recno = m.RecNo;
        var buf = m.Buf ??= new TableBuffer();
        if (recno > rcTable)
        {
            int ai = recno - rcTable - 1;
            if (ai >= 0 && ai < buf.Appends.Count) buf.Appends[ai].Deleted = deleted;
        }
        else if (recno >= 1 && recno <= rcTable)
        {
            if (!buf.Rows.TryGetValue(recno, out var e)) { e = new RowEdit(); buf.Rows[recno] = e; }
            e.DeletedOverride = deleted;
        }
        if (buf.IsEmpty) m.Buf = null;
    }

    /// <summary>Buffer an INSERT (append) into the target area's buffer: the row is VISIBLE on the live
    /// cursor immediately (its field values are evaluated now) but is not written to disk until TABLEUPDATE
    /// replays the statement through the ordinary INSERT path (so RI fires deferred).</summary>
    private void BufferAppend(VfpSession.WorkArea wa, AreaMeta m, InsertStmt ins, InsertStatement stmt)
    {
        var ae = new AppendEntry { Stmt = ins };
        var cols = stmt.Columns;
        for (int i = 0; i < stmt.Values.Count; i++)
        {
            int idx = cols is not null && i < cols.Count ? ColumnIndex(wa.Table, cols[i]) : i;
            if (idx < 0 || idx >= wa.Table.Columns.Count) continue;
            var v = ParseExpressionCached(MicroVfpExprRewrite.Normalize(stmt.Values[i].Text)).Evaluate(_row, _ctx);
            var col = wa.Table.Columns[idx];
            ae.Fields[idx] = v.IsNull ? null : (col.Type is 'C' or 'M' or 'V' ? FitToField(col, v.AsString) : v.ToClr());
        }
        // The columns the INSERT omits follow SET NULL as it is NOW, at INSERT time — as the write-through
        // INSERT does: blank under OFF (left out of Fields, written blank at commit), .NULL. in a NULL-able
        // column under ON (an explicit null in Fields). An omitted NOT NULL column under ON is VFP error
        // 1581 and no row, before any later field DEFAULT is evaluated.
        // (A buffered FROM MEMVAR / FROM ARRAY reads none of its values here, so it does not apply SET NULL.)
        var columns = wa.Table.Columns;
        var provided = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (idx, v) in ae.Fields) provided[columns[idx].Name] = v;
        // DEFAULTs run once, in field order, before the append becomes visible in the buffer.
        if (stmt.SourceKind == InsertSourceKind.Values)
        {
            DmlExecutor.EvaluateDbcDefaults(Session, stmt.Table, columns, provided);
            for (int i = 0; i < columns.Count; i++)
                if (provided.TryGetValue(columns[i].Name, out var value)) ae.Fields[i] = value;
        }
        bool[] blank = DmlExecutor.OmittedColumnsBlankMask(Session, stmt.Table, columns, provided,
            honourSetNull: stmt.SourceKind == InsertSourceKind.Values);
        for (int i = 0; i < columns.Count; i++)
            if (!columns[i].IsSystem && !columns[i].IsAutoIncrement && !blank[i] && !ae.Fields.ContainsKey(i))
                ae.Fields[i] = null;
        (m.Buf ??= new TableBuffer()).Appends.Add(ae);
    }

    /// <summary>Write ONE buffered existing-row edit (field changes + delete/recall mark) to disk, firing the
    /// bound RI trigger when enforced (deferred to commit per hackfox s4g346). Returns false when a RESTRICT
    /// trigger aborts the row (the write is rolled back). Mirrors <see cref="ExecReplace"/>'s disk tail.</summary>
    private bool CommitExistingRow(int area, VfpSession.WorkArea wa, string path, int recno, RowEdit edit)
    {
        int recIndex = recno - 1;
        if (recIndex < 0 || recIndex >= wa.Table.RecordCount) return true;
        var m = Meta(area);
        bool hasFields = edit.Fields.Count > 0;

        // A buffered DELETE fires the delete trigger BEFORE the mark (ExecDelete order); a RESTRICT aborts.
        if (EnforceReferentialIntegrity && edit.DeletedOverride == true)
        {
            GoRecordCore(area, recno);
            if (!FireDmlTrigger(RiEvent.Delete, area)) return false;
        }

        // The buffered field NAMES this commit writes — for the record-level RI pre-image (whether a touched
        // field is FPT-backed) and the TAG-SCOPED in-place refresh (5.5). Empty for a pure delete/recall flip.
        var replacedFields = new List<string>(edit.Fields.Count);
        if (hasFields)
            foreach (var kv in edit.Fields)
                if (kv.Key >= 0 && kv.Key < wa.Table.Columns.Count) replacedFields.Add(wa.Table.Columns[kv.Key].Name);

        // 5.5 hot path (must-fix): the RI-abort pre-image is RECORD-LEVEL — only the one committed record can
        // change between the write and a trigger abort — not the whole .dbf/.cdx/.fpt (O(recordsize), not
        // O(filesize) per buffered row). Whole-file CaptureSnapshot is used only for a row it cannot represent
        // record-level (falls back exactly like ExecReplace).
        bool autoFireUpdate = EnforceReferentialIntegrity && hasFields && ResolveTriggerProc(RiEvent.Update, wa) is not null;
        RecordImage? parentImg = null; FileSnapshot? parentSnap = null;
        if (autoFireUpdate)
        {
            if (TryCaptureRecordImage(path, recIndex, wa, replacedFields, out var pImg)) parentImg = pImg;
            else parentSnap = CaptureSnapshot(path);
        }

        // 6.3: parentSnap (the whole-file RI pre-image when the record-level image could not represent the row)
        // streams to a temp file — clean it on every exit. RestoreSnapshot already cleans a restored pre-image;
        // Cleanup is idempotent.
        // 6.3 LEAK-WINDOW FIX: SnapshotForTxn runs as the FIRST line INSIDE the try (not before it). It may
        // CaptureSnapshot, which now THROWS on an existing-file copy I/O failure; a throw before the try would
        // skip the finally and LEAK parentSnap's already-captured temp file. Ordering (txn pre-image before
        // LeaseWriter) and semantics are unchanged; the txn snapshot is owned by _txn (cleaned at
        // commit/rollback), so the finally only ever touches parentSnap.
        try
        {
            SnapshotForTxn(path);
            using (var lease = LeaseWriter(path))
            {
                var writer = lease.Writer;
                if (hasFields)
                {
                    var values = new object?[writer.Schema.Columns.Count];
                    for (int i = 0; i < values.Length; i++) values[i] = DbfWriter.KeepValue;
                    foreach (var kv in edit.Fields) if (kv.Key >= 0 && kv.Key < values.Length) values[kv.Key] = kv.Value;
                    writer.UpdateRecord(recIndex, values);
                }
                if (edit.DeletedOverride is bool del) { if (del) writer.Delete(recIndex); else writer.Recall(recIndex); }
                writer.Flush();
            }
            // 5.5 hot path (must-fix): the commit is IN-PLACE (record count unchanged) — drop the read buffers in
            // place and PRESERVE the ordered cache unless a replaced field feeds a structural tag key (then the
            // cdx moved ⇒ RefreshAfterInPlaceWrite reopens). Replaces the unconditional full ReopenFileAreas that
            // re-opened every area (the ~8 ms Defender-scanned open) + dropped the whole Ordered cache per row.
            RefreshAfterInPlaceWrite(path, wa, replacedFields);

            if (autoFireUpdate)
            {
                GoRecordCore(area, recno);
                m.OldVals = new Dictionary<string, object?>(edit.Old, StringComparer.OrdinalIgnoreCase);
                if (!FireDmlTrigger(RiEvent.Update, area))
                {
                    if (parentImg is { } pRev) RestoreRecordImage(pRev);
                    else if (parentSnap is not null) RestoreSnapshot(parentSnap);
                    return false;
                }
            }
            return true;
        }
        finally { parentSnap?.Cleanup(); }
    }

    /// <summary>TABLEUPDATE core — commit the current row (allRows=false) or every buffered row + append
    /// (allRows=true) of <paramref name="area"/> to disk. Returns false when a RESTRICT trigger aborts a
    /// row (that row stays buffered). Committed rows/appends leave the buffer.</summary>
    private bool CommitBuffer(int area, bool allRows)
    {
        if (!_meta.TryGetValue(area, out var m)) return true;
        // TABLEUPDATE on an area whose buffering was NEVER enabled (mode 1) is a catchable ERROR in VFP9,
        // not a trivial success — distinguish it from "buffering on but nothing pending" (which returns .T.).
        if (m.Buffering <= 1)
            throw new MicroVfpRuntimeException("TABLEUPDATE(): the current work area is not buffered.");
        if (m.Buf is null) return true;                        // buffering enabled, nothing pending → .T.
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string livePath) return true;
        // COPY-ON-WRITE seam (ADO.NET transaction): redirect the buffered commit onto the table's private
        // working copy on FIRST write — the SAME seam ExecReplace/WriteFlag use — so a stored-proc
        // CURSORSETPROP('Buffering',n>1)+REPLACE/DELETE/INSERT+TABLEUPDATE() inside a FoxDbfTransaction is
        // isolated + rolled back instead of escaping to the live .dbf/.fpt/.cdx. No-op in autocommit, so
        // EnforceRules=off / non-transactional buffering stays byte-identical.
        string path = BeginTxWrite(livePath);
        wa = Session.AreaAt(area) ?? wa;                        // BeginTxWrite may have reopened the area on the copy.
        var buf = m.Buf;
        bool ok = true;
        _inBufferCommit = true;
        try
        {
            if (allRows)
            {
                // ATOMIC (task requirement): wrap the whole multi-row commit in a transaction frame so the
                // per-file pre-images are captured (SnapshotForTxn, incl. any child tables a deferred RI
                // trigger touches). On the FIRST RESTRICT abort we stop and roll EVERYTHING back, leaving the
                // buffer INTACT and returning .F. — never a half-written table with a half-cleared buffer.
                BeginTransaction();
                try
                {
                    var doneRows = new List<int>();
                    var doneApps = new List<AppendEntry>();
                    foreach (var kv in new List<KeyValuePair<int, RowEdit>>(buf.Rows))
                    {
                        if (CommitExistingRow(area, wa, path, kv.Key, kv.Value)) doneRows.Add(kv.Key);
                        else { ok = false; break; }
                    }
                    if (ok)
                        foreach (var ae in new List<AppendEntry>(buf.Appends))
                        {
                            if (CommitAppend(ae)) doneApps.Add(ae);
                            else { ok = false; break; }
                        }
                    if (ok)
                    {
                        EndTransaction();                      // commit: the writes already landed on disk.
                        foreach (var r in doneRows) buf.Rows.Remove(r);
                        foreach (var ae in doneApps) buf.Appends.Remove(ae);
                    }
                    else
                    {
                        RollbackTransaction();                 // revert every already-written row/append.
                    }
                }
                catch
                {
                    // A RAISED constraint violation (e.g. a CANDIDATE duplicate from CommitAppend) must roll
                    // the transaction frame back — reverting every already-committed row/append and leaving
                    // the buffer INTACT — instead of dangling the frame on the _txn stack.
                    RollbackTransaction();
                    throw;
                }
            }
            else
            {
                int rec = m.RecNo, rcTable = wa.Table.RecordCount;
                if (rec >= 1 && rec <= rcTable && buf.Rows.TryGetValue(rec, out var e))
                {
                    if (CommitExistingRow(area, wa, path, rec, e)) buf.Rows.Remove(rec); else ok = false;
                }
                else if (rec > rcTable)
                {
                    int ai = rec - rcTable - 1;
                    if (ai >= 0 && ai < buf.Appends.Count)
                    {
                        if (CommitAppend(buf.Appends[ai])) buf.Appends.RemoveAt(ai); else ok = false;
                    }
                }
            }
        }
        finally { _inBufferCommit = false; }
        // A rollback rebuilds AreaMeta (buffer preserved), so re-fetch before the empty-buffer cleanup.
        if (_meta.TryGetValue(area, out var cm) && cm.Buf is { IsEmpty: true }) cm.Buf = null;
        return ok;
    }

    /// <summary>Commit ONE buffered append: write the values that were FIXED at APPEND time straight to disk
    /// (NOT a re-run of the INSERT's VALUES expressions — a memvar that changed since the APPEND cannot alter
    /// the committed row), then fire the DEFERRED bound insert trigger / RI (hackfox s4g346). A RESTRICT
    /// abort rolls the appended row back. Returns true iff the row is actually persisted.</summary>
    private bool CommitAppend(AppendEntry ae)
    {
        string? table = (ae.Stmt.Parsed as InsertStatement)?.Table
                     ?? (SafeParseSql(ae.Stmt.Sql) as InsertStatement)?.Table;
        if (table is null) return false;
        var wa = Session.FindInsertArea(table);
        if (wa?.Table.SourcePath is not string livePath) return false;
        int area = wa.Area;
        // COPY-ON-WRITE seam: a buffered INSERT committed here (TABLEUPDATE / row-buffer auto-commit) must
        // land on the table's private working copy inside a FoxDbfTransaction — mirror ExecInsert's redirect.
        // No-op in autocommit, so the non-transactional buffered append stays byte-identical.
        string path = BeginTxWrite(livePath);
        wa = Session.AreaAt(area) ?? wa;                        // BeginTxWrite may have reopened the area on the copy.

        // Row built from the pre-evaluated field values; a column the INSERT left blank (not in Fields) is
        // written as VFP's blank bytes (spaces / zeros, a 0 memo pointer), never as a typed 0 / "" / .F.
        var vals = new object?[wa.Table.Columns.Count];
        var blank = new bool[vals.Length];
        for (int i = 0; i < vals.Length; i++)
        {
            if (ae.Fields.TryGetValue(i, out var v)) vals[i] = v;
            else blank[i] = true;
        }

        // 5.5 hot path (must-fix): reverting a committed APPEND removes the last physical record — it is NOT
        // a record-level UpdateRecord, so it keeps the whole-file pre-image. But capture it ONLY when the
        // append can actually be aborted — a CANDIDATE tag to re-check, or a bound insert trigger (RESTRICT).
        // A plain buffered append (the bulk Buffering=5 + TABLEUPDATE case) has no abort path, so it pays NO
        // per-row whole-file read (the pathology (3) cost this must-fix removes). The PRG/ADO transaction
        // frame's own once-per-table pre-image (SnapshotForTxn) still covers a batch rollback.
        bool candidateHere = CandidateTagsFor(path) is not null;
        bool autoFireInsert = EnforceReferentialIntegrity && ResolveTriggerProc(RiEvent.Insert, wa) is not null;
        // snap lives OUTSIDE the try so the finally can Cleanup it; the CAPTURE runs INSIDE the try (below).
        FileSnapshot? snap = null;
        // 6.3: the whole-file append pre-image (snap) streams to a temp file — clean it on every exit
        // (success, the candidate-violation throw, or the trigger-abort return). RestoreSnapshot already cleans
        // a restored pre-image; Cleanup is idempotent.
        // 6.3 LEAK-WINDOW FIX: the append + txn-frame captures run as the FIRST lines INSIDE the try (not before
        // it). CaptureSnapshot now THROWS on an existing-file copy I/O failure; a throw in SnapshotForTxn after
        // snap was captured would otherwise skip the finally and LEAK snap's temp file. Ordering (append + txn
        // pre-image before LeaseWriter) and semantics are unchanged; the txn snapshot is owned by _txn (cleaned
        // at commit/rollback), so the finally only ever touches snap.
        try
        {
            snap = (candidateHere || autoFireInsert) ? CaptureSnapshot(path) : null;
            SnapshotForTxn(path);
            using (var lease = LeaseWriter(path))
            {
                lease.Writer.AppendRecord(vals, blank);
                lease.Writer.Flush();
            }
            ReopenFileAreas(path);   // an append changed the record count/geometry ⇒ full re-open (RefreshView cannot).

            // CANDIDATE: a buffered APPEND committed here (TABLEUPDATE) that duplicates a candidate key must
            // RAISE — the same enforcement the write-through INSERT path applies. The maintained cdx now holds
            // the duplicate; restore the pre-image and raise on a violation. (CommitBuffer's allRows transaction
            // frame is rolled back by its catch so the raise leaves the buffer intact.)
            if (candidateHere && FirstViolatedCandidate(path) is string badAppendTag)
            {
                if (snap is not null) RestoreSnapshot(snap);
                throw new MicroVfpRuntimeException(
                    $"APPEND: CANDIDATE tag {badAppendTag} uniqueness violated — a duplicate key value exists.", 1884); // VFP err 1884 (oracle-pinned).
            }

            // Deferred insert trigger / RI: positioned ON the new (last physical) record; .F. ⇒ RESTRICT abort.
            if (autoFireInsert)
            {
                int savedArea = Session.CurrentArea;
                Session.SelectArea(area);
                var fresh = Session.AreaAt(area);
                if (fresh is not null) GoRecordCore(area, fresh.Table.RecordCount);
                bool ok = FireDmlTrigger(RiEvent.Insert, area);
                Session.SelectArea(savedArea);
                if (!ok) { if (snap is not null) RestoreSnapshot(snap); return false; }
            }

            // A buffered DELETE on the appended row commits as a deleted physical record.
            if (ae.Deleted && Session.AreaAt(area) is { } after && after.Table.RecordCount > 0)
            {
                using (var lease2 = LeaseWriter(path))
                {
                    lease2.Writer.Delete(after.Table.RecordCount - 1);
                    lease2.Writer.Flush();
                }
                ReopenFileAreas(path);
            }
            return true;
        }
        finally { snap?.Cleanup(); }
    }

    private int AreaOfClause(ReplaceClause c)
    {
        string name = NameOf(c.Field);
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            var wa = Session.FindAreaByAlias(name[..dot]);
            if (wa is not null) return wa.Area;
        }
        return Session.CurrentArea;
    }

    // ─────────────────────────── buffering model (microVFP P1 gap #4) ───────────────────────────
    //
    // A per-work-area buffer that DEFERS REPLACE/DELETE/RECALL (existing rows) and INSERT (appends) when
    // the area's Buffering mode is >1, so they can be committed atomically (TABLEUPDATE) or discarded
    // (TABLEREVERT). While buffered, field reads/DELETED()/OLDVAL()/GETFLDSTATE() reflect the buffer;
    // CURVAL() reads the ON-DISK value. Single-process ⇒ CURVAL == disk and optimistic-vs-pessimistic
    // conflict is moot (no fabricated conflict errors). Buffered appends are VISIBLE on the live cursor
    // (RECCOUNT/GO n) but not on disk until TABLEUPDATE, which replays them through the ordinary INSERT
    // path (so the DBC RULE/TRIGGER/RI pipeline fires DEFERRED, per hackfox s4g346).

    private sealed class TableBuffer
    {
        // Edits to EXISTING physical rows, keyed by 1-based recno.
        public readonly Dictionary<int, RowEdit> Rows = new();
        // Buffered appended rows (table buffering only), in append order. Recno = RecordCount + 1 + index.
        public readonly List<AppendEntry> Appends = new();
        public bool IsEmpty => Rows.Count == 0 && Appends.Count == 0;
    }

    private sealed class RowEdit
    {
        public readonly Dictionary<int, object?> Fields = new();               // colIndex → buffered new value.
        public bool? DeletedOverride;                                          // null ⇒ no pending delete change.
        public readonly Dictionary<string, object?> Old = new(StringComparer.OrdinalIgnoreCase); // OLDVAL per field.
    }

    private sealed class AppendEntry
    {
        public InsertStmt Stmt = null!;                                        // replayed through ExecInsert at commit.
        public readonly Dictionary<int, object?> Fields = new();              // colIndex → value (visible pre-commit).
        public bool Deleted;                                                   // a buffered DELETE on the appended row.
    }

    // Cheap fast-path gate: true once any area has been switched to a buffering mode (2..5). Lets the
    // default (write-through) INSERT path skip the buffer pre-check entirely — never reset (a stray reparse
    // is harmless), so the ~2141 Buffering=1 tests are byte-for-byte unchanged.
    private bool _anyBuffering;
    // Re-entrancy guard: while TABLEUPDATE replays buffered appends through ExecInsert, they must WRITE
    // THROUGH (not re-buffer).
    private bool _inBufferCommit;

    /// <summary>ROW buffering (mode 2/3): a USER pointer move implicitly commits the pending edit on the
    /// CURRENT record (TABLEUPDATE of that one row) before leaving it. A no-op for table buffering (4/5) —
    /// those wait for an explicit TABLEUPDATE — and for unbuffered areas. Only the current EXISTING row is
    /// committed (row buffering never defers appends). Internal (non-user) moves go through the *Core
    /// methods and so never trip this.</summary>
    private void MaybeAutoCommitRow(int area)
    {
        if (_inBufferCommit) return;   // internal moves DURING a commit must not re-trigger the auto-commit.
        if (!_meta.TryGetValue(area, out var m) || (m.Buffering != 2 && m.Buffering != 3) || m.Buf is null) return;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string livePath) return;
        // COPY-ON-WRITE seam: a row-buffer pointer-move auto-commit is a real write — redirect it onto the
        // table's private working copy inside a FoxDbfTransaction (mirrors CommitBuffer/ExecReplace). No-op
        // in autocommit, so the non-transactional row-buffer auto-commit stays byte-identical.
        string path = BeginTxWrite(livePath);
        wa = Session.AreaAt(area) ?? wa;                        // BeginTxWrite may have reopened the area on the copy.
        int rec = m.RecNo, rcTable = wa.Table.RecordCount;
        _inBufferCommit = true;
        try
        {
            if (rec >= 1 && rec <= rcTable && m.Buf.Rows.TryGetValue(rec, out var e))
            {
                // Only DROP the buffered edit once it is actually on disk; a RESTRICT abort keeps it buffered
                // AND surfaces the failure (VFP blocks the move rather than silently losing the edit).
                if (CommitExistingRow(area, wa, path, rec, e)) m.Buf.Rows.Remove(rec);
                else throw new MicroVfpRuntimeException("Update conflict: the pending row was blocked on commit.");
            }
            else if (rec > rcTable)                            // the current record is a buffered APPEND.
            {
                int ai = rec - rcTable - 1;
                if (ai >= 0 && ai < m.Buf.Appends.Count)
                {
                    if (CommitAppend(m.Buf.Appends[ai])) m.Buf.Appends.RemoveAt(ai);
                    else throw new MicroVfpRuntimeException("Insert conflict: the pending append was blocked on commit.");
                }
            }
        }
        finally { _inBufferCommit = false; }
        if (_meta.TryGetValue(area, out var cm) && cm.Buf is { IsEmpty: true }) cm.Buf = null;
    }

    // OLDVAL(cField [, cAlias]) — the value at buffer start, captured by the matching REPLACE (so a
    // manually-fired RI UPDATE trigger reads the OLD key); falls back to the current value when none.
    private VfpValue FnOldVal(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Null;
        string field = StripQualifier(a[0].AsString);
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var m = Meta(area);
        // The buffer's per-record OLD map (the value at buffer start / last commit) takes precedence, then
        // the inline-RI OldVals (write-through path), then the current on-disk value.
        if (m.Buf is { } buf && buf.Rows.TryGetValue(m.RecNo, out var e) && e.Old.TryGetValue(field, out var bold))
            return VfpValue.FromClr(bold);
        if (m.OldVals is not null && m.OldVals.TryGetValue(field, out var old))
            return VfpValue.FromClr(old);
        return FnCurVal(a);
    }

    // CURVAL(cField [, cAlias]) — the current ON-DISK value (bypasses the buffer). Single-process ⇒ this is
    // also the "committed" value the (moot) optimistic-conflict check would compare against.
    private VfpValue FnCurVal(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Null;
        string field = StripQualifier(a[0].AsString);
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        return wa is null ? VfpValue.Null : VfpValue.FromClr(ReadFieldOnDisk(wa, field));
    }

    // ─────────────────────────── CURSORGETPROP / CURSORSETPROP / TABLEUPDATE / TABLEREVERT / GETFLDSTATE ─────────

    // CURSORGETPROP(cProperty [, cAlias|nWorkArea]) — the free-table property subset microVFP backs
    // (s4g348): Buffering (real per-area mode), SourceName/SourceType/Database. Unknown properties fall
    // back to .NULL. (a view-only property set is out of scope — no view model).
    private VfpValue FnCursorGetProp(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Null;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        return a[0].AsString.ToUpperInvariant() switch
        {
            "BUFFERING" => VfpValue.Integer(m.Buffering),
            "SOURCENAME" => VfpValue.Character(m.SourceName ?? wa?.Alias ?? string.Empty),
            "SOURCETYPE" => m.SourceType ?? VfpValue.Integer(0),
            "DATABASE" => VfpValue.Character(m.DatabaseProp ?? string.Empty),
            _ => VfpValue.Null,
        };
    }

    // CURSORSETPROP(cProperty, eValue [, cAlias|nWorkArea]) — sets the free-table subset (s4g348). Buffering
    // 1..5 (needs SET MULTILOCKS ON in real VFP for 4/5; single-process here so no lock model). Setting a
    // property outside the backed subset returns .F. (rather than throwing). Returns .T. on success.
    private VfpValue FnCursorSetProp(VfpValue[] a)
    {
        if (a.Length < 2) return VfpValue.Logical(false);
        int area = a.Length > 2 ? AreaNumber(a[2]) : Session.CurrentArea;
        var m = Meta(area);
        switch (a[0].AsString.ToUpperInvariant())
        {
            case "BUFFERING":
                int n = (int)a[1].AsNumber;
                if (n < 1 || n > 5) return VfpValue.Logical(false);
                // Changing the buffering mode while edits are still PENDING is rejected in VFP9 (the buffer
                // must be committed/reverted first) — leave BOTH the mode and the buffer untouched. This also
                // guarantees a live buffer never coexists with mode 1 (reads never shadow disk under 1).
                if (n != m.Buffering && m.Buf is { IsEmpty: false }) return VfpValue.Logical(false);
                m.Buffering = n;
                if (n >= 2) _anyBuffering = true;
                return VfpValue.Logical(true);
            case "SOURCENAME": m.SourceName = a[1].AsString; return VfpValue.Logical(true);
            case "SOURCETYPE": m.SourceType = a[1]; return VfpValue.Logical(true);
            case "DATABASE": m.DatabaseProp = a[1].AsString; return VfpValue.Logical(true);
            default: return VfpValue.Logical(false);   // outside the backed free-table subset.
        }
    }

    // TABLEUPDATE([nRows | lAllRows] [, lForce] [, cAlias|nWorkArea]) — commit buffered changes to disk;
    // .T./.F. lForce is accepted but moot single-process (no optimistic conflict is fabricated). A numeric
    // >=1 or logical .T. first arg ⇒ all rows; otherwise the current row only.
    private VfpValue FnTableUpdate(VfpValue[] a)
    {
        bool allRows = a.Length > 0 && (a[0].Type == VfpType.Logical ? a[0].AsLogical : (IsNumeric(a[0]) && a[0].AsNumber >= 1));
        int area = a.Length > 2 ? AreaNumber(a[2]) : Session.CurrentArea;
        return VfpValue.Logical(CommitBuffer(area, allRows));
    }

    // TABLEREVERT([lAllRows] [, cAlias|nWorkArea]) — discard buffered changes; returns the count reverted.
    private VfpValue FnTableRevert(VfpValue[] a)
    {
        bool allRows = a.Length > 0 && (a[0].Type == VfpType.Logical ? a[0].AsLogical : (IsNumeric(a[0]) && a[0].AsNumber >= 1));
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        // TABLEREVERT on a never-buffered (mode 1) area is a catchable ERROR in VFP9, not a trivial 0.
        if (!_meta.TryGetValue(area, out var m) || m.Buffering <= 1)
            throw new MicroVfpRuntimeException("TABLEREVERT(): the current work area is not buffered.");
        if (m.Buf is null) return VfpValue.Integer(0);         // buffering on, nothing pending → 0.
        var wa = Session.AreaAt(area);
        var buf = m.Buf;
        int count;
        if (allRows)
        {
            count = buf.Rows.Count + buf.Appends.Count;
            buf.Rows.Clear();
            buf.Appends.Clear();
        }
        else
        {
            count = 0;
            int rec = m.RecNo, rcTable = wa?.Table.RecordCount ?? 0;
            if (rec >= 1 && rec <= rcTable) { if (buf.Rows.Remove(rec)) count = 1; }
            else if (rec > rcTable) { int ai = rec - rcTable - 1; if (ai >= 0 && ai < buf.Appends.Count) { buf.Appends.RemoveAt(ai); count = 1; } }
        }
        m.Cached = null;                         // reverted values re-read from disk.
        if (buf.IsEmpty) m.Buf = null;
        return VfpValue.Integer(count);
    }

    // GETFLDSTATE(cFieldName | nFieldNumber [, cAlias]) — the per-field buffer change state: 1 unchanged,
    // 2 changed, 3 appended (unchanged field), 4 appended+changed. nFieldNumber is 1-based (0 ⇒ the record
    // delete-state). A buffered-but-unedited field is 1. An unknown character name raises catchable VFP
    // error 11, while an UNBUFFERED area raises error 1586 (verified live). (s4g395: on real tables the
    // state is derived from the buffer; SETFLDSTATE is inert.)
    private VfpValue FnGetFldState(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Integer(1);
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return VfpValue.Integer(1);
        var m = Meta(area);
        if (m.Buffering <= 1)
            throw new MicroVfpRuntimeException("Function requires row or table buffering mode.", 1586);
        bool isField0 = IsNumeric(a[0]) && a[0].AsNumber == 0m;
        int colIdx = IsNumeric(a[0]) ? (int)a[0].AsNumber - 1 : ColumnIndex(wa.Table, StripQualifier(a[0].AsString));
        // -1 is reserved for numeric field 0 (the record delete-state). An unresolved character
        // name is a real VFP error, not another spelling of field 0.
        if (!isField0 && colIdx < 0)
            throw new MicroVfpRuntimeException("Field name was not found.", 11);
        if (m.Buf is not { } buf) return VfpValue.Integer(1);   // buffering on, nothing pending.
        int rcTable = wa.Table.RecordCount;
        if (m.RecNo > rcTable)                                  // a buffered appended row.
        {
            int ai = m.RecNo - rcTable - 1;
            if (ai >= 0 && ai < buf.Appends.Count)
                return VfpValue.Integer(colIdx >= 0 && buf.Appends[ai].Fields.ContainsKey(colIdx) ? 4 : 3);
            return VfpValue.Integer(1);
        }
        if (m.RecNo >= 1 && m.RecNo <= rcTable && buf.Rows.TryGetValue(m.RecNo, out var e))
        {
            if (colIdx >= 0) return VfpValue.Integer(e.Fields.ContainsKey(colIdx) ? 2 : 1);
            return VfpValue.Integer(e.DeletedOverride is not null ? 2 : 1);   // field 0 = record delete-state.
        }
        return VfpValue.Integer(1);
    }

}
