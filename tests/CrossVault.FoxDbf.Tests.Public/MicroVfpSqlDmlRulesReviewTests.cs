using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlRulesReviewTests
{
    internal const string Procedures = "FUNCTION counted\ncalls=calls+1\nRETURN .T.\nENDFUNC";
    internal const string CountSetup = "PUBLIC calls\ncalls=0\nCREATE TABLE t (n I CHECK counted())";
    internal const string CountScenario = "USE t\ncalls=0\nINSERT INTO t VALUES (1)\nresult=TRANSFORM(calls)+'|'\ncalls=0\nUPDATE t SET n=2\nresult=result+TRANSFORM(calls)";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rules_RunOnce(bool ado) => Assert.Equal("1|1", RunCounts(ado));

    internal static string RunCounts(bool ado)
    {
        if (!ado) return Run(CountSetup, CountScenario, Procedures);
        using var dir = new MicroVfpTestSupport.TempDir("rules_ado");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var connection = new FoxDbfConnection($"Data Source={dir.File("probe.dbc")};EnforceRules=on");
        connection.Open();
        var vfp = connection.Interpreter;
        vfp.Execute(Procedures + "\n" + CountSetup);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (1)";
        vfp.Execute("calls=0");
        command.ExecuteNonQuery();
        int insert = vfp.Memory.Get("calls").AsInteger;
        command.CommandText = "UPDATE t SET n=2";
        vfp.Execute("calls=0");
        command.ExecuteNonQuery();
        return insert + "|" + vfp.Memory.Get("calls").AsInteger;
    }

    [Fact]
    public void Rules_ProviderReusesTargetArea()
    {
        using var dir = new MicroVfpTestSupport.TempDir("rules_areas");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var connection = new FoxDbfConnection($"Data Source={dir.File("probe.dbc")};EnforceRules=on");
        connection.Open();
        connection.Interpreter.Execute("CREATE TABLE t (n I)");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (1)";
        for (int i = 0; i < 4; i++) command.ExecuteNonQuery();
        Assert.Single(connection.Session.OpenAreas);
    }

    public static IEnumerable<object[]> PrecisionCases()
    {
        foreach (int buffer in new[] { 1, 3, 5 })
        foreach (string command in new[] { "INSERT INTO t VALUES (9.9)", "REPLACE n WITH 9.9", "UPDATE t SET n=9.9" })
        foreach (bool accepts in new[] { true, false })
            yield return new object[] { buffer, command, accepts };
    }
    internal static string PrecisionSetup(bool accepts) => "CREATE TABLE t (n N(4,0) CHECK " + (accepts ? "n=10" : "n<10") + ")\nINSERT INTO t VALUES (" + (accepts ? "10" : "1") + ")";
    internal static string PrecisionScenario(int buffer, string command) => $"SET MULTILOCKS ON\nUSE t\n=CURSORSETPROP('Buffering',{buffer})\n" + MicroVfpSqlDmlTests.Catch(command) + "\nresult=result+TRANSFORM(n)\n" + (buffer > 1 ? "=TABLEREVERT(.T.)" : "");
    [Theory]
    [MemberData(nameof(PrecisionCases))]
    public void Rules_SeeStoredPrecision(int buffer, string command, bool accepts) => Assert.Equal(
        accepts ? "10" : "ERR:1582|" + (command.StartsWith("INSERT") ? "0" : buffer == 1 ? "1" : "10"),
        Run(PrecisionSetup(accepts), PrecisionScenario(buffer, command)));

    internal const string AutoSetup = "CREATE TABLE t (id I AUTOINC NEXTVALUE 1 STEP 1 CHECK id>0, n I)";
    internal const string AutoScenario = "USE t\nINSERT INTO t (n) VALUES (7)\nresult=TRANSFORM(id)\nINSERT INTO t (n) VALUES (8)\nresult=result+'|'+TRANSFORM(id)";
    [Fact]
    public void Rules_SeeGeneratedAutoincrement() => Assert.Equal("1|2", Run(AutoSetup, AutoScenario));

    internal const string AppendScenario = "SET MULTILOCKS ON\nUSE t\n=CURSORSETPROP('Buffering',5)\nINSERT INTO t VALUES (1)\ncalls=0\nUPDATE t SET n=2\nresult=TRANSFORM(calls)+'|'\n=TABLEUPDATE(.T.)\nresult=result+TRANSFORM(calls)";
    [Fact]
    public void Rules_BufferedAppendUpdateRunsOnce() => Assert.Equal("1|1", Run(CountSetup, AppendScenario, Procedures));

    internal const string RetrySetup = "PUBLIC limit\nlimit=100\nCREATE TABLE t (id I, n I, CHECK n<=m.limit)\nINSERT INTO t VALUES (1,5)\nINSERT INTO t VALUES (2,5)";
    internal const string Disk = "USE t AGAIN ALIAS persisted IN 0\nSELECT persisted\nGO 1\nresult=result+TRANSFORM(n)+','\nGO 2\nresult=result+TRANSFORM(n)+'|'\nUSE IN persisted\nSELECT t";
    internal const string RetryScenario = "SET MULTILOCKS ON\nPUBLIC limit\nlimit=20\nUSE t\n=CURSORSETPROP('Buffering',5)\nGO 1\nREPLACE n WITH 15\nGO 2\nREPLACE n WITH 25\nresult=TRANSFORM(TABLEUPDATE(.T.))+'|'\nFLUSH\n=STRTOFILE(FILETOSTR('t.dbf'),'first.dbf')\n" + Disk + "\nlimit=10\nREPLACE n WITH 5\nresult=result+TRANSFORM(TABLEUPDATE(.T.))+'|'\nFLUSH\n=STRTOFILE(FILETOSTR('t.dbf'),'second.dbf')\n" + Disk + "\n=TABLEREVERT(.T.)";
    [Fact]
    public void Rules_TableUpdatePrevalidatesBeforePublishing() => Assert.Equal(".F.|5,5|.T.|15,5|", Run(RetrySetup, RetryScenario));

    internal static string ArrayScenario(int buffer) => $"SET MULTILOCKS ON\nUSE t\n=CURSORSETPROP('Buffering',{buffer})\nDIMENSION laInputReview[3,2]\nlaInputReview[1,1]=1\nlaInputReview[1,2]=1\nlaInputReview[2,1]=2\nlaInputReview[2,2]=2\nlaInputReview[3,1]=-1\nlaInputReview[3,2]=3\n" + MicroVfpSqlDmlTests.Catch("INSERT INTO t FROM ARRAY laInputReview") + "\nresult=result+TRANSFORM(RECCOUNT())+'|'\nSELECT n FROM t INTO ARRAY laStored\nresult=result+TRANSFORM(_TALLY)\nSELECT t\n" + (buffer > 1 ? "=TABLEREVERT(.T.)" : "");
    [Theory]
    [InlineData(1, "ERR:1582|2|2")]
    [InlineData(3, "ERR:1582|2|2")]
    [InlineData(5, "ERR:1582|2|0")]
    public void Rules_ArrayKeepsAcceptedPrefix(int buffer, string expected) => Assert.Equal(expected, Run("CREATE TABLE t (n I CHECK n>0, id I)", ArrayScenario(buffer)));

    public static IEnumerable<object[]> ParserCases()
    {
        foreach (string op in new[] { "=", ">", "<", "#", "!=", "==", "<>", "$" })
        foreach (bool spaced in new[] { false, true })
            yield return new object[] { "DELETE FOR from" + (spaced ? " " : "") + op + (spaced ? " " : "") + (op == "$" ? "'a'" : "1"), spaced ? "ERR:36|" : "ERR:1|" };
    }
    [Theory]
    [MemberData(nameof(ParserCases))]
    public void Parser_FromOperators(string command, string expected) => Assert.Equal(expected,
        Run("CREATE TABLE t (from I)\nINSERT INTO t VALUES (1)", "SET TABLEPROMPT OFF\nUSE t\n" + MicroVfpSqlDmlTests.Catch(command)));

    [Fact]
    public void Rules_ValueCountErrorIsIndependentOfEnforcement()
    {
        string plain = CountError(false), enforced = CountError(true);
        Assert.Equal(plain, enforced);
    }
    private static string CountError(bool enforce)
    {
        using var dir = new MicroVfpTestSupport.TempDir("count_error");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session) { EnforceReferentialIntegrity = enforce };
        vfp.Execute("CREATE TABLE t (n I)");
        return Assert.ThrowsAny<Exception>(() => vfp.Execute("INSERT INTO t VALUES (1,2)")).Message;
    }

    internal static string Run(string setup, string scenario, string procedures = "") =>
        MicroVfpSqlDmlConsolidatedTests.Run("SET TABLEPROMPT OFF\n" + setup, "SET TABLEPROMPT OFF\n" + scenario, procedures, true);
}
