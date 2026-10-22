using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D7 — CDX index WRITING surface on the writer: <see cref="CreateTag"/> builds /
/// adds a tag to the structural compound <c>.cdx</c> beside the table, and
/// <see cref="Reindex"/> rebuilds every existing tag (REINDEX). Both delegate the
/// bulk-load to <see cref="CdxIndexBuilder"/>.
/// </summary>
public sealed partial class DbfWriter
{
    /// <summary>
    /// Build (or extend) the structural compound <c>.cdx</c> beside this table with a tag for
    /// <paramref name="definition"/> (plan §D7): compute the KEY for every non-deleted record via
    /// the expression engine, apply the FOR filter, sort, and bulk-load a balanced compact B-tree.
    /// </summary>
    public void CreateTag(CdxTagDefinition definition, EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(definition);

        // FAST PATH (project-review 5.6): append the NEW tag as its OWN pages at the cdx file end and wire it
        // in with a single tag-directory INSERT — the SIBLING tags are neither re-read nor rewritten. Only the
        // new tag needs a table scan (unavoidable). REPLACING an existing name unlinks the old tag first.
        string cdx = ExistingCdxPath();
        List<CdxTagDefinition>? fastFallbackTags = null;
        if (File.Exists(cdx) && TryFastAppendTag(cdx, definition, evalContext, includeDeleted, out fastFallbackTags))
        {
            EnsureStructuralCdxAdvertised();
            InvalidateTagComputerCache();
            return;
        }

        // FALLBACK / FIRST tag: whole-file build (also the byte-exact single-tag golden path). Preserves the
        // corrupt-sidecar guard in ReadExistingTagDefinitions (never rebuild-from-nothing and wipe real tags).
        var tags = fastFallbackTags ?? ReadExistingTagDefinitions();
        tags.RemoveAll(t => string.Equals(t.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
        tags.Add(definition);
        RebuildStructuralCdx(tags, evalContext, includeDeleted);
        InvalidateTagComputerCache();
    }

    /// <summary>
    /// REINDEX — rebuild every tag of the structural <c>.cdx</c> from the live table data
    /// (plan §D7). A no-op when the table has no structural index.
    /// </summary>
    public void Reindex(EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tags = ReadExistingTagDefinitions();
        if (tags.Count == 0)
            return;
        RebuildStructuralCdx(tags, evalContext, includeDeleted);
    }

    /// <summary>
    /// Build a standalone legacy <c>.idx</c> at <paramref name="idxPath"/> for KEY
    /// <paramref name="keyExpr"/> (optional <paramref name="forExpr"/> filter) over the live table —
    /// the write side of <c>INDEX ON eExpr TO cIdx</c>. Overwrites any existing file at the path.
    /// A standalone index is NOT advertised in the DBF header (it is not auto-opened by USE).
    /// </summary>
    public void CreateStandaloneIdx(string idxPath, string keyExpr, string? forExpr = null,
        bool unique = false, EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(idxPath);
        ArgumentNullException.ThrowIfNull(keyExpr);
        Flush();
        var rows = MaterializeRows();
        IdxIndexBuilder.Build(idxPath, _schema, rows, keyExpr, forExpr, unique, evalContext, includeDeleted);
    }

    /// <summary>
    /// Build (or replace) a tag <paramref name="definition"/> in the compound <c>.cdx</c> at
    /// <paramref name="cdxPath"/> — the STRUCTURAL sidecar (<paramref name="structural"/> true, the DBF
    /// header structural bit is then advertised) or a NAMED non-structural <c>.cdx</c>
    /// (<paramref name="structural"/> false). Preserves any sibling tags already in the file.
    /// </summary>
    public void CreateTagIn(string cdxPath, bool structural, CdxTagDefinition definition,
        EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cdxPath);
        ArgumentNullException.ThrowIfNull(definition);

        // FAST PATH (project-review 5.6): append the new tag's pages + splice its directory entry, leaving
        // sibling tags untouched. Falls back to the whole-file rebuild for a legacy / unreadable sidecar.
        List<CdxTagDefinition>? fastFallbackTags = null;
        if (File.Exists(cdxPath) && TryFastAppendTag(cdxPath, definition, evalContext, includeDeleted, out fastFallbackTags))
        {
            if (structural)
                EnsureStructuralCdxAdvertised();
            InvalidateTagComputerCache();
            return;
        }

        var tags = fastFallbackTags ?? ReadTagDefinitionsFrom(cdxPath, _schema.Encoding);
        tags.RemoveAll(t => string.Equals(t.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
        tags.Add(definition);

        Flush();
        var rows = MaterializeRows();
        CdxIndexBuilder.Build(cdxPath, _schema, rows, tags, evalContext, includeDeleted);
        if (structural)
            EnsureStructuralCdxAdvertised();
        InvalidateTagComputerCache();
    }

    /// <summary>
    /// DELETE TAG — remove <paramref name="names"/> (or ALL when <paramref name="names"/> is
    /// <see langword="null"/>) from the compound <c>.cdx</c> at <paramref name="cdxPath"/>, rebuilding
    /// the remaining tags. When the last tag is removed the file is DELETED (VFP behaviour) and, for a
    /// <paramref name="structural"/> sidecar, the DBF header structural bit is cleared. Returns the number
    /// of tags remaining afterwards (0 ⇒ the file was deleted).
    /// </summary>
    public int DeleteTagsIn(string cdxPath, bool structural, IReadOnlyCollection<string>? names,
        EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cdxPath);

        var existing = ReadTagDefinitionsFrom(cdxPath, _schema.Encoding);
        if (existing.Count == 0)
            return 0;

        // Partition the current tags into survivors vs the ones the caller named (creation order preserved).
        List<CdxTagDefinition> survivors;
        List<string> toRemove;
        bool everyRequestedExists;
        if (names is null)                                  // DELETE TAG ALL
        {
            survivors = new List<CdxTagDefinition>();
            toRemove = existing.Select(t => t.Name).ToList();
            everyRequestedExists = true;
        }
        else
        {
            bool Named(CdxTagDefinition t) => names.Any(n => string.Equals(n, t.Name, StringComparison.OrdinalIgnoreCase));
            survivors = existing.Where(t => !Named(t)).ToList();
            toRemove = existing.Where(Named).Select(t => t.Name).ToList();
            everyRequestedExists = names.All(n => existing.Any(t => string.Equals(n, t.Name, StringComparison.OrdinalIgnoreCase)));
        }

        if (survivors.Count == 0)
        {
            // Removing the LAST tag deletes the file (VFP behaviour); a structural sidecar also clears the DBF
            // header structural bit. (Unchanged from the whole-rebuild path.)
            try { if (File.Exists(cdxPath)) File.Delete(cdxPath); } catch { /* best-effort */ }
            if (structural && _hasStructuralCdx)
            {
                ClearStructuralCdxFlag();
                _hasStructuralCdx = false;
            }
            InvalidateTagComputerCache();
            return 0;
        }

        // FAST PATH (project-review 5.6): unlink each named tag's directory entry and abandon its pages — the
        // surviving tags are neither re-read nor rewritten, and NO table row is materialized. Taken only when
        // every requested name actually exists (else fall back, which also matches today's silent-ignore of a
        // non-existent tag) and the sidecar is a fast-editable standard compound index.
        if (everyRequestedExists && toRemove.Count > 0 && TryFastUnlinkTags(cdxPath, toRemove, _schema.Encoding))
        {
            InvalidateTagComputerCache();
            return survivors.Count;
        }

        // FALLBACK (whole-file rebuild of the survivors from live rows) — today's behaviour, kept for a legacy /
        // unreadable sidecar and for a delete that names a non-existent tag.
        Flush();
        var rows = MaterializeRows();
        CdxIndexBuilder.Build(cdxPath, _schema, rows, survivors, evalContext, includeDeleted);
        InvalidateTagComputerCache();
        return survivors.Count;
    }

    /// <summary>Read the tag definitions currently in the compound <c>.cdx</c> at
    /// <paramref name="cdxPath"/> (empty when the file is missing). Throws only when the file exists but
    /// cannot be parsed.</summary>
    private static List<CdxTagDefinition> ReadTagDefinitionsFrom(string cdxPath, System.Text.Encoding textEncoding)
    {
        var result = new List<CdxTagDefinition>();
        if (!File.Exists(cdxPath))
            return result;

        using var index = IndexFile.Open(cdxPath);
        index.TextEncoding = textEncoding;
        var fileHeader = index.ReadCdxHeader(0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byOffset = new List<(uint Offset, CdxTagDefinition Def)>();
        foreach (var (headerOffset, nameBytes) in
                 IndexTraversal.EnumerateCompact(index, fileHeader.Root, fileHeader.KeyLength, isCharacter: true))
        {
            string name = TrimName(nameBytes, textEncoding);
            if (name.Length == 0 || !seen.Add(name))
                continue;
            var hdr = index.ReadCdxHeader(headerOffset);
            string? forExpr = hdr.HasFor && hdr.ForExpression.Length > 0 ? hdr.ForExpression : null;
            byOffset.Add((headerOffset, new CdxTagDefinition(name, hdr.KeyExpression, forExpr,
                hdr.Descending, hdr.SortOrder, hdr.IsUnique)));
        }
        byOffset.Sort((a, b) => a.Offset.CompareTo(b.Offset));   // creation (header-layout) order.
        foreach (var (_, def) in byOffset) result.Add(def);
        return result;
    }

    /// <summary>The structural <c>.cdx</c> path beside the table (same stem, <c>.cdx</c> extension).</summary>
    private string StructuralCdxPath() => SidecarFile.ChangeExtension(_stream.Name, ".cdx");

    /// <summary>
    /// Resolve the on-disk structural <c>.cdx</c> path, honouring an existing case variant
    /// (<c>.CDX</c>) beside the table; falls back to the lower-case <c>.cdx</c> stem.
    /// </summary>
    private string ExistingCdxPath()
    {
        string lower = StructuralCdxPath();
        if (File.Exists(lower))
            return lower;

        string dir = Path.GetDirectoryName(lower) is { Length: > 0 } d ? d : ".";
        if (Directory.Exists(dir))
        {
            string name = Path.GetFileName(lower);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        return lower;
    }

    /// <summary>
    /// Read the definitions of every tag currently in the structural <c>.cdx</c> (empty when none),
    /// so <see cref="CreateTag"/> can preserve siblings and <see cref="Reindex"/> can rebuild them.
    /// </summary>
    private List<CdxTagDefinition> ReadExistingTagDefinitions()
    {
        var result = new List<CdxTagDefinition>();
        string cdx = ExistingCdxPath();
        if (!File.Exists(cdx))
            return result;

        try
        {
            var textEncoding = _schema.Encoding;
            using var index = IndexFile.Open(cdx);
            index.TextEncoding = textEncoding;
            var fileHeader = index.ReadCdxHeader(0);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The directory enumerates tags in NAME (B-tree) order, but VFP numbers tags by their
            // HEADER-PAGE LAYOUT order (= creation order — the directory's "recno" is the header byte
            // offset). Collect with that offset and sort by it so a rebuild preserves creation order —
            // which is what TAG()/SYS(14)/DESCENDING() enumerate over (verified vs the VFP9 runtime).
            var byOffset = new List<(uint Offset, CdxTagDefinition Def)>();
            foreach (var (headerOffset, nameBytes) in
                     IndexTraversal.EnumerateCompact(index, fileHeader.Root, fileHeader.KeyLength, isCharacter: true))
            {
                string name = TrimName(nameBytes, textEncoding);
                if (name.Length == 0 || !seen.Add(name))
                    continue;

                var hdr = index.ReadCdxHeader(headerOffset);
                string? forExpr = hdr.HasFor && hdr.ForExpression.Length > 0 ? hdr.ForExpression : null;
                byOffset.Add((headerOffset, new CdxTagDefinition(
                    name, hdr.KeyExpression, forExpr, hdr.Descending, hdr.SortOrder, hdr.IsUnique)));
            }
            byOffset.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            foreach (var (_, def) in byOffset) result.Add(def);
        }
        catch (Exception ex)
        {
            // The sidecar EXISTS (checked above) but could not be parsed. Returning an empty list here
            // would make CreateTag rebuild the structural CDX from nothing, DESTROYING the sibling tags
            // it cannot see. Surface the failure so a corrupt .cdx can never silently wipe tags — the
            // "no sidecar" case (legitimately empty) already returned above.
            throw new InvalidDataException(
                $"The structural index '{cdx}' exists but could not be read; refusing to rebuild it from " +
                "an empty tag set (which would discard its existing tags). Repair or delete the file.", ex);
        }

        // The IndexFile reader is designed to NEVER throw on malformed pages — it just yields nothing.
        // So a sidecar that EXISTS (checked above) yet parses to ZERO tags is corrupt, not legitimately
        // empty: a structural compound index always carries at least one tag. Treat it the same as an
        // unreadable sidecar so CreateTag can't silently rebuild from nothing and wipe its real tags.
        if (result.Count == 0)
            throw new InvalidDataException(
                $"The structural index '{cdx}' exists but no tags could be read from it; refusing to rebuild " +
                "it from an empty tag set (which would discard its existing tags). Repair or delete the file.");

        return result;
    }

    private static string TrimName(byte[] raw, System.Text.Encoding? encoding)
    {
        int len = raw.Length;
        while (len > 0 && (raw[len - 1] == 0x20 || raw[len - 1] == 0x00))
            len--;
        return CodePageText.Decode(raw.AsSpan(0, len), encoding);
    }

    /// <summary>Bulk-build the structural <c>.cdx</c> for <paramref name="tags"/> over the live table.</summary>
    private void RebuildStructuralCdx(IReadOnlyList<CdxTagDefinition> tags, EvaluationContext? evalContext = null,
        bool includeDeleted = false, CdxIndexBuilder.AppendUniqueState? appendUniqueState = null)
    {
        Flush();
        var rows = MaterializeRows();
        CdxIndexBuilder.Build(StructuralCdxPath(), _schema, rows, tags, evalContext, includeDeleted, appendUniqueState);

        // The sidecar VFP just wrote is a STRUCTURAL compound .cdx (options 0xE0). Advertise it in the
        // DBF header (byte 28 bit 0) so a real VFP runtime auto-opens it on USE, and flip the writer's
        // own _hasStructuralCdx/_usesStructuralScheme so a subsequent Pack/Zap correctly invalidates
        // (deletes + ReindexNeeded) the now-owned sidecar instead of early-returning and leaving it stale.
        EnsureStructuralCdxAdvertised();
        _tagComputers = null;
    }

    // ---- incremental tag-DDL fast paths (project-review 5.6) --------------------

    /// <summary>The tag-directory key length (10-byte tag names) and its space pad — the standard compound-index
    /// directory scheme the fast paths edit in place.</summary>
    private const int DirKeyLen = 10;
    private const byte DirPad = 0x20;

    /// <summary>Test seam (project-review 5.6 structural guard): when set, <see cref="MaterializeRows"/> throws.
    /// The DELETE-TAG fast path must complete WITHOUT ever reading table rows, so a test can arm this and prove
    /// the fast path never materializes (while a whole-file rebuild — REINDEX / the fallback — would throw).</summary>
    internal bool FailMaterializeRowsForTests { get; set; }

    /// <summary>Test seam: when set, the CREATE-TAG fast path throws after a REPLACE unlink but before the new
    /// directory insert, exercising the whole-file fallback over a partially edited directory.</summary>
    internal bool FailFastTagDirectoryInsertForTests { get; set; }

    /// <summary>Advertise the structural <c>.cdx</c> in the DBF header (byte 28 bit 0) and flip the writer's own
    /// tracking so a later Pack/Zap correctly invalidates it — the shared tail of every structural tag-build
    /// path. No-op when the sidecar is already advertised.</summary>
    private void EnsureStructuralCdxAdvertised()
    {
        if (_hasStructuralCdx)
            return;
        SetStructuralCdxFlag();
        _hasStructuralCdx = true;
        _usesStructuralScheme = VfpLock.UsesStructuralScheme(_version.Code, true);
    }

    /// <summary>Drop the cached per-tag key computers after any tag DDL — a tag's DEFINITION may have changed
    /// (REPLACE), been added or removed, so incremental write-path maintenance must re-derive them from the
    /// fresh on-disk headers rather than trust a stale name-keyed cache.</summary>
    private void InvalidateTagComputerCache()
    {
        // Any tag DDL (add/replace/drop) can move header offsets or change a tag's definition, so the cached
        // incremental editor's resolved plans are now stale — flush + release it (some DDL fast paths, e.g.
        // TryFastUnlinkTags, do not route through Flush()). It re-resolves on the next append/update.
        CloseCdxMaint(flush: true);
        _tagComputers = null;
    }

    /// <summary>
    /// Try to add (or REPLACE) tag <paramref name="def"/> in the compound <c>.cdx</c> at <paramref name="cdxPath"/>
    /// WITHOUT rewriting the sibling tags (project-review 5.6): probe the tag directory, scan the table ONCE for
    /// only the new tag's keys, build just that tag's pages, append them at the file end and splice its directory
    /// entry in via <see cref="CdxTreeEditor"/>. When the name already exists it is UNLINKED first (its pages are
    /// abandoned — dead pages are acceptable; REINDEX compacts). Returns <see langword="true"/> on success;
    /// <see langword="false"/> (caller falls back to a whole-file rebuild) when the sidecar is not a fast-editable
    /// standard compound index. If a splice fails after mutation begins, <paramref name="fallbackTagDefinitions"/>
    /// receives the pre-mutation tag definitions so the rebuild does not re-read a partially edited directory. A
    /// genuine truncated-table throw from <see cref="MaterializeRows"/> propagates (as it does today) rather than
    /// silently falling back.
    /// </summary>
    private bool TryFastAppendTag(string cdxPath, CdxTagDefinition def, EvaluationContext? evalContext,
        bool includeDeleted, out List<CdxTagDefinition>? fallbackTagDefinitions)
    {
        fallbackTagDefinitions = null;

        // Probe the directory read-only FIRST so a legacy / unreadable sidecar falls back before the table scan.
        List<(string Name, long HeaderOffset)>? dir;
        try
        {
            using var probe = new FileStream(cdxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            dir = ReadDirectoryEntries(probe, _schema.Encoding);
        }
        catch { return false; }
        if (dir is null)
            return false;

        // ONE table scan for THIS tag's keys (the win is NOT rewriting the siblings). A truncated-table throw
        // here surfaces to the caller exactly as the whole-file path would (both refuse to index a partial table).
        Flush();
        var rows = MaterializeRows();

        List<CdxTagDefinition>? preMutationDefinitions = null;
        try
        {
            using var rw = new FileStream(cdxPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            long baseOffset = rw.Length;
            if (baseOffset <= 0 || baseOffset % IndexFile.PageSize != 0)
                return false;   // not a page-aligned compound index → fall back

            preMutationDefinitions = ReadTagDefinitionsFrom(cdxPath, _schema.Encoding);

            var block = CdxIndexBuilder.BuildTagBlock(_schema, rows, def, baseOffset, evalContext, includeDeleted);
            rw.Seek(baseOffset, SeekOrigin.Begin);
            rw.Write(block.Pages, 0, block.Pages.Length);
            rw.Flush();

            // Recno geometry for the DIRECTORY leaf is sized from the DATA (the largest existing tag-header byte
            // offset — the directory's "recnos" ARE header offsets), NOT from rw.Length: the just-appended block
            // (and unrelated 5.1 row-append maintenance) can push the file past an 8-bit band without the
            // directory needing a wider record field, and an rw.Length basis would then re-pack the directory
            // leaf one byte-per-entry WIDER (project-review 5.6 MUST-FIX — silent sibling loss on the REPLACE
            // unlink). The new entry's own (larger) offset is folded in by LeafGeometry on the Insert, which
            // SPLITS safely if it must; the Delete only ever narrows, so it can never overflow.
            long basis = DirectoryRecnoBasis(dir);
            byte[] nameKey = CdxIndexBuilder.DirectoryKey(def.Name, _schema.Encoding);
            using (var editor = new CdxTreeEditor(rw, _schema.Encoding))
            {
                var old = dir.FirstOrDefault(e => string.Equals(e.Name, def.Name, StringComparison.OrdinalIgnoreCase));
                if (old.Name is not null)
                    editor.Delete(0, nameKey, (uint)old.HeaderOffset, DirKeyLen, DirPad, basis);  // REPLACE: unlink old
                if (FailFastTagDirectoryInsertForTests)
                    throw new IOException("Injected fast tag-directory insert fault (test seam).");
                editor.Insert(0, nameKey, (uint)block.HeaderOffset, DirKeyLen, DirPad, basis);
                editor.Flush();
            }
            return true;
        }
        catch
        {
            // A mid-splice failure leaves at most dead appended pages + a possibly half-edited directory. Hand the
            // caller the pre-mutation definitions so the full rebuild overwrites from a clean snapshot instead of
            // re-reading the edited directory.
            fallbackTagDefinitions = preMutationDefinitions;
            return false;
        }
    }

    /// <summary>
    /// Try to UNLINK every tag in <paramref name="removeNames"/> from the compound <c>.cdx</c> at
    /// <paramref name="cdxPath"/> by deleting its tag-directory entry (via <see cref="CdxTreeEditor"/>) and
    /// abandoning its pages — WITHOUT reading any table row or touching the surviving tags (project-review 5.6).
    /// Returns <see langword="true"/> on success; <see langword="false"/> (caller falls back to a whole-file
    /// rebuild) when the sidecar is not a fast-editable standard compound index or a requested tag is not present
    /// in the directory. Never materializes table rows on the success path.
    /// </summary>
    private static bool TryFastUnlinkTags(string cdxPath, IReadOnlyList<string> removeNames, System.Text.Encoding textEncoding)
    {
        FileStream? rw = null;
        try
        {
            rw = new FileStream(cdxPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var dir = ReadDirectoryEntries(rw, textEncoding);
            if (dir is null)
                return false;

            var targets = new List<(byte[] Key, uint Recno)>(removeNames.Count);
            foreach (var nm in removeNames)
            {
                var hit = dir.FirstOrDefault(e => string.Equals(e.Name, nm, StringComparison.OrdinalIgnoreCase));
                if (hit.Name is null)
                    return false;   // a requested tag is not in the directory → fall back (matches today)
                targets.Add((CdxIndexBuilder.DirectoryKey(nm, textEncoding), (uint)hit.HeaderOffset));
            }

            // Size the directory leaf's recno field from the DATA (max tag-header offset), NOT rw.Length — the
            // file may have grown past an 8-bit band via unrelated 5.1 row-append maintenance with the directory
            // untouched, and an rw.Length basis would re-pack the leaf one byte-per-entry WIDER, overflowing a
            // near-full directory leaf and silently losing surviving tags (project-review 5.6 MUST-FIX). Header
            // offsets never move, so this basis can only leave the delete re-pack equal-or-narrower.
            long basis = DirectoryRecnoBasis(dir);
            using (var editor = new CdxTreeEditor(rw, textEncoding))
            {
                foreach (var (key, recno) in targets)
                    editor.Delete(0, key, recno, DirKeyLen, DirPad, basis);
                editor.Flush();
            }
            return true;
        }
        catch { return false; }
        finally { rw?.Dispose(); }
    }

    /// <summary>
    /// The recno-geometry basis for a DIRECTORY-leaf edit: the LARGEST tag-header byte offset the directory must
    /// represent (a directory entry's "recno" IS that tag's header offset). Deriving the compact-leaf record
    /// field width from the DATA — not from the FILE LENGTH — is what keeps a tag-directory unlink/insert safe:
    /// the cdx grows past 8-bit record-field bands (256B / 64KiB / 16MiB / 4GiB of length) via UNRELATED 5.1
    /// row-append maintenance that never edits the directory, so an <c>rw.Length</c> basis would re-pack the
    /// directory leaf one byte-per-entry WIDER and overflow a near-full leaf on a delete — silently dropping
    /// surviving tags (project-review 5.6 MUST-FIX). Tag-header offsets never move once written, so this basis
    /// never exceeds the geometry the leaf was last packed with: a delete re-pack stays equal-or-narrower (never
    /// overflows), and an insert folds the new (possibly larger) offset in via <see cref="CdxTreeEditor"/>'s own
    /// split-safe path. Floored at 1 so an empty directory still yields a valid single-byte field.
    /// </summary>
    private static long DirectoryRecnoBasis(IEnumerable<(string Name, long HeaderOffset)> dir)
    {
        long basis = 1;
        foreach (var (_, headerOffset) in dir)
            if (headerOffset > basis)
                basis = headerOffset;
        return basis;
    }

    /// <summary>Read the compound-index tag directory over <paramref name="stream"/> as (tag name → header byte
    /// offset) pairs, or <see langword="null"/> when the file is not a fast-editable STANDARD compound index
    /// (directory key length ≠ 10, unreadable, or no tags) — the signal for the tag-DDL fast paths to fall back
    /// to a whole-file rebuild. Reads only index pages; never touches the table.</summary>
    private static List<(string Name, long HeaderOffset)>? ReadDirectoryEntries(FileStream stream, System.Text.Encoding textEncoding)
    {
        try
        {
            using var index = IndexFile.Open(stream, leaveOpen: true);
            index.TextEncoding = textEncoding;
            var fileHeader = index.ReadCdxHeader(0);
            if (fileHeader.KeyLength != DirKeyLen)
                return null;   // not the 10-byte tag-name directory scheme → not fast-editable here
            var result = new List<(string, long)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (headerOffset, nameBytes) in
                     IndexTraversal.EnumerateCompact(index, fileHeader.Root, fileHeader.KeyLength, isCharacter: true))
            {
                string name = TrimName(nameBytes, textEncoding);
                if (name.Length == 0 || !seen.Add(name))
                    continue;
                result.Add((name, headerOffset));
            }
            return result.Count == 0 ? null : result;
        }
        catch { return null; }
    }

    /// <summary>
    /// Set the structural-CDX flag (header byte 28, bit <c>0x01</c>) in place, preserving every other
    /// flag in that byte (e.g. the memo bit <c>0x02</c>). The inverse of
    /// <see cref="ClearStructuralCdxFlag"/>. No-op if the bit is already set.
    /// </summary>
    private void SetStructuralCdxFlag()
    {
        _stream.Seek(28, SeekOrigin.Begin);
        int current = _stream.ReadByte();
        if (current < 0)
            return; // header truncated — nothing safe to rewrite.

        byte set = (byte)(current | 0x01);
        if (set == (byte)current)
            return; // already set.

        _stream.Seek(28, SeekOrigin.Begin);
        _stream.WriteByte(set);
        _stream.Flush();
    }

    /// <summary>
    /// Snapshot every physical record (1-based recno + decoded row) straight off the raw handle via
    /// <see cref="RandomAccess"/>, using the writer's LIVE record count — the schema's cached count
    /// is stale once records have been appended. Deleted-record filtering happens in the builder.
    /// </summary>
    private List<CdxIndexBuilder.BuildRow> MaterializeRows()
    {
        if (FailMaterializeRowsForTests)
            throw new InvalidOperationException(
                "Injected MaterializeRows fault (test seam): a fast tag-DDL path must not read table rows.");

        int count = _recordCount;
        var rows = new List<CdxIndexBuilder.BuildRow>(count);
        var buffer = new byte[_recordLength];
        for (int i = 0; i < count; i++)
        {
            long offset = _headerLength + (long)i * _recordLength;
            int read = ReadRawInto(_handle, offset, buffer);
            if (read < _recordLength)
                // A truncated tail means the file is shorter than its header's record count claims —
                // silently dropping the missing rows would build an index over an INCOMPLETE table.
                throw new InvalidDataException(
                    $"Table is truncated: expected {count} records but record {i + 1} is incomplete " +
                    $"({read} of {_recordLength} bytes). Refusing to index a partial table.");
            var copy = (byte[])buffer.Clone();
            rows.Add(new CdxIndexBuilder.BuildRow(i + 1, new DbfRecord(_schema, copy)));
        }
        return rows;
    }
}
