using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// microVFP WRITE HOT-PATH accelerators (project-review 5.5). Four caches that turn a per-statement
/// SCAN+REPLACE / APPEND loop from O(n · filesize) into O(n):
/// <list type="number">
///   <item>a CACHED <see cref="DbfWriter"/> per open <c>.dbf</c> path — the writer is kept OPEN across
///     statements instead of re-opened (open+lock+write+flush+dispose) per REPLACE/DELETE. Opening a
///     <c>.dbf</c> read/write is the dominant cost (≈8&#160;ms measured vs ≈80&#160;µs for update+flush on an
///     already-open writer); caching removes it. The writer holds a <see cref="LockMode.Shared"/>
///     (<see cref="FileShare.ReadWrite"/>) handle — the SAME share the transient per-write writer took —
///     and NO byte-range lock is held between statements (they are per-mutation, released in
///     <c>WithLock</c>'s finally), so a concurrent VFP's RLOCK/FLOCK/USE behaviour is unchanged. The
///     area's READ handles are still refreshed by <c>ReopenFileAreas</c> after every write, so sibling
///     (USE..AGAIN) visibility is byte-for-byte identical.</item>
///   <item>a bounded PARSE cache for <see cref="Execute(string)"/> — the same snippet source re-lexes once.</item>
///   <item>a bounded compiled-EXPRESSION cache — a repeated DBC RULE/DEFAULT / enforced-write per-row
///     expression parses once and re-evaluates against the moving row.</item>
/// </list>
/// The incremental ordered-cache maintenance (point 3 of 5.5) lives in <c>VfpInterpreter.Navigation</c>
/// and <c>VfpInterpreter.Dml</c>; the record-level RI pre-image (point 2) in <c>VfpInterpreter.Ri</c>.
/// <para>
/// CACHING IS AUTOCOMMIT / PRG-TRANSACTION ONLY. Inside an ADO.NET copy-on-write transaction
/// (<see cref="VfpSession.TxBeginWritePath"/> set) the write path is a redirected private copy the Data
/// layer swaps on commit/rollback, so a persistent handle could straddle a file swap — there we fall back
/// to the transient per-write writer (byte-identical to the pre-5.5 path). A PRG <c>BEGIN TRANSACTION</c>
/// (the <c>_txn</c> snapshot stack) DOES cache: the cached writer is disposed before any whole-file
/// <see cref="RestoreSnapshot"/> rewrite so the <c>File.Copy</c> from the temp pre-image is never blocked.
/// </para>
/// </summary>
public sealed partial class VfpInterpreter
{
    // Full-path → the persistent Shared writer open on that .dbf (autocommit / PRG-txn). Keyed by path (not
    // area) so USE..AGAIN siblings share ONE writer and its live _recordCount, and so a write through any
    // area on the file goes through the same handle. OrdinalIgnoreCase to match the session's path compares.
    // Per-DATA-SESSION (5.14): the cached writers carry the session's held byte-range locks, so they are
    // swapped with the data session; non-readonly so a SET DATASESSION switch re-points it (and releasing a
    // session disposes ITS writers, freeing the locks for another session to take).
    private Dictionary<string, DbfWriter> _cachedWriters = new(StringComparer.OrdinalIgnoreCase);

    // Bounded parse/expression caches (short-lived session; a full Clear on overflow keeps them bounded
    // without LRU bookkeeping — a repeated source/expression stays hot, a stream of distinct ones just
    // churns without leaking). No global static state.
    private readonly Dictionary<string, PrgProgram> _parseCache = new(StringComparer.Ordinal);
    private const int ParseCacheCap = 512;
    private readonly Dictionary<string, VfpExpression> _exprCache = new(StringComparer.Ordinal);
    private const int ExprCacheCap = 2048;

    /// <summary>A writer to use for one statement's write to <paramref name="path"/>. In autocommit /
    /// PRG-transaction the returned lease wraps the PERSISTENT cached writer (its <see cref="WriterLease.Dispose"/>
    /// is a no-op — the writer stays open); inside an ADO.NET copy-on-write transaction it wraps a FRESH
    /// transient writer the lease disposes (the pre-5.5 behaviour). Callers still <c>Flush()</c> after the
    /// write, so persistence is identical either way.</summary>
    private WriterLease LeaseWriter(string path)
    {
        if (Session.TxBeginWritePath is null)
            return new WriterLease(GetOrOpenCachedWriter(path), dispose: false);
        // ADO.NET COW transaction: the Data layer owns the private-copy file lifecycle — never straddle it
        // with a persistent handle.
        return new WriterLease(OpenWriter(path), dispose: true);
    }

    private readonly struct WriterLease : IDisposable
    {
        public DbfWriter Writer { get; }
        private readonly bool _dispose;
        public WriterLease(DbfWriter writer, bool dispose) { Writer = writer; _dispose = dispose; }
        public void Dispose() { if (_dispose) Writer.Dispose(); }
    }

    private DbfWriter GetOrOpenCachedWriter(string path)
    {
        RequireCompactIndexWriteContext(path);
        string key = Path.GetFullPath(path);
        if (_cachedWriters.TryGetValue(key, out var w))
        {
            w.SetOpenAdditionalIndexes(OpenAdditionalIndexPaths(path), _ctx);
            return w;
        }
        var writer = OpenWriter(path);
        _cachedWriters[key] = writer;
        return writer;
    }

    /// <summary>A Shared writer on <paramref name="path"/> that presents the column names this session READS
    /// the table under (<see cref="ReadColumnsFor"/>; a DBC member: its long field names), like the SQL DML
    /// writer (<see cref="VfpSession.OpenWritableTarget"/>). VFP9 stores the structural <c>.cdx</c> tag KEY
    /// text with the long names (<c>CUSTOMER_NAME</c>); with the 10-char physical names the writer finds such
    /// a tag un-derivable and deletes the <c>.cdx</c>, and the INSERT fast path cannot map a long column name.
    /// Writes stay positional.</summary>
    private DbfWriter OpenWriter(string path)
    {
        RequireCompactIndexWriteContext(path);
        var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared, SessionCodePage = SessionCodePage }, ReadNamesFor(path));
        try
        {
            writer.IndexContext = _ctx;
            writer.SetOpenAdditionalIndexes(OpenAdditionalIndexPaths(path), _ctx);
            return writer;
        }
        catch { writer.Dispose(); throw; }
    }

    private void RequireCompactIndexWriteContext(string path)
    {
        // PRG snapshots include every open additional index, including compact IDX.
        // ADO.NET COW and candidate/RI rollback retain their separate write restrictions.
        var paths = OpenAdditionalIndexPaths(path);
        if (paths.Count > 0 && (Session.TxBeginWritePath is not null
            || CandidateTagsFor(path) is not null || EnforceReferentialIntegrity))
            throw new MicroVfpRuntimeException("Writing with open standalone indexes is not supported in transactions or with candidate/RI rollback enforcement.");
    }

    private IReadOnlyList<string> OpenAdditionalIndexPaths(string path)
    {
        List<string>? paths = null;
        HashSet<string>? seen = null;
        foreach (var area in Session.OpenAreas)
        {
            // Most writes only have a structural CDX. Inspect the live inventory so USE/SET INDEX,
            // sibling areas and data-session switches need no separate cache invalidation protocol.
            if (!_meta.TryGetValue(area.Area, out var meta) || meta.ExtraIndexes is not { Count: > 0 } extra
                || !SamePath(area.Table.SourcePath, path))
                continue;
            foreach (string file in extra)
            {
                string full = Path.GetFullPath(file);
                if (!(seen ??= new(StringComparer.OrdinalIgnoreCase)).Add(full)) continue;
                (paths ??= new()).Add(full);
            }
        }
        return paths is null ? Array.Empty<string>() : paths;
    }

    /// <summary>True when any open area on the table at <paramref name="path"/> (compared copy→live, so an area
    /// riding a transaction's private copy counts) has a standalone index open.</summary>
    private bool HasAdditionalIndexesOnTable(string path)
    {
        string Live(string p) => Path.GetFullPath(Session.TxLivePath?.Invoke(p) ?? p);
        string target = Live(path);
        foreach (var area in Session.OpenAreas)
            if (_meta.TryGetValue(area.Area, out var meta) && meta.ExtraIndexes is { Count: > 0 }
                && area.Table.SourcePath is string source
                && string.Equals(Live(source), target, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>The column-name view of <see cref="OpenWriter"/> for <paramref name="path"/>. The EXCLUSIVE
    /// rewriters (INDEX / REINDEX / PACK / ZAP) close the areas on the file first, so they capture it before.</summary>
    private Func<IReadOnlyList<DbfColumn>, IReadOnlyList<DbfColumn>>? ReadNamesFor(string path)
        => VfpSession.NamesOf(ReadColumnsFor(path));

    /// <summary>An Exclusive writer on <paramref name="path"/> under <paramref name="names"/> (from
    /// <see cref="ReadNamesFor"/>, taken while the areas were still open): a tag built, rebuilt or kept by the
    /// rewrite derives its KEY / FOR from the names the session reads the table under — a DBC member's long
    /// names, which VFP9 writes into the tag KEY text.</summary>
    private DbfWriter OpenExclusiveWriter(string path, Func<IReadOnlyList<DbfColumn>, IReadOnlyList<DbfColumn>>? names)
    {
        var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Exclusive, SessionCodePage = SessionCodePage }, names);
        writer.IndexContext = _ctx;
        return writer;
    }

    /// <summary>The columns <paramref name="path"/> is read under: those of a work area open on the file, else
    /// those of the open database's member stored there; empty (physical names) for a free table.</summary>
    private IReadOnlyList<DbfColumn> ReadColumnsFor(string path)
    {
        foreach (var a in Session.OpenAreas)
            if (SamePath(a.Table.SourcePath, path)) return a.Table.Columns;
        if (Session.Database is { } db)
        {
            foreach (var name in db.TableNames)
            {
                if (!SamePath(db.GetTablePath(name), path)) continue;
                try { using var t = db.OpenTable(name); return t.Columns; }
                catch { break; }
            }
        }
        return Array.Empty<DbfColumn>();
    }

    // ── Batch 4: DEFERRED direct-append read-view refresh ─────────────────────────────────────────────
    // Full .dbf paths whose fast-path INSERT appends have NOT yet been reflected in the open read views. A
    // direct append lands the row through the cached writer (its live RecordCount grows) but a work area's
    // DbfTable read view has a record count FIXED at open, so the new row is invisible until the area is
    // reopened. Reopening per row would eat the win, so we DEFER: mark here, then Flush()+reopen lazily at the
    // next READ (Exec statement boundary for a non-INSERT, RECCOUNT/RECNO/EOF/… via AreaArg, a field read via
    // ResolveName, or GO/SKIP/SEEK nav). Null until the first fast append — the common no-append path pays a
    // single null check.
    private HashSet<string>? _pendingAppendPaths;

    private void MarkAppendPending(string fullPath)
        => (_pendingAppendPaths ??= new(StringComparer.OrdinalIgnoreCase)).Add(fullPath);

    /// <summary>Flush EVERY deferred direct-append refresh (the statement-boundary sweep in <c>Exec</c> for a
    /// non-INSERT statement, so a run of fast INSERTs reopens the view ONCE — here — not per row).</summary>
    private void FlushPendingAppends()
    {
        if (_pendingAppendPaths is not { Count: > 0 } pend) return;
        foreach (var p in new List<string>(pend)) RefreshDirectAppends(p);   // snapshot: RefreshDirectAppends mutates the set.
    }

    /// <summary>Flush the deferred refresh for the file backing work area <paramref name="area"/> only — used
    /// by the function/field/nav read choke points that observe one specific area.</summary>
    private void FlushPendingAppends(int area)
    {
        if (_pendingAppendPaths is not { Count: > 0 }) return;
        if (Session.AreaAt(area)?.Table.SourcePath is { } sp) RefreshDirectAppends(Path.GetFullPath(sp));
    }

    /// <summary>Persist (<c>Flush</c>) the deferred fast-path appends for <paramref name="path"/> — EVEN WHEN
    /// the cached writer holds an explicit RLOCK/FLOCK byte-range lock — and drop the pending mark, WITHOUT
    /// reopening the read view. Called at the head of an INSERT that FALLS BACK to <see cref="VfpSession.Execute"/>
    /// (FROM ARRAY/MEMVAR, an unknown column, RI, candidate, a read-only session): that route re-enters
    /// <see cref="VfpSession.OpenWritableTarget"/>, which opens a SECOND writer that reads the on-disk record
    /// count. A prior fast append buffered a row in the cached writer WITHOUT a per-row <c>Flush</c> (its
    /// on-disk count lags), and <see cref="InvalidateCachedWriter"/> PINS (does not drop) that writer while it
    /// holds a lock (finding 5.13) — so the second writer would read a STALE count and append OVER the
    /// un-persisted row (record-count divergence / row clobbering). Flushing first makes the on-disk count
    /// correct before the foreign writer opens; the foreign write's own reopen then refreshes the read view, so
    /// this deliberately does NOT reopen here (and, unlike <see cref="InvalidateCachedWriter"/>, does NOT
    /// dispose the writer — the held lock must outlive the flush). Mirrors the <c>ReopenFileAreas</c>
    /// preamble. No-op when the path is not pending.</summary>
    private void PersistPendingAppends(string? path)
    {
        if (path is null || _pendingAppendPaths is not { Count: > 0 }) return;
        string full = Path.GetFullPath(path);
        if (_pendingAppendPaths.Remove(full) && _cachedWriters.TryGetValue(full, out var w))
        {
            try { w.Flush(); } catch { /* best-effort — a flush failure surfaces on the next real write */ }
        }
    }

    /// <summary>Make the deferred fast-path appends on <paramref name="fullPath"/> visible: persist them (the
    /// cached writer buffered the rows without a per-row Flush so the <c>.dbf</c> stream + the incremental
    /// <c>.cdx</c> accelerator stayed hot across the run), then reopen the read view of every area riding the
    /// file so the new rows appear (DbfTable.RecordCount is fixed at open). Mirrors the OLD Session.Execute →
    /// ReopenArea round-trip's observable state: the handle is refreshed and the current-record cache dropped,
    /// while the record pointer is preserved and record/order/key-range caches are dropped so count-changing
    /// appends rebuild ordered navigation lazily. Idempotent; a no-op when the path is not pending.</summary>
    private void RefreshDirectAppends(string fullPath)
    {
        if (_pendingAppendPaths is null || !_pendingAppendPaths.Remove(fullPath)) return;
        if (_cachedWriters.TryGetValue(fullPath, out var w))
        {
            try { w.Flush(); } catch { /* best-effort — a flush failure surfaces on the next real write */ }
        }
        var areas = new List<int>();
        foreach (var a in Session.OpenAreas)
            if (SamePath(a.Table.SourcePath, fullPath)) areas.Add(a.Area);
        foreach (var a in areas)
        {
            Session.ReopenAreaTable(a);
            if (_meta.TryGetValue(a, out var mm))
            {
                mm.Cached = null; mm.CachedRec = -1;
                mm.Ordered = null; mm.OrderedFor = null; mm.OrderPos = -1; mm.KeyVisible = null;
            }
        }
    }

    /// <summary>Dispose + drop the cached writer for <paramref name="path"/> (if any). MUST run before any
    /// out-of-band rewrite/truncate of the file (whole-file <see cref="RestoreSnapshot"/>, PACK, an
    /// INDEX/REINDEX exclusive re-open) and after a foreign writer (SQL DML append) changed the record count
    /// under it, so a stale handle / cached <c>_recordCount</c> can never survive.</summary>
    private void InvalidateCachedWriter(string? path)
    {
        if (path is null) return;
        string key = Path.GetFullPath(path);
        if (!_cachedWriters.TryGetValue(key, out var w)) return;
        // 5.13: an explicit RLOCK/FLOCK lives on this handle. Disposing it here would SILENTLY drop the
        // byte-range lock a stored proc is coordinating with — pin it instead. The lock is released only by
        // UNLOCK / area close / CLEAR ALL / session dispose (which route through ReleaseAreaLocks/-AllLocks
        // BEFORE disposal). A destructive rewrite that needed this invalidation cannot proceed while records
        // are locked anyway — exactly as VFP refuses PACK/REINDEX on a shared table with held locks.
        if (w.HasHeldLocks) return;
        if (_cachedWriters.Remove(key, out w))
        {
            try { w.Dispose(); } catch { /* best-effort release */ }
        }
    }

    /// <summary>
    /// Force-dispose the cached writer for <paramref name="path"/> EVEN WHEN it holds an explicit
    /// RLOCK/FLOCK byte-range lock — for an OUT-OF-BAND whole-file rewrite (a rollback
    /// <see cref="RestoreSnapshot"/> / CANDIDATE-index <c>RollbackFiles</c> / an EXCLUSIVE index rebuild)
    /// that DEPENDS on the OS handle actually closing: a pinned handle would make the whole-file
    /// <see cref="File.Copy(string, string, bool)"/> from the temp pre-image (its FileShare.None destination
    /// open) or an <see cref="LockMode.Exclusive"/> reopen (FileShare.None) throw a sharing violation — which
    /// the rewrite's <c>catch{}</c> would then
    /// SWALLOW, silently no-op'ing the revert (finding 5.13). Unlike <see cref="InvalidateCachedWriter"/>
    /// (whose <see cref="DbfWriter.HasHeldLocks"/> pin is correct ONLY for cached-writer churn), this closes
    /// the handle so the rewrite can proceed; <see cref="DbfWriter.Dispose"/> releases the OS locks. The
    /// interpreter still TRACKS the lock in <c>_areaLocks</c>, so the caller pairs this with
    /// <see cref="ReacquireHeldLocks"/> AFTER the rewrite + area reopen to re-take the lock on a fresh handle —
    /// the explicit lock stays logically held across the rewrite (never silently dropped).
    /// </summary>
    private void ForceCloseCachedWriter(string? path)
    {
        if (path is null) return;
        string key = Path.GetFullPath(path);
        if (_cachedWriters.Remove(key, out var w))
        {
            try { w.Dispose(); } catch { /* best-effort — Dispose releases held OS byte-range locks */ }
        }
    }

    /// <summary>Re-take, on a FRESH cached writer, the explicit byte-range locks the interpreter still tracks
    /// in <c>_areaLocks</c> for <paramref name="path"/> — the second half of the
    /// <see cref="ForceCloseCachedWriter"/> out-of-band-rewrite protocol, run AFTER the rewrite + area reopen.
    /// Best-effort per lock: should a foreign process have grabbed the byte in the tiny release→reacquire
    /// window the re-take throws and is swallowed (the record is then genuinely not ours — the same net state
    /// VFP reaches when it loses a byte). A whole-file (FLOCK) set re-takes only the file lock (it superseded
    /// its records when acquired); a record set re-takes its record/header bytes.</summary>
    private void ReacquireHeldLocks(string? path)
    {
        if (path is null || _areaLocks.Count == 0) return;
        string key = Path.GetFullPath(path);
        DbfWriter? w = null;
        foreach (var set in _areaLocks.Values)
        {
            if (!set.Any || !SamePath(set.Path, key)) continue;
            w ??= GetOrOpenCachedWriter(set.Path);
            if (set.File)
            {
                try { w.LockFile(); } catch { /* lost the byte to a foreign holder — best-effort */ }
            }
            else
            {
                foreach (var rec in set.Records)
                    try { if (rec == 0) w.LockHeader(); else w.Lock(rec); }
                    catch { /* lost the byte to a foreign holder — best-effort */ }
            }
        }
    }

    /// <summary>Dispose + drop every cached writer whose path is no longer riding an OPEN work area — the
    /// post-USE/close prune, so closing (or repurposing) the last area on a file releases its writer handle
    /// (the WriterReleased* pins: after USE/switch the <c>.dbf</c> must be FileShare.None-openable).</summary>
    private void PruneCachedWriters()
    {
        if (_cachedWriters.Count == 0) return;
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wa in Session.OpenAreas)
            if (wa.Table.SourcePath is { } sp) live.Add(Path.GetFullPath(sp));
        List<string>? drop = null;
        foreach (var (key, w) in _cachedWriters)
            // 5.13: never prune a writer that still holds an explicit RLOCK/FLOCK — the byte-range lock must
            // outlive cached-writer churn (it is released by UNLOCK / close via ReleaseAreaLocks first, which
            // drops the lock before the area leaves OpenAreas, so a genuinely closed file still prunes).
            if (!live.Contains(key) && !w.HasHeldLocks) (drop ??= new()).Add(key);
        if (drop is null) return;
        foreach (var key in drop)
            if (_cachedWriters.Remove(key, out var w))
            {
                try { w.Dispose(); } catch { /* best-effort */ }
            }
    }

    /// <summary>Dispose EVERY cached writer (CLEAR ALL, and the session-disposing hook).</summary>
    private void DisposeAllCachedWriters()
    {
        foreach (var w in _cachedWriters.Values)
        {
            try { w.Dispose(); } catch { /* best-effort */ }
        }
        _cachedWriters.Clear();
    }

    // ── 6.3 rollback pre-images stream to TEMP FILES (not byte[] in RAM) ──────────────────────────────────
    // The snapshot temp dir. ONE per interpreter (shared across data sessions — temp names are unique), created
    // lazily on the first snapshot and deleted whole on session/interpreter dispose (a catch-all: each
    // FileSnapshot.Cleanup already removes its own temps after commit/rollback, so the dir never grows across a
    // long session). Null until the first CaptureSnapshot.
    private string? _snapshotTempDir;

    private string SnapshotTempDir => _snapshotTempDir ??= CreateSnapshotTempDir();

    private static string CreateSnapshotTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cvfoxdbf-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Delete the whole snapshot temp dir — the session/interpreter-dispose catch-all (best-effort).</summary>
    private void DeleteSnapshotTempDir()
    {
        if (_snapshotTempDir is null) return;
        try { if (Directory.Exists(_snapshotTempDir)) Directory.Delete(_snapshotTempDir, recursive: true); }
        catch { /* best-effort — a locked temp is dropped by the OS temp sweep */ }
        _snapshotTempDir = null;
    }

    /// <summary>Test-only (6.3): the count of live rollback pre-image temp files in the snapshot temp dir
    /// (0 before the first snapshot, or after the dir was deleted). A commit/rollback must leave this at 0 —
    /// the guard that snapshots stream to files and are cleaned up (no orphan growth across a session).</summary>
    internal int SnapshotTempFileCount()
        => _snapshotTempDir is not null && Directory.Exists(_snapshotTempDir)
            ? Directory.GetFiles(_snapshotTempDir).Length : 0;

    /// <summary>Stream <paramref name="source"/> into a fresh temp file for a rollback pre-image and return the
    /// temp path — or null when <paramref name="source"/> is ABSENT (fail-soft; a null on restore deletes any
    /// companion that appeared, the <c>.dbf</c> is left untouched). Reads with <see cref="FileShare.ReadWrite"/>
    /// so the copy can run WHILE a 5.5 cached writer holds the source open (a plain <c>File.Copy</c> requests
    /// FileShare.Read, which a live read/write handle denies). 6.3: a copy failure on an EXISTING file THROWS —
    /// a capture I/O failure must fail the write/txn honestly, never silently disable rollback (the old
    /// <c>ReadAllBytesShared</c> swallowed it → a null pre-image → a no-op restore). A partial temp from a
    /// mid-copy throw is deleted before rethrow so a failed capture leaks nothing.</summary>
    private string? CopyToTempSnapshot(string source)
    {
        if (!File.Exists(source)) return null;
        Directory.CreateDirectory(SnapshotTempDir);   // survive an external wipe of the dir mid-session.
        string temp = Path.Combine(SnapshotTempDir, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        try
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort partial cleanup */ }
            throw;
        }
        return temp;
    }

    // ── parse / expression caches (pathology 4: re-lex per Execute, re-parse per row) ──

    /// <summary>Parse <paramref name="source"/> to a <see cref="PrgProgram"/>, reusing a cached AST for an
    /// identical source string (the AST is immutable data the tree-walk only READS, so reuse is safe and
    /// also warms each <c>PrgExpr</c>'s lazily-parsed expression). Bounded.</summary>
    private PrgProgram ParseProgramCached(string source)
    {
        if (_parseCache.TryGetValue(source, out var prog)) return prog;
        prog = PrgParser.Parse(source);
        if (_parseCache.Count >= ParseCacheCap) _parseCache.Clear();
        _parseCache[source] = prog;
        return prog;
    }

    /// <summary>Parse <paramref name="normalized"/> (already run through <see cref="MicroVfpExprRewrite.Normalize"/>)
    /// to a reusable <see cref="VfpExpression"/>, cached by the normalized text so a DBC RULE/DEFAULT or an
    /// enforced-write per-row predicate parses once and re-evaluates against the moving row. Bounded.</summary>
    private VfpExpression ParseExpressionCached(string normalized)
    {
        if (_exprCache.TryGetValue(normalized, out var expr)) return expr;
        expr = VfpExpression.Parse(normalized);
        if (_exprCache.Count >= ExprCacheCap) _exprCache.Clear();
        _exprCache[normalized] = expr;
        return expr;
    }
}
