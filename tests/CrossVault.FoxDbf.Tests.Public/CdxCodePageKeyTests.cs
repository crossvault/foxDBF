using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// CHARACTER CDX key DATA is stored in the TABLE code page under MACHINE (VFP9: 'Peñarol' in a CP850 table is
/// <c>50 65 A4 61 72 6F 6C</c>, see <c>vfp_test/uninames/uni850.CDX</c>; in CP1252 it is <c>50 65 F1 …</c>).
/// The bulk build, the incremental row maintenance, <see cref="CdxTag.Seek(object)"/> and the Rushmore
/// character bound must all produce those same bytes, or a seek / an indexed WHERE silently misses the row.
/// </summary>
public sealed class CdxCodePageKeyTests : IDisposable
{
    private readonly IndexMaintTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Path(string name) => System.IO.Path.Combine(_dir.Path, name);

    private static string Fixture(string name) => Fixtures.VfpTest(System.IO.Path.Combine("uninames", name));

    private static readonly Encoding Cp1252 = Encodings.ResolveEncoding(0x03);
    private static readonly Encoding Cp850 = Encodings.ResolveEncoding(0x02);

    private static readonly string[] Values = { "Peñarol", "Ánimo", "zeta", "Öl", "Straße", "éclair", "abc" };

    private static DbfColumnDef[] Columns() =>
        new[] { new DbfColumnDef("NAME", 'C', 10), new DbfColumnDef("ID", 'I', 4) };

    private static List<(string Key, uint RecNo)> Entries(string dbf, string tag)
    {
        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(System.IO.Path.ChangeExtension(dbf, ".cdx"), table);
        return cdx.Tag(tag)!.EnumerateEntries().Select(e => (Convert.ToHexString(e.Key), e.RecordNumber)).ToList();
    }

    // ---- the VFP9-built CP850 fixture ---------------------------------------------------------

    [Fact]
    public void VfpCdx_Cp850_SeekAndDecode_UseTheTableCodePage()
    {
        using var table = DbfTable.Open(Fixture("uni850.dbf"));
        using var cdx = CdxFile.Open(Fixture("uni850.CDX"), table);
        var tag = cdx.Tag("RAZÓN")!;
        var entry = Assert.Single(tag.EnumerateEntries());

        Assert.Equal(Cp850.GetBytes("Peñarol   "), entry.Key);
        Assert.Equal(1u, tag.Seek("Peñarol"));
        Assert.Equal(1u, tag.Seek("Peña"));
        Assert.Equal("Peñarol", tag.DecodeKey(entry.Key).AsString);
    }

    [Fact]
    public void VfpCdx_Cp850_RushmoreCharacterBound_FindsTheRow()
    {
        using var table = DbfTable.Open(Fixture("uni850.dbf"));
        using var cdx = CdxFile.Open(Fixture("uni850.CDX"), table);

        var eq = QueryOptimizer.FindRecords(table, cdx, "razónsoc = 'Peñarol'");
        Assert.True(eq.Optimized);
        Assert.Equal(new[] { 1 }, eq.RecordNumbers);
        Assert.Equal(new[] { 1 }, QueryOptimizer.FindRecords(table, cdx, "razónsoc = 'Peña'").RecordNumbers);
        Assert.Equal(new[] { 1 }, QueryOptimizer.FindRecords(table, cdx, "razónsoc >= 'Peñ'").RecordNumbers);
        Assert.Empty(QueryOptimizer.FindRecords(table, cdx, "razónsoc = 'Peñb'").RecordNumbers);
    }

    [Fact]
    public void CreateTag_Cp850_FixtureRebuilt_IsByteIdenticalToVfp9()
    {
        string dbf = Path("uni850.dbf");
        using (var w = DbfWriter.Create(dbf,
                   new[] { new DbfColumnDef("RAZÓNSOC", 'C', 10), new DbfColumnDef("ID", 'I', 4) },
                   new DbfCreateOptions { CodePage = 0x02 }))
        {
            w.AppendRecord("Peñarol", 2);
            w.CreateTag(new CdxTagDefinition("RAZÓN", "razónsoc"));
        }

        using var vfpTable = DbfTable.Open(Fixture("uni850.dbf"));
        using var vfpCdx = CdxFile.Open(Fixture("uni850.CDX"), vfpTable);
        var expected = vfpCdx.Tag("RAZÓN")!.EnumerateEntries().Select(e => (Convert.ToHexString(e.Key), e.RecordNumber));
        Assert.Equal(expected, Entries(dbf, "RAZÓN"));
    }

    // ---- our own build: bulk, incremental, code pages ----------------------------------------------

    [Theory]
    [InlineData((byte)0x02, 850)]
    [InlineData((byte)0x03, 1252)]
    public void CreateTag_MachineKeys_AreTheTableCodePageBytes_InByteOrder(byte codePage, int cp)
    {
        var enc = Encodings.ResolveEncoding(codePage);
        Assert.Equal(cp, enc.CodePage);
        string dbf = Path($"t{cp}.dbf");
        using (var w = DbfWriter.Create(dbf, Columns(), new DbfCreateOptions { CodePage = codePage }))
        {
            for (int i = 0; i < Values.Length; i++) w.AppendRecord(Values[i], i + 1);
            w.CreateTag(new CdxTagDefinition("NAME", "name"));
        }

        var expected = Values
            .Select((v, i) => (Key: enc.GetBytes(v.PadRight(10)), RecNo: (uint)(i + 1)))
            .OrderBy(e => e.Key, ByteOrder.Instance)
            .Select(e => (Convert.ToHexString(e.Key), e.RecNo));
        Assert.Equal(expected, Entries(dbf, "NAME"));

        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(System.IO.Path.ChangeExtension(dbf, ".cdx"), table);
        for (int i = 0; i < Values.Length; i++)
            Assert.Equal((uint)(i + 1), cdx.Tag("NAME")!.Seek(Values[i]));
    }

    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x03)]
    public void IncrementalAppend_ProducesTheSameEntriesAsTheBulkBuild(byte codePage)
    {
        string bulk = Path("bulk.dbf");
        using (var w = DbfWriter.Create(bulk, Columns(), new DbfCreateOptions { CodePage = codePage }))
        {
            for (int i = 0; i < Values.Length; i++) w.AppendRecord(Values[i], i + 1);
            w.CreateTag(new CdxTagDefinition("NAME", "name"));
            w.CreateTag(new CdxTagDefinition("UP", "UPPER(name)+CHR(130)"));
            w.CreateTag(new CdxTagDefinition("GEN", "name") { Collation = "GENERAL" });
        }

        string incr = Path("incr.dbf");
        using (var w = DbfWriter.Create(incr, Columns(), new DbfCreateOptions { CodePage = codePage }))
        {
            w.CreateTag(new CdxTagDefinition("NAME", "name"));
            w.CreateTag(new CdxTagDefinition("UP", "UPPER(name)+CHR(130)"));
            w.CreateTag(new CdxTagDefinition("GEN", "name") { Collation = "GENERAL" });
        }
        using (var w = DbfWriter.Open(incr))
            for (int i = 0; i < Values.Length; i++) w.AppendRecord(Values[i], i + 1);

        foreach (var tag in new[] { "NAME", "UP", "GEN" })
            Assert.Equal(Entries(bulk, tag), Entries(incr, tag));

        // CHR(130) is byte 0x82 in the table code page (CP850 'é', CP1252 '‚'), never '?'.
        Assert.All(Entries(bulk, "UP"), e => Assert.Equal("82", e.Key.Substring(20, 2)));
    }

    [Fact]
    public void CreateTag_NoCpTransField_KeepsTheRawBytes()
    {
        string dbf = Path("raw.dbf");
        using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("NAME", 'C', 2, binary: true) },
                   new DbfCreateOptions { CodePage = 0x02 }))
        {
            w.AppendRecord("\u0080¤");
            w.CreateTag(new CdxTagDefinition("NAME", "name"));
        }

        Assert.Equal("80A4", Assert.Single(Entries(dbf, "NAME")).Key);
        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(System.IO.Path.ChangeExtension(dbf, ".cdx"), table);
        Assert.Equal(1u, cdx.Tag("NAME")!.Seek("\u0080¤"));
    }

    [Fact]
    public void Cp850_Rushmore_OverOurOwnCdx_FindsEveryValue()
    {
        string dbf = Path("q850.dbf");
        using (var w = DbfWriter.Create(dbf, Columns(), new DbfCreateOptions { CodePage = 0x02 }))
        {
            for (int i = 0; i < Values.Length; i++) w.AppendRecord(Values[i], i + 1);
            w.CreateTag(new CdxTagDefinition("NAME", "name"));
        }

        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(System.IO.Path.ChangeExtension(dbf, ".cdx"), table);
        for (int i = 0; i < Values.Length; i++)
        {
            var r = QueryOptimizer.FindRecords(table, cdx, $"name = '{Values[i]}'");
            Assert.True(r.Optimized);
            Assert.Equal(new[] { i + 1 }, r.RecordNumbers);
        }
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
