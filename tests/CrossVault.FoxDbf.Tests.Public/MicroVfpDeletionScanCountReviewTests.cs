namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanCountReviewTests
{
    internal static string RemovedExpressionScenario(string command) => $$"""
        SET TABLEPROMPT OFF
        USE t EXCLUSIVE
        SET DELETED OFF
        INDEX ON IIF(DELETED(),'Z',k) TAG live FOR NOT DELETED()
        SET ORDER TO live
        GO 2
        DELETE
        {{command}}
        result=ALLTRIM(STR(x,12,2))+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())
        SKIP -1
        result=result+'|'+TRANSFORM(RECNO())
        """;

    [Theory]
    [InlineData("COUNT REST TO x", "1.00|4|.T.|3")]
    [InlineData("SUM id REST TO x", "2.00|4|.T.|3")]
    [InlineData("AVERAGE id REST TO x", "2.00|4|.T.|3")]
    [InlineData("LOCATE REST FOR id>2\nx=IIF(FOUND(),RECNO(),0)", "0.00|4|.T.|3")]
    [InlineData("LOCATE REST\nCONTINUE\nx=IIF(FOUND(),RECNO(),0)", "0.00|4|.T.|3")]
    public void RemovedExpressionKeyScope(string command, string expected) => Assert.Equal(expected,
        MicroVfpSqlDmlConsolidatedTests.Run(MicroVfpDeletionScanReviewTests.Setup, RemovedExpressionScenario(command)));

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
