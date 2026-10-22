using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// A read-only page reader for a Visual FoxPro compound index (<c>.cdx</c>) or a
/// legacy single index (<c>.idx</c>). Indexes are a sequence of fixed
/// <see cref="PageSize"/>-byte pages addressed by absolute byte offset
/// (plan §C2/§C3). The reader is deliberately defensive: out-of-range or
/// malformed reads return an empty/none result rather than throwing.
/// </summary>
public sealed class IndexFile : IDisposable
{
    /// <summary>The fixed page size of every CDX/IDX node, in bytes.</summary>
    public const int PageSize = 512;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly string? _sourcePath;

    private IndexFile(Stream stream, bool leaveOpen, string? sourcePath = null)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _sourcePath = sourcePath;
    }

    /// <summary>
    /// The on-disk path this index was opened from, or <see langword="null"/> when it was opened over
    /// a caller-supplied stream. Mirrors <see cref="DbfTable.SourcePath"/>: a read-only identity hint
    /// (used by the Highlike warm cache to fold the <c>.cdx</c> into its change-token); never a handle.
    /// </summary>
    public string? SourcePath => _sourcePath;

    /// <summary>
    /// The code page tag names and KEY/FOR expressions are decoded with — the owning TABLE's encoding, since
    /// VFP stores that text in the table code page (RAZÓN = 0xD3 in CP1252, 0xE0 in CP850). Null = the
    /// library default code page (no table known).
    /// </summary>
    internal System.Text.Encoding? TextEncoding { get; set; }

    /// <summary>Opens an index file from a path (.cdx or .idx).</summary>
    public static IndexFile Open(string path)
        => Open(path, DbfReadBackend.FileStream, DbfDriveKind.Auto);

    /// <summary>
    /// Opens an index file from a path with an explicit read <paramref name="backend"/> (Highlike
    /// Phase B-3). When mapping is requested AND the file is on LOCAL/fixed storage the page reads are
    /// served from a mapped view; a network/UNC path or any mapping failure falls back to
    /// <see cref="FileStream"/> (page reads stay byte-identical either way).
    /// </summary>
    public static IndexFile Open(string path, DbfReadBackend backend, DbfDriveKind driveKind)
    {
        // Opt-in §B-3: try the local-only read-only memory map; null → classic FileStream (unchanged).
        Stream stream = ReadBackendResolver.TryOpenMapped(path, backend, driveKind)
            ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new IndexFile(stream, leaveOpen: false, sourcePath: path);
    }

    /// <summary>
    /// True when this index's page reads are served from a memory-mapped view rather than per-read
    /// <see cref="FileStream"/> syscalls (Highlike Phase B-3); <see langword="false"/> on a network/UNC
    /// path, a mapping failure, or a stream-only open (the safe <see cref="FileStream"/> path).
    /// </summary>
    public bool IsMemoryMapped => _stream is MemoryMappedReadStream;

    /// <summary>Opens an index over an existing seekable stream.</summary>
    public static IndexFile Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new IndexFile(stream, leaveOpen);
    }

    /// <summary>Total length of the underlying index file in bytes.</summary>
    public long Length => _stream.Length;

    /// <summary>
    /// Reads and parses a CDX file header (offset 0) or a per-tag header at the
    /// given byte offset, including its trailing KEY/FOR expression pool.
    /// </summary>
    public CdxHeader ReadCdxHeader(long byteOffset = 0)
    {
        if (byteOffset < 0 || byteOffset + PageSize > Length)
            return default;

        // First read just the 512-byte header to learn the expression lengths,
        // then read the header plus its trailing pool in one go.
        var header = new byte[PageSize];
        if (!ReadExact(byteOffset, header, PageSize))
            return default;

        ushort forExprLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(506));
        ushort keyExprLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(510));

        int wanted = PageSize + keyExprLength + forExprLength;

        // FAIL CLOSED (§6.6): a legit CDX always stores its FULL KEY/FOR expression pool inline right after
        // the header page. If the declared pool extends past EOF the tag is truncated/corrupt — clamping and
        // parsing the zero-filled remainder would mis-read it as a VALID but silently SHORTENED expression
        // (a real risk on a partially-written / damaged sidecar). Treat the tag as unusable instead, matching
        // this reader's own doctrine of never silently parsing zero-filled fields.
        if (byteOffset + (long)wanted > Length)
            return default;

        var buf = new byte[wanted];

        // Reuse the already-validated 512-byte header; only read the trailing expression pool. A failed pool
        // read is a real I/O fault (not EOF), so return default rather than silently parsing zero-filled fields.
        Buffer.BlockCopy(header, 0, buf, 0, PageSize);
        int tail = wanted - PageSize;
        if (tail > 0 && !ReadExact(byteOffset + PageSize, buf.AsSpan(PageSize, tail)))
            return default;

        return CdxHeader.Parse(buf, TextEncoding);
    }

    /// <summary>Reads and parses a legacy single-IDX header (offset 0).</summary>
    public IdxHeader ReadIdxHeader(long byteOffset = 0)
    {
        var page = ReadPage(byteOffset);
        return page is null ? default : IdxHeader.Parse(page, TextEncoding);
    }

    /// <summary>
    /// Returns the raw 512-byte page at <paramref name="byteOffset"/>, or null
    /// when the offset is negative or the page lies past end-of-file.
    /// </summary>
    public byte[]? ReadPage(long byteOffset)
    {
        if (byteOffset < 0 || byteOffset + PageSize > Length)
            return null;

        var buf = new byte[PageSize];
        return ReadExact(byteOffset, buf, PageSize) ? buf : null;
    }

    /// <summary>
    /// Reads the 12-byte node header at <paramref name="byteOffset"/>, or null
    /// when the offset is out of range / the page cannot be read (never throws).
    /// </summary>
    public IndexNodeHeader? ReadNodeHeader(long byteOffset)
    {
        var page = ReadPage(byteOffset);
        return page is null ? null : IndexNodeHeader.Parse(page);
    }

    /// <summary>
    /// Decodes the branch (interior) entries of the node at
    /// <paramref name="byteOffset"/> using the given key length. Returns an
    /// empty list for a leaf, an out-of-range offset, or a malformed node.
    /// </summary>
    public IReadOnlyList<BranchEntry> ReadBranchEntries(long byteOffset, int keyLength)
    {
        var page = ReadPage(byteOffset);
        if (page is null || keyLength <= 0)
            return Array.Empty<BranchEntry>();

        var header = IndexNodeHeader.Parse(page);
        if (header.IsLeaf)
            return Array.Empty<BranchEntry>();

        // Branch entry = [key(keyLength)] [recno(4, BE)] [childPointer(4, BE)].
        // Cap the initial capacity to what can physically fit in a page so a
        // crafted KeyCount (u16, up to 0xFFFF) cannot force a huge allocation
        // before the per-entry bounds check runs.
        int entrySize = keyLength + 8;
        int maxEntries = (PageSize - 12) / entrySize;
        var entries = new List<BranchEntry>(Math.Min(header.KeyCount, maxEntries));

        int offset = 12;
        for (int i = 0; i < header.KeyCount; i++)
        {
            if (offset + entrySize > PageSize)
                break; // malformed / truncated node: stop rather than throw

            var key = page.AsSpan(offset, keyLength).ToArray();
            uint recno = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset + keyLength, 4));
            uint child = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset + keyLength + 4, 4));

            entries.Add(new BranchEntry(key, recno, child));
            offset += entrySize;
        }

        return entries;
    }

    /// <summary>
    /// Reads the compact leaf node at <paramref name="byteOffset"/> and decodes
    /// it into ordered <see cref="LeafEntry"/> pairs (plan §C4).
    /// <paramref name="keyLength"/> is the owning tag's key length;
    /// <paramref name="isCharacter"/> selects the trailing pad byte. Returns an
    /// empty list for an out-of-range offset, a non-leaf node, or an empty leaf;
    /// never throws.
    /// </summary>
    public IReadOnlyList<LeafEntry> ReadLeafEntries(long byteOffset, int keyLength, bool isCharacter)
    {
        var page = ReadPage(byteOffset);
        if (page is null || keyLength <= 0)
            return Array.Empty<LeafEntry>();

        var header = IndexNodeHeader.Parse(page);
        if (!header.IsLeaf)
            return Array.Empty<LeafEntry>();

        return CompactLeaf.Decode(page, keyLength, isCharacter);
    }

    private bool ReadExact(long offset, byte[] buffer, int count)
        => ReadExact(offset, buffer.AsSpan(0, Math.Min(count, buffer.Length)));

    private bool ReadExact(long offset, Span<byte> destination)
    {
        if (destination.Length == 0)
            return true;

        _stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < destination.Length)
        {
            int n = _stream.Read(destination[total..]);
            if (n == 0)
                return false;
            total += n;
        }
        return true;
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream?.Dispose();
    }
}
