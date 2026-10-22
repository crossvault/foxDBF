using System.Buffers.Binary;

namespace CrossVault.FoxDbf;

/// <summary>
/// Reads a single memo by block number from a DBF sidecar memo file (plan §A6).
/// Abstract base for the three on-disk flavours: <see cref="Dbase3MemoFile"/> (.dbt,
/// fixed 512-byte blocks, 0x00/0x1A-stripped), <see cref="Dbase4MemoFile"/> (.dbt,
/// length-prefixed LITTLE-endian) and <see cref="FoxproMemoFile"/> (.fpt, BIG-endian
/// block header). Owned and disposed by the <see cref="DbfTable"/> that opened it.
/// </summary>
/// <remarks>
/// Reading errors never throw — a bad/short/invalid block degrades to
/// <see langword="null"/> (§A6/§A10). A block number <c>&lt;= 0</c> is always
/// <see langword="null"/> (the empty/blank memo pointer).
/// </remarks>
public abstract class MemoFile : IDisposable
{
    /// <summary>The fixed default memo block size (512) when no header overrides it (§A6).</summary>
    protected const int DefaultBlockSize = 512;

    /// <summary>The fixed memo block header size in bytes (§A6).</summary>
    protected const int BlockHeaderSize = 8;

    /// <summary>The backing stream over the sidecar memo file.</summary>
    protected readonly Stream Stream;
    private readonly bool _leaveOpen;

    /// <summary>Wrap <paramref name="stream"/>; close it on <see cref="Dispose"/> unless <paramref name="leaveOpen"/>.</summary>
    protected MemoFile(Stream stream, bool leaveOpen)
    {
        Stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>The memo block size in bytes (fixed 512 for .dbt; header-derived for .fpt).</summary>
    public abstract int BlockSize { get; }

    /// <summary>True once <see cref="Dispose"/> has run (lets the owning table assert ownership).</summary>
    public bool IsDisposed { get; protected set; }

    /// <summary>
    /// True when this memo file's block reads are served from a memory-mapped view rather than
    /// per-read <see cref="System.IO.FileStream"/> syscalls (Highlike Phase B-3). Mirrors the
    /// owning table's effective backend; <see langword="false"/> on a network/UNC path or any
    /// mapping failure (the safe <see cref="System.IO.FileStream"/> path).
    /// </summary>
    // The EFFECTIVE backend: a memo opened on the mapped backend carries a MemoryMappedReadStream;
    // every other open (FileStream default, network fall-back, mapping failure) carries a plain Stream.
    public virtual bool IsMemoryMapped => Stream is MemoryMappedReadStream;

    /// <summary>
    /// Read the raw content bytes of the memo at <paramref name="blockNumber"/> (§A6).
    /// Returns <see langword="null"/> when <paramref name="blockNumber"/> is
    /// <c>&lt;= 0</c>, the block is invalid/empty, or any read error occurs — never throws.
    /// Transcoding (text vs binary) is the caller's responsibility.
    /// </summary>
    public abstract byte[]? ReadBytes(int blockNumber);

    /// <summary>Read exactly <paramref name="count"/> bytes at <paramref name="offset"/>, or fewer at EOF.</summary>
    private protected byte[] ReadExact(long offset, int count)
    {
        if (count <= 0)
            return [];
        Stream.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[count];
        int read = Stream.ReadAtLeast(buf, count, throwOnEndOfStream: false);
        if (read == count)
            return buf;
        // Short read at EOF: return only the bytes actually present.
        var trimmed = new byte[read];
        Array.Copy(buf, trimmed, read);
        return trimmed;
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (!_leaveOpen)
            Stream.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Open the sidecar memo file for <paramref name="dbfPath"/> selecting the concrete
    /// reader by <see cref="DbfVersion.MemoKind"/> (plan §A6). Sidecar auto-discovery:
    /// beside the <c>.dbf</c> look for the basename + <c>.fpt</c>/<c>.dbt</c>
    /// (case-insensitive), or <c>.dct</c> for a <c>.dbc</c>. When
    /// <paramref name="memoPathOverride"/> is supplied it is used verbatim. Returns
    /// <see langword="null"/> when the version carries no memo or no sidecar is found.
    /// </summary>
    public static MemoFile? Open(string dbfPath, DbfVersion version, string? memoPathOverride = null,
        DbfReadBackend backend = DbfReadBackend.FileStream, DbfDriveKind driveKind = DbfDriveKind.Auto)
    {
        if (version.MemoKind == MemoKind.None)
            return null;

        string? memoPath = memoPathOverride ?? DiscoverSidecar(dbfPath);
        if (memoPath is null || !File.Exists(memoPath))
            return null;

        Stream stream;
        try
        {
            // Opt-in §B-3: try the local-only read-only memory map; null → classic FileStream.
            // FileShare.ReadWrite (not .Read): on a network/forced/failure fall-back the sidecar
            // memo FileStream must not block a concurrent DbfWriter — matches the .dbf fall-back,
            // MemoryMappedReadStream, and IndexFile.Open (§B-3).
            stream = ReadBackendResolver.TryOpenMapped(memoPath, backend, driveKind)
                ?? new FileStream(memoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch
        {
            // Never throw on a missing/locked sidecar — the memo just stays absent (§A6).
            return null;
        }

        try
        {
            return version.MemoKind switch
            {
                MemoKind.Dbase3 => new Dbase3MemoFile(stream),
                MemoKind.Dbase4 => new Dbase4MemoFile(stream),
                MemoKind.Foxpro => new FoxproMemoFile(stream),
                _ => null,
            };
        }
        catch
        {
            stream.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Locate the sidecar memo file beside <paramref name="dbfPath"/> (plan §A6): a
    /// <c>.dbc</c> uses <c>.dct</c>; otherwise try <c>.fpt</c> then <c>.dbt</c>. The
    /// match is case-insensitive on the extension, scanning the directory so it also
    /// holds on a case-sensitive filesystem.
    /// </summary>
    private static string? DiscoverSidecar(string dbfPath)
    {
        string ext = Path.GetExtension(dbfPath);
        string[] candidates = string.Equals(ext, ".dbc", StringComparison.OrdinalIgnoreCase)
            ? [".dct"]
            : [".fpt", ".dbt"];

        foreach (var candidateExt in candidates)
        {
            var match = FindCaseInsensitive(Path.ChangeExtension(dbfPath, candidateExt));
            if (match is not null)
                return match;
        }
        return null;
    }

    /// <summary>Resolve <paramref name="path"/> ignoring case on the filename (§A6).</summary>
    private static string? FindCaseInsensitive(string path) => SidecarFile.FindCaseInsensitive(path);
}

/// <summary>
/// dBase III memo reader (.dbt, plan §A6): fixed 512-byte blocks from
/// <c>block * 512</c>, every <c>0x00</c> and <c>0x1A</c> byte stripped per block,
/// appending until a short (&lt; 512) read.
/// </summary>
public sealed class Dbase3MemoFile : MemoFile
{
    /// <summary>Open a dBase III memo over <paramref name="stream"/> (512-byte blocks).</summary>
    public Dbase3MemoFile(Stream stream, bool leaveOpen = false) : base(stream, leaveOpen) { }

    /// <inheritdoc />
    public override int BlockSize => DefaultBlockSize;

    /// <inheritdoc />
    public override byte[]? ReadBytes(int blockNumber)
    {
        if (blockNumber <= 0)
            return null;

        try
        {
            long offset = (long)blockNumber * DefaultBlockSize;
            Stream.Seek(offset, SeekOrigin.Begin);

            using var memo = new MemoryStream();
            var block = new byte[DefaultBlockSize];
            bool readAny = false;
            while (true)
            {
                int read = Stream.ReadAtLeast(block, DefaultBlockSize, throwOnEndOfStream: false);
                if (read == 0 && !readAny)
                    return null;
                readAny |= read > 0;
                // Strip all 0x00 and 0x1A bytes in this block, then append (§A6). The gem
                // breaks on the STRIPPED length, so a block carrying the 0x00/0x1A
                // terminator/padding ends the memo even on a full 512-byte raw read.
                int kept = 0;
                for (int i = 0; i < read; i++)
                {
                    byte b = block[i];
                    if (b != 0x00 && b != 0x1A)
                    {
                        memo.WriteByte(b);
                        kept++;
                    }
                }
                if (kept < DefaultBlockSize)
                    break; // stripped block shorter than 512 => end of memo.
            }
            return memo.ToArray();
        }
        catch
        {
            return null; // never throw (§A6/§A10)
        }
    }
}

/// <summary>
/// dBase IV memo reader (.dbt, plan §A6): fixed 512-byte blocks; per block skip 4 bytes,
/// then read the LITTLE-endian u32 length at offset 4 and return exactly that many bytes.
/// </summary>
public sealed class Dbase4MemoFile : MemoFile
{
    /// <summary>Open a dBase IV memo over <paramref name="stream"/> (length-prefixed LE).</summary>
    public Dbase4MemoFile(Stream stream, bool leaveOpen = false) : base(stream, leaveOpen) { }

    /// <inheritdoc />
    public override int BlockSize => DefaultBlockSize;

    /// <inheritdoc />
    public override byte[]? ReadBytes(int blockNumber)
    {
        if (blockNumber <= 0)
            return null;

        try
        {
            long offset = (long)blockNumber * DefaultBlockSize;
            var header = ReadExact(offset, BlockHeaderSize);
            if (header.Length < BlockHeaderSize)
                return null;

            // Skip 4 bytes; length is a LITTLE-endian u32 at block offset 4 (§A6).
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (length == 0)
                return null;

            long remaining = Stream.Length - offset - BlockHeaderSize;
            if (length > int.MaxValue || remaining < 0 || (long)length > remaining)
                return null;

            var content = ReadExact(offset + BlockHeaderSize, (int)length);
            return content;
        }
        catch
        {
            return null; // never throw (§A6/§A10)
        }
    }
}

/// <summary>
/// Visual FoxPro / FoxPro memo reader (.fpt, plan §A6): block size is the BIG-endian u16
/// at file offset 6; each memo at <c>block * blockSize</c> has a BIG-endian u32 type @0
/// and BIG-endian u32 length @4, content @8, valid only when <c>type == 1 &amp;&amp;
/// length &gt; 0</c>, content read contiguously for the full <c>length</c> across blocks.
/// </summary>
public sealed class FoxproMemoFile : MemoFile
{
    private readonly int _blockSize;

    /// <summary>Open a FoxPro memo over <paramref name="stream"/> (BIG-endian header).</summary>
    public FoxproMemoFile(Stream stream, bool leaveOpen = false) : base(stream, leaveOpen)
    {
        // Block size is the BIG-endian u16 at file offset 6 — the single most common .fpt
        // bug is reading it LITTLE-endian (§A6). Fall back to the 512 default if absent.
        int size = 0;
        try
        {
            var head = ReadExact(0, 8);
            if (head.Length >= 8)
                size = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6));
        }
        catch
        {
            size = 0;
        }
        _blockSize = size > 0 ? size : DefaultBlockSize;
    }

    /// <inheritdoc />
    public override int BlockSize => _blockSize;

    /// <inheritdoc />
    public override byte[]? ReadBytes(int blockNumber)
    {
        if (blockNumber <= 0)
            return null;

        try
        {
            long offset = (long)blockNumber * _blockSize;
            var header = ReadExact(offset, BlockHeaderSize);
            if (header.Length < BlockHeaderSize)
                return null;

            // type u32 BE @0, length u32 BE @4 — valid only when type == 1 && length > 0 (§A6).
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (type != 1 || length == 0)
                return null;

            long remaining = Stream.Length - offset - BlockHeaderSize;
            if (length > int.MaxValue || remaining < 0 || (long)length > remaining)
                return null;

            // Content is read contiguously for the full length, spanning blocks (§A6).
            var content = ReadExact(offset + BlockHeaderSize, (int)length);
            return content;
        }
        catch
        {
            return null; // never throw (§A6/§A10)
        }
    }
}
