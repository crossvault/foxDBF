using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class GermanCollationTests
{
    [Fact]
    public void GermanOn850_IndexRaisesVfp1915_AfterSetCollateSucceeds()
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        using (DbfWriter.Create(Path.Combine(temp.Path, "t.dbf"), [new DbfColumnDef("name", 'C', 12)],
            new DbfCreateOptions { CodePage = 2 })) { }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("USE t\nSET COLLATE TO 'GERMAN'");
        Assert.Equal(1915, Assert.Throws<MicroVfpRuntimeException>(() => interpreter.Execute("INDEX ON name TAG k")).VfpErrorNumber);
        Assert.False(File.Exists(Path.Combine(temp.Path, "t.cdx")));
    }

    // Natural keys pinned from VFP9-made CDX entries (including CP437's surprising ö -> SS).
    [Theory]
    [InlineData(1252, "äa", "606660")]
    [InlineData(1252, "äá", "606660000001")]
    [InlineData(1252, "öa", "726660")]
    [InlineData(1252, "üá", "786660000001")]
    [InlineData(1252, "ßá", "767660000001")]
    [InlineData(1252, "Æá", "606660000001")]
    [InlineData(1252, "aä", "606066")]
    [InlineData(1252, "äää", "606660666066")]
    [InlineData(1252, "oe", "7266")]
    [InlineData(1252, "ss", "7676")]
    [InlineData(437, "äa", "606660")]
    [InlineData(437, "äá", "606660000001")]
    [InlineData(437, "öa", "767660")]
    [InlineData(437, "üá", "786660000001")]
    [InlineData(437, "ßá", "767660000001")]
    [InlineData(437, "Æá", "606660000001")]
    [InlineData(437, "aä", "606066")]
    [InlineData(437, "äää", "606660666066")]
    [InlineData(437, "oe", "7266")]
    [InlineData(437, "ss", "7676")]
    [InlineData(10000, "äa", "777B77")]
    [InlineData(10000, "äá", "777B77000002")]
    [InlineData(10000, "öa", "857B77")]
    [InlineData(10000, "üá", "8C7B77000002")]
    [InlineData(10000, "ßá", "89897702")]
    [InlineData(10000, "Æá", "777B77000002")]
    [InlineData(10000, "aä", "77777B")]
    [InlineData(10000, "äää", "777B777B777B")]
    [InlineData(10000, "oe", "857B")]
    [InlineData(10000, "ss", "8989")]
    public void German_KeysMatchVfp9(int codePage, string value, string expected)
        => Assert.Equal(expected, Convert.ToHexString(VfpCollations.ByName("GERMAN", codePage).GetCollatedKey(value)));

    [Theory]
    [InlineData(1252, 0x03)]
    [InlineData(437, 0x01)]
    [InlineData(10000, 0x04)]
    public void German_IsExact_AndWriterMaintainsTag(int codePage, byte mark)
    {
        Assert.Equal("GERMAN", VfpCollations.ByName("GERMAN", codePage).Name);
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "german.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 20)],
            new DbfCreateOptions { CodePage = mark }))
        {
            writer.AppendRecord("Öl");
            writer.CreateTag(new CdxTagDefinition("k", "name") { Collation = "GERMAN" });
            writer.AppendRecord("Straße");
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        Assert.True(cdx.Tag("k")!.IsKeyCollationExact);
        Assert.Equal(2u, cdx.Tag("k")!.Seek("Straße"));
    }
}
