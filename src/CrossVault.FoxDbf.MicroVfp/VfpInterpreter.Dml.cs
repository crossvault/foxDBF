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

/// <summary>microVFP DML + whole-table/record commands — REPLACE/APPEND/INSERT/DELETE/GATHER/SCATTER and PACK/COPY/TOTAL/RENAME/...</summary>
public sealed partial class VfpInterpreter
{
    /// <summary>Every route below (direct append, buffered, CANDIDATE, RI, the SQL DML fallback) fills a DBC
    /// member's omitted columns from their field DEFAULTs, as VFP9 does: the session's
    /// <see cref="VfpSession.DbcDefaultEvaluator"/> is this interpreter for the statement's duration. The
    /// previous evaluator is restored afterwards, so an INSERT inside a DEFAULT's UDF nests.</summary>
    private void ExecInsert(InsertStmt ins)
    {
        var previous = Session.DbcDefaultEvaluator;
        var previousValues = Session.InsertValueEvaluator;
        Session.DbcDefaultEvaluator = _dbcDefaultEvaluator ??= EvalDbcDefault;
        Session.InsertValueEvaluator = _insertValueEvaluator ??= expression =>
        {
            string normalized = MicroVfpExprRewrite.Normalize(expression.Text);
            // The SQL parser already produced this immutable AST. Only array/reference
            // rewrites require a different tree; evaluation still uses the live PRG context.
            return (normalized == expression.Text ? expression : ParseExpressionCached(normalized)).Evaluate(_row, _ctx);
        };
        try
        {
            ExecInsertCore(ins);
            if (ResolveInsertStatement(ins) is { } statement)
            {
                var wa = Session.FindInsertArea(statement.Table);
                if (wa is null)
                {
                    // INSERT opens a closed target in a free area, retaining the selected area.
                    Session.Use(statement.Table, inArea: 0);
                    wa = Session.FindInsertArea(statement.Table)!;
                }
                int count = EffCount(wa.Area, wa);
                if (wa.Table.SourcePath is { } path && _cachedWriters.TryGetValue(Path.GetFullPath(path), out var writer))
                    count = Math.Max(count, writer.RecordCount);
                PositionPointer(Meta(wa.Area), null, count);
            }
        }
        finally
        {
            Session.DbcDefaultEvaluator = previous;
            Session.InsertValueEvaluator = previousValues;
        }
    }

    private Action<string, IReadOnlyList<(string Field, string Expression)>, Dictionary<string, object?>>? _dbcDefaultEvaluator;
    private Func<VfpExpression, VfpValue>? _insertValueEvaluator;

    private sealed record DefaultRow(int Area, int RecNo, Dictionary<string, object?> Values, DefaultRow? Previous, bool AppendBlank);
    private DefaultRow? _defaultRow;
    private bool _appendBlankDefaults;

    private DefaultRow? DefaultRowFor(int area)
    {
        for (var row = _defaultRow; row is not null; row = row.Previous)
            if (row.Area == area) return row;
        return null;
    }

    /// <summary>The DBC field DEFAULTs of one row inserted into <paramref name="table"/>, evaluated in field
    /// order with the target as the current work area, as VFP9 does (probed: <c>ALIAS()</c> inside a DEFAULT
    /// UDF is the target's) — opened in a free area for the evaluation when it is not open, closed again
    /// after. The previous current area is restored. Unlike <see cref="EvalExpression"/> an error propagates
    /// (VFP raises it and writes no row); a UDF runs from the loaded stored procedures.</summary>
    private void EvalDbcDefault(string table, IReadOnlyList<(string Field, string Expression)> defaults,
        Dictionary<string, object?> values)
    {
        bool appendBlank = _appendBlankDefaults;
        _appendBlankDefaults = false; // A nested INSERT in a DEFAULT UDF retains INSERT's own context.
        int saved = Session.CurrentArea;
        int opened = 0;
        try
        {
            var wa = Session.FindInsertArea(table);
            if (wa is null)
            {
                Session.SelectArea(0);
                Session.Use(table);
                wa = Session.AreaAt(Session.CurrentArea)
                    ?? throw new MicroVfpRuntimeException($"Cannot open INSERT target '{table}'.", 1);
                opened = wa.Area;
            }
            FlushPendingAppends(wa.Area);
            wa = Session.AreaAt(wa.Area)!;
            Session.SelectArea(wa.Area);
            var meta = Meta(wa.Area);
            var savedPosition = (meta.RecNo, meta.Eof, meta.Bof);
            var previous = _defaultRow;
            // VFP evaluates against the new row: supplied VALUES and earlier DEFAULTs are visible,
            // and RECCOUNT includes it. Keep that row in memory so a failed DEFAULT cannot half-append.
            int recno = EffCount(wa.Area, wa) + 1;
            _defaultRow = new DefaultRow(wa.Area, recno, values, previous, appendBlank);
            meta.RecNo = recno;
            meta.Eof = meta.Bof = false;
            try
            {
                foreach (var (field, expression) in defaults)
                    values[field] = ParseExpressionCached(MicroVfpExprRewrite.Normalize(expression)).Evaluate(_row, _ctx).ToClr();
            }
            finally
            {
                _defaultRow = previous;
                (meta.RecNo, meta.Eof, meta.Bof) = savedPosition;
            }
        }
        finally
        {
            if (opened > 0 && Session.AreaAt(opened) is not null) { Session.CloseArea(opened); _meta.Remove(opened); }
            Session.SelectArea(saved);
        }
    }

    private void ExecInsertCore(InsertStmt ins)
    {
        // An INSERT the SQL parser cannot represent used to fall through every path below into a swallowed
        // Session.Execute — the row silently never landed. Raise the parser's error instead (catchable).
        if (ins.Parsed is null && ResolveInsertStatement(ins) is null) ThrowSqlParseError(ins.Sql);
        if (ResolveInsertStatement(ins) is { SourceKind: InsertSourceKind.Select } select)
        {
            ExecSelectImport(select);
            return;
        }
        long checkStart = VfpInsertProfile.Start();
        // P3b: an INSERT on a DBC member with a bound insert trigger (the RI insert RESTRICT rule) is
        // ENFORCED atomically — the row lands, the bound __RI_INSERT_<table> trigger fires positioned ON
        // the new record, and a .F. return (a missing parent key) ROLLS THE INSERTED ROW BACK, exactly as
        // VFP9 blocks such an INSERT. The target table is resolved from the PARSED statement (NOT the
        // current area — INSERT-SQL need not have selected onto the new record). When RI is off / the
        // table is free / it has no bound insert trigger, perform a plain insert with no RI enforcement.
        // BUFFERING (mode>1): defer the append into the target area's buffer (visible on the live cursor,
        // flushed at TABLEUPDATE for table buffering, or on the next pointer move for row buffering 2/3).
        // Gated on _anyBuffering so the default write-through INSERT path (and every Buffering=1 test) pays
        // only a bool check. Skipped while replaying at commit.
        if (_anyBuffering && !_inBufferCommit
            && ResolveInsertStatement(ins) is { } bufStmt
            && Session.FindInsertArea(bufStmt.Table) is { } bufWa
            && _meta.TryGetValue(bufWa.Area, out var bufMeta) && bufMeta.Buffering > 1)
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.BufferingRiChecks, checkStart);
            // INSERT moves off the preceding row, so row buffering must commit it before the new append.
            MaybeAutoCommitRow(bufWa.Area);
            bufWa = Session.AreaAt(bufWa.Area)!;
            BufferAppend(bufWa, Meta(bufWa.Area), ins, bufStmt);
            return;
        }
        VfpInsertProfile.Stop(VfpInsertProfileBucket.BufferingRiChecks, checkStart);

        long targetStart = VfpInsertProfile.Start();
        var parsed = ResolveInsertStatement(ins);   // reuse InsertStmt.Parsed; NEVER re-parse per row.
        string? table = parsed?.Table;

        // ── DIRECT-APPEND FAST PATH (Batch 4) ──────────────────────────────────────────────────────────
        // A PLAIN autocommit INSERT … VALUES into an OPEN, unbuffered, non-candidate, no-RI-insert-trigger
        // table appends straight through the interpreter's PERSISTENT cached writer — no per-row SQL re-parse
        // (the parsed statement is reused) and no per-row work-area close+reopen (the old Session.Execute →
        // OpenWritableTarget round-trip, measured at ~8–11 ms/row, IS entirely that open+reopen). The read-view
        // refresh is DEFERRED and flushed lazily on the next read (RECCOUNT/GO/SEEK/SCAN/field), so a run of
        // appends reopens ONCE, not per row. ANY anomaly (no open area, buffered, candidate, bound RI insert
        // trigger, COW redirect, unknown column / count mismatch, a VALUES-eval error) FALLS THROUGH to the old
        // route below, so error text/numbers, the RI enforcement and the COW isolation all stay byte-identical.
        if (parsed is { SourceKind: InsertSourceKind.Values }
            && table is not null
            && _txn.Count == 0
            && Session.TxBeginWritePath is null
            && TryDirectAppendInsert(parsed))
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.InsertTargetColumnResolution, targetStart);
            return;
        }

        // The fallback uses a session-owned SQL writer with no standalone-index inventory.
        if (ResolveInsertTablePath(table) is string indexedPath
            && OpenAdditionalIndexPaths(indexedPath).Count > 0)
            throw new MicroVfpRuntimeException("This INSERT path cannot maintain open standalone indexes.");

        // A write-through INSERT appends through the SQL DML writer (OpenWritableTarget), NOT the cached
        // interpreter writer — that foreign append bumps the on-disk record count under any writer this
        // interpreter has cached on the same table, so drop it here (a stale cached _recordCount would
        // otherwise reject a later REPLACE of the appended row). Cheap; INSERT is not the hot gate path.
        if (table is not null)
        {
            var targetPath = Session.FindInsertArea(table)?.Table.SourcePath;
            // Batch 4: this INSERT is about to fall back to Session.Execute → OpenWritableTarget, a SECOND
            // writer that reads the on-disk record count. Persist any DEFERRED fast appends on this file FIRST —
            // unconditionally, even under a held RLOCK/FLOCK — so the foreign writer never reads a stale count
            // and clobbers an un-persisted fast-appended row (InvalidateCachedWriter below PINS the lock-held
            // writer and would leave the row un-flushed; the foreign write's own reopen refreshes the view).
            PersistPendingAppends(targetPath);
            InvalidateCachedWriter(targetPath);
        }

        // CANDIDATE enforcement: if the target table has a tag created CANDIDATE, an INSERT that duplicates
        // one of its keys must RAISE (VFP). The row is written (funnelling the incremental maintenance),
        // then each candidate tag is re-checked; a violation rolls the write back + raises.
        if (table is not null
            && Session.FindInsertArea(table)?.Table.SourcePath is string candPath
            && CandidateTagsFor(candPath) is not null)
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.InsertTargetColumnResolution, targetStart);
            long indexStart = VfpInsertProfile.Start();
            string writePath = Path.GetFullPath(BeginTxWrite(candPath));
            SnapshotForTxn(writePath);
            try { EnforceCandidateInsert(ins, writePath, candPath); }
            finally { VfpInsertProfile.Stop(VfpInsertProfileBucket.IndexMaintenanceCall, indexStart); }
            return;
        }
        VfpInsertProfile.Stop(VfpInsertProfileBucket.InsertTargetColumnResolution, targetStart);

        checkStart = VfpInsertProfile.Start();
        if (!EnforceReferentialIntegrity || table is null)
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.BufferingRiChecks, checkStart);
            string? insertPath = ResolveInsertWritePath(table);
            SnapshotForTxn(insertPath);
            long sqlStart = VfpInsertProfile.Start();
            try
            {
                Session.Execute(ins.Sql);
                if (insertPath is not null) ReopenFileAreas(insertPath);
            }
            finally { VfpInsertProfile.Stop(VfpInsertProfileBucket.SqlDmlFallback, sqlStart); }
            return;
        }

        // Resolve a work area for the target up front (opening one if the caller did not have it open) so
        // we can snapshot its .dbf for a revertible insert AND position the trigger on the new record.
        bool openedHere = false;
        var wa = Session.FindInsertArea(table);
        if (wa is null)
        {
            int savedArea = Session.CurrentArea;
            try
            {
                Session.SelectArea(0);
                Session.Use(table);
                wa = Session.AreaAt(Session.CurrentArea);
            }
            finally { Session.SelectArea(savedArea); }
            openedHere = wa is not null;
        }

        // No resolvable target / no bound insert trigger ⇒ plain DML, no enforcement (free table or no rule).
        if (wa is null || ResolveTriggerProc(RiEvent.Insert, wa) is null)
        {
            string? insertPath = ResolveInsertWritePath(table);
            SnapshotForTxn(insertPath);
            if (openedHere && wa is not null) { Session.CloseArea(wa.Area); _meta.Remove(wa.Area); }
            VfpInsertProfile.Stop(VfpInsertProfileBucket.BufferingRiChecks, checkStart);
            long sqlStart = VfpInsertProfile.Start();
            try
            {
                Session.Execute(ins.Sql);
                if (insertPath is not null) ReopenFileAreas(insertPath);
            }
            finally { VfpInsertProfile.Stop(VfpInsertProfileBucket.SqlDmlFallback, sqlStart); }
            return;
        }
        VfpInsertProfile.Stop(VfpInsertProfileBucket.BufferingRiChecks, checkStart);

        int area = wa.Area;
        // COPY-ON-WRITE seam (ADO.NET transaction): take the table's private copy BEFORE snapshotting so the
        // pre-image, the row append (Session.Execute re-enters the SAME seam via the table name), the trigger's
        // read-your-writes and a trigger-abort RestoreSnapshot all operate on the copy — isolated + rollback-
        // able. No-op in autocommit (the live path is returned unchanged).
        long leaseStart = VfpInsertProfile.Start();
        string? path = wa.Table.SourcePath is { } sp ? Path.GetFullPath(BeginTxWrite(sp)) : null;
        VfpInsertProfile.Stop(VfpInsertProfileBucket.BeginTxWriteLease, leaseStart);
        SnapshotForTxn(path);
        FileSnapshot? snap = path is not null ? CaptureSnapshot(path) : null;   // pre-image for the rollback.

        try
        {

        long riSqlStart = VfpInsertProfile.Start();
        try { Session.Execute(ins.Sql); }
        catch { if (openedHere) { Session.CloseArea(area); _meta.Remove(area); } throw; }
        finally { VfpInsertProfile.Stop(VfpInsertProfileBucket.SqlDmlFallback, riSqlStart); }

        if (path is not null) ReopenFileAreas(path);   // every open handle must see the appended row.

        int savedArea = Session.CurrentArea;
        Session.SelectArea(area);                       // the trigger reads the new record via SELECT().
        var fresh = Session.AreaAt(area);
        if (fresh is not null) GoRecord(area, fresh.Table.RecordCount);   // appended row = last physical.
        bool ok = FireDmlTrigger(RiEvent.Insert, area);
        Session.SelectArea(savedArea);

        // RESTRICT (parent key missing): the trigger rolled back nothing of ITS own (it only reads) — roll
        // the inserted row back here so VFP's "blocked INSERT" is matched and no orphan child survives.
        if (!ok && snap is not null) RestoreSnapshot(snap);
        if (openedHere && Session.AreaAt(area) is not null) { Session.CloseArea(area); _meta.Remove(area); }
        // VFP9 reports the rejected INSERT only after removing the orphan row.
        if (!ok) throw TriggerFailed(RiEvent.Insert, area, wa.Alias);
        }
        finally { snap?.Cleanup(); }
    }

    /// <summary>The parsed <see cref="InsertStatement"/> for <paramref name="ins"/>, reusing the parse
    /// captured at PRG-parse time (<see cref="InsertStmt.Parsed"/>) and, only when that is null, a lazy
    /// once-parsed backfill cached on the node (<see cref="InsertStmt.ParsedCache"/>). NEVER re-parses the SQL
    /// text per row — the whole point of Batch 4 (the AST node is reused across executions of the same source,
    /// so a single backfill amortizes). Returns null when the SQL is not a well-formed INSERT.</summary>
    private static InsertStatement? ResolveInsertStatement(InsertStmt ins)
    {
        if (ins.Parsed is InsertStatement p) return p;
        if (ins.ParsedCache is InsertStatement c) return c;
        var parsed = SafeParseSql(ins.Sql);
        ins.ParsedCache = parsed;                       // backfill once (harmless when it stays a non-INSERT).
        return parsed as InsertStatement;
    }

    /// <summary>Resolve the effective path written by INSERT, including the ADO.NET COW redirect. When the
    /// target is not already open, use a short-lived named-table handle only to discover its physical path.</summary>
    private string? ResolveInsertWritePath(string? table)
    {
        string? path = ResolveInsertTablePath(table);
        return path is null ? null : Path.GetFullPath(BeginTxWrite(path));
    }

    // Resolve the physical target before the fallback writer can bypass a custom alias's IDX inventory.
    private string? ResolveInsertTablePath(string? table)
    {
        if (table is null) return null;
        string? path = Session.FindInsertArea(table)?.Table.SourcePath;
        if (path is null)
        {
            DbfTable? opened = null;
            CdxFile? cdx = null;
            try
            {
                (opened, cdx) = Session.OpenNamedTable(table);
                path = opened.SourcePath;
            }
            catch { return null; }
            finally { cdx?.Dispose(); opened?.Dispose(); }
        }
        return path;
    }

    /// <summary>
    /// The Batch-4 DIRECT-APPEND fast path for a plain autocommit <c>INSERT … VALUES</c>: append the row
    /// straight through the interpreter's persistent cached <see cref="DbfWriter"/> (the 5.5 write hot-path
    /// handle — its incremental <c>.cdx</c> maintenance keeps the structural index current) and DEFER the
    /// read-view refresh, instead of the per-row Session.Execute → <c>OpenWritableTarget</c> open+reopen
    /// round-trip. Returns <see langword="true"/> when it handled the insert; <see langword="false"/> to FALL
    /// BACK to the old route (so error numbers / RI / candidate / COW / buffering semantics are unchanged).
    /// <para>
    /// Falls back (returns false, appends nothing) when: the target is not open in a work area (the slow path
    /// auto-opens it) or is read-only; the area is buffered (mode &gt; 1 → <see cref="BufferAppend"/>); the
    /// table carries a CANDIDATE tag (needs a post-append duplicate re-check); RI enforcement is ON (the ADO.NET
    /// EnforceRules=on model, which reads the row back through external handles that bypass the lazy refresh —
    /// and which fires the bound insert trigger ON the new row); or a STRUCTURAL anomaly (column/value count
    /// mismatch, an unknown column) that must surface EXACTLY as the ADO.NET/slow path raises it (err 12 etc.).
    /// A COW (ADO.NET-transaction) write redirect is excluded by the caller.
    /// </para>
    /// </summary>
    private bool TryDirectAppendInsert(InsertStatement st)
    {
        // A READ-ONLY session (VfpSession.ReadOnly — the connection-wide ReadOnly=true) rejects ALL DML at
        // OpenWritableTarget (it throws). The fast path bypasses that seam entirely, so a table opened WRITABLE
        // before ReadOnly was toggled on (wa.NoUpdate is still false) would otherwise be appended straight
        // through the cached writer — a real write into a read-only session. Refuse here so every read-only
        // INSERT takes the Session.Execute route, which rejects it before any row lands.
        if (Session.ReadOnly) return false;
        var wa = Session.FindInsertArea(st.Table);
        if (wa is null || wa.NoUpdate) return false;                 // not open / read-only area → slow path.
        if (wa.Table.SourcePath is not { } sourcePath) return false;
        if (_meta.TryGetValue(wa.Area, out var m) && m.Buffering > 1) return false;  // buffered → BufferAppend.

        string fullPath = Path.GetFullPath(sourcePath);
        if (CandidateTagsFor(fullPath) is not null) return false;    // needs post-append dup re-check.
        // RI-ENFORCED writes (the ADO.NET EnforceRules=on model drives the interpreter with this flag set) read
        // the appended row back through EXTERNAL handles — a follow-up SQL SELECT / ADO.NET reader that bypasses
        // the interpreter's lazy refresh — so a DEFERRED append would be invisible to them. Keep the whole
        // RI-enforced path on the durable per-row route (it already fires per-row DEFAULT/RULE/trigger, so the
        // INSERT open was never its bottleneck). This also subsumes the bound-insert-trigger enforcement.
        if (EnforceReferentialIntegrity) return false;

        // STRUCTURAL pre-validation against the read view's columns (identical geometry to the writer's): an
        // unknown column / count mismatch must raise through the ADO.NET path verbatim, so refuse here and let
        // the old route handle it — never half-append a bad row via the fast path.
        var columns = wa.Table.Columns;
        if (st.Columns is { } cols)
        {
            if (cols.Count != st.Values.Count) return false;
            foreach (var c in cols) if (ColumnIndex(wa.Table, c) < 0) return false;
        }
        else if (st.Values.Count != columns.Count) return false;

        long start = VfpInsertProfile.Start();
        try
        {
            // Append through the SAME Sql VALUES-eval + column-map the ADO.NET DmlExecutor uses (byte-identical
            // values + incremental .cdx maintenance via the cached writer). NO per-row Flush(): the writer
            // buffers the row (its live RecordCount grows) and keeps the .cdx accelerator HOT across the run;
            // the deferred refresh Flush()es once, before it reopens the view.
            var writer = GetOrOpenCachedWriter(sourcePath);
            DmlExecutor.AppendInsertValuesRow(Session, writer, st);
            MarkAppendPending(fullPath);
        }
        catch
        {
            // Drop a possibly touched writer and refresh the view, but preserve the INSERT error.
            InvalidateCachedWriter(sourcePath);
            MarkAppendPending(fullPath);
            throw;
        }
        finally { VfpInsertProfile.Stop(VfpInsertProfileBucket.DirectAppend, start); }
        return true;
    }

    // ─────────────────────────── REPLACE / DELETE / RECALL / SUM ───────────────────────────

    private void ExecAppendBlank(AppendBlankStmt command)
    {
        int area = command.In is null ? Session.CurrentArea : ResolveAreaRef(command.In);
        FlushPendingAppends(area);
        MaybeAutoCommitRow(area);
        var wa = Session.AreaAt(area)
            ?? throw new MicroVfpRuntimeException("No table is open in the current work area.", 52);
        if (Session.ReadOnly || wa.NoUpdate)
            throw new MicroVfpRuntimeException("Cannot update the cursor, since it is read-only.", 111);
        string path = wa.Table.SourcePath!;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var previous = Session.DbcDefaultEvaluator;
        bool previousBlank = _appendBlankDefaults;
        Session.DbcDefaultEvaluator = _dbcDefaultEvaluator ??= EvalDbcDefault;
        _appendBlankDefaults = true;
        try
        {
            // APPEND BLANK ignores SET NULL but still runs each field DEFAULT exactly once.
            DmlExecutor.EvaluateDbcDefaults(Session, wa.Alias, wa.Table.Columns, values, honourSetNull: false);
        }
        finally
        {
            Session.DbcDefaultEvaluator = previous;
            _appendBlankDefaults = previousBlank;
        }
        wa = Session.AreaAt(area)!;
        var meta = Meta(area);
        int recno;
        if (meta.Buffering > 1)
        {
            var statement = new InsertStatement(null, wa.Alias, new List<string>(), new List<VfpExpression>());
            var entry = new AppendEntry { Stmt = new InsertStmt(string.Empty, statement) };
            foreach (var (field, value) in values) entry.Fields[ColumnIndex(wa.Table, field)] = value;
            (meta.Buf ??= new TableBuffer()).Appends.Add(entry);
            recno = EffCount(area, wa);
        }
        else
        {
            path = BeginTxWrite(path);
            SnapshotForTxn(path);
            bool fireInsert = EnforceReferentialIntegrity && ResolveTriggerProc(RiEvent.Insert, wa) is not null;
            FileSnapshot? snapshot = fireInsert ? CaptureSnapshot(path) : null;
            try
            {
                using (var lease = LeaseWriter(path))
                {
                    var writer = lease.Writer;
                    writer.AppendRecord(values, DmlExecutor.OmittedColumnsBlankMask(Session, wa.Alias,
                        writer.Schema.Columns, values, honourSetNull: false));
                    recno = writer.RecordCount;
                    writer.Flush();
                }
                ReopenFileAreas(path);
                meta = Meta(area);
                PositionPointer(meta, null, recno);
                if (fireInsert)
                {
                    int savedArea = Session.CurrentArea;
                    bool allowed;
                    try
                    {
                        Session.SelectArea(area);
                        allowed = FireDmlTrigger(RiEvent.Insert, area);
                    }
                    finally { Session.SelectArea(savedArea); }
                    if (!allowed)
                    {
                        RestoreSnapshot(snapshot!);
                        // VFP removes the rejected append and leaves the pointer at EOF.
                        GoRecord(area, recno);
                        throw TriggerFailed(RiEvent.Insert, area, wa.Alias);
                    }
                }
            }
            finally { snapshot?.Cleanup(); }
        }
        PositionPointer(meta, null, recno);
        meta.Ordered = null;
        meta.KeyVisible = null;
    }

    private void ExecReplace(ReplaceStmt rp)
    {
        if (rp.Scope is not null || rp.For is not null || rp.While is not null)
        {
            ExecReplaceScope(rp);
            return;
        }

        if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends();

        // Default scope = the CURRENT record of the target area (risk #4 — never the whole table).
        int area = rp.In is not null ? ResolveAreaRef(rp.In) : AreaOfClause(rp.Clauses[0]);
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);

        // BUFFERING (mode>1): defer the field writes into the area's buffer instead of writing through.
        if (m.Buffering > 1) { BufferReplace(rp, area, wa, m); return; }

        int recIndex = m.RecNo - 1;
        if (recIndex < 0 || recIndex >= wa.Table.RecordCount) return;

        var path = wa.Table.SourcePath;
        if (path is null) return;

        // COPY-ON-WRITE seam (ADO.NET transaction): redirect this direct write onto the table's private
        // working copy — the SAME seam the raw DML path uses — so an EnforceRules=on UPDATE / RI-cascade
        // child REPLACE is isolated and rolled back, instead of escaping to the live file. From here `path`
        // is the copy, so the snapshots / writer / reopen below all operate on it. No-op in autocommit.
        path = BeginTxWrite(path);
        wa = Session.AreaAt(area) ?? wa;   // BeginTxWrite may have reopened the area on the copy.

        // ATOMICITY (P3b): when a bound UPDATE trigger will auto-fire, the parent key change must be
        // REVERTIBLE — the parent write lands first (so the trigger reads the NEW key via the current
        // field) but a RESTRICT/error abort (trigger returns .F.) has to roll the parent key back too, or
        // the parent moves to the new key while its children keep the old one (orphans). Capture the
        // parent pre-image up front; restore it below if the trigger aborts.
        // 5.5 RECORD-LEVEL pre-image (pathology 3): the abort revert needs only the ONE affected record, not
        // the whole .dbf/.cdx/.fpt (O(recordsize), not O(filesize) per statement). A whole-file FileSnapshot
        // is used only as the fallback for a record that cannot be captured record-level (a deleted current row).
        // The fields this REPLACE writes (resolvable columns only), computed UP FRONT so the record-level
        // pre-image below can tell whether a TOUCHED field is FPT-backed (a G/P field can't round-trip, so
        // it forces the whole-file snapshot). Also drives the TAG-SCOPED ordered-cache refresh (5.5
        // incremental maintenance): a REPLACE that touches no field of the controlling order's key leaves
        // the cached recno sequence + OrderPos intact (no O(n) re-read; the ordered SCAN keeps advancing).
        var replacedFields = new List<string>(rp.Clauses.Count);
        foreach (var clause in rp.Clauses)
        {
            string f = StripQualifier(NameOf(clause.Field));
            if (ColumnIndex(wa.Table, f) >= 0) replacedFields.Add(f);
        }

        bool autoFireUpdate = EnforceReferentialIntegrity && ResolveTriggerProc(RiEvent.Update, wa) is not null;
        RecordImage? parentImg = null; FileSnapshot? parentSnap = null;
        bool hasCandidate = CandidateTagsFor(path) is not null;
        RecordImage? candImg = null; FileSnapshot? candSnap = null;
        try
        {
        if (autoFireUpdate)
        {
            if (TryCaptureRecordImage(path, recIndex, wa, replacedFields, out var pImg)) parentImg = pImg;
            else parentSnap = CaptureSnapshot(path);
        }

        // CANDIDATE: a REPLACE that changes an indexed key to a duplicate of a CANDIDATE tag must RAISE
        // (VFP error 1884), matching INDEX ON … CANDIDATE. The incremental cdx maintenance leaves the
        // duplicate visible in the tag (a free-table candidate tag is plain/non-UNIQUE on disk), so we
        // capture the pre-image up front and, after the maintained write, re-check every candidate tag —
        // rolling the record back and raising on a violation (the same flow the INSERT path uses).
        if (hasCandidate)
        {
            if (TryCaptureRecordImage(path, recIndex, wa, replacedFields, out var cImg)) candImg = cImg;
            else candSnap = CaptureSnapshot(path);
        }

        SnapshotForTxn(path);

        using (var lease = LeaseWriter(path))
        {
            var writer = lease.Writer;
            var values = new object?[writer.Schema.Columns.Count];
            for (int i = 0; i < values.Length; i++) values[i] = DbfWriter.KeepValue;

            foreach (var clause in rp.Clauses)
            {
                string field = StripQualifier(NameOf(clause.Field));
                int idx = ColumnIndex(wa.Table, field);
                if (idx < 0 || idx >= values.Length) continue;
                // OLDVAL: capture the value at the START of THIS change (the field's CURRENT on-disk value
                // before this REPLACE), OVERWRITING any prior entry. A second key REPLACE of the same record
                // with no intervening pointer move must re-base OLDVAL to the value the first REPLACE
                // committed — else the auto-fired __RI_UPDATE_* would SCAN children FOR a key that was
                // already cascaded away (no cascade → orphans). The trigger reads OLDVAL() after the write.
                RememberOldVal(m, field, ReadField(wa, field));
                var v = Eval(clause.Value);
                char type = wa.Table.Columns[idx].Type;
                values[idx] = v.IsNull ? null : (type == 'M' && v.IsBinary ? v.ToClr()
                    : type is 'C' or 'M' or 'V' ? v.AsString : v.ToClr());
            }
            writer.UpdateRecord(recIndex, values);
            writer.Flush();
        }
        // Refresh EVERY handle on this file (USE..AGAIN shares one buffer in VFP) so siblings see the write.
        // 5.5 fast path: drop the read buffers in place (no per-statement file re-open) and preserve the
        // ordered cache unless the write moved the structural cdx — see RefreshAfterInPlaceWrite.
        RefreshAfterInPlaceWrite(path, wa, replacedFields);

        if (hasCandidate && FirstViolatedCandidate(path) is string badReplaceTag)
        {
            if (candImg is { } cRevert) RestoreRecordImage(cRevert); else RestoreSnapshot(candSnap!);
            throw new MicroVfpRuntimeException(
                $"REPLACE: CANDIDATE tag {badReplaceTag} uniqueness violated — a duplicate key value exists.", 1884); // VFP err 1884 (oracle-pinned).
        }
        // P3b: a key-changing REPLACE on a parent fires its bound update trigger, cascading the new key
        // to children (the trigger reads OLDVAL() — captured above — for the OLD key). A .F./error return
        // is a RESTRICT/update abort: the trigger already rolled back its child cascade (riend(.F.) →
        // ROLLBACK) — restore the parent pre-image too so the whole op is atomic (parent key reverts).
        if (autoFireUpdate && !FireDmlTrigger(RiEvent.Update, area))
        {
            if (parentImg is { } pRevert) RestoreRecordImage(pRevert);
            else if (parentSnap is not null) RestoreSnapshot(parentSnap);
            throw TriggerFailed(RiEvent.Update, area, wa.Alias);
        }
        }
        finally
        {
            parentSnap?.Cleanup();
            candSnap?.Cleanup();
        }
    }

    private void ExecReplaceScope(ReplaceStmt rp)
    {
        int area = rp.In is not null ? ResolveAreaRef(rp.In) : AreaOfClause(rp.Clauses[0]);
        var single = rp with { Scope = null, For = null, While = null };
        ExecRecordScope(area, rp.Scope, rp.For, rp.While, () => ExecReplace(single));
    }

    // REPLACE, DELETE and RECALL share VFP's scope order, visibility and final-pointer rules.
    private void ExecRecordScope(int area, string? scope, PrgExpr? forExpr, PrgExpr? whileExpr,
        Action action, bool includeDeleted = false, (LocScope Kind, int Count)? resolvedScope = null)
    {
        int savedArea = Session.CurrentArea;
        FlushPendingAppends(area);
        var wa = Session.AreaAt(area)
            ?? throw new MicroVfpRuntimeException("No table is open in the current work area.", 52);
        var (kind, count) = resolvedScope ?? ResolveLocScope(scope);
        if (scope is null && whileExpr is not null) kind = LocScope.Rest;
        Session.SelectArea(area);
        try
        {
            if (scope is null && forExpr is null && whileExpr is null)
            {
                action();
                return;
            }
            if (kind == LocScope.Record)
            {
                GoRecord(area, count);
                if ((whileExpr is null || ScopePredicate(whileExpr)) && (forExpr is null || ScopePredicate(forExpr)))
                    action();
                return;
            }

            // ALL without WHILE uses physical order; bounded/WHILE scopes follow the controlling index.
            // Snapshot before changing indexed fields, including the table buffer's unpublished keys.
            var order = ReplaceOrder(wa, kind == LocScope.All && whileExpr is null, out var keyVisible);
            int start = kind == LocScope.All ? 0 : CurrentIndex(order, Meta(area));
            if (start < 0) start = order.Count;
            int remaining = kind == LocScope.Next ? count : int.MaxValue;
            if (remaining <= 0) return;
            for (int i = start; i < order.Count; i++)
            {
                wa = Session.AreaAt(area)!;
                int rec = order[i];
                if ((kind == LocScope.All || i != start) && !Visible(wa, rec, keyVisible, includeDeleted)) continue;
                GoRecord(area, rec);
                if (whileExpr is not null && !ScopePredicate(whileExpr)) return;
                if (forExpr is null || ScopePredicate(forExpr)) action();
                // VFP leaves a bounded scope ON its last examined record (unlike SCAN's final SKIP).
                if (--remaining == 0) return;
            }
            GoRecord(area, EffCount(area, Session.AreaAt(area)!) + 1);
        }
        finally { Session.SelectArea(savedArea); }

        bool ScopePredicate(PrgExpr expression)
        {
            var value = Eval(expression);
            if (resolvedScope is not null && !value.IsNull && value.Type != VfpType.Logical)
                throw new MicroVfpRuntimeException("You must use a logical expression with a FOR or WHILE clause.", 1127);
            return Truth(value);
        }
    }

    private List<int> ReplaceOrder(VfpSession.WorkArea wa, bool physical, out HashSet<int>? keyVisible)
    {
        var m = Meta(wa.Area);
        keyVisible = null;
        using var src = OpenMasterOrder(wa.Area);
        if (src is null) return Enumerable.Range(1, EffCount(wa.Area, wa)).ToList();
        var entries = OrderedEntries(src).ToList();
        if (m.Buf is { IsEmpty: false } buffer)
        {
            string key = src.CdxTag?.KeyExpression ?? src.Idx!.Header.KeyExpression;
            string? condition = src.CdxTag?.ForExpression ?? src.Idx?.Header.ForExpression;
            int savedRec = m.RecNo;
            bool savedEof = m.Eof, savedBof = m.Bof;
            var savedCache = m.Cached;
            int savedCachedRec = m.CachedRec;
            try
            {
                var changed = buffer.Rows.Keys.Concat(Enumerable.Range(wa.Table.RecordCount + 1, buffer.Appends.Count));
                foreach (int rec in changed)
                {
                    entries.RemoveAll(e => e.Recno == rec);
                    m.RecNo = rec; m.Eof = m.Bof = false; m.Cached = null;
                    if (!string.IsNullOrWhiteSpace(condition) && !Truth(EvalTextLoud(condition))) continue;
                    var value = EvalTextLoud(key);
                    byte[] bytes = src.IsCharacterKey ? PaddedCharacterKey(src, value)
                        : IndexKey.Encode(value.ToClr(), src.KeyType) ?? Array.Empty<byte>();
                    entries.Add((bytes, rec));
                }
            }
            finally
            {
                m.RecNo = savedRec; m.Eof = savedEof; m.Bof = savedBof;
                m.Cached = savedCache; m.CachedRec = savedCachedRec;
            }
        }
        if (m.KeySet) keyVisible = KeyVisibleFromEntries(m, src, entries);
        if (physical) return Enumerable.Range(1, EffCount(wa.Area, wa)).ToList();
        bool descending = src.Descending != m.OrderReversed;
        entries.Sort((left, right) =>
        {
            int c = CompareBytesUnsigned(left.Key, right.Key);
            if (c == 0) c = left.Recno.CompareTo(right.Recno);
            return descending ? -c : c;
        });
        return entries.Select(e => e.Recno).ToList();
    }

    private void ExecDelete(DeleteStmt del)
    {
        ExecDeleteRecall(del.In, del.Scope, del.For, del.While, del.SyntaxError, recall: false);
    }

    private void ExecRecall(RecallStmt rc)
    {
        ExecDeleteRecall(rc.In, rc.Scope, rc.For, rc.While, rc.SyntaxError, recall: true);
    }

    private void ExecDeleteRecall(NameRef? inArea, string? scope, PrgExpr? forExpr, PrgExpr? whileExpr, int syntaxError, bool recall)
    {
        // Validate the entire syntax before moving or marking any row. The general scope resolver is
        // permissive for legacy commands; DELETE/RECALL must never turn an invalid scope into ALL.
        if (syntaxError != 0)
            throw new MicroVfpRuntimeException(syntaxError switch
            {
                36 => "Command contains unrecognized phrase/keyword.",
                152 => "Missing expression.",
                1231 => "Missing operand.",
                _ => "Syntax error.",
            }, syntaxError);
        ValidateDeleteRecallScope(scope, forExpr, whileExpr);
        int area = inArea is not null ? ResolveAreaRef(inArea) : Session.CurrentArea;
        if (Session.AreaAt(area) is null)
            throw new MicroVfpRuntimeException(inArea is null ? "No table is open." : "Alias is not found.", inArea is null ? 52 : 13);
        int tally = 0;
        WriterLease? lease = null;
        string? writePath = null;
        bool savedRequireNames = _requireDmlNames;
        int savedArea = Session.CurrentArea;
        _requireDmlNames = true;
        bool started = false;
        bool finishBufferedRows = false;
        try
        {
            // VFP evaluates NEXT/RECORD expressions in the IN target, even when resolution fails.
            Session.SelectArea(area);
            var resolvedScope = ResolveDeleteRecallScope(scope, area);
            // NEXT 1 is VFP's single-row form: like bare/RECORD it retains the pending edit.
            finishBufferedRows = (scope is not null || forExpr is not null || whileExpr is not null)
                && resolvedScope.Kind != LocScope.Record
                && !(resolvedScope.Kind == LocScope.Next && resolvedScope.Count == 1);
            started = true;
            Memory.Set("_TALLY", VfpValue.Integer(0));
            ExecRecordScope(area, scope, forExpr, whileExpr, Mark, includeDeleted: recall, resolvedScope: resolvedScope);
            if (EffCount(area, Session.AreaAt(area)!) == 0) Meta(area).Bof = true;
        }
        finally
        {
            _requireDmlNames = savedRequireNames;
            Session.SelectArea(savedArea);
            try { lease?.Writer.Flush(); }
            finally { lease?.Dispose(); }
            if (started) Memory.Set("_TALLY", VfpValue.Integer(tally));
        }

        void Mark()
        {
            var wa = Session.AreaAt(area)!;
            var m = Meta(area);
            int rec = m.RecNo;
            if (m.Eof || rec < 1 || rec > EffCount(area, wa)) return;
            bool deleted = m.Buf?.Rows.TryGetValue(rec, out var edit) == true && edit.DeletedOverride is bool flag
                ? flag : rec > wa.Table.RecordCount
                    ? m.Buf!.Appends[rec - wa.Table.RecordCount - 1].Deleted
                    : wa.Table.IsRecordDeleted(rec - 1);
            if (deleted != recall) return; // VFP counts transitions, not matching records.
            if (m.Buffering > 1)
            {
                BufferDeleteFlag(area, wa, m, deleted: !recall);
                Memory.Set("_TALLY", VfpValue.Integer(++tally));
                // Multi-record commands finish even the final row; NEXT 1 remains buffered.
                if (finishBufferedRows)
                {
                    try { MaybeAutoCommitRow(area); }
                    catch when (EnforceReferentialIntegrity && LastDmlTriggerAborted)
                    {
                        throw TriggerFailed(RiEvent.Delete, area, wa.Alias);
                    }
                }
                return;
            }
            else
            {
                // Triggers can recurse and invalidate cached writers, so retain a statement lease only
                // on the ordinary path. A rejected trigger leaves earlier records marked.
                bool trigger = !recall && EnforceReferentialIntegrity && ResolveTriggerProc(RiEvent.Delete, wa) is not null;
                if (wa.Table.SourcePath is not { } path) return;
                if (writePath is null)
                {
                    writePath = BeginTxWrite(path);
                    SnapshotForTxn(writePath);
                }
                if (trigger)
                {
                    using (var rowLease = LeaseWriter(writePath))
                    {
                        rowLease.Writer.Delete(rec - 1);
                        rowLease.Writer.Flush();
                    }
                    RefreshDeletionFlag(writePath);
                    // VFP exposes the candidate mark inside the trigger and includes a rejected row
                    // in _TALLY. On rejection it restores that row, retaining earlier successful marks.
                    Memory.Set("_TALLY", VfpValue.Integer(++tally));
                    try
                    {
                        if (!FireDmlTrigger(RiEvent.Delete, area))
                            throw TriggerFailed(RiEvent.Delete, area, wa.Alias);
                    }
                    catch
                    {
                        using var undo = LeaseWriter(writePath);
                        undo.Writer.Recall(rec - 1);
                        undo.Writer.Flush();
                        RefreshDeletionFlag(writePath);
                        throw;
                    }
                    return;
                }
                else
                {
                    lease ??= LeaseWriter(writePath);
                    if (recall) lease.Value.Writer.Recall(rec - 1);
                    else lease.Value.Writer.Delete(rec - 1);
                }
                // Preserve index order, but make each flag visible to subsequent predicates/UDFs.
                RefreshDeletionFlag(writePath);
            }
            Memory.Set("_TALLY", VfpValue.Integer(++tally));
        }
    }

    // A missing name in a DELETE/RECALL predicate (including its UDFs) aborts the scan, not a NULL match.
    private bool _requireDmlNames;

    private void RefreshDeletionFlag(string path)
    {
        // The COW redirect has already rebound the areas. A mark changes neither record count nor
        // ordinary index keys, so even a transient writer needs no per-record close/reopen.
        foreach (var wa in Session.OpenAreas)
        {
            if (!SamePath(wa.Table.SourcePath, path)) continue;
            wa.Table.RefreshView();
            if (_meta.TryGetValue(wa.Area, out var m)) m.Cached = null;
        }
    }

    private (LocScope Kind, int Count) ResolveDeleteRecallScope(string? scope, int area)
    {
        if (scope is null) return (LocScope.All, 0);
        string head = PrgScan.FirstWord(scope);
        if (head == "ALL") return (LocScope.All, 0);
        if (head == "REST") return (LocScope.Rest, 0);
        var value = Eval(PrgExpr.Parse(PrgScan.AfterFirstWord(scope)));
        if (!IsNumeric(value)) throw new MicroVfpRuntimeException("Syntax error.", 10);
        int count = (int)value.AsNumber;
        FlushPendingAppends(area);
        if (head == "RECORD" && (count < 1 || count > EffCount(area, Session.AreaAt(area)!)))
            throw new MicroVfpRuntimeException("Record is out of range.", 5);
        return (head == "NEXT" ? LocScope.Next : LocScope.Record, count);
    }

    private static void ValidateDeleteRecallScope(string? scope, PrgExpr? forExpr, PrgExpr? whileExpr)
    {
        if (forExpr is { IsParsed: false } || whileExpr is { IsParsed: false })
            throw new MicroVfpRuntimeException("Syntax error.", 10);
        if (scope is null) return;
        string head = PrgScan.FirstWord(scope);
        string rest = PrgScan.AfterFirstWord(scope).Trim();
        if (head is "ALL" or "REST" && rest.Length == 0) return;
        if (head is "NEXT" or "RECORD" && rest.Length == 0)
            throw new MicroVfpRuntimeException("Syntax error.", 10);
        if (head is "NEXT" or "RECORD" && rest.Length > 0 && PrgExpr.Parse(rest).IsParsed) return;
        throw new MicroVfpRuntimeException("Command contains unrecognized phrase/keyword.", 36);
    }

    private void ExecSum(SumStmt sum)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);
        int rc = wa.Table.RecordCount;
        int n = sum.Expressions.Count;
        var acc = new double[n];

        for (int rec = 1; rec <= rc; rec++)
        {
            if (!Visible(wa, rec)) continue;
            m.RecNo = rec; m.Eof = false; m.Bof = false; m.Cached = null;
            if (sum.While is not null && !Truth(Eval(sum.While))) break;
            if (sum.For is not null && !Truth(Eval(sum.For))) continue;
            for (int k = 0; k < n; k++)
            {
                var v = Eval(sum.Expressions[k]);
                if (!v.IsNull) acc[k] += v.AsDouble;
            }
        }
        m.RecNo = rc + 1; m.Eof = true; m.Bof = false; m.Cached = null;   // SUM leaves the pointer at EOF.
        for (int k = 0; k < sum.To.Count && k < n; k++)
            AssignTo(sum.To[k], VfpValue.Number(acc[k]));
    }

    // ─────────────────────────── P2 table/record movers + whole-table I/O ───────────────────────────

    /// <summary>The fields moved by a SCATTER/GATHER: an explicit FIELDS list (order preserved, unknown
    /// names skipped) or every non-system column. Memo-like columns (M/G/P/W) are excluded unless MEMO.</summary>
    private static List<DbfColumn> ScatterFields(DbfTable table, IReadOnlyList<string> requested, bool memo,
        bool recordObject = false, string? pattern = null, bool except = false)
    {
        static bool IsMemoLike(char t) => t is 'M' or 'G' or 'P' or 'W';
        var cols = table.Columns;
        var result = new List<DbfColumn>();
        if (requested.Count > 0)
        {
            foreach (var name in requested)
            {
                int idx = ColumnIndex(table, name);
                if (idx < 0)
                {
                    if (recordObject) throw new MicroVfpRuntimeException($"Variable '{name.ToUpperInvariant()}' is not found.", 12);
                    continue;
                }
                var c = cols[idx];
                if (c.IsSystem) continue;
                if (recordObject && c.Type is 'G' or 'P') continue;
                if (!memo && IsMemoLike(c.Type)) continue;
                result.Add(c);
            }
            return result;
        }
        foreach (var c in cols)
        {
            if (c.IsSystem) continue;
            if (recordObject && c.Type is 'G' or 'P') continue;
            if (!memo && IsMemoLike(c.Type)) continue;
            if (pattern is not null)
            {
                string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                bool matches = System.Text.RegularExpressions.Regex.IsMatch(c.Name, regex,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                if (matches == except) continue;
            }
            result.Add(c);
        }
        return result;
    }

    private void ExecScatter(ScatterStmt s)
    {
        var wa = Session.AreaAt(Session.CurrentArea);
        if (wa is null) return;
        var fields = ScatterFields(wa.Table, s.Fields, s.Memo, s.Kind == ScatterKind.Object, s.FieldPattern, s.Except);

        if (s.Kind == ScatterKind.Object)
        {
            string name = s.Name!;
            if (name.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            VfpObject obj;
            if (s.Additive && Memory.IsDefined(name))
            {
                if (!TryGetObject(Memory.Get(name), out var existing))
                    throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
                obj = existing;
            }
            else
            {
                obj = new VfpRecordObject();
                AssignTo(name, NewObjectRef(obj));
            }
            foreach (var c in fields)
            {
                var value = s.Blank ? BlankValueFor(c) : VfpValue.FromClr(ReadField(wa, c.Name));
                if (!value.IsNull && c.Type == 'Y') value = VfpValue.Currency(value.AsNumber);
                if (s.Blank && c.Type is 'Q' or 'W') value = VfpValue.Varbinary(Array.Empty<byte>());
                obj.Fields[c.Name] = value;
            }
            return;
        }

        if (s.Kind == ScatterKind.Array)
        {
            if (string.IsNullOrEmpty(s.Name)) return;
            var arr = Memory.RedimOrCreateArray(s.Name, fields.Count, 0);
            for (int i = 0; i < fields.Count; i++)
                arr.SetLinear(i + 1, s.Blank ? BlankValueFor(fields[i]) : VfpValue.FromClr(ReadField(wa, fields[i].Name)));
            return;
        }

        // MEMVAR: create/update a same-named memory variable per field.
        foreach (var c in fields)
            Memory.Set(c.Name, s.Blank ? BlankValueFor(c) : VfpValue.FromClr(ReadField(wa, c.Name)));
    }

    private void ExecGather(GatherStmt g)
    {
        var wa = Session.AreaAt(Session.CurrentArea);
        if (wa is null) return;
        var fields = ScatterFields(wa.Table, g.Fields, g.Memo, g.Kind == ScatterKind.Object, g.FieldPattern, g.Except);

        var clauses = new List<ReplaceClause>();
        if (g.Kind == ScatterKind.Array)
        {
            var arr = string.IsNullOrEmpty(g.Name) ? null : Memory.FindArray(g.Name);
            if (arr is null) return;
            int n = Math.Min(fields.Count, arr.Length);    // positional: element i → field i.
            for (int i = 0; i < n; i++)
                clauses.Add(new ReplaceClause(NameRef.OfName(fields[i].Name), PrgExpr.Parse($"{g.Name}({i + 1})"), false));
        }
        else if (g.Kind == ScatterKind.Object)
        {
            string name = g.Name!;
            if (name.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            if (!Memory.IsDefined(name))
                throw new MicroVfpRuntimeException($"Variable '{name.ToUpperInvariant()}' is not found.", 12);
            if (!TryGetObject(EvalTextLoud(g.Name!), out var obj))
                throw new MicroVfpRuntimeException($"{g.Name} is not an object.", 1924);
            foreach (var c in fields)
            {
                try { obj.Get(c.Name); }
                catch (MicroVfpRuntimeException ex) when (ex.VfpErrorNumber == 1734) { continue; }
                clauses.Add(new ReplaceClause(NameRef.OfName(c.Name), PrgExpr.Parse($"{g.Name}.{c.Name}"), false));
            }
        }
        else // MEMVAR: replace each field that has a matching visible memvar (missing ones are left unchanged).
        {
            foreach (var c in fields)
                if (Memory.IsDefined(c.Name))
                    clauses.Add(new ReplaceClause(NameRef.OfName(c.Name), PrgExpr.Parse($"m.{c.Name}"), false));
        }
        if (clauses.Count == 0) return;
        ExecReplace(new ReplaceStmt(clauses, null, null, null, null));
    }

    private void ExecAppendFrom(AppendFromStmt s)
    {
        if (s.Array || s.Type?.ToUpperInvariant() is "SDF" or "DELIMITED")
        {
            ExecExternalAppend(s);
            return;
        }
        if (s.Type is { } ty && !IsDbfType(ty))
            throw new MicroVfpRuntimeException($"APPEND FROM TYPE '{ty}' (non-DBF) is not supported.");
        var targetWa = Session.AreaAt(Session.CurrentArea);
        string? targetPath = targetWa?.Table.SourcePath;
        if (targetWa is null || targetPath is null) return;

        string sourceName = NameOf(s.Source);
        var fieldFilter = s.Fields.Count > 0 ? new HashSet<string>(s.Fields, StringComparer.OrdinalIgnoreCase) : null;

        int save = Session.CurrentArea;
        var before = Session.OpenAreas.Select(w => w.Area).ToHashSet();
        try { Session.Use(sourceName, inArea: 0); } catch { return; }
        int srcArea = Session.OpenAreas.Select(w => w.Area).FirstOrDefault(n => !before.Contains(n));
        var srcWa = srcArea == 0 ? null : Session.AreaAt(srcArea);
        if (srcWa is null) return;

        // Each source row: the copied values by name, and the names of the fields that are BLANK on disk.
        // VFP9 copies a blank field blank and a .NULL. field .NULL. (oracle-pinned by
        // MicroVfpAppendBlankFieldsVfp9OracleTests), but a blank N/D/M decodes to null and a blank L to .F.,
        // so the decoded value alone cannot tell blank, .NULL. and .F. apart — the raw record does.
        var rows = new List<(Dictionary<string, object?> Values, HashSet<string> Blank)>();
        try
        {
            var sm = _meta[srcArea] = new AreaMeta();
            Session.SelectArea(srcArea);                    // FOR is evaluated against the SOURCE record.
            int rc = srcWa.Table.RecordCount;
            for (int rec = 1; rec <= rc; rec++)
            {
                if (!Visible(srcWa, rec)) continue;
                sm.RecNo = rec; sm.Cached = null; sm.Eof = false; sm.Bof = false;
                if (s.For is not null && !Truth(Eval(s.For))) continue;
                var raw = srcWa.Table.GetRecord(rec - 1);
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var blank = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in srcWa.Table.Columns)
                {
                    if (col.IsSystem) continue;
                    if (fieldFilter is not null && !fieldFilter.Contains(col.Name)) continue;
                    // Copy the stored value, not the runtime's blank-to-empty conversion: a blank N/F
                    // reads as 0 in expressions but APPEND FROM must preserve its blank bytes.
                    object? value = raw?.GetStoredExpressionValue(col.Name);
                    if (value is string text && col.Type is 'C' or 'V' or 'M')
                    {
                        // APPEND FROM converts directly between tables, bypassing the current session.
                        value = col.IsBinary
                            ? targetWa.Table.Encoding.GetString(Encoding.Latin1.GetBytes(text))
                            : VfpSessionText.ForTable(text, srcWa.Table.Encoding, targetWa.Table.Encoding);
                    }
                    bool isNull = raw is { } r && r.IsNullFlagSet(col);
                    if (!isNull && (value is null || (col.Type == 'L' && raw is { } l && l.GetRawField(col)[0] == (byte)' ')))
                    {
                        value = null;
                        blank.Add(col.Name);
                    }
                    map[col.Name] = isNull ? null : value;
                }
                rows.Add((map, blank));
            }
        }
        finally
        {
            Session.CloseArea(srcArea); _meta.Remove(srcArea);
            Session.SelectArea(save);
        }

        if (rows.Count == 0) return;
        // COPY-ON-WRITE seam (ADO.NET transaction): redirect the bulk append onto the target table's private
        // working copy so a stored-proc APPEND FROM inside a FoxDbfTransaction is isolated + rolled back
        // instead of landing rows on the live .dbf. No-op in autocommit / EnforceRules=off (path unchanged).
        targetPath = BeginTxWrite(targetPath);
        SnapshotForTxn(targetPath);
        try
        {
            using var lease = LeaseWriter(targetPath);
            var writer = lease.Writer;
            var columns = writer.Schema.Columns;
            writer.Schema.SetTextSession(targetWa.Table.Encoding.CodePage);
            try
            {
                // Row by row, as VFP9: a row that would put .NULL. into a NOT NULL field raises 1581 and
                // stops the APPEND; the rows before it stay appended.
                foreach (var (values, blank) in rows)
                    writer.AppendRecord(values, AppendFromBlankMask(targetWa.Alias, columns, values, blank));
            }
            finally
            {
                writer.Schema.SetTextSession(SessionCodePage);
                writer.Flush();
            }
        }
        finally { ReopenFileAreas(targetPath); }   // appended rows changed the count ⇒ rebuild the order cache.
    }

    /// <summary>
    /// The blank mask for one <c>APPEND FROM</c> row, per VFP9 (oracle-pinned by
    /// MicroVfpAppendBlankFieldsVfp9OracleTests): a target field the source does not supply is an omitted
    /// INSERT column — blank under <c>SET NULL OFF</c>, <c>.NULL.</c> (or error 1581 when NOT NULL) under
    /// <c>SET NULL ON</c>; a supplied blank field is written blank; a supplied <c>.NULL.</c> stays
    /// <c>.NULL.</c> whatever SET NULL says, and raises 1581 when the target field is NOT NULL.
    /// </summary>
    private bool[] AppendFromBlankMask(string table, IReadOnlyList<DbfColumn> columns,
        Dictionary<string, object?> values, HashSet<string> blank)
    {
        var mask = DmlExecutor.OmittedColumnsBlankMask(Session, table, columns, values, honourSetNull: true);
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.IsSystem || !values.TryGetValue(col.Name, out var v) || v is not null) continue;
            if (blank.Contains(col.Name)) mask[i] = true;
            else if (!col.IsNullable && !col.IsAutoIncrement)
                throw new MicroVfpRuntimeException($"Field {col.Name.ToUpperInvariant()} does not accept null values.", 1581);
        }
        return mask;
    }

    private void ExecCopyStructure(CopyStructureStmt s)
    {
        var wa = Session.AreaAt(Session.CurrentArea);
        if (wa is null) return;
        string targetPath = TargetDbfPath(NameOf(s.Target));

        if (!s.Extended)
        {
            var cols = ScatterFields(wa.Table, s.Fields, memo: true);
            var defs = cols.Select(ToColumnDef).ToList();
            if (defs.Count == 0) return;
            using (DbfWriter.Create(targetPath, defs, Session.NewTableOptions with { Overwrite = true })) { }
            return;
        }

        // EXTENDED: a structure-descriptor table — one row per source field.
        var descCols = new[]
        {
            new DbfColumnDef("FIELD_NAME", 'C', 11),
            new DbfColumnDef("FIELD_TYPE", 'C', 1),
            new DbfColumnDef("FIELD_LEN", 'N', 3),
            new DbfColumnDef("FIELD_DEC", 'N', 3),
            new DbfColumnDef("FIELD_NULL", 'L'),
            new DbfColumnDef("FIELD_NOCP", 'L'),
        };
        var srcCols = ScatterFields(wa.Table, s.Fields, memo: true);
        using (var writer = DbfWriter.Create(targetPath, descCols, Session.NewTableOptions with { Overwrite = true }))
        {
            foreach (var c in srcCols)
                writer.AppendRecord(c.Name, c.Type.ToString(), (decimal)c.Length, (decimal)c.Decimal, c.IsNullable, c.IsBinary);
            writer.Flush();
        }
    }

    private void ExecCreateFrom(CreateFromStmt s)
    {
        string fromName = NameOf(s.From);
        var defs = new List<DbfColumnDef>();
        DbfTable descriptor;
        try { (descriptor, _) = Session.OpenNamedTable(fromName); }
        catch { return; }
        using (descriptor)
        {
            for (int i = 0; i < descriptor.RecordCount; i++)
            {
                if (descriptor.GetRecord(i) is not { } rec) continue;
                string name = (rec["FIELD_NAME"]?.ToString() ?? string.Empty).Trim();
                string typeStr = (rec["FIELD_TYPE"]?.ToString() ?? string.Empty).Trim();
                if (name.Length == 0 || typeStr.Length == 0) continue;
                char type = char.ToUpperInvariant(typeStr[0]);
                int len = ToInt(rec["FIELD_LEN"]);
                int dec = ToInt(rec["FIELD_DEC"]);
                bool nullable = rec["FIELD_NULL"] is bool bn && bn;
                bool binary = rec["FIELD_NOCP"] is bool bc && bc;
                defs.Add(new DbfColumnDef(name, type, len, dec, nullable, binary));
            }
        }
        if (defs.Count == 0) return;
        string targetPath = TargetDbfPath(NameOf(s.Table));
        using (DbfWriter.Create(targetPath, defs, Session.NewTableOptions with { Overwrite = true })) { }
        OpenCreatedTable(NameOf(s.Table));
    }

    private void ExecCopyTo(CopyToStmt s)
    {
        if (s.Memo) { ExecCopyMemo(s); return; }
        if (s.Type is { } ty && !IsDbfType(ty))
            throw new MicroVfpRuntimeException($"COPY TO TYPE '{ty}' (non-DBF) is not supported.");

        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);
        var cols = ScatterFields(wa.Table, s.Fields, memo: true);
        var defs = cols.Select(ToColumnDef).ToList();
        if (defs.Count == 0) return;
        string targetPath = TargetDbfPath(NameOf(s.Target));

        int savRec = m.RecNo; bool savEof = m.Eof, savBof = m.Bof;
        try
        {
            // SET BLOCKSIZE feeds the .fpt block size of the memo file this COPY TO creates.
            using var writer = DbfWriter.Create(targetPath, defs, Session.NewTableOptions with { Overwrite = true, MemoBlockSize = MemoBlockBytes() });
            int rc = wa.Table.RecordCount;
            for (int rec = 1; rec <= rc; rec++)
            {
                if (!Visible(wa, rec)) continue;
                m.RecNo = rec; m.Cached = null; m.Eof = false; m.Bof = false;
                if (s.For is not null && !Truth(Eval(s.For))) continue;
                var vals = new object?[cols.Count];
                for (int i = 0; i < cols.Count; i++) vals[i] = ReadField(wa, cols[i].Name);
                writer.AppendRecord(vals);
            }
            writer.Flush();
        }
        finally
        {
            m.RecNo = savRec; m.Eof = savEof; m.Bof = savBof; m.Cached = null;
        }
    }

    /// <summary><c>COPY MEMO mField TO cFile</c> — write the CURRENT record's memo field content to a text file.</summary>
    private void ExecCopyMemo(CopyToStmt s)
    {
        var wa = Session.AreaAt(Session.CurrentArea);
        if (wa is null || s.MemoField is null) return;
        string field = StripQualifier(NameOf(s.MemoField));
        string content = ReadField(wa, field)?.ToString() ?? string.Empty;
        string target = NameOf(s.Target);
        string path = Path.IsPathRooted(target)
            ? target
            : Path.Combine(Session.DataDirectory ?? Directory.GetCurrentDirectory(), target);
        if (!Path.HasExtension(path)) path += ".txt";
        // VFP COPY MEMO writes the field's session bytes; NOCPTRANS preserves the original bytes.
        var column = wa.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, field, StringComparison.OrdinalIgnoreCase));
        var encoding = column is { IsBinary: true } ? wa.Table.BinaryTextEncoding : ConnectionEncoding();
        byte[] bytes = encoding.GetBytes(content);
        if (s.Additive)
        {
            // ADDITIVE appends to an EXISTING file; VFP9 raises error 1 when the file is missing (verified).
            if (!File.Exists(path)) throw new MicroVfpRuntimeException("File does not exist.", 1);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(bytes);
            return;
        }
        File.WriteAllBytes(path, bytes);
    }

    private void ExecTotal(TotalStmt s)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);
        var cols = wa.Table.Columns.Where(c => !c.IsSystem).ToList();
        var defs = cols.Select(ToColumnDef).ToList();
        if (defs.Count == 0) return;

        var totalSet = s.Fields.Count > 0 ? new HashSet<string>(s.Fields, StringComparer.OrdinalIgnoreCase) : null;
        var totalIdx = new List<int>();
        for (int i = 0; i < cols.Count; i++)
        {
            bool numeric = cols[i].Type is 'N' or 'F' or 'I' or 'B' or 'Y';
            if (numeric && (totalSet is null || totalSet.Contains(cols[i].Name))) totalIdx.Add(i);
        }

        string targetPath = TargetDbfPath(NameOf(s.Target));
        int savRec = m.RecNo; bool savEof = m.Eof, savBof = m.Bof;
        var groups = new List<object?[]>();
        object?[]? rep = null;
        string? curKey = null;
        try
        {
            var order = ActiveOrder(area);
            IEnumerable<int> Sequence()
            {
                if (order is not null) { foreach (var r in order) yield return r; }
                else { for (int r = 1; r <= wa.Table.RecordCount; r++) yield return r; }
            }
            foreach (int rec in Sequence())
            {
                if (!Visible(wa, rec)) continue;
                m.RecNo = rec; m.Cached = null; m.Eof = false; m.Bof = false;
                if (s.For is not null && !Truth(Eval(s.For))) continue;
                string key = KeyNorm(Eval(s.Key));
                if (rep is null || key != curKey)
                {
                    if (rep is not null) groups.Add(rep);
                    rep = new object?[cols.Count];
                    for (int i = 0; i < cols.Count; i++) rep[i] = ReadField(wa, cols[i].Name);
                    curKey = key;
                }
                else
                {
                    foreach (int ti in totalIdx)
                        rep[ti] = Convert.ToDecimal(rep[ti] ?? 0m) + Convert.ToDecimal(ReadField(wa, cols[ti].Name) ?? 0m);
                }
            }
            if (rep is not null) groups.Add(rep);
        }
        finally
        {
            m.RecNo = savRec; m.Eof = savEof; m.Bof = savBof; m.Cached = null;
        }

        using (var writer = DbfWriter.Create(targetPath, defs, Session.NewTableOptions with { Overwrite = true }))
        {
            foreach (var g in groups) writer.AppendRecord(g);
            writer.Flush();
        }
    }

    private void ExecPack(PackStmt s)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        string? path = wa?.Table.SourcePath;
        if (wa is null || path is null) return;
        // §D2 VFP-faithful exclusivity gate — oracle-pinned to VFP9 err 110 ("File must be opened
        // exclusively.", vfp9.exe 2026-07-05): PACK requires the table opened EXCLUSIVE. A SHARED open
        // (SET EXCLUSIVE OFF / USE … SHARED) is refused with the SAME typed number the runtime raises —
        // mirrors ExecZap and the Core DbfWriter.Pack guard. Thrown before any close/rewrite, so the file
        // is untouched.
        if (!wa.Exclusive && !Session.DefaultExclusive)
            throw new MicroVfpRuntimeException("File must be opened exclusively.", 110);
        var names = ReadNamesFor(path);                 // before the COW redirect: the copy matches no area.
        // COPY-ON-WRITE seam (ADO.NET transaction): PACK physically truncates/compacts the .dbf/.fpt/.cdx —
        // an irreversible destructive op. Redirect it onto the table's private working copy so a stored-proc
        // PACK inside a FoxDbfTransaction hits the copy (Rollback discards it, restoring the deleted rows)
        // instead of the live file. No-op in autocommit / EnforceRules=off (path returned unchanged).
        path = BeginTxWrite(path);
        string full = Path.GetFullPath(path);

        SnapshotForTxn(path);
        InvalidateCachedWriter(full);                   // 5.5: release the cached writer before PACK truncates the file.
        var indexes = OpenAdditionalIndexPaths(path);
        var reopen = Session.CloseAreasForPath(full);   // release read handles so Pack can truncate the file.
        try
        {
            // Exclusive open (FileShare.None): we have already released every read handle above, and the
            // Core Pack guard requires LockMode.Exclusive. A FOREIGN process holding the table SHARED makes
            // this open throw a sharing IOException — PACK fails cleanly, the file untouched.
            using var writer = OpenExclusiveWriter(path, names);
            writer.SetOpenAdditionalIndexes(indexes, _ctx);
            writer.Pack();                              // physical delete-compaction (+ memo relocation).
        }
        finally
        {
            Session.ReopenAreas(reopen);
        }
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, path) && _meta.TryGetValue(w.Area, out var mm))
            { mm.Cached = null; mm.Ordered = null; }
        GoTop(area);
    }

    private void ExecRenameTable(RenameTableStmt s)
    {
        string? dbcPath = Session.DatabasePath;
        if (Session.Database is null || dbcPath is null || !File.Exists(dbcPath)) return;
        string oldName = NameOf(s.From).Trim();
        string newName = NameOf(s.To).Trim();
        if (oldName.Length == 0 || newName.Length == 0) return;

        using (var writer = DbfWriter.Open(dbcPath, new DbfOptions { LockMode = LockMode.Shared }))
        {
            var schema = writer.Schema;
            int nameIdx = ColumnIndex(schema, "OBJECTNAME");
            if (nameIdx < 0) return;
            for (int i = 0; i < schema.RecordCount; i++)
            {
                if (schema.GetRecord(i) is not { } rec) continue;
                if (rec["OBJECTTYPE"]?.ToString()?.Trim() is not { } ot
                    || !ot.Equals("Table", StringComparison.OrdinalIgnoreCase)) continue;
                if (rec["OBJECTNAME"]?.ToString()?.Trim() is not { } on
                    || !on.Equals(oldName, StringComparison.OrdinalIgnoreCase)) continue;
                var values = new object?[schema.Columns.Count];
                for (int k = 0; k < values.Length; k++) values[k] = DbfWriter.KeepValue;
                values[nameIdx] = newName;
                writer.UpdateRecord(i, values);
                writer.Flush();
                break;
            }
        }
    }

    private void ExecSqlPassthrough(SqlPassthroughStmt s)
    {
        // 5.5: a passthrough may UPDATE/DELETE/INSERT/ALTER/DROP any table through the SQL DML/DDL writer —
        // drop every cached interpreter writer so none straddles a foreign rewrite / count change. Cold path.
        DisposeAllCachedWriters();
        // An unparseable or failing statement raises (it used to be swallowed, leaving no table behind).
        var parsed = SafeParseSql(s.Sql) ?? ThrowSqlParseError(s.Sql);
        var previous = Session.DdlExpressionEvaluator;
        var previousArray = Session.DdlArrayResolver;
        Session.DdlArrayResolver = name =>
        {
            var array = Memory.FindArray(name);
            if (array is null) return null;
            var values = new VfpValue[array.Rows, array.Cols];
            for (int row = 0; row < array.Rows; row++)
                for (int col = 0; col < array.Cols; col++)
                    values[row, col] = array.Get(row + 1, col + 1);
            return values;
        };
        Session.DdlExpressionEvaluator = expression =>
            ParseExpressionCached(MicroVfpExprRewrite.Normalize(expression.Text)).Evaluate(_row, _ctx);
        try
        {
            Session.Execute(s.Sql);
        }
        finally
        {
            Session.DdlExpressionEvaluator = previous;
            Session.DdlArrayResolver = previousArray;
        }
        // VFP opens a freshly CREATEd table in a work area; do the same so a follow-up USE/SELECT/field read works.
        if (parsed is CreateTableStatement cts) OpenCreatedTable(cts.Table);
    }

    /// <summary>Open the just-created table <paramref name="name"/> in a fresh work area, seed its meta and
    /// position at top (best-effort; a failure to open leaves the session unchanged).</summary>
    private void OpenCreatedTable(string name)
    {
        try
        {
            string? member = Session.Database?.TableNames.FirstOrDefault(table =>
                SamePath(Session.Database.GetTablePath(table), TargetDbfPath(name)));
            Session.Use(member ?? name, inArea: 0, alias: Path.GetFileNameWithoutExtension(name));
            var wa = Session.OpenAreas
                .FirstOrDefault(w => SamePath(w.Table.SourcePath, TargetDbfPath(name)));
            if (wa is not null)
            {
                _meta[wa.Area] = new AreaMeta
                {
                    SourceName = member,
                    DatabaseProp = member is null ? null : Session.DatabasePath,
                };
                Session.SelectArea(wa.Area);
                GoTop(wa.Area);
            }
        }
        catch { /* best-effort — the file exists on disk regardless. */ }
    }

    private VfpValue FnLupdate(VfpValue[] a)
    {
        var wa = AreaArg(a, 0);
        string? path = wa?.Table.SourcePath;
        if (path is null) return VfpValue.Date(default);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> h = stackalloc byte[4];
            if (fs.Read(h) < 4) return VfpValue.Date(default);
            int yy = h[1], mm = h[2], dd = h[3];
            if (mm is < 1 or > 12 || dd is < 1 or > 31) return VfpValue.Date(default);
            // Two-digit header year → full year via the VFP 1950–2049 rollover window.
            int year = yy >= 100 ? 1900 + yy : (yy <= 49 ? 2000 + yy : 1900 + yy);
            return VfpValue.Date(new DateOnly(year, mm, dd));
        }
        catch { return VfpValue.Date(default); }
    }

    // ── shared little helpers for the whole-table commands ──

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        decimal d => (int)d,
        double db => (int)db,
        _ => int.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0,
    };

    private static DbfColumnDef ToColumnDef(DbfColumn c)
        => new(c.Name, c.Type, c.Length, c.Decimal, c.IsNullable, c.IsBinary);

    private static VfpValue BlankValueFor(DbfColumn col) => col.Type switch
    {
        'C' => VfpValue.Character(new string(' ', col.Length)),
        'V' or 'M' or 'G' or 'P' or 'W' => VfpValue.Character(string.Empty),
        'I' => VfpValue.Integer(0),
        'N' or 'F' or 'B' or 'Y' => VfpValue.Number(0m),
        'L' => VfpValue.Logical(false),
        'D' => VfpValue.Date(default),
        'T' => VfpValue.DateTime(default),
        _ => VfpValue.Null,
    };

    /// <summary>A stable grouping signature for a TOTAL key value (right-trimmed for character keys).</summary>
    private static string KeyNorm(VfpValue v)
    {
        if (v.IsNull) return "\0<null>";
        return v.Type == VfpType.Character
            ? v.AsString.TrimEnd()
            : Convert.ToString(v.ToClr(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Resolve a target table NAME to a full <c>.dbf</c> path in the session's data directory
    /// (an absolute path is used as-is; a missing <c>.dbf</c> extension is appended).</summary>
    private string TargetDbfPath(string name)
    {
        string file = name.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? name : name + ".dbf";
        return Path.IsPathRooted(file)
            ? file
            : Path.Combine(Session.DataDirectory ?? Directory.GetCurrentDirectory(), file);
    }

    /// <summary>True when a COPY TO / APPEND FROM TYPE clause names a DBF (table) format (or is absent);
    /// non-DBF export/import formats (SDF / DELIMITED / spreadsheet / XML) are flagged out of scope.</summary>
    private static bool IsDbfType(string type) => type.ToUpperInvariant() switch
    {
        "DBF" or "FOX2X" or "FOXPLUS" or "VFP" => true,
        _ => false,
    };

}
