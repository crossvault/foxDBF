using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class EmptyDateIndexTests
{
    // Raw keys and ordered SEEK results are pinned by EmptyDateIndexVfp9OracleTests.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyDateKey_IsTransformedZero(bool timestamp)
    {
        object empty = timestamp ? (object)DateTime.MinValue : DateOnly.MinValue;
        var type = timestamp ? IndexKeyType.DateTime : IndexKeyType.Date;
        Assert.Equal("8000000000000000", Convert.ToHexString(IndexKey.Encode(empty, type)!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BlankDateField_BulkAndIncrementalKeysMatchVfp9(bool timestamp, bool incremental)
    {
        using var temp = new MicroVfpTestSupport.TempDir("emptydate");
        string path = temp.File("dates.dbf");
        using (var writer = DbfWriter.Create(path, new[] { new DbfColumnDef("VALUE", timestamp ? 'T' : 'D', 8) }))
        {
            if (incremental) writer.CreateTag(new CdxTagDefinition("k", "value"));
            writer.AppendRecord(new object?[] { null });
            if (!incremental) writer.CreateTag(new CdxTagDefinition("k", "value"));
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        var tag = cdx.Tag("k")!;
        Assert.Equal("8000000000000000", Convert.ToHexString(Assert.Single(tag.EnumerateEntries()).Key));
        Assert.Equal(1u, tag.Seek(timestamp ? (object)DateTime.MinValue : DateOnly.MinValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyDateSeek_AscendingAndDescendingLandOnFirstDuplicate(bool timestamp)
    {
        using var temp = new MicroVfpTestSupport.TempDir("emptyseek");
        using (var writer = DbfWriter.Create(temp.File("dates.dbf"),
                   new[] { new DbfColumnDef("VALUE", timestamp ? 'T' : 'D', 8) }))
        {
            writer.AppendRecord(timestamp ? (object)new DateTime(2024, 1, 2, 12, 0, 0) : new DateOnly(2024, 1, 2));
            writer.AppendRecord(new object?[] { null });
            writer.AppendRecord(timestamp ? (object)new DateTime(2020, 1, 2, 6, 0, 0) : new DateOnly(2020, 1, 2));
            writer.AppendRecord(new object?[] { null });
            writer.CreateTag(new CdxTagDefinition("ka", "value"));
            writer.CreateTag(new CdxTagDefinition("kd", "value") { Descending = true });
        }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        var vfp = new VfpInterpreter(session);
        vfp.Execute("USE dates\nlcResult = ''");
        foreach (string tag in new[] { "ka", "kd" })
        {
            vfp.Execute($"SET ORDER TO TAG {tag}\nGO TOP\nSCAN\nlcResult = lcResult + TRANSFORM(RECNO()) + ','\nENDSCAN\n" +
                        $"SEEK {(timestamp ? "CTOT('')" : "{}")}\nlcResult = lcResult + TRANSFORM(FOUND()) + ':' + TRANSFORM(RECNO()) + ';'");
        }
        Assert.Equal("2,4,3,1,.T.:2;1,3,4,2,.T.:4;", vfp.EvalExpression("lcResult").AsString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullableDateField_NullFlagIsNotAnEmptyDate(bool timestamp)
    {
        using var temp = new MicroVfpTestSupport.TempDir("nulldate");
        string path = temp.File("dates.dbf");
        using (var writer = DbfWriter.Create(path,
                   new[] { new DbfColumnDef("VALUE", timestamp ? 'T' : 'D', 8) { Nullable = true } }))
        {
            writer.AppendRecord(new object?[] { null });
            writer.CreateTag(new CdxTagDefinition("k", "value"));
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        // VFP9 reserves a leading NULL marker in nullable field keys.
        Assert.Equal("000000000000000000", Convert.ToHexString(Assert.Single(cdx.Tag("k")!.EnumerateEntries()).Key));
    }
}
