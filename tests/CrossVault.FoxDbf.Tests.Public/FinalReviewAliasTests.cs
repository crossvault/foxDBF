using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class FinalReviewAliasTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertFromArray_CustomAliasCompactIndex_RefusesBeforeWriting(bool database)
    {
        using var temp = new MicroVfpTestSupport.TempDir("alias_idx");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        CreateTable(temp, database);
        if (database) session.OpenDatabase(temp.File("sample.dbc"));
        using var vfp = new VfpInterpreter(session) { EnforceReferentialIntegrity = false };
        vfp.Execute("USE customer ALIAS cust INDEX x.idx");
        byte[] dbf = File.ReadAllBytes(temp.File("customer.dbf"));
        byte[] idx = File.ReadAllBytes(temp.File("x.idx"));
        vfp.Execute("DIMENSION values[1]\nvalues[1] = 'CHARLIE'");
        var error = Assert.Throws<MicroVfpRuntimeException>(() => vfp.Execute("INSERT INTO customer FROM ARRAY values"));
        Assert.Contains("standalone indexes", error.Message);
        vfp.Execute("USE");
        Assert.Equal(dbf, File.ReadAllBytes(temp.File("customer.dbf")));
        Assert.Equal(idx, File.ReadAllBytes(temp.File("x.idx")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TableUpdate_DbcCustomAlias_PersistsBufferedAppend(bool allRows)
    {
        using var temp = new MicroVfpTestSupport.TempDir("alias_buffer");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        CreateTable(temp, database: true);
        session.OpenDatabase(temp.File("sample.dbc"));
        using var vfp = new VfpInterpreter(session) { EnforceReferentialIntegrity = false };
        vfp.Execute("""
            USE customer ALIAS cust
            SET MULTILOCKS ON
            =CURSORSETPROP('Buffering', 5)
            INSERT INTO customer VALUES ('CHARLIE')
            GO BOTTOM
            """);
        Assert.True(vfp.EvalExpression(allRows ? "TABLEUPDATE(.T.)" : "TABLEUPDATE(.F.)").AsLogical);
        vfp.Execute("USE\nUSE customer");
        Assert.Equal(2m, vfp.EvalExpression("RECCOUNT()").AsNumber);
        vfp.Execute("GO BOTTOM");
        Assert.Equal("CHARLIE", vfp.EvalExpression("ALLTRIM(k)").AsString);
    }

    private static void CreateTable(MicroVfpTestSupport.TempDir temp, bool database)
    {
        var columns = new[] { new DbfColumnDef("k", 'C', 10) };
        if (database)
            DbfDatabaseBuilder.Create(temp.File("sample.dbc"), [new DbcTableSpec("customer", "customer.dbf", columns)]);
        else
            DbfWriter.Create(temp.File("customer.dbf"), columns).Dispose();
        using (var writer = DbfWriter.Open(temp.File("customer.dbf")))
        {
            writer.AppendRecord("ALPHA");
            writer.CreateCompactIdx(temp.File("x.idx"), "k");
        }
        using var source = DbfWriter.Create(temp.File("source.dbf"), columns);
        source.AppendRecord("CHARLIE");
    }
}
