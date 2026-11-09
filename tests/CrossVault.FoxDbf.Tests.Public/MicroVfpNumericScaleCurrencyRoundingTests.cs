using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleCurrencyRoundingTests
{
    internal static readonly (string Expression, string Expected)[] Cases =
    [
        ("$1*1.00005", "Y|$1.0001|                  1.0001000000"),
        ("$1*1.00015", "Y|$1.0002|                  1.0002000000"),
        ("-$1*1.00005", "Y|$-1.0001|                 -1.0001000000"),
        ("$1/32", "Y|$0.0312|                  0.0312000000"),
        ("-$1/32", "Y|$-0.0312|                 -0.0312000000"),
        ("$1.2345+0.000049", "Y|$1.2345|                  1.2345000000"),
        ("$1.2345-0.000049", "Y|$1.2345|                  1.2345000000"),
        ("$1.2345*0.000049", "Y|$0.0000|                  0.0000000000"),
        ("$1.2345/1.000049", "Y|$1.2345|                  1.2345000000"),
        ("NTOM(1.23445)", "Y|$1.2345|                  1.2345000000"),
        ("NTOM(-1.23445)", "Y|$-1.2345|                 -1.2345000000"),
        ("MTON(1.5)", "N|1.5|                  1.5000000000"),
        ("MTON(1.50000)", "N|1.50000|                  1.5000000000"),
        ("MOD(1.23456,$1)", "N|0.23456|                  0.2345600000"),
        ("1.23456%$1", "N|0.23456|                  0.2345600000"),
        ("$1%0.123456", "Y|$0.0124|                  0.0124000000"),
        ("ROUND($1.2345,3)", "Y|$1.2350|                  1.2350000000"),
        ("ROUND(-$1.2345,3)", "Y|$-1.2350|                 -1.2350000000"),
        ("MAX($2.5,1)", "N|2.5000|                  2.5000000000"),
        ("MIN($1.5,2)", "N|1.5000|                  1.5000000000"),
        ("MAX($1.5,$2.5,1)", "N|2.5000|                  2.5000000000"),
    ];
    internal static string Scenario(string expression) => "SET DECIMALS TO 4\nx=" + expression + "\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+STR(x,30,10)";
    [Fact]
    public void ConversionAndArithmeticRounding_MatchVfp9()
    {
        using var session = new VfpSession();
        using var vfp = new VfpInterpreter(session);
        var failures = new List<string>();
        foreach (var (expression, expected) in Cases)
        {
            vfp.Execute(Scenario(expression));
            string actual = vfp.Memory.Get("result").AsString;
            if (actual != expected) failures.Add(expression + $": expected [{expected}], actual [{actual}]");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
    internal const string FieldSetup = "CREATE TABLE t (c Y,n N(12,6))\nINSERT INTO t VALUES ($1.2345,1)";
    internal static readonly (string Name, string Script, string Expected)[] FieldCases =
    [
        ("sql_filter", "SELECT n FROM t WHERE c/2*2=c INTO CURSOR zz\nresult=TRANSFORM(RECCOUNT())", "0"),
        ("sql_join", "SELECT TRANSFORM(a.c/2*2) AS x FROM t a JOIN t b ON a.n=b.n INTO CURSOR zz\nresult=ALLTRIM(zz.x)", "$1.23"),
        ("sql_join_precision", "SELECT a.c/2*2 AS x FROM t a JOIN t b ON a.n=b.n INTO CURSOR zz\nresult=STR(zz.x,12,4)", "      1.2344"),
        ("native_update", "UPDATE t SET n=c/2*2\nGO TOP\nresult=STR(n,12,4)", "      1.2344"),
        ("index_rounding", "INDEX ON c/2*2 TAG rounded\nSEEK 1.2344\nresult=TRANSFORM(FOUND())", ".T.")
    ];
    [Fact]
    public void FieldsAcrossSqlAndIndexes_MatchVfp9()
    {
        foreach (var test in FieldCases)
            Assert.Equal(test.Expected, MicroVfpXbaseUpdateTests.Run(FieldSetup, "USE t\n" + test.Script));
    }
    [Fact]
    public void NativeSqlFilterAndUpdate_UseCurrencyArithmetic()
    {
        using var dir = new MicroVfpTestSupport.TempDir("currency_native");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using (var vfp = new VfpInterpreter(session)) vfp.Execute(FieldSetup + "\nUSE");
        Assert.Empty(session.Execute("SELECT n FROM t WHERE c/2*2=c")!.Rows);
        session.Execute("UPDATE t SET n=c/2*2");
        Assert.Equal(1.2344m, Convert.ToDecimal(session.Execute("SELECT n FROM t")!.Rows.Single()[0]));
    }
}
