using System.Buffers.Binary;
using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D7 — BULK build / rebuild (REINDEX) of a compact compound <c>.cdx</c>: the
/// inverse of the Teil C reader. Computes every key via the expression engine,
/// sorts the (key, recno) pairs by unsigned key bytes (recno tiebreak), bulk-loads
/// a balanced B-tree into 512-byte pages (compact leaves + branch nodes), wires the
/// tag headers + tag directory, and writes the file.
/// </summary>
/// <remarks>
/// The on-disk format is the exact inverse of <see cref="CompactLeaf"/>.Decode /
/// <see cref="CdxHeader.Parse(ReadOnlySpan{byte}, Encoding)"/> / <see cref="IndexFile.ReadBranchEntries"/>, and the
/// leaf bit-width algorithm mirrors CodeBase <c>r4reinde.c</c> (validated byte-for-byte
/// against VFP9 fixtures such as <c>vfp_test/ligtest.CDX</c>). Page layout (per VFP9):
/// page0 = file header, page1 = its expression pool, then the tag directory, and for each
/// tag a header page + pool page + its B-tree pages in VFP's allocation order.
/// </remarks>
internal static class CdxIndexBuilder
{
    private const int Page = IndexFile.PageSize;       // 512
    private const int StdHeader = 12;                  // node std header (attr/keys/left/right)
    private const int NodeInfo = 12;                   // compact-leaf info block (@12..23)
    private const int LeafDataStart = StdHeader + NodeInfo; // 24
    private const int LeafBudget = Page - LeafDataStart;     // 488 bytes of keys + entry info per leaf
    private const long EmptyTagMaxRecno = 0xFFF;             // geometry of an empty root leaf (INDEX ON over no rows)
    private const uint NoPtr = 0xFFFFFFFF;

    // Node attribute bits (CodeBase d4data.h: 0=index/branch, 1=root, 2=leaf, 4=data-tag leaf).
    private const ushort AttrRoot = 0x01;
    private const ushort AttrLeaf = 0x02;
    private const ushort AttrData = 0x04;

    /// <summary>A single source record to index: its 1-based record number and decoded row.</summary>
    public readonly record struct BuildRow(int RecNo, DbfRecord Record);

    /// <summary>
    /// UNIQUE-tag state captured before a bulk append rebuild overwrites the current CDX. Existing entries are
    /// the fixed pre-append winners; only rows beyond <see cref="ExistingRecordCount"/> may add new keys.
    /// </summary>
    internal readonly record struct AppendUniqueState(
        int ExistingRecordCount,
        IReadOnlyDictionary<string, IReadOnlyList<IndexEntry>> ExistingEntriesByTag);

    /// <summary>
    /// Build a fresh compound <c>.cdx</c> at <paramref name="cdxPath"/> covering every
    /// definition in <paramref name="tags"/> over <paramref name="rows"/> (the live,
    /// non-deleted-aware record set of <paramref name="schema"/>). Overwrites any existing
    /// file at the path.
    /// <para>
    /// <paramref name="evalContext"/> supplies the AMBIENT expression settings (SET EXACT / SET ANSI /
    /// culture / code page) the KEY and FOR expressions are compiled+evaluated with; the per-tag
    /// <see cref="CdxTagDefinition.Collation"/> always overrides the context's collation. Passing
    /// <see langword="null"/> keeps the VFP-neutral defaults (EXACT OFF) so an existing byte-exact golden
    /// stays identical. <paramref name="includeDeleted"/> mirrors SET DELETED OFF — when <see langword="true"/>
    /// deleted rows are indexed too (VFP keeps their entries in the CDX until PACK); the default excludes them.
    /// </para>
    /// <para>
    /// The leaves carry the 0x04 attribute bit as VFP9's INDEX ON sets it (see <see cref="PackLeaves"/>), unless
    /// <paramref name="containerIndex"/>: a database container's .dcx, which VFP grows record by record, sets it by the
    /// entry size (vfp_test/dbctest/ref.DCX).
    /// </para>
    /// </summary>
    public static void Build(string cdxPath, DbfTable schema, IReadOnlyList<BuildRow> rows, IReadOnlyList<CdxTagDefinition> tags,
        EvaluationContext? evalContext = null, bool includeDeleted = false, AppendUniqueState? appendUniqueState = null,
        bool containerIndex = false, IReadOnlyDictionary<string, byte[]>? preservedKeyStatistics = null)
    {
        var sink = new PageSink();
        sink.Reserve();           // page 0: file header (filled last)
        sink.Reserve();           // page 1: file-header expression pool (2 NUL bytes; already zero)

        // Plan every per-tag tree up front (keys, geometry, header metadata, page footprint).
        var plans = new List<TagPlan>(tags.Count);
        foreach (var def in tags)
            plans.Add(PlanTag(schema, rows, def, evalContext, includeDeleted, appendUniqueState));

        // VFP places the tag-DIRECTORY root EARLY — on page 2 (offset 1024), BEFORE the per-tag
        // trees — so the real VFP9 runtime can open a .dbc that uses this .dcx (error 1552 otherwise).
        // The directory leaf stores each tag NAME (10-byte key) → that tag's HEADER byte offset as its
        // "recno". Those offsets depend on where the tags land, which depends on how many pages the
        // directory itself needs — a small fixed point (1 iteration in every realistic case: the
        // directory fits in a single page for up to ~37 tags). We solve it before reserving anything.
        List<Entry> dirEntries = new(tags.Count);
        int dirPages = 1;
        while (true)
        {
            dirEntries = new List<Entry>(tags.Count);
            int page = 2 + dirPages;                       // first page after [hdr][pool][directory…]
            foreach (var p in plans)
            {
                long headerOffset = (long)page * Page;
                dirEntries.Add(new Entry(p.NameKey, (uint)headerOffset));
                // header + pool + B-tree pages; the tree's branch grouping depends on where its first page lands
                page += 2 + CountTreePages(p.LeafCount, p.KeyLen, firstPage: page + 2);
            }
            dirEntries.Sort(EntryComparer.Instance);
            int need = CountTreePages(PackLeaves(dirEntries, keyLen: 10, pad: 0x20).Count, keyLen: 10, firstPage: 2);
            if (need == dirPages) break;
            dirPages = need;
        }

        // Reserve the directory page(s) at page 2 BEFORE the tags, then lay every tag down after them.
        int dirStart = sink.Reserve();                     // page 2
        for (int i = 1; i < dirPages; i++) sink.Reserve();

        foreach (var p in plans)
        {
            int headerIndex = sink.Reserve();              // tag header page
            sink.Reserve();                                // its expression-pool page
            long headerOffset = (long)headerIndex * Page;
            uint treeRoot = containerIndex
                ? BuildTree(sink, () => sink.Reserve(), p.Entries, p.KeyLen, p.Pad, indexOn: false)
                : BuildIndexOnTree(sink, p);

            WriteHeaderCommon(sink.At(headerIndex), root: treeRoot, keyLen: p.KeyLen, options: p.Options,
                signature: p.Signature, sortOrder: p.SortOrder, descending: p.Descending,
                keyExpr: p.KeyExpr, forExpr: p.ForExpr);
            if (preservedKeyStatistics is not null)
                preservedKeyStatistics[p.Name].CopyTo(sink.At(headerIndex), 16);
            else if (!containerIndex)
                WriteKeyStatistics(sink.At(headerIndex), p.Entries, p.Pad);
            WriteExpressionPool(sink, headerOffset, p.KeyExpr, p.ForExpr);
        }

        // Fill the reserved directory page(s) with the directory tree (leaves first, then branches —
        // the same allocation order CountTreePages counted, so the pre-reserved range matches exactly).
        var dirRange = Enumerable.Range(dirStart, dirPages).GetEnumerator();
        uint dirRoot = BuildTree(sink, () => { dirRange.MoveNext(); return dirRange.Current; },
            dirEntries, keyLen: 10, pad: 0x20, indexOn: false);

        // File header (page 0) + its pool (page 1: keyExprLen=1, forExprLen=1 → 2 NUL bytes).
        var fileHeader = sink.At(0);
        WriteHeaderCommon(fileHeader, root: dirRoot, keyLen: 10,
            options: (byte)0xE0,           // structural | compound | compact
            signature: 1, sortOrder: null, descending: false,
            keyExpr: Array.Empty<byte>(), forExpr: Array.Empty<byte>());

        File.WriteAllBytes(cdxPath, sink.ToArray());
    }

    // ---- single-tag append (project-review 5.6: page-level CREATE-TAG fast path) ------------------

    /// <summary>One tag's on-disk pages (header + expression pool + compact B-tree) as a CONTIGUOUS block ready
    /// to APPEND at <see cref="HeaderOffset"/> (the block's first byte), plus that header byte offset — the unit
    /// the incremental CREATE-TAG fast path splices onto a live compound <c>.cdx</c> without rewriting siblings.</summary>
    internal readonly record struct TagBlock(byte[] Pages, long HeaderOffset);

    /// <summary>
    /// Build ONLY the pages for the single tag <paramref name="def"/> over <paramref name="rows"/>, laid out to
    /// sit at file offset <paramref name="baseByteOffset"/> (which MUST be page-aligned — the end of a valid
    /// <c>.cdx</c> always is): a header page, its expression-pool page, then the tag's compact B-tree. Returns
    /// the contiguous block plus the tag's header byte offset. Reuses the EXACT same per-tag plan, tree
    /// bulk-loader and header/pool writers as the whole-file <see cref="Build"/>, so an appended tag reads back
    /// byte-identical to one a fresh build would place at the same page position — the win is that the SIBLING
    /// tags are never touched. The caller appends the block, then inserts the tag's directory entry
    /// (name → header offset) via <see cref="CdxTreeEditor"/>.
    /// </summary>
    internal static TagBlock BuildTagBlock(DbfTable schema, IReadOnlyList<BuildRow> rows, CdxTagDefinition def,
        long baseByteOffset, EvaluationContext? evalContext, bool includeDeleted)
    {
        if (baseByteOffset < 0 || baseByteOffset % Page != 0)
            throw new ArgumentException($"CDX append base offset {baseByteOffset} is not page-aligned.", nameof(baseByteOffset));

        var plan = PlanTag(schema, rows, def, evalContext, includeDeleted, appendUniqueState: null);
        var sink = new PageSink((int)(baseByteOffset / Page));

        int headerIndex = sink.Reserve();   // tag header page (absolute page index == basePage)
        sink.Reserve();                      // its expression-pool page
        long headerOffset = (long)headerIndex * Page;

        uint treeRoot = BuildIndexOnTree(sink, plan);

        WriteHeaderCommon(sink.At(headerIndex), root: treeRoot, keyLen: plan.KeyLen, options: plan.Options,
            signature: plan.Signature, sortOrder: plan.SortOrder, descending: plan.Descending,
            keyExpr: plan.KeyExpr, forExpr: plan.ForExpr);
        WriteKeyStatistics(sink.At(headerIndex), plan.Entries, plan.Pad);
        WriteExpressionPool(sink, headerOffset, plan.KeyExpr, plan.ForExpr);

        return new TagBlock(sink.ToArray(), headerOffset);
    }

    // INDEX ON uses the same allocation order and branch data bit for CDX tags and compact IDX trees.
    // Keep the container (.dcx) path separate: VFP builds those indexes incrementally.
    private static uint BuildIndexOnTree(PageSink sink, TagPlan plan)
    {
        int rootPage = sink.Reserve();
        var (leaves, branches, nextPage) = TreeLayout(plan.LeafCount, plan.KeyLen, rootPage);
        for (int page = rootPage + 1; page < nextPage; page++) sink.Reserve();
        var pages = leaves.Concat(branches.SelectMany(level => level.Select(node => node.Page))).GetEnumerator();
        var groups = branches.Select(level => level.Select(node => node.Count).ToList()).ToList();
        return BuildTree(sink, () => { pages.MoveNext(); return pages.Current; },
            plan.Entries, plan.KeyLen, plan.Pad, indexOn: true, groups: groups, branchesCarryLeafData: true);
    }

    // ---- compact standalone .idx (INDEX ON … TO x.idx COMPACT) ------------------------------------

    /// <summary>
    /// Build a COMPACT standalone <c>.idx</c> at <paramref name="idxPath"/> — what VFP9's
    /// <c>INDEX ON … TO x.idx COMPACT</c> writes (and plain <c>TO x.idx</c> under a collation other than MACHINE).
    /// The file is one compact tree, the same format as a CDX tag: page 0 holds a tag header (options 0x20 |
    /// UNIQUE 0x01 | FOR 0x08, the collation at @494), page 1 its KEY/FOR expression pool, and the tree starts on
    /// page 2. <paramref name="def"/>'s name is not stored; its collation decides the key bytes.
    /// <para>
    /// Like CDX tags built by INDEX ON, the nodes sit where VFP9 grows them key by key
    /// (<see cref="TreeLayout"/>: root on page 2, leaves and branch nodes interleaved), and every branch node
    /// carries the first leaf's 0x04 bit. The header holds VFP's five key statistics at @16
    /// (<see cref="WriteKeyStatistics"/>). Overwrites any existing file at the path.
    /// </para>
    /// </summary>
    internal static void BuildCompactIdx(string idxPath, DbfTable schema, IReadOnlyList<BuildRow> rows, CdxTagDefinition def,
        EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        if (def.Descending)
            throw new DbfWriteException("Cannot sort .IDX files in descending order.");

        var p = PlanTag(schema, rows, def, evalContext, includeDeleted, appendUniqueState: null, standalone: true);
        var (leafPages, branches, nextPage) = TreeLayout(p.LeafCount, p.KeyLen, rootPage: 2);

        var sink = new PageSink();
        for (int i = 0; i < nextPage; i++) sink.Reserve();

        // BuildTree takes the leaves' pages first, then each branch level's in key order.
        var pages = leafPages.Concat(branches.SelectMany(level => level.Select(node => node.Page))).GetEnumerator();
        var groups = branches.Select(level => level.Select(node => node.Count).ToList()).ToList();
        uint root = BuildTree(sink, () => { pages.MoveNext(); return pages.Current; },
            p.Entries, p.KeyLen, p.Pad, indexOn: true, groups: groups, branchesCarryLeafData: true);

        var header = sink.At(0);
        WriteHeaderCommon(header, root: root, keyLen: p.KeyLen, options: p.Options, signature: p.Signature,
            sortOrder: p.SortOrder, descending: false, keyExpr: p.KeyExpr, forExpr: p.ForExpr);
        WriteKeyStatistics(header, p.Entries, p.Pad);
        WriteExpressionPool(sink, headerOffset: 0, p.KeyExpr, p.ForExpr);

        File.WriteAllBytes(idxPath, sink.ToArray());
    }

    /// <summary>
    /// The five key statistics VFP9 keeps at @16..35 of a tag header (32-bit LE each), over the entries in key
    /// order: the entry count; the number of entries whose key differs from the one before (the first is compared
    /// with an all-<paramref name="pad"/> key, so a blank first key is not counted); the sum of the record-number
    /// distances |r(i) − r(i−1)|; the sum of sign(|d(i)| − |d(i−1)|) over those distances (signed); and the number
    /// of steps down, r(i) &lt; r(i−1). Derived from VFP9 compact <c>.idx</c> files: 300- to 12000-row tables with
    /// duplicates, blank keys, FOR and UNIQUE, and every arrangement of short key sequences.
    /// </summary>
    private static void WriteKeyStatistics(byte[] header, List<Entry> entries, byte pad)
    {
        uint distinct = 0, distanceSum = 0, descents = 0;
        int trend = 0;
        byte[]? previous = null;
        long previousDistance = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            var key = entries[i].Key;
            bool same = previous is null
                ? key.AsSpan().IndexOfAnyExcept(pad) < 0
                : key.AsSpan().SequenceEqual(previous);
            if (!same) distinct++;
            previous = key;

            if (i == 0) continue;
            if (entries[i].Recno < entries[i - 1].Recno) descents++;
            long distance = Math.Abs((long)entries[i].Recno - entries[i - 1].Recno);
            distanceSum += (uint)distance;
            if (previousDistance >= 0)
                trend += Math.Sign(distance - previousDistance);
            previousDistance = distance;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), distinct);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), distanceSum);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), trend);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), descents);
    }

    /// <summary>The 10-byte, space-padded tag-directory key for <paramref name="name"/> — the exact bytes the
    /// compound-index directory tree sorts and stores. Shared with the incremental tag-DDL fast paths so an
    /// unlink/insert targets the same key bytes the bulk builder wrote.</summary>
    internal static byte[] DirectoryKey(string name, Encoding encoding) => MakeNameKey(name, encoding);

    // ---- per-tag build ---------------------------------------------------------

    /// <summary>The fully resolved plan for one tag: its sorted entries, leaf geometry, page
    /// footprint, and every value needed to emit its header — computed BEFORE any page is placed.</summary>
    private sealed class TagPlan
    {
        public required string Name;
        public required byte[] NameKey;
        public required List<Entry> Entries;
        public int KeyLen;
        public byte Pad;
        public int LeafCount;
        public byte Options;
        public byte Signature;
        public string? SortOrder;
        public bool Descending;
        public byte[] KeyExpr = Array.Empty<byte>();
        public byte[] ForExpr = Array.Empty<byte>();
    }

    // standalone: plan the single tree of a COMPACT standalone .idx — no tag name, and the options byte lacks the
    // compound bit (0x20 instead of 0x60).
    private static TagPlan PlanTag(DbfTable schema, IReadOnlyList<BuildRow> rows, CdxTagDefinition def,
        EvaluationContext? evalContext, bool includeDeleted, AppendUniqueState? appendUniqueState,
        bool standalone = false)
    {
        string keyExpr = def.KeyExpression ?? string.Empty;
        bool hasFor = !string.IsNullOrWhiteSpace(def.ForExpression);
        string forExpr = hasFor ? def.ForExpression! : string.Empty;

        // The KEY/FOR text is stored as VFP's INDEX ON stores it: DECOMPILED (`UPPER( name )` → `UPPER(name)`,
        // `RECN()` → `RECNO()`), unless the tag keeps the text it already had. The keys are computed from the
        // STORED text — what incremental maintenance (and VFP after a reopen) evaluates — so a bulk build and
        // a later per-row edit can never disagree (an abbreviated `!DELE()` now works in both).
        if (!def.StoreExpressionsVerbatim)
        {
            keyExpr = VfpExpressionText.Canonicalize(keyExpr);
            if (hasFor) forExpr = VfpExpressionText.Canonicalize(forExpr);
            def = def with { KeyExpression = keyExpr, ForExpression = hasFor ? forExpr : def.ForExpression };
        }

        // Tag name and KEY/FOR text are stored in the TABLE's code page, as VFP9 does ('ó' of razónsoc is
        // 0xF3 in a CP1252 table, 0xA2 in a CP850 one) — never as '?'.
        var textEncoding = schema.Encoding;
        string what = standalone ? "compact IDX" : "CDX tag";
        byte[] nameKey = standalone ? Array.Empty<byte>() : MakeNameKey(def.Name, textEncoding);
        byte[] keyText = CodePageText.Encode(keyExpr, textEncoding, $"{what} KEY expression", m => new DbfWriteException(m));
        byte[] forText = CodePageText.Encode(forExpr, textEncoding, $"{what} FOR expression", m => new DbfWriteException(m));
        if (keyText.Length + forText.Length + 2 > Page)
            throw new DbfWriteException($"{what} KEY + FOR expressions exceed the 512-byte expression pool.");

        // The per-row KEY/FOR derivation (collation, key type/length, FOR filter, encoding) is factored
        // into a reusable TagKeyComputer — the SAME primitive the incremental write-path maintenance
        // (DbfWriter index maintenance) drives, so a maintained entry is byte-identical to a bulk-built one.
        var computer = CreateKeyComputer(schema, def, evalContext);

        int recCount = rows.Count;
        List<Entry> entries;
        if (def.Unique &&
            appendUniqueState is { } uniqueState &&
            uniqueState.ExistingEntriesByTag.TryGetValue(def.Name, out var existingUniqueEntries))
        {
            entries = PlanAppendUniqueTag(rows, existingUniqueEntries, uniqueState.ExistingRecordCount,
                computer, recCount, includeDeleted);
        }
        else
        {
            entries = new List<Entry>(rows.Count);
            foreach (var row in rows)
            {
                // VFP keeps index entries for DELETED records in the CDX (only PACK removes them). With
                // SET DELETED OFF (includeDeleted) we index them too; the default (SET DELETED ON) skips them.
                if (!includeDeleted && row.Record.IsDeleted)
                    continue;
                if (computer.TryComputeKey(row.Record, row.RecNo, recCount, out var keyBytes))
                    entries.Add(new Entry(keyBytes, (uint)row.RecNo));
            }

            entries.Sort(EntryComparer.Instance);

            if (def.Unique)
                entries = DropDuplicateKeys(entries);
        }

        byte options = standalone ? (byte)0x20 : (byte)0x60; // [compound |] compact
        if (def.Unique) options |= 0x01;
        if (hasFor) options |= 0x08;

        // Tag signature (byte 15): 0x01 for an IDENTITY/MACHINE collation, 0x02 only for a real
        // named (GENERAL, …) weight collation. Verified against vfp_test/dbctest/ref.DCX (both DBC
        // tags MACHINE ⇒ 0x01) and vfp_test/ligtest.CDX (GENERAL ⇒ 0x02).
        byte tagSig = IsIdentityCollation(computer.Collation) ? (byte)1 : (byte)2;

        return new TagPlan
        {
            Name = def.Name,
            NameKey = nameKey,
            Entries = entries,
            KeyLen = computer.KeyLen,
            Pad = computer.Pad,
            LeafCount = PackLeaves(entries, computer.KeyLen, computer.Pad).Count,
            Options = options,
            Signature = tagSig,
            SortOrder = computer.Collation.Name,
            Descending = def.Descending,
            KeyExpr = keyText,
            ForExpr = forText,
        };
    }

    private static List<Entry> PlanAppendUniqueTag(IReadOnlyList<BuildRow> rows,
        IReadOnlyList<IndexEntry> existingUniqueEntries, int existingRecordCount,
        TagKeyComputer computer, int recCount, bool includeDeleted)
    {
        int newRowCapacity = Math.Max(0, rows.Count - Math.Max(0, existingRecordCount));
        var entries = new List<Entry>(existingUniqueEntries.Count + newRowCapacity);
        var seen = new HashSet<byte[]>(KeyBytesEqualityComparer.Instance);
        uint oldMax = existingRecordCount <= 0 ? 0u : (uint)existingRecordCount;

        foreach (var existing in existingUniqueEntries)
        {
            if (existing.RecordNumber == 0 || existing.RecordNumber > oldMax)
                continue;
            byte[] key = (byte[])existing.Key.Clone();
            if (!seen.Add(key))
                continue;
            entries.Add(new Entry(key, existing.RecordNumber));
        }

        foreach (var row in rows)
        {
            if (row.RecNo <= existingRecordCount)
                continue;
            if (!includeDeleted && row.Record.IsDeleted)
                continue;
            if (!computer.TryComputeKey(row.Record, row.RecNo, recCount, out var keyBytes))
                continue;
            if (!seen.Add(keyBytes))
                continue;
            entries.Add(new Entry(keyBytes, (uint)row.RecNo));
        }

        entries.Sort(EntryComparer.Instance);
        return entries;
    }

    // ---- reusable per-tag key derivation (shared by bulk build + incremental maintenance) ----------

    /// <summary>
    /// The compiled, per-tag KEY/FOR derivation for one <see cref="CdxTagDefinition"/> over a
    /// <see cref="DbfTable"/> schema: the resolved key type / stored key length / trailing pad /
    /// collation, plus the compiled KEY and (optional) FOR functions. A single instance turns a
    /// <see cref="DbfRecord"/> into the exact on-disk key bytes the bulk builder would produce, so the
    /// incremental write-path maintenance and the fresh REINDEX stay in lock-step.
    /// </summary>
    internal sealed class TagKeyComputer
    {
        public required IndexKeyType KeyType;
        public required int KeyLen;
        public required bool IsChar;
        public required byte Pad;
        public required IVfpCollation Collation;
        /// <summary>The code page a MACHINE character key is stored in (<see cref="IndexKey.ResolveCharacterEncoding"/>).</summary>
        public required Encoding CharacterEncoding;
        public required Func<IRowContext, VfpValue> KeyFn;
        public DbfColumn? DateField;
        public required Func<IRowContext, VfpValue>? ForFn;
        public required bool Unique;
        public required bool Descending;
        public Action? ValidateHost;

        /// <summary>
        /// Evaluate the tag's FOR filter and KEY over <paramref name="record"/>. Returns <see langword="true"/>
        /// and the encoded key bytes when the record belongs in the tag (FOR passes / no FOR); returns
        /// <see langword="false"/> (and an empty key) when a FOR filter excludes it. Deleted-record filtering
        /// is the CALLER's concern (VFP keeps deleted entries in the CDX until PACK; the write path indexes
        /// regardless of the deleted flag so an UPDATE keeps a deleted row's entry consistent).
        /// </summary>
        public bool TryComputeKey(DbfRecord record, int recNo, int recCount, out byte[] key)
        {
            var rc = new RowContext(record, recNo, recCount);
            if (ForFn is not null)
            {
                var f = ForFn(rc);
                if (!(f.Type == VfpType.Logical && f.AsLogical))
                {
                    key = Array.Empty<byte>();
                    return false;
                }
            }
            key = DateField is not null
                ? IndexKey.EncodeDateField(record, DateField)
                : EncodeKey(KeyFn(rc), KeyType, KeyLen, Collation, CharacterEncoding, Pad);
            return true;
        }
    }

    /// <summary>
    /// Build the <see cref="TagKeyComputer"/> for <paramref name="def"/> against <paramref name="schema"/>.
    /// The per-tag collation ALWAYS wins (it is baked into the tag header), but the KEY/FOR expressions
    /// otherwise honour the caller's LIVE SET EXACT / SET ANSI (via <paramref name="evalContext"/>) so a
    /// character <c>=</c> inside a FOR/KEY filters exactly as a VFP run of the same program would.
    /// </summary>
    internal static TagKeyComputer CreateKeyComputer(DbfTable schema, CdxTagDefinition def, EvaluationContext? evalContext)
    {
        // The stored keys are weighted in the TABLE code page (VFP9: CP850 GENERAL expands Ä to AE, CP1252 does
        // not). Approximate weights are not VFP keys either: refuse before writing any index bytes.
        if (VfpCollations.Resolve(def.Collation, schema.Encoding.CodePage, out var collation) != CollationSupport.Exact)
            throw new DbfWriteException(
                $"Cannot derive index tag '{def.Name}' exactly (collation '{def.Collation}', code page {schema.Encoding.CodePage}).");
        var ctx = new EvaluationContext
        {
            // KEY/FOR comparisons run on the decoded text (VFP: in the current code page), so they use the
            // code-page-neutral collation of that name; only the stored key bytes follow the table code page.
            Collation = collation.Name switch
            {
                // These sequences are unavailable in CP1252. FOR expressions need their
                // language session, rather than the default factory's MACHINE fallback.
                "TURKISH" => VfpCollations.ByName(def.Collation, 1254),
                "RUSSIAN" => VfpCollations.ByName(def.Collation, 1251),
                "GREEK" => VfpCollations.ByName(def.Collation, 1253),
                _ => VfpCollations.ByName(def.Collation),
            },
            Exact = evalContext?.Exact ?? false,
            Ansi = evalContext?.Ansi ?? false,
            Culture = evalContext?.Culture,
            // CHR()/ASC() inside KEY/FOR map bytes in the TABLE code page — the same encoding the key is
            // stored in (VFP9: INDEX ON CHR(130) on a CP850 table stores 0x82), and identical for the bulk
            // build and the incremental maintenance (which has no live evaluation context).
            Encoding = schema.Encoding,
            IndexEvaluator = evalContext?.IndexEvaluator,
            Random = evalContext?.Random ?? new VfpRandom(),
        };

        var (keyType, baseLen, isChar) = ResolveKey(schema, def.KeyExpression, ctx);
        int keyLen = CollatedKeyLength(baseLen, isChar, collation);
        // VFP9 pads a MACHINE character key with blanks and a weighted (GENERAL / SPANISH) one with 0x00.
        byte pad = isChar ? IndexKey.CharacterKeyPad(def.Collation) : (byte)0x00;

        var keyFn = CompileIndex(def.KeyExpression, ctx);
        Func<IRowContext, VfpValue>? forFn = !string.IsNullOrWhiteSpace(def.ForExpression)
            ? CompileIndex(def.ForExpression!, ctx)
            : null;

        Action? validateHost = null;
        if (evalContext?.ValidateIndexFunction is { } validate)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in new[] { def.KeyExpression, def.ForExpression })
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                VfpExpression.Parse(text).ContainsFunction((name, _) =>
                {
                    name = VfpFunctionAbbreviations.Canonical(name);
                    if (!VfpRuntime.EngineFunctionNames.Contains(name)) names.Add(name);
                    return false;
                });
            }
            if (names.Count > 0) validateHost = () => { foreach (string name in names) validate(name); };
        }
        return new TagKeyComputer
        {
            ValidateHost = validateHost,
            KeyType = keyType,
            KeyLen = keyLen,
            IsChar = isChar,
            Pad = pad,
            Collation = collation,
            CharacterEncoding = IndexKey.ResolveCharacterEncoding(schema, def.KeyExpression),
            KeyFn = keyFn,
            DateField = IndexKey.ResolveDateField(schema, def.KeyExpression),
            ForFn = forFn,
            Unique = def.Unique,
            Descending = def.Descending,
        };
    }

    // ---- B-tree bulk loader ----------------------------------------------------

    /// <summary>
    /// Count the pages a <see cref="BuildTree"/> over <paramref name="leafCount"/> leaves would allocate when its
    /// first page is <paramref name="firstPage"/>, WITHOUT writing anything — used to size the tag directory's
    /// pre-reserved page range and to place the tags up front. The branch grouping (and so the node count) depends
    /// on the page position, see <see cref="BranchLayout"/>.
    /// </summary>
    private static int CountTreePages(int leafCount, int keyLen, int firstPage)
    {
        int total = leafCount;
        foreach (var level in BranchLayout(leafCount, keyLen, firstPage))
            total += level.Count;
        return total;
    }

    /// <summary>
    /// The child count of every branch node, level by level from the leaf parents up to the root, that VFP9's
    /// INDEX ON gives a tree of <paramref name="leafCount"/> leaves whose first page is <paramref name="rootPage"/>.
    /// <para>
    /// VFP9 grows the tree as if the sorted keys were inserted one by one: the root stays on the tag's first page,
    /// a root split takes the next two pages (the right half on the lower one), every other new node takes the
    /// next page, and pages are handed out in the order leaves and nodes appear. When a full branch node gets one
    /// more child, a new node opens to its right with just that child — except when the full node sits on an ODD
    /// page and an entry (key + recno + child pointer) is shorter than 50 bytes: then the full node also gives up
    /// its last child (it keeps capacity-1, the new node starts with 2). Derived from VFP9 over 3357 trees
    /// (key lengths 1-240, up to 4 branch levels, extra tags in front shifting the tree by 3 to 6 pages):
    /// fan-outs and page numbers all reproduce exactly (e.g. key length 20 on page 5: 16/17/17…; key length 40 on
    /// page 5: 9/9/10/10/9/10/10/9…, on page 8: 10/10/10/9/10/10/9…; key length 42: 10/10/10…).
    /// </para>
    /// <para>
    /// This helper takes only the grouping for the directory/container path. INDEX ON also uses the page
    /// positions from <see cref="TreeLayout"/>. Leaves are packed greedily, as VFP does.
    /// </para>
    /// </summary>
    private static List<List<int>> BranchLayout(int leafCount, int keyLen, int rootPage)
        => TreeLayout(leafCount, keyLen, rootPage).Branches
            .Select(level => level.Select(node => node.Count).ToList())
            .ToList();

    /// <summary>A node of a laid-out tree: its page number and (for a branch) its child count.</summary>
    internal readonly record struct TreeNode(int Page, int Count);

    /// <summary>
    /// The tree <see cref="BranchLayout"/> describes, with the PAGE of every node: the leaves in key order, the
    /// branch levels from the leaf parents up to the root, and the first page after the tree. Pages are handed out
    /// in the order VFP9 creates the nodes: the root keeps <paramref name="rootPage"/>, the first split puts the
    /// right leaf on the next page and the left one on the page after, every later leaf takes the next page, and
    /// the branch nodes its arrival splits off follow it, bottom-up (a root split: the right half first).
    /// </summary>
    internal static (List<int> Leaves, List<List<TreeNode>> Branches, int NextPage) TreeLayout(
        int leafCount, int keyLen, int rootPage)
    {
        var branches = new List<List<TreeNode>>();   // per level: the nodes in key order
        if (leafCount <= 1)
            return (new List<int> { rootPage }, branches, rootPage + 1);   // a single leaf is the root

        int entrySize = keyLen + 8;
        int cap = Math.Max(2, (Page - StdHeader) / entrySize);
        bool givesUpLast = entrySize < 50;
        int next = rootPage + 1;

        // The root leaf splits: two new leaves (left on rootPage+2, right on rootPage+1), the root becomes a branch.
        int rightLeaf = next++;
        int leftLeaf = next++;
        var leaves = new List<int> { leftLeaf, rightLeaf };
        branches.Add(new List<TreeNode> { new(rootPage, 2) });

        for (int leaf = 2; leaf < leafCount; leaf++)
        {
            leaves.Add(next++);                       // the new leaf's page
            for (int h = 0; ; h++)
            {
                var level = branches[h];
                var node = level[^1];
                if (node.Count < cap)
                {
                    level[^1] = node with { Count = node.Count + 1 };
                    break;
                }

                int keep = givesUpLast && (node.Page & 1) == 1 ? cap - 1 : cap;
                if (node.Page == rootPage)
                {
                    // Root split: the root's children go to two new nodes, the root (same page) gets a level above.
                    int right = next++;
                    int left = next++;
                    level[^1] = new TreeNode(left, keep);
                    level.Add(new TreeNode(right, cap + 1 - keep));
                    branches.Add(new List<TreeNode> { new(rootPage, 2) });
                    break;
                }

                level[^1] = node with { Count = keep };
                level.Add(new TreeNode(next++, cap + 1 - keep));
            }
        }
        return (leaves, branches, next);
    }

    /// <summary>Build the compact B-tree for <paramref name="entries"/> and return the root page offset.
    /// Pages are taken from <paramref name="alloc"/> (either fresh reservations or a pre-reserved range), leaves
    /// first, then each branch level in key order. <paramref name="indexOn"/>: the leaves carry the 0x04 bit as
    /// INDEX ON sets it (see <see cref="PackLeaves"/>). <paramref name="groups"/> overrides the branch grouping
    /// (default: <see cref="BranchLayout"/> from the first leaf's page); <paramref name="branchesCarryLeafData"/>
    /// gives every branch node the 0x04 bit of the first leaf, as VFP9 does on the trees it grows key by key.</summary>
    private static uint BuildTree(PageSink sink, Func<int> alloc, List<Entry> entries, int keyLen, byte pad, bool indexOn,
        List<List<int>>? groups = null, bool branchesCarryLeafData = false)
    {
        var leaves = PackLeaves(entries, keyLen, pad, indexOn);

        // Assign page offsets to every leaf first (so siblings can reference each other).
        foreach (var leaf in leaves)
            leaf.Index = alloc();

        // The 0x04 bit: see PackLeaves. Without indexOn it is set exactly when an entry's info block packs into one
        // or two bytes (BytesPerEntry <= 2), which every freshly VFP9-built .dcx golden shows: vfp_test/dbctest/ref.DCX
        // (its wide composite tag BytesPerEntry=3 => 0x03, the str(parentid)+objecttype tag BytesPerEntry=2 => 0x07).
        // The reader keys only off bits 0x01/0x02, never 0x04.
        // Single leaf → it is also the root (root | leaf).
        if (leaves.Count == 1)
        {
            var only = leaves[0];
            WriteLeaf(sink.At(only.Index), only, keyLen, (ushort)(AttrRoot | AttrLeaf | only.AttrExtra),
                left: NoPtr, right: NoPtr);
            return (uint)(only.Index * Page);
        }

        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            uint left = i > 0 ? (uint)(leaves[i - 1].Index * Page) : NoPtr;
            uint right = i < leaves.Count - 1 ? (uint)(leaves[i + 1].Index * Page) : NoPtr;
            WriteLeaf(sink.At(leaf.Index), leaf, keyLen, (ushort)(AttrLeaf | leaf.AttrExtra), left, right);
        }

        // Build branch levels until a single root node remains.
        var level = leaves
            .Select(l => new ChildRef((uint)(l.Index * Page), l.LastKey, l.LastRecno))
            .ToList();

        // Group them into branch levels the way VFP9 does; the layout depends on where the tree starts, which
        // is the first page handed out (the first leaf's).
        ushort branchExtra = branchesCarryLeafData ? leaves[0].AttrExtra : (ushort)0;
        foreach (var levelGroups in groups ?? BranchLayout(leaves.Count, keyLen, rootPage: leaves[0].Index))
            level = BuildBranchLevel(sink, alloc, level, keyLen, levelGroups, branchExtra);

        return level[0].Offset;
    }

    private static List<ChildRef> BuildBranchLevel(PageSink sink, Func<int> alloc, List<ChildRef> children, int keyLen,
        List<int> groups, ushort attrExtra = 0)
    {
        var nodes = new List<(int index, List<ChildRef> kids)>(groups.Count);
        int start = 0;
        foreach (int count in groups)
        {
            nodes.Add((alloc(), children.GetRange(start, count)));
            start += count;
        }
        if (start != children.Count)
            throw new InvalidOperationException($"CDX branch layout covers {start} of {children.Count} children.");

        bool isRoot = nodes.Count == 1;
        var result = new List<ChildRef>(nodes.Count);
        for (int n = 0; n < nodes.Count; n++)
        {
            var (index, kids) = nodes[n];
            uint left = n > 0 ? (uint)(nodes[n - 1].index * Page) : NoPtr;
            uint right = n < nodes.Count - 1 ? (uint)(nodes[n + 1].index * Page) : NoPtr;
            WriteBranch(sink.At(index), kids, keyLen, (ushort)((isRoot ? AttrRoot : 0) | attrExtra), left, right);

            var last = kids[^1];
            result.Add(new ChildRef((uint)(index * Page), last.SeparatorKey, last.SeparatorRecno));
        }
        return result;
    }

    private static void WriteBranch(byte[] page, List<ChildRef> kids, int keyLen, ushort attr, uint left, uint right)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), (ushort)kids.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

        int off = StdHeader;
        foreach (var k in kids)
        {
            int copy = Math.Min(keyLen, k.SeparatorKey.Length);
            k.SeparatorKey.AsSpan(0, copy).CopyTo(page.AsSpan(off, keyLen));
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen, 4), k.SeparatorRecno);
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen + 4, 4), k.Offset);
            off += keyLen + 8;
        }
    }

    // ---- leaf packing (mirror of CompactLeaf.Decode / r4reinde.c) ---------------

    /// <summary>
    /// Pack the sorted <paramref name="entries"/> into compact leaves the way VFP9's INDEX ON does, which appends the
    /// sorted keys one by one.
    /// <para>
    /// Each leaf has its OWN geometry, the narrowest whose record-number field holds the largest recno in that leaf.
    /// The first leaf starts with the narrowest geometry of all; an entry whose recno does not fit the current
    /// leaf's field widens the whole leaf if the leaf, re-encoded, still fits the page, else a new leaf starts with
    /// that entry (sized for its recno). Heavily duplicated short keys show it: sorted by key then recno, a key's
    /// recnos climb within its run, so leaves holding only small recnos pack 2-byte entries where a table-wide
    /// geometry needs 3 (VFP9: C(1), 17983 rows over 36 values give 92 leaves, not 112). An entry whose key is all
    /// pad (an empty C field) needs one byte more room than it takes. An empty tag gets the default geometry, the
    /// narrowest with a 12-bit record-number field, whatever the record count.
    /// </para>
    /// <para>
    /// With <paramref name="indexOn"/> the 0x04 attribute bit marks a leaf packed narrower than the geometry it was
    /// opened with: the first leaf is opened with the default geometry, every other leaf with the geometry of the
    /// leaf before it. It is decided when the leaf opens and again whenever it widens, and closing changes it: a
    /// leaf the next recno would have widened past the page gets it, any other leaf but the first loses it.
    /// Without, it follows the entry size (<see cref="DataBit"/>). Derived from VFP9 over 60 tables (C keys of
    /// length 1-200 under MACHINE and GENERAL, I and N keys, ascending, descending and unique, up to 70000 rows,
    /// all-blank keys, empty tags, hand-made recno runs): every leaf page reproduces byte for byte.
    /// </para>
    /// </summary>
    private static List<LeafNode> PackLeaves(List<Entry> entries, int keyLen, byte pad, bool indexOn = true)
    {
        var leaves = new List<LeafNode>();
        var standard = LeafGeometry.For(keyLen, EmptyTagMaxRecno);
        if (entries.Count == 0)
        {
            leaves.Add(new LeafNode
            {
                Items = new List<LeafItem>(), FreeSpace = (ushort)LeafBudget, Geom = standard,
                AttrExtra = indexOn ? (ushort)0 : DataBit(standard),
            });
            return leaves;
        }

        var cur = new List<LeafItem>();
        int used = 0;                      // distinct key bytes in the current leaf
        uint maxRecno = 0;
        byte[]? prev = null;
        int prevTrail = keyLen;
        var geom = LeafGeometry.For(keyLen, 1);
        var opened = standard;             // the geometry the current leaf was opened with
        ushort attr = Narrower(geom, opened);

        foreach (var e in entries)
        {
            var (dup, trail) = DupTrail(e.Key, prev, prevTrail, keyLen, pad);
            int len = keyLen - dup - trail;
            uint max = Math.Max(maxRecno, e.Recno);
            var need = LeafGeometry.For(keyLen, max);
            bool widen = need.RecnoBits > geom.RecnoBits;
            if (cur.Count > 0 && used + Room(len, trail, keyLen) + (cur.Count + 1) * need.BytesPerEntry > LeafBudget)
            {
                // Current leaf is full: flush it and start a fresh one (dup resets to 0).
                if (widen) attr = AttrData;
                else if (leaves.Count > 0) attr = 0;
                Flush();
                opened = geom;
                cur = new List<LeafItem>();
                used = 0;
                (dup, trail) = DupTrail(e.Key, null, keyLen, keyLen, pad);
                len = keyLen - dup - trail;
                max = e.Recno;
                geom = LeafGeometry.For(keyLen, max);
                attr = Narrower(geom, opened);
            }
            else if (widen)
            {
                geom = need;
                attr = Narrower(geom, opened);
            }

            var fresh = new byte[len];
            Array.Copy(e.Key, dup, fresh, 0, len);
            cur.Add(new LeafItem(e.Recno, dup, trail, fresh));
            used += len;
            maxRecno = max;
            prev = e.Key;
            prevTrail = trail;
        }

        Flush();
        return leaves;

        void Flush()
        {
            var leaf = MakeLeafNode(cur, used, maxRecno, keyLen, prev!);
            leaf.AttrExtra = indexOn ? attr : DataBit(leaf.Geom);
            leaves.Add(leaf);
        }

        static ushort Narrower(LeafGeometry geom, LeafGeometry than) => geom.RecnoBits < than.RecnoBits ? AttrData : (ushort)0;
    }

    /// <summary>The room an entry needs in a leaf: its distinct key bytes, one more for an all-pad key (VFP9 leaves
    /// 2 bytes free where one more 2-byte blank entry would fit).</summary>
    private static int Room(int len, int trail, int keyLen) => trail == keyLen ? len + 1 : len;

    private static LeafNode MakeLeafNode(List<LeafItem> items, int used, uint maxRecno, int keyLen, byte[] lastKey)
    {
        var geom = LeafGeometry.For(keyLen, maxRecno);
        return new LeafNode
        {
            Items = items,
            Geom = geom,
            FreeSpace = (ushort)(LeafBudget - used - items.Count * geom.BytesPerEntry),
            LastKey = lastKey,
            LastRecno = items[^1].Recno,
        };
    }

    /// <summary>The leading bytes an entry shares with the previous one (dup) and its trailing pad bytes (trail)
    /// in a compact leaf — <paramref name="prev"/> null for a leaf's first entry. Mirrors CompactLeaf.Decode.</summary>
    private static (int Dup, int Trail) DupTrail(byte[] key, byte[]? prev, int prevTrail, int keyLen, byte pad)
    {
        int dup = 0;
        if (prev is not null)
        {
            dup = CommonPrefix(key, prev, keyLen);
            if (dup > keyLen - prevTrail) dup = keyLen - prevTrail;
        }
        int trail = dup == keyLen ? 0 : TrailingPad(key, keyLen, pad);
        if (dup > keyLen - trail) dup = keyLen - trail;
        return (dup, trail);
    }

    /// <summary>The 0x04 bit by the entry size: set when the entry info packs into one or two bytes (the .dcx goldens'
    /// rule, kept outside INDEX ON).</summary>
    private static ushort DataBit(LeafGeometry geom) => geom.BytesPerEntry <= 2 ? AttrData : (ushort)0;

    private static void WriteLeaf(byte[] page, LeafNode leaf, int keyLen, ushort attr, uint left, uint right)
    {
        var geom = leaf.Geom;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), (ushort)leaf.Items.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

        // Leaf info block (@12..23).
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), leaf.FreeSpace);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), geom.RecnoMask);
        page[18] = geom.DupMask;
        page[19] = geom.TrailMask;
        page[20] = geom.RecnoBits;
        page[21] = geom.DupBits;
        page[22] = geom.TrailBits;
        page[23] = geom.BytesPerEntry;

        int kby = geom.BytesPerEntry;
        int infoPos = LeafDataStart;   // entry-info array grows forward from 24
        int tail = Page;               // distinct key bytes grow backward from the page end
        foreach (var item in leaf.Items)
        {
            ulong v = (ulong)item.Recno & geom.RecnoMaskFull
                      | ((ulong)(uint)item.Dup << geom.RecnoBits)
                      | ((ulong)(uint)item.Trail << (geom.RecnoBits + geom.DupBits));
            for (int b = 0; b < kby; b++)
                page[infoPos + b] = (byte)(v >> (8 * b));
            infoPos += kby;

            tail -= item.Fresh.Length;
            Array.Copy(item.Fresh, 0, page, tail, item.Fresh.Length);
        }
    }

    // ---- incremental-edit encoding primitives (shared with the write-path CDX maintenance) ---------

    /// <summary>A branch child slot: the child's MAX (separator) key + that key's recno, and the child
    /// node's byte offset. Mirrors one on-disk branch entry <c>[key][recno BE][child BE]</c>.</summary>
    internal readonly record struct BranchChild(byte[] SepKey, uint SepRecno, uint Child);

    /// <summary>Bytes a compact leaf holding <paramref name="count"/> sorted entries from <paramref name="start"/>
    /// needs (distinct key bytes + entry info at that leaf's own record-number geometry, plus the extra byte an
    /// all-pad last key needs, see <see cref="PackLeaves"/>). The leaf fits when the result is at most
    /// <see cref="LeafCapacity"/>.</summary>
    internal static int LeafUsedBytes(IReadOnlyList<(byte[] Key, uint Recno)> entries, int start, int count, int keyLen, byte pad)
    {
        if (count <= 0)
            return 0;
        int used = 0;
        uint maxRecno = 0;
        byte[]? prev = null;
        int prevTrail = keyLen;
        for (int i = start; i < start + count; i++)
        {
            var (key, recno) = entries[i];
            var (dup, trail) = DupTrail(key, prev, prevTrail, keyLen, pad);
            used += keyLen - dup - trail;
            if (recno > maxRecno) maxRecno = recno;
            prev = key;
            prevTrail = trail;
        }
        // The last entry is the one that had to fit: an all-pad key needs one byte more room than it takes.
        return used + (prevTrail == keyLen ? 1 : 0) + count * LeafGeometry.For(keyLen, maxRecno).BytesPerEntry;
    }

    /// <summary>The key/entry-info budget of one compact leaf page (512 − 24 header bytes).</summary>
    internal const int LeafCapacity = LeafBudget;

    /// <summary>
    /// Encode the <paramref name="count"/> sorted entries from <paramref name="start"/> as ONE compact leaf page
    /// (its own record-number geometry, byte-compatible with a bulk build), or <see langword="null"/> when they do
    /// not fit a page. The attribute carries the leaf (+ data) bits and the root bit when <paramref name="isRoot"/>.
    /// </summary>
    internal static byte[]? EncodeLeafPage(IReadOnlyList<(byte[] Key, uint Recno)> entries, int start, int count,
        int keyLen, byte pad, bool isRoot, uint left, uint right)
    {
        if (LeafUsedBytes(entries, start, count, keyLen, pad) > LeafBudget)
            return null;
        var items = new List<LeafItem>(count);
        int used = 0;
        uint maxRecno = 0;
        byte[]? prev = null;
        int prevTrail = keyLen;
        for (int i = start; i < start + count; i++)
        {
            var (key, recno) = entries[i];
            var (dup, trail) = DupTrail(key, prev, prevTrail, keyLen, pad);
            int len = keyLen - dup - trail;
            var fresh = new byte[len];
            Array.Copy(key, dup, fresh, 0, len);
            items.Add(new LeafItem(recno, dup, trail, fresh));
            used += len;
            if (recno > maxRecno) maxRecno = recno;
            prev = key;
            prevTrail = trail;
        }
        // An empty ROOT leaf (a tag whose last entry went) gets INDEX ON's empty geometry; an emptied leaf left behind
        // in the file gets no record-number bits at all (VFP9).
        var leaf = count == 0
            ? new LeafNode { Items = items, FreeSpace = (ushort)LeafBudget, Geom = LeafGeometry.For(keyLen, isRoot ? EmptyTagMaxRecno : 0) }
            : MakeLeafNode(items, used, maxRecno, keyLen, prev!);
        var page = new byte[Page];
        WriteLeaf(page, leaf, keyLen, (ushort)((isRoot ? AttrRoot : 0) | AttrLeaf | DataBit(leaf.Geom)), left, right);
        return page;
    }

    /// <summary>Maximum branch entries that fit one 512-byte page for the given key length.</summary>
    internal static int BranchCapacity(int keyLen)
    {
        int entrySize = keyLen + 8;
        int maxPer = (Page - StdHeader) / entrySize;
        return maxPer < 1 ? 1 : maxPer;
    }

    /// <summary>Encode a branch (interior) page from <paramref name="kids"/> — the inverse of
    /// <see cref="IndexFile.ReadBranchEntries"/>. Sibling pointers and the attribute word are supplied by
    /// the caller (the incremental editor manages the root bit + sibling chain).</summary>
    internal static byte[] EncodeBranchPage(IReadOnlyList<BranchChild> kids, int keyLen, ushort attr, uint left, uint right)
    {
        var page = new byte[Page];
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), (ushort)kids.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

        int off = StdHeader;
        foreach (var k in kids)
        {
            int copy = Math.Min(keyLen, k.SepKey.Length);
            k.SepKey.AsSpan(0, copy).CopyTo(page.AsSpan(off, keyLen));
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen, 4), k.SepRecno);
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen + 4, 4), k.Child);
            off += keyLen + 8;
        }
        return page;
    }

    /// <summary>The node-attribute / sentinel constants the incremental editor needs for page patching.</summary>
    internal const ushort AttrRootBit = AttrRoot;
    internal const uint NoPointer = NoPtr;

    // ---- key encoding ----------------------------------------------------------

    /// <summary>
    /// The stored key length for a tag. For a MACHINE (identity) collation the CHARACTER key is the
    /// raw value bytes (the field / expression width). For a NON-identity collation (GENERAL) over a
    /// CHARACTER key VFP reserves the EXPANDED width — 2× the base length — because each source
    /// character can yield up to two head weight bytes (the ligatures Œ→OE, Æ→AE, Þ→TH, ß→SS) plus a
    /// diacritic-tail run; truncating to the field width would discard tail/diacritic weight and
    /// mis-order accented or wide data (and be byte-incompatible with VFP9). Verified against the
    /// VFP9-built fixture <c>vfp_test/ligtest.CDX</c> (C(10) GENERAL ⇒ KeyLength 20) and
    /// character expressions of width 120 (GENERAL ⇒ 240). VFP9 rejects wider keys with error 112;
    /// it does not cap them, which would lose the expression width needed by SEEK under EXACT OFF.
    /// </summary>
    private static int CollatedKeyLength(int baseLen, bool isChar, IVfpCollation collation)
    {
        int factor = isChar && !IsIdentityCollation(collation) ? 2 : 1;
        if (baseLen > 240 / factor)
            throw new DbfWriteException("Index key is too long.", 112);
        return Math.Max(1, baseLen * factor);
    }

    /// <summary>An identity collation (MACHINE) maps a character value to its raw code-page bytes,
    /// so the stored key needs no expansion; any other (GENERAL) is a variable-length weight key.</summary>
    private static bool IsIdentityCollation(IVfpCollation collation)
        => string.Equals(collation.Name, "MACHINE", StringComparison.OrdinalIgnoreCase);

    private static byte[] EncodeKey(VfpValue value, IndexKeyType type, int keyLen, IVfpCollation collation,
        Encoding characterEncoding, byte pad)
    {
        if (type == IndexKeyType.Character)
        {
            // Pad the NATURAL collated key up to the (already collation-expanded) keyLen: blanks under MACHINE,
            // 0x00 under GENERAL / SPANISH (VFP9; see IndexKey.CharacterKeyPad). MACHINE stores the raw TABLE
            // code-page bytes (VFP9: CP850 'Peñarol' = 50 65 A4 ...). For GENERAL the natural key is the head+tail
            // weight run, which for accented / ligature data is LONGER than the field width — keyLen is 2× the base
            // width so it rarely is truncated; when heads+tails still overflow, the key is cut at keyLen.
            var natural = value.IsNull
                ? Array.Empty<byte>()
                : IndexKey.EncodeCharacterKey(value.AsString.AsSpan(), collation, characterEncoding, keyLen);
            var key = new byte[keyLen];
            for (int i = 0; i < keyLen; i++) key[i] = pad;
            int copy = Math.Min(natural.Length, keyLen);
            natural.AsSpan(0, copy).CopyTo(key);
            return key;
        }

        object? clr = type switch
        {
            IndexKeyType.Logical => value.IsNull ? null : (object)value.AsLogical,
            IndexKeyType.Integer => value.IsNull ? null : (object)value.AsInteger,
            IndexKeyType.Numeric => value.IsNull ? null : (object)value.AsDouble,
            IndexKeyType.Date => value.IsNull ? null : value.ToClr(),
            IndexKeyType.DateTime => value.IsNull ? null : value.ToClr(),
            _ => null,
        };

        byte[]? encoded = clr is null ? null : IndexKey.Encode(clr, type);
        if (encoded is null)
            return new byte[keyLen]; // null / unencodable → all-zero key (sorts first)

        var buf = new byte[keyLen];
        Array.Copy(encoded, buf, Math.Min(encoded.Length, keyLen));
        return buf;
    }

    /// <summary>Resolve the on-disk key type, byte length and char-ness for a tag KEY expression.</summary>
    internal static Func<IRowContext, VfpValue> CompileIndex(string text, EvaluationContext context)
    {
        var expression = VfpExpression.Parse(text);
        if (context.IndexEvaluator is { } evaluate && VfpRuntime.UnsupportedIndexFunction(text) is not null)
            return row => evaluate(expression, row, context);
        return expression.Compile(context);
    }

    internal static (IndexKeyType Type, int KeyLen, bool IsChar) ResolveKey(DbfTable schema, string keyExpr, EvaluationContext? context = null)
    {
        if (context?.IndexEvaluator is { } evaluate && VfpRuntime.UnsupportedIndexFunction(keyExpr) is not null)
        {
            var record = (schema.RecordCount > 0 ? schema.GetRecord(0) : null) ?? new DbfRecord(schema, new byte[schema.RecordLength]);
            var value = evaluate(VfpExpression.Parse(keyExpr), new RowContext(record, 1, schema.RecordCount), context);
            return value.Type switch
            {
                VfpType.Character => (IndexKeyType.Character, Math.Max(1, value.AsString.Length), true),
                VfpType.Logical => (IndexKeyType.Logical, 1, false),
                VfpType.Date => (IndexKeyType.Date, 8, false),
                VfpType.DateTime => (IndexKeyType.DateTime, 8, false),
                _ => (IndexKeyType.Numeric, 8, false),
            };
        }
        string field = (keyExpr ?? string.Empty).Trim();
        foreach (var col in schema.Columns)
        {
            if (string.Equals(col.Name, field, StringComparison.OrdinalIgnoreCase))
                return FromColumn(col.Type, col.Length);
        }

        // Composite expression: infer the static type from the schema.
        try
        {
            var info = VfpExpression.Parse(keyExpr ?? string.Empty).InferType(new TableSchema(schema));
            if (info.Type == VfpType.Character && info.Length == 0)
            {
                var record = (schema.RecordCount > 0 ? schema.GetRecord(0) : null) ?? new DbfRecord(schema, new byte[schema.RecordLength]);
                var value = CompileIndex(keyExpr!, context ?? new EvaluationContext { Encoding = schema.Encoding })(new RowContext(record, 1, schema.RecordCount));
                return (IndexKeyType.Character, Math.Max(1, value.AsString.Length), true);
            }
            return info.Type switch
            {
                VfpType.Character => (IndexKeyType.Character, Math.Max(1, info.Length), true),
                VfpType.Integer => (IndexKeyType.Integer, 4, false),
                VfpType.Numeric or VfpType.Currency => (IndexKeyType.Numeric, 8, false),
                VfpType.Logical => (IndexKeyType.Logical, 1, false),
                VfpType.Date => (IndexKeyType.Date, 8, false),
                VfpType.DateTime => (IndexKeyType.DateTime, 8, false),
                _ => (IndexKeyType.Character, Math.Max(1, info.Length == 0 ? 10 : info.Length), true),
            };
        }
        catch
        {
            return (IndexKeyType.Character, 10, true);
        }
    }

    private static (IndexKeyType, int, bool) FromColumn(char dbfType, int length) => dbfType switch
    {
        'C' or 'V' => (IndexKeyType.Character, Math.Max(1, length), true),
        'I' => (IndexKeyType.Integer, 4, false),
        'N' or 'F' or 'Y' or 'B' => (IndexKeyType.Numeric, 8, false),
        'L' => (IndexKeyType.Logical, 1, false),
        'D' => (IndexKeyType.Date, 8, false),
        'T' => (IndexKeyType.DateTime, 8, false),
        _ => (IndexKeyType.Character, Math.Max(1, length), true),
    };

    // ---- header / pool writers -------------------------------------------------

    private static void WriteHeaderCommon(byte[] page, uint root, int keyLen, byte options, byte signature,
        string? sortOrder, bool descending, byte[] keyExpr, byte[] forExpr)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0), root);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), 0u);    // free list (unused)
        BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(8), 0u);       // version counter
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), (ushort)keyLen);
        page[14] = options;
        page[15] = signature;

        // MACHINE collation is encoded as an EMPTY sort-order field (all zeros): VFP looks up
        // any NON-empty name as a named collating sequence and raises error 1915 ("Collating
        // sequence 'MACHINE' is not found") on the literal "MACHINE" — verified against the real
        // VFP9 runtime. Only a real named collation (GENERAL, ...) writes its name at @494.
        if (!string.IsNullOrEmpty(sortOrder) && !string.Equals(sortOrder, "MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            byte[] s = Encoding.ASCII.GetBytes(sortOrder);
            int n = Math.Min(s.Length, 8);
            s.AsSpan(0, n).CopyTo(page.AsSpan(494, 8));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(502), (ushort)(descending ? 1 : 0));
        ushort forLen = (ushort)(forExpr.Length + 1);  // encoded BYTES, includes terminating NUL
        ushort keyLenExpr = (ushort)(keyExpr.Length + 1);
        // @504 mirrors the KEY-expression length (VFP writes it alongside the @510 copy the reader uses).
        // Confirmed in vfp_test/dbctest/ref.DCX and vfp_test/ligtest.CDX: @504 == @510 == keyExprLen on
        // every header page (file header and each tag). The reader ignores it; byte-identity needs it.
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(504), keyLenExpr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(506), forLen);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(510), keyLenExpr);
    }

    /// <summary>Write the KEY then FOR expression strings into the pool at <paramref name="headerOffset"/> + 512.</summary>
    private static void WriteExpressionPool(PageSink sink, long headerOffset, byte[] keyExpr, byte[] forExpr)
    {
        int poolIndex = (int)((headerOffset / Page) + 1);
        var pool = sink.At(poolIndex);
        keyExpr.CopyTo(pool, 0);
        pool[keyExpr.Length] = 0;
        forExpr.CopyTo(pool, keyExpr.Length + 1);
        pool[keyExpr.Length + 1 + forExpr.Length] = 0;
    }

    // ---- small helpers ---------------------------------------------------------

    /// <summary>The 10-byte, space-padded directory key: <paramref name="name"/> in the table code page
    /// (VFP9 stores tag RAZÓN with 'Ó' = 0xD3 in CP1252). An unencodable or over-long name throws instead of
    /// being substituted or clipped — a clipped key would no longer match the name it was created under.</summary>
    private static byte[] MakeNameKey(string name, Encoding encoding)
    {
        string trimmed = name.Trim();
        byte[] bytes = CodePageText.Encode(trimmed, encoding, "CDX tag name", m => new DbfWriteException(m));
        if (bytes.Length > 10)
            throw new DbfWriteException(
                $"CDX tag name '{trimmed}' needs {bytes.Length} bytes in code page {encoding.CodePage}; a tag name holds 10.");
        var key = new byte[10];
        for (int i = 0; i < 10; i++) key[i] = 0x20;
        bytes.CopyTo(key, 0);
        return key;
    }

    private static List<Entry> DropDuplicateKeys(List<Entry> sorted)
    {
        var result = new List<Entry>(sorted.Count);
        byte[]? prev = null;
        foreach (var e in sorted)
        {
            if (prev is not null && prev.AsSpan().SequenceEqual(e.Key))
                continue; // duplicate key → keep only the first (lowest recno).
            result.Add(e);
            prev = e.Key;
        }
        return result;
    }

    private sealed class KeyBytesEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly KeyBytesEqualityComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
            => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            unchecked
            {
                int hash = 17;
                foreach (byte b in obj)
                    hash = (hash * 31) + b;
                return hash;
            }
        }
    }

    private static int CommonPrefix(byte[] a, byte[] b, int len)
    {
        int n = Math.Min(len, Math.Min(a.Length, b.Length));
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static int TrailingPad(byte[] key, int len, byte pad)
    {
        int n = Math.Min(len, key.Length);
        int t = 0;
        while (t < n && key[n - 1 - t] == pad) t++;
        return t;
    }

    internal static int Bits(long value)
    {
        int c = 0;
        ulong v = (ulong)Math.Max(0, value);
        while (v != 0) { c++; v >>= 1; }
        return c;
    }

    // ---- supporting types ------------------------------------------------------

    private sealed class PageSink
    {
        private readonly List<byte[]> _pages = new();
        private readonly int _basePage;

        /// <summary>A sink whose first reserved page is ABSOLUTE page index <paramref name="basePage"/> — 0 for
        /// a whole-file <see cref="Build"/>, or the append base for a single-tag block (<see cref="BuildTagBlock"/>).
        /// <see cref="Reserve"/> hands out absolute page indices and <see cref="At"/> maps them back, so the B-tree
        /// bulk-loader emits correct ABSOLUTE byte offsets whether it lays down page 0 of a fresh file or a block
        /// appended at the end of an existing one.</summary>
        public PageSink(int basePage = 0) { _basePage = basePage; }

        public int Reserve() { _pages.Add(new byte[Page]); return _basePage + _pages.Count - 1; }
        public byte[] At(int index) => _pages[index - _basePage];
        public byte[] ToArray()
        {
            var buf = new byte[_pages.Count * Page];
            for (int i = 0; i < _pages.Count; i++)
                Array.Copy(_pages[i], 0, buf, i * Page, Page);
            return buf;
        }
    }

    private readonly record struct Entry(byte[] Key, uint Recno);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static readonly EntryComparer Instance = new();
        public int Compare(Entry x, Entry y)
        {
            int n = Math.Min(x.Key.Length, y.Key.Length);
            for (int i = 0; i < n; i++)
            {
                int d = x.Key[i] - y.Key[i];
                if (d != 0) return d;
            }
            if (x.Key.Length != y.Key.Length) return x.Key.Length - y.Key.Length;
            return x.Recno.CompareTo(y.Recno);
        }
    }

    private readonly record struct LeafItem(uint Recno, int Dup, int Trail, byte[] Fresh);

    private sealed class LeafNode
    {
        public List<LeafItem> Items = new();
        public LeafGeometry Geom;
        public ushort AttrExtra;          // the 0x04 attribute bit, or 0
        public ushort FreeSpace;
        public byte[] LastKey = Array.Empty<byte>();
        public uint LastRecno;
        public int Index;
    }

    private readonly record struct ChildRef(uint Offset, byte[] SeparatorKey, uint SeparatorRecno);

    /// <summary>The compact-leaf bit-width geometry (mirrors CodeBase <c>r4reinde.c</c>).</summary>
    private readonly struct LeafGeometry
    {
        public readonly byte RecnoBits, DupBits, TrailBits, BytesPerEntry;
        public readonly byte DupMask, TrailMask;
        public readonly uint RecnoMask;
        public readonly ulong RecnoMaskFull;

        private LeafGeometry(byte recnoBits, byte dupBits, byte trailBits, byte kby)
        {
            RecnoBits = recnoBits; DupBits = dupBits; TrailBits = trailBits; BytesPerEntry = kby;
            DupMask = (byte)((1 << dupBits) - 1);
            TrailMask = (byte)((1 << trailBits) - 1);
            RecnoMask = recnoBits >= 32 ? 0xFFFFFFFFu : (1u << recnoBits) - 1;
            RecnoMaskFull = RecnoMask;
        }

        /// <summary>The geometry of a leaf whose highest record number is <paramref name="maxRecno"/>.</summary>
        public static LeafGeometry For(int keyLen, long maxRecno)
        {
            int dupTrailBits = Math.Max(1, Bits(keyLen));

            // Cap the record-number width at 32 bits BEFORE the byte-boundary padding, then pad. The
            // padding may push recBits past 32 — that is fine: RecnoMask already saturates to
            // 0xFFFFFFFF for recnoBits ≥ 32 and the decoder shifts dup/trail by the on-disk RecnoBits,
            // so RecnoBits + DupBits + TrailBits stays exactly equal to BytesPerEntry·8 (the invariant
            // VFP/CodeBase validate). Capping AFTER padding instead would desync the bit widths from
            // BytesPerEntry near ~2^31 records.
            // An EMPTY leaf left behind by maintenance has no record-number bits at all (VFP9).
            int recBits = Math.Min(32, Bits(maxRecno));
            while ((recBits + dupTrailBits + dupTrailBits) % 8 != 0)
                recBits++;
            int kby = (recBits + dupTrailBits + dupTrailBits) / 8;

            return new LeafGeometry((byte)recBits, (byte)dupTrailBits, (byte)dupTrailBits, (byte)kby);
        }
    }

    /// <summary>Adapts a <see cref="DbfRecord"/> to the expression engine row contract.</summary>
    internal sealed class RowContext : IRowContext, ISchema
    {
        private readonly DbfRecord _record;
        public RowContext(DbfRecord record, int recNo, int recCount) { _record = record; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _record.IsDeleted;
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
            => new TableSchema(_record.Table).TryGetColumn(name, out type, out length, out decimals);

        public object? GetField(string name)
        {
            // VFP evaluates an index KEY/FOR expression over the FULL declared field width — a
            // CHARACTER field keeps its trailing space padding (and any leading spaces). The record
            // reader (DbfRecord[name]) TRIMS character fields (§A5/ruby-dbf oracle), which is correct
            // for display but WRONG inside a composite key: e.g. str(parentid)+objecttype+lower(name)
            // must embed objecttype as "Field     " (C(10)), not the trimmed "Field". Re-decode the
            // raw bytes at full width for 'C' fields; a NULL-flagged field still reads as NULL.
            var value = _record[name];
            if (value is DateOnly date) return VfpValue.Date(date, empty: false);
            if (value is DateTime timestamp) return VfpValue.DateTime(timestamp, empty: false);
            if (value is null)
            {
                foreach (var col in _record.Table.Columns)
                {
                    if (string.Equals(col.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        // Blank D/T fields remain typed empties in KEY/FOR expressions; genuine
                        // .NULL. still follows the record's null flag (VFP9 empty-date oracle).
                        if (col.Type is 'D' or 'T')
                            return _record.GetVfpValue(name);
                        break;
                    }
                }
            }
            if (value is string)
            {
                foreach (var col in _record.Table.Columns)
                {
                    if (string.Equals(col.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (col.Type == 'C')
                        {
                            var raw = _record.GetRawField(col);
                            var enc = col.IsBinary ? Encoding.Latin1 : _record.Table.Encoding;
                            return FieldDecoder.DecodeCharacterPadded(col, raw, enc, _record.Table.SuppressUtf8Detection);
                        }
                        break;
                    }
                }
            }
            return value;
        }
    }

    /// <summary>Adapts a <see cref="DbfTable"/> to the expression engine static-schema contract.</summary>
    internal sealed class TableSchema : ISchema
    {
        private readonly DbfTable _table;
        public TableSchema(DbfTable table) => _table = table;
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            foreach (var c in _table.Columns)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    type = c.Type; length = c.Length; decimals = c.Decimal;
                    return true;
                }
            }
            type = '\0'; length = 0; decimals = 0;
            return false;
        }
    }
}
