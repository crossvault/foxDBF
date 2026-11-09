using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
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
    internal const string ReentrantSetup = "PUBLIC calls,seen\ncalls=0\nseen=''\nCREATE TABLE t (id I AUTOINC NEXTVALUE 1 STEP 1, n I, CHECK nest())";
    internal const string ReentrantProcedure = "FUNCTION nest\ncalls=calls+1\nseen=seen+TRANSFORM(id)+','\nIF calls>8\nRETURN .F.\nENDIF\nIF n=1\nINSERT INTO t (n) VALUES (2)\nENDIF\nRETURN .T.\nENDFUNC";
    internal static string ReentrantScenario(bool slow) => "USE t EXCLUSIVE\n" + (slow ? "INDEX ON n TAG n CANDIDATE\n" : "") +
        MicroVfpSqlDmlTests.Catch("INSERT INTO t (n) VALUES (1)") + "\nSELECT t\nresult=result+TRANSFORM(calls)+'|'+seen+'|'+TRANSFORM(RECCOUNT())\nSCAN\nresult=result+'|'+TRANSFORM(id)+','+TRANSFORM(n)\nENDSCAN";
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task H5_ReentrantRuleTerminates(bool slow) => Assert.Equal("ERR:1887|1|1,|0",
        await Task.Run(() => MicroVfpSqlDmlFinalReviewTests.Run(ReentrantSetup, ReentrantScenario(slow), ReentrantProcedure)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void H5_RejectedRuleConsumesAutoincrement(bool slow) => Assert.Equal("ERR:1887|1|1,|0|next:2",
        MicroVfpSqlDmlFinalReviewTests.Run(ReentrantSetup, ReentrantScenario(slow) +
            "\nINSERT INTO t (n) VALUES (3)\nresult=result+'|next:'+TRANSFORM(id)", ReentrantProcedure));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void H5_ValidationFailureConsumesAutoincrement(bool field, bool slow) => Assert.Equal(field ? "ERR:1582|2" : "ERR:1583|2",
        MicroVfpSqlDmlFinalReviewTests.Run("CREATE TABLE t (id I AUTOINC, n I" + (field ? " CHECK n>0" : ", CHECK n>0") + ")",
            "USE t EXCLUSIVE\n" + (slow ? "INDEX ON n TAG n CANDIDATE\n" : "") + MicroVfpSqlDmlTests.Catch("INSERT INTO t (n) VALUES (-1)") +
            "\nINSERT INTO t (n) VALUES (1)\nresult=result+TRANSFORM(id)"));

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task H5_ContentionRevalidatesAtMostOnce(bool fallback) => await Task.Run(() =>
    {
        using var dir = new MicroVfpTestSupport.TempDir("h5_contention");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE t (id I AUTOINC, n I)");
        using var writer = fallback ? null : DbfWriter.Open(dir.File("t.dbf"));
        int calls = 0;
        session.InsertRowValidator = (_, _, _, _, _) =>
        {
            if (++calls > 2) throw new InvalidOperationException("Unbounded revalidation");
            if (writer is not null) Assert.False(writer.HasHeldLocks);
            using var other = DbfWriter.Open(dir.File("t.dbf"));
            other.AppendRecord(new object?[] { 0, 99 });
        };
        Assert.Throws<IOException>(() =>
        {
            if (fallback) session.Execute("INSERT INTO t (n) VALUES (1)");
            else DmlExecutor.AppendInsertValuesRow(session, writer!, (InsertStatement)SqlParser.Parse("INSERT INTO t (n) VALUES (1)"));
        });
        Assert.Equal(2, calls);
        Assert.Equal(2, session.Execute("SELECT * FROM t")!.Rows!.Count());
    });

}
