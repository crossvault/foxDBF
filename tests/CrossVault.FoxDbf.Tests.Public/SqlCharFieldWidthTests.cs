using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// SQL expressions see a C field at its full declared width, as VFP9 does (answers taken from VFP9): a C(5) field
/// holding <c>'N'</c> is <c>'N    '</c>, so <c>region + region</c> is <c>'N    N    '</c> in a single-table SELECT, with
/// an alias qualifier and across a JOIN; <c>LEN(region)</c> is 5 and <c>SUBSTR</c> / <c>RIGHT</c> see the blanks.
/// Comparisons on the padded value follow SET ANSI (OFF cuts the longer operand, ON pads the shorter).
/// </summary>
public sealed class SqlCharFieldWidthTests : IDisposable
{
    private const string Join = " FROM cust ca JOIN opened ao ON ca.acctno = ao.acctno";

    private readonly MicroVfpTestSupport.TempDir _dir = new("sql_char_width");
    private readonly VfpSession _s = new(new EvaluationContext { Deleted = true });

    public SqlCharFieldWidthTests()
    {
        _s.OpenDirectory(_dir.Path);
        _s.Execute("CREATE TABLE cust (cname C(20), acctno I, region C(5))");
        _s.Execute("INSERT INTO cust VALUES ('Alice', 10, 'N')");
        _s.Execute("INSERT INTO cust VALUES ('Bob', 20, 'S')");
        _s.Execute("CREATE TABLE opened (acctno I, balance I, region C(5))");
        _s.Execute("INSERT INTO opened VALUES (10, 100, 'X')");
        _s.Execute("INSERT INTO opened VALUES (20, 200, 'Y')");
        _s.Execute("CREATE TABLE w (c5 C(5), c10 C(10), lead C(5))");
        _s.Execute("INSERT INTO w VALUES ('A', 'A', '  A')");
    }

    public void Dispose() { _s.Dispose(); _dir.Dispose(); }

    private object?[] Column(string sql) => _s.Execute(sql)!.Rows.Select(r => r[0]).ToArray();

    private int Count(string sql) => System.Convert.ToInt32(_s.Execute(sql)!.Rows.Single()[0]);

    [Theory]
    [InlineData("SELECT region + region AS x FROM cust")]
    [InlineData("SELECT p.region + p.region AS x FROM cust p")]
    public void Concatenation_KeepsThePaddingOfBothFields(string sql)
    {
        var r = _s.Execute(sql)!;
        Assert.Equal(new object?[] { "N    N    ", "S    S    " }, r.Rows.Select(row => row[0]).ToArray());
        Assert.Equal('C', r.Columns[0].VfpType);
        Assert.Equal(10, r.Columns[0].Length);
    }

    [Fact]
    public void Concatenation_AcrossAJoin_KeepsThePadding()
    {
        Assert.Equal(new object?[] { "N    X    ", "S    Y    " }, Column("SELECT ca.region + ao.region AS x" + Join));
        Assert.Equal(new[] { 10m, 10m }, Column("SELECT LEN(ca.region + ao.region) AS x" + Join).Select(v => System.Convert.ToDecimal(v)).ToArray());
        Assert.Equal(1, Count("SELECT COUNT(*) AS n" + Join + " WHERE ca.region + ao.region = 'N    X'"));
    }

    [Fact]
    public void StringFunctions_SeeTheFullWidth()
    {
        Assert.Equal(new[] { 5m, 5m }, Column("SELECT LEN(region) AS x FROM cust").Select(v => System.Convert.ToDecimal(v)).ToArray());
        Assert.Equal("N    |", Column("SELECT region + '|' AS x FROM cust")[0]);
        Assert.Equal("N|    ", Column("SELECT region - '|' AS x FROM cust")[0]);
        Assert.Equal("N  |", Column("SELECT PADR(region, 3) + '|' AS x FROM cust")[0]);
        Assert.Equal("  |", Column("SELECT SUBSTR(region, 2, 2) + '|' AS x FROM cust")[0]);
        Assert.Equal("  |", Column("SELECT RIGHT(region, 2) + '|' AS x FROM cust")[0]);
        Assert.Equal("  A  |", Column("SELECT lead + '|' AS x FROM w")[0]);
    }

    [Theory]
    [InlineData(false, "region + '|' = 'N    |'", 1)]
    [InlineData(true, "region + '|' = 'N    |'", 1)]
    [InlineData(false, "region + '|' = 'N|'", 0)]
    [InlineData(false, "LEN(region) = 5", 2)]
    [InlineData(false, "region == 'N'", 1)]
    [InlineData(true, "region == 'N    '", 1)]
    [InlineData(false, "region = ''", 2)]
    [InlineData(true, "region = ''", 0)]
    [InlineData(true, "region = 'N      '", 1)]
    public void Comparisons_OnThePaddedValue_FollowSetAnsi(bool ansi, string where, int expected)
    {
        _s.Context.Ansi = ansi;
        Assert.Equal(expected, Count("SELECT COUNT(*) AS n FROM cust WHERE " + where));
    }

    [Theory]
    [InlineData("SELECT COUNT(*) AS n FROM w WHERE c5 = c10", 1)]
    [InlineData("SELECT COUNT(*) AS n FROM w WHERE c5 == c10", 1)]
    [InlineData("SELECT COUNT(*) AS n FROM w WHERE c5 + c5 = c10", 0)]
    [InlineData("SELECT COUNT(*) AS n FROM w WHERE lead = 'A'", 0)]
    [InlineData("SELECT COUNT(*) AS n FROM w WHERE lead = '  A'", 1)]
    [InlineData("SELECT COUNT(*) AS n FROM w a JOIN w b ON a.c5 = b.c10", 1)]
    [InlineData("SELECT COUNT(*) AS n FROM w a JOIN w b ON a.c5 + 'z' = b.c10 + 'z'", 0)]
    public void FieldAgainstField_OfAnotherWidth(string sql, int expected)
        => Assert.Equal(expected, Count(sql));
}
