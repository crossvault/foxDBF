namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanSumReviewTests
{
    internal const string Setup = """
        SET TABLEPROMPT OFF
        CREATE TABLE t (id I, rest I, record I, next I)
        FOR i=1 TO 4
            INSERT INTO t VALUES (i,3,i,10)
        ENDFOR
        """;

    internal static string Scenario(string command) => $$"""
        SET TABLEPROMPT OFF
        USE t
        GO 3
        x=-99
        err=0
        TRY
            {{command}}
        CATCH TO failure
            err=failure.ErrorNo
        ENDTRY
        result=ALLTRIM(STR(x,12,2))+'|'+TRANSFORM(err)+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())+'|'+TRANSFORM(BOF())
        """;

    [Theory]
    [InlineData("SUM rest TO x", "-99.00|10|3|.F.|.F.")]
    [InlineData("SUM record+1 TO x", "-99.00|10|1|.F.|.F.")]
    [InlineData("SUM id+next TO x", "50.00|0|5|.T.|.F.")]
    [InlineData("SUM NEXT 5 id TO x", "7.00|0|5|.T.|.F.")]
    [InlineData("SUM id TO x REST", "7.00|0|5|.T.|.F.")]
    [InlineData("SUM id FOR id>0 NEXT 1", "-99.00|0|3|.F.|.F.")]
    [InlineData("AVERAGE rest TO x", "-99.00|10|3|.F.|.F.")]
    [InlineData("AVERAGE record+1 TO x", "-99.00|10|1|.F.|.F.")]
    [InlineData("AVERAGE id+next TO x", "12.50|0|5|.T.|.F.")]
    [InlineData("AVERAGE NEXT 5 id TO x", "3.50|0|5|.T.|.F.")]
    [InlineData("AVERAGE id TO x REST", "3.50|0|5|.T.|.F.")]
    [InlineData("AVERAGE id FOR id>0 NEXT 1", "-99.00|0|3|.F.|.F.")]
    [InlineData("SUM t.rest TO x", "12.00|0|5|.T.|.F.")]
    [InlineData("SUM t.record+1 TO x", "14.00|0|5|.T.|.F.")]
    [InlineData("SUM NEXT (1 + 1) id TO x", "7.00|0|4|.F.|.F.")]
    [InlineData("SUM id,rest TO x,y", "10.00|0|5|.T.|.F.")]
    [InlineData("AVERAGE t.rest TO x", "3.00|0|5|.T.|.F.")]
    [InlineData("AVERAGE t.record+1 TO x", "3.50|0|5|.T.|.F.")]
    [InlineData("AVERAGE NEXT (1 + 1) id TO x", "3.50|0|4|.F.|.F.")]
    [InlineData("AVERAGE id,rest TO x,y", "2.50|0|5|.T.|.F.")]
    [InlineData("SUM NEXT 2 (id) TO x", "7.00|0|4|.F.|.F.")]
    [InlineData("SUM NEXT (1 + 1) (id) TO x", "7.00|0|4|.F.|.F.")]
    [InlineData("AVERAGE NEXT 2 (id) TO x", "3.50|0|4|.F.|.F.")]
    [InlineData("AVERAGE NEXT (1 + 1) (id) TO x", "3.50|0|4|.F.|.F.")]
    [InlineData("SUM NEXT 1 + 1 TO x", "-99.00|10|3|.F.|.F.")]
    [InlineData("SUM NEXT 1 + 1 id TO x", "7.00|0|4|.F.|.F.")]
    [InlineData("SUM RECORD 1 + 1 TO x", "-99.00|10|2|.F.|.F.")]
    [InlineData("AVERAGE NEXT 1 + 1 TO x", "-99.00|10|3|.F.|.F.")]
    [InlineData("AVERAGE NEXT 1 + 1 id TO x", "3.50|0|4|.F.|.F.")]
    [InlineData("AVERAGE RECORD 1 + 1 TO x", "-99.00|10|2|.F.|.F.")]
    public void ExpressionsAndScope(string command, string expected) =>
        Assert.Equal(expected, MicroVfpSqlDmlConsolidatedTests.Run(Setup, Scenario(command)));
}
