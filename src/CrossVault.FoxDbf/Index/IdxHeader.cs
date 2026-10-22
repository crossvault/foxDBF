using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// The 512-byte header of a legacy single <c>.idx</c> file (plan §C2 note).
/// Unlike the compact CDX header the expression text is stored inline:
/// Root@0, FreeList@4, EOF@8, KeyLength@12, Options@14, KEY expr @16..235,
/// FOR expr @236..455. Pointers are byte offsets; <c>0xFFFFFFFF</c> = none.
/// </summary>
public readonly struct IdxHeader
{
    /// <summary>Root node pointer (byte offset), @0 LE.</summary>
    public uint Root { get; }

    /// <summary>Free-list pointer, @4 LE; null when <c>0xFFFFFFFF</c>.</summary>
    public uint? FreeList { get; }

    /// <summary>End-of-file / next-available byte offset, @8 LE.</summary>
    public uint Eof { get; }

    /// <summary>Key length in bytes, @12 LE.</summary>
    public ushort KeyLength { get; }

    /// <summary>Raw options byte, @14.</summary>
    public byte Options { get; }

    /// <summary>KEY expression text, @16..235 (NUL-terminated ASCII).</summary>
    public string KeyExpression { get; }

    /// <summary>FOR expression text, @236..455 (NUL-terminated ASCII).</summary>
    public string ForExpression { get; }

    public bool IsUnique => (Options & 0x01) != 0;
    public bool HasFor => (Options & 0x08) != 0;
    public bool IsCompact => (Options & 0x20) != 0;

    public IdxHeader(uint root, uint? freeList, uint eof, ushort keyLength, byte options,
        string keyExpression, string forExpression)
    {
        Root = root;
        FreeList = freeList;
        Eof = eof;
        KeyLength = keyLength;
        Options = options;
        KeyExpression = keyExpression;
        ForExpression = forExpression;
    }

    /// <summary>Parses the 512-byte legacy IDX header from the start of a page span.</summary>
    public static IdxHeader Parse(ReadOnlySpan<byte> page) => Parse(page, null);

    /// <summary>
    /// As <see cref="Parse(ReadOnlySpan{byte})"/>, decoding the KEY/FOR expressions with
    /// <paramref name="textEncoding"/> — the table code page they were written in (null = library default).
    /// </summary>
    public static IdxHeader Parse(ReadOnlySpan<byte> page, System.Text.Encoding? textEncoding)
    {
        uint root = BinaryPrimitives.ReadUInt32LittleEndian(page);
        uint freeRaw = BinaryPrimitives.ReadUInt32LittleEndian(page[4..]);
        uint eof = BinaryPrimitives.ReadUInt32LittleEndian(page[8..]);
        ushort keyLength = BinaryPrimitives.ReadUInt16LittleEndian(page[12..]);
        byte options = page[14];

        string keyExpression = IndexText.ReadZ(page, 16, 220, textEncoding);   // @16..235
        string forExpression = IndexText.ReadZ(page, 236, 220, textEncoding);  // @236..455

        return new IdxHeader(
            root,
            freeRaw == IndexNodeHeader.NoPointer ? null : freeRaw,
            eof,
            keyLength,
            options,
            keyExpression,
            forExpression);
    }
}
