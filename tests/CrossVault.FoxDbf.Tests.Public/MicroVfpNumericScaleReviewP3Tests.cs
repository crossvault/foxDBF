namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleReviewP3Tests
{
    internal static readonly (string Name, string Script, string Expected)[] Evidence =
    [
        ("having_source", "CREATE TABLE t FREE (id I,c Y)\nINSERT INTO t VALUES (1,$1.2345)\nINSERT INTO t VALUES (2,$0.0001)\nSELECT c FROM t GROUP BY c HAVING c/2*2=c INTO CURSOR zz\nresult=TRANSFORM(RECCOUNT())", "0"),
        ("having_alias", "CREATE TABLE t FREE (id I,c Y)\nINSERT INTO t VALUES (1,$1.2345)\nINSERT INTO t VALUES (2,$0.0001)\nSELECT c AS price FROM t GROUP BY c HAVING price/2*2=price INTO CURSOR zz\nresult=TRANSFORM(RECCOUNT())", "0"),
        ("delete", "CREATE TABLE t FREE (id I,c Y)\nINSERT INTO t VALUES (1,$1.2345)\nINSERT INTO t VALUES (2,$0.0001)\nDELETE FROM t WHERE c/2*2=c\nresult=TRANSFORM(_TALLY)", "0"),
        ("delete_source", "CREATE TABLE t FREE (id I,c Y)\nINSERT INTO t VALUES (1,$1.2345)\nINSERT INTO t VALUES (2,$0.0001)\nCREATE TABLE s FREE (sid I,price Y)\nINSERT INTO s VALUES (1,$1.2345)\nINSERT INTO s VALUES (2,$0.0001)\nCLOSE TABLES ALL\nUSE t\nUSE s IN 0\nSELECT t\nDELETE t FROM s WHERE t.id=s.sid AND s.price/2*2=s.price\nresult=TRANSFORM(_TALLY)", "0"),
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
