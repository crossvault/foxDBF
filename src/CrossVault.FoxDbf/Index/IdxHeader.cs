using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// The header of a standalone single <c>.idx</c> file (plan §C2 note). An UNCOMPRESSED <c>.idx</c> has a 512-byte
/// header with the expression text inline: Root@0, FreeList@4, EOF@8, KeyLength@12, Options@14, KEY expr
/// @16..235, FOR expr @236..455. A COMPACT one (<see cref="IsCompact"/>, options bit 0x20) has the header of a CDX
/// tag instead (<see cref="CdxHeader"/>): the collation at @494 and the KEY/FOR text in the pool page at @512.
/// Pointers are byte offsets; <c>0xFFFFFFFF</c> = none.
/// </summary>
public readonly struct IdxHeader
{
    /// <summary>Root node pointer (byte offset), @0 LE.</summary>
    public uint Root { get; }

    /// <summary>Free-list pointer, @4 LE; null when <c>0xFFFFFFFF</c>.</summary>
    public uint? FreeList { get; }

    /// <summary>End-of-file / next-available byte offset, @8 LE, of an uncompressed <c>.idx</c>; 0 for a compact one
    /// (that slot holds the multi-user version counter there).</summary>
    public uint Eof { get; }

    /// <summary>Key length in bytes, @12 LE.</summary>
    public ushort KeyLength { get; }

    /// <summary>Raw options byte, @14.</summary>
    public byte Options { get; }

    /// <summary>KEY expression text, @16..235 (NUL-terminated ASCII).</summary>
    public string KeyExpression { get; }

    /// <summary>FOR expression text, @236..455 (NUL-terminated ASCII).</summary>
    public string ForExpression { get; }

    /// <summary>The collation the keys were built under: from @494 of a compact <c>.idx</c>; an uncompressed one is
    /// always MACHINE.</summary>
    public string Collation => _collation ?? "MACHINE";

    private readonly string? _collation;

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
        _collation = null;
    }

    private IdxHeader(CdxHeader compact)
        : this(compact.Root, compact.FreeList, 0, compact.KeyLength, compact.Options,
            compact.KeyExpression ?? string.Empty, compact.ForExpression ?? string.Empty)
        => _collation = compact.SortOrder;

    /// <summary>The <see cref="IdxHeader"/> of a compact <c>.idx</c> whose CDX-tag header is <paramref name="compact"/>.</summary>
    internal static IdxHeader FromCompact(CdxHeader compact) => new(compact);

    /// <summary>Parses the IDX header from the start of a page span: the 512-byte header page, plus the expression
    /// pool page behind it for a compact <c>.idx</c> (its KEY/FOR text reads empty when the span stops at 512).</summary>
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
        if ((options & 0x20) != 0)
            return FromCompact(CdxHeader.Parse(page, textEncoding));

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
