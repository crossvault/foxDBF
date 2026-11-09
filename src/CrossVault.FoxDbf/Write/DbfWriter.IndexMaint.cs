using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Incremental structural-CDX maintenance. Every mutation validates all open index definitions before
/// writing DBF/FPT bytes. Unsupported weights or expressions must never destroy the stored tag definitions.
/// </summary>
public sealed partial class DbfWriter
{
    // Compiled per-tag key/FOR derivation, cached across this writer's lifetime keyed by tag name (a NULL
    // value caches "this tag cannot be incrementally maintained" — see GetComputer). A tag's DEFINITION
    // (key expr / collation / FOR / unique) is stable for the writer's life; only its on-disk ROOT moves
    // (read fresh from the header on every edit), so caching the computer is safe.
    private Dictionary<string, CdxIndexBuilder.TagKeyComputer?>? _tagComputers;
    internal EvaluationContext? IndexContext { get; set; }
    private Action[]? _hostIndexValidators;

    // ── cached incremental-CDX maintenance editor (ROADMAP item 2 — write battle W1 + W3 fix) ─────────
    // A single-row APPEND or keyed-UPDATE loop previously re-OPENED the structural .cdx, re-parsed its header,
    // re-walked the tag directory (ReadTags) and re-resolved every tag's key computer on EVERY row — that
    // per-row open/parse dominated the cost (the bulk AppendRecords path already amortizes all of it under
    // ONE editor session, and was ~87x faster for the identical inserts). We now keep the read/write .cdx
    // stream + its CdxTreeEditor + the resolved per-tag plans open ACROSS a run of incremental edits (appends
    // AND in-place updates share ONE editor), reusing them, and STILL flush after every row. In Shared mode a
    // cached editor is refreshed from disk before each mutation while the DBF append/record lock is held, so a
    // second writer's prior serialized CDX edit cannot leave stale pages or a stale allocation pointer behind.
    //   The cache is a PURE incremental accelerator: any operation that opens its OWN .cdx handle or replaces
    // the file (a BATCH append's one-shot session, PACK/ZAP/tag-DDL invalidation, a §D5 Alter reopen) or a
    // public Flush/Dispose tears it down first (CloseCdxMaint), so two editors never coexist on the file and
    // no stale handle blocks a delete.
    private FileStream? _cdxMaintStream;
    private CdxTreeEditor? _cdxMaintEditor;
    private List<(CdxTreeEditor.TagHandle Tag, CdxIndexBuilder.TagKeyComputer Computer)>? _cdxMaintPlans;
    private CdxMaintMode _cdxMaintMode;

    private enum CdxMaintMode
    {
        None,
        Append,
        Update,
    }

    internal int CdxAppendPageCachePageCountForTests
        => _cdxMaintEditor?.AppendPageCachePageCountForTests ?? 0;

    /// <summary>True when a structural index is available for incremental maintenance.</summary>
    private bool ShouldMaintainIndexes()
    {
        if (!_hasStructuralCdx) return false;
        // One live existence check on the common path; preserve case-variant discovery on a miss.
        string path = _cdxMaintStream?.Name ?? Path.ChangeExtension(_stream.Name, ".cdx");
        return File.Exists(path) || File.Exists(ExistingCdxPath());
    }

    // Q2: VFP refuses an unavailable index expression instead of discarding its definition.
    // DML calls this inside the mutation lock, before memo allocation, auto-increment or any DBF write.
    private bool ValidateIndexes(CdxMaintMode mode = CdxMaintMode.Update)
    {
        ValidateCompactIndexes();
        if (ReindexNeeded)
            throw new DbfWriteException("Index maintenance previously failed; rebuild the structural index before writing again.");
        if (!ShouldMaintainIndexes())
        {
            CloseCdxMaint(flush: true);
            return false;
        }
        // In unattended VFP, a released array suppresses this tag's maintenance without
        // aborting the DBF/FPT write. Re-evaluate availability on each write in that mode.
        if (IndexContext?.SkipUnavailableIndexExpression is not null) CloseCdxMaint(flush: true);
        EnsureCdxMaint(mode);
        if (_hostIndexValidators is { } validators)
            foreach (var validate in validators) validate();
        return true;
    }

    /// <summary>Insert the appended row into every applicable structural tag.</summary>
    private void MaintainIndexesAfterAppend(int recNo, DbfRecord newRecord)
    {
        // ValidateIndexes prepared this editor under the same mutation lock, before DBF/FPT writes.
        // Reopening/revalidating here repeats filesystem queries and discards the fresh shared-page cache.
        if (_cdxMaintEditor is null) return;
        try
        {
            ThrowIfInjectedFault();
            var editor = _cdxMaintEditor!;
            foreach (var (tag, computer) in _cdxMaintPlans!)
            {
                if (!computer.TryComputeKey(newRecord, recNo, _recordCount, out var key))
                    continue;   // FOR filter excludes this record from the tag
                if (computer.Unique && editor.ContainsKey(tag.HeaderOffset, key, computer.KeyLen, computer.Pad))
                    continue;   // UNIQUE tag already holds the key (keep the first record — VFP semantics)
                editor.Insert(tag.HeaderOffset, key, (uint)recNo, computer.KeyLen, computer.Pad);
            }
            // Flush after every append so on-disk visibility is byte-for-byte what the per-append
            // open/insert/flush/close path produced — only the redundant re-open/re-parse is removed.
            editor.Flush();
        }
        catch
        {
            MarkIndexMaintenanceFailed();
            throw;
        }
    }

    /// <summary>Open or refresh the cached editor, rejecting any unmaintainable tag before editing.</summary>
    private void EnsureCdxMaint(CdxMaintMode mode)
    {
        if (_cdxMaintEditor is not null && _cdxMaintPlans is not null)
        {
            PrepareCdxMaintMode(mode);
            return;
        }

        // Keep ownership local until all definitions are validated; failures must release every handle.
        FileStream? rw = null;
        CdxTreeEditor? editor = null;
        try
        {
            rw = OpenCdxReadWrite();
            FailCdxMaintEditorForTests?.Invoke();   // test seam: simulate a CdxTreeEditor ctor throw (else no-op).
            editor = new CdxTreeEditor(rw, _schema.Encoding);
            if (mode == CdxMaintMode.Append)
                editor.BeginAppendRun();
            var plans = ResolveComputers(editor);

            // Hand ownership to the cache, then null the locals so finally leaves the live handles open.
            _cdxMaintStream = rw;
            _cdxMaintEditor = editor;
            _cdxMaintPlans = plans;
            var validators = plans.Where(p => p.Computer.ValidateHost is not null).Select(p => p.Computer.ValidateHost!).ToArray();
            _hostIndexValidators = validators.Length == 0 ? null : validators;
            _cdxMaintMode = mode;
            rw = null;
            editor = null;
        }
        finally
        {
            editor?.Dispose();
            rw?.Dispose();
        }
    }

    /// <summary>Test-only fault before editor ownership transfers; used to verify handle cleanup.</summary>
    [ThreadStatic]
    internal static Action? FailCdxMaintEditorForTests;

    private void PrepareCdxMaintMode(CdxMaintMode mode)
    {
        if (_cdxMaintEditor is null)
        {
            _cdxMaintMode = CdxMaintMode.None;
            return;
        }

        if (_lockMode == LockMode.Shared)
        {
            _cdxMaintEditor.RefreshFromDisk();
            if (mode == CdxMaintMode.Append)
                _cdxMaintEditor.BeginAppendRun();
            _cdxMaintMode = mode;
            return;
        }

        if (_cdxMaintMode == mode)
            return;

        _cdxMaintEditor.Flush();
        _cdxMaintEditor.ClearAppendRunCache();

        if (mode == CdxMaintMode.Append)
            _cdxMaintEditor.BeginAppendRun();

        _cdxMaintMode = mode;
    }

    /// <summary>
    /// Tear down the cached incremental-maintenance CDX editor (if any): optionally flush its stream, dispose
    /// the editor's reader (leaveOpen — does not close the stream) and the read/write stream, and forget the
    /// plans. Idempotent. Called before any operation that opens its own <c>.cdx</c> handle, replaces the
    /// file, or closes the writer, so the incremental cache is a pure accelerator that never outlives its loop.
    /// </summary>
    private void CloseCdxMaint(bool flush)
    {
        if (_cdxMaintEditor is null && _cdxMaintStream is null)
            return;
        try { if (flush) _cdxMaintStream?.Flush(); } catch { /* best-effort */ }
        try { _cdxMaintEditor?.Dispose(); } catch { /* best-effort */ }
        try { _cdxMaintStream?.Dispose(); } catch { /* best-effort */ }
        _cdxMaintEditor = null;
        _cdxMaintStream = null;
        _cdxMaintPlans = null;
        _hostIndexValidators = null;
        _cdxMaintMode = CdxMaintMode.None;
    }

    /// <summary>Maintain a small append batch incrementally, or bulk-rebuild a large batch.</summary>
    private void MaintainIndexesAfterAppendBatch(IReadOnlyList<(int RecNo, byte[] Record)> appended, int existingRecordCount)
    {
        if (appended.Count == 0 || !ShouldMaintainIndexes())
            return;
        if (ShouldBulkBuildIndexesAfterAppendBatch(appended.Count, existingRecordCount))
        {
            RebuildIndexesAfterBulkAppend(existingRecordCount);
            return;
        }
        try
        {
            ThrowIfInjectedFault();
            // Share the mutation's page undo log with the single-row path.
            var editor = _cdxMaintEditor!;
            var plans = _cdxMaintPlans!;
            // Record by record, every tag for each one — the order VFP maintains its tags in.
            foreach (var (recNo, bytes) in appended)
            {
                var record = new DbfRecord(_schema, bytes);
                foreach (var (tag, computer) in plans)
                {
                    if (!computer.TryComputeKey(record, recNo, _recordCount, out var key))
                        continue;
                    if (computer.Unique && editor.ContainsKey(tag.HeaderOffset, key, computer.KeyLen, computer.Pad))
                        continue;
                    editor.Insert(tag.HeaderOffset, key, (uint)recNo, computer.KeyLen, computer.Pad);
                }
            }
            editor.Flush();
        }
        catch
        {
            MarkIndexMaintenanceFailed();
            throw;
        }
    }

    private static bool ShouldBulkBuildIndexesAfterAppendBatch(int appendedCount, int existingRecordCount)
        => appendedCount > 0 && appendedCount >= existingRecordCount;

    [ThreadStatic]
    internal static Action? AfterBulkIndexRebuildForTests;

    private void RebuildIndexesAfterBulkAppend(int existingRecordCount)
    {
        try
        {
            ThrowIfInjectedFault();
            var tags = ReadExistingTagDefinitions();
            if (tags.Count == 0)
                return;
            var appendUniqueState = CaptureAppendUniqueState(tags, existingRecordCount);
            var statistics = CaptureAppendKeyStatistics();
            RebuildStructuralCdx(tags, includeDeleted: true, appendUniqueState: appendUniqueState,
                preservedKeyStatistics: statistics);
            AfterBulkIndexRebuildForTests?.Invoke();
        }
        catch
        {
            MarkIndexMaintenanceFailed();
            throw;
        }
    }

    private Dictionary<string, byte[]> CaptureAppendKeyStatistics()
    {
        // VFP9 keeps the last INDEX ON / REINDEX statistics during APPEND FROM, even for empty or
        // UNIQUE tags. A bulk rebuild is an append optimization, not a new statistics collection.
        using var index = IndexFile.Open(ExistingCdxPath());
        var header = index.ReadCdxHeader(0);
        var statistics = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (offset, name) in IndexTraversal.EnumerateCompact(index, header.Root, header.KeyLength, pad: 0x20))
        {
            var page = index.ReadPage(offset) ?? throw new InvalidDataException("Cannot read CDX tag statistics.");
            statistics.Add(TrimName(name, _schema.Encoding), page.AsSpan(16, 20).ToArray());
        }
        return statistics;
    }

    private CdxIndexBuilder.AppendUniqueState? CaptureAppendUniqueState(
        IReadOnlyList<CdxTagDefinition> tags, int existingRecordCount)
    {
        var uniqueTags = tags.Where(t => t.Unique).ToList();
        if (uniqueTags.Count == 0)
            return null;

        var entriesByTag = new Dictionary<string, IReadOnlyList<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in uniqueTags)
            entriesByTag[tag.Name] = Array.Empty<IndexEntry>();

        string cdx = ExistingCdxPath();
        if (File.Exists(cdx))
        {
            using var file = CdxFile.Open(cdx, _schema);
            uint oldMax = existingRecordCount <= 0 ? 0u : (uint)existingRecordCount;
            foreach (var def in uniqueTags)
            {
                var tag = file.Tag(def.Name);
                if (tag is null)
                    continue;

                var existing = new List<IndexEntry>();
                foreach (var entry in tag.EnumerateEntries())
                {
                    if (entry.RecordNumber == 0 || entry.RecordNumber > oldMax)
                        continue;
                    existing.Add(new IndexEntry(entry.RecordNumber, (byte[])entry.Key.Clone()));
                }
                entriesByTag[def.Name] = existing;
            }
        }

        return new CdxIndexBuilder.AppendUniqueState(existingRecordCount, entriesByTag);
    }

    /// <summary>Replace changed keys and FOR membership after an in-place update.</summary>
    private void MaintainIndexesAfterUpdate(int recNo, DbfRecord oldRecord, DbfRecord newRecord)
    {
        if (_cdxMaintEditor is null) return;   // prepared by this mutation's ValidateIndexes
        try
        {
            ThrowIfInjectedFault();
            var editor = _cdxMaintEditor!;
            foreach (var (tag, computer) in _cdxMaintPlans!)
            {
                bool oldIn = computer.TryComputeKey(oldRecord, recNo, _recordCount, out var oldKey);
                bool newIn = computer.TryComputeKey(newRecord, recNo, _recordCount, out var newKey);

                // Unchanged membership AND identical key → nothing to do for this tag.
                if (oldIn && newIn && oldKey.AsSpan().SequenceEqual(newKey))
                    continue;

                if (oldIn)
                    editor.Delete(tag.HeaderOffset, oldKey, (uint)recNo, computer.KeyLen, computer.Pad);
                if (newIn && !(computer.Unique && editor.ContainsKey(tag.HeaderOffset, newKey, computer.KeyLen, computer.Pad)))
                    editor.Insert(tag.HeaderOffset, newKey, (uint)recNo, computer.KeyLen, computer.Pad);
            }
            // Flush after every update so on-disk visibility is byte-for-byte what the per-update
            // open/edit/flush/close path produced — only the redundant re-open/re-parse is removed.
            editor.Flush();
        }
        catch
        {
            MarkIndexMaintenanceFailed();
            throw;
        }
    }

    private List<(CdxTreeEditor.TagHandle Tag, CdxIndexBuilder.TagKeyComputer Computer)> ResolveComputers(CdxTreeEditor editor)
    {
        var plans = new List<(CdxTreeEditor.TagHandle, CdxIndexBuilder.TagKeyComputer)>();
        foreach (var tag in editor.ReadTags())
        {
            if (IndexContext?.SkipUnavailableIndexExpression is { } skip
                && (skip(tag.Header.KeyExpression) || skip(tag.Header.ForExpression))) continue;
            var computer = GetComputer(tag);
            if (computer is null)
            {
                string? function = VfpRuntime.UnsupportedIndexFunction(tag.Header.KeyExpression)
                    ?? VfpRuntime.UnsupportedIndexFunction(tag.Header.ForExpression);
                throw new DbfWriteException(
                    $"Cannot maintain index tag '{tag.Name}' (KEY '{tag.Header.KeyExpression}', FOR '{tag.Header.ForExpression}', "
                    + $"collation '{tag.Header.SortOrder}', code page {_schema.Encoding.CodePage})."
                    + (function is null ? "" : $" Function {function}() is not supported by the index engine."));
            }
            plans.Add((tag, computer));
        }
        return plans;
    }

    private void MarkIndexMaintenanceFailed()
    {
        // An unexpected I/O failure still propagates, but must not destroy the tag definitions.
        // The enclosing IndexMutation still needs the editor to restore its touched pages.
        ReindexNeeded = true;
    }

    /// <summary>Test seam: force the NEXT maintenance pass to throw so the crash-safety backstop can be proven.</summary>
    private void ThrowIfInjectedFault()
    {
        if (FailIndexMaintenanceForTests)
            throw new InvalidOperationException("Injected index-maintenance fault (test seam).");
    }

    /// <summary>Open the structural <c>.cdx</c> read/write, sharing with any concurrent reader (the microVFP
    /// work area keeps its own read handle open across a write).</summary>
    private FileStream OpenCdxReadWrite()
        => new(ExistingCdxPath(), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>Cache a key computer only when KEY/FOR fields, functions, weights and stored width resolve.</summary>
    private CdxIndexBuilder.TagKeyComputer? GetComputer(CdxTreeEditor.TagHandle tag)
    {
        _tagComputers ??= new Dictionary<string, CdxIndexBuilder.TagKeyComputer?>(StringComparer.OrdinalIgnoreCase);
        if (_tagComputers.TryGetValue(tag.Name, out var cached))
            return cached;

        CdxHeader hdr = tag.Header;
        CdxIndexBuilder.TagKeyComputer? computer = null;
        try
        {
            string? forExpr = hdr.HasFor && hdr.ForExpression.Length > 0 ? hdr.ForExpression : null;

            // RESOLVE-aware guard (finding 2): every field the KEY and FOR expressions read must exist in the
            // physical schema, else our derivation silently produces wrong key bytes regardless of any length
            // coincidence. Checked BEFORE building the computer so an un-derivable DBC long-name tag can never
            // be mistaken for derivable just because its width happens to match.
            if (IndexExpressionSupported(hdr.KeyExpression) && IndexExpressionSupported(forExpr))
            {
                var def = new CdxTagDefinition(tag.Name, hdr.KeyExpression, forExpr, hdr.Descending, hdr.SortOrder, hdr.IsUnique);

                // No ambient EvaluationContext on the raw write path → VFP-neutral defaults (EXACT/ANSI OFF), the
                // same defaults the bulk builder uses when CreateTag/Reindex run without a context.
                var c = CdxIndexBuilder.CreateKeyComputer(_schema, def, IndexContext);
                if (c.KeyLen == hdr.KeyLength && c.KeyType != IndexKeyType.Unknown)
                    computer = c;   // derived key length matches the stored tag → safe to edit incrementally
            }
        }
        catch when (IndexContext?.IndexEvaluator is null)
        {
            computer = null;    // report the stored definition from ResolveComputers before any mutation
        }

        _tagComputers[tag.Name] = computer;
        return computer;
    }

    /// <summary>True when EVERY field referenced by <paramref name="expr"/> exists in this writer's physical
    /// schema (an empty / null expression trivially resolves). An unparseable expression does not resolve.
    /// This is the resolve-aware core of the un-derivable check (finding 2).</summary>
    private bool FieldsResolve(string? expr, EvaluationContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return true;
        IReadOnlyCollection<string> fields;
        try
        {
            // Array-reference arguments such as ASCAN(a, id) are names, not missing DBF fields.
            string normalized = (context ?? IndexContext)?.NormalizeIndexExpression?.Invoke(expr) ?? expr;
            fields = VfpExpression.Parse(normalized).ReferencedFields();
        }
        catch { return false; }   // cannot even parse → not derivable
        foreach (var f in fields)
            if (!_schema.Columns.Any(c => string.Equals(c.Name, f, StringComparison.OrdinalIgnoreCase)))
                return false;
        return true;
    }

    private bool IndexExpressionSupported(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;
        return FieldsResolve(expression) && (IndexContext?.IndexEvaluator is not null || !VfpExpression.Parse(expression).ContainsFunction((name, _) =>
            !VfpRuntime.EngineFunctionNames.Contains(VfpFunctionAbbreviations.Canonical(name))));
    }
}
