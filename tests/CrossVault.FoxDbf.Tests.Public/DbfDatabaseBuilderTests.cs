using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// §D6 — CREATE a Visual FoxPro database container (<c>.dbc</c> + <c>.DCT</c> + <c>.DCX</c>) with
/// member tables and backlinks (<see cref="DbfDatabaseBuilder"/>). The created container MUST be
/// structurally identical to a VFP9-generated one: the same v0x30 meta-column schema, the five
/// Database root records, a Table + per-column Field record tree per member, the exact PROPERTY
/// path blobs, the member backlinks, and a sibling <c>.DCT</c> + <c>.DCX</c>. Every expected value
/// is HARDCODED, reverse-engineered byte-for-byte from the VFP9 reference
/// <c>vfp_test/dbctest/ref.DBC</c> (+ <c>ref.DCT</c>, <c>ref.DCX</c>, <c>cust.DBF</c>, <c>ord.DBF</c>).
///
/// Validation is two-pronged: (A) ROUND-TRIP through <see cref="DbfDatabase.OpenFoxpro(string)"/>
/// (TableNames, OpenTable, long field names) and (B) BYTE/STRUCTURE compare of the record tree and
/// PROPERTY blobs against the reference. SAFETY: every test writes ONLY into a throwaway temp dir
/// (deleted in finally). The read-only reference under vfp_test/dbctest is opened for reading only.
/// </summary>
public sealed class DbfDatabaseBuilderTests
{
    // ---- reference-derived constants (HARDCODED from vfp_test/dbctest) ------------------

    // The Database root record PROPERTY blob (block 8 in ref.DCT, 11 bytes).
    private static readonly byte[] DatabasePropertyBlob =
        { 0x0b, 0x00, 0x00, 0x00, 0x01, 0x00, 0x18, 0x00, 0x00, 0x00, 0x0a };

    // The meta-column schema of the .dbc itself (name, type, length).
    private static readonly (string Name, char Type, int Length)[] MetaColumns =
    {
        ("OBJECTID",   'I', 4),
        ("PARENTID",   'I', 4),
        ("OBJECTTYPE", 'C', 10),
        ("OBJECTNAME", 'C', 128),
        ("PROPERTY",   'M', 4),
        ("CODE",       'M', 4),
        ("RIINFO",     'C', 6),
        ("USER",       'M', 4),
    };

    private static string RefDbc => Path.Combine(Fixtures.VfpTestDir, "dbctest", "ref.DBC");

    // ---- temp-dir helpers --------------------------------------------------------------

    private static string FreshTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_dbc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Nuke(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // The two reference member tables (exact column structure of cust.DBF / ord.DBF).
    private static DbcTableSpec CustSpec(IReadOnlyList<string>? longNames = null) => new(
        tableName: "cust",
        fileName: "cust.dbf",
        columns: new[]
        {
            new DbfColumnDef("CUST_ID", 'I', 4),
            new DbfColumnDef("COMPANY", 'C', 30),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
        },
        longFieldNames: longNames);

    private static DbcTableSpec OrdSpec() => new(
        tableName: "ord",
        fileName: "ord.dbf",
        columns: new[]
        {
            new DbfColumnDef("ORDER_ID", 'I', 4),
            new DbfColumnDef("CUST_ID", 'I', 4),
            new DbfColumnDef("ODATE", 'D', 8),
        });

    // ---- a tiny .dbc record reader (raw, for the byte/structure compare) ----------------

    private readonly record struct DbcRow(int ObjectId, int ParentId, string ObjectType, string ObjectName, byte[] PropertyBlob);

    /// <summary>
    /// Read every record of a <c>.dbc</c> (the meta columns + the raw PROPERTY memo blob) via the
    /// production reader stack (<see cref="DbfTable"/> + its <c>.DCT</c> memo). An empty PROPERTY
    /// memo (block ≤ 0) yields an empty blob.
    /// </summary>
    private static List<DbcRow> ReadDbcRows(string dbcPath)
    {
        using var t = DbfTable.Open(dbcPath);
        var propCol = t.Columns.First(c => c.Name == "PROPERTY");
        var rows = new List<DbcRow>();
        foreach (var rec in t.EnumerateAll(includeDeleted: false))
        {
            int objId = Convert.ToInt32(rec["OBJECTID"]);
            int parId = Convert.ToInt32(rec["PARENTID"]);
            string type = ((rec["OBJECTTYPE"] as string) ?? "").Trim();
            string name = ((rec["OBJECTNAME"] as string) ?? "").Trim();

            byte[] raw = rec.GetRawField(propCol).ToArray();
            int block = raw.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(raw) : 0;
            byte[] blob = block > 0 ? (t.Memo?.ReadBytes(block) ?? Array.Empty<byte>()) : Array.Empty<byte>();

            rows.Add(new DbcRow(objId, parId, type, name, blob));
        }
        return rows;
    }

    /// <summary>Build the expected VFP Table PROPERTY path blob for a relative .dbf path (plan §D6).</summary>
    private static byte[] ExpectedPathBlob(string relativeDbfPath)
    {
        // 08 00 00 00 | 01 00 | 02 01 | <u32 = blobLen-8> | 01 00 01 | <path ASCII> 00
        byte[] path = System.Text.Encoding.ASCII.GetBytes(relativeDbfPath);
        var bytes = new List<byte> { 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01 };
        int totalLen = 8 /*08000000 0100 0201*/ + 4 /*u32*/ + 3 /*010001*/ + path.Length + 1 /*NUL*/;
        uint u32 = (uint)(totalLen - 8);
        bytes.AddRange(BitConverter.GetBytes(u32)); // LE u32
        bytes.AddRange(new byte[] { 0x01, 0x00, 0x01 });
        bytes.AddRange(path);
        bytes.Add(0x00);
        return bytes.ToArray();
    }

    private static byte[] Backlink(string dbfPath)
    {
        byte[] image = File.ReadAllBytes(dbfPath);
        int headerLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(8, 2));
        // The 263-byte backlink is the last 263 bytes of the header region.
        return image.AsSpan(headerLen - 263, 263).ToArray();
    }

    private static string BacklinkString(string dbfPath)
    {
        byte[] bl = Backlink(dbfPath);
        int end = Array.IndexOf(bl, (byte)0);
        if (end < 0) end = bl.Length;
        return System.Text.Encoding.ASCII.GetString(bl, 0, end);
    }

    // =====================================================================================
    // (B) BYTE/STRUCTURE compare against the VFP9 reference vfp_test/dbctest/ref.DBC
    // =====================================================================================

    [Fact]
    public void Create_MetaColumnSchema_MatchesReference()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            using var created = DbfTable.Open(dbc);

            // v0x30 DBF (VFP, .DCT memo).
            byte[] raw = File.ReadAllBytes(dbc);
            Assert.Equal(0x30, raw[0]);

            // Exactly the 8 meta columns in order (the hidden _NullFlags is NOT present: no nullable column).
            var actual = created.Columns
                .Where(c => !c.IsSystem)
                .Select(c => (c.Name, c.Type, c.Length))
                .ToArray();
            Assert.Equal(MetaColumns.Length, actual.Length);
            for (int i = 0; i < MetaColumns.Length; i++)
            {
                Assert.Equal(MetaColumns[i].Name, actual[i].Name);
                Assert.Equal(MetaColumns[i].Type, actual[i].Type);
                Assert.Equal(MetaColumns[i].Length, actual[i].Length);
            }

            // And the reference carries the identical meta schema.
            using var refDbc = DbfTable.Open(RefDbc);
            var refCols = refDbc.Columns.Where(c => !c.IsSystem).Select(c => (c.Name, c.Type, c.Length)).ToArray();
            Assert.Equal(actual, refCols);
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void Create_RecordTree_MatchesReferenceSequence()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            var rows = ReadDbcRows(dbc);

            // 5 Database records + (Table + 3 Fields) per member table = 5 + 4 + 4 = 13.
            Assert.Equal(13, rows.Count);

            var expected = new (int Id, int Pid, string Type, string Name)[]
            {
                (1, 1, "Database", "Database"),
                (2, 1, "Database", "TransactionLog"),
                (3, 1, "Database", "StoredProceduresSource"),
                (4, 1, "Database", "StoredProceduresObject"),
                (5, 1, "Database", "StoredProceduresDependencies"),
                (6, 1, "Table", "cust"),
                (7, 6, "Field", "CUST_ID"),
                (8, 6, "Field", "COMPANY"),
                (9, 6, "Field", "AMOUNT"),
                (10, 1, "Table", "ord"),
                (11, 10, "Field", "ORDER_ID"),
                (12, 10, "Field", "CUST_ID"),
                (13, 10, "Field", "ODATE"),
            };

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Id, rows[i].ObjectId);
                Assert.Equal(expected[i].Pid, rows[i].ParentId);
                Assert.Equal(expected[i].Type, rows[i].ObjectType);
                Assert.Equal(expected[i].Name, rows[i].ObjectName, ignoreCase: true);
            }
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void Create_DatabaseRecord_PropertyBlob_Equals11ReferenceBytes()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            var rows = ReadDbcRows(dbc);
            Assert.Equal(DatabasePropertyBlob, rows[0].PropertyBlob);

            // Cross-check: the reference's first record carries the identical blob.
            var refRows = ReadDbcRows(RefDbc);
            Assert.Equal(DatabasePropertyBlob, refRows[0].PropertyBlob);
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void Create_TablePropertyBlob_EqualsExactExpectedBytes()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            var rows = ReadDbcRows(dbc);

            // The cust Table record is index 5 (6th record): blob = 24 bytes ending "cust.dbf\0".
            var custTable = rows[5];
            Assert.Equal("cust", custTable.ObjectName, ignoreCase: true);

            byte[] expectedCust =
            {
                0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01,
                0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01,
                (byte)'c', (byte)'u', (byte)'s', (byte)'t', (byte)'.', (byte)'d', (byte)'b', (byte)'f', 0x00,
            };
            Assert.Equal(expectedCust, custTable.PropertyBlob);
            Assert.Equal(expectedCust, ExpectedPathBlob("cust.dbf"));

            // The ord Table record is index 9: u32 = 0x0f, ends "ord.dbf\0".
            var ordTable = rows[9];
            Assert.Equal("ord", ordTable.ObjectName, ignoreCase: true);
            byte[] expectedOrd =
            {
                0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01,
                0x0f, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01,
                (byte)'o', (byte)'r', (byte)'d', (byte)'.', (byte)'d', (byte)'b', (byte)'f', 0x00,
            };
            Assert.Equal(expectedOrd, ordTable.PropertyBlob);

            // Byte-equality with the reference Table blobs.
            var refRows = ReadDbcRows(RefDbc);
            Assert.Equal(refRows[5].PropertyBlob, custTable.PropertyBlob);
            Assert.Equal(refRows[9].PropertyBlob, ordTable.PropertyBlob);
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void Create_FieldRecords_HaveEmptyProperty()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            var rows = ReadDbcRows(dbc);
            foreach (var r in rows.Where(r => r.ObjectType == "Field"))
                Assert.Empty(r.PropertyBlob);
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // (C) member .dbf 263-byte backlink == the relative .dbc name
    // =====================================================================================

    [Fact]
    public void Create_MemberBacklinks_EqualRelativeDbcName()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            // Each member must carry the relative container name in its 263-byte backlink.
            Assert.Equal("shop.dbc", BacklinkString(Path.Combine(dir, "cust.dbf")), ignoreCase: true);
            Assert.Equal("shop.dbc", BacklinkString(Path.Combine(dir, "ord.dbf")), ignoreCase: true);

            // The backlink slot is exactly 263 bytes.
            Assert.Equal(263, Backlink(Path.Combine(dir, "cust.dbf")).Length);
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // (D) sibling .DCT (holds the PROPERTY blobs) + .DCX exist
    // =====================================================================================

    [Fact]
    public void Create_WritesSiblingDctAndDcx()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            string dct = Path.Combine(dir, "shop.DCT");
            string dcx = Path.Combine(dir, "shop.DCX");

            // Extension discovery is case-insensitive on disk; assert via case-insensitive lookup.
            Assert.True(FileExistsCi(dct), "the .DCT memo sidecar must exist");
            Assert.True(FileExistsCi(dcx), "the .DCX structural index must exist");

            // The .DCT must actually contain the Database PROPERTY blob bytes.
            byte[] dctBytes = File.ReadAllBytes(ResolveCi(dct)!);
            Assert.True(IndexOf(dctBytes, DatabasePropertyBlob) >= 0,
                "the .DCT must hold the Database PROPERTY blob");
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // (A) ROUND-TRIP through DbfDatabase.OpenFoxpro
    // =====================================================================================

    [Fact]
    public void RoundTrip_TableNames_ListsBothMembers()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            using var db = DbfDatabase.OpenFoxpro(dbc);
            var names = db.TableNames.ToList();
            Assert.Equal(2, names.Count);
            Assert.Contains("cust", names);
            Assert.Contains("ord", names);
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void RoundTrip_OpenTable_ResolvesMemberAndExposesColumns()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            using var db = DbfDatabase.OpenFoxpro(dbc);

            using var cust = db.OpenTable("cust");
            var custCols = cust.Columns.Where(c => !c.IsSystem).Select(c => c.Name).ToArray();
            Assert.Equal(new[] { "CUST_ID", "COMPANY", "AMOUNT" }, custCols);

            using var ord = db.OpenTable("ord");
            var ordCols = ord.Columns.Where(c => !c.IsSystem).Select(c => c.Name).ToArray();
            Assert.Equal(new[] { "ORDER_ID", "CUST_ID", "ODATE" }, ordCols);
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void RoundTrip_LongFieldNames_AreExposedByReader()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            // The DBC Field OBJECTNAME may be LONGER than the 10-char physical descriptor name.
            var longNames = new[] { "customer_identifier", "company_long_name", "amount" };
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(longNames), OrdSpec() });

            using var db = DbfDatabase.OpenFoxpro(dbc);
            using var cust = db.OpenTable("cust");
            var names = cust.Columns.Where(c => !c.IsSystem).Select(c => c.Name).ToArray();
            Assert.Equal(longNames, names);
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // EDGE / ADVERSARIAL
    // =====================================================================================

    [Fact]
    public void EmptyDatabase_HasExactlyTheFiveDatabaseRecords()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "empty.dbc");
            DbfDatabaseBuilder.Create(dbc, Array.Empty<DbcTableSpec>());

            var rows = ReadDbcRows(dbc);
            Assert.Equal(5, rows.Count);

            var expected = new (int Id, int Pid, string Type, string Name)[]
            {
                (1, 1, "Database", "Database"),
                (2, 1, "Database", "TransactionLog"),
                (3, 1, "Database", "StoredProceduresSource"),
                (4, 1, "Database", "StoredProceduresObject"),
                (5, 1, "Database", "StoredProceduresDependencies"),
            };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Id, rows[i].ObjectId);
                Assert.Equal(expected[i].Pid, rows[i].ParentId);
                Assert.Equal(expected[i].Type, rows[i].ObjectType);
                Assert.Equal(expected[i].Name, rows[i].ObjectName);
            }
            Assert.Equal(DatabasePropertyBlob, rows[0].PropertyBlob);

            using var db = DbfDatabase.OpenFoxpro(dbc);
            Assert.Empty(db.TableNames);
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void TableNameDifferingFromFileName_IsHandled()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            // Logical name "customers" but the physical file is cust.dbf.
            var spec = new DbcTableSpec(
                tableName: "customers",
                fileName: "cust.dbf",
                columns: new[]
                {
                    new DbfColumnDef("CUST_ID", 'I', 4),
                    new DbfColumnDef("COMPANY", 'C', 30),
                    new DbfColumnDef("AMOUNT", 'N', 10, 2),
                });
            DbfDatabaseBuilder.Create(dbc, new[] { spec });

            var rows = ReadDbcRows(dbc);
            var tableRow = rows.Single(r => r.ObjectType == "Table");
            // The DBC OBJECTNAME is the LOGICAL name...
            Assert.Equal("customers", tableRow.ObjectName);
            // ...but the PROPERTY path blob points at the physical FILE name.
            Assert.Equal(ExpectedPathBlob("cust.dbf"), tableRow.PropertyBlob);

            // The physical file exists and its backlink is the dbc name.
            Assert.True(File.Exists(Path.Combine(dir, "cust.dbf")));
            Assert.Equal("shop.dbc", BacklinkString(Path.Combine(dir, "cust.dbf")), ignoreCase: true);

            // The reader resolves the member by its logical name.
            using var db = DbfDatabase.OpenFoxpro(dbc);
            Assert.Contains("customers", db.TableNames);
            using var t = db.OpenTable("customers");
            Assert.Equal(3, t.Columns.Count(c => !c.IsSystem));
        }
        finally { Nuke(dir); }
    }

    [Fact]
    public void Create_RefusesToClobber_WithoutOverwrite()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec() });

            // A second build at the same path without Overwrite must throw (CreateNew semantics).
            Assert.ThrowsAny<Exception>(() =>
                DbfDatabaseBuilder.Create(dbc, new[] { CustSpec() }));
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // (E) the .DCX tag set — re-read via the production CDX/structural reader
    // =====================================================================================

    /// <summary>
    /// Read every (tagName, header) pair of a compound index the way the production directory reader
    /// does (file header → compact directory, whose stored "recno" is the tag header byte offset),
    /// returning the parsed <see cref="CdxHeader"/> per tag so a test can assert key/FOR/signature.
    /// </summary>
    private static List<(string Name, CdxHeader Header)> ReadTagHeaders(string cdxPath)
    {
        using var idx = IndexFile.Open(cdxPath);
        var fileHdr = idx.ReadCdxHeader(0);
        var result = new List<(string, CdxHeader)>();
        foreach (var (headerOffset, nameBytes) in
                 IndexTraversal.EnumerateCompact(idx, fileHdr.Root, fileHdr.KeyLength, pad: 0x20))
        {
            string name = System.Text.Encoding.ASCII.GetString(nameBytes).Trim('\0', ' ');
            if (name.Length == 0)
                continue;
            result.Add((name, idx.ReadCdxHeader(headerOffset)));
        }
        return result;
    }

    [Fact]
    public void Create_Dcx_TagSet_KeysForAndMachineSignature_MatchReference()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            string dcx = ResolveCi(Path.Combine(dir, "shop.DCX"))!;
            Assert.NotNull(dcx);

            // (A) round-trip the tag SET via the high-level reader (case-insensitive, directory order).
            using (var table = DbfTable.Open(dbc))
            using (var cdx = CdxFile.Open(dcx, table))
            {
                Assert.Equal(new[] { "OBJECTNAME", "OBJECTTYPE" },
                    cdx.TagNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());

                var objType = cdx.Tag("OBJECTTYPE")!;
                Assert.Equal("str(parentid)+objecttype", objType.KeyExpression, ignoreCase: true);
                Assert.Equal("!DELETED()", objType.ForExpression, ignoreCase: true);

                var objName = cdx.Tag("OBJECTNAME")!;
                Assert.Equal("str(parentid)+objecttype+lower(objectname)", objName.KeyExpression, ignoreCase: true);
                Assert.Equal("!DELETED()", objName.ForExpression, ignoreCase: true);
            }

            // (B) signature byte 15: both DBC tags are MACHINE (identity) collation, so VFP9 writes
            // 0x01 (NOT the old hardcoded 0x02). Assert via the structural header reader.
            var generated = ReadTagHeaders(dcx);
            Assert.Equal(2, generated.Count);
            foreach (var (name, hdr) in generated)
                Assert.Equal(0x01, hdr.Signature);

            // (C) cross-check the VFP9 reference: its two DBC tags carry the identical 0x01 signature
            // and the identical key/FOR expressions — i.e. our .DCX tag headers match byte 15.
            string refDcx = Path.Combine(Fixtures.VfpTestDir, "dbctest", "ref.DCX");
            var reference = ReadTagHeaders(refDcx);
            Assert.Equal(2, reference.Count);
            foreach (var (name, hdr) in reference)
                Assert.Equal(0x01, hdr.Signature);

            // The reference .DCX literally carries 0x01 at byte 15 of both tag header pages.
            byte[] refBytes = File.ReadAllBytes(refDcx);
            Assert.Equal(0x01, refBytes[1536 + 15]);
            Assert.Equal(0x01, refBytes[3072 + 15]);
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // (E2) the produced compound .DCX is BYTE-IDENTICAL to the VFP9 reference — locks in the
    //      tag-directory page layout (root at page 2) so the real VFP9 runtime opens a database
    //      via OUR .dcx. The CDX header has no last-update date byte, so this compare is stable.
    // =====================================================================================

    [Fact]
    public void Create_CustOrd_ProducesByteIdenticalDcx()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "ref.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() }, new DbcCreateOptions { Overwrite = true });

            byte[] ours = File.ReadAllBytes(ResolveCi(Path.Combine(dir, "ref.DCX"))!);
            byte[] reference = File.ReadAllBytes(Path.Combine(Fixtures.VfpTestDir, "dbctest", "ref.DCX"));
            Assert.Equal(reference, ours);
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // (F) non-ASCII member file name round-trips (container-encoded PROPERTY path blob)
    // =====================================================================================

    [Fact]
    public void Create_NonAsciiMemberFileName_RoundTrips_ViaContainerEncoding()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "shop.dbc");

            // A CP1252 German file name (ö=0xF6, ß=0xDF). Under the old ASCII-only blob encoder these
            // bytes collapsed to '?' (0x3F) in the PROPERTY path, so the reader resolved "gr??e.dbf"
            // and OpenTable threw — the container did NOT round-trip. With the container encoding
            // (CP1252, the default 0x03 code page) the blob stores the true bytes and resolves.
            const string fileName = "größe.dbf";
            var spec = new DbcTableSpec(
                tableName: "groesse",
                fileName: fileName,
                columns: new[] { new DbfColumnDef("ID", 'I', 4) });

            DbfDatabaseBuilder.Create(dbc, new[] { spec });

            // The physical file is created with its true (non-ASCII) name.
            Assert.True(File.Exists(Path.Combine(dir, fileName)), "the member .dbf must exist under its true name");

            // The PROPERTY path blob must hold the CP1252 bytes (NOT '?'-substituted).
            var enc1252 = System.Text.Encoding.GetEncoding(1252);
            var rows = ReadDbcRows(dbc);
            var tableRow = rows.Single(r => r.ObjectType == "Table");
            byte[] pathBytes = enc1252.GetBytes(fileName);
            Assert.True(IndexOf(tableRow.PropertyBlob, pathBytes) >= 0,
                "the PROPERTY blob must carry the CP1252-encoded member name, not '?'");
            Assert.DoesNotContain((byte)'?', tableRow.PropertyBlob);

            // And the container round-trips: the reader resolves and opens the member by logical name.
            using var db = DbfDatabase.OpenFoxpro(dbc);
            Assert.Contains("groesse", db.TableNames);
            using var t = db.OpenTable("groesse");
            Assert.Equal(1, t.Columns.Count(c => !c.IsSystem));
        }
        finally { Nuke(dir); }
    }

    // =====================================================================================
    // (G) BYTE-IDENTICAL compound .DCX — the multi-tag tag-directory page layout
    // =====================================================================================

    private static string RefDct => Path.Combine(Fixtures.VfpTestDir, "dbctest", "ref.DCT");
    private static string RefDcx => Path.Combine(Fixtures.VfpTestDir, "dbctest", "ref.DCX");

    /// <summary>
    /// Rebuild the SAME container as the VFP9 reference (tables cust + ord, the exact column
    /// structure, NO long field names) and assert the produced <c>.DCX</c> is BYTE-FOR-BYTE equal
    /// to <c>vfp_test/dbctest/ref.DCX</c>. The reference places the tag-directory root EARLY (page 2,
    /// offset 1024) — BEFORE the per-tag trees — so a real VFP9 runtime can open the .dbc. Our current
    /// builder appends the directory AFTER every per-tag tree, so its root lands on a late page; this
    /// test pins the correct layout and currently FAILS until CdxIndexBuilder reserves the directory
    /// page(s) up front. The sibling <c>.DBC</c> and <c>.DCT</c> are also compared byte-for-byte as a
    /// regression guard (they already match VFP9). SAFETY: writes ONLY into a throwaway temp dir; the
    /// reference is opened read-only.
    /// </summary>
    [Fact]
    public void Create_Dcx_IsByteIdenticalToReference()
    {
        string dir = FreshTempDir();
        try
        {
            // Same container name as the reference so any (hypothetical) embedded name matches too.
            string dbc = Path.Combine(dir, "ref.dbc");

            // VFP stores the DBC Field OBJECTNAMEs in their ORIGINAL (here: lowercase) case while the
            // physical .DBF descriptor stays uppercase — the reference was created with lowercase field
            // names. To rebuild the byte-identical container we must feed those exact names (via the
            // long-field-name channel, which is what populates the DBC Field records). The OBJECTNAME
            // index keys are lower(objectname) regardless, so the .DCX is unaffected by this casing.
            var custLc = new DbcTableSpec("cust", "cust.dbf", CustSpec().Columns,
                new[] { "cust_id", "company", "amount" });
            var ordLc = new DbcTableSpec("ord", "ord.dbf", OrdSpec().Columns,
                new[] { "order_id", "cust_id", "odate" });
            DbfDatabaseBuilder.Create(dbc, new[] { custLc, ordLc });

            string producedDcx = ResolveCi(Path.Combine(dir, "ref.DCX"))!;
            string producedDct = ResolveCi(Path.Combine(dir, "ref.DCT"))!;
            Assert.NotNull(producedDcx);
            Assert.NotNull(producedDct);

            byte[] gotDcx = File.ReadAllBytes(producedDcx);
            byte[] wantDcx = File.ReadAllBytes(RefDcx);

            // Pinpoint the first divergence for a useful failure message (and prove it is the layout).
            AssertBytesEqual(wantDcx, gotDcx, "ref.DCX");

            // Regression guards: the .DBC and .DCT match VFP9 byte-for-byte EXCEPT the .dbc's
            // last-update date stamp (header bytes 1-3 = YY/MM/DD), which is "today" and differs
            // from the reference's build day — neutralize those 3 bytes before comparing. The .DCX
            // (CDX header) and .DCT (FPT header) carry no date stamp, so they compare in full.
            byte[] gotDbc = File.ReadAllBytes(dbc);
            byte[] wantDbc = File.ReadAllBytes(RefDbc);
            for (int i = 1; i <= 3; i++) gotDbc[i] = wantDbc[i] = 0;
            AssertBytesEqual(wantDbc, gotDbc, "ref.DBC");
            AssertBytesEqual(File.ReadAllBytes(RefDct), File.ReadAllBytes(producedDct), "ref.DCT");
        }
        finally { Nuke(dir); }
    }

    /// <summary>
    /// The 2-tag compound index round-trips through the production <see cref="CdxFile"/> reader: both
    /// tags resolve and each enumerates every one of the 13 (non-deleted) .dbc records.
    /// </summary>
    [Fact]
    public void Create_Dcx_BothTags_RoundTripViaCdxFile()
    {
        string dir = FreshTempDir();
        try
        {
            string dbc = Path.Combine(dir, "ref.dbc");
            DbfDatabaseBuilder.Create(dbc, new[] { CustSpec(), OrdSpec() });

            string producedDcx = ResolveCi(Path.Combine(dir, "ref.DCX"))!;

            using var table = DbfTable.Open(dbc);
            using var cdx = CdxFile.Open(producedDcx, table);

            Assert.Equal(new[] { "OBJECTNAME", "OBJECTTYPE" },
                cdx.TagNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());

            foreach (var name in new[] { "OBJECTTYPE", "OBJECTNAME" })
            {
                var tag = cdx.Tag(name)!;
                Assert.NotNull(tag);
                int count = tag.EnumerateEntries().Count();
                Assert.Equal(13, count);
            }
        }
        finally { Nuke(dir); }
    }

    /// <summary>Assert two byte arrays are identical; on mismatch report length and first differing offset.</summary>
    private static void AssertBytesEqual(byte[] expected, byte[] actual, string what)
    {
        if (expected.Length == actual.Length)
        {
            int firstDiff = -1;
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) { firstDiff = i; break; }
            if (firstDiff < 0)
                return; // identical
            Assert.Fail($"{what}: byte mismatch at offset {firstDiff} (page {firstDiff / 512}, " +
                        $"in-page {firstDiff % 512}): expected 0x{expected[firstDiff]:x2}, got 0x{actual[firstDiff]:x2}.");
        }
        Assert.Fail($"{what}: length mismatch: expected {expected.Length} bytes ({expected.Length / 512} pages), " +
                    $"got {actual.Length} bytes ({actual.Length / 512} pages).");
    }

    // ---- tiny local helpers ------------------------------------------------------------

    private static bool FileExistsCi(string path) => ResolveCi(path) is not null;

    private static string? ResolveCi(string path)
    {
        if (File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileName(path);
        if (!Directory.Exists(dir)) return null;
        foreach (var entry in Directory.EnumerateFiles(dir))
            if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
