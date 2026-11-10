namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleReviewP1Tests
{
    internal static readonly (string Name, string Script, string Expected)[] Evidence =
    [
        ("$1*1e30", "x=$1*1e30\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:1988"),
        ("$900000000000000*1e15", "x=$900000000000000*1e15\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:1988"),
        ("$1e14%1e-20", "x=$1e14%1e-20\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("$922337203685477.5807+$0.0001", "x=$922337203685477.5807+$0.0001\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:1988"),
        ("-$922337203685477.5807-$0.0002", "x=-$922337203685477.5807-$0.0002\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:1988"),
        ("NTOM(1e30)", "x=NTOM(1e30)\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:1988"),
        ("$1e14%0.00003", "x=$1e14%0.00003\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("$0+1e30", "x=$0+1e30\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:1988"),
        ("$1/1e30", "x=$1/1e30\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:1988"),
        ("$1e14%1", "x=$1e14%1\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("$1e14%10000", "x=$1e14%10000\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("$2147483648%1", "x=$2147483648%1\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$0.0000|0.0000"),
        ("$1%1e-20", "x=$1%1e-20\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$0.0000|0.0000"),
        ("$1e12%1", "x=$1e12%1\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("$100000000000000%1e-20", "x=$100000000000000%1e-20\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$0.0000|0.0000"),
        ("$100000000000000%0.00003", "x=$100000000000000%0.00003\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$0.0000|0.0000"),
        ("$1e13%1", "x=$1e13%1\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("replace", "CREATE TABLE t FREE (c Y)\nAPPEND BLANK\nREPLACE c WITH 1e30\nresult=TRANSFORM(c)", "ERR:39"),
        ("sql", "CREATE TABLE t FREE (c Y)\nINSERT INTO t VALUES ($900000000000000)\nSELECT c*1e15 AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(zz.x)", "ERR:1988"),
    ];
    public static IEnumerable<object[]> Cases => Evidence.Select(c => new object[] { c.Name, c.Script, c.Expected });
    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesVfp9(string name, string script, string expected)
        => Assert.Equal(expected, MicroVfpNumericScaleReviewSupport.Run(script));
}
