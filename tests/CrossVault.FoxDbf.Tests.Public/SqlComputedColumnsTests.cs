using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>Cursor metadata and values measured by SqlComputedColumnsVfp9OracleTests.</summary>
public sealed class SqlComputedColumnsTests : IDisposable
{
    private readonly MicroVfpTestSupport.TempDir _dir = new("sql_computed");
    private readonly VfpSession _session = new();

    public SqlComputedColumnsTests()
    {
        _session.OpenDirectory(_dir.Path);
        _session.Execute("CREATE TABLE ct (id I, g I, w C(5), n N(8,2) NULL, d D)");
        _session.Execute("INSERT INTO ct VALUES (1,1,'a',.NULL.,{^2020-01-02})");
        _session.Execute("INSERT INTO ct VALUES (2,1,'bb',.NULL.,{^2020-02-03})");
        _session.Execute("INSERT INTO ct VALUES (3,2,'ccc',12.34,{^2020-03-04})");
        _session.Execute("INSERT INTO ct VALUES (4,2,'dddd',.NULL.,{^2020-04-05})");
        _session.Execute("INSERT INTO ct VALUES (5,2,'eeeee',2.01,{^2020-05-06})");
        using var writer = DbfWriter.Create(_dir.File("nt.dbf"), new[]
        {
            new DbfColumnDef("ni", 'N', 5), new DbfColumnDef("nf", 'F', 9, 3),
            new DbfColumnDef("bd", 'B', 8, 3), new DbfColumnDef("cy", 'Y')
        });
        writer.AppendRecord(3, 1.235m, 1.235, 1.2345m);
        writer.AppendRecord(4, 2.234m, 2.234, 2.3456m);
    }

    [Theory]
    [InlineData("LEN(w)", 'N', 10, 0)]
    [InlineData("ASC(w)", 'N', 4, 0)]
    [InlineData("AT('a',w)", 'N', 10, 0)]
    [InlineData("DAY(d)", 'N', 3, 0)]
    [InlineData("MONTH(d)", 'N', 3, 0)]
    [InlineData("YEAR(d)", 'N', 5, 0)]
    [InlineData("d-d", 'N', 10, 0)]
    [InlineData("n+1", 'N', 9, 2)]
    [InlineData("n*2", 'N', 10, 2)]
    [InlineData("n/3", 'N', 10, 4)]
    [InlineData("ABS(n)", 'N', 8, 2)]
    [InlineData("INT(n)", 'N', 8, 0)]
    [InlineData("ROUND(n,1)", 'N', 9, 1)]
    [InlineData("VAL('12.34')", 'N', 8, 2)]
    [InlineData("12.34", 'N', 5, 2)]
    [InlineData("1", 'N', 1, 0)]
    [InlineData("1/2", 'N', 1, 0)]
    [InlineData("1.2*1.2", 'N', 6, 2)]
    [InlineData("1.2+0.03", 'N', 5, 2)]
    [InlineData("123-123", 'N', 5, 0)]
    [InlineData("SUM(n)", 'N', 16, 2)]
    [InlineData("AVG(n)", 'N', 16, 2)]
    [InlineData("COUNT(n)", 'N', 10, 0)]
    [InlineData("MIN(n)", 'N', 8, 2)]
    [InlineData("SUM(id)", 'I', 4, 0)]
    [InlineData("SUBSTR(w,2)", 'C', 4, 0)]
    [InlineData("RTRIM(w)+'|'", 'C', 6, 0)]
    [InlineData("IIF(id=1,'a','long')", 'C', 1, 0)]
    public void ComputedColumn_MetadataMatchesVfp9(string expression, char type, int width, int decimals)
    {
        var result = _session.Execute($"SELECT {expression} AS x FROM ct")!;
        var column = Assert.Single(result.Columns);
        Assert.Equal((type, width, decimals), (column.VfpType, column.Length, column.Decimals));
    }

    [Fact]
    public void CharacterResults_ArePaddedAndTruncated_AlsoAcrossJoinsAndUnion()
    {
        Assert.Equal(new[] { "a|    ", "bb|   ", "ccc|  ", "dddd| ", "eeeee|" },
            Values("SELECT RTRIM(w)+'|' AS x FROM ct"));
        Assert.Equal(new[] { "a", "l", "l", "l", "l" }, Values("SELECT IIF(id=1,'a','long') AS x FROM ct"));
        Assert.Equal(new[] { "a|    ", "bb|   ", "ccc|  ", "dddd| ", "eeeee|" },
            Values("SELECT RTRIM(a.w)+'|' AS x FROM ct a JOIN ct b ON a.id=b.id"));
        Assert.All(Values("SELECT RTRIM(w)+'|' AS x FROM ct UNION ALL SELECT 'z' AS x FROM ct"),
            v => Assert.Equal(6, Assert.IsType<string>(v).Length));
        Assert.Equal(new[] { "a" }, Values("SELECT DISTINCT IIF(id=1,'a','ab') AS x FROM ct"));
    }

    [Fact]
    public void NumericResults_RoundToTheDeclaredScale()
    {
        Assert.Equal(new object?[] { null, null, 4.1133m, null, .67m }, Values("SELECT n/3 AS x FROM ct"));
        Assert.Equal(new object?[] { 7.18m }, Values("SELECT AVG(n) AS x FROM ct"));
        Assert.Equal(new object?[] { 99m, null, 12m, null, 2m }, Values("SELECT IIF(id=1,99,n) AS x FROM ct"));
    }

    [Theory]
    [InlineData("SUM", 14.35)]
    [InlineData("AVG", 7.18)]
    [InlineData("MIN", 2.01)]
    [InlineData("MAX", 12.34)]
    public void Aggregates_IgnoreNulls_AndReturnNullForAllNullGroups(string function, double mixed)
    {
        Assert.Equal(new object?[] { null, (decimal)mixed }, Values($"SELECT {function}(n) AS x FROM ct GROUP BY g"));
        Assert.Equal(new object?[] { 0m, 2m }, Values("SELECT COUNT(n) AS x FROM ct GROUP BY g"));
    }

    [Theory]
    [InlineData("n IS NULL", new[] { 1, 2, 4 })]
    [InlineData("n IS NOT NULL", new[] { 3, 5 })]
    [InlineData("(n IS NULL OR n > 1) AND id > 1", new[] { 2, 3, 4, 5 })]
    public void NullPredicates_AreAccepted(string predicate, int[] expected)
        => Assert.Equal(expected, Values($"SELECT id FROM ct WHERE {predicate}").Cast<int>());

    [Theory]
    [InlineData("AVG(ni)", 'N', 16, 2, 3.5)]
    [InlineData("AVG(nf)", 'F', 16, 3, 1.735)]
    [InlineData("AVG(bd)", 'B', 8, 3, 1.7345)]
    [InlineData("AVG(cy)", 'Y', 8, 4, 1.7901)]
    public void NumericFamilies_KeepVfp9TypeAndScale(string expression, char type, int width, int scale, double expected)
    {
        var result = _session.Execute($"SELECT {expression} AS x FROM nt")!;
        var column = Assert.Single(result.Columns);
        Assert.Equal((type, width, scale), (column.VfpType, column.Length, column.Decimals));
        Assert.Equal(expected, Convert.ToDouble(Assert.Single(Assert.Single(result.Rows))));
    }

    [Fact]
    public void IntegerAverage_Truncates_AndCursorPreservesNullStrings()
    {
        Assert.Equal(new object?[] { 1, 4 }, Values("SELECT AVG(id) AS x FROM ct GROUP BY g"));
        _session.Execute("SELECT STR(n,8,2) AS x FROM ct INTO CURSOR q");
        Assert.Equal(new object?[] { null, null, "   12.34", null, "    2.01" }, Values("SELECT x FROM q"));
    }

    private object?[] Values(string sql) => _session.Execute(sql)!.Rows.Select(r => r[0]).ToArray();
    public void Dispose() { _session.Dispose(); _dir.Dispose(); }
}
