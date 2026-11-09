using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlStoredProjectionTests
{
    // Width, order and TOP boundaries are pinned by FinalReviewSqlVfp9OracleTests.
    [Theory]
    [InlineData("SELECT IIF(.F., '!', nm) AS x, id FROM src ORDER BY 1", "3,2,1", 3)]
    [InlineData("SELECT IIF(id = 1, '!', nm) AS x, id FROM src ORDER BY 1", "1,2,3", 1)]
    [InlineData("SELECT IIF(id = 2, '!', nm) AS x, id FROM src ORDER BY 1", "2,3,1", 3)]
    [InlineData("SELECT IIF(id = 1, '!', nm) AS x, id FROM src ORDER BY x DESC", "2,3,1", 1)]
    [InlineData("SELECT TOP 2 IIF(id = 1, '!', nm) AS x, id FROM src ORDER BY 1", "1,2", 1)]
    [InlineData("SELECT IIF(id = 1, 1.0, amount) AS x, id FROM src ORDER BY 1", "1,2,3", 3)]
    [InlineData("SELECT TOP 2 IIF(id = 1, 1.0, amount) AS x, id FROM src ORDER BY 1", "1,2", 3)]
    [InlineData("SELECT IIF(id = 1, '!', nm) AS x, id FROM src WHERE id > 1 ORDER BY 1", "2,3", 1)]
    [InlineData("SELECT IIF(a.id = 1, '!', a.nm) AS x, a.id FROM src a JOIN src b ON a.id = b.id ORDER BY 1", "1,2,3", 1)]
    [InlineData("SELECT IIF(id = 1, '!', MAX(nm)) AS x, id FROM src GROUP BY id ORDER BY 1", "1,2,3", 1)]
    public void StoredValues_DetermineOrderAndTop(string sql, string ids, int width)
    {
        using var dir = new MicroVfpTestSupport.TempDir("stored_projection");
        using (var writer = DbfWriter.Create(dir.File("src.dbf"),
            [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("NM", 'C', 3), new DbfColumnDef("AMOUNT", 'N', 6, 2)]))
        {
            writer.AppendRecord(1, "zzz", 1.00m);
            writer.AppendRecord(2, "abd", 1.04m);
            writer.AppendRecord(3, "abc", 1.03m);
        }
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var result = session.Execute(sql)!;
        Assert.Equal(width, result.Columns[0].Length);
        Assert.Equal(ids, string.Join(",", result.Rows.Select(r => r[1])));
        if (result.Columns[0].VfpType == 'N')
        {
            Assert.Equal(1, result.Columns[0].Decimals);
            Assert.All(result.Rows, r => Assert.Equal(1.0m, r[0]));
        }
        else
            Assert.All(result.Rows, r => Assert.Equal(width, Assert.IsType<string>(r[0]).Length));
    }
}
