namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleReviewP2Tests
{
    internal static readonly (string Name, string Script, string Expected)[] Evidence =
    [
        ("$0+123456789012.34567", "x=$0+123456789012.34567\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$123,456,789,012.3457|123456789012.3457"),
        ("ROUND($123456789012.3456,4)", "x=ROUND($123456789012.3456,4)\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$123,456,789,012.3456|123456789012.3456"),
        ("NTOM(123456789012.34567)", "x=NTOM(123456789012.34567)\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$123,456,789,012.3457|123456789012.3457"),
        ("MTON(1e30)/1e30", "x=MTON(1e30)/1e30\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "N|1|1.0000"),
        ("$0+(123456789012.34567+0)", "x=$0+(123456789012.34567+0)\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$123,456,789,012.3457|123456789012.3457"),
        ("NTOM(123456789012.34567+0)", "x=NTOM(123456789012.34567+0)\nresult=TYPE('x')+'|'+TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "Y|$123,456,789,012.3457|123456789012.3457"),
        ("edge_$922337203685477.5807", "x=$922337203685477.5807\nresult=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "$922,337,203,685,477.6000|922337203685477.6000"),
        ("edge_-$922337203685477.5807-$0.0001", "x=-$922337203685477.5807-$0.0001\nresult=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "$-922,337,203,685,477.6000|-922337203685477.6000"),
        ("edge_$1234567890123.4567", "x=$1234567890123.4567\nresult=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "$1,234,567,890,123.4570|1234567890123.4570"),
        ("edge_$12345678901234.5678", "x=$12345678901234.5678\nresult=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "$12,345,678,901,234.5700|12345678901234.5700"),
        ("edge_ROUND($123456789012.3456,-1)", "x=ROUND($123456789012.3456,-1)\nresult=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,4))", "$123,456,789,010.0000|123456789010.0000"),
        ("narrow_currency_str", "result=STR($1,1,2)+'|'+STR(-$1,2,2)+'|'+STR($1,2,2)", "1|-1| 1"),
        ("field_precision", "CREATE TABLE t FREE (c Y)\nAPPEND BLANK\nx=123456789012.34567+0\nREPLACE c WITH x\nresult=TRANSFORM(c)", "$123,456,789,012.3457"),
    ];
    public static IEnumerable<object[]> Cases => Evidence.Select(c => new object[] { c.Name, c.Script, c.Expected });
    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesVfp9(string name, string script, string expected)
    {
        string actual = MicroVfpNumericScaleReviewSupport.Run(script);
        Assert.True(expected == actual, $"{name}: expected [{expected}], actual [{actual}]");
    }
}
