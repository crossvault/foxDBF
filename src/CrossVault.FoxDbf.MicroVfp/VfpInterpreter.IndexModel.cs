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

/// <summary>microVFP index/order model — INDEX ON/REINDEX/DELETE TAG/SET INDEX/SET ORDER/SET COLLATE, the multi-index inventory + tag introspection functions.</summary>
public sealed partial class VfpInterpreter
{
    private void ExecSetOrder(SetOrderStmt so)
    {
        int area = so.In is not null ? ResolveAreaRef(so.In) : Session.CurrentArea;
        if (area <= 0) return;
        var m = Meta(area);
        string? name = so.Order is null ? null : NameOf(so.Order).Trim();
        m.Order = ResolveOrderName(area, name);
        // Changing the controlling order turns off any active SET KEY range (hackfox s4g704) and
        // re-bases the cached index sequence so a following GO TOP / SKIP walks the NEW order.
        m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
        m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1;
        // Per-call ASCENDING|DESCENDING override (task scope): the explicit clause selects the ABSOLUTE
        // traversal direction for this order, overriding the tag's own stored Descending. We track it as
        // an OrderReversed flag = (explicit direction) XOR (tag's stored direction); ActiveOrder reverses
        // the cached recno sequence when set, so GO TOP/BOTTOM/SKIP all follow the requested direction.
        // (NB: hackfox s4g093 per-tag direction PERSISTENCE across a later clause-less SET ORDER is a
        // separate, out-of-scope refinement — here the override lasts until the next SET ORDER.)
        if (so.Direction is bool wantDescending)
        {
            bool tagDescending = MasterDescending(area);
            m.OrderReversed = wantDescending != tagDescending;
        }
        else
        {
            m.OrderReversed = false;
        }
    }

    /// <summary>Resolve a SET ORDER operand to a tag NAME (case-insensitively matched at use):
    /// a blank / <c>"0"</c> ⇒ natural (record) order (<see langword="null"/>); a positive number ⇒ the
    /// n-th tag (1-based) of the area's structural <c>.cdx</c>; otherwise the named tag. An UNKNOWN tag
    /// name or an OUT-OF-RANGE index number raises a catchable error (VFP 1683 "Tag … not found" /
    /// index-number-out-of-range) instead of silently degrading to natural order.</summary>
    private string? ResolveOrderName(int area, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var inv = IndexInventory(area);   // full open set: idx + structural cdx tags + extra cdx tags.
        if (int.TryParse(name, out int n))
        {
            if (n <= 0) return null;   // SET ORDER TO 0 ⇒ natural/record order.
            if (n <= inv.Count) return inv[n - 1].Name;
            throw new MicroVfpRuntimeException(
                $"SET ORDER TO {n}: index number is out of range (the work area has {inv.Count} index(es)).");
        }
        foreach (var slot in inv)
            if (string.Equals(slot.Name, name, StringComparison.OrdinalIgnoreCase))
                return slot.Name;
        throw new MicroVfpRuntimeException($"SET ORDER TO {name}: tag not found in the current work area.");
    }

    // ─────────────────────────── INDEX / REINDEX (microVFP P1 gap #1) ───────────────────────────
    //
    // INDEX ON eKey TAG cTag [FOR lExpr] [ASCENDING|DESCENDING] [UNIQUE|CANDIDATE] [ADDITIVE] builds (or
    // replaces) a tag in the STRUCTURAL .cdx via the byte-exact CDX builder (DbfWriter.CreateTag), then
    // makes it the controlling order. TO <idx> (standalone) and TAG … OF <cdx> (non-structural) are
    // explicit, catchable refusals — there is no .idx writer / multi-CDX-per-area model yet. CANDIDATE
    // builds the tag then verifies key-uniqueness, rolling the files back + raising on a duplicate.
    //
    // Deleted records: the build honours the LIVE SET DELETED — SET DELETED ON (the microVFP default)
    // excludes deleted rows; SET DELETED OFF indexes them too (VFP keeps their CDX entries until PACK).
    // The live SET EXACT / SET ANSI ride along into the KEY/FOR expression evaluation (via _ctx) so a
    // character `=` in a FOR/KEY filters exactly as a VFP run would. A FOR clause is honoured by the
    // builder. SET FILTER (now modelled — see SetFilter/Visible) deliberately does NOT narrow the build set:
    // VFP's INDEX ON builds over every record regardless of the active filter, so the builder ignores it too.
    private void ExecIndex(IndexStmt ix)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null)
            throw new MicroVfpRuntimeException("INDEX ON: no table is open in the current work area.");

        if (wa.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("INDEX ON: the current table has no file on disk.");

        string keyExpr = ix.Key.Text.Trim();
        string? forExpr = ix.For?.Text is { } f && f.Trim().Length > 0 ? f.Trim() : null;
        bool candidate = ix.Candidate;
        // UNIQUE clause OR the SET UNIQUE session default (candidate is a distinct constraint, not UNIQUE).
        bool unique = ix.Unique || (Runtime.Unique && !candidate);
        string collation = _ctx.Collation?.Name ?? "MACHINE";
        var m = Meta(area);

        // ── INDEX ON eExpr TO <idx> — build a standalone legacy .idx and make it the controlling order. ──
        if (ix.ToIdx is not null)
        {
            string idxPath = ResolveSidecarPath(path, NameOf(ix.ToIdx).Trim(), ".idx");
            BuildTagOnDisk(path, w => w.CreateStandaloneIdx(idxPath, keyExpr, forExpr, unique, _ctx, !_ctx.Deleted));
            AddExtraIndex(area, idxPath);
            m.Order = IdxOrderName(idxPath);
            ResetOrderState(m);
            GoTop(area);
            return;
        }

        if (ix.Tag is null)
            throw new MicroVfpRuntimeException("INDEX ON: a TAG name is required.");

        // VFP UPPERCASES the tag name in the .cdx directory (verified byte-for-byte vs VFP9); the KEY /
        // FOR expression case is PRESERVED as typed (matches the reverse-engineered DBC .dcx). Upper-casing is
        // per code page: VFP9 maps razón → RAZÓN and ÿ → Ÿ, but leaves ƒ alone (CP1252 has no 'Ƒ').
        string tagName = CodePageText.ToUpperInCodePage(NameOf(ix.Tag).Trim(), wa.Table.Encoding);
        if (tagName.Length == 0)
            throw new MicroVfpRuntimeException("INDEX ON: a TAG name is required.");

        var def = new CdxTagDefinition(tagName, keyExpr, forExpr, ix.Descending, collation, unique);

        // ── INDEX ON eExpr TAG cTag OF <cdx> — build a tag in a NAMED (non-structural) compound index. ──
        if (ix.OfCdx is not null)
        {
            string cdxPath = ResolveSidecarPath(path, NameOf(ix.OfCdx).Trim(), ".cdx");
            BuildTagOnDisk(path, w => w.CreateTagIn(cdxPath, structural: false, def, _ctx, !_ctx.Deleted));
            AddExtraIndex(area, cdxPath);
            m.Order = tagName;
            ResetOrderState(m);
            GoTop(area);
            return;
        }

        // ── structural .cdx tag (the already-shipped path). ──
        // CANDIDATE: capture the pre-image so a uniqueness violation rolls the .cdx/.dbf back (VFP does not
        // create the tag on a duplicate).
        FileSnapshot? pre = candidate ? CaptureSnapshot(path) : null;
        try
        {
            BuildTagOnDisk(path, w => w.CreateTag(def, _ctx, includeDeleted: !_ctx.Deleted));

            if (candidate)
            {
                var built = Session.AreaAt(area)?.Cdx;
                var tag = built?.Tag(tagName) ?? built?.Tag(tagName.ToUpperInvariant());
                if (tag is not null && HasDuplicateKeys(tag))
                {
                    RollbackFiles(pre!, path);
                    throw new MicroVfpRuntimeException(
                        $"INDEX ON … TAG {tagName} CANDIDATE: uniqueness violated — a duplicate key value exists.", 1884); // VFP err 1884 (oracle-pinned).
                }
                // The tag is now valid + candidate: remember its candidacy so a later write that duplicates a
                // key raises (the on-disk tag looks plain for a free table, so nothing else could tell).
                RegisterCandidateTag(path, tagName);
            }

            // The new tag becomes the controlling order (VFP behaviour) and the pointer goes to its top.
            m.Order = tagName;
            ResetOrderState(m);
            GoTop(area);
        }
        finally { pre?.Cleanup(); }   // 6.3: on the no-violation path the pre-image temps are unused — delete them (idempotent: RollbackFiles already cleaned on the abort path).
    }

    /// <summary>Reset the cached index sequence + any SET KEY range after the controlling order changes
    /// (a fresh INDEX ON / SET ORDER re-bases GO TOP / SKIP and clears any DESCENDING override).</summary>
    private static void ResetOrderState(AreaMeta m)
    {
        m.OrderReversed = false;
        m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1;
        m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
    }

    /// <summary>Resolve a sidecar index file path relative to the table's directory: an explicit
    /// extension is honoured, else <paramref name="defaultExt"/> is appended.</summary>
    private static string ResolveSidecarPath(string tablePath, string name, string defaultExt)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(tablePath)) ?? ".";
        string file = Path.HasExtension(name) ? name : name + defaultExt;
        return Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(dir, file));
    }

    /// <summary>The controlling-order identity a standalone <c>.idx</c> is addressed by — its file stem,
    /// uppercased (an <c>.idx</c> has no tag name).</summary>
    private static string IdxOrderName(string idxPath)
        => Path.GetFileNameWithoutExtension(idxPath).ToUpperInvariant();

    /// <summary>Track <paramref name="fullPath"/> as an open non-structural index of <paramref name="area"/>
    /// (idempotent; case-insensitive).</summary>
    private void AddExtraIndex(int area, string fullPath)
    {
        var m = Meta(area);
        m.ExtraIndexes ??= new List<string>();
        if (!m.ExtraIndexes.Any(p => SamePath(p, fullPath)))
            m.ExtraIndexes.Add(Path.GetFullPath(fullPath));
    }

    // ─────────────────────────── DELETE TAG / SET INDEX (microVFP INDEX/ORDER MODEL) ───────────────────────────

    /// <summary>DELETE TAG cTag[, …] | ALL [OF cCdx] — rebuild the target compound <c>.cdx</c> without the
    /// named tag(s) (structural by default, or the named <c>OF</c> file). ALL removes every tag (and, when
    /// the file becomes empty, VFP deletes it). Deleting the controlling order reverts to natural order.</summary>
    private void ExecDeleteTag(DeleteTagStmt dt)
    {
        int area = dt.In is not null ? ResolveAreaRef(dt.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("DELETE TAG: no table is open in the work area.");

        bool structural = dt.OfCdx is null;
        string cdxPath = structural
            ? Path.ChangeExtension(path, ".cdx")
            : ResolveSidecarPath(path, NameOf(dt.OfCdx!).Trim(), ".cdx");
        IReadOnlyCollection<string>? names = dt.All ? null : dt.Tags;

        BuildTagOnDisk(path, w => w.DeleteTagsIn(cdxPath, structural, names, _ctx, includeDeleted: !_ctx.Deleted));

        // A non-structural .cdx that was emptied+deleted is no longer an open index.
        if (!structural && !File.Exists(cdxPath))
        {
            var mm = Meta(area);
            mm.ExtraIndexes?.RemoveAll(p => SamePath(p, cdxPath));
        }

        // If the controlling order was among the removed tags, VFP reverts to natural (record) order.
        var m = Meta(area);
        if (!string.IsNullOrEmpty(m.Order))
        {
            var src = OpenOrderSource(area, m.Order!);
            if (src is null) { m.Order = null; ResetOrderState(m); }
            else src.Dispose();
        }
    }

    /// <summary>SET INDEX TO [cList] [ORDER …] [ADDITIVE] — open the listed non-structural index files in
    /// the current work area. Without ADDITIVE the previously-opened non-structural indexes are closed
    /// first; <c>SET INDEX TO</c> (no args) closes them all. ORDER selects the controlling tag.</summary>
    private void ExecSetIndex(SetIndexStmt si)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("SET INDEX TO: no table is open in the current work area.");
        var m = Meta(area);

        if (!si.Additive)
            m.ExtraIndexes = null;   // replace: close previously-opened non-structural indexes.

        foreach (var f in si.Files)
        {
            string fp = ResolveExistingIndexPath(path, NameOf(f).Trim());
            if (!File.Exists(fp))
                throw new MicroVfpRuntimeException($"SET INDEX TO: index file '{NameOf(f)}' was not found.");
            AddExtraIndex(area, fp);
        }

        if (si.Order is not null)
        {
            m.Order = ResolveOrderName(area, NameOf(si.Order).Trim());
            if (si.Direction is bool wantDesc)
            {
                bool tagDesc = MasterDescending(area);
                m.OrderReversed = wantDesc != tagDesc;
            }
            else m.OrderReversed = false;
            m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1;
            m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
            GoTop(area);
        }
        else if (!string.IsNullOrEmpty(m.Order))
        {
            // Closing indexes may have invalidated the controlling order → revert to natural order.
            if (OpenOrderSource(area, m.Order!) is { } src) src.Dispose();
            else { m.Order = null; ResetOrderState(m); }
        }
    }

    // ─────────────────────────── multi-index inventory + order resolution ───────────────────────────

    /// <summary>One addressable index in a work area's OPEN SET, in tag-number order.</summary>
    private sealed class IndexSlot
    {
        public string Name = "";       // tag name (CDX) or file stem uppercased (standalone IDX).
        public string FilePath = "";   // owning index file.
        public bool IsIdx;
        public bool Structural;
        public string KeyExpr = "";
        public string ForExpr = "";
        public bool Descending;
        public bool Unique;
        public string Collation = "MACHINE";
    }

    /// <summary>The area's open index set in TAG-NUMBER order: standalone <c>.idx</c> files (open order)
    /// first, then the structural <c>.cdx</c> tags (creation/header-layout order), then each additional
    /// <c>.cdx</c>'s tags (open order). Opens the extra files on demand — never held long-term.</summary>
    private List<IndexSlot> IndexInventory(int area)
    {
        var slots = new List<IndexSlot>();
        var wa = Session.AreaAt(area);
        if (wa is null) return slots;
        var extras = _meta.TryGetValue(area, out var m) ? m.ExtraIndexes : null;

        // 1) standalone .idx files (open order).
        if (extras is not null)
            foreach (var p in extras)
                if (IsIdxPath(p))
                    try
                    {
                        using var idx = IdxFile.Open(p, wa.Table.Encoding);
                        var h = idx.Header;
                        slots.Add(new IndexSlot
                        {
                            Name = IdxOrderName(p), FilePath = p, IsIdx = true,
                            KeyExpr = h.KeyExpression.Trim(), ForExpr = h.ForExpression.Trim(),
                            Descending = false, Unique = h.IsUnique, Collation = "MACHINE",
                        });
                    }
                    catch { /* unreadable idx → skip */ }

        // 2) structural .cdx tags (creation order = ascending root-page offset).
        if (wa.Cdx is not null)
            foreach (var tag in TagsInLayoutOrder(wa.Cdx))
                slots.Add(SlotForTag(tag, wa.Cdx.SourcePath ?? string.Empty, structural: true));

        // 3) additional .cdx tags (open order).
        if (extras is not null)
            foreach (var p in extras)
                if (!IsIdxPath(p))
                    try
                    {
                        using var cdx = CdxFile.Open(p, wa.Table);
                        foreach (var tag in TagsInLayoutOrder(cdx))
                            slots.Add(SlotForTag(tag, p, structural: false));
                    }
                    catch { /* unreadable cdx → skip */ }

        return slots;
    }

    private static IndexSlot SlotForTag(Index.CdxTag tag, string filePath, bool structural) => new()
    {
        Name = tag.Name, FilePath = filePath, IsIdx = false,
        KeyExpr = tag.KeyExpression.Trim(), ForExpr = tag.ForExpression.Trim(),
        Descending = tag.Descending, Unique = tag.IsUnique,
        Collation = string.IsNullOrEmpty(tag.Collation) ? "MACHINE" : tag.Collation,
        Structural = structural,
    };

    /// <summary>The tags of a compound index in CREATION (header-page layout) order — ascending root-page
    /// offset (the directory enumerates them by NAME, but VFP numbers by layout order).</summary>
    private static IEnumerable<Index.CdxTag> TagsInLayoutOrder(CdxFile cdx)
        => cdx.TagNames.Select(n => cdx.Tag(n)).Where(t => t is not null).Select(t => t!)
              .OrderBy(t => t.RootPageOffset);

    private static bool IsIdxPath(string p)
        => string.Equals(Path.GetExtension(p), ".idx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolve an index-file name for SET INDEX / USE … INDEX: an explicit extension wins, else an
    /// existing <c>.cdx</c> then <c>.idx</c> beside the table, defaulting to <c>.cdx</c>.</summary>
    private static string ResolveExistingIndexPath(string tablePath, string name)
    {
        if (Path.HasExtension(name)) return ResolveSidecarPath(tablePath, name, Path.GetExtension(name));
        foreach (var ext in new[] { ".cdx", ".idx" })
        {
            string cand = ResolveSidecarPath(tablePath, name, ext);
            if (File.Exists(cand)) return cand;
        }
        return ResolveSidecarPath(tablePath, name, ".cdx");
    }

    /// <summary>A live handle on a controlling ORDER (a CDX tag or a standalone IDX), plus the temp file
    /// handle to release when done (null for the structural <c>.cdx</c>, which the work area owns).</summary>
    private sealed class OrderSource : IDisposable
    {
        public Index.CdxTag? CdxTag;
        public IdxFile? Idx;
        public IndexKeyType IdxKeyType;
        public Encoding IdxCharacterEncoding = Encoding.Latin1;
        public bool Descending;
        public string Name = "";
        private readonly IDisposable? _owner;
        public OrderSource(IDisposable? owner) => _owner = owner;
        public void Dispose() => _owner?.Dispose();

        // ── uniform key metadata across a CDX tag OR a standalone .idx (legacy .idx is always MACHINE-
        // collated raw code-page bytes, ascending) so the SET KEY / master-tag callers work regardless
        // of which open index the controlling order was sourced from. ──
        public bool IsCharacterKey => CdxTag?.IsCharacterKey ?? (IdxKeyType == IndexKeyType.Character);
        public IndexKeyType KeyType => CdxTag?.KeyType ?? IdxKeyType;
        public int KeyLength => CdxTag?.KeyLength ?? (Idx?.KeyLength ?? 0);
        public string CollationName =>
            CdxTag is { } t && !string.IsNullOrEmpty(t.Collation) ? t.Collation : "MACHINE";
        public IndexKey DecodeKey(byte[] keyBytes) =>
            CdxTag is { } t ? t.DecodeKey(keyBytes) : IndexKey.Decode(keyBytes, IdxKeyType);
    }

    /// <summary>Resolve an ORDER identity (tag name or standalone-IDX stem) to a live <see cref="OrderSource"/>
    /// across the FULL open index set (structural <c>.cdx</c>, then extra <c>.idx</c>/<c>.cdx</c>), or null
    /// when unresolved. The caller MUST dispose the result.</summary>
    private OrderSource? OpenOrderSource(int area, string identity)
    {
        var wa = Session.AreaAt(area);
        if (wa is null || string.IsNullOrEmpty(identity)) return null;

        // structural .cdx first (owner null — the work area keeps it open).
        if (wa.Cdx is not null)
        {
            var t = wa.Cdx.Tag(identity) ?? wa.Cdx.Tag(identity.ToUpperInvariant());
            if (t is not null) return new OrderSource(null) { CdxTag = t, Descending = t.Descending, Name = t.Name };
        }

        var extras = _meta.TryGetValue(area, out var m) ? m.ExtraIndexes : null;
        if (extras is null) return null;
        foreach (var p in extras)
        {
            if (IsIdxPath(p))
            {
                if (string.Equals(IdxOrderName(p), identity, StringComparison.OrdinalIgnoreCase))
                {
                    var idx = IdxFile.Open(p, wa.Table.Encoding);
                    return new OrderSource(idx)
                    {
                        Idx = idx, Name = IdxOrderName(p),
                        IdxKeyType = IndexKey.ResolveType(idx.Header.KeyExpression.Trim(), wa.Table),
                        IdxCharacterEncoding = IdxIndexBuilder.ResolveCharacterEncoding(
                            wa.Table, idx.Header.KeyExpression.Trim()),
                    };
                }
            }
            else
            {
                var cdx = CdxFile.Open(p, wa.Table);
                var t = cdx.Tag(identity) ?? cdx.Tag(identity.ToUpperInvariant());
                if (t is not null)
                    return new OrderSource(cdx) { CdxTag = t, Descending = t.Descending, Name = t.Name };
                cdx.Dispose();
            }
        }
        return null;
    }

    /// <summary>The default controlling-order identity for a SEEK with no explicit tag and no active order:
    /// the current order, else the first structural tag, else the first inventory slot.</summary>
    private string? DefaultOrderIdentity(int area)
    {
        var m = Meta(area);
        if (!string.IsNullOrEmpty(m.Order)) return m.Order;
        var inv = IndexInventory(area);
        return inv.Count > 0 ? inv[0].Name : null;
    }

    /// <summary>The (key, recno) entries of an order in CONTROLLING order (a CDX tag reverses for a
    /// DESCENDING tag; a standalone IDX is ascending).</summary>
    private static IEnumerable<(byte[] Key, int Recno)> OrderedEntries(OrderSource src)
    {
        if (src.CdxTag is { } t)
            return t.EnumerateEntries().Select(e => (e.Key, (int)e.RecordNumber));
        if (src.Idx is { } idx)
            return idx.EnumerateEntries().Select(e => (e.Key, (int)e.RecordNumber));
        return Array.Empty<(byte[], int)>();
    }

    private void ExecReindex(ReindexStmt rix)
    {
        int area = rix.In is not null ? ResolveAreaRef(rix.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            return;   // no open table / no file — REINDEX is a no-op (nothing to rebuild).
        BuildTagOnDisk(path, w => w.Reindex(_ctx, includeDeleted: !_ctx.Deleted));
        var m = Meta(area);
        m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1; m.KeyVisible = null;
    }

    /// <summary>Run an index-mutating writer action against <paramref name="path"/>: release every work
    /// area riding the file (so the exclusive writer + the .cdx rewrite never hit a sharing conflict),
    /// perform <paramref name="action"/>, re-open the areas in place, and drop their record/order caches.</summary>
    private void BuildTagOnDisk(string path, Action<DbfWriter> action)
    {
        string full = Path.GetFullPath(path);
        SnapshotForTxn(full);
        // 5.5: drop any cached writer first — this opens an EXCLUSIVE writer (FileShare.None), which a
        // lingering Shared cached-writer handle would deny, and the index rewrite invalidates its state.
        // 5.13: force-close even a lock-pinned writer (else the EXCLUSIVE open throws) and re-take the lock
        // after the rebuild, so an explicit RLOCK/FLOCK survives a REINDEX/INDEX.
        ForceCloseCachedWriter(full);
        var reopen = Session.CloseAreasForPath(full);
        try
        {
            using var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Exclusive });
            action(writer);
        }
        finally
        {
            Session.ReopenAreas(reopen);
        }
        ResetMetaCachesForPath(path);
        ReacquireHeldLocks(full);
    }

    /// <summary>Drop the cached record / index-order / key-range state of every open area riding
    /// <paramref name="path"/> (after its files were rewritten out-of-band by an INDEX/REINDEX).</summary>
    private void ResetMetaCachesForPath(string path)
    {
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, path) && _meta.TryGetValue(w.Area, out var mm))
            {
                mm.Cached = null; mm.Ordered = null; mm.OrderedFor = null; mm.OrderPos = -1; mm.KeyVisible = null;
            }
    }

    /// <summary>Remember that <paramref name="tag"/> on <paramref name="path"/> was created CANDIDATE.</summary>
    private void RegisterCandidateTag(string path, string tag)
    {
        string key = Path.GetFullPath(path);
        if (!_candidateTags.TryGetValue(key, out var set))
            _candidateTags[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(tag);
    }

    /// <summary>The CANDIDATE tag names registered for <paramref name="path"/>, or null when none.</summary>
    private HashSet<string>? CandidateTagsFor(string path)
        => _candidateTags.TryGetValue(Path.GetFullPath(path), out var set) && set.Count > 0 ? set : null;

    /// <summary>Run an INSERT against a table carrying CANDIDATE tag(s): perform the append (which funnels the
    /// incremental index maintenance), then re-check every candidate tag for a duplicate key. A violation
    /// rolls the write back (the .dbf/.cdx pre-image) and raises the same catchable error INDEX ON … CANDIDATE
    /// raises.</summary>
    private void EnforceCandidateInsert(InsertStmt ins, string path, string? candidateRegistrationPath = null)
    {
        var pre = CaptureSnapshot(path);
        try
        {
            try { Session.Execute(ins.Sql); }
            catch { RestoreSnapshot(pre); throw; }
            ReopenFileAreas(path);

            string? bad = FirstViolatedCandidate(path, candidateRegistrationPath);
            if (bad is not null)
            {
                RestoreSnapshot(pre);
                throw new MicroVfpRuntimeException(
                    $"INSERT INTO {NameOfTable(ins)}: CANDIDATE tag {bad} uniqueness violated — a duplicate key value exists.", 1884); // VFP err 1884 (oracle-pinned).
            }
        }
        finally { pre.Cleanup(); }   // 6.3: on the clean path the pre-image temps are unused — delete them (idempotent after a RestoreSnapshot).
    }

    /// <summary>The first registered CANDIDATE tag on <paramref name="path"/> that now holds a duplicate key,
    /// or null when all are still unique. Opens the table + its structural <c>.cdx</c> read-only.</summary>
    private string? FirstViolatedCandidate(string path, string? candidateRegistrationPath = null)
    {
        var set = CandidateTagsFor(candidateRegistrationPath ?? path);
        if (set is null) return null;
        string cdx = Path.ChangeExtension(path, ".cdx");
        if (!File.Exists(cdx)) return null;
        using var table = DbfTable.Open(path, new DbfOptions { LockMode = LockMode.Shared });
        using var cdxFile = CdxFile.Open(cdx, table);
        foreach (var name in set)
        {
            var tag = cdxFile.Tag(name) ?? cdxFile.Tag(name.ToUpperInvariant());
            if (tag is not null && HasDuplicateKeys(tag))
                return name;
        }
        return null;
    }

    /// <summary>The target table name of an INSERT for a diagnostic message (best-effort).</summary>
    private static string NameOfTable(InsertStmt ins)
        => (ins.Parsed as InsertStatement)?.Table ?? "?";

    /// <summary>True when <paramref name="tag"/> has two entries with identical key bytes — a CANDIDATE
    /// violation. The tag stores entries in key order, so any duplicate keys are adjacent.</summary>
    private static bool HasDuplicateKeys(CdxTag tag)
    {
        byte[]? prev = null;
        foreach (var e in tag.EnumerateEntries())
        {
            if (prev is not null && prev.AsSpan().SequenceEqual(e.Key))
                return true;
            prev = e.Key;
        }
        return false;
    }

    /// <summary>Roll a table's <c>.dbf</c>/<c>.cdx</c> back to <paramref name="pre"/> (used when a CANDIDATE
    /// INDEX must not persist): close the areas, restore/delete the sidecars, then re-open. When the
    /// pre-image had NO <c>.cdx</c> the freshly written one is DELETED (not left orphaned on disk).</summary>
    private void RollbackFiles(FileSnapshot pre, string path)
    {
        // 5.5/5.13: release the cached handle before File.WriteAllBytes rewrites the files — force-closing
        // even a lock-pinned writer so the rewrite is not silently blocked, then re-take the lock after.
        ForceCloseCachedWriter(path);
        var reopen = Session.CloseAreasForPath(Path.GetFullPath(path));
        try
        {
            try { if (pre.DbfTemp is not null) File.Copy(pre.DbfTemp, pre.Path, overwrite: true); } catch { }
            string cdx = Path.ChangeExtension(pre.Path, ".cdx");
            if (pre.CdxTemp is not null) { try { File.Copy(pre.CdxTemp, cdx, overwrite: true); } catch { } }
            else { try { if (File.Exists(cdx)) File.Delete(cdx); } catch { } }
        }
        finally
        {
            Session.ReopenAreas(reopen);
        }
        ResetMetaCachesForPath(path);
        ReacquireHeldLocks(path);
        pre.Cleanup();   // 6.3: the pre-image temps are consumed — delete them.
    }

    // ─────────────────────────── SET COLLATE / SET KEY (microVFP P1 gap #1) ───────────────────────────

    /// <summary>SET COLLATE TO cSeq — set the session collation baked into the next INDEX tag. Only
    /// MACHINE + GENERAL are supported; any other sequence is a catchable error (never a silent fallback).
    /// <c>SET COLLATE TO</c> (no arg) resets to MACHINE. Read back by <c>SET("COLLATE")</c>.</summary>
    private void SetCollate(string arg)
    {
        string rest = arg;
        if (PrgScan.FirstWord(rest).Equals("TO", StringComparison.OrdinalIgnoreCase))
            rest = PrgScan.AfterFirstWord(rest);
        string seq = rest.Trim().Trim('"', '\'').Trim();
        if (seq.Length == 0) { _ctx.Collation = VfpCollations.Machine; return; }
        _ctx.Collation = seq.ToUpperInvariant() switch
        {
            "MACHINE" => VfpCollations.Machine,
            "GENERAL" => VfpCollations.General,
            _ => throw new MicroVfpRuntimeException(
                $"SET COLLATE TO {seq}: collating sequence not supported (only MACHINE and GENERAL)."),
        };
    }

    // ─────────────────────────── index-introspection functions ───────────────────────────

    /// <summary>Parse the shared <c>[cIndexFile,] nIndexNumber [, cAlias|nWorkArea]</c> argument shape and
    /// resolve the addressed <see cref="IndexSlot"/> from the area's open set — null when the number is
    /// absent (except a master-tag fallback), out of range, or no index is open.</summary>
    private IndexSlot? ResolveSlot(VfpValue[] a)
    {
        int ai = 0, area = Session.CurrentArea;
        string? file = null;
        int? n = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length && IsNumeric(a[ai])) { n = (int)a[ai].AsNumber; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);

        var inv = FilterInventory(IndexInventory(area), file);
        if (n is null)   // no number ⇒ the master (controlling) tag.
        {
            string? ord = Meta(area).Order;
            return ord is null ? null : inv.FirstOrDefault(s => string.Equals(s.Name, ord, StringComparison.OrdinalIgnoreCase));
        }
        return n >= 1 && n <= inv.Count ? inv[n.Value - 1] : null;
    }

    /// <summary>Narrow an inventory to a single named index file (by file name, extension optional); the
    /// whole set when <paramref name="file"/> is null/empty.</summary>
    private static List<IndexSlot> FilterInventory(List<IndexSlot> inv, string? file)
    {
        if (string.IsNullOrEmpty(file)) return inv;
        string want = Path.GetFileNameWithoutExtension(file);
        return inv.Where(s => string.Equals(Path.GetFileNameWithoutExtension(s.FilePath), want, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private int FnTagCount(VfpValue[] a)
    {
        int area = Session.CurrentArea;
        string? file = null;
        if (a.Length >= 1) { if (a[0].Type == VfpType.Character) file = a[0].AsString; else area = AreaNumber(a[0]); }
        if (a.Length >= 2) area = AreaNumber(a[1]);
        return FilterInventory(IndexInventory(area), file).Count;
    }

    private string FnTag(VfpValue[] a)
    {
        int ai = 0, area = Session.CurrentArea;
        string? file = null;
        int? n = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length && IsNumeric(a[ai])) { n = (int)a[ai].AsNumber; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);
        var inv = FilterInventory(IndexInventory(area), file);
        if (n is null) return Meta(area).Order ?? string.Empty;   // master tag name.
        return n >= 1 && n <= inv.Count ? inv[n.Value - 1].Name : string.Empty;
    }

    private int FnTagNo(VfpValue[] a)
    {
        int ai = 0, area = Session.CurrentArea;
        string? tagName = null, file = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { tagName = a[ai].AsString; ai++; }
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);
        var inv = FilterInventory(IndexInventory(area), file);
        string? target = tagName ?? Meta(area).Order;   // no name ⇒ the controlling order.
        if (string.IsNullOrEmpty(target)) return 0;
        for (int i = 0; i < inv.Count; i++)
            if (string.Equals(inv[i].Name, target, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    private string FnCdx(VfpValue[] a)
    {
        if (a.Length == 0 || !IsNumeric(a[0])) return string.Empty;
        int n = (int)a[0].AsNumber;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var files = FileInventory(area);
        return n >= 1 && n <= files.Count ? files[n - 1] : string.Empty;
    }

    private string FnNdx(VfpValue[] a)
    {
        if (a.Length == 0 || !IsNumeric(a[0])) return string.Empty;
        int n = (int)a[0].AsNumber;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var idxFiles = IdxFileInventory(area);
        return n >= 1 && n <= idxFiles.Count ? idxFiles[n - 1] : string.Empty;
    }

    private string FnOrder(VfpValue[] a)
    {
        int area = a.Length > 0 ? AreaNumber(a[0]) : Session.CurrentArea;
        return Meta(area).Order ?? string.Empty;
    }

    /// <summary>Open index FILES of an area for CDX()/MDX(): the structural <c>.cdx</c> (index 1, if any),
    /// then the additional <c>.cdx</c>/<c>.idx</c> in open order.</summary>
    private List<string> FileInventory(int area)
    {
        var files = new List<string>();
        var wa = Session.AreaAt(area);
        if (wa?.Cdx?.SourcePath is { } sp) files.Add(Path.GetFullPath(sp));
        if (_meta.TryGetValue(area, out var m) && m.ExtraIndexes is { } ex)
            files.AddRange(ex.Select(Path.GetFullPath));
        return files;
    }

    /// <summary>Open standalone <c>.idx</c> files of an area (in open order) — the NDX() domain.</summary>
    private List<string> IdxFileInventory(int area)
        => _meta.TryGetValue(area, out var m) && m.ExtraIndexes is { } ex
            ? ex.Where(IsIdxPath).Select(Path.GetFullPath).ToList()
            : new List<string>();

    /// <summary>ATAGINFO(ArrayName [, cTagFile [, area]]) — fill a (n×6) array with one row per open tag
    /// ([1] name, [2] type, [3] key, [4] filter, [5] direction, [6] collation) and return the tag count.</summary>
    private int FnATagInfo(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        string arrName = a[0].AsString;
        int ai = 1, area = Session.CurrentArea;
        string? file = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);

        var inv = FilterInventory(IndexInventory(area), file);
        if (inv.Count == 0) return 0;
        var arr = Memory.RedimOrCreateArray(arrName, inv.Count, 6);
        for (int i = 0; i < inv.Count; i++)
        {
            var s = inv[i];
            arr.Set(i + 1, 1, VfpValue.Character(s.Name));
            arr.Set(i + 1, 2, VfpValue.Character(s.Unique ? "UNIQUE" : "REGULAR"));
            arr.Set(i + 1, 3, VfpValue.Character(s.KeyExpr));
            arr.Set(i + 1, 4, VfpValue.Character(s.ForExpr));
            arr.Set(i + 1, 5, VfpValue.Character(s.Descending ? "DESCENDING" : "ASCENDING"));
            arr.Set(i + 1, 6, VfpValue.Character(s.Collation));
        }
        return inv.Count;
    }

}
