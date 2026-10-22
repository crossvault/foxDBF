using System.Buffers.Binary;
using System.Text;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// One member-table definition for <see cref="DbfDatabaseBuilder"/> (plan §D6): the logical
/// table name carried in the DBC <c>OBJECTNAME</c> (<see cref="TableName"/>), the relative
/// <c>.dbf</c> file name written into both the DBC Table <c>PROPERTY</c> path blob and the
/// member's 263-byte backlink (<see cref="FileName"/>), the on-disk <see cref="Columns"/>, and
/// the optional <see cref="LongFieldNames"/> exposed as the DBC Field <c>OBJECTNAME</c>s (when
/// <see langword="null"/> the physical 10-char descriptor names are used).
/// </summary>
public sealed record DbcTableSpec
{
    /// <summary>The logical table name (the DBC Table record's <c>OBJECTNAME</c>); may differ from <see cref="FileName"/>.</summary>
    public string TableName { get; init; }

    /// <summary>The relative <c>.dbf</c> file name (e.g. <c>cust.dbf</c>) embedded in the PROPERTY path blob and the member backlink.</summary>
    public string FileName { get; init; }

    /// <summary>The member table's physical columns (passed verbatim to <see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>).</summary>
    public IReadOnlyList<DbfColumnDef> Columns { get; init; }

    /// <summary>Optional long field names (one per column, in physical order) written as the DBC Field <c>OBJECTNAME</c>s; <see langword="null"/> uses the descriptor names.</summary>
    public IReadOnlyList<string>? LongFieldNames { get; init; }

    /// <summary>Create a member-table spec.</summary>
    public DbcTableSpec(string tableName, string fileName, IReadOnlyList<DbfColumnDef> columns,
        IReadOnlyList<string>? longFieldNames = null)
    {
        TableName = tableName;
        FileName = fileName;
        Columns = columns;
        LongFieldNames = longFieldNames;
    }
}

/// <summary>
/// CREATE-time options for <see cref="DbfDatabaseBuilder.Create(string, System.Collections.Generic.IEnumerable{DbcTableSpec}, DbcCreateOptions)"/>
/// (plan §D6): the code-page byte for the <c>.dbc</c> header (default <c>0x03</c> = CP1252), the
/// <c>.DCT</c> memo block size (default 64), and an <see cref="Overwrite"/> guard.
/// </summary>
public sealed record DbcCreateOptions
{
    /// <summary>The code-page byte written into the <c>.dbc</c> (and each member <c>.dbf</c>) header. Default <c>0x03</c>.</summary>
    public byte CodePage { get; init; } = 0x03;

    /// <summary>The <c>.DCT</c> memo block size (big-endian u16 @6). Default 64 (the VFP9 default).</summary>
    public int MemoBlockSize { get; init; } = 64;

    /// <summary>When <see langword="false"/> (default) refuses to clobber an existing <c>.dbc</c>; set true to replace.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>
/// §D6 — CREATE a Visual FoxPro database container (<c>.dbc</c> + <c>.DCT</c> + <c>.DCX</c>) with
/// member tables and their backlinks, structurally matching a VFP9-generated container. The
/// <c>.dbc</c> is itself a v<c>0x30</c> DBF (memo extension <c>.DCT</c>, not <c>.fpt</c>) carrying
/// the meta columns OBJECTID/PARENTID/OBJECTTYPE/OBJECTNAME/PROPERTY/CODE/RIINFO/USER, the five
/// Database root records, and a Table + per-column Field record tree for every member.
/// </summary>
public static class DbfDatabaseBuilder
{
    /// <summary>
    /// Build the container at <paramref name="dbcPath"/> with the given member <paramref name="tables"/>
    /// (plan §D6). Creates each member <c>.dbf</c> via <see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>
    /// with the <c>.dbc</c> backlink, writes the <c>.dbc</c>/<c>.DCT</c> record tree, and builds the
    /// <c>.DCX</c> structural index.
    /// </summary>
    public static void Create(string dbcPath, IEnumerable<DbcTableSpec> tables, DbcCreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dbcPath);
        ArgumentNullException.ThrowIfNull(tables);
        options ??= new DbcCreateOptions();

        var specs = tables.ToList();

        // The container's text encoding (resolved from the code-page byte exactly as DbfTable does).
        // The Table PROPERTY path blob MUST be encoded with this — the reader decodes it back with the
        // SAME container encoding (DbfDatabase.ScanDbfPath), so a non-ASCII member name (e.g. a CP1252
        // "größe.dbf") round-trips byte-for-byte instead of being mangled to '?' by ASCII and then
        // failing to match the on-disk file.
        Encoding enc = Encodings.ResolveEncoding(options.CodePage);

        string full = Path.GetFullPath(dbcPath);
        string dir = Path.GetDirectoryName(full) is { Length: > 0 } d ? d : ".";
        string dbcName = Path.GetFileName(full); // the relative container name written into each backlink

        // (1) The .dbc IS a v0x30 DBF whose memo is .DCT. Create it with the eight meta columns.
        // The PROPERTY/CODE/USER memo columns force DbfWriter.Create to emit a companion memo file
        // (initially a .fpt — renamed to .DCT below) with the VFP-default 512-byte (8×64) header,
        // so the first PROPERTY block lands at block 8 exactly as the VFP9 reference does.
        var metaColumns = new[]
        {
            new DbfColumnDef("OBJECTID", 'I', 4, binary: true),
            new DbfColumnDef("PARENTID", 'I', 4, binary: true),
            new DbfColumnDef("OBJECTTYPE", 'C', 10),
            new DbfColumnDef("OBJECTNAME", 'C', 128),
            new DbfColumnDef("PROPERTY", 'M', 4, binary: true),
            new DbfColumnDef("CODE", 'M', 4, binary: true),
            new DbfColumnDef("RIINFO", 'C', 6),
            new DbfColumnDef("USER", 'M', 4),
        };

        var dbcCreateOptions = new DbfCreateOptions
        {
            CodePage = options.CodePage,
            MemoBlockSize = options.MemoBlockSize,
            Overwrite = options.Overwrite,
            // The .dbc itself is the root container — its backlink stays all-zero.
        };

        // CreateNew semantics: refuses to clobber an existing .dbc unless Overwrite is set (§D6).
        using (var dbc = DbfWriter.Create(full, metaColumns, dbcCreateOptions))
        {
            int nextId = 1;

            // (2) The five standard Database root records (OBJECTID 1..5, all PARENTID 1).
            int databaseId = nextId++;
            dbc.AppendRecord(databaseId, 1, "Database", "Database", DatabaseRootPropertyBlob(), null, null, null);
            dbc.AppendRecord(nextId++, 1, "Database", "TransactionLog", null, null, null, null);
            dbc.AppendRecord(nextId++, 1, "Database", "StoredProceduresSource", null, null, null, null);
            dbc.AppendRecord(nextId++, 1, "Database", "StoredProceduresObject", null, null, null, null);
            dbc.AppendRecord(nextId++, 1, "Database", "StoredProceduresDependencies", null, null, null, null);

            // (3) Per member table: create the member .dbf with the container backlink, then add a
            // Table record (PROPERTY = the path blob) followed by one Field record per column.
            foreach (var spec in specs)
            {
                string memberPath = Path.Combine(dir, spec.FileName);
                var memberOptions = new DbfCreateOptions
                {
                    CodePage = options.CodePage,
                    Overwrite = options.Overwrite,
                    BacklinkPath = dbcName, // the 263-byte backlink = the relative .dbc name
                };
                using (DbfWriter.Create(memberPath, spec.Columns, memberOptions)) { /* empty member */ }

                int tableId = nextId++;
                dbc.AppendRecord(tableId, 1, "Table", spec.TableName, TablePropertyBlob(spec.FileName, enc), null, null, null);

                var longNames = spec.LongFieldNames;
                for (int i = 0; i < spec.Columns.Count; i++)
                {
                    string fieldName = (longNames is not null && i < longNames.Count && !string.IsNullOrWhiteSpace(longNames[i]))
                        ? longNames[i]
                        : spec.Columns[i].Name;
                    dbc.AppendRecord(nextId++, tableId, "Field", fieldName, null, null, null, null);
                }
            }
        }

        // The .dbc's memo extension is .DCT (NOT .fpt): rename the sidecar DbfWriter just produced so
        // the reader (and a real VFP9 runtime) discovers it. The PROPERTY blobs already live inside it.
        string fptPath = SidecarFile.ChangeExtension(full, ".fpt");
        string dctPath = Path.ChangeExtension(full, ".DCT");
        if (File.Exists(fptPath))
            File.Move(fptPath, dctPath, overwrite: true);

        // Advertise the .dbc in its header flag byte (memo 0x02 | structural-index 0x01 | dbc 0x04 = 0x07),
        // matching the VFP9 reference. Purely cosmetic for our reader, but byte-faithful.
        SetDbcHeaderFlag(full);

        // (4) Build the .DCX structural index replicating the VFP9 reference tag set.
        BuildStructuralDcx(full);
    }

    /// <summary>The Database root record's 11-byte PROPERTY blob (block 8 of the reference .DCT, §D6).</summary>
    private static byte[] DatabaseRootPropertyBlob() =>
        new byte[] { 0x0b, 0x00, 0x00, 0x00, 0x01, 0x00, 0x18, 0x00, 0x00, 0x00, 0x0a };

    /// <summary>
    /// Build a Table record's PROPERTY path blob for <paramref name="relativeDbfPath"/> (§D6):
    /// <c>08 00 00 00 | 01 00 | 02 01 | &lt;u32 = blobLen-8&gt; | 01 00 01 | &lt;path ASCII&gt; 00</c>.
    /// </summary>
    private static byte[] TablePropertyBlob(string relativeDbfPath, Encoding encoding)
    {
        byte[] path = encoding.GetBytes(relativeDbfPath);
        int totalLen = 8 /* 08000000 0100 0201 */ + 4 /* u32 */ + 3 /* 010001 */ + path.Length + 1 /* NUL */;
        var blob = new byte[totalLen];
        blob[0] = 0x08; blob[1] = 0x00; blob[2] = 0x00; blob[3] = 0x00;
        blob[4] = 0x01; blob[5] = 0x00;
        blob[6] = 0x02; blob[7] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8, 4), (uint)(totalLen - 8));
        blob[12] = 0x01; blob[13] = 0x00; blob[14] = 0x01;
        path.CopyTo(blob.AsSpan(15));
        blob[^1] = 0x00;
        return blob;
    }

    /// <summary>Set the VFP container flags (memo|structural-cdx|dbc = 0x07) in header byte 28.</summary>
    private static void SetDbcHeaderFlag(string dbcPath)
    {
        try
        {
            using var fs = new FileStream(dbcPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Seek(28, SeekOrigin.Begin);
            int current = fs.ReadByte();
            if (current < 0)
                return;
            fs.Seek(28, SeekOrigin.Begin);
            fs.WriteByte((byte)(current | 0x05)); // OR in structural-cdx (0x01) + dbc (0x04)
        }
        catch
        {
            // Best-effort header cosmetic; the container is fully usable without it.
        }
    }

    /// <summary>
    /// Build the <c>.DCX</c> structural index over the freshly written <c>.dbc</c>, replicating the
    /// VFP9 reference tag set (§D6): an <c>OBJECTTYPE</c> tag keyed on
    /// <c>str(parentid)+objecttype</c> and an <c>OBJECTNAME</c> tag keyed on
    /// <c>str(parentid)+objecttype+lower(objectname)</c>, both filtered <c>!DELETED()</c>.
    /// </summary>
    private static void BuildStructuralDcx(string dbcPath)
    {
        string dcxPath = Path.ChangeExtension(dbcPath, ".DCX");

        // Order matters: VFP lays the tag B-trees down in CREATION order (OBJECTNAME first, then
        // OBJECTTYPE — verified against vfp_test/dbctest/ref.DCX, where OBJECTNAME's tree sits at the
        // earlier page). The tag DIRECTORY is name-sorted independently, so DBC reads are unaffected;
        // this order only pins the physical page layout to be byte-identical to VFP9.
        var tags = new[]
        {
            new CdxTagDefinition("OBJECTNAME", "str(parentid)+objecttype+lower(objectname)", "!DELETED()"),
            new CdxTagDefinition("OBJECTTYPE", "str(parentid)+objecttype", "!DELETED()"),
        };

        using var table = DbfTable.Open(dbcPath);
        var rows = new List<CdxIndexBuilder.BuildRow>();
        int recNo = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
            rows.Add(new CdxIndexBuilder.BuildRow(++recNo, rec));

        CdxIndexBuilder.Build(dcxPath, table, rows, tags);
    }
}
