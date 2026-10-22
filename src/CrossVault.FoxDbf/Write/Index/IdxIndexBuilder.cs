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
/// sorts the (key, recno) pairs by unsigned key bytes (recno tiebreak), bulk-loads a balanced B-tree
/// into 512-byte pages, and writes the file.
/// </summary>
/// <remarks>
/// GUARANTEES (what the oracle pins):
/// <list type="bullet">
/// <item>SINGLE-LEAF (fits one 512-byte node): byte-for-byte identical to VFP9 in every DETERMINISTIC
/// region — the header (@0 root, @4 free=−1, @8 eof, @12 key length, @14 options, @15 signature 0x01,
/// @16 KEY text, @236 FOR text) and every used leaf entry. VFP leaves the node SLACK filled with
/// uninitialised heap memory (non-deterministic even run-to-run); this builder zero-fills it — the reader
/// ignores the slack, so behaviour is identical.</item>
/// <item>MULTI-LEAF (needs branch nodes) and non-character (INTEGER/NUMERIC/DATE) keys: a VALID,
/// VFP9-READABLE B-tree — VFP9 opens it and SEEK/SCANs it in the correct order (oracle-verified). It is
/// NOT byte-for-byte identical to VFP9's own multi-leaf file: this builder's greedy leaf packing +
/// append-branches-last page layout differs from VFP9's balanced-leaf / root-after-header layout (same
/// total size, different page order and per-leaf counts). Full physical parity for the general case is
/// out of scope; the writer→<see cref="IdxFile"/>-reader round-trip and VFP9-readability are what hold.</item>
/// </list>
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
        };

        var (keyType, keyLen, _) = CdxIndexBuilder.ResolveKey(schema, keyExpr);
        Encoding characterEncoding = ResolveCharacterEncoding(schema, keyExpr);

        var keyFn = VfpExpression.Parse(keyExpr).Compile(ctx);
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
            var value = keyFn(rc);
            entries.Add(new Entry(EncodeKey(value, keyType, keyLen, characterEncoding), (uint)row.RecNo));
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
    {
        string field = (keyExpr ?? string.Empty).Trim();
        foreach (var col in schema.Columns)
            if (string.Equals(col.Name, field, StringComparison.OrdinalIgnoreCase))
                return col.IsBinary ? Encoding.Latin1 : schema.Encoding;
        return schema.Encoding;
    }

    // ---- B-tree bulk loader ----------------------------------------------------

    private static void WriteFile(string idxPath, List<Entry> entries, int keyLen, byte options,
        byte[] keyText, byte[] forText)
    {
        int entrySize = keyLen + 4;
        int perLeaf = Math.Max(1, (Page - StdHeader) / entrySize);

        // Slice the sorted entries into leaves.
        var leafSlices = new List<List<Entry>>();
        if (entries.Count == 0)
            leafSlices.Add(new List<Entry>());
        else
            for (int i = 0; i < entries.Count; i += perLeaf)
                leafSlices.Add(entries.GetRange(i, Math.Min(perLeaf, entries.Count - i)));

        var sink = new PageSink();
        sink.Reserve();                                   // page 0: header (filled last)

        // Reserve every leaf page first so siblings can reference each other.
        var leafIndex = new int[leafSlices.Count];
        for (int i = 0; i < leafSlices.Count; i++) leafIndex[i] = sink.Reserve();

        bool singleLeaf = leafSlices.Count == 1;
        for (int i = 0; i < leafSlices.Count; i++)
        {
            uint left = i > 0 ? (uint)(leafIndex[i - 1] * Page) : NoPtr;
            uint right = i < leafSlices.Count - 1 ? (uint)(leafIndex[i + 1] * Page) : NoPtr;
            ushort attr = (ushort)(AttrLeaf | (singleLeaf ? AttrRoot : 0));
            WriteLeaf(sink.At(leafIndex[i]), leafSlices[i], keyLen, attr, left, right);
        }

        uint root;
        if (singleLeaf)
        {
            root = (uint)(leafIndex[0] * Page);
        }
        else
        {
            // Build interior levels: one entry per child (separator = child's LAST key), until one root.
            var level = new List<ChildRef>(leafSlices.Count);
            for (int i = 0; i < leafSlices.Count; i++)
                level.Add(new ChildRef((uint)(leafIndex[i] * Page), leafSlices[i][^1].Key));
            while (level.Count > 1)
                level = BuildBranchLevel(sink, level, keyLen);
            root = level[0].Offset;
        }

        long eof = (long)sink.PageCount * Page;
        WriteHeader(sink.At(0), root, (ushort)keyLen, options, keyText, forText, (uint)eof);
        File.WriteAllBytes(idxPath, sink.ToArray());
    }

    private static List<ChildRef> BuildBranchLevel(PageSink sink, List<ChildRef> children, int keyLen)
    {
        int entrySize = keyLen + 4;
        int maxPer = Math.Max(1, (Page - StdHeader) / entrySize);

        var groups = new List<List<ChildRef>>();
        for (int i = 0; i < children.Count; i += maxPer)
            groups.Add(children.GetRange(i, Math.Min(maxPer, children.Count - i)));

        // Reserve the branch pages first (sibling links).
        var idx = new int[groups.Count];
        for (int i = 0; i < groups.Count; i++) idx[i] = sink.Reserve();

        bool isRoot = groups.Count == 1;
        var result = new List<ChildRef>(groups.Count);
        for (int n = 0; n < groups.Count; n++)
        {
            uint left = n > 0 ? (uint)(idx[n - 1] * Page) : NoPtr;
            uint right = n < groups.Count - 1 ? (uint)(idx[n + 1] * Page) : NoPtr;
            WriteBranch(sink.At(idx[n]), groups[n], keyLen, (ushort)(isRoot ? AttrRoot : 0), left, right);
            result.Add(new ChildRef((uint)(idx[n] * Page), groups[n][^1].Key));
        }
        return result;
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

    private sealed class PageSink
    {
        private readonly List<byte[]> _pages = new();
        public int PageCount => _pages.Count;
        public int Reserve() { _pages.Add(new byte[Page]); return _pages.Count - 1; }
        public byte[] At(int index) => _pages[index];
        public byte[] ToArray()
        {
            var buf = new byte[_pages.Count * Page];
            for (int i = 0; i < _pages.Count; i++) Array.Copy(_pages[i], 0, buf, i * Page, Page);
            return buf;
        }
    }
}
