using System.Buffers.Binary;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// TRUE INCREMENTAL edit of a live compound <c>.cdx</c> B-tree (project-review finding 5.1): insert or
/// delete a single <c>(key, recno)</c> entry in one tag's tree by descending to the target leaf,
/// decoding it (<see cref="CompactLeaf"/>), editing the entry list, and re-encoding via the SAME
/// primitives the bulk <see cref="CdxIndexBuilder"/> uses — so a maintained leaf reads back byte-identical
/// to a fresh build. A node that overflows splits or shares its entries with its right sibling exactly as VFP9
/// does (the rules head the maintenance code below); new pages come from the end of the file and the root never moves.
/// Delete allows SPARSE leaves and unlinks a leaf that empties (no merge, as VFP; REINDEX compacts).
/// </summary>
/// <remarks>
/// The same row operations on the same file give the same pages as VFP9 — leaf bytes, branch entries, sibling
/// links, page numbers (checked against VFP9 page by page); only the 0x04 attribute bit, which VFP sets by page
/// history, and the unused tail of a branch page, where VFP leaves stale bytes, can differ.
/// The reader/editor never trusts a malformed page beyond the defensive guards inherited from
/// <see cref="IndexFile"/>. The key pad (0x20 for a MACHINE character tag, else 0x00) is supplied per operation by
/// the caller.
/// </remarks>
internal sealed class CdxTreeEditor : IDisposable
{
    private const int PageSize = IndexFile.PageSize;   // 512
    private const uint NoPtr = CdxIndexBuilder.NoPointer;

    private readonly FileStream _rw;
    private readonly IndexFile _reader;   // page reads over the SAME stream (leaveOpen)
    private long _end;                     // next free byte offset (== file length); new pages land here
    private Dictionary<long, byte[]>? _appendPageCache;
    private readonly System.Text.Encoding? _textEncoding;   // the table code page tag names/expressions are stored in

    public CdxTreeEditor(FileStream rw, System.Text.Encoding? textEncoding = null)
    {
        _rw = rw;
        _textEncoding = textEncoding;
        _reader = IndexFile.Open(rw, leaveOpen: true);
        _reader.TextEncoding = textEncoding;
        _end = rw.Length;
    }

    /// <summary>One tag of the compound index: its directory name, its header byte offset (where the
    /// root pointer lives), and the parsed header (root / key length / expressions / flags).</summary>
    public readonly record struct TagHandle(string Name, long HeaderOffset, CdxHeader Header);

    /// <summary>Enumerate every tag in the compound index (name + header offset + parsed header), so the
    /// caller can build a per-tag key computer and drive <see cref="Insert"/> / <see cref="Delete"/>.</summary>
    public List<TagHandle> ReadTags()
    {
        var result = new List<TagHandle>();
        var fileHeader = ReadCdxHeader(0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (headerOffset, nameBytes) in
                 EnumerateCompact(fileHeader.Root, fileHeader.KeyLength, pad: 0x20))
        {
            string name = TrimName(nameBytes, _textEncoding);
            if (name.Length == 0 || !seen.Add(name))
                continue;
            var hdr = ReadCdxHeader(headerOffset);
            result.Add(new TagHandle(name, headerOffset, hdr));
        }
        return result;
    }

    // ---- public single-entry operations ----------------------------------------

    /// <summary>Insert <paramref name="key"/> → <paramref name="recno"/> into the tag rooted at the header
    /// at <paramref name="headerOffset"/>, splitting / rebalancing nodes as VFP9 does.</summary>
    public void Insert(long headerOffset, byte[] key, uint recno, int keyLen, byte pad)
    {
        var path = Descend(ReadTagRoot(headerOffset), key, recno, keyLen, pad);
        var leaf = path.Leaf;
        int pos = leaf.Entries.Count;
        for (int i = 0; i < leaf.Entries.Count; i++)
            if (CompareEntry(leaf.Entries[i].Key, leaf.Entries[i].Recno, key, recno) > 0) { pos = i; break; }
        leaf.Entries.Insert(pos, (key, recno));
        InsertIntoLeaf(path, keyLen, pad);
    }

    /// <summary>Delete the entry <paramref name="key"/> → <paramref name="recno"/> from the tag rooted at
    /// the header at <paramref name="headerOffset"/> (a no-op when the exact entry is absent).</summary>
    public void Delete(long headerOffset, byte[] key, uint recno, int keyLen, byte pad)
    {
        var path = Descend(ReadTagRoot(headerOffset), key, recno, keyLen, pad);
        var leaf = path.Leaf;
        int idx = -1;
        for (int i = 0; i < leaf.Entries.Count; i++)
            if (leaf.Entries[i].Recno == recno && CompareBytes(leaf.Entries[i].Key, key) == 0) { idx = i; break; }
        if (idx < 0)
            return;   // absent: leave as-is
        leaf.Entries.RemoveAt(idx);
        DeleteFromLeaf(path, keyLen, pad);
    }

    /// <summary>True when the tag already stores <paramref name="key"/> (UNIQUE-tag pre-insert check —
    /// VFP keeps only the first record per distinct key in a UNIQUE tag). Descends to the leaf where the
    /// key would sort and scans forward while keys stay equal.</summary>
    public bool ContainsKey(long headerOffset, byte[] key, int keyLen, byte pad)
    {
        uint root = ReadTagRoot(headerOffset);
        long leaf = DescendToLeaf(root, key, keyLen);
        if (leaf < 0)
            return false;
        var visited = new HashSet<long>();
        long cur = leaf;
        while (cur >= 0 && visited.Add(cur))
        {
            foreach (var e in ReadLeafList(cur, keyLen, pad))
            {
                int c = CompareBytes(e.Key, key);
                if (c == 0) return true;
                if (c > 0) return false;   // passed where the key would sort
            }
            var hdr = ReadNodeHeader(cur);
            cur = hdr?.RightSibling is uint r ? r : -1;
        }
        return false;
    }

    public void BeginAppendRun()
        => _appendPageCache ??= new Dictionary<long, byte[]>();

    public void ClearAppendRunCache()
    {
        _appendPageCache?.Clear();
        _appendPageCache = null;
    }

    public void RefreshFromDisk()
    {
        ClearAppendRunCache();
        _end = _rw.Length;
    }

    internal int AppendPageCachePageCountForTests => _appendPageCache?.Count ?? 0;

    public void Flush() => _rw.Flush();
    public void Dispose()
    {
        ClearAppendRunCache();
        _reader.Dispose();   // leaveOpen: does not close _rw (owned by the caller)
    }

    // ---- VFP9's incremental B-tree maintenance -----------------------------------
    //
    // Derived page by page from VFP9 (INSERT INTO / REPLACE on indexed tables; every split/merge shape below
    // reproduced with page numbers, entries and sibling links): the root never moves, new pages come from the end
    // of the file in the order the nodes appear (a leaf before the branch nodes its split cascades into, bottom-up),
    // and a node that overflows
    //   • root leaf:   gives its entries to two new leaves (the right one on the lower page), filled greedily;
    //   • rightmost leaf (no right sibling): keeps all that fit, the rest go to a new right leaf — so appends in
    //                  key order fill leaves completely, exactly as INDEX ON does;
    //   • other leaf:  shares its entries with the right sibling when that sibling can take the overflowing
    //                  leaf's last entry (the two end up byte-balanced), else splits into itself + a new right leaf,
    //                  byte-balanced;
    //   • root branch, rightmost branch: keep all but the last child — on an ODD page with entries shorter than
    //                  50 bytes all but the last two (the rule CdxIndexBuilder.BranchLayout reproduces for INDEX ON);
    //   • other branch: shares its children with a right sibling that has room (left half rounded up), else it,
    //                  a new node and the full right sibling split the children three ways (ceil, floor, rest).
    // A leaf that empties is unlinked, written back as an empty leaf and dropped from its parent; nodes never merge.
    // Every leaf packs its record numbers at its own geometry (sized by the highest record number in it).

    private sealed class LeafNode
    {
        public long Off;
        public ushort Attr;
        public uint? Left, Right;
        public bool IsRoot;
        public List<(byte[] Key, uint Recno)> Entries = new();
    }

    private sealed class BranchNode
    {
        public long Off;
        public ushort Attr;
        public uint? Left, Right;
        public List<CdxIndexBuilder.BranchChild> Kids = new();
        public int Ci;   // the child the descent took
    }

    /// <summary>The nodes from the root down to the leaf where an entry sorts.</summary>
    private sealed class TreePath
    {
        public readonly List<BranchNode> Branches = new();   // root first
        public LeafNode Leaf = null!;
    }

    private TreePath Descend(long root, byte[] key, uint recno, int keyLen, byte pad)
    {
        var path = new TreePath();
        var seen = new HashSet<long>();
        long cur = root;
        while (true)
        {
            if (!seen.Add(cur))
                throw new InvalidDataException($"CDX tree loops back to the node at {cur}.");
            var page = ReadPage(cur);
            var hdr = IndexNodeHeader.Parse(page);
            if (hdr.IsLeaf)
            {
                path.Leaf = ToLeafNode(cur, page, hdr, keyLen, pad, isRoot: path.Branches.Count == 0);
                return path;
            }
            var node = ToBranchNode(cur, page, hdr, keyLen);
            if (node.Kids.Count == 0)
                throw new InvalidDataException($"CDX branch at {cur} has no children.");
            node.Ci = ChooseChild(node.Kids, key, recno);
            path.Branches.Add(node);
            cur = node.Kids[node.Ci].Child;
        }
    }

    private void InsertIntoLeaf(TreePath path, int keyLen, byte pad)
    {
        var leaf = path.Leaf;
        var s = leaf.Entries;
        int n = s.Count;
        int d = path.Branches.Count - 1;
        if (LeafFits(s, 0, n, keyLen, pad))
        {
            WriteLeaf(leaf.Off, s, 0, n, keyLen, pad, leaf.IsRoot, leaf.Left, leaf.Right);
            UpdateSeparators(path, d, s[^1], keyLen);
            return;
        }

        if (leaf.IsRoot)
        {
            // The root page stays put and becomes a branch over two new leaves (the right one on the lower page).
            int cut = GreedyCut(s, keyLen, pad);
            long right = Alloc(), left = Alloc();
            WriteLeaf(left, s, 0, cut, keyLen, pad, isRoot: false, null, (uint)right);
            WriteLeaf(right, s, cut, n - cut, keyLen, pad, isRoot: false, (uint)left, null);
            var kids = new List<CdxIndexBuilder.BranchChild> { Sep(s[cut - 1], left), Sep(s[^1], right) };
            WriteBranch(leaf.Off, kids, keyLen, (ushort)((leaf.Attr & ~LeafBit) | CdxIndexBuilder.AttrRootBit), null, null);
            return;
        }

        if (leaf.Right is not uint rightOff)
        {
            // Rightmost leaf: keep what fits, the rest opens a new leaf to the right.
            int cut = GreedyCut(s, keyLen, pad);
            long fresh = Alloc();
            WriteLeaf(leaf.Off, s, 0, cut, keyLen, pad, isRoot: false, leaf.Left, (uint)fresh);
            WriteLeaf(fresh, s, cut, n - cut, keyLen, pad, isRoot: false, (uint)leaf.Off, null);
            AddChild(path, d, s[cut - 1], Sep(s[^1], fresh), keyLen);
            return;
        }

        var sibling = ReadLeafNode(rightOff, keyLen, pad);
        var probe = new List<(byte[] Key, uint Recno)>(sibling.Entries.Count + 1) { s[^1] };
        probe.AddRange(sibling.Entries);
        if (LeafFits(probe, 0, probe.Count, keyLen, pad))
        {
            // The right sibling can take the last entry: the two leaves share all entries, byte-balanced.
            var pool = new List<(byte[] Key, uint Recno)>(n + sibling.Entries.Count);
            pool.AddRange(s);
            pool.AddRange(sibling.Entries);
            int cut = BalancedCut(pool, keyLen, pad);
            if (cut > 0)
            {
                WriteLeaf(leaf.Off, pool, 0, cut, keyLen, pad, isRoot: false, leaf.Left, leaf.Right);
                WriteLeaf(sibling.Off, pool, cut, pool.Count - cut, keyLen, pad, isRoot: false, sibling.Left, sibling.Right);
                UpdateSeparators(path, d, pool[cut - 1], keyLen);
                return;
            }
        }

        int split = BalancedCut(s, keyLen, pad);
        if (split <= 0)
            throw new InvalidDataException($"CDX leaf at {leaf.Off} cannot be split into two pages.");
        long added = Alloc();
        WriteLeaf(leaf.Off, s, 0, split, keyLen, pad, isRoot: false, leaf.Left, (uint)added);
        WriteLeaf(added, s, split, n - split, keyLen, pad, isRoot: false, (uint)leaf.Off, rightOff);
        PatchLeftPointer(rightOff, (uint)added);
        AddChild(path, d, s[split - 1], Sep(s[^1], added), keyLen);
    }

    /// <summary>Branch level <paramref name="d"/> of <paramref name="path"/>: its child on the path now ends at
    /// <paramref name="leftMax"/> and <paramref name="added"/> (a new node) follows it.</summary>
    private void AddChild(TreePath path, int d, (byte[] Key, uint Recno) leftMax, CdxIndexBuilder.BranchChild added, int keyLen)
    {
        var p = path.Branches[d];
        p.Kids[p.Ci] = Sep(leftMax, p.Kids[p.Ci].Child);
        p.Kids.Insert(p.Ci + 1, added);
        int cap = CdxIndexBuilder.BranchCapacity(keyLen);
        if (p.Kids.Count <= cap)
        {
            WriteBranch(p.Off, p.Kids, keyLen, p.Attr, p.Left, p.Right);
            UpdateSeparators(path, d - 1, Max(p.Kids), keyLen);
            return;
        }

        var all = p.Kids;
        // Full node on an ODD page with entries shorter than 50 bytes: it also gives up its last child.
        int keep = cap - (keyLen + 8 < 50 && (p.Off / PageSize) % 2 == 1 ? 1 : 0);
        ushort childAttr = (ushort)(p.Attr & ~CdxIndexBuilder.AttrRootBit);
        if (d == 0)
        {
            // Root: stays put over two new nodes (the right one on the lower page).
            long right = Alloc(), left = Alloc();
            var leftKids = all.GetRange(0, keep);
            var rightKids = all.GetRange(keep, all.Count - keep);
            WriteBranch(left, leftKids, keyLen, childAttr, null, (uint)right);
            WriteBranch(right, rightKids, keyLen, childAttr, (uint)left, null);
            var rootKids = new List<CdxIndexBuilder.BranchChild>
            {
                new(leftKids[^1].SepKey, leftKids[^1].SepRecno, (uint)left),
                new(rightKids[^1].SepKey, rightKids[^1].SepRecno, (uint)right),
            };
            WriteBranch(p.Off, rootKids, keyLen, p.Attr, null, null);
            return;
        }

        if (p.Right is not uint rightOff)
        {
            long fresh = Alloc();
            WriteBranch(p.Off, all.GetRange(0, keep), keyLen, p.Attr, p.Left, (uint)fresh);
            WriteBranch(fresh, all.GetRange(keep, all.Count - keep), keyLen, childAttr, (uint)p.Off, null);
            AddChild(path, d - 1, Max(all, keep - 1), Sep(Max(all, all.Count - 1), fresh), keyLen);
            return;
        }

        var sibling = ReadBranchNode(rightOff, keyLen);
        var pool = new List<CdxIndexBuilder.BranchChild>(all.Count + sibling.Kids.Count);
        pool.AddRange(all);
        pool.AddRange(sibling.Kids);
        if (sibling.Kids.Count < cap)
        {
            int half = (pool.Count + 1) / 2;
            WriteBranch(p.Off, pool.GetRange(0, half), keyLen, p.Attr, p.Left, p.Right);
            WriteBranch(sibling.Off, pool.GetRange(half, pool.Count - half), keyLen, sibling.Attr, sibling.Left, sibling.Right);
            UpdateSeparators(path, d - 1, Max(pool, half - 1), keyLen);
            return;
        }

        // Both full: this node, a new one and the right sibling share the children three ways.
        int first = (pool.Count + 2) / 3, second = pool.Count / 3;
        long middle = Alloc();
        WriteBranch(p.Off, pool.GetRange(0, first), keyLen, p.Attr, p.Left, (uint)middle);
        WriteBranch(middle, pool.GetRange(first, second), keyLen, childAttr, (uint)p.Off, rightOff);
        WriteBranch(sibling.Off, pool.GetRange(first + second, pool.Count - first - second), keyLen, sibling.Attr,
            (uint)middle, sibling.Right);
        AddChild(path, d - 1, Max(pool, first - 1), Sep(Max(pool, first + second - 1), middle), keyLen);
    }

    /// <summary>The child on the path at branch level <paramref name="d"/> (and up) now ends at
    /// <paramref name="childMax"/>: rewrite the separators that changed.</summary>
    private void UpdateSeparators(TreePath path, int d, (byte[] Key, uint Recno) childMax, int keyLen)
    {
        for (; d >= 0; d--)
        {
            var p = path.Branches[d];
            var cur = p.Kids[p.Ci];
            if (cur.SepRecno == childMax.Recno && CompareBytes(cur.SepKey, childMax.Key) == 0)
                return;
            p.Kids[p.Ci] = Sep(childMax, cur.Child);
            WriteBranch(p.Off, p.Kids, keyLen, p.Attr, p.Left, p.Right);
            childMax = Max(p.Kids, p.Kids.Count - 1);
        }
    }

    private void DeleteFromLeaf(TreePath path, int keyLen, byte pad)
    {
        var leaf = path.Leaf;
        var s = leaf.Entries;
        int d = path.Branches.Count - 1;
        if (s.Count > 0 || leaf.IsRoot)
        {
            // Removing an entry can still LENGTHEN its successor (a shorter shared prefix): refuse a re-pack that no
            // longer fits rather than drop entries — the callers fall back to a full rebuild.
            var page = CdxIndexBuilder.EncodeLeafPage(s, 0, s.Count, keyLen, pad, leaf.IsRoot,
                leaf.Left ?? NoPtr, leaf.Right ?? NoPtr)
                ?? throw new InvalidOperationException(
                    $"CDX leaf delete at {leaf.Off} no longer fits one page; the caller must fall back to a full rebuild.");
            WritePage(leaf.Off, page);
            if (s.Count > 0)
                UpdateSeparators(path, d, s[^1], keyLen);
            return;
        }

        // The leaf emptied: unlink it, leave it behind as an empty leaf, drop it from its parent.
        Unlink(leaf.Left, leaf.Right);
        WriteLeaf(leaf.Off, s, 0, 0, keyLen, pad, isRoot: false, leaf.Left, leaf.Right);
        RemoveChild(path, d, keyLen, pad);
    }

    private void RemoveChild(TreePath path, int d, int keyLen, byte pad)
    {
        var p = path.Branches[d];
        p.Kids.RemoveAt(p.Ci);
        if (p.Kids.Count > 0)
        {
            WriteBranch(p.Off, p.Kids, keyLen, p.Attr, p.Left, p.Right);
            if (p.Ci == p.Kids.Count)
                UpdateSeparators(path, d - 1, Max(p.Kids, p.Kids.Count - 1), keyLen);
            return;
        }
        if (d == 0)
        {
            // The whole tag emptied: the root page becomes an empty root leaf.
            WriteLeaf(p.Off, new List<(byte[] Key, uint Recno)>(), 0, 0, keyLen, pad, isRoot: true, null, null);
            return;
        }
        Unlink(p.Left, p.Right);   // an emptied branch is only unlinked (its page is not reused)
        RemoveChild(path, d - 1, keyLen, pad);
    }

    // ---- node helpers ----------------------------------------------------------

    private const ushort LeafBit = 0x02;

    private long Alloc()
    {
        long off = _end;
        _end += PageSize;
        return off;
    }

    private static bool LeafFits(List<(byte[] Key, uint Recno)> s, int start, int count, int keyLen, byte pad)
        => CdxIndexBuilder.LeafUsedBytes(s, start, count, keyLen, pad) <= CdxIndexBuilder.LeafCapacity;

    /// <summary>The largest prefix of <paramref name="s"/> (at least one entry) that fits one leaf.</summary>
    private static int GreedyCut(List<(byte[] Key, uint Recno)> s, int keyLen, byte pad)
    {
        int lo = 1, hi = s.Count;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (LeafFits(s, 0, mid, keyLen, pad)) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The cut that splits <paramref name="s"/> into two leaves whose used bytes differ least (the
    /// smaller left part on a tie), both fitting a page; 0 when no such cut exists.</summary>
    private static int BalancedCut(List<(byte[] Key, uint Recno)> s, int keyLen, byte pad)
    {
        int n = s.Count;
        if (n < 2)
            return 0;
        int Diff(int i) => CdxIndexBuilder.LeafUsedBytes(s, 0, i, keyLen, pad)
                           - CdxIndexBuilder.LeafUsedBytes(s, i, n - i, keyLen, pad);
        // The left part grows and the right part shrinks with the cut: find the first cut where left >= right.
        int lo = 1, hi = n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Diff(mid) >= 0) hi = mid; else lo = mid + 1;
        }
        int best = 0, bestDiff = int.MaxValue;
        for (int i = Math.Max(1, lo - 1); i <= Math.Min(n - 1, lo); i++)
        {
            if (!LeafFits(s, 0, i, keyLen, pad) || !LeafFits(s, i, n - i, keyLen, pad))
                continue;
            int diff = Math.Abs(Diff(i));
            if (diff < bestDiff) { best = i; bestDiff = diff; }
        }
        return best;
    }

    private void WriteLeaf(long off, List<(byte[] Key, uint Recno)> s, int start, int count, int keyLen, byte pad,
        bool isRoot, uint? left, uint? right)
    {
        var page = CdxIndexBuilder.EncodeLeafPage(s, start, count, keyLen, pad, isRoot, left ?? NoPtr, right ?? NoPtr)
            ?? throw new InvalidDataException($"CDX leaf at {off} does not fit one page.");
        WritePage(off, page);
    }

    private void WriteBranch(long off, List<CdxIndexBuilder.BranchChild> kids, int keyLen, ushort attr, uint? left, uint? right)
        => WritePage(off, CdxIndexBuilder.EncodeBranchPage(kids, keyLen, attr, left ?? NoPtr, right ?? NoPtr));

    private void PatchLeftPointer(long off, uint left)
    {
        var page = ReadPage(off);
        PatchLeft(page, left);
        WritePage(off, page);
    }

    private static CdxIndexBuilder.BranchChild Sep((byte[] Key, uint Recno) max, long child)
        => new(max.Key, max.Recno, (uint)child);

    private static (byte[] Key, uint Recno) Max(List<CdxIndexBuilder.BranchChild> kids, int i)
        => (kids[i].SepKey, kids[i].SepRecno);

    private static (byte[] Key, uint Recno) Max(List<CdxIndexBuilder.BranchChild> kids) => Max(kids, kids.Count - 1);

    private LeafNode ReadLeafNode(long off, int keyLen, byte pad)
    {
        var page = ReadPage(off);
        var hdr = IndexNodeHeader.Parse(page);
        if (!hdr.IsLeaf)
            throw new InvalidDataException($"CDX node at {off} is not a leaf.");
        return ToLeafNode(off, page, hdr, keyLen, pad, isRoot: false);
    }

    private BranchNode ReadBranchNode(long off, int keyLen)
    {
        var page = ReadPage(off);
        var hdr = IndexNodeHeader.Parse(page);
        if (hdr.IsLeaf)
            throw new InvalidDataException($"CDX node at {off} is not a branch.");
        return ToBranchNode(off, page, hdr, keyLen);
    }

    private static LeafNode ToLeafNode(long off, byte[] page, IndexNodeHeader hdr, int keyLen, byte pad, bool isRoot)
    {
        var node = new LeafNode { Off = off, Attr = hdr.Attribute, Left = hdr.LeftSibling, Right = hdr.RightSibling, IsRoot = isRoot };
        foreach (var e in CompactLeaf.Decode(page, keyLen, pad))
            node.Entries.Add((e.Key, e.RecordNumber));
        return node;
    }

    private static BranchNode ToBranchNode(long off, byte[] page, IndexNodeHeader hdr, int keyLen)
    {
        var node = new BranchNode { Off = off, Attr = hdr.Attribute, Left = hdr.LeftSibling, Right = hdr.RightSibling };
        int entrySize = keyLen + 8;
        for (int i = 0, pos = 12; i < hdr.KeyCount && pos + entrySize <= PageSize; i++, pos += entrySize)
        {
            node.Kids.Add(new CdxIndexBuilder.BranchChild(
                page.AsSpan(pos, keyLen).ToArray(),
                BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(pos + keyLen, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(pos + keyLen + 4, 4))));
        }
        return node;
    }

    // ---- descent / comparison --------------------------------------------------

    /// <summary>Descend from <paramref name="root"/> to the leaf that would hold <paramref name="key"/>
    /// (first child whose separator MAX-key ≥ the key). Returns the leaf offset, or -1 on a malformed tree.</summary>
    private long DescendToLeaf(long root, byte[] key, int keyLen)
    {
        long cur = root;
        var seen = new HashSet<long>();
        while (seen.Add(cur))
        {
            var hdr = ReadNodeHeader(cur);
            if (hdr is null) return -1;
            if (hdr.Value.IsLeaf) return cur;
            var kids = ReadBranchList(cur, keyLen);
            if (kids.Count == 0) return -1;
            int j = 0;
            while (j < kids.Count && CompareBytes(kids[j].SepKey, key) < 0) j++;
            if (j >= kids.Count) j = kids.Count - 1;
            cur = kids[j].Child;
        }
        return -1;
    }

    /// <summary>Choose the child that (by subtree MAX separator) covers <c>(key, recno)</c>: the first
    /// child whose (sepKey, sepRecno) ≥ (key, recno); the right-most when the entry sorts after all.</summary>
    private static int ChooseChild(List<CdxIndexBuilder.BranchChild> kids, byte[] key, uint recno)
    {
        for (int i = 0; i < kids.Count; i++)
            if (CompareEntry(key, recno, kids[i].SepKey, kids[i].SepRecno) <= 0)
                return i;
        return kids.Count - 1;
    }

    private static int CompareEntry(byte[] k1, uint r1, byte[] k2, uint r2)
    {
        int c = CompareBytes(k1, k2);
        return c != 0 ? c : r1.CompareTo(r2);
    }

    /// <summary>Unsigned, shorter-sorts-first byte comparison — the CDX key order.</summary>
    private static int CompareBytes(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    // ---- node decoding ---------------------------------------------------------

    private List<(byte[] Key, uint Recno)> ReadLeafList(long off, int keyLen, byte pad)
    {
        var raw = ReadLeafEntries(off, keyLen, pad);
        var list = new List<(byte[], uint)>(raw.Count);
        foreach (var e in raw)
            list.Add((e.Key, e.RecordNumber));
        return list;
    }

    private List<CdxIndexBuilder.BranchChild> ReadBranchList(long off, int keyLen)
    {
        var raw = ReadBranchEntries(off, keyLen);
        var list = new List<CdxIndexBuilder.BranchChild>(raw.Count);
        foreach (var e in raw)
            list.Add(new CdxIndexBuilder.BranchChild(e.Key, e.RecordNumber, e.ChildPointer));
        return list;
    }

    // ---- sibling-chain / page plumbing -----------------------------------------

    private void Unlink(uint? left, uint? right)
    {
        if (left is uint L) { var p = ReadPage(L); PatchRight(p, right ?? NoPtr); WritePage(L, p); }
        if (right is uint R) { var p = ReadPage(R); PatchLeft(p, left ?? NoPtr); WritePage(R, p); }
    }

    private static void PatchSiblings(byte[] page, uint left, uint right)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);
    }

    private static void PatchLeft(byte[] page, uint left)
        => BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);

    private static void PatchRight(byte[] page, uint right)
        => BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

    private static void PatchRoot(byte[] page, bool isRoot)
    {
        ushort attr = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(0));
        attr = (ushort)(isRoot ? attr | CdxIndexBuilder.AttrRootBit : attr & ~CdxIndexBuilder.AttrRootBit);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
    }

    private CdxHeader ReadCdxHeader(long byteOffset = 0)
    {
        if (_appendPageCache is null)
            return _reader.ReadCdxHeader(byteOffset);

        if (byteOffset < 0 || byteOffset + PageSize > _rw.Length)
            return default;

        var header = TryReadPage(byteOffset);
        if (header is null)
            return default;

        ushort forExprLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(506));
        ushort keyExprLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(510));

        int wanted = PageSize + keyExprLength + forExprLength;
        var buf = new byte[wanted];
        Buffer.BlockCopy(header, 0, buf, 0, PageSize);

        int available = (int)Math.Min(wanted, _rw.Length - byteOffset);
        int tail = available - PageSize;
        if (tail > 0 && !ReadExact(byteOffset + PageSize, buf.AsSpan(PageSize, tail)))
            return default;

        return CdxHeader.Parse(buf, _textEncoding);
    }

    private IndexNodeHeader? ReadNodeHeader(long byteOffset)
    {
        if (_appendPageCache is null)
            return _reader.ReadNodeHeader(byteOffset);

        var page = TryReadPage(byteOffset);
        return page is null ? null : IndexNodeHeader.Parse(page);
    }

    private IReadOnlyList<BranchEntry> ReadBranchEntries(long byteOffset, int keyLength)
    {
        if (_appendPageCache is null)
            return _reader.ReadBranchEntries(byteOffset, keyLength);

        var page = TryReadPage(byteOffset);
        if (page is null || keyLength <= 0)
            return Array.Empty<BranchEntry>();

        var header = IndexNodeHeader.Parse(page);
        if (header.IsLeaf)
            return Array.Empty<BranchEntry>();

        int entrySize = keyLength + 8;
        int maxEntries = (PageSize - 12) / entrySize;
        var entries = new List<BranchEntry>(Math.Min(header.KeyCount, maxEntries));

        int offset = 12;
        for (int i = 0; i < header.KeyCount; i++)
        {
            if (offset + entrySize > PageSize)
                break;

            var key = page.AsSpan(offset, keyLength).ToArray();
            uint recno = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset + keyLength, 4));
            uint child = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset + keyLength + 4, 4));

            entries.Add(new BranchEntry(key, recno, child));
            offset += entrySize;
        }

        return entries;
    }

    private IReadOnlyList<LeafEntry> ReadLeafEntries(long byteOffset, int keyLength, byte pad)
    {
        if (_appendPageCache is null)
            return _reader.ReadLeafEntries(byteOffset, keyLength, pad);

        var page = TryReadPage(byteOffset);
        if (page is null || keyLength <= 0)
            return Array.Empty<LeafEntry>();

        var header = IndexNodeHeader.Parse(page);
        if (!header.IsLeaf)
            return Array.Empty<LeafEntry>();

        return CompactLeaf.Decode(page, keyLength, pad);
    }

    private IEnumerable<(uint Recno, byte[] Key)> EnumerateCompact(uint root, int keyLength, byte pad)
    {
        if (_appendPageCache is null)
            return IndexTraversal.EnumerateCompact(_reader, root, keyLength, pad);
        return EnumerateCompactCached(root, keyLength, pad);
    }

    private IEnumerable<(uint Recno, byte[] Key)> EnumerateCompactCached(uint root, int keyLength, byte pad)
    {
        if (keyLength <= 0)
            yield break;

        long cur = root;
        var descended = new HashSet<long>();
        while (true)
        {
            if (!descended.Add(cur))
                yield break;

            var header = ReadNodeHeader(cur);
            if (header is null)
                yield break;
            if (header.Value.IsLeaf)
                break;

            var branch = ReadBranchEntries(cur, keyLength);
            if (branch.Count == 0)
                yield break;

            cur = branch[0].ChildPointer;
        }

        var visited = new HashSet<long>();
        while (true)
        {
            if (!visited.Add(cur))
                yield break;

            foreach (var e in ReadLeafEntries(cur, keyLength, pad))
                yield return (e.RecordNumber, e.Key);

            var header = ReadNodeHeader(cur);
            if (header is null)
                break;
            var right = header.Value.RightSibling;
            if (right is null)
                break;
            cur = right.Value;
        }
    }

    private byte[] ReadPage(long off)
        => TryReadPage(off) ?? throw new InvalidDataException($"CDX page at {off} is unreadable.");

    private byte[]? TryReadPage(long off)
    {
        if (_appendPageCache is null)
            return _reader.ReadPage(off);

        if (off < 0 || off + PageSize > _rw.Length)
            return null;

        if (_appendPageCache.TryGetValue(off, out var cached))
            return (byte[])cached.Clone();

        var page = _reader.ReadPage(off);
        if (page is not null)
            _appendPageCache[off] = (byte[])page.Clone();
        return page;
    }

    private void WritePage(long off, byte[] page)
    {
        _rw.Seek(off, SeekOrigin.Begin);
        _rw.Write(page, 0, PageSize);
        if (_appendPageCache is not null)
            _appendPageCache[off] = page.AsSpan(0, PageSize).ToArray();
        if (off + PageSize > _end)
            _end = off + PageSize;
    }

    private uint ReadTagRoot(long headerOffset)
    {
        var page = ReadPage(headerOffset);
        return BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0));
    }

    private bool ReadExact(long offset, Span<byte> destination)
    {
        if (destination.Length == 0)
            return true;

        _rw.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < destination.Length)
        {
            int n = _rw.Read(destination[total..]);
            if (n == 0)
                return false;
            total += n;
        }
        return true;
    }

    private static string TrimName(byte[] raw, System.Text.Encoding? encoding)
    {
        int len = raw.Length;
        while (len > 0 && (raw[len - 1] == 0x20 || raw[len - 1] == 0x00))
            len--;
        return CodePageText.Decode(raw.AsSpan(0, len), encoding);
    }
}
