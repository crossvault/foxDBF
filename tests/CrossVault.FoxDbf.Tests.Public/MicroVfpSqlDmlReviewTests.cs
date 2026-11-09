using System.Diagnostics;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlReviewTests(ITestOutputHelper output)
{
    internal const string Rule = "FUNCTION countRule\nhits=hits+1\nRETURN .T.\nENDFUNC";
    internal const string Trigger = "PROCEDURE __RI_UPDATE_t\nRETURN id<>2\nENDPROC";
    internal static string Scenario(string kind)
    {
        if (kind.StartsWith("nav_"))
        {
            string[] parts = kind.Split('_');
            return $"""
                USE t EXCLUSIVE
                SET DELETED {parts[1]}
                INDEX ON k TAG live FOR NOT DELETED()
                SET ORDER TO live
                {(parts[2] == "recall" ? "GO 2\nDELETE\nGO 2\nRECALL\nSKIP" : parts[2] == "skip" ? "GO 1\nDELETE\nSKIP" : parts[2] == "back" ? "GO 3\nDELETE\nSKIP -1" : "SCAN NEXT 8\nresult=result+TRANSFORM(id)+','\nDELETE\nENDSCAN")}
                result=result+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())+'|'+TRANSFORM(BOF())
                """;
        }
        if (kind.StartsWith("parser_"))
            return "SET TABLEPROMPT OFF\nCREATE TABLE words (from I)\nINSERT INTO words VALUES (1)\nINSERT INTO words VALUES (2)\nUSE words ALIAS from\nGO 1\n_TALLY=77\n" +
                MicroVfpSqlDmlTests.Catch(kind == "parser_for" ? "DELETE FOR from=1" : "DELETE IN from") +
                "\nresult=result+TRANSFORM(_TALLY)+'|'\nGO 1\nresult=result+TRANSFORM(DELETED())";
        if (kind.StartsWith("source_"))
            return $"""
                SET MULTILOCKS ON
                USE t IN 1
                USE other IN 2
                SELECT t
                =CURSORSETPROP('Buffering',{(kind.EndsWith("row") ? 3 : 5)})
                GO 2
                REPLACE id WITH 42
                SELECT other
                DELETE other FROM t WHERE t.id=42
                result=TRANSFORM(_TALLY)+'|'+TRANSFORM(DELETED())+'|'+TRANSFORM(RECNO('t'))
                SELECT t
                result=result+'|'+TRANSFORM(id)+'|'+TRANSFORM(TABLEREVERT(.T.))
                """;
        if (kind.StartsWith("rule_"))
        {
            string[] parts = kind.Split('_');
            return $"""
                PUBLIC hits
                hits=0
                SET MULTILOCKS ON
                USE t
                =CURSORSETPROP('Buffering',{parts[1]})
                GO 1
                {(kind.Contains("_sql") ? "UPDATE t SET n=11 WHERE id=1" : "REPLACE n WITH 11")}
                result=TRANSFORM(hits)+'|'
                hits=0
                {(parts[2] == "move" ? "SKIP" : "saved=TABLEUPDATE(" + (parts[2] == "all" ? ".T." : ".F.") + ")")}
                result=result+TRANSFORM(hits)
                """;
        }
        if (kind == "tally_empty")
            return "CLOSE TABLES ALL\nSET TABLEPROMPT OFF\n_TALLY=77\n" + MicroVfpSqlDmlTests.Catch("REPLACE ALL n WITH 1") + "\nresult=result+TRANSFORM(_TALLY)";
        if (kind == "tally_trigger")
            return "USE t\n_TALLY=77\n" + MicroVfpSqlDmlTests.Catch("REPLACE ALL n WITH n+1") + "\nresult=result+TRANSFORM(_TALLY)+'|'+TRANSFORM(RECNO())";
        if (kind == "batch")
            return "USE t EXCLUSIVE\nINDEX ON id TAG id\nINDEX ON k TAG k\nDELETE FROM t WHERE MOD(id,2)=0\n" + MicroVfpSqlDmlTests.State + "\n" + MicroVfpSqlDmlTests.Dump;
        throw new ArgumentOutOfRangeException(nameof(kind));
    }

    [Theory]
    [InlineData("nav_OFF_scan", "1,2,3,|4|.T.|.F.")]
    [InlineData("nav_ON_scan", "1,2,3,|4|.T.|.F.")]
    [InlineData("nav_OFF_skip", "|2|.F.|.F.")]
    [InlineData("nav_ON_skip", "|2|.F.|.F.")]
    [InlineData("nav_OFF_back", "|2|.F.|.F.")]
    [InlineData("nav_ON_back", "|2|.F.|.F.")]
    [InlineData("nav_OFF_recall", "|3|.F.|.F.")]
    [InlineData("nav_ON_recall", "|3|.F.|.F.")]
    [InlineData("parser_for", "ERR:1|77|.F.")]
    [InlineData("parser_in", "ERR:10|77|.F.")]
    [InlineData("source_table", "0|.F.|2|42|1")]
    [InlineData("source_row", "0|.F.|2|42|1")]
    [InlineData("rule_5_all", "1|0")]
    [InlineData("rule_5_one", "1|0")]
    [InlineData("rule_3_all", "1|0")]
    [InlineData("rule_3_one", "1|0")]
    [InlineData("rule_3_move", "1|0")]
    [InlineData("rule_5_all_record", "0|1")]
    [InlineData("rule_5_one_record", "0|1")]
    [InlineData("rule_3_all_record", "0|1")]
    [InlineData("rule_3_one_record", "0|1")]
    [InlineData("rule_3_move_record", "0|1")]
    [InlineData("rule_5_all_sql", "1|0")]
    [InlineData("rule_5_one_sql", "1|0")]
    [InlineData("rule_5_one_sql_record", "1|0")]
    [InlineData("rule_5_all_sql_record", "1|0")]
    [InlineData("rule_3_one_sql_record", "1|0")]
    [InlineData("tally_empty", "ERR:52|77")]
    [InlineData("tally_trigger", "ERR:1539|2|2")]
    [InlineData("batch", "T|3|.F.|1|.T.\n1,10,ALPHA,.F.;2,20,BRAVO,.T.;3,30,CHARLIE,.F.;")]
    public void ReviewBehavior(string kind, string expected)
    {
        string setup = MicroVfpSqlDmlTests.Setup;
        string procedures = kind.StartsWith("rule_") ? Rule : kind == "tally_trigger" ? Trigger : "";
        if (kind.StartsWith("rule_")) setup = kind.EndsWith("record")
            ? setup.Replace("k C(10))", "k C(10), CHECK countRule())")
            : setup.Replace("n I,", "n I CHECK countRule(),");
        Assert.Equal(expected, Run(Scenario(kind), setup, procedures, kind.StartsWith("rule_") || kind == "tally_trigger"));
    }

    [Fact]
    public void XbaseUpdate_RemainsLoudlyUnsupported()
    {
        var error = Assert.Throws<MicroVfpRuntimeException>(() => Run("USE t\nUPDATE ON id FROM other REPLACE n WITH 1"));
        Assert.Equal(16, error.VfpErrorNumber);
        Assert.Contains("UPDATE ON", error.Statement);
    }

    [Fact]
    public void DeletePerformance()
    {
        using var dir = new MicroVfpTestSupport.TempDir("sqldml_perf");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("CREATE TABLE t (id I, k C(10))\nFOR i=1 TO 2000\nINSERT INTO t VALUES (i,TRANSFORM(i))\nENDFOR\nINDEX ON id TAG id\nINDEX ON k TAG k");
        var medians = new List<double>();
        foreach (string command in new[] { "DELETE FROM t WHERE MOD(id,2)=0", "DELETE ALL FOR MOD(id,2)=0" })
        {
            var times = new List<double>();
            for (int pass = 0; pass < 4; pass++)
            {
                vfp.Execute("RECALL ALL");
                var watch = Stopwatch.StartNew();
                vfp.Execute(command);
                watch.Stop();
                Assert.Equal(1000, vfp.Memory.Get("_TALLY").AsInteger);
                if (pass > 0) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            output.WriteLine(command + ": " + string.Join(", ", times.Select(t => t.ToString("F1"))) + " ms");
            medians.Add(times.Order().ElementAt(1));
        }
        // Allow scheduler noise, but catch the former per-row writer overhead (about 5x).
        Assert.True(medians[0] <= medians[1] * 2 + 20, $"SQL {medians[0]:F1} ms; xBase {medians[1]:F1} ms");
    }

    private static string Run(string scenario, string? setup = null, string procedures = "", bool enforce = false)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sqldml_review");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(procedures + "\n" + (setup ?? MicroVfpSqlDmlTests.Setup) + "\nCLOSE TABLES ALL");
        vfp.EnforceReferentialIntegrity = enforce;
        vfp.Execute("PUBLIC result\nresult=''\n" + scenario);
        return vfp.Memory.Get("result").AsString.Replace("\r", "");
    }
}
