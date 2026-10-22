using System.Buffers.Binary;
using System.Linq;
using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// A Visual FoxPro database container (<c>.dbc</c>, plan §A8). A <c>.dbc</c> is itself
/// a <c>.dbf</c> (version <c>0x30</c>) whose memo file is <c>.DCT</c> (NOT <c>.fpt</c>) —
/// it MUST be opened together with its sibling <c>.DCT</c> or the <c>PROPERTY</c> memo
/// (which carries each member's physical <c>.dbf</c> path) reads empty.
/// </summary>
/// <remarks>
/// Walks the DBC object tree: <c>OBJECTTYPE=="Table"</c> records become table nodes
/// keyed by <c>OBJECTID</c> with their <c>OBJECTNAME</c>; <c>OBJECTTYPE=="Field"</c>
/// records append <c>OBJECTNAME</c> to their parent table's long-field-name list (in
/// physical column order, via the <c>PARENTID</c> tree). <see cref="OpenTable(string)"/>
/// resolves the member's physical <c>.dbf</c> path from the matching Table record's
/// <c>PROPERTY</c> memo (scanned as RAW bytes for the first printable run ending in
/// <c>.dbf</c>), resolved against the DBC directory case-insensitively, then applies
/// the long field names PER COLUMN with the hidden <c>_NullFlags</c> system column set
/// aside first (the DBC Field tree has no entry for it — §A8 M2).
/// </remarks>
public sealed partial class DbfDatabase : IDisposable
{
    /// <summary>One <c>OBJECTTYPE=="Table"</c> node parsed from the DBC object tree.</summary>
    private sealed class TableNode
    {
        public int ObjectId;
        public string ObjectName = "";
        /// <summary>Relative <c>.dbf</c> path scanned out of the table's <c>PROPERTY</c> memo (§A8).</summary>
        public string? PropertyPath;
        /// <summary>Optional <c>U_XCASE</c> basename (opt-in xCase artifact fallback, §A8).</summary>
        public string? XCaseBase;
        /// <summary>Long field names from the Field records, in physical column order.</summary>
        public List<string> LongFieldNames = new();
        /// <summary>The Table record's <c>PROPERTY</c> memo block number (0 = none), for §A8/P3b rule decoding.</summary>
        public int PropertyBlock;
        /// <summary>Each child Field record's <c>PROPERTY</c> memo block number, parallel to <see cref="LongFieldNames"/> (§A8/P3b).</summary>
        public List<int> FieldPropertyBlocks = new();
    }

    private readonly DbfTable _dbc;

    /// <summary>The container's own code page — the encoding its object names (table names) decode with.</summary>
    internal System.Text.Encoding ContainerEncoding => _dbc.Encoding;
    private readonly DbfDatabaseOptions _options;
    private readonly string _dbcDir;
    private readonly List<TableNode> _tables = new();
    private readonly List<string> _tableNames = new();
    private readonly Dictionary<string, TableNode> _byName = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private DbfDatabase(string dbcPath, DbfTable dbc, DbfDatabaseOptions options)
    {
        _dbc = dbc;
        _options = options;
        _dbcDir = Path.GetDirectoryName(Path.GetFullPath(dbcPath)) ?? ".";
        BuildTree();
    }

    /// <summary>
    /// Open a Visual FoxPro <c>.dbc</c> container together with its sibling <c>.DCT</c>
    /// memo (plan §A8) using default <see cref="DbfDatabaseOptions"/>.
    /// </summary>
    public static DbfDatabase OpenFoxpro(string dbcPath)
        => OpenFoxpro(dbcPath, new DbfDatabaseOptions());

    /// <summary>
    /// Open a Visual FoxPro <c>.dbc</c> container together with its sibling <c>.DCT</c>
    /// memo (plan §A8) with explicit <paramref name="options"/>.
    /// </summary>
    public static DbfDatabase OpenFoxpro(string dbcPath, DbfDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(dbcPath))
            throw new DbfFileNotFoundException($"DBC file not found: {dbcPath}");

        // Critical (§A8): open the .dbc as a DBF table WITH its sibling .DCT memo. The
        // memo extension for a .dbc is .DCT (not .fpt); MemoFile auto-discovers it from
        // the .dbc extension. Without it every PROPERTY memo reads empty and the member
        // file paths are lost.
        var dbc = DbfTable.Open(dbcPath);
        try
        {
            return new DbfDatabase(dbcPath, dbc, options);
        }
        catch
        {
            dbc.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The logical table names of all <c>OBJECTTYPE=="Table"</c> entries in the DBC
    /// (their <c>OBJECTNAME</c>s, §A8). For a real-world production DBC this contained 139 names.
    /// </summary>
    public IReadOnlyList<string> TableNames
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tableNames;
        }
    }

    /// <summary>
    /// Open the member table named <paramref name="name"/> (its DBC <c>OBJECTNAME</c>)
    /// as a <see cref="DbfTable"/> with the DBC long field names applied per column (§A8).
    /// The physical file is resolved from the Table record's <c>PROPERTY</c> memo
    /// (RAW-byte path scan), with the §A8 fallback chain.
    /// </summary>
    public DbfTable OpenTable(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);

        if (!_byName.TryGetValue(name, out var node))
            throw new DbfFileNotFoundException($"No table named '{name}' in the database container.");

        string? path = ResolveMemberPath(node);
        if (path is null)
            throw new DbfFileNotFoundException(
                $"Could not resolve a physical .dbf for table '{node.ObjectName}' " +
                $"(PROPERTY='{node.PropertyPath}') under '{_dbcDir}'.");

        // Layer the DBC long field names onto the PHYSICAL column list as a pre-construction
        // transform so the bitmap and public Columns stay reference-consistent (§A8).
        var longNames = node.LongFieldNames;
        return DbfTable.Open(path, _options.TableOptions, cols => ApplyLongNames(cols, longNames));
    }

    /// <summary>
    /// Resolve the physical <c>.dbf</c> path of member <paramref name="name"/> (its DBC
    /// <c>OBJECTNAME</c>) WITHOUT opening it — the same §A8 resolution <see cref="OpenTable(string)"/>
    /// uses. Returns <see langword="null"/> when the member is unknown or its file cannot be resolved.
    /// Used by the ADO.NET copy-on-write transaction to redirect a DBC member's reads to its private
    /// working copy without first opening the live file.
    /// </summary>
    public string? GetTablePath(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out var node) ? ResolveMemberPath(node) : null;
    }

    /// <summary>
    /// Open member <paramref name="name"/> from an EXPLICIT physical <paramref name="path"/> (e.g. a
    /// transaction's private working copy returned by <see cref="GetTablePath(string)"/> via the
    /// copy-on-write read redirect) with the DBC long field names applied per column (§A8), exactly as
    /// <see cref="OpenTable(string)"/> would for the live file.
    /// </summary>
    public DbfTable OpenTableAt(string name, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);

        if (!_byName.TryGetValue(name, out var node))
            throw new DbfFileNotFoundException($"No table named '{name}' in the database container.");

        var longNames = node.LongFieldNames;
        return DbfTable.Open(path, _options.TableOptions, cols => ApplyLongNames(cols, longNames));
    }

    /// <summary>Convenience indexer equivalent to <see cref="OpenTable(string)"/>.</summary>
    public DbfTable this[string name] => OpenTable(name);

    /// <summary>
    /// The container's stored-procedure SOURCE text, read from the <c>CODE</c> memo of the
    /// record whose <c>OBJECTNAME == "StoredProceduresSource"</c> (the authoritative location
    /// per the VFP <c>gendbc.prg</c> tool, §A8). Returns <see langword="null"/> when the
    /// database carries no such record or an empty CODE memo.
    /// </summary>
    /// <remarks>
    /// The <c>CODE</c> memo is flagged NOCPTRANS, so it decodes 1:1 via Latin1 (the raw
    /// source bytes) — matching VFP9 <c>COPY PROCEDURES TO</c> / <c>COPY MEMO Code TO</c>.
    /// The COMPILED object code lives in <c>StoredProceduresObject</c> and is NOT returned here.
    /// </remarks>
    public string? StoredProcedureSource
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var rec in _dbc.EnumerateAll(includeDeleted: false))
            {
                if (!string.Equals(rec["OBJECTNAME"] as string, "StoredProceduresSource", StringComparison.OrdinalIgnoreCase))
                    continue;

                // CODE decodes to a Latin1 string for this NOCPTRANS memo (DbfTable.ReadMemoValue).
                var code = rec["CODE"] as string;
                return string.IsNullOrEmpty(code) ? null : code;
            }
            return null;
        }
    }

    /// <summary>
    /// Apply the DBC long field names to a member table's PHYSICAL column list, PER COLUMN
    /// (plan §A8 M2). The hidden <c>_NullFlags</c>/system column (the DBC Field tree has no
    /// entry for it, so physical columns are typically <c>longNames.Count + 1</c>) is set
    /// aside first; the remaining columns are renamed positionally; a missing/empty long
    /// name falls back to the physical 10-char name (never throws). A whole-table count
    /// guard would wrongly drop ALL long names for the 137/139 nullable tables — hence the
    /// per-column approach.
    /// </summary>
    internal static IReadOnlyList<DbfColumn> ApplyLongNames(IReadOnlyList<DbfColumn> physical, IReadOnlyList<string> longNames)
    {
        var result = new List<DbfColumn>(physical.Count);
        int j = 0; // index into longNames, advanced ONLY for non-system columns.
        foreach (var c in physical)
        {
            // System/hidden columns (e.g. the trailing _NullFlags) have no Field-tree entry —
            // keep them verbatim and do NOT consume a long name (§A8 M2: set aside first).
            if (c.IsSystem)
            {
                result.Add(c);
                continue;
            }

            string name = (j < longNames.Count && !string.IsNullOrWhiteSpace(longNames[j]))
                ? longNames[j]
                : c.Name; // missing/empty long name → physical 10-char fallback (never empty).
            j++;

            result.Add(name == c.Name
                ? c
                : new DbfColumn(name, c.Type, c.Length, c.Decimal, c.Offset, c.Flags));
        }
        return result;
    }

    // ---- DBC object-tree walk --------------------------------------------------------

    private void BuildTree()
    {
        var propColumn = FindColumn("PROPERTY");
        bool wantXCase = _options.UseXCaseFallback && HasColumn("U_XCASE");

        // Field records may follow their parent Table record; buffer them keyed by PARENTID
        // and attach after the full pass so attachment is order-independent (§A8). Each entry
        // also carries the field's PROPERTY memo block (P3b) so its DEFAULT/RULE can be decoded.
        var fieldsByParent = new Dictionary<int, List<(string Name, int Block)>>();

        foreach (var rec in _dbc.EnumerateAll(includeDeleted: false))
        {
            string type = (rec["OBJECTTYPE"] as string) ?? "";

            if (string.Equals(type, "Table", StringComparison.OrdinalIgnoreCase))
            {
                var node = new TableNode
                {
                    ObjectId = ToInt(rec["OBJECTID"]),
                    ObjectName = (rec["OBJECTNAME"] as string) ?? "",
                    PropertyPath = ExtractPropertyPath(rec, propColumn),
                    PropertyBlock = ReadPropertyBlock(rec, propColumn),
                };
                if (wantXCase)
                    node.XCaseBase = BaseName(rec["U_XCASE"] as string);

                _tables.Add(node);
                _tableNames.Add(node.ObjectName);
                if (!_byName.ContainsKey(node.ObjectName))
                    _byName[node.ObjectName] = node;
            }
            else if (string.Equals(type, "Field", StringComparison.OrdinalIgnoreCase))
            {
                int parent = ToInt(rec["PARENTID"]);
                string field = (rec["OBJECTNAME"] as string) ?? "";
                int block = ReadPropertyBlock(rec, propColumn);
                if (!fieldsByParent.TryGetValue(parent, out var list))
                    fieldsByParent[parent] = list = new List<(string, int)>();
                list.Add((field, block));
            }
        }

        foreach (var node in _tables)
            if (fieldsByParent.TryGetValue(node.ObjectId, out var fields))
            {
                node.LongFieldNames = fields.Select(f => f.Name).ToList();
                node.FieldPropertyBlocks = fields.Select(f => f.Block).ToList();
            }
    }

    /// <summary>Read a record's <c>PROPERTY</c> memo BLOCK NUMBER (the 4-byte LE pointer), or 0 when unset (§A8/P3b).</summary>
    private static int ReadPropertyBlock(DbfRecord rec, DbfColumn propColumn)
    {
        var raw = rec.GetRawField(propColumn);
        if (raw.Length < 4)
            return 0;
        int block = BinaryPrimitives.ReadInt32LittleEndian(raw);
        return block > 0 ? block : 0;
    }

    /// <summary>
    /// Read the table record's <c>PROPERTY</c> memo as RAW bytes and scan for the first
    /// printable run ending in <c>.dbf</c> (§A8). A transcoding read would corrupt the
    /// binary property blob around the path, so the memo block is read verbatim.
    /// </summary>
    private string? ExtractPropertyPath(DbfRecord rec, DbfColumn propColumn)
    {
        var raw = rec.GetRawField(propColumn);
        if (raw.Length < 4)
            return null;

        // The .dbc is Visual FoxPro 0x30, so the memo pointer is a 4-byte LE block number.
        int block = BinaryPrimitives.ReadInt32LittleEndian(raw);
        if (block <= 0)
            return null;

        var bytes = _dbc.Memo?.ReadBytes(block);
        if (bytes is null)
            return null;

        return ScanDbfPath(bytes, _dbc.Encoding);
    }

    /// <summary>
    /// Find the first maximal printable run that contains <c>.dbf</c> and return the
    /// substring up to and including <c>.dbf</c> (case-insensitive) — i.e. the first
    /// printable run ending in <c>.dbf</c> (§A8, validated 139/139 on a real-world production database).
    /// </summary>
    /// <remarks>
    /// "Printable" includes HIGH bytes (≥ 0x80): VFP stores PROPERTY paths in the container's
    /// OEM code page, so a path component such as <c>tabellen\größe</c> contains <c>0xF6</c>/
    /// <c>0xDF</c>. Treating those as run-breakers (the old ASCII-only bound) split the run and
    /// could yield a truncated/wrong path. The matched run is decoded with the container's
    /// <paramref name="encoding"/> (NOT <see cref="System.Text.Encoding.ASCII"/>, which maps
    /// 0x80–0xFF to '?' and would break the on-disk file lookup).
    /// </remarks>
    internal static string? ScanDbfPath(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        int i = 0;
        int n = bytes.Length;
        while (i < n)
        {
            if (!IsPrintable(bytes[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < n && IsPrintable(bytes[i]))
                i++;

            var run = bytes.Slice(start, i - start);
            int idx = IndexOfDbf(run);
            if (idx >= 0)
                return encoding.GetString(run.Slice(0, idx + 4));
        }
        return null;
    }

    private static int IndexOfDbf(ReadOnlySpan<byte> run)
    {
        for (int j = 0; j + 4 <= run.Length; j++)
        {
            if (run[j] == (byte)'.' &&
                (run[j + 1] | 0x20) == 'd' &&
                (run[j + 2] | 0x20) == 'b' &&
                (run[j + 3] | 0x20) == 'f')
                return j;
        }
        return -1;
    }

    // Printable for the path-run scan: any byte ≥ 0x20 except DEL (0x7f). High bytes
    // (≥ 0x80) are KEPT so an OEM-code-page umlaut in a member path does not split the run.
    private static bool IsPrintable(byte b) => b >= 0x20 && b != 0x7f;

    // ---- member-file resolution (§A8 fallback chain) ---------------------------------

    private string? ResolveMemberPath(TableNode node)
    {
        // Primary (standard VFP, validated 139/139): the relative .dbf path from PROPERTY,
        // resolved against the DBC directory case-insensitively.
        if (!string.IsNullOrEmpty(node.PropertyPath))
        {
            var byProperty = ResolveRelative(node.PropertyPath);
            if (byProperty is not null)
                return byProperty;
        }

        // Fallback (1): OBJECTNAME + ".dbf" in the DBC directory.
        var byName = ResolveRelative(node.ObjectName + ".dbf");
        if (byName is not null)
            return byName;

        // Fallback (2): a user-supplied resolver hook.
        if (_options.TableResolver is not null)
        {
            var info = new DbcTableInfo
            {
                ObjectName = node.ObjectName,
                DatabaseDirectory = _dbcDir,
                PropertyPath = node.PropertyPath,
                LongFieldNames = node.LongFieldNames,
            };
            var hook = _options.TableResolver(info);
            if (!string.IsNullOrEmpty(hook))
            {
                if (Path.IsPathRooted(hook))
                {
                    if (File.Exists(hook))
                        return hook;
                }
                else
                {
                    var resolved = ResolveRelative(hook);
                    if (resolved is not null)
                        return resolved;
                }
            }
        }

        // Fallback (3): opt-in U_XCASE custom field (xCase artifact, not a VFP standard).
        if (_options.UseXCaseFallback && !string.IsNullOrEmpty(node.XCaseBase))
        {
            var byXCase = ResolveRelative(node.XCaseBase + ".dbf");
            if (byXCase is not null)
                return byXCase;
        }

        return null;
    }

    /// <summary>Resolve a relative member path against the DBC directory, case-insensitively (§A8).</summary>
    private string? ResolveRelative(string relativePath)
    {
        string normalized = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        // Fast path: on a case-insensitive filesystem (Windows) the direct combine wins
        // even for a case-mismatched name (wae.dbf → WAE.DBF).
        string combined = Path.Combine(_dbcDir, normalized);
        if (File.Exists(combined))
            return combined;

        // Portable case-insensitive walk for case-sensitive filesystems.
        return ResolveCaseInsensitive(_dbcDir, normalized);
    }

    private static string? ResolveCaseInsensitive(string baseDir, string relativePath)
    {
        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        string current = baseDir;
        for (int s = 0; s < segments.Length; s++)
        {
            if (!Directory.Exists(current))
                return null;

            bool last = s == segments.Length - 1;
            string? match = null;
            var entries = last
                ? Directory.EnumerateFileSystemEntries(current)
                : Directory.EnumerateDirectories(current);
            foreach (var entry in entries)
            {
                if (string.Equals(Path.GetFileName(entry), segments[s], StringComparison.OrdinalIgnoreCase))
                {
                    match = entry;
                    break;
                }
            }
            if (match is null)
                return null;
            current = match;
        }

        return File.Exists(current) ? current : null;
    }

    // ---- small helpers ---------------------------------------------------------------

    private DbfColumn FindColumn(string name)
    {
        foreach (var c in _dbc.Columns)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                return c;
        throw new DbfColumnNameException($"The .dbc is missing the required '{name}' column (not a valid VFP database container?).");
    }

    private bool HasColumn(string name)
    {
        foreach (var c in _dbc.Columns)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        decimal m => (int)m,
        _ => 0,
    };

    /// <summary>Last path segment of a (possibly backslash/forward-slash) path, sans extension.</summary>
    private static string? BaseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string s = value.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0)
            s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot >= 0)
            s = s[..dot];
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The .dbc table (and its owned .DCT memo) is owned by this database. Member tables
        // returned by OpenTable are owned by the caller and disposed independently.
        _dbc.Dispose();
    }
}
