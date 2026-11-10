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
