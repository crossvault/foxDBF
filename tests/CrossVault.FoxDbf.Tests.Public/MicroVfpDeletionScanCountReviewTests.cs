namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanCountReviewTests
{
    internal const string Setup = """
        SET TABLEPROMPT OFF
        CREATE TABLE t (k C(1))
        INSERT INTO t VALUES ('A')
        INSERT INTO t VALUES ('M')
        INSERT INTO t VALUES ('T')
        INSERT INTO t VALUES ('Z')
        """;

    internal static string Scenario(bool whileStop) => $$"""
        SET TABLEPROMPT OFF
        USE t EXCLUSIVE
        SET DELETED OFF
        INDEX ON k TAG live FOR NOT DELETED() AND k<>'{{(whileStop ? "A" : "Z")}}'
        SET ORDER TO live
        GO 2
        DELETE
        GO {{(whileStop ? "1" : "4")}}
        COUNT REST {{(whileStop ? "WHILE k<>'Z'" : "")}} TO hits
        result=TRANSFORM(hits)+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())
        SKIP -1
        result=result+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())
        """;

    [Theory]
    [InlineData(false, "1|5|.T.|3|.F.")]
    [InlineData(true, "2|4|.F.|3|.F.")]
    public void CountReanchorsAfterOrderReload(bool whileStop, string expected) =>
        Assert.Equal(expected, MicroVfpSqlDmlConsolidatedTests.Run(Setup, Scenario(whileStop)));
}
