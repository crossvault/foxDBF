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

/// <summary>microVFP referential-integrity / trigger auto-fire plus the transaction snapshot-and-restore atomicity machinery.</summary>
public sealed partial class VfpInterpreter
{
    /// <summary>
    /// P2 (RI) seam — the VFP <c>_triggerlevel</c> system variable the RI trigger procedures branch on
    /// (<c>IF _triggerlevel=1</c> gates the BEGIN TRANSACTION + SET-save + ON ERROR setup block). A test
    /// invoking a trigger as the OUTERMOST trigger sets this to 1. STUB for P2: stored + surfaced as the
    /// <c>_triggerlevel</c> name, but the nesting is NOT yet auto-managed by the cascade machinery
    /// (RIDELETE/RIUPDATE do not yet increment/decrement it), so the cascade contracts are still RED.
    /// </summary>
    public int TriggerLevel { get; set; } = 1;

    /// <summary>
    /// P3b master switch — when <see langword="true"/>, a PLAIN interpreter <c>DELETE</c>/<c>REPLACE</c>/
    /// <c>INSERT</c> on a DBC member that has a bound RI trigger AUTO-FIRES that trigger (cascade / restrict),
    /// exactly as VFP9 enforces its RI without any manual trigger call. Default <see langword="false"/> so
    /// the P2 manual-invocation behaviour (and all existing tests) is untouched.
    /// </summary>
    /// <remarks>
    /// TRIGGER BINDING — the bound trigger expression is NOT decodable from this codebase's DBC reader
    /// (the container tree decodes only the member's <c>PROPERTY</c> path, not its delete/update/insert
    /// trigger properties). So auto-fire FALLS BACK to the RI Builder NAMING CONVENTION the bindings are
    /// generated to match: <c>__RI_DELETE_&lt;table&gt;</c> / <c>__RI_UPDATE_&lt;table&gt;</c> /
    /// <c>__RI_INSERT_&lt;table&gt;</c>. A table with no such loaded proc (a free table, or no RI rule) does
    /// a plain DML with no trigger.
    /// </remarks>
    public bool EnforceReferentialIntegrity { get; set; }

    /// <summary>
    /// P3b instrumentation — the value of <c>_triggerlevel</c> captured at the entry of each AUTO-FIRED RI
    /// trigger, in firing order. The top-level fire records <c>1</c>; a nested cascade (a child DML fired
    /// from inside a running trigger) records <c>2</c>, <c>3</c>, … This lets a test observe the
    /// <c>_triggerlevel</c> deepening + restoration without reaching into the trigger procs. STUB: stays
    /// empty until the auto-fire machinery is built, so the nesting contract is RED.
    /// </summary>
    public IReadOnlyList<int> FiredTriggerLevels => _firedTriggerLevels;
    private readonly List<int> _firedTriggerLevels = new();

    /// <summary>
    /// P3b (ADO.NET enforced write-model) — set to <see langword="true"/> when the OUTERMOST auto-fired RI
    /// trigger of a DML op returns <c>.F.</c> (a RESTRICT abort, or a child-cascade restrict that propagated
    /// up to the top-level trigger). The plain <c>ExecInsert</c>/<c>ExecReplace</c>/<c>ExecDelete</c> path
    /// rolls the row back but returns NORMALLY (the trigger result is not raised), so the enforced ADO.NET
    /// write path inspects this flag after each row op to raise VFP error 1539 instead of reporting a phantom
    /// success with a wrong affected-record count. The caller resets it to <see langword="false"/> before each
    /// row op; it is never set on a successful (allowed) DML.
    /// </summary>
    public bool LastDmlTriggerAborted { get; set; }

    // P3b nesting counter — 0 outside any auto-fired trigger; 1 at the top-level fire, 2 inside a cascade
    // fired from within a running trigger, … This is the REAL backing for VFP's _triggerlevel: every
    // auto-fire increments it around the trigger Call and decrements on the way out. The cascade DML the
    // trigger itself issues (RIDELETE's DELETE / RIUPDATE's REPLACE) re-enters the interpreter's DML and
    // auto-fires the child trigger at the next depth — so the nesting deepens by construction.
    private int _triggerDepth;

    // P3b RI events a bound trigger fires for (delete current/scoped record, key-changing replace, append).
    private enum RiEvent { Delete, Update, Insert }

    /// <summary>
    /// P3b auto-fire seam — fire the table's bound RI trigger for <paramref name="ev"/> on work area
    /// <paramref name="area"/> the SAME way VFP does, managing <c>_triggerlevel</c> nesting. Returns
    /// <see langword="true"/> to ALLOW the DML, <see langword="false"/> to ABORT it (a RESTRICT rule —
    /// the trigger returned <c>.F.</c>). A table with no bound trigger (no matching <c>__RI_*</c> proc —
    /// a free table or a member with no RI rule) returns <see langword="true"/> and fires nothing, so it
    /// does a plain DML. ATOMICITY: the trigger itself wraps its cascade in <c>BEGIN/END TRANSACTION</c>
    /// (gated by <c>IF _triggerlevel=1</c>) and ROLLBACKs via <c>riend(.F.)</c> on a restrict/error — the
    /// P2 transaction machinery — so the parent op + its cascade are atomic without extra wrapping here.
    /// </summary>
    private bool FireDmlTrigger(RiEvent ev, int area)
    {
        var wa = Session.AreaAt(area);
        if (wa is null) return true;
        string? proc = ResolveTriggerProc(ev, wa);
        if (proc is null) return true;                       // no bound trigger ⇒ plain DML.

        _triggerDepth++;
        int saved = TriggerLevel;
        TriggerLevel = _triggerDepth;                        // 1 at the top fire; 2, 3, … when nested.
        _firedTriggerLevels.Add(_triggerDepth);
        try
        {
            // The trigger runs positioned on the current record of the DML's area (the parent being
            // deleted / the just-replaced parent). A .F. return = a RESTRICT violation ⇒ ABORT the DML.
            var r = Call(proc);
            bool allowed = !(r.Type == VfpType.Logical && !r.AsLogical);
            // Surface a TOP-LEVEL abort (the outermost op the enforced ADO.NET path issued) so that path can
            // raise VFP err 1539 rather than report a phantom success — a nested child-restrict propagates up
            // to this top-level trigger's .F. return by construction (the RI cascade re-enters and re-fires).
            if (!allowed && _triggerDepth == 1) LastDmlTriggerAborted = true;
            return allowed;
        }
        finally
        {
            _triggerDepth--;
            TriggerLevel = _triggerDepth == 0 ? saved : _triggerDepth;   // restore to the top level (1).
        }
    }

    /// <summary>Resolve the bound RI trigger proc for <paramref name="ev"/> on <paramref name="wa"/> via the
    /// RI Builder NAMING CONVENTION <c>__RI_&lt;event&gt;_&lt;table&gt;</c> (the binding expression is not
    /// decodable from this codebase's DBC reader — see <see cref="EnforceReferentialIntegrity"/>). Tries the
    /// work-area alias then the .dbf base name; null when no such proc is loaded.</summary>
    private string? ResolveTriggerProc(RiEvent ev, VfpSession.WorkArea wa)
    {
        string evName = ev switch { RiEvent.Delete => "DELETE", RiEvent.Update => "UPDATE", _ => "INSERT" };
        foreach (var table in TableNameCandidates(wa))
        {
            string name = "__RI_" + evName + "_" + table;
            if (_procs.ContainsKey(name)) return name;       // _procs is OrdinalIgnoreCase.
        }
        return null;
    }

    private static IEnumerable<string> TableNameCandidates(VfpSession.WorkArea wa)
    {
        yield return wa.Alias;
        var path = wa.Table.SourcePath;
        if (!string.IsNullOrEmpty(path))
        {
            string fn = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(fn, wa.Alias, StringComparison.OrdinalIgnoreCase)) yield return fn;
        }
    }

    // ─────────────────────────── transactions (snapshot / restore) ───────────────────────────

    private void BeginTransaction() => _txn.Add(new TxnFrame());

    private void EndTransaction()
    {
        if (_txn.Count == 0) return;                      // unbalanced END is a no-op (VFP would error).
        var done = _txn[^1];
        _txn.RemoveAt(_txn.Count - 1);
        // COMMIT: the writes already landed on the live files; just hand any not-yet-owned snapshots to
        // the enclosing transaction so an OUTER rollback can still revert inner-committed changes.
        if (_txn.Count > 0)
        {
            var parent = _txn[^1];
            foreach (var kv in done.Snaps)
                if (!Snapshotted(kv.Key)) parent.Snaps[kv.Key] = kv.Value;   // hand up — the parent keeps the temp pre-image.
                else kv.Value.Cleanup();                                     // parent already owns this path — drop the inner temp.
        }
        else
            // 6.3: the OUTERMOST commit keeps the live files, so the pre-image temps are done — delete every one
            // (temp dir empty after commit). A nested commit hands its temps up (above), so this only fires once.
            foreach (var kv in done.Snaps) kv.Value.Cleanup();
    }

    private void RollbackTransaction()
    {
        if (_txn.Count == 0) return;
        var frame = _txn[^1];
        _txn.RemoveAt(_txn.Count - 1);
        foreach (var snap in frame.Snaps.Values) RestoreSnapshot(snap);
    }

    /// <summary>True when <paramref name="path"/> already has a snapshot in ANY active transaction frame
    /// (so the first writer per table in the whole transaction stack captures the pre-image once).</summary>
    private bool Snapshotted(string path)
    {
        foreach (var f in _txn)
            if (f.Snaps.ContainsKey(path)) return true;
        return false;
    }

    /// <summary>Before a table WRITE inside a transaction, capture the pre-image of its <c>.dbf</c>
    /// (+ sibling <c>.cdx</c>/<c>.fpt</c>) once, into the innermost frame — so a ROLLBACK restores it.</summary>
    private void SnapshotForTxn(string? path)
    {
        if (_txn.Count == 0 || path is null || Snapshotted(path)) return;
        _txn[^1].Snaps[path] = CaptureSnapshot(path);
    }

    /// <summary>Capture the pre-image of a table's <c>.dbf</c> (+ sibling <c>.cdx</c>/<c>.fpt</c>) into a
    /// standalone snapshot — independent of the transaction stack. P3b uses this to make a PLAIN (no open
    /// user transaction) parent <c>REPLACE</c>/<c>INSERT</c> revertible: the parent write lands first (so the
    /// auto-fired update/insert trigger can read the NEW key via the current field), and if that trigger
    /// returns <c>.F.</c>/errors (a RESTRICT abort), <see cref="RestoreSnapshot"/> rolls the parent write back
    /// so the parent op + its triggered cascade are atomic.</summary>
    private FileSnapshot CaptureSnapshot(string path)
    {
        var snap = new FileSnapshot { Path = path };
        // 6.3: stream the pre-images to TEMP FILES (CopyToTempSnapshot) rather than whole files into byte[] —
        // FileShare.ReadWrite so the copy runs WHILE the 5.5 cached writer holds the .dbf/.fpt open. A copy
        // failure on an EXISTING file THROWS (a capture failure fails the write/txn honestly instead of a
        // silent no-rollback); an ABSENT companion stays null (its restore deletes any file that appeared).
        // If a later copy throws, clean up the temps already made so a failed capture leaks nothing.
        try
        {
            snap.DbfTemp = CopyToTempSnapshot(path);
            snap.CdxTemp = CopyToTempSnapshot(Path.ChangeExtension(path, ".cdx"));
            snap.FptTemp = CopyToTempSnapshot(Path.ChangeExtension(path, ".fpt"));
        }
        catch
        {
            snap.Cleanup();
            throw;
        }
        return snap;
    }

    /// <summary>ROLLBACK one table: close any work area on it, rewrite its files from the pre-image, then
    /// re-open the area(s) in place (same number / alias / order) so the reverted state is visible.</summary>
    private void RestoreSnapshot(FileSnapshot snap)
    {
        // 5.5: a cached writer holding this .dbf/.fpt open would block the File.Copy rewrite below (its
        // FileShare.None destination open), and its stale handle/count must not survive a rollback. Drop it
        // first — the next write re-opens.
        // 5.13: FORCE-close it even when an explicit RLOCK/FLOCK pins it — InvalidateCachedWriter would
        // SKIP a pinned writer, leaving the handle open so the File.Copy below hits a sharing violation
        // that the surrounding catch{} silently swallows (⇒ the rollback no-ops, leaving the duplicate/invalid
        // rows on disk under the raised error). ReacquireHeldLocks at the end re-takes the tracked lock.
        ForceCloseCachedWriter(snap.Path);
        // Capture the work areas currently riding this file so they can be re-opened after the restore.
        // TablePath (the actual .dbf source) is captured alongside the alias: a riopen SCRATCH area has an
        // alias like "__ri6" which is NOT a DBC member / on-disk file, so re-opening BY ALIAS would throw —
        // re-open BY PATH instead (TryDbcName re-resolves the path to its DBC member to keep long names).
        // The buffering mode + any pending buffer (and the CURSORSETPROP free-table props) must SURVIVE a
        // rollback: a TABLEUPDATE that fails half-way rolls the file(s) back but has to leave the microVFP
        // buffer intact (so the caller can keep the edits buffered and return .F.). RestoreSnapshot otherwise
        // rebuilds AreaMeta from scratch, which would silently drop the buffer + reset the mode to 1.
        var reopen = new List<(int Area, string Alias, bool Excl, bool NoUpd, string? TablePath, AreaMeta? Meta)>();
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, snap.Path))
            {
                _meta.TryGetValue(w.Area, out var mm);
                reopen.Add((w.Area, w.Alias, w.Exclusive, w.NoUpdate, w.Table.SourcePath, mm));
            }

        foreach (var r in reopen) Session.CloseArea(r.Area);

        try { if (snap.DbfTemp is not null) File.Copy(snap.DbfTemp, snap.Path, overwrite: true); } catch { }
        string cdx = Path.ChangeExtension(snap.Path, ".cdx");
        string fpt = Path.ChangeExtension(snap.Path, ".fpt");
        RestoreSnapshotSidecar(cdx, snap.CdxTemp);
        RestoreSnapshotSidecar(fpt, snap.FptTemp);

        int savedCur = Session.CurrentArea;
        foreach (var r in reopen)
        {
            // Inside an ADO.NET transaction the captured TablePath is the private COPY; canonicalize it back to
            // the LIVE path so Use re-resolves the DBC member (long field names) and re-applies the read
            // redirect onto the copy. In autocommit TxLivePath is null → the path is used unchanged.
            string? reopenTarget = r.TablePath is { } tp && Session.TxLivePath is { } toLive ? toLive(tp) : r.TablePath;
            Session.Use(reopenTarget ?? r.Alias, r.Area, r.Alias, again: false, exclusive: r.Excl, noUpdate: r.NoUpd);
            _meta[r.Area] = RestorePersistentAreaMeta(r.Meta);
        }
        foreach (var r in reopen) GoTop(r.Area);
        int[] relationParents = Session.OpenAreas.Select(w => w.Area).ToArray();
        foreach (int area in relationParents) RepositionChildren(area);
        Session.SelectArea(savedCur);
        // 5.13: re-take on the fresh cached writer the explicit RLOCK/FLOCK we force-closed above, so a lock
        // an SP holds while a transaction / RI / CANDIDATE abort rolls the file back stays held afterwards.
        ReacquireHeldLocks(snap.Path);
        // 6.3: the pre-image temp files are consumed — delete them (so the temp dir is empty after a rollback).
        snap.Cleanup();
    }

    private static AreaMeta RestorePersistentAreaMeta(AreaMeta? old)
    {
        if (old is null) return new AreaMeta();
        return new AreaMeta
        {
            Order = old.Order,
            OrderReversed = old.OrderReversed,
            Buffering = old.Buffering,
            Buf = old.Buf,
            SourceName = old.SourceName,
            SourceType = old.SourceType,
            DatabaseProp = old.DatabaseProp,
            FilterExpr = old.FilterExpr,
            FilterText = old.FilterText,
            KeySet = old.KeySet,
            KeyRange = old.KeyRange,
            KeyLow = old.KeyLow,
            KeyHigh = old.KeyHigh,
            Relations = old.Relations is null ? null : new List<Relation>(old.Relations),
            LocateActive = old.LocateActive,
            LocateFor = old.LocateFor,
            LocateWhile = old.LocateWhile,
            LocateWindow = old.LocateWindow is null ? null : new HashSet<int>(old.LocateWindow),
            LocateWindowFull = old.LocateWindowFull,
            ExtraIndexes = old.ExtraIndexes is null ? null : new List<string>(old.ExtraIndexes),
        };
    }

    private static void RestoreSnapshotSidecar(string path, string? tempPreimage)
    {
        try
        {
            if (tempPreimage is not null) File.Copy(tempPreimage, path, overwrite: true);
            else if (File.Exists(path)) File.Delete(path);   // review fix 7: pre-image absent ⇒ delete the companion that appeared.
        }
        catch { }
    }

    // ── 5.5 RECORD-LEVEL pre-image (pathology 3): the per-statement RI atomicity snapshots for a REPLACE do
    // not need the WHOLE .dbf/.cdx/.fpt — only the ONE affected record can change between the parent write and
    // a trigger/candidate abort (the trigger's child cascade lives in OTHER tables, reverted by the trigger's
    // own transaction). So capture just that record's values + deleted flag (O(recordsize), not O(filesize))
    // and revert it in place through the cached writer (which re-maintains the cdx). Falls back to a whole-file
    // FileSnapshot only for the record it cannot represent (a deleted/unreadable current row). The PRG BEGIN
    // TRANSACTION frame (SnapshotForTxn) intentionally stays whole-file. ──
    private readonly struct RecordImage
    {
        public readonly string Path;
        public readonly int RecIndex;
        // Per public column. M/W hold the OLD decoded CONTENT (string / byte[]) so RestoreRecordImage can
        // round-trip it into a fresh .fpt block; UNTOUCHED G/P hold DbfWriter.KeepValue (their on-disk block
        // pointer is preserved verbatim). A TOUCHED G/P cannot be represented here — TryCaptureRecordImage
        // bails to the whole-file snapshot in that case (see the method).
        public readonly object?[] Values;
        public readonly bool Deleted;
        public RecordImage(string path, int recIndex, object?[] values, bool deleted)
        { Path = path; RecIndex = recIndex; Values = values; Deleted = deleted; }
    }

    /// <summary>Capture a record-level pre-image of physical record <paramref name="recIndex"/> of
    /// <paramref name="wa"/> for a revertible REPLACE. <paramref name="replacedFields"/> is the set of fields
    /// the statement writes (empty for a DELETE/RECALL flag flip). Returns <see langword="false"/> (⇒ the
    /// caller falls back to a whole-file <see cref="CaptureSnapshot"/>) when the row cannot be represented
    /// record-level:
    /// <list type="bullet">
    ///   <item>a deleted/unreadable current record (<see cref="DbfTable.GetRecord"/> returns null); or</item>
    ///   <item>the statement TOUCHES a General/Picture (<c>G</c>/<c>P</c>) field — those decode to a raw
    ///     4-byte FPT block POINTER, not content, so feeding the read-back value back through
    ///     <see cref="DbfWriter.UpdateRecord(int, object?[])"/> would write the pointer bytes AS block data
    ///     and destroy the object. Only the whole-file snapshot can revert such a field.</item>
    /// </list>
    /// FPT-BACKED CONTENT (the 5.5 must-fix): memo/blob (<c>M</c>/<c>W</c>) fields decode to their actual
    /// CONTENT (a text string for <c>M</c>, raw <c>byte[]</c> for <c>W</c>), which <c>UpdateRecord</c> writes
    /// into a fresh <c>.fpt</c> block — so an aborted <c>REPLACE …, memofld WITH newtext</c> reverts the memo
    /// (storing <see cref="DbfWriter.KeepValue"/> instead, as pre-fix, kept the NEW block ⇒ a partial write
    /// survived the abort). An UNTOUCHED <c>M</c>/<c>W</c> is captured as content too (harmlessly re-written
    /// on the cold abort path); an untouched <c>G</c>/<c>P</c> keeps its pointer via <c>KeepValue</c>.</summary>
    private bool TryCaptureRecordImage(string path, int recIndex, VfpSession.WorkArea wa,
                                       IReadOnlyList<string> replacedFields, out RecordImage image)
    {
        image = default;
        if (recIndex < 0 || recIndex >= wa.Table.RecordCount) return false;
        if (wa.Table.GetRecord(recIndex) is not { } rec) return false;   // deleted/unreadable ⇒ whole-file fallback.
        var cols = wa.Table.Columns;
        var vals = new object?[cols.Count];
        for (int i = 0; i < cols.Count; i++)
        {
            char t = cols[i].Type;
            if (t is 'M' or 'W')
                vals[i] = rec[i];                        // decoded CONTENT — round-trips into a fresh .fpt block.
            else if (t is 'G' or 'P')
            {
                // rec[i] is the block POINTER, not content — un-round-trippable. Safe to KEEP verbatim only
                // when UNtouched; a touched one needs the whole-file snapshot.
                if (FieldIn(replacedFields, cols[i].Name)) return false;
                vals[i] = DbfWriter.KeepValue;
            }
            else
                vals[i] = rec[i];
        }
        image = new RecordImage(path, recIndex, vals, wa.Table.IsRecordDeleted(recIndex));
        return true;
    }

    /// <summary>Case-insensitive membership test of <paramref name="name"/> in <paramref name="fields"/>.</summary>
    private static bool FieldIn(IReadOnlyList<string> fields, string name)
    {
        for (int i = 0; i < fields.Count; i++)
            if (string.Equals(fields[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Revert a REPLACE by rewriting the single affected record from its <see cref="RecordImage"/>
    /// pre-image through the cached writer (which re-maintains the structural cdx, reverting any key change),
    /// then refresh the read views. The record-level counterpart to <see cref="RestoreSnapshot"/> for the
    /// per-statement RI/candidate abort — parent-record-scoped, exactly what those two snapshots covered.</summary>
    private void RestoreRecordImage(RecordImage image)
    {
        using (var lease = LeaseWriter(image.Path))
        {
            var writer = lease.Writer;
            if (image.RecIndex >= 0 && image.RecIndex < writer.RecordCount)
            {
                writer.UpdateRecord(image.RecIndex, image.Values);
                if (image.Deleted) writer.Delete(image.RecIndex); else writer.Recall(image.RecIndex);
                writer.Flush();
            }
        }
        ReopenFileAreas(image.Path);   // a reverted key moved the cdx back ⇒ full reopen (cold abort path).
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null &&
           string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>After an out-of-band write to <paramref name="path"/>, refresh EVERY open work area riding
    /// that file — not just the one that wrote — and drop its record/order caches. VFP9 shares one buffer
    /// across all <c>USE..AGAIN</c> handles of a file, so a sibling handle (e.g. a riopen scratch cursor)
    /// must see the write immediately; without this it would serve stale bytes until reopened on its own.</summary>
    private void ReopenFileAreas(string path) => ReopenFileAreas(path, replacedFields: null);

    /// <summary>Refresh EVERY open work area riding <paramref name="path"/> (see the single-arg overload),
    /// with 5.5 TAG-SCOPED order-cache maintenance driven by <paramref name="replacedFields"/>:
    /// <list type="bullet">
    ///   <item><c>null</c> — a structural / count-changing write (APPEND, INSERT, PACK, index rebuild):
    ///     drop the cached ordered recno sequence so it rebuilds (legacy behaviour).</item>
    ///   <item>a field list — a REPLACE (or an empty list = a DELETE/RECALL flag flip): the on-disk cdx was
    ///     kept current incrementally (§5.1), so the cached ordered sequence + OrderPos are PRESERVED unless
    ///     a replaced field feeds the area's CONTROLLING order key (then that area's sequence is dropped).
    ///     Preserving it is what turns an ordered SCAN+REPLACE from O(n·filesize)/non-terminating into O(n):
    ///     the SCAN's next SKIP keeps walking the same sequence instead of re-reading the whole tag and
    ///     re-topping.</item>
    /// </list>
    /// The record cache (<c>Cached</c>) is always dropped (the row bytes changed); the CdxFile handle is
    /// always reopened, so a following SEEK / non-controlling-tag read still sees the maintained on-disk cdx.</summary>
    private void ReopenFileAreas(string path, IReadOnlyList<string>? replacedFields)
    {
        // Batch 4: a full reopen SUPERSEDES a deferred fast-append refresh for this file. Persist any pending
        // appends first (the cached writer buffered them) and drop the mark, so the reopen below sees the new
        // rows and a later lazy refresh does not reopen redundantly (or, worse, read a stale count).
        if (_pendingAppendPaths is { Count: > 0 })
        {
            string full = Path.GetFullPath(path);
            if (_pendingAppendPaths.Remove(full) && _cachedWriters.TryGetValue(full, out var pw))
            {
                try { pw.Flush(); } catch { /* best-effort */ }
            }
        }
        var areas = new List<int>();
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, path)) areas.Add(w.Area);
        foreach (var a in areas)
        {
            Session.ReopenAreaTable(a);
            if (!_meta.TryGetValue(a, out var mm)) continue;
            mm.Cached = null;
            if (replacedFields is null || OrderKeyTouchedBy(a, mm, replacedFields))
            {
                mm.Ordered = null; mm.OrderedFor = null; mm.OrderPos = -1;
            }
            // else: preserve mm.Ordered / mm.OrderedFor / mm.OrderPos — the incremental cdx left the
            // controlling order unchanged, so the cached sequence is still valid (no O(n) re-read).
        }
    }

    /// <summary>True when any field in <paramref name="replacedFields"/> feeds area <paramref name="a"/>'s
    /// CONTROLLING order key expression (so its cached ordered sequence must be dropped). A conservative
    /// substring/identifier scan of the tag's key expression: it may DROP a cache it could have kept
    /// (correct, just a re-read), never KEEP one it should drop. No controlling order ⇒ never touched.</summary>
    private bool OrderKeyTouchedBy(int a, AreaMeta mm, IReadOnlyList<string> replacedFields)
    {
        if (replacedFields.Count == 0) return false;               // a flag flip touches no key.
        if (mm.Ordered is null || string.IsNullOrEmpty(mm.OrderedFor)) return false; // nothing cached to keep.
        string? keyExpr = mm.OrderedKeyExpr;
        if (keyExpr is null) return true;                          // unknown key ⇒ be safe, drop.
        foreach (var f in replacedFields)
            if (IdentifierAppears(keyExpr, f)) return true;
        return false;
    }

    /// <summary>Whether identifier <paramref name="name"/> appears as a whole word (case-insensitive) in the
    /// index key <paramref name="expr"/> — an over-eager but never-under-eager field-in-key test.</summary>
    private static bool IdentifierAppears(string expr, string name)
    {
        if (name.Length == 0) return false;
        int i = 0;
        while ((i = expr.IndexOf(name, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk = i == 0 || !IsIdentChar(expr[i - 1]);
            int end = i + name.Length;
            bool rightOk = end >= expr.Length || !IsIdentChar(expr[end]);
            if (leftOk && rightOk) return true;
            i = end;
        }
        return false;
    }

    private static bool IsIdentChar(char c) => VfpIdentifier.IsPart(c);

    /// <summary>
    /// 5.5 write hot-path: refresh read views after an IN-PLACE write (REPLACE / DELETE / RECALL — the
    /// record COUNT is unchanged) that went through the cached writer.
    /// <list type="bullet">
    ///   <item>FAST PATH (autocommit + no structural cdx move): the cached writer already flushed the new
    ///     bytes to the shared file, so every open area riding it only needs its read BUFFER dropped
    ///     (<see cref="DbfTable.RefreshView"/>) + its current-record cache cleared — NO file re-open (the
    ///     ~8&#160;ms Defender-scanned open that dominated the pre-5.5 per-statement cost). The cached ordered
    ///     recno sequence + OrderPos are preserved: the on-disk structural cdx did not move (no tag key was
    ///     touched) and non-structural indexes are not writer-maintained (so they did not move either), so
    ///     the cached sequence still matches disk — this is what turns an ordered SCAN+REPLACE from
    ///     O(n·filesize)/non-terminating into O(n).</item>
    ///   <item>COLD PATH (a STRUCTURAL tag key was touched ⇒ the on-disk cdx moved, or an ADO.NET COW
    ///     transaction is active): fall back to the full <c>ReopenFileAreas</c>
    ///     so a following SEEK / ordered read sees fresh cdx pages. Cold because key-changing REPLACEs are
    ///     rare next to the value-field REPLACE hot loop.</item>
    /// </list>
    /// </summary>
    private void RefreshAfterInPlaceWrite(string path, VfpSession.WorkArea wa, IReadOnlyList<string> replacedFields)
    {
        if (Session.TxBeginWritePath is not null || AnyStructuralTagKeyTouchedBy(wa, replacedFields))
        {
            ReopenFileAreas(path, replacedFields);   // cold: cdx pages moved / tx copy — reopen handles fresh.
            return;
        }
        foreach (var w in Session.OpenAreas)
        {
            if (!SamePath(w.Table.SourcePath, path)) continue;
            w.Table.RefreshView();                    // drop the stale read buffer (cheap; no file open).
            if (_meta.TryGetValue(w.Area, out var mm)) mm.Cached = null;
            // mm.Ordered / mm.OrderedFor / mm.OrderPos PRESERVED — the on-disk index did not move.
        }
    }

    /// <summary>True when any field in <paramref name="replacedFields"/> feeds the KEY expression of ANY
    /// tag in the area's STRUCTURAL <c>.cdx</c> — i.e. the writer's incremental maintenance moved a cdx
    /// entry, so the read cdx handle + ordered caches are stale and must be re-opened. Structural-only: the
    /// writer maintains ONLY the structural cdx (extra <c>.idx</c>/<c>.cdx</c> are not writer-maintained, so
    /// they never move on a write). Reads the already-open in-memory tag directory (no file open).</summary>
    private static bool AnyStructuralTagKeyTouchedBy(VfpSession.WorkArea wa, IReadOnlyList<string> replacedFields)
    {
        if (replacedFields.Count == 0) return false;
        var cdx = wa.Cdx;
        if (cdx is null) return false;
        foreach (var tagName in cdx.TagNames)
        {
            var tag = cdx.Tag(tagName);
            if (tag is null) continue;
            string key = tag.KeyExpression ?? string.Empty;
            if (key.Length == 0) continue;
            foreach (var f in replacedFields)
                if (IdentifierAppears(key, f)) return true;
        }
        return false;
    }

    /// <summary>
    /// Engage the ADO.NET COPY-ON-WRITE transaction seam for a table the interpreter is about to write to
    /// DIRECTLY (via its own short-lived <see cref="DbfWriter"/>), returning the path the writer must open.
    /// <para>
    /// Inside a <c>FoxDbfTransaction</c> this lazily takes the table's private working copy on FIRST write —
    /// the SAME <c>TxBeginWritePath</c> seam the raw DML path uses via <c>OpenWritableTarget</c> — and repoints
    /// every open work area on the live file at the copy (read-your-writes), then returns the COPY path. From
    /// then on the caller's <c>path</c> is the copy, so EVERY downstream file touch in the same op — the
    /// pre-image snapshots (<see cref="CaptureSnapshot"/> for a trigger-abort revert, <see cref="SnapshotForTxn"/>
    /// for a PRG <c>BEGIN TRANSACTION</c> frame), the <see cref="DbfWriter"/>, the read-your-writes
    /// <c>ReopenFileAreas</c>, and any <see cref="RestoreSnapshot"/> — all operate on the copy. The two
    /// snapshot layers therefore compose rather than fight: the interpreter's own transaction/trigger-abort
    /// reverts happen WITHIN the copy, and the outer COW transaction commits or discards the copy as a whole.
    /// </para>
    /// <para>
    /// In autocommit (no active transaction — including EnforceRules=off and every direct-interpreter test)
    /// the live path is returned unchanged and no area is reopened, so the write path is byte-identical.
    /// </para>
    /// </summary>
    private string BeginTxWrite(string path)
    {
        if (Session.TxBeginWritePath is null) return path;   // autocommit — unchanged.
        string writePath = Session.RedirectWritePath(path);  // live → private copy (idempotent for a copy path).
        if (!SamePath(writePath, path))
        {
            // The write now lands on the tx's private copy; a writer this interpreter cached on the LIVE
            // path (before the tx) must not linger — the Data layer swaps the copy over the live file on
            // commit/rollback, and a live-path handle would block that. Drop it (tx writes never cache).
            InvalidateCachedWriter(path);
            ReopenFileAreas(path);                            // repoint the live-riding area(s) at the copy.
        }
        return writePath;
    }

}
