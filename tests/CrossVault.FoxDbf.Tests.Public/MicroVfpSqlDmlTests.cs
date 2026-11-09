using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlTests
{
    internal const string Setup = """
        CREATE TABLE t (id I, n I, k C(10))
        INSERT INTO t VALUES (1,10,'ALPHA')
        INSERT INTO t VALUES (2,20,'BRAVO')
        INSERT INTO t VALUES (3,30,'CHARLIE')
        CREATE TABLE other (id I)
        INSERT INTO other VALUES (99)
        """;

    internal const string Procedures = """
        FUNCTION changevalue
        LPARAMETERS value
        RETURN value + m.increment
        ENDFUNC
        """;

    internal static string Scenario(string kind) => "SET DELETED OFF\nUSE t IN 1\nUSE other IN 2\nSELECT t\nGO 2\n_TALLY=77\n" + (kind switch
    {
        "update" => "UPDATE t SET k='ZULU' WHERE k='ALPHA'",
        "delete" => "DELETE FROM t WHERE id >= 2",
        "nomatch" => "UPDATE t SET n=99 WHERE id=9",
        "other" => "SELECT other\nUPDATE t SET n=n+1 WHERE id=1",
        "closed" => "USE\nSELECT other\nUPDATE t SET n=n+1 WHERE id=1",
        "alias" => "USE t ALIAS x\nUPDATE x SET n=n+1 WHERE id=1",
        "expressions" => "increment=5\nUPDATE t SET n=changevalue(n)+m.increment, id=id+increment, k=LOWER(k) WHERE id<=2",
        "simultaneous" => "UPDATE t SET id=n, n=id",
        "visibility" => "DELETE\nSET DELETED ON\nSET FILTER TO id=1\nUPDATE t SET n=99",
        "deletedoff" => "DELETE\nUPDATE t SET n=99\nDELETE FROM t\nDELETE FROM t",
        "badcolumn" => Catch("UPDATE t SET absent=1"),
        "badvalue" => Catch("UPDATE t SET n='bad' WHERE id=1"),
        "missing" => "SET TABLEPROMPT OFF\n" + Catch("UPDATE no_such_table SET n=1"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    }) + "\n" + State + "\nSELECT " + (kind == "alias" ? "x" : "t") + "\n" + Dump;

    internal static string Catch(string command) => "TRY\n" + command + "\nCATCH TO failure\nresult=result+'ERR:'+TRANSFORM(failure.ErrorNo)+'|'\nENDTRY";
    internal const string State = "result=result+ALIAS()+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())+'|'+TRANSFORM(_TALLY)+'|'+TRANSFORM(USED('t'))+CHR(10)";
    internal const string Dump = """
        FOR row=1 TO 3
            GO row
            result=result+TRANSFORM(id)+','+TRANSFORM(n)+','+ALLTRIM(k)+','+TRANSFORM(DELETED())+';'
        ENDFOR
        """;

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("nomatch")]
    [InlineData("other")]
    [InlineData("closed")]
    [InlineData("alias")]
    [InlineData("expressions")]
    [InlineData("simultaneous")]
    [InlineData("visibility")]
    [InlineData("deletedoff")]
    [InlineData("badcolumn")]
    [InlineData("badvalue")]
    [InlineData("missing")]
    public void SqlCommands_ExecuteInPrograms(string kind)
    {
        // These full state snapshots are pinned by MicroVfpSqlDmlVfp9OracleTests.
        string original = "1,10,ALPHA,.F.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;";
        string expected = kind switch
        {
            "update" => "T|3|.F.|1|.T.\n" + original.Replace("ALPHA", "ZULU"),
            "delete" => "T|3|.F.|2|.T.\n1,10,ALPHA,.F.;2,20,BRAVO,.T.;3,30,CHARLIE,.T.;",
            "nomatch" => "T|3|.F.|0|.T.\n" + original,
            "other" or "closed" => "OTHER|1|.F.|1|.T.\n" + original.Replace("1,10", "1,11"),
            "alias" => "X|3|.F.|1|.F.\n" + original.Replace("1,10", "1,11"),
            "expressions" => "T|3|.F.|2|.T.\n6,20,alpha,.F.;7,30,bravo,.F.;3,30,CHARLIE,.F.;",
            "simultaneous" => "T|3|.F.|3|.T.\n10,10,ALPHA,.F.;20,20,BRAVO,.F.;30,30,CHARLIE,.F.;",
            "visibility" => "T|3|.F.|2|.T.\n1,99,ALPHA,.F.;2,20,BRAVO,.T.;3,99,CHARLIE,.F.;",
            "deletedoff" => "T|3|.F.|3|.T.\n1,99,ALPHA,.T.;2,99,BRAVO,.T.;3,99,CHARLIE,.T.;",
            "badcolumn" => "ERR:1806|T|2|.F.|77|.T.\n" + original,
            "badvalue" => "ERR:9|T|1|.F.|77|.T.\n" + original,
            "missing" => "ERR:1|T|2|.F.|77|.T.\n" + original,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.Equal(expected, Run(Scenario(kind)));
    }

    internal static string BufferScenario(int mode, bool delete) => $"""
        SET DELETED OFF
        SET MULTILOCKS ON
        USE t
        =CURSORSETPROP('Buffering',{mode})
        GO 2
        {(delete ? "DELETE FROM t WHERE id<=2" : "UPDATE t SET n=n+1 WHERE id<=2")}
        {State}
        result=result+TRANSFORM(GETFLDSTATE(0))+'|'+TRANSFORM(TABLEREVERT(.T.))+CHR(10)
        {Dump}
        """;

    [Theory]
    [InlineData(3, false)]
    [InlineData(5, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public void Buffering_UsesVfpRowCommitAndTableRevert(int mode, bool delete)
    {
        string rows = mode == 3
            ? delete ? "1,10,ALPHA,.T.;2,20,BRAVO,.T.;3,30,CHARLIE,.F.;" : "1,11,ALPHA,.F.;2,21,BRAVO,.F.;3,30,CHARLIE,.F.;"
            : "1,10,ALPHA,.F.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;";
        Assert.Equal($"T|3|.F.|2|.T.\n1|{(mode == 3 ? 0 : 2)}\n" + rows, Run(BufferScenario(mode, delete)));
    }

    internal static string IndexScenario(int transaction) => $"""
        SET DELETED OFF
        USE t EXCLUSIVE
        INDEX ON k TAG structural FOR NOT DELETED()
        {(transaction == 0 ? "INDEX ON k TO standalone COMPACT FOR NOT DELETED()" : "")}
        GO 1
        REPLACE n WITH 11
        {(transaction == 0 ? "" : "BEGIN TRANSACTION")}
        UPDATE t SET k='ZULU' WHERE id=1
        DELETE FROM t WHERE id=2
        {(transaction == 0 ? "" : transaction == 1 ? "END TRANSACTION" : "ROLLBACK")}
        SET ORDER TO TAG structural
        result=result+TRANSFORM(SEEK('ZULU'))+'|'+TRANSFORM(SEEK('BRAVO'))+'|'
        {(transaction == 0 ? "SET ORDER TO 2\nresult=result+TRANSFORM(SEEK('ZULU'))+'|'+TRANSFORM(SEEK('BRAVO'))+CHR(10)" : "")}
        {Dump}
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IndexesAndTxn_UpdateLiveSeekAndPreserveRollback(int transaction)
    {
        string expected = transaction == 2
            ? ".F.|.T.|1,11,ALPHA,.F.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;"
            : ".T.|.F.|" + (transaction == 0 ? ".T.|.F.\n" : "") + "1,11,ZULU,.F.;2,20,BRAVO,.T.;3,30,CHARLIE,.F.;";
        Assert.Equal(expected, Run(IndexScenario(transaction)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rules_RejectTheFailingRowAndKeepEarlierUpdates(bool record)
    {
        string setup = record ? Setup.Replace("k C(10))", "k C(10), CHECK n<25)") : Setup.Replace("n I,", "n I CHECK n<25,");
        string scenario = "USE t\nGO 3\n_TALLY=77\n" + Catch("UPDATE t SET n=n+10") + "\n" + State + "\n" + Dump;
        Assert.Equal($"ERR:{(record ? 1583 : 1582)}|T|2|.F.|77|.T.\n1,20,ALPHA,.F.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;",
            Run(scenario, setup, enforce: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Triggers_RejectTheFailingRowAndKeepEarlierWrites(bool delete)
    {
        string procedure = delete ? "DELETE" : "UPDATE";
        string scenario = $"PROCEDURE __RI_{procedure}_t\nRETURN id<>2\nENDPROC\nUSE t\nGO 3\n_TALLY=77\n" +
            Catch(delete ? "DELETE FROM t" : "UPDATE t SET n=n+10") + "\n" + State + "\n" + Dump;
        string rows = delete ? "1,10,ALPHA,.T.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;" : "1,20,ALPHA,.F.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;";
        Assert.Equal("ERR:1539|T|2|.F.|77|.T.\n" + rows, Run(scenario, enforce: true));
    }

    [Theory]
    [InlineData("UPDATE t SET n=unknown", 1806)]
    [InlineData("UPDATE t SET n=1 WHERE unknown=1", 1806)]
    [InlineData("DELETE FROM t WHERE unknown=1", 1806)]
    [InlineData("UPDATE t SET n=1 WHERE 1", 1833)]
    [InlineData("DELETE FROM no_such_table", 1)]
    [InlineData("DELETE missing FROM t WHERE id=1", 1)]
    public void InvalidSql_PreservesPointerRowsAndTally(string command, int error)
    {
        string scenario = "USE t\nGO 2\n_TALLY=77\n" + Catch(command) + "\n" + State + "\n" + Dump;
        Assert.Equal($"ERR:{error}|T|2|.F.|77|.T.\n1,10,ALPHA,.F.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;", Run(scenario));
    }

    [Theory]
    [InlineData("field", 3)]
    [InlineData("field", 5)]
    [InlineData("record", 3)]
    [InlineData("record", 5)]
    [InlineData("update_trigger", 3)]
    [InlineData("update_trigger", 5)]
    [InlineData("delete_trigger", 3)]
    [InlineData("delete_trigger", 5)]
    public void BufferedValidation_RetainsRejectedEditsAndVfpPartialCommits(string kind, int mode)
    {
        bool trigger = kind.EndsWith("trigger");
        bool delete = kind == "delete_trigger";
        string setup = kind == "record" ? Setup.Replace("k C(10))", "k C(10), CHECK n<25)")
            : kind == "field" ? Setup.Replace("n I,", "n I CHECK n<25,") : Setup;
        string procedures = trigger ? $"PROCEDURE __RI_{(delete ? "DELETE" : "UPDATE")}_t\nRETURN id<>2\nENDPROC\n" : "";
        string scenario = procedures + $"SET MULTILOCKS ON\nUSE t\n=CURSORSETPROP('Buffering',{mode})\nGO 3\n_TALLY=77\n" +
            Catch(delete ? "DELETE FROM t" : "UPDATE t SET n=n+10") + "\n" + State + "\n" +
            "saved=TABLEUPDATE(.T.)\nresult=result+TRANSFORM(saved)+'|'+TRANSFORM(TABLEREVERT(.T.))+CHR(10)\n" + Dump;
        string head = trigger && mode == 5 ? "T|3|.F.|3|.T.\n"
            : $"ERR:{(trigger ? 1539 : kind == "field" ? 1582 : 1583)}|T|2|.F.|77|.T.\n";
        string first = delete ? "1,10,ALPHA,.T.;" : mode == 3 || trigger ? "1,20,ALPHA,.F.;" : "1,10,ALPHA,.F.;";
        Assert.Equal(head + $".F.|{(mode == 3 ? 1 : 2)}\n" + first + "2,20,BRAVO,.F.;3,30,CHARLIE,.F.;",
            Run(scenario, setup, enforce: true));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Replace_BufferedTriggerFailureKeepsEarlierRowsAndTally(int mode)
    {
        string scenario = $"PROCEDURE __RI_UPDATE_t\nRETURN id<>2\nENDPROC\nSET MULTILOCKS ON\nUSE t\n=CURSORSETPROP('Buffering',{mode})\nGO 3\n_TALLY=77\n" +
            Catch("REPLACE ALL n WITH n+10") + "\n" + State + "\nsaved=TABLEUPDATE(.T.)\nresult=result+TRANSFORM(saved)+'|'+TRANSFORM(TABLEREVERT(.T.))+CHR(10)\n" + Dump;
        string head = mode == 3 ? "ERR:1539|T|2|.F.|2|.T.\n.F.|1\n" : "T|4|.T.|3|.T.\n.F.|2\n";
        Assert.Equal(head + "1,20,ALPHA,.F.;2,20,BRAVO,.F.;3,30,CHARLIE,.F.;", Run(scenario, enforce: true));
    }

    [Fact]
    public void Delete_RefreshesFilteredCdxAndIdxReads()
    {
        Assert.Equal(".T.|.F.|.T.|.F.\n1,11,ZULU,.F.;2,20,BRAVO,.T.;3,30,CHARLIE,.F.;",
            Run(IndexScenario(0).Replace("DELETE FROM t WHERE id=2", "DELETE FOR id=2")));
    }

    [Fact]
    public void DeletedRow_SourceParticipatesWithDeletedOff()
    {
        Assert.Equal("1|.T.", Run("SET DELETED OFF\nUSE t IN 1\nUSE other IN 2\nSELECT t\nGO 1\nDELETE\nSELECT other\nDELETE other FROM t WHERE t.id=1\nresult=TRANSFORM(_TALLY)+'|'+TRANSFORM(DELETED())"));
    }

    [Fact]
    public void DeletedRow_BufferedRuleUsesItsStoredFields()
    {
        Assert.Equal(".T.|11|.T.", Run("SET DELETED OFF\nSET MULTILOCKS ON\nUSE t\nGO 1\nDELETE\n=CURSORSETPROP('Buffering',5)\nUPDATE t SET n=n+1 WHERE id=1\nresult=TRANSFORM(TABLEUPDATE(.T.))+'|'\nGO 1\nresult=result+TRANSFORM(n)+'|'+TRANSFORM(DELETED())",
            Setup.Replace("id I,", "id I CHECK id>0,"), enforce: true));
    }

    private static string Run(string scenario, string? setup = null, bool enforce = false)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sqldml");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        vfp.Execute((setup ?? Setup) + "\nCLOSE TABLES ALL\n" + Procedures);
        vfp.EnforceReferentialIntegrity = enforce;
        vfp.Execute("PUBLIC result\nresult=''\n" + scenario);
        return vfp.Memory.Get("result").AsString.Replace("\r", "");
    }
}
