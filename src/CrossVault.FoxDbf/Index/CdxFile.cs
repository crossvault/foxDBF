using CrossVault.FoxDbf;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// A read-only compound index (<c>.cdx</c>) — plan §C1/§C2. Page 0 carries the
/// file header whose root points at the TAG-DIRECTORY node; the directory's keys
/// are 10-byte tag NAMES and each entry's "record number" is the BYTE OFFSET of
/// that tag's own header page. <see cref="TagNames"/> lists the tags and
/// <see cref="Tag(string)"/> resolves one to a <see cref="CdxTag"/>.
/// </summary>
/// <remarks>
/// An optional <see cref="DbfTable"/> supplies column types so a tag whose KEY is
/// a bare field name can decide its <see cref="CdxTag.IsCharacterKey"/> (the
/// compact-leaf pad byte). Never throws on a malformed index — surfaces an empty
/// tag list / a null tag instead.
/// </remarks>
public sealed class CdxFile : IDisposable
{
    private readonly IndexFile _index;
    private readonly bool _leaveOpen;
    private readonly DbfTable? _table;

    private readonly List<string> _tagNames = new();
    private readonly Dictionary<string, CdxTag> _tags =
        new(StringComparer.OrdinalIgnoreCase);

    private CdxFile(IndexFile index, DbfTable? table, bool leaveOpen)
    {
        _index = index;
        _table = table;
        // Tag names and KEY/FOR expressions are stored in the table's code page (VFP9: RAZÓN = 0xD3 in
        // CP1252, 0xE0 in CP850); without a table the library default code page decodes them.
        if (table is not null)
            _index.TextEncoding = table.Encoding;
        _leaveOpen = leaveOpen;
        try
        {
            BuildDirectory();
        }
        catch
        {
            // A parse throw must not leak the IndexFile/stream we just opened (we own it unless the
            // caller asked to leave it open).
            if (!_leaveOpen) _index.Dispose();
            throw;
        }
    }

    /// <summary>Opens a compound index from a <c>.cdx</c> path.</summary>
    /// <param name="path">Path to the <c>.cdx</c>.</param>
    /// <param name="table">Optional table for bare-field key-type resolution.</param>
    public static CdxFile Open(string path, DbfTable? table = null)
        => new(IndexFile.Open(path), table, leaveOpen: false);

    /// <summary>Opens a compound index over an existing seekable stream.</summary>
    public static CdxFile Open(Stream stream, DbfTable? table = null, bool leaveOpen = false)
        => new(IndexFile.Open(stream, leaveOpen), table, leaveOpen);

    /// <summary>
    /// Opens a compound index from a <c>.cdx</c> path with an explicit read
    /// <paramref name="backend"/> (Highlike Phase B-3). When <paramref name="backend"/> requests
    /// memory-mapping AND the file is on LOCAL/fixed storage, page reads are served from a mapped
    /// view; a network/UNC path or any mapping failure falls back to <see cref="System.IO.FileStream"/>
    /// (page reads stay byte-identical either way). Inspect <see cref="IsMemoryMapped"/> for the
    /// effective backend.
    /// </summary>
    public static CdxFile Open(string path, DbfTable? table, DbfReadBackend backend)
        => Open(path, table, backend, DbfDriveKind.Auto);

    /// <summary>
    /// As <see cref="Open(string, DbfTable?, DbfReadBackend)"/>, but with an explicit
    /// <paramref name="driveKind"/> (Highlike Phase B-3). The table forwards its OWN resolved
    /// backend/drive-kind here so the structural <c>.cdx</c> rides the same opt-in: a forced
    /// <see cref="DbfDriveKind.Network"/> falls the CDX back to <see cref="System.IO.FileStream"/>
    /// in lock-step with the <c>.dbf</c>/<c>.fpt</c>. Resolution still happens inside
    /// <c>ReadBackendResolver</c>; page reads stay byte-identical either way.
    /// </summary>
    public static CdxFile Open(string path, DbfTable? table, DbfReadBackend backend, DbfDriveKind driveKind)
        => new(IndexFile.Open(path, backend, driveKind), table, leaveOpen: false);

    /// <summary>
    /// True when this index's page reads are served from a memory-mapped view rather than per-read
    /// <see cref="System.IO.FileStream"/> syscalls (Highlike Phase B-3). <see langword="false"/> on a
    /// network/UNC path or any mapping failure (the safe <see cref="System.IO.FileStream"/> path).
    /// </summary>
    public bool IsMemoryMapped => _index.IsMemoryMapped;

    /// <summary>The underlying page reader.</summary>
    public IndexFile Index => _index;

    /// <summary>
    /// The on-disk path this <c>.cdx</c> was opened from, or <see langword="null"/> when it was opened
    /// over a caller-supplied stream. A read-only identity hint (the Highlike warm cache folds it into
    /// its change-token / slot key so a different — or rewritten — index is never served stale recnos).
    /// </summary>
    public string? SourcePath => _index.SourcePath;

    /// <summary>The names of every tag in the directory, in directory order.</summary>
    public IReadOnlyList<string> TagNames => _tagNames;

    /// <summary>Resolves a tag by name (case-insensitive); null when unknown.</summary>
    public CdxTag? Tag(string name)
        => name is not null && _tags.TryGetValue(name.Trim(), out var tag) ? tag : null;

    /// <summary>Indexer form of <see cref="Tag(string)"/>.</summary>
    public CdxTag? this[string name] => Tag(name);

    /// <summary>
    /// Decodes the tag directory: page-0 root → compact directory node(s) whose
    /// 10-byte character keys are tag names and whose "recno" is the byte offset of
    /// that tag's header. Each header is then parsed into a <see cref="CdxTag"/>.
    /// Wholly defensive — any failure leaves the directory empty.
    /// </summary>
    private void BuildDirectory()
    {
        var file = _index.ReadCdxHeader(0);
        int dirKeyLength = file.KeyLength; // directory keys are 10-byte tag names

        foreach (var (headerOffset, nameBytes) in
                 IndexTraversal.EnumerateCompact(_index, file.Root, dirKeyLength, isCharacter: true))
        {
            string name = TrimName(nameBytes, _index.TextEncoding);
            if (name.Length == 0 || _tags.ContainsKey(name))
                continue;

            var hdr = _index.ReadCdxHeader(headerOffset);

            // §6.6 fail-closed: a truncated / corrupt tag header returns default(CdxHeader), whose
            // KeyExpression is null (its KEY/FOR pool ran past EOF — see IndexFile.ReadCdxHeader). Treat that
            // ONE tag as genuinely ABSENT (Tag(name) → null) and keep parsing the HEALTHY sibling tags, rather
            // than building a CdxTag from a null expression (which would NRE in ResolveIsCharacterKey and, via
            // the ctor catch, rethrow — aborting the whole index open and violating this class's "never throws
            // on a malformed index" contract).
            if (hdr.KeyExpression is null)
                continue;

            bool isChar = ResolveIsCharacterKey(hdr.KeyExpression);
            var keyType = IndexKey.ResolveType(hdr.KeyExpression, _table);

            var tag = new CdxTag(
                _index,
                name,
                hdr.KeyLength,
                hdr.KeyExpression,
                hdr.ForExpression,
                hdr.Descending,
                hdr.SortOrder,
                hdr.Root,
                isChar,
                keyType,
                hdr.IsUnique);

            _tagNames.Add(name);
            _tags[name] = tag;
        }
    }

    /// <summary>
    /// Decides the compact-leaf pad byte for a tag: a CHARACTER key pads with
    /// <c>0x20</c>, everything else with <c>0x00</c>. When the KEY expression is a
    /// bare field name and a <see cref="DbfTable"/> was supplied, the column type
    /// decides.
    /// <para>
    /// With NO schema we default to <c>false</c> (pad <c>0x00</c>) — the
    /// ORDER-PRESERVING default: <c>0x00</c> is the true on-disk trailing pad for
    /// every numeric/date/datetime/integer key, so their transformed key bytes stay
    /// byte-exact, and character keys still ENUMERATE in correct order because
    /// <c>0x00</c> sorts below printable ASCII. (Byte-exact CHARACTER keys — which
    /// pad with <c>0x20</c> — therefore still require a <see cref="DbfTable"/>
    /// schema to be resolved.) Padding an unknown key with <c>0x20</c> would instead
    /// corrupt the value of any numeric key whose transform has trailing zero bytes.
    /// </para>
    /// </summary>
    private bool ResolveIsCharacterKey(string keyExpression)
    {
        // Null-guard the expression too (defense in depth, mirroring IndexKey.ResolveType): a fail-closed
        // truncated tag carries a null KeyExpression, and Trim() on it would NRE.
        if (_table is null || keyExpression is null)
            return false;

        string field = keyExpression.Trim();
        if (field.Length == 0)
            return true;

        foreach (var col in _table.Columns)
        {
            if (string.Equals(col.Name, field, StringComparison.OrdinalIgnoreCase))
                return col.Type is 'C' or 'V';
        }

        // Not a bare field name (composite expression, function, …): default char.
        return true;
    }

    private static string TrimName(byte[] raw, System.Text.Encoding? encoding)
    {
        int len = raw.Length;
        while (len > 0 && (raw[len - 1] == 0x20 || raw[len - 1] == 0x00))
            len--;
        return CodePageText.Decode(raw.AsSpan(0, len), encoding);
    }

    public void Dispose()
    {
        // The optional DbfTable is owned by the caller, never by us.
        if (!_leaveOpen)
            _index.Dispose();
    }
}
