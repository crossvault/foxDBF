using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class CentralEuropeanCollationTests
{
    [Theory]
    [InlineData("o", "=")]
    [InlineData("oz", "=")]
    [InlineData("o", ">=")]
    [InlineData("oz", ">=")]
    public void Hungary_ContractedPrefixBelowLiteral_RemainsInRushmoreRange(string prefix, string op)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "range.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 10)],
                   new DbfCreateOptions { CodePage = 0xC8 }))
        {
            writer.AppendRecord("ozs");
            writer.CreateTag(new CdxTagDefinition("k", "name") { Collation = "HUNGARY" });
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        var context = new EvaluationContext { Collation = VfpCollations.ByName("HUNGARY", 1250), Exact = false };
        string filter = $"name {op} '{prefix}'";
        var scan = QueryOptimizer.FindRecords(table, null, filter, context);
        var indexed = QueryOptimizer.FindRecords(table, cdx, filter, context);
        Assert.Single(scan.RecordNumbers);
        Assert.Equal(scan.RecordNumbers, indexed.RecordNumbers);
        Assert.True(indexed.Optimized);
    }

    [Theory]
    [InlineData("CH", "ch")]
    [InlineData("cH", "ch")]
    public void Hungary_ZeroTailPositions_DoNotMakeCaseVariantsUnequal(string left, string right)
    {
        var collation = (GeneralCollation)VfpCollations.ByName("HUNGARY", 1250);
        Assert.Equal(0, collation.Compare(left, right, StringCompareMode.TruncateLeft));
    }

    [Theory]
    [InlineData("CZECH", 1250, 0xC8)]
    [InlineData("CZECH", 852, 0x64)]
    [InlineData("CZECH", 895, 0x68)]
    [InlineData("SLOVAK", 1250, 0xC8)]
    [InlineData("SLOVAK", 852, 0x64)]
    [InlineData("SLOVAK", 895, 0x68)]
    [InlineData("POLISH", 1250, 0xC8)]
    [InlineData("POLISH", 852, 0x64)]
    [InlineData("POLISH", 620, 0x69)]
    [InlineData("HUNGARY", 1250, 0xC8)]
    [InlineData("HUNGARY", 852, 0x64)]
    public void ExactCells_BuildMaintainAndMicroVfpSeek(string name, int cp, byte mark)
    {
        Assert.Equal(name, VfpCollations.ByName(name, cp).Name);
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "central.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 10)],
                   new DbfCreateOptions { CodePage = mark }))
        {
            writer.AppendRecord("ch");
            writer.CreateTag(new CdxTagDefinition("k", "name") { Collation = name });
            writer.AppendRecord("dzs");
        }
        using (var table = DbfTable.Open(path))
        using (var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table))
        {
            Assert.True(cdx.Tag("k")!.IsKeyCollationExact);
            Assert.Equal(2u, cdx.Tag("k")!.Seek("dzs"));
        }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session, 1250);
        interpreter.Execute($"SET COLLATE TO '{name}'\nUSE central\nINDEX ON name TAG micro\nSEEK 'dzs'");
        Assert.True(interpreter.EvalExpression("FOUND()").AsLogical);
        Assert.Equal(name, interpreter.EvalExpression("IDXCOLLATE(2)").AsString);
    }
}
