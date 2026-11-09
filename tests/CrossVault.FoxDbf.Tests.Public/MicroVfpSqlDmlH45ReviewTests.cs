namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpSqlDmlH45ReviewTests
{
    internal const string MessageSetup = "CREATE TABLE t (n I CHECK n>0 ERROR 'Rejected '+TRANSFORM(n))\nINSERT INTO t VALUES (1)";
    internal static string MessageScenario(int buffering, bool append) => $"SET MULTILOCKS ON\nUSE t\n=CURSORSETPROP('Buffering',{buffering})\n" +
        (append ? "INSERT INTO t VALUES (5)\n" : "") + "REPLACE n WITH 5\nDIMENSION errors[1]\nTRY\nREPLACE n WITH -1\nCATCH TO failure\n=AERROR(errors)\nresult=TRANSFORM(failure.ErrorNo)+'|'+failure.Message+'|'+errors[1,2]\nENDTRY\nresult=result+'|'+TRANSFORM(n)" +
        (buffering > 1 ? "\n=TABLEREVERT(.T.)" : "");
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void H4_PreviousFieldValue(int buffering, bool append) => Assert.Equal(buffering == 1 ? "1582|Rejected 5|Rejected 5|5" : "1582|Rejected -1|Rejected -1|-1",
        MicroVfpSqlDmlFinalReviewTests.Run(MessageSetup, MessageScenario(buffering, append)));
}
