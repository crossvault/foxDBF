using System;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Non-ASCII names on the WRITE and INDEX side, checked against the VFP9-generated fixtures in
/// <c>vfp_test/uninames/</c> (<c>mkuninames.prg</c>). VFP stores field names, tag names and KEY/FOR
/// expressions in the TABLE's code page: <c>RAZÓNSOC</c> carries 'Ó' as 0xD3 in CP1252 (unifree.dbf) and as
/// 0xE0 in CP850 (uni850.dbf); the tag key <c>razónsoc</c> carries 'ó' as 0xF3 / 0xA2. A character the code
/// page cannot hold must be refused, never written as '?'.
/// </summary>
public sealed class UnicodeNameWriteTests : IDisposable
{
    private readonly IndexMaintTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Path(string name) => System.IO.Path.Combine(_dir.Path, name);

    private static string Fixture(string name) => Fixtures.VfpTest(System.IO.Path.Combine("uninames", name));

    private static readonly Encoding Cp1252 = Encodings.ResolveEncoding(0x03);
    private static readonly Encoding Cp850 = Encodings.ResolveEncoding(0x02);

    private static DbfColumnDef[] RazonSocColumns() =>
        new[] { new DbfColumnDef("RAZÓNSOC", 'C', 10), new DbfColumnDef("ID", 'I', 4) };

    /// <summary>The name-bearing descriptor bytes: the whole first (character) descriptor and the 11-byte name
    /// slot of the second. (The rest of the I descriptor differs for an unrelated reason: VFP9 sets the 0x04
    /// binary flag on Integer fields, our writer does not.)</summary>
    private static byte[] DescriptorRegion(string dbf)
    {
        byte[] bytes = File.ReadAllBytes(dbf);
        return bytes.AsSpan(32, 32).ToArray().Concat(bytes.AsSpan(64, 11).ToArray()).ToArray();
    }

    private static bool Contains(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;

    // ---- DBF descriptor names ---------------------------------------------------------------------

    [Fact]
    public void Create_Cp1252_FieldDescriptors_AreByteIdenticalToVfp9()
    {
        string dbf = Path("unifree.dbf");
        using (DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x03 })) { }

        Assert.Equal(0xD3, File.ReadAllBytes(dbf)[32 + 3]);
        Assert.Equal(DescriptorRegion(Fixture("unifree.dbf")), DescriptorRegion(dbf));
        using var table = DbfTable.Open(dbf);
        Assert.Equal("RAZÓNSOC", table.Columns[0].Name);
    }

    [Fact]
    public void Create_Cp850_FieldName_UsesTheTableCodePage_LikeVfp9()
    {
        string dbf = Path("uni850.dbf");
        using (DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x02 })) { }

        Assert.Equal(0xE0, File.ReadAllBytes(dbf)[32 + 3]);
        Assert.Equal(DescriptorRegion(Fixture("uni850.dbf")), DescriptorRegion(dbf));
        using var table = DbfTable.Open(dbf);
        Assert.Equal("RAZÓNSOC", table.Columns[0].Name);
    }

    [Fact]
    public void Alter_VfpCp850Table_KeepsItsCodePageForNames()
    {
        // Alter rebuilds the header through the same name encoder; it must reuse the table's own code page
        // (CP850: 'Ó' = 0xE0) and accept a new non-ASCII column in it.
        string dbf = Path("uni850.dbf");
        File.Copy(Fixture("uni850.dbf"), dbf);
        using (var w = DbfWriter.Open(dbf, new DbfOptions { LockMode = LockMode.Exclusive }))
            w.AddColumn(new DbfColumnDef("AÑO", 'N', 4));

        byte[] raw = File.ReadAllBytes(dbf);
        Assert.Equal(0x02, raw[29]);
        Assert.Equal(0xE0, raw[32 + 3]);
        using var table = DbfTable.Open(dbf);
        Assert.Equal(new[] { "RAZÓNSOC", "ID", "AÑO" }, table.Columns.Select(c => c.Name).ToArray());
        Assert.Equal("Peñarol", table.GetRecord(0)?["RAZÓNSOC"]?.ToString()?.TrimEnd());
    }

    [Fact]
    public void Create_NameTheCodePageCannotHold_IsRefused_NotWrittenAsQuestionMark()
    {
        string dbf = Path("bad.dbf");
        var cols = new[] { new DbfColumnDef("ŐRSZÁG", 'C', 10) };   // 'Ő' (U+0150) is not in CP1252

        var ex = Assert.Throws<DbfSchemaException>(() => DbfWriter.Create(dbf, cols, new DbfCreateOptions { CodePage = 0x03 }));
        Assert.Contains("U+0150", ex.Message);
        Assert.Contains("1252", ex.Message);
        Assert.False(File.Exists(dbf));
    }

    [Fact]
    public void Create_NameFittingTenCharsButNotTenBytes_IsRefused()
    {
        string dbf = Path("utf8.dbf");
        var cols = new[] { new DbfColumnDef("ÓÓÓÓÓÓ", 'C', 10) };   // 6 chars, 12 bytes in UTF-8

        var ex = Assert.Throws<DbfSchemaException>(() =>
            DbfWriter.Create(dbf, cols, new DbfCreateOptions { Encoding = Encoding.UTF8 }));
        Assert.Contains("12 bytes", ex.Message);
    }

    // ---- reading VFP9's CDX ---------------------------------------------------------------------

    [Fact]
    public void VfpCdx_TagNamesAndExpressions_DecodeInTheTableCodePage()
    {
        using var table = DbfTable.Open(Fixture("uniidx.dbf"));
        using var cdx = CdxFile.Open(Fixture("uniidx.CDX"), table);

        // Directory order is BYTE order: R (0x52) < Š (0x8A) < À (0xC0) — not UTF-16 order (À < Š).
        Assert.Equal(new[] { "RAZÓN", "ŠUP", "ÀID" }, cdx.TagNames);
        Assert.Equal("razónsoc", cdx.Tag("razón")!.KeyExpression);
        Assert.Equal("UPPER(razónsoc)", cdx.Tag("šup")!.KeyExpression);
        Assert.Equal("razónsoc#\"X\"", cdx.Tag("ŠUP")!.ForExpression);
        Assert.NotNull(cdx.Tag("RAZÓN")!.Seek("Peñarol"));
    }

    [Fact]
    public void VfpCdx_Cp850_TagNameAndKey_DecodeInTheTableCodePage()
    {
        using var table = DbfTable.Open(Fixture("uni850.dbf"));
        using var cdx = CdxFile.Open(Fixture("uni850.CDX"), table);

        Assert.Equal(new[] { "RAZÓN" }, cdx.TagNames);
        Assert.Equal("razónsoc", cdx.Tag("RAZÓN")!.KeyExpression);
        Assert.Equal("RAZÓNSOC", table.Columns[0].Name);
    }

    // ---- building our own CDX -------------------------------------------------------------------

    [Fact]
    public void CreateTag_UnicodeNamesAndKeys_MatchVfp9Bytes_AndDirectoryOrder()
    {
        string dbf = Path("uniidx.dbf");
        using (var w = DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x03 }))
        {
            w.AppendRecord("Peñarol", 2);
            w.AppendRecord("ACME", 1);
            w.CreateTag(new CdxTagDefinition("RAZÓN", "razónsoc"));
            w.CreateTag(new CdxTagDefinition("ŠUP", "UPPER(razónsoc)", "razónsoc#\"X\""));
            w.CreateTag(new CdxTagDefinition("ÀID", "id"));
        }

        byte[] ours = File.ReadAllBytes(System.IO.Path.ChangeExtension(dbf, ".cdx"));
        byte[] vfp = File.ReadAllBytes(Fixture("uniidx.CDX"));
        foreach (var piece in new[] { "RAZÓN", "razónsoc\0", "UPPER(razónsoc)\0razónsoc#\"X\"\0" })
        {
            byte[] needle = Cp1252.GetBytes(piece);
            Assert.True(Contains(vfp, needle), $"VFP9 fixture lacks '{piece}'");
            Assert.True(Contains(ours, needle), $"our .cdx lacks '{piece}' in CP1252");
        }
        Assert.True(Contains(ours, new byte[] { 0x8A, (byte)'U', (byte)'P' }));
        Assert.True(Contains(ours, new byte[] { 0xC0, (byte)'I', (byte)'D' }));

        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(System.IO.Path.ChangeExtension(dbf, ".cdx"), table);
        using var vfpTable = DbfTable.Open(Fixture("uniidx.dbf"));
        using var vfpCdx = CdxFile.Open(Fixture("uniidx.CDX"), vfpTable);
        Assert.Equal(vfpCdx.TagNames, cdx.TagNames);
        Assert.NotNull(cdx.Tag("razón")!.Seek("Peñarol"));
    }

    [Fact]
    public void CreateTag_ReopenedWriter_KeepsUnicodeTags_ThroughTheFastPaths()
    {
        string dbf = Path("t.dbf");
        string cdxPath = System.IO.Path.ChangeExtension(dbf, ".cdx");
        using (var w = DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x03 }))
        {
            w.AppendRecord("Peñarol", 2);
            w.CreateTag(new CdxTagDefinition("RAZÓN", "razónsoc"));
            w.CreateTag(new CdxTagDefinition("ÀID", "id"));
        }

        // Reopen: append a tag (fast append), unlink one (fast unlink), maintain on row append.
        using (var w = DbfWriter.Open(dbf))
        {
            w.CreateTag(new CdxTagDefinition("ŠUP", "UPPER(razónsoc)"));
            Assert.Equal(2, w.DeleteTagsIn(cdxPath, structural: true, new[] { "ÀID" }));
            w.AppendRecord("ACME", 1);
        }

        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(cdxPath, table);
        Assert.Equal(new[] { "RAZÓN", "ŠUP" }, cdx.TagNames);
        Assert.NotNull(cdx.Tag("ŠUP")!.Seek(Cp1252.GetBytes("PEÑAROL")));   // composite key: seek raw bytes
        Assert.NotNull(cdx.Tag("RAZÓN")!.Seek("ACME"));
    }

    [Fact]
    public void CreateTag_UnencodableName_Throws()
    {
        string dbf = Path("t.dbf");
        using var w = DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x03 });
        w.AppendRecord("ACME", 1);

        var ex = Assert.Throws<DbfWriteException>(() => w.CreateTag(new CdxTagDefinition("ŐX", "id")));
        Assert.Contains("U+0150", ex.Message);
    }

    [Fact]
    public void CreateTag_NameLongerThanTenBytes_Throws_LikeVfp9Error1888()
    {
        // VFP9: INDEX ON id TAG abcdefghijkl → error 1888 "Tag name is too long" (probe 2026-10-22).
        // The builder used to clip the name silently to 10 bytes.
        string dbf = Path("t.dbf");
        using var w = DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x03 });
        w.AppendRecord("ACME", 1);

        Assert.Throws<DbfWriteException>(() => w.CreateTag(new CdxTagDefinition("ABCDEFGHIJK", "id")));
    }

    // ---- standalone IDX -------------------------------------------------------------------------

    [Fact]
    public void CreateStandaloneIdx_UnicodeKey_IsStoredAndReadInTheTableCodePage()
    {
        string dbf = Path("t.dbf");
        string idx = Path("razon.idx");
        using (var w = DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x02 }))
        {
            w.AppendRecord("Peñarol", 2);
            w.CreateStandaloneIdx(idx, "razónsoc");
        }

        byte[] raw = File.ReadAllBytes(idx);
        Assert.Equal(Cp850.GetBytes("razónsoc\0"), raw.AsSpan(16, 9).ToArray());   // 'ó' = 0xA2 in CP850
        using var file = IdxFile.Open(idx, Cp850);
        Assert.Equal("razónsoc", file.Header.KeyExpression);
    }

    [Fact]
    public void CreateStandaloneIdx_UnencodableKey_Throws()
    {
        string dbf = Path("t.dbf");
        using var w = DbfWriter.Create(dbf, RazonSocColumns(), new DbfCreateOptions { CodePage = 0x03 });
        w.AppendRecord("ACME", 1);

        Assert.Throws<DbfWriteException>(() => w.CreateStandaloneIdx(Path("x.idx"), "razónsoc + \"Ő\""));
        Assert.False(File.Exists(Path("x.idx")));
    }
}
