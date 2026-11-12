using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Result descriptors over Float fields. The VFP types are VFP9's AFIELDS answers (internal oracle
/// SqlFloatResultTypeVfp9OracleTests); the CLR types are the ones the columns had before the VFP type
/// changed, because the CLR mapping of F values is a separate decision.
/// </summary>
public sealed class SqlFloatResultTypeTests
{
    public static TheoryData<string, string> Cases() => new()
    {
        { "SELECT f2 AS v FROM fn UNION ALL SELECT n2 FROM fn", "v=F,20,2,Decimal" },
        { "SELECT n2 AS v FROM fn UNION ALL SELECT f2 FROM fn", "v=N,20,2,Decimal" },
        { "SELECT f2 AS v FROM fn UNION SELECT n2 FROM fn", "v=F,20,2,Decimal" },
        { "SELECT f2 AS v FROM fn UNION ALL SELECT ii FROM fn", "v=F,20,2,Decimal" },
        { "SELECT ii AS v FROM fn UNION ALL SELECT f2 FROM fn", "v=F,20,2,Decimal" },
        { "SELECT ii AS v FROM fn UNION ALL SELECT yy FROM fn", "v=Y,8,4,Decimal" },
        { "SELECT bb AS v FROM fn UNION ALL SELECT n2 FROM fn", "v=B,8,3,Decimal" },
        { "SELECT n2 AS v FROM fn UNION ALL SELECT bb FROM fn", "v=N,20,3,Decimal" },
        { "SELECT f2 AS v FROM fn UNION ALL SELECT bb FROM fn", "v=F,20,3,Double" },
        { "SELECT bb AS v FROM fn UNION ALL SELECT f2 FROM fn", "v=B,8,3,Decimal" },
        { "SELECT SUM(f2) AS v FROM fn UNION ALL SELECT SUM(n2) FROM fn", "v=F,20,2,Decimal" },
        { "SELECT f2 AS v FROM fn UNION ALL SELECT f2 FROM fn", "v=F,20,2,Double" },
        { "SELECT ABS(f2) AS ab, IIF(.T., f2, n2) AS iif1, NVL(f2, 0) AS nv1, ICASE(.T., f2, n2) AS ic1 FROM fn",
          "ab=N,20,2,Double iif1=N,20,2,Double nv1=N,20,2,Double ic1=N,20,2,Double" },
        { "SELECT f2 AS a, SUM(f2) AS s, MAX(f2) AS m FROM fn GROUP BY 1", "a=F,20,2,Double s=F,20,2,Double m=F,20,2,Double" },
        { "SELECT SUM(ABS(f2)) AS s, MAX(IIF(.T., f2, n2)) AS m FROM fn", "s=N,20,2,Double m=N,20,2,Double" },
        { "SELECT ABS(f2) AS v FROM fn UNION ALL SELECT f2 FROM fn", "v=N,20,2,Decimal" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Float_result_descriptor_matches_vfp9_type_and_keeps_the_clr_type(string select, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_float_type");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE fn FREE (n2 N(20,2), f2 F(20,2), ii I, yy Y, bb B(3))");
        session.Execute("INSERT INTO fn VALUES (2.5, 2.5, 4, 5.5, 6.5)");
        session.Execute("INSERT INTO fn VALUES (3.75, 3.75, 5, 6.25, 7.25)");

        var columns = session.Execute(select)!.Columns;

        Assert.Equal(expected, string.Join(" ", columns.Select(c =>
            $"{c.Name.ToLowerInvariant()}={c.VfpType},{c.Length},{c.Decimals},{c.ClrType.Name}")));
    }

    [Theory]
    [InlineData("SELECT f2 AS v FROM fn UNION ALL SELECT yy FROM fn")]
    [InlineData("SELECT yy AS v FROM fn UNION ALL SELECT f2 FROM fn")]
    [InlineData("SELECT n2 AS v FROM fn UNION ALL SELECT yy FROM fn")]
    [InlineData("SELECT yy AS v FROM fn UNION ALL SELECT bb FROM fn")]
    public void Currency_union_with_n_f_or_b_is_error_1851(string select)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_float_type_y");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE fn FREE (n2 N(20,2), f2 F(20,2), ii I, yy Y, bb B(3))");

        var error = Assert.Throws<FoxDbfSqlException>(() => session.Execute(select));
        Assert.Equal(1851, error.VfpErrorNumber);
    }
}
