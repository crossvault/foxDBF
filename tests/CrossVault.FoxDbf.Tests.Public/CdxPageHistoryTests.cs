using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class CdxPageHistoryTests
{
    [Theory]
    [InlineData("MACHINE", 8, 3)]
    [InlineData("GENERAL", 8, 7)]
    [InlineData("DUTCH", 8, 7)]
    [InlineData("GENERAL", 120, 3)]
    public void FirstInsert_UsesVfpRecordWidthAttribute(string collation, int width, ushort attribute)
    {
        // Pinned by CdxPageHistoryVfp9OracleTests (CP1252, both CDX and compact IDX).
        using var dir = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(dir.Path, "t.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', width)], new DbfCreateOptions { CodePage = 3 }))
        {
            writer.CreateTag(new CdxTagDefinition("K", "name", collation: collation));
            writer.AppendRecord("A");
        }
        using var table = DbfTable.Open(path);
        using var index = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        Assert.Equal(attribute, index.Index.ReadNodeHeader(index.Tag("K")!.RootPageOffset)!.Value.Attribute);
    }

    [Fact]
    public void Split_NarrowedExistingLeafAndNewLeafHaveDifferentHistory()
    {
        // Native GENERAL C(120): insert 256 widens/splits, insert 257 narrows page 18.
        using var dir = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(dir.Path, "t.dbf");
        var random = new Random(4711);
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 120)], new DbfCreateOptions { CodePage = 3 }))
        {
            writer.CreateTag(new CdxTagDefinition("K", "name", collation: "GENERAL") { ForExpression = "NOT DELETED()" });
            for (int i = 0; i < 257; i++)
                writer.AppendRecord(new string(Enumerable.Range(0, 120).Select(_ => (char)('A' + random.Next(26))).ToArray()));
        }
        byte[] bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".cdx"));
        Assert.Equal(6, bytes[18 * 512]);
        Assert.Equal(8, bytes[18 * 512 + 20]);
        Assert.Equal(2, bytes[217 * 512]);
        Assert.Equal(8, bytes[217 * 512 + 20]);
    }

    [Fact]
    public void DeleteAndRecall_MaintainStructuralDeletedFilter()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(dir.Path, "t.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 8)]))
        {
            writer.CreateTag(new CdxTagDefinition("K", "name") { ForExpression = "NOT DELETED()" });
            writer.AppendRecord("ALPHA");
            writer.AppendRecord("BETA");
            writer.Delete(0);
        }
        Assert.Equal(new uint[] { 2 }, Records());
        using (var writer = DbfWriter.Open(path)) writer.Recall(0);
        Assert.Equal(new uint[] { 1, 2 }, Records());

        uint[] Records()
        {
            using var table = DbfTable.Open(path);
            using var index = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
            return index.Tag("K")!.EnumerateEntries().Select(e => e.RecordNumber).ToArray();
        }
    }

    [Theory]
    [InlineData("name", null, false)]
    [InlineData("UPPER(name)", "LEN(name) > 0", false)]
    [InlineData("name", "NOT DELETED()", true)]
    [InlineData("IIF(DELETED(), 'X', name)", null, true)]
    public void DeleteAndRecall_ReKeyOnlyTagsThatCanSeeTheDeletionFlag(string key, string? filter, bool changes)
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(dir.Path, "t.dbf");
        string cdx = Path.ChangeExtension(path, ".cdx");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 8)]))
        {
            writer.CreateTag(new CdxTagDefinition("K", key) { ForExpression = filter });
            writer.AppendRecord("ALPHA");
            writer.AppendRecord("BETA");
        }
        byte[] before = File.ReadAllBytes(cdx);
        using (var writer = DbfWriter.Open(path)) writer.Delete(0);
        Assert.Equal(changes, !before.AsSpan().SequenceEqual(File.ReadAllBytes(cdx)));
    }
}
