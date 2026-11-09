namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanRecordReviewTests
{
    internal const string Setup = "SET TABLEPROMPT OFF\nCREATE TABLE t (id I)\nINSERT INTO t VALUES (7)";

    internal static string Scenario(string command, bool eof) => $$"""
        SET TABLEPROMPT OFF
        USE t
        GO TOP
        {{(eof ? "SKIP" : "")}}
        x=-99
        err=0
        TRY
            {{command}}
        CATCH TO failure
            err=failure.ErrorNo
        ENDTRY
        result=TRANSFORM(x)+'|'+TRANSFORM(err)+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())+'|'+TRANSFORM(BOF())
        """;

    [Theory]
    [InlineData("SUM id RECORD 5 TO x", false)]
    [InlineData("SUM id RECORD -1 TO x", false)]
    [InlineData("SUM RECORD -1", false)]
    [InlineData("AVERAGE id RECORD 5 TO x", false)]
    [InlineData("AVERAGE id RECORD -1 TO x", false)]
    [InlineData("SUM id RECORD 0 TO x", true)]
    [InlineData("AVERAGE id RECORD 0 TO x", true)]
    public void InvalidRecordPreservesPointer(string command, bool eof) =>
        Assert.Equal(eof ? "-99|5|2|.T.|.F." : "-99|5|1|.F.|.F.",
            MicroVfpSqlDmlConsolidatedTests.Run(Setup, Scenario(command, eof)));
}
