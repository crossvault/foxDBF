using System.Buffers.Binary;
using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §C7 — BULK build of a legacy standalone single-tag index (<c>.idx</c>) — the WRITE counterpart to
/// <see cref="CrossVault.FoxDbf.Index.IdxFile"/>. Unlike the compact CDX, a legacy IDX node is
/// UNCOMPRESSED: each entry is <c>[key(KeyLength)] [pointer(4, BIG-endian)]</c> and the 512-byte
/// header stores the KEY / FOR expression text inline. Computes every key via the expression engine,
/// sorts the (key, recno) pairs by unsigned key bytes (recno tiebreak), lays them out in the B-tree VFP9's
/// <c>INDEX ON … TO</c> builds (see <see cref="Layout"/>) and writes the file.
/// </summary>
/// <remarks>
/// GUARANTEE (what the oracles pin): byte-for-byte identical to VFP9 in every DETERMINISTIC region, single leaf
/// or many levels — the header page (@0 root, @4 free=−1, @8 eof, @12 key length, @14 options, @15 signature
/// 0x01, @16 KEY text, @236 FOR text) and, on every page, the node header (attribute, key count, sibling links)
/// and every used entry (leaf: key + recno, branch: key + child offset). VFP leaves the node SLACK filled with
/// uninitialised heap memory (non-deterministic even run-to-run); this builder zero-fills it — the reader
/// ignores the slack, so behaviour is identical. A COMPACT <c>.idx</c> is written by
/// <see cref="CdxIndexBuilder.BuildCompactIdx"/>.
/// </remarks>
internal static class IdxIndexBuilder
{
    private const int Page = IndexFile.PageSize;       // 512
    private const int StdHeader = 12;                  // node std header (attr/keys/left/right)
    private const uint NoPtr = 0xFFFFFFFF;

    // Node attribute bits mirror the compact tree (0=index/branch, 1=root, 2=leaf).
    private const ushort AttrRoot = 0x01;
    private const ushort AttrLeaf = 0x02;

    /// <summary>
    /// Build a fresh standalone <c>.idx</c> at <paramref name="idxPath"/> over <paramref name="rows"/>
    /// (the live record set of <paramref name="schema"/>) for KEY <paramref name="keyExpr"/> with an
    /// optional <paramref name="forExpr"/> filter. <paramref name="unique"/> keeps one entry (lowest
    /// recno) per distinct key; <paramref name="includeDeleted"/> mirrors SET DELETED OFF. The KEY/FOR
    /// expressions ride the ambient <paramref name="evalContext"/> (SET EXACT / SET ANSI / culture),
    /// MACHINE-collated (legacy IDX is raw code-page bytes). Overwrites any existing file at the path.
    /// </summary>
    public static void Build(string idxPath, DbfTable schema, IReadOnlyList<CdxIndexBuilder.BuildRow> rows,
        string keyExpr, string? forExpr, bool unique,
        EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        // VFP stores the DECOMPILED text (`MOD(RECNO(), 3) = 0` → `MOD(RECNO(),3)=0`); the keys come from that
        // stored text too, as anything re-reading the index evaluates it.
        keyExpr = VfpExpressionText.Canonicalize(keyExpr);
        if (!string.IsNullOrWhiteSpace(forExpr)) forExpr = VfpExpressionText.Canonicalize(forExpr);

        // KEY/FOR text lives in the TABLE's code page (as VFP writes it) — never substituted, never clipped.
        byte[] keyText = EncodeExpression(keyExpr, schema.Encoding, "KEY");
        byte[] forText = EncodeExpression(forExpr ?? string.Empty, schema.Encoding, "FOR");

        var ctx = new EvaluationContext
        {
            Collation = VfpCollations.Machine,
            Exact = evalContext?.Exact ?? false,
            Ansi = evalContext?.Ansi ?? false,
            Culture = evalContext?.Culture,
            Encoding = schema.Encoding,
            SessionCodePage = evalContext?.SessionCodePage ?? schema.IndexSessionCodePage ?? schema.Encoding.CodePage,
        };

        var (keyType, keyLen, _) = CdxIndexBuilder.ResolveKey(schema, keyExpr);
        Encoding characterEncoding = ResolveCharacterEncoding(schema, keyExpr);

        var keyFn = VfpExpression.Parse(keyExpr).Compile(ctx);
        var dateField = IndexKey.ResolveDateField(schema, keyExpr);
        Func<IRowContext, VfpValue>? forFn = !string.IsNullOrWhiteSpace(forExpr)
            ? VfpExpression.Parse(forExpr!).Compile(ctx)
            : null;

        int recCount = rows.Count;
        var entries = new List<Entry>(rows.Count);
        foreach (var row in rows)
        {
            if (!includeDeleted && row.Record.IsDeleted)
                continue;
            var rc = new CdxIndexBuilder.RowContext(row.Record, row.RecNo, recCount);
            if (forFn is not null)
            {
                var f = forFn(rc);
                if (!(f.Type == VfpType.Logical && f.AsLogical))
                    continue;
            }
            byte[] key = dateField is not null
                ? IndexKey.EncodeDateField(row.Record, dateField)
                : EncodeKey(keyFn(rc), keyType, keyLen, characterEncoding);
            entries.Add(new Entry(key, (uint)row.RecNo));
        }

        entries.Sort(EntryComparer.Instance);
        if (unique)
            entries = DropDuplicateKeys(entries);

        byte options = 0x00;
        if (unique) options |= 0x01;
        if (forFn is not null) options |= 0x08;

        WriteFile(idxPath, entries, keyLen, options, keyText, forText);
    }

    // ---- key encoding (legacy IDX == raw MACHINE bytes) ------------------------

    private static byte[] EncodeKey(VfpValue value, IndexKeyType type, int keyLen, Encoding characterEncoding)
    {
        if (type == IndexKeyType.Character)
        {
            var buf = new byte[keyLen];
            for (int i = 0; i < keyLen; i++) buf[i] = 0x20;
            if (!value.IsNull)
            {
                byte[] raw = characterEncoding.GetBytes(value.AsString);
                raw.AsSpan(0, Math.Min(raw.Length, keyLen)).CopyTo(buf);
            }
            return buf;
        }

        object? clr = type switch
        {
            IndexKeyType.Integer => value.IsNull ? null : (object)value.AsInteger,
            IndexKeyType.Numeric => value.IsNull ? null : (object)value.AsDouble,
            IndexKeyType.Date => value.IsNull ? null : (object)value.AsDate,
            IndexKeyType.DateTime => value.IsNull ? null : (object)value.AsDateTime,
            _ => null,
        };
        byte[]? encoded = clr is null ? null : IndexKey.Encode(clr, type);
        var key = new byte[keyLen];
        if (encoded is not null)
            Array.Copy(encoded, key, Math.Min(encoded.Length, keyLen));
        return key;
    }

    /// <summary>Resolve the byte encoding a standalone character IDX stores and compares. A direct
    /// binary/NOCPTRANS field is byte-identity Latin-1; a normal direct field and every character
    /// expression use the table encoding.</summary>
    internal static Encoding ResolveCharacterEncoding(DbfTable schema, string keyExpr)
        => IndexKey.ResolveCharacterEncoding(schema, keyExpr);

    // ---- B-tree bulk loader ----------------------------------------------------

    private static void WriteFile(string idxPath, List<Entry> entries, int keyLen, byte options,
        byte[] keyText, byte[] forText)
    {
        var layout = Layout(entries.Count, keyLen);
        var pages = new byte[layout.PageCount][];
        for (int i = 0; i < pages.Length; i++) pages[i] = new byte[Page];

        // Leaves: the sorted entries in VFP's per-leaf counts, each on VFP's page.
        var leaves = layout.Levels[0];
        bool singleLeaf = leaves.Count == 1;
        var level = new List<ChildRef>(leaves.Count);
        int start = 0;
        for (int i = 0; i < leaves.Count; i++)
        {
            var items = entries.GetRange(start, leaves[i].Count);
            start += leaves[i].Count;
            ushort attr = (ushort)(AttrLeaf | (singleLeaf ? AttrRoot : 0));
            WriteLeaf(pages[leaves[i].Page], items, keyLen, attr, SiblingOffset(leaves, i - 1), SiblingOffset(leaves, i + 1));
            // An empty index is a single empty root leaf; it is never referenced by a branch.
            level.Add(new ChildRef((uint)(leaves[i].Page * Page), items.Count > 0 ? items[^1].Key : Array.Empty<byte>()));
        }

        // Branch levels: one entry per child (separator = the child's LAST key), grouped as VFP groups them.
        for (int h = 1; h < layout.Levels.Count; h++)
            level = BuildBranchLevel(pages, level, keyLen, layout.Levels[h], isRoot: h == layout.Levels.Count - 1);

        uint root = level[0].Offset;
        WriteHeader(pages[0], root, (ushort)keyLen, options, keyText, forText, (uint)(pages.Length * Page));
        var buf = new byte[pages.Length * Page];
        for (int i = 0; i < pages.Length; i++) pages[i].CopyTo(buf, i * Page);
        File.WriteAllBytes(idxPath, buf);
    }

    private static uint SiblingOffset(List<Node> nodes, int i)
        => i >= 0 && i < nodes.Count ? (uint)(nodes[i].Page * Page) : NoPtr;

    private static List<ChildRef> BuildBranchLevel(byte[][] pages, List<ChildRef> children, int keyLen,
        List<Node> nodes, bool isRoot)
    {
        var result = new List<ChildRef>(nodes.Count);
        int start = 0;
        for (int n = 0; n < nodes.Count; n++)
        {
            var kids = children.GetRange(start, nodes[n].Count);
            start += nodes[n].Count;
            WriteBranch(pages[nodes[n].Page], kids, keyLen, (ushort)(isRoot ? AttrRoot : 0),
                SiblingOffset(nodes, n - 1), SiblingOffset(nodes, n + 1));
            result.Add(new ChildRef((uint)(nodes[n].Page * Page), kids[^1].Key));
        }
        if (start != children.Count)
            throw new InvalidOperationException($"IDX branch layout covers {start} of {children.Count} children.");
        return result;
    }

    /// <summary>A node of the tree: its page number and its entry (leaf) or child (branch) count.</summary>
    internal readonly record struct Node(int Page, int Count);

    /// <summary>
    /// The tree VFP9's <c>INDEX ON … TO x.idx</c> builds over <paramref name="entryCount"/> sorted entries:
    /// every node with its page and entry count, level by level from the leaves (level 0) up to the root, and
    /// the file's page count (header page included).
    /// <para>
    /// VFP9 grows the tree as if the sorted keys were inserted one by one. The root stays on page 1 (right after
    /// the header); a root split takes the next two pages (the right half on the lower one), every other new
    /// node the next page, in the order nodes appear (a leaf first, then the cascade bottom-up). Every node,
    /// leaf or branch, holds up to 500 / (key length + 4) entries. When a full node gets one more entry, a new
    /// node opens to its right with just that entry — except when the full node sits on an ODD page and an
    /// entry is shorter than 50 bytes (key length &lt;= 45): then it also gives up its last entry (it keeps
    /// capacity-1, the new node starts with 2). The same rule as the CDX branch levels
    /// (<see cref="CdxIndexBuilder"/>), with the 4-byte entries of the uncompressed format, and applied to the
    /// leaves as well (key length 45: leaves of 9/10/10/9/10/9…). Derived from VFP9 over 335 trees (key
    /// lengths 1-96, up to five levels): every node's count, every page number and the file size reproduce.
    /// </para>
    /// </summary>
    internal static (List<List<Node>> Levels, int PageCount) Layout(int entryCount, int keyLen)
    {
        const int rootPage = 1;
        int entrySize = keyLen + 4;
        int cap = Math.Max(2, (Page - StdHeader) / entrySize);
        bool givesUpLast = entrySize < 50;
        int next = rootPage + 1;
        var levels = new List<List<Node>> { new() { new Node(rootPage, 0) } };

        int remaining = entryCount;
        while (remaining > 0)
        {
            var leaves = levels[0];
            int room = cap - leaves[^1].Count;
            if (room > 0)
            {
                // Fill the rightmost leaf in one step: nothing splits until it is full.
                int take = Math.Min(room, remaining);
                leaves[^1] = leaves[^1] with { Count = leaves[^1].Count + take };
                remaining -= take;
                continue;
            }

            // One more entry for a full leaf: split, and cascade up while the parent is full as well.
            remaining--;
            for (int h = 0; ; h++)
            {
                var level = levels[h];
                var node = level[^1];
                if (node.Count < cap)
                {
                    level[^1] = node with { Count = node.Count + 1 };
                    break;
                }

                int keep = givesUpLast && (node.Page & 1) == 1 ? cap - 1 : cap;
                if (h == levels.Count - 1)
                {
                    // Root split: its entries go to two new nodes, the root (same page) gets a level above them.
                    int right = next++;
                    int left = next++;
                    level[^1] = new Node(left, keep);
                    level.Add(new Node(right, cap + 1 - keep));
                    levels.Add(new List<Node> { new(rootPage, 2) });
                    break;
                }

                level[^1] = node with { Count = keep };
                level.Add(new Node(next++, cap + 1 - keep));
            }
        }
        return (levels, next);
    }

    private static void WriteLeaf(byte[] page, List<Entry> items, int keyLen, ushort attr, uint left, uint right)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), (ushort)items.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

        int off = StdHeader;
        foreach (var e in items)
        {
            e.Key.AsSpan(0, keyLen).CopyTo(page.AsSpan(off, keyLen));
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen, 4), e.Recno);   // recno BIG-endian
            off += keyLen + 4;
        }
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
            int copy = Math.Min(keyLen, k.Key.Length);
            k.Key.AsSpan(0, copy).CopyTo(page.AsSpan(off, keyLen));
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen, 4), k.Offset);   // child ptr BIG-endian
            off += keyLen + 4;
        }
    }

    private static void WriteHeader(byte[] page, uint root, ushort keyLen, byte options,
        byte[] keyExpr, byte[] forExpr, uint eof)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0), root);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), NoPtr);   // free list = none
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), eof);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), keyLen);
        page[14] = options;
        page[15] = 0x01;                                                   // signature (verified vs VFP9)

        keyExpr.CopyTo(page, 16);                                         // @16..235 (NUL already zero)
        forExpr.CopyTo(page, 236);                                        // @236..455
    }

    /// <summary>Encode an IDX KEY/FOR expression in the table code page; it must fit its 220-byte slot with a NUL.</summary>
    private static byte[] EncodeExpression(string text, Encoding encoding, string what)
    {
        byte[] bytes = CodePageText.Encode(text, encoding, $"IDX {what} expression", m => new DbfWriteException(m));
        if (bytes.Length > 219)
            throw new DbfWriteException($"IDX {what} expression needs {bytes.Length} bytes; the header slot holds 219.");
        return bytes;
    }

    // ---- helpers ---------------------------------------------------------------

    private static List<Entry> DropDuplicateKeys(List<Entry> sorted)
    {
        var result = new List<Entry>(sorted.Count);
        byte[]? prev = null;
        foreach (var e in sorted)
        {
            if (prev is not null && prev.AsSpan().SequenceEqual(e.Key)) continue;
            result.Add(e);
            prev = e.Key;
        }
        return result;
    }

    private readonly record struct Entry(byte[] Key, uint Recno);
    private readonly record struct ChildRef(uint Offset, byte[] Key);

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
}
