using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Write;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlFinalReviewTests(ITestOutputHelper output)
{
    internal static string OmittedSetup(string type, bool defaults) =>
        $"CREATE TABLE t (id I, note {type} {(defaults ? "NOT NULL DEFAULT " + Default(type) : "NULL")} CHECK NOT ISNULL(note))";

    private static string Default(string type) => type switch
    {
        "C(4)" or "M" => "'ok'", "N(4)" => "7", "D" => "{^2026-01-02}", "L" => ".T.", _ => "''"
    };

    internal const string OmittedScenario = """
        SET TABLEPROMPT OFF
        USE t
        SET NULL ON
        TRY
            INSERT INTO t (id) VALUES (1)
            result='OK'
        CATCH TO failure
            result='ERR:'+TRANSFORM(failure.ErrorNo)
        ENDTRY
        result=result+'|'+TRANSFORM(RECCOUNT())
        """;

    public static IEnumerable<object[]> OmittedCases()
    {
        foreach (string type in new[] { "M", "W", "G", "C(4)", "N(4)", "D", "L" })
            yield return new object[] { type, false };
        foreach (string type in new[] { "M", "C(4)", "N(4)", "D", "L" })
            yield return new object[] { type, true };
    }

    [Theory]
    [MemberData(nameof(OmittedCases))]
    public void OmittedNullableFields(string type, bool defaults)
    {
        string actual = Run(OmittedSetup(type, defaults), OmittedScenario);
        output.WriteLine(actual);
        Assert.Equal(defaults ? "OK|1" : "ERR:1582|0", actual);
    }


    internal const string CounterProcedure = "FUNCTION counted\ncalls=calls+1\nRETURN calls<2\nENDFUNC";
    internal static string CounterSetup(bool record) => "PUBLIC calls\ncalls=-10\nCREATE TABLE t (id N(4), n I" +
        (record ? "," : "") + " CHECK counted())\nINSERT INTO t VALUES (1,0)\nCREATE TABLE src (id N(4), n I)\nINSERT INTO src VALUES (1,7)";
    internal static string CounterScenario(int mode) => $"""
        SET TABLEPROMPT OFF
        SET MULTILOCKS ON
        USE src IN 0
        USE t IN 0
        SELECT t
        =CURSORSETPROP('Buffering',{mode})
        calls=0
        TRY
            UPDATE ON id FROM src REPLACE n WITH src.n
            result='OK'
        CATCH TO failure
            result='ERR:'+TRANSFORM(failure.ErrorNo)
        ENDTRY
        result=result+'|'+TRANSFORM(calls)+'|'+TRANSFORM(n)
        {(mode > 1 ? "=TABLEREVERT(.T.)" : "")}
        """;

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void XbaseRulesOnce(int mode, bool record) =>
        Assert.Equal(mode == 5 && record ? "OK|0|7" : "OK|1|7", Run(CounterSetup(record), CounterScenario(mode), CounterProcedure));

    internal const string AppendSetup = "CREATE TABLE t (id N(4), n I)\nCREATE TABLE src (id N(4), n I)\nINSERT INTO src VALUES (1,7)";
    internal const string AppendScenario = "SET TABLEPROMPT OFF\nSET MULTILOCKS ON\nUSE src IN 0\nUSE t IN 0\nSELECT t\n=CURSORSETPROP('Buffering',5)\n" +
        "INSERT INTO t VALUES (1,0)\nUPDATE ON id FROM src REPLACE n WITH src.n\nresult=TRANSFORM(TABLEUPDATE(.F.))+'|'+TRANSFORM(GETNEXTMODIFIED(0))+'|'+TRANSFORM(TABLEREVERT(.T.))";

    [Fact]
    public void UpdatedAppendLeavesNoPhantomEdit() => Assert.Equal(".T.|0|0", Run(AppendSetup, AppendScenario));

    internal static string KeywordSetup(string field, string alias) =>
        $"CREATE TABLE t ({field} N(4), n I)\nINSERT INTO t VALUES (1,0)\nCREATE TABLE src ({field} N(4), n I)\nINSERT INTO src VALUES (1,7)";
    internal static string KeywordScenario(string field, string alias) => $"SET TABLEPROMPT OFF\nUSE src ALIAS {alias} IN 0\nUSE t IN 0\nSELECT t\n" +
        MicroVfpSqlDmlTests.Catch($"UPDATE ON {field} FROM {alias} REPLACE n WITH {alias}.n") + "\nresult=result+TRANSFORM(n)";
    public static IEnumerable<object[]> Keywords()
    {
        foreach (string name in new[] { "from", "replace", "random", "on", "with" })
        {
            yield return new object[] { name, "src" };
            yield return new object[] { "id", name };
        }
    }
    [Theory]
    [MemberData(nameof(Keywords))]
    public void XbaseKeywordIdentifiers(string field, string alias) =>
        Assert.Equal("7", Run(KeywordSetup(field, alias), KeywordScenario(field, alias)));

    internal const string PartialSetup = "CREATE TABLE t (id N(4), n I, k C(4))\nINSERT INTO t VALUES (1,0,'keep')\n" +
        "CREATE TABLE src (id N(4))\nINSERT INTO src VALUES (1)";
    internal static string PartialScenario(int mode, bool xbase) => $"""
        SET TABLEPROMPT OFF
        SET MULTILOCKS ON
        USE src IN 0
        USE t IN 0
        SELECT t
        =CURSORSETPROP('Buffering',{mode})
        {MicroVfpSqlDmlTests.Catch((xbase ? "UPDATE ON id FROM src " : "") + "REPLACE n WITH 1, k WITH 123")}
        result=result+TRANSFORM(n)+'|'+ALLTRIM(k)
        {(mode > 1 ? "=TABLEREVERT(.T.)" : "")}
        """;
    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    public void PartialAssignments(int mode, bool xbase) => Assert.Equal("ERR:9|1|keep", Run(PartialSetup, PartialScenario(mode, xbase)));

    internal static string WidthSetup(string type) => $"CREATE TABLE t (id {type}(4), n I)\nINSERT INTO t VALUES ('a',0)\nCREATE TABLE src (id {type}(8), n I)\nINSERT INTO src VALUES ('a',7)";
    [Theory]
    [InlineData("V")]
    [InlineData("Q")]
    public void XbaseVariableKeyWidths(string type) => Assert.Equal("ERR:9|0", Run(WidthSetup(type), KeywordScenario("id", "src")));


    internal static string MessageSetup(bool record, bool text) => "CREATE TABLE t (n I" + (record ? "," : "") +
        " CHECK n>0" + (text ? " ERROR 'Rejected '+TRANSFORM(n)" : "") + ")\nINSERT INTO t VALUES (1)";
    internal static string MessageScenario(bool update) => $"""
        SET TABLEPROMPT OFF
        USE t
        DIMENSION errors[1]
        TRY
            {(update ? "UPDATE t SET n=-1" : "INSERT INTO t VALUES (-1)")}
            result='OK'
        CATCH TO failure
            =AERROR(errors)
            result=TRANSFORM(failure.ErrorNo)+'|'+MESSAGE()+'|'+errors[1,2]
        ENDTRY
        """;
    internal static string RuleMessage(bool record, bool text, bool update) => text ? "Rejected " + (record ? "-1" : update ? "1" : "0") :
        record ? "Record validation rule is violated." : "Field N validation rule is violated.";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void RuleErrorMessages(bool record, bool text, bool update)
    {
        string message = RuleMessage(record, text, update);
        Assert.Equal($"{(record ? 1583 : 1582)}|{message}|{message}", Run(MessageSetup(record, text), MessageScenario(update)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AdoRuleErrorMessages(bool record, bool update)
    {
        using var dir = new MicroVfpTestSupport.TempDir("ado_message");
        using var connection = OpenAdo(dir, MessageSetup(record, true));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = update ? "UPDATE t SET n=-1" : "INSERT INTO t VALUES (-1)";
        var error = Assert.Throws<FoxDbfException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(record ? 1583 : 1582, error.VfpErrorNumber);
        Assert.Equal(RuleMessage(record, true, update), error.Message);
    }

    internal static string MissingSetup(string expression) => $"CREATE TABLE t (n I CHECK IIF(n=1,.T.,{expression}))\nINSERT INTO t VALUES (1)";
    [Theory]
    [InlineData(false, "nosuchudf()", 1)]
    [InlineData(true, "nosuchudf()", 1)]
    [InlineData(false, "nosuchvar", 12)]
    [InlineData(true, "nosuchvar", 12)]
    public void AdoRuleEvaluationErrors(bool update, string expression, int code)
    {
        using var dir = new MicroVfpTestSupport.TempDir("ado_rule_eval");
        using var connection = OpenAdo(dir, MissingSetup(expression));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = update ? "UPDATE t SET n=-1" : "INSERT INTO t VALUES (-1)";
        var error = Assert.Throws<FoxDbfException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(code, error.VfpErrorNumber);
        Assert.NotNull(error.InnerException);
    }

    internal const string AliasSetup = "CREATE TABLE t (id I, origin C(10) DEFAULT ALIAS())\nINSERT INTO t VALUES (1,'base')\nCREATE TABLE other (id I)\nINSERT INTO other VALUES (50)";
    internal const string AliasOpen = "SET TABLEPROMPT OFF\nCLOSE TABLES ALL\nUSE t AGAIN ALIAS x IN 0\nUSE other IN 0\nSELECT other";
    internal static string AliasScenario(bool update) => AliasOpen + "\n" + MicroVfpSqlDmlTests.Catch(update
        ? "UPDATE t SET id=id+1" : "INSERT INTO t (id) VALUES (2)") +
        "\nSELECT x\nGO BOTTOM\nresult=result+TRANSFORM(id)+'|'+ALLTRIM(origin)";
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdoReusesDifferentAlias(bool update)
    {
        using var dir = new MicroVfpTestSupport.TempDir("ado_alias");
        using var connection = OpenAdo(dir, AliasSetup);
        connection.Interpreter.Execute(AliasOpen);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = update ? "UPDATE t SET id=id+1" : "INSERT INTO t (id) VALUES (2)";
        Assert.Equal(1, cmd.ExecuteNonQuery());
        connection.Interpreter.Execute("SELECT x\nGO BOTTOM\nresult=TRANSFORM(id)+'|'+ALLTRIM(origin)");
        Assert.Equal(update ? "2|base" : "2|X", connection.Interpreter.Memory.Get("result").AsString);
        Assert.Equal(2, connection.Session.OpenAreas.Count());
    }

    internal static FoxDbfConnection OpenAdo(MicroVfpTestSupport.TempDir dir, string setup)
    {
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        var connection = new FoxDbfConnection($"Data Source={dir.File("probe.dbc")};EnforceRules=on");
        connection.Open();
        connection.Interpreter.Execute(setup);
        return connection;
    }

    [Fact]
    public void G1ComparisonIsNotAnAssignment() => Assert.Equal("ERR:107|0", Run(
        "PUBLIC calls\ncalls=0\nCREATE TABLE t (n I, CHECK (calls=calls+1)<2)",
        "USE t\n" + MicroVfpSqlDmlTests.Catch("INSERT INTO t VALUES (1)") + "\nresult=result+TRANSFORM(calls)"));

    internal static string Run(string setup, string scenario, string procedures = "", bool enforce = true) =>
        MicroVfpSqlDmlConsolidatedTests.Run(setup, scenario, procedures, enforce);
}

