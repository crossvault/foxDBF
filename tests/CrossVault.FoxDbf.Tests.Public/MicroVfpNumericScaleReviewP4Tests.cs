namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleReviewP4Tests
{
    internal static readonly (string Name, string Script, string Expected)[] Evidence =
    [
        ("mod_MOD(7,2.4)", "x=MOD(7,2.4)\nresult=TRANSFORM(x)+'|'+TRANSFORM(x+0)+'|'+ALLTRIM(STR(x,20,8))", "2|2|2.20000000"),
        ("mod_7%2.4", "x=7%2.4\nresult=TRANSFORM(x)+'|'+TRANSFORM(x+0)+'|'+ALLTRIM(STR(x,20,8))", "2|2|2.20000000"),
        ("mod_MOD(7.00,2.4)", "x=MOD(7.00,2.4)\nresult=TRANSFORM(x)+'|'+TRANSFORM(x+0)+'|'+ALLTRIM(STR(x,20,8))", "2.20|2.20|2.20000000"),
        ("mod_MOD(7,2.40000)", "x=MOD(7,2.40000)\nresult=TRANSFORM(x)+'|'+TRANSFORM(x+0)+'|'+ALLTRIM(STR(x,20,8))", "2|2|2.20000000"),
        ("sqlmod_MOD(i,2.4)CURSOR", "CREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT MOD(i,2.4) AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+TRANSFORM(x)", "N|12|0|2"),
        ("sqlmod_MOD(i,2.4)TABLE", "CREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT MOD(i,2.4) AS x FROM t INTO TABLE zz\nUSE zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+TRANSFORM(x)", "N|12|0|2"),
        ("sqlmod_i%2.4CURSOR", "CREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i%2.4 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+TRANSFORM(x)", "N|12|0|2"),
        ("sqlmod_i%2.4TABLE", "CREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i%2.4 AS x FROM t INTO TABLE zz\nUSE zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+TRANSFORM(x)", "N|12|0|2"),
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
