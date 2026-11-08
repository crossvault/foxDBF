using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class StructuralCdxCompactionTests
{
    [Theory]
    [InlineData("PACK")]
    [InlineData("ZAP")]
    public void MicroVfp_CompactionKeepsStructuralTags(string command)
    {
        using var temp = new MicroVfpTestSupport.TempDir("cdxcompact");
        using (var writer = DbfWriter.Create(temp.File("t.dbf"),
                   [new DbfColumnDef("k", 'C', 12), new DbfColumnDef("active", 'L')]))
        {
            writer.AppendRecord("BRAVO", true);
            writer.AppendRecord("ALPHA", false);
            writer.AppendRecord("CHARLIE", true);
            writer.CreateTag(new CdxTagDefinition("names", "k"));
            writer.CreateTag(new CdxTagDefinition("selected", "UPPER(k)", "active", descending: true));
        }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        var vfp = new VfpInterpreter(session);
        vfp.Execute($"SET DELETED OFF\nUSE t EXCLUSIVE\nSET ORDER TO names\nGO 2\nDELETE\n{command}");
        Assert.True(File.Exists(temp.File("t.cdx")), $"{command} deleted the structural CDX");
        Assert.Equal(2m, vfp.EvalExpression("TAGCOUNT()").AsNumber);
        Assert.Equal("K", vfp.EvalExpression("KEY(1)").AsString);
        Assert.Equal("ACTIVE", vfp.EvalExpression("FOR(2)").AsString);
        Assert.Equal(command == "PACK", vfp.EvalExpression("SEEK('CHARLIE', 't', 'names')").AsLogical);
        if (command == "PACK")
        {
            Assert.Equal(2m, vfp.EvalExpression("RECNO()").AsNumber);
            vfp.Execute("SET ORDER TO selected\nGO TOP");
            Assert.Equal(2m, vfp.EvalExpression("RECNO()").AsNumber);
        }
        vfp.Execute("INSERT INTO t VALUES ('DELTA', .T.)");
        Assert.True(vfp.EvalExpression("SEEK('DELTA', 't', 'selected')").AsLogical);
    }
}
