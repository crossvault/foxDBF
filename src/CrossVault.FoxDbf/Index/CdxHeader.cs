using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// The 512-byte CDX file header (page 0) or a per-tag header, which share the
/// same layout (plan §C2). Pointers are byte offsets; <c>0xFFFFFFFF</c> means
/// "none". The KEY/FOR expression pool lives at <c>offset + 512</c>.
/// </summary>
public readonly struct CdxHeader
{
    /// <summary>Root node pointer (byte offset), @0 LE.</summary>
    public uint Root { get; }

    /// <summary>Free-list pointer (byte offset), @4 LE; null when <c>0xFFFFFFFF</c>.</summary>
    public uint? FreeList { get; }

    /// <summary>Multi-user version counter, @8 read BIG-endian.</summary>
    public uint Version { get; }

    /// <summary>Key length in bytes, @12 LE.</summary>
    public ushort KeyLength { get; }

    /// <summary>Raw options byte, @14.</summary>
    public byte Options { get; }

    /// <summary>Signature byte, @15.</summary>
    public byte Signature { get; }

    /// <summary>Collation name from @494 (8 ASCII bytes); "MACHINE" when all-zero/empty.</summary>
    public string SortOrder { get; }

    /// <summary>Descending flag, @502 LE (non-zero =&gt; descending).</summary>
    public bool Descending { get; }

    /// <summary>FOR-expression length, @506 LE (includes terminating NUL).</summary>
    public ushort ForExprLength { get; }

    /// <summary>KEY-expression length, @510 LE (includes terminating NUL).</summary>
    public ushort KeyExprLength { get; }

    /// <summary>The uncompiled KEY expression text from the pool at @512.</summary>
    public string KeyExpression { get; }

    /// <summary>The uncompiled FOR expression text from the pool after the KEY string.</summary>
    public string ForExpression { get; }

    public bool IsUnique => (Options & 0x01) != 0;
    public bool HasFor => (Options & 0x08) != 0;
    public bool IsCompact => (Options & 0x20) != 0;
    public bool IsCompound => (Options & 0x40) != 0;
    public bool IsStructural => (Options & 0x80) != 0;

    public CdxHeader(uint root, uint? freeList, uint version, ushort keyLength, byte options,
        byte signature, string sortOrder, bool descending, ushort forExprLength,
        ushort keyExprLength, string keyExpression, string forExpression)
    {
        Root = root;
        FreeList = freeList;
        Version = version;
        KeyLength = keyLength;
        Options = options;
        Signature = signature;
        SortOrder = sortOrder;
        Descending = descending;
        ForExprLength = forExprLength;
        KeyExprLength = keyExprLength;
        KeyExpression = keyExpression;
        ForExpression = forExpression;
    }

    /// <summary>
    /// Parses a CDX file/tag header. The span must cover the 512-byte header
    /// page plus the trailing expression pool
    /// (length &gt;= 512 + KeyExprLength + ForExprLength).
    /// </summary>
    public static CdxHeader Parse(ReadOnlySpan<byte> headerAndPool) => Parse(headerAndPool, null);

    /// <summary>
    /// As <see cref="Parse(ReadOnlySpan{byte})"/>, decoding the KEY/FOR expressions with
    /// <paramref name="textEncoding"/> — the table code page they were written in (null = library default).
    /// </summary>
    public static CdxHeader Parse(ReadOnlySpan<byte> headerAndPool, System.Text.Encoding? textEncoding)
    {
        var b = headerAndPool;

        uint root = BinaryPrimitives.ReadUInt32LittleEndian(b);
        uint freeRaw = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        uint version = BinaryPrimitives.ReadUInt32BigEndian(b[8..]);      // @8 BIG-endian
        ushort keyLength = BinaryPrimitives.ReadUInt16LittleEndian(b[12..]);
        byte options = b[14];
        byte signature = b[15];

        string sortOrder = IndexText.ReadCollation(b.Slice(494, 8));
        bool descending = BinaryPrimitives.ReadUInt16LittleEndian(b[502..]) != 0;
        ushort forExprLength = BinaryPrimitives.ReadUInt16LittleEndian(b[506..]);
        ushort keyExprLength = BinaryPrimitives.ReadUInt16LittleEndian(b[510..]);

        // Expression pool at offset 512: KEY string then FOR string.
        string keyExpression = IndexText.ReadZ(b, 512, keyExprLength, textEncoding);
        string forExpression = IndexText.ReadZ(b, 512 + keyExprLength, forExprLength, textEncoding);

        return new CdxHeader(
            root,
            freeRaw == IndexNodeHeader.NoPointer ? null : freeRaw,
            version,
            keyLength,
            options,
            signature,
            sortOrder,
            descending,
            forExprLength,
            keyExprLength,
            keyExpression,
            forExpression);
    }
}
