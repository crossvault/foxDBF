using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class EasternCollationTests
{
    [Fact]
    public void Turkish_DottedCapitalI_EqualsLowerI_WithExactOff()
    {
        var context = new EvaluationContext { Collation = VfpCollations.ByName("TURKISH", 1254), Exact = false };
        Assert.True(VfpExpression.Parse("'İ' = 'i'").Evaluate(null!, context).AsLogical);
    }

    [Theory]
    [InlineData("TURKISH", 0xCA, "İ", "i", "z")]
    [InlineData("RUSSIAN", 0xC9, "Е", "е", "Ж")]
    [InlineData("GREEK", 0xCB, "Α", "α", "Β")]
    public void FilteredTag_UsesItsLanguageCollation(string sequence, byte mark, string value, string needle, string other)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "f.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 10)], new DbfCreateOptions { CodePage = mark }))
        {
            writer.AppendRecord(value);
            writer.AppendRecord(other);
            writer.CreateTag(new CdxTagDefinition("k", "name", $"name = '{needle}'", collation: sequence));
            writer.AppendRecord(value);
        }
        using var table = DbfTable.Open(path);
        using var index = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        Assert.Equal(new uint[] { 1, 3 }, index.Tag("k")!.EnumerateEntries().Select(e => e.RecordNumber));
    }

    [Theory]
    [InlineData("TURKISH", 1254, 0xCA, "İıiI")]
    [InlineData("TURKISH", 857, 0x6B, "İıiI")]
    [InlineData("RUSSIAN", 1251, 0xC9, "еёЕЁ")]
    [InlineData("RUSSIAN", 866, 0x65, "еёЕЁ")]
    [InlineData("RUSSIAN", 10007, 0x96, "еёЕЁ")]
    [InlineData("GREEK", 1253, 0xCB, "άςσΣ")]
    [InlineData("GREEK", 737, 0x6A, "άςσΣ")]
    [InlineData("GREEK", 10006, 0x98, "άςσΣ")]
    public void ExactRegistration_WriterAndMicroVfp(string sequence, int cp, byte mark, string value)
    {
        Assert.Equal(sequence, VfpCollations.ByName(sequence, cp).Name);
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "t.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 10)], new DbfCreateOptions { CodePage = mark }))
        {
            writer.AppendRecord(value);
            writer.CreateTag(new CdxTagDefinition("k", "name", collation: sequence));
            writer.AppendRecord("zz");
        }
        using (var table = DbfTable.Open(path))
        using (var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table))
        {
            Assert.True(cdx.Tag("k")!.IsKeyCollationExact);
            Assert.Equal(1u, cdx.Tag("k")!.Seek(value));
        }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"SET COLLATE TO '{sequence}'\nUSE t\nINDEX ON name TAG m\nSET ORDER TO m");
        Assert.True(interpreter.EvalExpression($"SEEK('{value}')").AsLogical);
    }
}
