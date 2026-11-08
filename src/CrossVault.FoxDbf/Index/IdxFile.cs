using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// A read-only standalone single-tag index (<c>.idx</c>) — plan §C7 phase C-1.
/// Unlike a compact CDX leaf, an IDX node is UNCOMPRESSED: every entry is
/// <c>[key(KeyLength)] [pointer(4, BIG-endian)]</c>, where the pointer is a DBF
/// record number in a leaf node or a child node byte offset in an interior node.
/// The 12-byte node header is <c>attribute u16 LE @0</c> (a leaf when
/// <c>attribute &gt; 1</c>), <c>key count u16 LE @2</c>, <c>left sibling u32 LE @4</c>,
/// <c>right sibling u32 LE @8</c> (<c>0xFFFFFFFF</c> = none).
/// <see cref="EnumerateEntries"/> yields the index entries in key order.
/// <para>
/// A COMPACT <c>.idx</c> (<see cref="IdxHeader.IsCompact"/>) is instead a single compact tree in the format of a
/// CDX tag; it is read through <see cref="CompactTag"/>.
/// </para>
/// </summary>
/// <remarks>
/// Never throws on a malformed file — yields what is readable and terminates on a
/// corrupt sibling cycle via a visited-set guard.
/// </remarks>
public sealed class IdxFile : IDisposable
{
    private const int Page = IndexFile.PageSize;
    private const uint NoPointer = IndexNodeHeader.NoPointer;

    private readonly IndexFile _index;
    private readonly DbfTable? _table;
    private CdxTag? _compactTag;

    private IdxFile(IndexFile index, DbfTable? table = null)
    {
        _index = index;
        _table = table;
    }

    /// <summary>Opens a legacy single index from an <c>.idx</c> path.</summary>
    public static IdxFile Open(string path) => new(IndexFile.Open(path));

    /// <summary>
    /// Opens a legacy single index from an <c>.idx</c> path, decoding its KEY/FOR expressions with
    /// <paramref name="textEncoding"/> — the code page of the table it indexes (null = library default).
    /// </summary>
    public static IdxFile Open(string path, System.Text.Encoding? textEncoding)
    {
        var index = IndexFile.Open(path);
        index.TextEncoding = textEncoding;
        return new(index);
    }

    /// <summary>
    /// Opens a standalone index from an <c>.idx</c> path for <paramref name="table"/>: its code page decodes the
    /// KEY/FOR expressions, and its columns give a compact <c>.idx</c> its key type and pad.
    /// </summary>
    public static IdxFile Open(string path, DbfTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var index = IndexFile.Open(path);
        index.TextEncoding = table.Encoding;
        return new(index, table);
    }

    /// <summary>Opens a legacy single index over an existing seekable stream.</summary>
    public static IdxFile Open(Stream stream, bool leaveOpen = false)
        => new(IndexFile.Open(stream, leaveOpen));

    /// <summary>The underlying page reader.</summary>
    public IndexFile Index => _index;

    /// <summary>The parsed 512-byte IDX header (offset 0).</summary>
    public IdxHeader Header => _index.ReadIdxHeader();

    /// <summary>The key length declared in the header.</summary>
    public int KeyLength => Header.KeyLength;

    /// <summary>
    /// The tree of a COMPACT <c>.idx</c> as a <see cref="CdxTag"/> (named after nothing — an <c>.idx</c> has no
    /// tag name), with its collation, key type and SEEK; null for an uncompressed <c>.idx</c> or an unreadable
    /// header.
    /// </summary>
    internal CdxTag? CompactTag
    {
        get
        {
            if (_compactTag is not null)
                return _compactTag;
            var page = _index.ReadPage(0);
            if (page is null || (page[14] & 0x20) == 0)
                return null;
            var hdr = _index.ReadCdxHeader(0);
            if (hdr.KeyExpression is null)
                return null;   // truncated expression pool (fail closed, as CdxFile does)
            return _compactTag = CdxFile.CreateTag(_index, _table, string.Empty, hdr);
        }
    }

    /// <summary>
    /// Enumerates the index entries in key order: descend the interior nodes to the
    /// left-most leaf (first entry's BIG-endian child pointer), then walk the leaf
    /// right-sibling chain reading each <c>[key][recno(BE)]</c> entry. A compact <c>.idx</c>
    /// enumerates through <see cref="CompactTag"/>.
    /// </summary>
    public IEnumerable<IndexEntry> EnumerateEntries()
    {
        var header = Header;
        if (header.IsCompact)
            return CompactTag?.EnumerateEntries() ?? Enumerable.Empty<IndexEntry>();
        return EnumerateUncompressed(header);
    }

    private IEnumerable<IndexEntry> EnumerateUncompressed(IdxHeader header)
    {
        int keyLength = header.KeyLength;
        if (keyLength <= 0)
            yield break;

        int entrySize = keyLength + 4;

        // --- 1) Descend to the left-most leaf. ---
        long cur = header.Root;
        var descended = new HashSet<long>();
        while (true)
        {
            if (!descended.Add(cur))
                yield break;

            var page = _index.ReadPage(cur);
            if (page is null)
                yield break;

            ushort attribute = BinaryPrimitives.ReadUInt16LittleEndian(page);
            ushort keyCount = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(2));
            bool isLeaf = attribute > 1;
            if (isLeaf)
                break;

            // Interior node: follow the first entry's child pointer (BIG-endian).
            if (keyCount == 0)
                yield break;
            int childOffset = 12 + keyLength;
            if (childOffset + 4 > Page)
                yield break;
            cur = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(childOffset, 4));
        }

        // --- 2) Walk the leaf right-sibling chain. ---
        var visited = new HashSet<long>();
        while (true)
        {
            if (!visited.Add(cur))
                yield break;

            var page = _index.ReadPage(cur);
            if (page is null)
                yield break;

            ushort keyCount = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(2));
            uint right = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(8));

            int offset = 12;
            for (int i = 0; i < keyCount; i++)
            {
                if (offset + entrySize > Page)
                    break; // truncated / malformed node

                var key = page.AsSpan(offset, keyLength).ToArray();
                uint recno = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset + keyLength, 4));
                yield return new IndexEntry(recno, key);
                offset += entrySize;
            }

            if (right == NoPointer)
                yield break;
            cur = right;
        }
    }

    public void Dispose() => _index.Dispose();
}
