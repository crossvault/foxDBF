namespace CrossVault.FoxDbf;

/// <summary>
/// Read-only entry point to a DBF table (plan §A4/§A10). Opens a <c>.dbf</c> from
/// a path or a <see cref="Stream"/>, parses the header and the field-descriptor
/// array, and exposes the table geometry: <see cref="Version"/>,
/// <see cref="RecordCount"/> and <see cref="Columns"/>.
/// </summary>
/// <remarks>
/// Field descriptors begin at <see cref="DbfVersion.HeaderSize"/> and are read at
/// the version's fixed width (16/32/48 bytes). The descriptor array is terminated
/// by a <c>0x0D</c> byte, located via a non-destructive peek; each column's
/// <see cref="DbfColumn.Offset"/> is the prefix-sum of prior lengths, so the
/// invariant <c>RecordLength &gt;= 1 + Σ column.Length</c> holds (trailing record slack is allowed).
/// Owns its backing <see cref="Stream"/> and disposes it unless opened with
/// <c>leaveOpen: true</c>.
/// </remarks>
public sealed partial class DbfTable : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _disposed;

    // The source .dbf path (when opened from a path), used to auto-locate the sidecar
    // structural .cdx for the ExplainQuery bridge. Null for stream-only opens.
    private readonly string? _dbfPath;

    // The sidecar memo reader (.fpt/.dbt/.dct), auto-opened beside the .dbf when present
    // (plan §A6). Owned by this table and disposed with it.
    private readonly MemoFile? _memo;

    // The opt-in query accelerator (Highlike) attached to this table, if any. The ONLY
    // Core surface the accelerator sub-project plugs into. Null → the Core QueryOptimizer
    // path runs unchanged. Dependency direction stays strictly Highlike → Core.
    private Query.IQueryAccelerator? _accelerator;
    private bool _ownsAccelerator;

    // The opt-in read backend / drive-kind this table was opened with (§B-3). Carried so
    // OpenStructuralCdx() can forward the SAME backend to the sidecar .cdx page reader —
    // otherwise the index-driven Query()/ExplainQuery() path would silently stay FileStream
    // even when the table was opened MemoryMapped. Resolution (local-only map, network
    // fall-back) still happens inside ReadBackendResolver, so this is a pure pass-through.
    private readonly DbfReadBackend _readBackend;
    private readonly DbfDriveKind _driveKind;

    /// <summary>
    /// The sidecar memo file backing this table's <c>M</c> fields (plan §A6), or
    /// <see langword="null"/> when the version carries no memo or no sidecar was found
    /// (or an explicit override resolved to nothing). Owned by this table — disposed
    /// together with it.
    /// </summary>
    public MemoFile? Memo => _memo;

    /// <summary>
    /// Decode an <c>M</c> (memo) field's pointer and resolve it against <see cref="Memo"/>
    /// (plan §A6). Pointer decode: VFP <c>0x30/0x31/0x32</c> store a 4-byte LITTLE-endian
    /// block number; all other versions store right-justified ASCII digits (blank → 0).
    /// A zero/blank pointer or a missing memo file yields <see langword="null"/>; otherwise
    /// the block is read and, for a text memo, transcoded via <see cref="Encoding"/>
    /// (binary/NOCPTRANS memo → raw <c>byte[]</c>). Read errors → <see langword="null"/>.
    /// </summary>
    internal object? ReadMemoValue(DbfColumn column, ReadOnlySpan<byte> raw)
    {
        try
        {
            int block = DecodeMemoPointer(raw);
            if (block <= 0 || _memo is null)
                return null;

            var bytes = _memo.ReadBytes(block);
            if (bytes is null)
                return null;

            // A 'W' (VFP9 0x32 Blob) is an opaque binary blob — return its raw FPT bytes,
            // always binary even when its descriptor lacks the 0x04 flag (§A5b line 257).
            if (column.Type == 'W')
                return bytes;

            // A memo ('M') is TEXT. The NOCPTRANS / binary flag (0x04) means "do not
            // translate code pages", NOT "opaque blob": such a memo is decoded 1:1 via
            // Latin1 (byte↔char passthrough) instead of the table code page. A VFP .dbc
            // flags its CODE and PROPERTY memos NOCPTRANS yet they hold readable text
            // (e.g. the StoredProceduresSource CODE memo IS the stored-procedure source) —
            // returning raw bytes there made every GetString() read empty. Latin1 round-
            // trips byte-exactly with the writer's EncodeMemoText, so a binary memo stays
            // lossless (a caller can recover the bytes via Encoding.Latin1.GetBytes).
            return column.IsBinary
                ? System.Text.Encoding.Latin1.GetString(bytes)
                : Encoding.GetString(bytes);
        }
        catch
        {
            return null; // never throw on a bad memo (§A6/§A10)
        }
    }

    /// <summary>
    /// Decode an <c>M</c> field's pointer (plan §A6): Visual FoxPro <c>0x30/0x31/0x32</c>
    /// store a 4-byte LITTLE-endian block number; every other version stores
    /// right-justified ASCII digits (blank → 0). Returns 0 for an empty/blank pointer.
    /// </summary>
    private int DecodeMemoPointer(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0)
            return 0;

        if (Version.Code is 0x30 or 0x31 or 0x32)
            return raw.Length >= 4 ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw) : 0;

        // Right-justified ASCII digits — Latin1 decode never fails; blank/junk → 0 (§A6).
        var s = System.Text.Encoding.Latin1.GetString(raw).Trim();
        return int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var block) ? block : 0;
    }

    /// <summary>
    /// True when this table's record byte reads are served from a memory-mapped view rather
    /// than per-read <see cref="System.IO.FileStream"/> syscalls (Highlike Phase B-3). Reflects
    /// the EFFECTIVE backend after fall-back: an opt-in <see cref="DbfOptions.ReadBackend"/> of
    /// <see cref="DbfReadBackend.Auto"/> / <see cref="DbfReadBackend.MemoryMapped"/> resolves to
    /// <see langword="true"/> only on LOCAL/fixed storage and a mappable file; a network/UNC path,
    /// an empty/too-small file, or any mapping failure leaves it <see langword="false"/> (the
    /// safe <see cref="System.IO.FileStream"/> path). Default open → always <see langword="false"/>.
    /// </summary>
    // The EFFECTIVE backend is read off the backing stream type: a path open that resolved to a
    // memory-mapped source carries a MemoryMappedReadStream; every other open (default FileStream,
    // network fall-back, mapping failure, stream-only open) carries an ordinary Stream.
    public bool IsMemoryMapped => _stream is MemoryMappedReadStream;

    /// <summary>The resolved version configuration (format/memo lookup, plan §A4).</summary>
    public DbfVersion Version { get; }

    /// <summary>Number of records declared in the header.</summary>
    public int RecordCount { get; }

    /// <summary>The parsed field descriptors, in physical column order.</summary>
    public IReadOnlyList<DbfColumn> Columns { get; }

    private readonly int _headerLength;
    private readonly int _recordLength;

    /// <summary>
    /// Byte offset of the first data record (header geometry, §A4). Under
    /// <see cref="Recovery.Reconstruct"/> this is the rebuilt value
    /// (<c>offset(0x0D) + 1</c>, plus 263 for the VFP backlink layout), not the
    /// (possibly zeroed) on-disk header field.
    /// </summary>
    public int HeaderLength => _headerLength;

    /// <summary>
    /// Bytes per physical record incl. the leading delete flag (header geometry, §A4).
    /// Under <see cref="Recovery.Reconstruct"/> this is <c>1 + Σ descriptor lengths</c>.
    /// </summary>
    public int RecordLength => _recordLength;

    /// <summary>
    /// The character encoding for this table's text fields, resolved from the
    /// header code-page byte with a CP1252 fallback (§A7). Used by field decoding.
    /// Selection order (§A7): explicit <see cref="DbfOptions.Encoding"/> override →
    /// header code page → <see cref="DbfOptions.DefaultEncoding"/> (default CP1252).
    /// </summary>
    public System.Text.Encoding Encoding { get; }

    /// <summary>
    /// True when <see cref="Encoding"/> came from an explicit <see cref="DbfOptions.Encoding"/>
    /// override (§A7 highest precedence). Suppresses the lowest-precedence UTF-8
    /// auto-detect short-circuit in character decoding so a forced encoding is honoured
    /// verbatim even when the raw bytes are coincidentally valid UTF-8.
    /// </summary>
    internal bool EncodingIsExplicit { get; }

    /// <summary>
    /// The hidden <c>_NullFlags</c> system column (type char <c>'0'</c>, IsSystem set —
    /// normally the last physical column), or <see langword="null"/> when the table
    /// carries no NULL/varlen bitmap (§A5b). Always resolved from the full physical
    /// column list regardless of <see cref="DbfOptions.ExposeSystemColumns"/>.
    /// </summary>
    internal DbfColumn? NullFlagsColumn { get; }

    /// <summary>Whether a set null bit forces a decoded value to <see langword="null"/> (§A5b).</summary>
    internal bool ApplyNullFlags { get; }

    // Per physical column: its varlen "full" bit and null bit in the _NullFlags bitmap
    // (-1 when the column consumes no such bit), assigned in PHYSICAL field order,
    // LSB-first, varlen (lower) before null (higher). Reference-keyed: the public and
    // physical lists share DbfColumn instances.
    private readonly Dictionary<DbfColumn, (int varlenBit, int nullBit)> _bitMap;

    /// <summary>The varlen/null bit indices for <paramref name="column"/>, or (-1,-1) when none (§A5b).</summary>
    internal (int varlenBit, int nullBit) GetVarlenNullBits(DbfColumn column)
        => _bitMap.TryGetValue(column, out var bits) ? bits : (-1, -1);

    private DbfTable(Stream stream, bool leaveOpen, DbfHeader header, IReadOnlyList<DbfColumn> physical, DbfOptions options, string? dbfPath)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _dbfPath = dbfPath;
        _readBackend = options.ReadBackend;
        _driveKind = options.DriveKind;
        Version = header.Version;

        // Auto-open the sidecar memo (.fpt/.dbt/.dct) beside the .dbf, or from an explicit
        // override (§A6). Stream-only opens with no path/override carry no memo. Never throws.
        // The sidecar memo rides the SAME opt-in read backend as the table (§B-3): a forced/auto map
        // on a local file maps the .fpt/.dbt too; a network path or any failure falls back to FileStream.
        if (options.MemoPath is not null)
            _memo = MemoFile.Open(dbfPath ?? string.Empty, Version, options.MemoPath, options.ReadBackend, options.DriveKind);
        else if (dbfPath is not null)
            _memo = MemoFile.Open(dbfPath, Version, memoPathOverride: null, options.ReadBackend, options.DriveKind);
        RecordCount = header.RecordCount;
        _headerLength = header.HeaderLength;
        _recordLength = header.RecordLength;
        // §A7 selection order: explicit DbfOptions.Encoding override → header code page
        // → DbfOptions.DefaultEncoding (default CP1252, applied inside ResolveEncoding).
        Encoding = options.Encoding
            ?? Encodings.ResolveEncoding(header.CodePage, options.DefaultEncoding);
        EncodingIsExplicit = options.Encoding is not null;
        ApplyNullFlags = options.ApplyNullFlags;

        // Identify the _NullFlags system column: type char '0', IsSystem set, normally
        // the last physical column (scan from the end to honour that convention).
        DbfColumn? nf = null;
        for (int i = physical.Count - 1; i >= 0; i--)
        {
            if (physical[i].IsSystem && physical[i].Type == '0')
            {
                nf = physical[i];
                break;
            }
        }
        NullFlagsColumn = nf;

        // Assign bitmap bits in physical field order, LSB-first: a V/Q field takes the
        // lower (varlen) bit, a nullable field the higher (null) bit; a field that is
        // both consumes two bits in that order (§A5b).
        _bitMap = new Dictionary<DbfColumn, (int, int)>(ReferenceEqualityComparer.Instance);
        int bit = 0;
        foreach (var c in physical)
        {
            int varlenBit = -1, nullBit = -1;
            if (c.Type is 'V' or 'Q')
                varlenBit = bit++;
            if (c.IsNullable)
                nullBit = bit++;
            if (varlenBit >= 0 || nullBit >= 0)
                _bitMap[c] = (varlenBit, nullBit);
        }

        // Public schema: hide system columns unless asked to expose them. The full
        // physical list (with identical offsets) still drives record layout / bitmap.
        Columns = options.ExposeSystemColumns
            ? physical
            : physical.Where(c => !c.IsSystem).ToList();
    }

    /// <summary>
    /// Open a DBF table from a file path. Throws
    /// <see cref="DbfFileNotFoundException"/> when the file does not exist (§A10).
    /// </summary>
    public static DbfTable Open(string path) => Open(path, new DbfOptions());

    /// <summary>
    /// Open a DBF table from a file path with explicit <paramref name="options"/>
    /// (plan §A5b: <see cref="DbfOptions.ExposeSystemColumns"/> /
    /// <see cref="DbfOptions.ApplyNullFlags"/>).
    /// </summary>
    public static DbfTable Open(string path, DbfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(path))
            throw new DbfFileNotFoundException($"DBF file not found: {path}");

        // Opt-in §B-3: try a local-only read-only memory map; null → classic FileStream (unchanged).
        // FileShare.ReadWrite (not .Read): when the map falls back to a FileStream (UNC/network
        // auto-detect, forced DriveKind.Network, or any mmap failure) the fallback must NOT block a
        // concurrent DbfWriter (which needs FileAccess.ReadWrite) — the multi-user FoxPro case the
        // opt-in exists for. Matches MemoryMappedReadStream and IndexFile.Open (§B-3).
        var stream = ReadBackendResolver.TryOpenMapped(path, options.ReadBackend, options.DriveKind)
            ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, options, leaveOpen: false, dbfPath: path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open a DBF table from a file path applying an internal physical-column transform
    /// before the table geometry is finalized (plan §A8). The <paramref name="columnTransform"/>
    /// receives the FULL physical column list (including the hidden <c>_NullFlags</c> system
    /// column) and returns the list to use; the bitmap and public <see cref="Columns"/> are
    /// both derived from its result so reference identity (and thus NULL/varlen decoding)
    /// stays consistent. Used by <see cref="DbfDatabase"/> to layer DBC long field names on.
    /// </summary>
    internal static DbfTable Open(string path, DbfOptions options, Func<IReadOnlyList<DbfColumn>, IReadOnlyList<DbfColumn>>? columnTransform)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(path))
            throw new DbfFileNotFoundException($"DBF file not found: {path}");

        // Opt-in §B-3: try a local-only read-only memory map; null → classic FileStream (unchanged).
        // FileShare.ReadWrite (not .Read): the fallback FileStream must not block a concurrent
        // DbfWriter when mapping falls back (network/forced/failure) — matches the path above,
        // MemoryMappedReadStream, and IndexFile.Open (§B-3).
        var stream = ReadBackendResolver.TryOpenMapped(path, options.ReadBackend, options.DriveKind)
            ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, options, leaveOpen: false, dbfPath: path, columnTransform);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open a DBF table over an existing stream. When <paramref name="leaveOpen"/>
    /// is false (the default) the stream is disposed together with this table.
    /// </summary>
    public static DbfTable Open(Stream stream, bool leaveOpen = false)
        => Open(stream, new DbfOptions(), leaveOpen);

    /// <summary>
    /// Directory-scan-friendly probe (plan §A12): open <paramref name="path"/> and return
    /// the table, or <see langword="null"/> when the file cannot be opened (e.g. an
    /// unknown/unsupported version byte such as the encrypted <c>V_usr.dbf</c> <c>0xee</c>),
    /// in which case a human-readable reason is returned via <paramref name="diagnostic"/>.
    /// Never throws for a merely unsupported/corrupt file — unlike <see cref="Open(string)"/>.
    /// </summary>
    public static DbfTable? TryOpen(string path, out string? diagnostic)
    {
        try
        {
            var table = Open(path);
            diagnostic = null;
            return table;
        }
        catch (Exception ex) when (
            ex is DbfUnsupportedVersionException
               or DbfCorruptHeaderException
               or DbfFileNotFoundException
               or DbfNoColumnsDefinedException
               or DbfColumnNameException
               or DbfColumnLengthException)
        {
            // Never-throw, directory-scan friendliness (§A12/§A10): surface the reason
            // instead of propagating. Genuinely unexpected failures (I/O, OOM) still throw.
            diagnostic = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Directory-scan-friendly probe (plan §A12): open <paramref name="path"/> or return
    /// <see langword="null"/> when it cannot be opened. See
    /// <see cref="TryOpen(string, out string?)"/> for the variant exposing the diagnostic.
    /// </summary>
    public static DbfTable? TryOpen(string path) => TryOpen(path, out _);

    /// <summary>
    /// Open a DBF table over an existing stream with explicit <paramref name="options"/>
    /// (plan §A5b). When <paramref name="leaveOpen"/> is false the stream is disposed
    /// together with this table.
    /// </summary>
    public static DbfTable Open(Stream stream, DbfOptions options, bool leaveOpen = false)
        => Open(stream, options, leaveOpen, dbfPath: null);

    private static DbfTable Open(Stream stream, DbfOptions options, bool leaveOpen, string? dbfPath,
        Func<IReadOnlyList<DbfColumn>, IReadOnlyList<DbfColumn>>? columnTransform = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return OpenCore(stream, options, leaveOpen, dbfPath, columnTransform);
        }
        catch
        {
            // We own the stream unless the caller asked to keep it open; on any
            // parse failure honour the ownership contract and dispose it before
            // rethrowing, mirroring Open(string)'s failure path.
            if (!leaveOpen)
                stream.Dispose();
            throw;
        }
    }

    private static DbfTable OpenCore(Stream stream, DbfOptions options, bool leaveOpen, string? dbfPath,
        Func<IReadOnlyList<DbfColumn>, IReadOnlyList<DbfColumn>>? columnTransform = null)
    {
        long fileSize = stream.CanSeek ? stream.Length : 0L;

        // Read the 32-byte header window from the start of the stream.
        var headerBuf = new byte[32];
        stream.Seek(0, SeekOrigin.Begin);
        int headerRead = stream.ReadAtLeast(headerBuf, headerBuf.Length, throwOnEndOfStream: false);
        var rawHeader = DbfHeader.Read(headerBuf.AsSpan(0, headerRead));

        bool reconstruct = options.Recovery == Recovery.Reconstruct;
        bool forced = options.ForceVersion is not null;

        // Reconstruct rebuilds the whole geometry from the (intact) descriptors, so it
        // never consults the (possibly zeroed) on-disk version/geometry fields (§A12).
        if (reconstruct)
            return Reconstruct(stream, leaveOpen, rawHeader, options, fileSize, dbfPath, columnTransform);

        // Force-Open (tools/forensics): interpret the file with an explicitly supplied
        // layout regardless of the on-disk version byte. Skips the version/corruption
        // gates below — the caller has accepted the risk (§A12).
        if (forced)
        {
            byte forcedByte = options.ForceVersion!.Value;
            var forcedVersion = DbfVersion.FromByte(forcedByte);
            var forcedHeader = rawHeader with { VersionByte = forcedByte, Version = forcedVersion };
            var forcedColumns = ParseColumns(stream, forcedVersion, forcedHeader.HeaderLength, NameEncoding(rawHeader, options), tolerantDescriptors: true);
            if (columnTransform is not null)
                forcedColumns = columnTransform(forcedColumns);
            return new DbfTable(stream, leaveOpen, forcedHeader, forcedColumns, options, dbfPath);
        }

        // Strict / Tolerant gates: reject a structurally impossible header (zeroed
        // RecordLength/HeaderLength — the corrupt.prg first-10-bytes-nulled case) and
        // an unrecognized/unsupported version byte (e.g. encrypted V_usr.dbf 0xee).
        if (rawHeader.RecordLength == 0 || rawHeader.HeaderLength == 0)
            throw new DbfCorruptHeaderException(
                $"Header geometry is structurally impossible (RecordLength={rawHeader.RecordLength}, " +
                $"HeaderLength={rawHeader.HeaderLength}); use Recovery.Reconstruct to rebuild it from the descriptors (§A12).");

        if (!rawHeader.Version.IsRecognized)
            throw new DbfUnsupportedVersionException(
                $"Unsupported/unknown DBF version byte 0x{rawHeader.VersionByte:x2}; " +
                $"use DbfTable.TryOpen for a non-throwing probe or DbfOptions.ForceVersion to force a layout (§A12).");

        var columns = ParseColumns(stream, rawHeader.Version, rawHeader.HeaderLength, NameEncoding(rawHeader, options));
        long requiredRecordLength = 1;
        foreach (var column in columns)
            requiredRecordLength += column.Length;
        if (rawHeader.RecordLength < requiredRecordLength)
            throw new DbfCorruptHeaderException(
                $"Header record geometry is inconsistent: declared RecordLength={rawHeader.RecordLength}, " +
                $"required={requiredRecordLength} from the physical field descriptors; " +
                "use Recovery.Reconstruct to rebuild the record geometry.");
        if (columnTransform is not null)
            columns = columnTransform(columns);

        var header = rawHeader;
        if (options.Recovery == Recovery.Tolerant && rawHeader.RecordLength > 0)
        {
            // Truncation: clamp the declared RecordCount to the records physically present
            // rather than throwing while iterating past the end of the file (§A12).
            long present = (fileSize - rawHeader.HeaderLength) / rawHeader.RecordLength;
            if (present < rawHeader.RecordCount)
                header = rawHeader with { RecordCount = (int)Math.Max(0, present) };
        }

        return new DbfTable(stream, leaveOpen, header, columns, options, dbfPath);
    }

    /// <summary>
    /// Rebuild the header geometry from the intact field descriptors (plan §A12,
    /// <c>Recovery.Reconstruct</c>): <c>RecordLength = 1 + Σ descriptor lengths</c>,
    /// <c>HeaderLength = offset(0x0D) + 1</c> (plus 263 for the Visual FoxPro backlink
    /// layout), <c>RecordCount = (fileSize − HeaderLength) / RecordLength</c> clamped to
    /// the records physically present. The +263 backlink and the inferred version byte
    /// are decided by a DETERMINISTIC discriminator (a type-'0' <c>_NullFlags</c>
    /// descriptor or a readable ".dbc" backlink string), with the modulo check used only
    /// as corroboration.
    /// </summary>
    private static DbfTable Reconstruct(Stream stream, bool leaveOpen, DbfHeader rawHeader, DbfOptions options, long fileSize, string? dbfPath,
        Func<IReadOnlyList<DbfColumn>, IReadOnlyList<DbfColumn>>? columnTransform = null)
    {
        // The version byte is (usually) zeroed; parse descriptors with the standard
        // 32/32 layout (VFP and dBase III both use it). Scan to the real 0x0D bounded
        // only by the file size, since the on-disk HeaderLength is unreliable here.
        var scanVersion = DbfVersion.FromByte(0x30); // 32-byte header, 32-byte descriptors
        var (columns, terminator) = ParseColumnsScan(stream, scanVersion, fileSize, NameEncoding(rawHeader, options), tolerantDescriptors: true);

        int recordLength = 1;
        foreach (var c in columns)
            recordLength += c.Length;

        long headerBase = terminator + 1; // offset(0x0D) + 1

        // Deterministic VFP discriminator: a type-'0' _NullFlags system descriptor, or a
        // readable ".dbc" backlink string in the 263 bytes following 0x0D. The modulo
        // rule is only corroborating (ambiguous since 263 is prime — §A12).
        bool hasNullFlags = columns.Any(c => c.Type == '0' && c.IsSystem);
        bool hasBacklinkString = HasReadableDbcBacklink(stream, headerBase, fileSize);
        bool vfpLayout = hasNullFlags || hasBacklinkString;

        // Infer the (zeroed) version byte FIRST, then derive the +263 backlink offset from
        // it, so the two can never contradict each other (§A12: "+263 Backlink nur bei
        // VFP-Version 0x30/0x31/0x32"). A VFP *free* table (e.g. Integer+Char, no nullable/
        // varchar columns) has neither a type-'0' _NullFlags descriptor nor a readable
        // ".dbc" backlink string, so vfpLayout=false — but hasVfpTypes=true still pins it to
        // VFP 0x30, and VFP tables ALWAYS carry the (zero-filled) 263-byte backlink. Tying
        // +263 to the inferred VFP version (not the narrower vfpLayout discriminator) keeps
        // HeaderLength and the record geometry consistent (§A12 Reconstruct DoD).
        bool hasVfpTypes = columns.Any(c => c.Type is 'I' or 'Y' or 'T' or 'B' or '0' or 'V' or 'Q');
        byte inferredByte = (vfpLayout || hasVfpTypes)
            ? (byte)0x30
            : (columns.Any(c => c.Type == 'M') ? (byte)0x83 : (byte)0x03);
        var inferredVersion = DbfVersion.FromByte(inferredByte);
        long headerLength = headerBase + (inferredVersion.HasBacklink ? 263 : 0);

        int recordCount = 0;
        if (recordLength > 0 && fileSize > headerLength)
            recordCount = (int)((fileSize - headerLength) / recordLength); // floor => clamps truncation

        var recovered = rawHeader with
        {
            VersionByte = inferredByte,
            Version = inferredVersion,
            HeaderLength = (int)headerLength,
            RecordLength = recordLength,
            RecordCount = recordCount,
        };

        IReadOnlyList<DbfColumn> finalColumns = columnTransform is not null ? columnTransform(columns) : columns;
        return new DbfTable(stream, leaveOpen, recovered, finalColumns, options, dbfPath);
    }

    /// <summary>
    /// True when the 263-byte backlink block following <paramref name="headerBase"/>
    /// contains a readable ".dbc" database-container reference (plan §A12 discriminator).
    /// </summary>
    private static bool HasReadableDbcBacklink(Stream stream, long headerBase, long fileSize)
    {
        long available = Math.Min(263L, fileSize - headerBase);
        if (available <= 4) return false;

        var buf = new byte[available];
        stream.Seek(headerBase, SeekOrigin.Begin);
        int read = stream.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        if (read <= 4) return false;

        var text = System.Text.Encoding.ASCII.GetString(buf, 0, read);
        return text.Contains(".dbc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse the field-descriptor array (plan §A4): begin at
    /// <see cref="DbfVersion.HeaderSize"/>, read fixed-width descriptors of
    /// <see cref="DbfVersion.DescriptorWidth"/> bytes, scanning until the 0x0D
    /// terminator via a non-destructive peek, and compute each column's offset as
    /// the prefix-sum of prior lengths.
    /// </summary>
    private static IReadOnlyList<DbfColumn> ParseColumns(Stream stream, DbfVersion version, long headerLengthBound,
        System.Text.Encoding nameEncoding, bool tolerantDescriptors = false)
    {
        long descriptorBytesBeforeBacklink = headerLengthBound - version.HeaderSize - 263 - 1;
        bool hasCanonicalBacklinkGeometry = version.HasBacklink &&
            descriptorBytesBeforeBacklink >= 0 &&
            descriptorBytesBeforeBacklink % version.DescriptorWidth == 0;
        if (hasCanonicalBacklinkGeometry && !tolerantDescriptors)
        {
            long expectedTerminator = headerLengthBound - 263 - 1;
            if (expectedTerminator < version.HeaderSize)
                throw new DbfCorruptHeaderException(
                    $"Declared HeaderLength={headerLengthBound} leaves no room for field descriptor terminator 0x0D; " +
                    "use Recovery.Reconstruct to recover from damaged header geometry.");

            stream.Seek(expectedTerminator, SeekOrigin.Begin);
            if (stream.ReadByte() != 0x0D)
                throw new DbfCorruptHeaderException(
                    $"Field descriptor terminator 0x0D is missing at expected offset {expectedTerminator} " +
                    $"for declared HeaderLength={headerLengthBound}; use Recovery.Reconstruct to recover.");
        }

        var scan = ParseColumnsScan(stream, version, headerLengthBound, nameEncoding, tolerantDescriptors);
        if (tolerantDescriptors)
            return scan.Columns;

        if (scan.TerminatorOffset >= headerLengthBound)
            throw new DbfCorruptHeaderException(
                $"Field descriptor terminator 0x0D is missing before declared HeaderLength={headerLengthBound}; " +
                "use Recovery.Reconstruct to recover from damaged header geometry.");

        stream.Seek(scan.TerminatorOffset, SeekOrigin.Begin);
        if (stream.ReadByte() != 0x0D)
            throw new DbfCorruptHeaderException(
                $"Field descriptor terminator 0x0D is missing at offset {scan.TerminatorOffset} " +
                $"before declared HeaderLength={headerLengthBound}; use Recovery.Reconstruct to recover.");

        return scan.Columns;
    }

    /// <summary>
    /// Core descriptor scan (plan §A4/§A12). Like <see cref="ParseColumns"/> but also
    /// returns the byte offset of the <c>0x0D</c> terminator (or where scanning stopped),
    /// which <see cref="Reconstruct"/> uses to rebuild <c>HeaderLength</c>. When
    /// <paramref name="tolerantDescriptors"/> is set, a descriptor that fails to parse
    /// (empty name / bad length on garbage or forced bytes) stops the scan instead of
    /// throwing.
    /// </summary>
    private static (List<DbfColumn> Columns, long TerminatorOffset) ParseColumnsScan(
        Stream stream, DbfVersion version, long headerLengthBound, System.Text.Encoding nameEncoding, bool tolerantDescriptors)
    {
        int headerSize = version.HeaderSize;
        int width = version.DescriptorWidth;

        var columns = new List<DbfColumn>();
        long pos = headerSize;
        int runningOffset = 0;
        var descriptor = new byte[width];
        long terminator = pos;

        while (true)
        {
            terminator = pos;

            // Bound the scan to the header region: a full descriptor must fit
            // before the bound. Guards against a missing/overwritten 0x0D
            // terminator (corrupt or zero-padded header, §A12), which would
            // otherwise read descriptor-width chunks out of the data region.
            if (pos + width > headerLengthBound)
                break;

            stream.Seek(pos, SeekOrigin.Begin);

            // Non-destructive peek of the first byte: 0x0D terminates the array.
            int first = stream.ReadByte();
            if (first < 0 || first == 0x0D)
                break;

            // Restore and read the full fixed-width descriptor.
            stream.Seek(pos, SeekOrigin.Begin);
            int got = stream.ReadAtLeast(descriptor, width, throwOnEndOfStream: false);
            if (got < width)
                break;

            DbfColumn col;
            try
            {
                col = ParseDescriptor(descriptor.AsSpan(0, width), version, runningOffset, nameEncoding);
            }
            catch (Exception ex) when (tolerantDescriptors &&
                ex is DbfColumnNameException or DbfColumnLengthException)
            {
                // Garbage/forced bytes: stop scanning rather than throw (§A12).
                break;
            }

            columns.Add(col);
            runningOffset += col.Length;
            pos += width;
        }

        return (columns, terminator);
    }

    /// <summary>
    /// Decode one fixed-width field descriptor (plan §A4 layouts).
    /// Standard 32B: name A11 (0–10), type @11, length @16, decimal @17.
    /// FoxBase 16B: name A11 (0–10), type @11, length @12, decimal forced 0.
    /// dBase 7 48B: name A32 (0–31), type @32, length @33, decimal @34.
    /// </summary>
    private static DbfColumn ParseDescriptor(ReadOnlySpan<byte> d, DbfVersion version, int offset, System.Text.Encoding nameEncoding)
    {
        int nameLen;
        int typeIndex;
        int lengthIndex;
        int decimalIndex; // -1 => forced to 0

        switch (version.DescriptorWidth)
        {
            case 16: // FoxBase / dBase II
                nameLen = 11;
                typeIndex = 11;
                lengthIndex = 12;
                decimalIndex = -1;
                break;
            case 48: // dBase 7
                nameLen = 32;
                typeIndex = 32;
                lengthIndex = 33;
                decimalIndex = 34;
                break;
            default: // 32B standard
                nameLen = 11;
                typeIndex = 11;
                lengthIndex = 16;
                decimalIndex = 17;
                break;
        }

        var name = CleanName(d[..nameLen], nameEncoding);
        char type = (char)d[typeIndex];
        int length = d[lengthIndex];
        int dec = decimalIndex >= 0 ? d[decimalIndex] : 0;

        // Field-flags byte lives at descriptor offset 18, only in the 32-byte layout
        // (the 16/48-byte layouts have no such byte → flags 0; §A5b).
        byte flags = version.DescriptorWidth == 32 && d.Length > 18 ? d[18] : (byte)0;

        return new DbfColumn(name, type, length, dec, offset, flags);
    }

    /// <summary>
    /// Clean a fixed-width name field: cut at the first NUL byte, decode with the table's
    /// code page, and trim surrounding spaces (plan §A4 — trim on NUL/space). VFP stores a
    /// field name such as <c>RAZÓNSOC</c> in the table's code page (0xD3 for 'Ó' in CP1252);
    /// decoding it as ASCII would turn it into <c>RAZ?NSOC</c>, which no expression can name.
    /// </summary>
    private static string CleanName(ReadOnlySpan<byte> nameField, System.Text.Encoding encoding)
    {
        int end = nameField.IndexOf((byte)0);
        if (end < 0) end = nameField.Length;
        var slice = nameField[..end];
        return encoding.GetString(slice).Trim();
    }

    /// <summary>The encoding field names are decoded with — the same §A7 selection the table uses
    /// for its data (explicit override → header code page → default).</summary>
    private static System.Text.Encoding NameEncoding(DbfHeader header, DbfOptions options)
        => options.Encoding ?? Encodings.ResolveEncoding(header.CodePage, options.DefaultEncoding);

    /// <summary>
    /// Stream the data region (plan §A3): seek <see cref="DbfHeader.HeaderLength"/>,
    /// read <see cref="DbfHeader.RecordLength"/> bytes per record into a reused buffer,
    /// and yield a <see cref="DbfRecord"/> backed by a copy so yielded records stay
    /// valid after the enumerator advances. Deleted records are skipped.
    /// </summary>
    public IEnumerable<DbfRecord> Records => EnumerateAll(includeDeleted: false);

    /// <summary>
    /// Stream every physical record in order (plan §A3). When
    /// <paramref name="includeDeleted"/> is false (the default) records whose leading
    /// flag byte is <c>0x2A</c> are skipped; when true, all <see cref="RecordCount"/>
    /// physical records are yielded, deleted ones with <see cref="DbfRecord.IsDeleted"/>
    /// set. The trailing <c>0x1A</c> EOF byte (if any) is never read as a record, and
    /// iteration does not depend on it being present.
    /// </summary>
    public IEnumerable<DbfRecord> EnumerateAll(bool includeDeleted = false) => Iterate(includeDeleted);

    private IEnumerable<DbfRecord> Iterate(bool includeDeleted)
    {
        // Guard here (not in EnumerateAll) so disposal is observed when enumeration
        // actually begins, not when the deferred iterator is merely constructed.
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = new byte[_recordLength];

        for (int i = 0; i < RecordCount; i++)
        {
            // Re-seek every iteration so the shared _stream position is owned per-read.
            // This makes Iterate position-independent: GetRecord(i) may be interleaved
            // inside a live foreach without corrupting the enumerator's progress.
            _stream.Seek(_headerLength + (long)i * _recordLength, SeekOrigin.Begin);

            int read = _stream.ReadAtLeast(buffer, _recordLength, throwOnEndOfStream: false);
            if (read < _recordLength)
                yield break; // truncated data region: stop rather than fabricate records.

            if (!includeDeleted && buffer[0] == 0x2A)
                continue; // deleted record, skipped by default.

            // Yield a record backed by a COPY so it stays valid as the buffer is reused.
            var copy = new byte[_recordLength];
            Array.Copy(buffer, copy, _recordLength);
            yield return new DbfRecord(this, copy);
        }
    }

    /// <summary>
    /// Random-access read of the record at <paramref name="index"/> (plan §A3): seek
    /// <c>HeaderLength + index * RecordLength</c> and read one record. Returns the
    /// <see cref="DbfRecord"/>, or <see langword="null"/> when that physical record is
    /// deleted. Throws <see cref="DbfNoColumnsDefinedException"/> when the table defines
    /// no columns (§A10).
    /// </summary>
    public DbfRecord? GetRecord(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Columns.Count == 0)
            throw new DbfNoColumnsDefinedException("The table defines no columns; record access is undefined.");

        if ((uint)index >= (uint)RecordCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        long pos = _headerLength + (long)index * _recordLength;
        _stream.Seek(pos, SeekOrigin.Begin);

        var buffer = new byte[_recordLength];
        int read = _stream.ReadAtLeast(buffer, _recordLength, throwOnEndOfStream: false);
        if (read < _recordLength)
            return null;

        if (buffer[0] == 0x2A)
            return null; // deleted record.

        return new DbfRecord(this, buffer);
    }

    /// <summary>
    /// CHEAP deletion-flag probe (plan §A3): seek <c>HeaderLength + index * RecordLength</c> and read
    /// ONLY the 1-byte leading deletion flag, WITHOUT materializing the whole record. Returns
    /// <see langword="true"/> when that physical record is marked deleted (<c>0x2A</c>), else
    /// <see langword="false"/> (including an out-of-range index or a truncated/short read). The
    /// count-only path uses this to honour SET DELETED ON over index candidates (the CDX can index
    /// deleted rows) without paying for a full-record read. Never allocates.
    /// </summary>
    public bool IsRecordDeleted(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)RecordCount) return false;

        _stream.Seek(_headerLength + (long)index * _recordLength, SeekOrigin.Begin);
        Span<byte> flag = stackalloc byte[1];
        int read = _stream.ReadAtLeast(flag, 1, throwOnEndOfStream: false);
        return read >= 1 && flag[0] == 0x2A;
    }

    /// <summary>
    /// Drop this read view's buffered bytes so the NEXT record/flag read fetches fresh from the OS —
    /// the cheap coexistence-freshness counterpart to re-opening the file. A <see cref="System.IO.FileStream"/>
    /// serves STALE buffered bytes after another handle (a sibling <see cref="Write.DbfWriter"/> on a
    /// <see cref="System.IO.FileShare.ReadWrite"/> open) writes IN PLACE to the same file; <c>Flush()</c> resets
    /// the managed read buffer so a following <see cref="GetRecord(int)"/> / <see cref="IsRecordDeleted(int)"/>
    /// re-reads the just-written bytes. Only the record DATA region is affected — an in-place UPDATE/DELETE
    /// leaves <see cref="RecordCount"/> (a header value fixed at open) unchanged, so this does NOT re-read it;
    /// an APPEND/PACK/truncate (which does change the geometry) still requires a full re-open. No-op for a
    /// memory-mapped view (its pages already reflect the shared file) or a non-seekable stream.
    /// </summary>
    /// <remarks>
    /// Used by the microVFP interpreter's 5.5 write hot-path: it keeps ONE read handle per work area open
    /// across statements (instead of re-opening it after every REPLACE/DELETE) and calls this to see the
    /// cached writer's flushed in-place edits, so a SCAN+REPLACE loop pays no per-row file-open cost.
    /// </remarks>
    public void RefreshView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is FileStream fs)
        {
            try { fs.Flush(); } catch { /* best-effort read-buffer reset */ }
        }
    }

    /// <summary>
    /// Produce a Rushmore EXPLAIN / ShowPlan (the VFP SYS(3054) equivalent) for
    /// <paramref name="filter"/>, auto-locating this table's sidecar structural <c>.cdx</c>
    /// (when opened from a path) to report which tags the optimizer would drive. Plan-ONLY —
    /// it does NOT run the residual scan — and never throws. When no <c>.cdx</c> is found the
    /// plan reports a full scan (nothing optimizable).
    /// </summary>
    public Query.QueryPlan ExplainQuery(string filter, Expressions.EvaluationContext? context = null)
        => ExplainQuery(filter, context, accelerator: null);

    /// <summary>
    /// As <see cref="ExplainQuery(string, Expressions.EvaluationContext?)"/>, but routed through an
    /// opt-in <paramref name="accelerator"/> (the Highlike sub-project) when one is supplied here or
    /// attached via <see cref="UseAccelerator(Query.IQueryAccelerator)"/>. With NO accelerator the Core
    /// <see cref="Query.QueryOptimizer.Explain"/> path runs unchanged.
    /// </summary>
    public Query.QueryPlan ExplainQuery(string filter, Expressions.EvaluationContext? context,
        Query.IQueryAccelerator? accelerator)
    {
        var acc = accelerator ?? _accelerator;
        Index.CdxFile? cdx = null;
        try
        {
            cdx = OpenStructuralCdx();
            return acc is not null
                ? acc.Explain(this, cdx, filter, context)
                : global::CrossVault.FoxDbf.Query.QueryOptimizer.Explain(this, cdx, filter, context);
        }
        catch
        {
            // Never throw from a diagnostic: fall back to a "nothing optimizable" plan.
            return new Query.QueryPlan(
                global::CrossVault.FoxDbf.Query.OptimizationLevel.None,
                Array.Empty<Query.ConditionPlan>(),
                Array.Empty<string>());
        }
        finally
        {
            cdx?.Dispose();
        }
    }

    /// <summary>
    /// Run a Rushmore-optimized FILTER query and return the matching 1-based record numbers
    /// (always the EXACT set a full table scan would return). Auto-locates this table's sidecar
    /// structural <c>.cdx</c> (when opened from a path) to drive index seeks. When an opt-in
    /// <paramref name="accelerator"/> is supplied here — or attached via
    /// <see cref="UseAccelerator(Query.IQueryAccelerator)"/> — execution is routed through it; otherwise the Core
    /// <see cref="Query.QueryOptimizer.FindRecords"/> path runs unchanged. The accelerator is a
    /// HINTS-only speed-up: the result set is identical either way (the seam's invariant). Never
    /// throws on a missing/unreadable index — it degrades to a correct full scan.
    /// </summary>
    public Query.QueryResult Query(string filter, Expressions.EvaluationContext? context = null,
        Query.IQueryAccelerator? accelerator = null)
    {
        var acc = accelerator ?? _accelerator;
        Index.CdxFile? cdx = null;
        try
        {
            cdx = OpenStructuralCdx();
            return acc is not null
                ? acc.FindRecords(this, cdx, filter, context)
                : global::CrossVault.FoxDbf.Query.QueryOptimizer.FindRecords(this, cdx, filter, context);
        }
        finally
        {
            cdx?.Dispose();
        }
    }

    /// <summary>
    /// Count the records matching <paramref name="filter"/> — the SAME number
    /// <see cref="Query(string, Expressions.EvaluationContext?, Query.IQueryAccelerator?)"/> would
    /// return as <c>RecordNumbers.Count</c>, computed more cheaply (no recno list, and for a fully
    /// index-resolvable filter no full-record reads). Auto-locates this table's sidecar structural
    /// <c>.cdx</c> to drive index seeks; routes through an opt-in <paramref name="accelerator"/> (here
    /// or attached via <see cref="UseAccelerator(Query.IQueryAccelerator)"/>) when present, else the Core
    /// <see cref="Query.QueryOptimizer.Count"/> path. Never throws — degrades to a correct full count.
    /// </summary>
    public int Count(string filter, Expressions.EvaluationContext? context = null,
        Query.IQueryAccelerator? accelerator = null)
    {
        var acc = accelerator ?? _accelerator;
        Index.CdxFile? cdx = null;
        try
        {
            cdx = OpenStructuralCdx();
            return acc is not null
                ? acc.Count(this, cdx, filter, context)
                : global::CrossVault.FoxDbf.Query.QueryOptimizer.Count(this, cdx, filter, context);
        }
        finally
        {
            cdx?.Dispose();
        }
    }

    /// <summary>
    /// Attach (or, with <see langword="null"/>, detach) an opt-in query
    /// <see cref="Query.IQueryAccelerator"/> — the single Core plug-in point for the Highlike
    /// sub-project. Returns <see langword="this"/> for fluent chaining. While attached,
    /// <see cref="Query"/> / <see cref="ExplainQuery(string, Expressions.EvaluationContext?, Query.IQueryAccelerator?)"/>
    /// route through it; detached, the Core optimizer path runs byte-for-byte unchanged.
    /// The caller retains ownership of accelerators attached through this public seam.
    /// </summary>
    public DbfTable UseAccelerator(Query.IQueryAccelerator? accelerator)
        => UseAccelerator(accelerator, owned: false);

    internal DbfTable UseAccelerator(Query.IQueryAccelerator? accelerator, bool owned)
    {
        if (ReferenceEquals(_accelerator, accelerator))
            return this; // A public same-reference reattach must not erase existing table ownership.

        var previous = _accelerator;
        bool disposePrevious = _ownsAccelerator;
        if (disposePrevious && previous is IDisposable disposable)
            disposable.Dispose();

        _accelerator = accelerator;
        _ownsAccelerator = owned;
        return this;
    }

    /// <summary>The opt-in query accelerator currently attached to this table, or null.</summary>
    public Query.IQueryAccelerator? Accelerator => _accelerator;

    /// <summary>
    /// The source <c>.dbf</c> path this table was opened from, or <see langword="null"/> for a
    /// stream-only open. Behaviour-neutral (read-only) — exposed so the opt-in accelerator
    /// sub-project can locate sidecar artifacts (e.g. the Highlike <c>.stx</c> statistics file)
    /// and read the staleness token. The Core optimizer never consults it.
    /// </summary>
    public string? SourcePath => _dbfPath;

    /// <summary>
    /// Open this table's sidecar structural <c>.cdx</c> (next to the <c>.dbf</c>) when the table
    /// was opened from a path and the file exists; else <see langword="null"/>. The caller owns
    /// and disposes the returned handle.
    /// </summary>
    private Index.CdxFile? OpenStructuralCdx()
    {
        if (_dbfPath is null) return null;
        string cdxPath = SidecarFile.ChangeExtension(_dbfPath, ".cdx");
        return System.IO.File.Exists(cdxPath) ? Index.CdxFile.Open(cdxPath, this, _readBackend, _driveKind) : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var accelerator = _accelerator;
        bool disposeAccelerator = _ownsAccelerator;
        _accelerator = null;
        _ownsAccelerator = false;
        if (disposeAccelerator && accelerator is IDisposable disposable)
            disposable.Dispose();
        // The sidecar memo file is owned by this table (§A6) — dispose it with us.
        _memo?.Dispose();
        if (!_leaveOpen)
            _stream.Dispose();
    }
}
