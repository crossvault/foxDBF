namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleReviewP6Tests
{
    internal static readonly (string Name, string Script, string Expected)[] Evidence =
    [
        ("cap_16i/2", "SET DECIMALS TO 16\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i/2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "N|20|18|3.5000"),
        ("cap_16i^2", "SET DECIMALS TO 16\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i^2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "N|20|16|49.0000"),
        ("cap_17i/2", "SET DECIMALS TO 17\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i/2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "N|20|18|3.5000"),
        ("cap_17i^2", "SET DECIMALS TO 17\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i^2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "N|20|17|49.0000"),
        ("cap_18i/2", "SET DECIMALS TO 18\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i/2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "N|20|18|3.5000"),
        ("cap_18i^2", "SET DECIMALS TO 18\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i^2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "N|20|18|49.0000"),
        ("cap_19i/2", "SET DECIMALS TO 19\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i/2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("cap_19i^2", "SET DECIMALS TO 19\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i^2 AS x FROM t INTO CURSOR zz\nSELECT zz\n=AFIELDS(aa)\nresult=aa[1,2]+'|'+TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "ERR:10"),
        ("storage_CURSOR", "SET DECIMALS TO 18\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i^2 AS x FROM t INTO CURSOR zz\n=AFIELDS(aa)\nresult=TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "20|18|49.0000"),
        ("storage_TABLE", "SET DECIMALS TO 18\nCREATE TABLE t FREE (i I)\nINSERT INTO t VALUES (7)\nSELECT i^2 AS x FROM t INTO TABLE zz\nUSE zz\n=AFIELDS(aa)\nresult=TRANSFORM(aa[1,3])+'|'+TRANSFORM(aa[1,4])+'|'+ALLTRIM(STR(x,30,4))", "20|18|49.0000"),
        ("field_N(6,2)49", "CREATE TABLE t FREE (n N(6,2))\nAPPEND BLANK\nREPLACE n WITH 49\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "49.00000000"),
        ("field_N(6,2)1234.5678", "CREATE TABLE t FREE (n N(6,2))\nAPPEND BLANK\nREPLACE n WITH 1234.5678\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "1234.60000000"),
        ("field_N(6,2)12345.67", "CREATE TABLE t FREE (n N(6,2))\nAPPEND BLANK\nREPLACE n WITH 12345.67\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "12346.00000000"),
        ("field_N(6,2)-49", "CREATE TABLE t FREE (n N(6,2))\nAPPEND BLANK\nREPLACE n WITH -49\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "-49.00000000"),
        ("field_N(6,2)9999999", "CREATE TABLE t FREE (n N(6,2))\nAPPEND BLANK\nREPLACE n WITH 9999999\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "ERR:39"),
        ("field_N(20,18)49", "CREATE TABLE t FREE (n N(20,18))\nAPPEND BLANK\nREPLACE n WITH 49\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "49.00000000"),
        ("field_N(20,18)1234.5678", "CREATE TABLE t FREE (n N(20,18))\nAPPEND BLANK\nREPLACE n WITH 1234.5678\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "1234.56780000"),
        ("field_N(20,18)12345.67", "CREATE TABLE t FREE (n N(20,18))\nAPPEND BLANK\nREPLACE n WITH 12345.67\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "12345.67000000"),
        ("field_N(20,18)-49", "CREATE TABLE t FREE (n N(20,18))\nAPPEND BLANK\nREPLACE n WITH -49\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "-49.00000000"),
        ("field_N(20,18)9999999", "CREATE TABLE t FREE (n N(20,18))\nAPPEND BLANK\nREPLACE n WITH 9999999\nUSE\nUSE t\nresult=ALLTRIM(STR(n,30,8))", "9999999.00000000"),
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
