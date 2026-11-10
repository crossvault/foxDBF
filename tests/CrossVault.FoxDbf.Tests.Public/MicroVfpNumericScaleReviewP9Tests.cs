namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleReviewP9Tests
{
    internal static readonly (string Name, string Script, string Expected)[] Evidence =
    [
        ("aggregate_MIN_", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MIN() AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_MIN_c", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MIN(c) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "$2.0000"),
        ("aggregate_MIN_c,n", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MIN(c,n) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:1300"),
        ("aggregate_MIN_*", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MIN(*) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_MAX_", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MAX() AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_MAX_c", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MAX(c) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "$2.0000"),
        ("aggregate_MAX_c,n", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MAX(c,n) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:1300"),
        ("aggregate_MAX_*", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MAX(*) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_SUM_", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT SUM() AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_SUM_c", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT SUM(c) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "$2.0000"),
        ("aggregate_SUM_c,n", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT SUM(c,n) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:1300"),
        ("aggregate_SUM_*", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT SUM(*) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_AVG_", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT AVG() AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_AVG_c", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT AVG(c) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "$2.0000"),
        ("aggregate_AVG_c,n", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT AVG(c,n) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:1300"),
        ("aggregate_AVG_*", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT AVG(*) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_COUNT_", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT COUNT() AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_COUNT_c", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT COUNT(c) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "1"),
        ("aggregate_COUNT_c,n", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT COUNT(c,n) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:1300"),
        ("aggregate_COUNT_*", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT COUNT(*) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "1"),
        ("aggregate_CNT_", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT CNT() AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:152"),
        ("aggregate_CNT_c", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT CNT(c) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "1"),
        ("aggregate_CNT_c,n", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT CNT(c,n) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "ERR:1300"),
        ("aggregate_CNT_*", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT CNT(*) AS x FROM t INTO CURSOR zz\nresult=TRANSFORM(x)", "1"),
        ("syntax_SELECT MAX(c,n) FROM t INTO CURSOR zz", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MAX(c,n) FROM t INTO CURSOR zz\n=AFIELDS(aa)\nresult=TRANSFORM(EVALUATE(aa[1,1]))", "ERR:1300"),
        ("syntax_SELECT MAX(c,n) x FROM t INTO CURSOR zz", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MAX(c,n) x FROM t INTO CURSOR zz\n=AFIELDS(aa)\nresult=TRANSFORM(EVALUATE(aa[1,1]))", "ERR:1300"),
        ("syntax_SELECT MAX(c,n)+1 AS x FROM t INTO CURSOR zz", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT MAX(c,n)+1 AS x FROM t INTO CURSOR zz\n=AFIELDS(aa)\nresult=TRANSFORM(EVALUATE(aa[1,1]))", "ERR:1300"),
        ("syntax_SELECT c FROM t WHERE MAX(c,n)>0 INTO CURSOR zz", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT c FROM t WHERE MAX(c,n)>0 INTO CURSOR zz\n=AFIELDS(aa)\nresult=TRANSFORM(EVALUATE(aa[1,1]))", "$2.0000"),
        ("syntax_SELECT CNT(c) FROM t INTO CURSOR zz", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT CNT(c) FROM t INTO CURSOR zz\n=AFIELDS(aa)\nresult=TRANSFORM(EVALUATE(aa[1,1]))", "1"),
        ("syntax_SELECT CNT(*)+1 AS x FROM t INTO CURSOR zz", "CREATE TABLE t FREE (c Y,n N(8,2))\nINSERT INTO t VALUES ($2,1)\nSELECT CNT(*)+1 AS x FROM t INTO CURSOR zz\n=AFIELDS(aa)\nresult=TRANSFORM(EVALUATE(aa[1,1]))", "2"),
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
