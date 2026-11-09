using System.Diagnostics;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlConsolidatedTests(ITestOutputHelper output)
{
    internal const string Position = "result=result+TRANSFORM(id)+'|'+TRANSFORM(EOF())+'|'+TRANSFORM(BOF())";

    public static IEnumerable<object[]> NavigationCases()
    {
        foreach (string key in new[] { "null", "duplicate", "changing" })
        foreach (string direction in new[] { "ascending", "tagdesc", "orderdesc" })
        foreach (bool deleted in new[] { false, true })
        foreach (int step in new[] { 1, -1 })
            yield return new object[] { key, direction, deleted, step, false };
        foreach (string key in new[] { "null", "duplicate", "changing" })
            yield return new object[] { key, "ascending", false, 1, true };
    }

    internal static string NavigationSetup(string key) => "SET TABLEPROMPT OFF\nCREATE TABLE t (id I, a I NULL, k C(1))\n" +
        (key == "null" ? "INSERT INTO t VALUES (1,.NULL.,'A')\nINSERT INTO t VALUES (2,.NULL.,'M')\nINSERT INTO t VALUES (3,.NULL.,'T')"
        : "INSERT INTO t VALUES (1,1,'A')\nINSERT INTO t VALUES (2,1,'M')\nINSERT INTO t VALUES (3,1,'T')");

    internal static string NavigationScenario(string key, string direction, bool deleted, int step, bool recall) => $"""
        SET TABLEPROMPT OFF
        USE t EXCLUSIVE
        SET DELETED {(deleted ? "ON" : "OFF")}
        INDEX ON {(key == "changing" ? "IIF(DELETED(),'Z',k)" : "a")} TAG live FOR NOT DELETED() {(direction == "tagdesc" ? "DESCENDING" : "")}
        SET ORDER TO live {(direction == "orderdesc" ? "DESCENDING" : "")}
        GO 2
        DELETE
        {(recall ? "RECALL" : "")}
        SKIP {step}
        {Position}
        """;

    [Theory]
    [MemberData(nameof(NavigationCases))]
    public void Navigation_RemovedEntry(string key, string direction, bool deleted, int step, bool recall)
    {
        int next = (direction == "ascending" ? step : -step) > 0 ? 3 : 1;
        string expected = key == "changing" && !recall
            ? direction == "ascending" ? step > 0 ? "0|.T.|.F." : "3|.F.|.F."
                : step > 0 ? "3|.F.|.F." : "3|.F.|.T."
            : $"{next}|.F.|.F.";
        Assert.Equal(expected, Run(NavigationSetup(key), NavigationScenario(key, direction, deleted, step, recall)));
    }

    internal static string AppendNavigation(bool ordered, int step) => $"""
        SET TABLEPROMPT OFF
        SET MULTILOCKS ON
        USE t EXCLUSIVE
        {(ordered ? "INDEX ON k TAG live" : "")}
        =CURSORSETPROP('Buffering',5)
        INSERT INTO t VALUES (4,1,'N')
        SKIP {step}
        {Position}
        """;

    [Theory]
    [InlineData(false, 1, "0|.T.|.F.")]
    [InlineData(false, -1, "3|.F.|.F.")]
    [InlineData(true, 1, "3|.F.|.F.")]
    [InlineData(true, -1, "2|.F.|.F.")]
    public void Navigation_BufferedAppend(bool ordered, int step, string expected) =>
        Assert.Equal(expected, Run(NavigationSetup("changing"), AppendNavigation(ordered, step)));

    internal static string RulesSetup(string kind, bool record, bool oracle = false)
    {
        string rule = kind == "retry" ? "gate()" : kind is "cross" or "replacecross" ? "n<=m" : "n>0";
        string setup = "SET TABLEPROMPT OFF\nPUBLIC limit\nlimit=100\nCREATE TABLE t (id I, n I, m I, k C(1))\n" +
            "INSERT INTO t VALUES (1,5,10,'A')\nINSERT INTO t VALUES (2,5,10,'B')\n";
        if (oracle) return setup + "ALTER TABLE t " + (record ? "" : "ALTER COLUMN n ") + "SET CHECK " + rule + " NOVALIDATE";
        return record ? setup.Replace("k C(1))", "k C(1), CHECK " + rule + ")")
            : setup.Replace("n I,", "n I CHECK " + rule + ",");
    }

    internal const string RuleProcedure = "FUNCTION gate\nRETURN n<=m.limit\nENDFUNC\nFUNCTION keycounter\nLPARAMETERS value\nhits=hits+1\nRETURN value\nENDFUNC";
    internal const string KeyEvaluation = "USE t EXCLUSIVE\nPUBLIC hits\nhits=0\nINDEX ON keycounter(a) TAG live FOR NOT DELETED()\nGO 2\nDELETE\nhits=0\nSKIP\nresult=TRANSFORM(hits)";

    [Fact]
    public void Navigation_KeyEvaluation() => Assert.Equal("1", Run(NavigationSetup("duplicate"), KeyEvaluation, RuleProcedure));
    internal static string RulesScenario(string kind, int buffering, bool record)
    {
        string prefix = $"SET TABLEPROMPT OFF\nPUBLIC limit\nlimit=100\nUSE t EXCLUSIVE\nSET MULTILOCKS ON\n=CURSORSETPROP('Buffering',{buffering})\nGO 1\n";
        string command = kind switch
        {
            "cross" => "UPDATE t SET n=15, m=20 WHERE id=1",
            "replacecross" => "REPLACE n WITH 15, m WITH 20",
            "bad" => "UPDATE t SET n=-1, k='X' WHERE id=1",
            "onerror" => "UPDATE t SET n=-1, k='X' WHERE id=1",
            "insert" => "INSERT INTO t (id,n) VALUES (3,-1)",
            "array" => "DIMENSION laInput[4]\nlaInput[1]=3\nlaInput[2]=-1\nlaInput[3]=0\nlaInput[4]=''\nINSERT INTO t FROM ARRAY laInput",
            "memvar" => "m.id=3\nm.n=-1\nm.m=0\nm.k=''\nINSERT INTO t FROM MEMVAR",
            "append" => "APPEND BLANK\nREPLACE n WITH -1",
            "retry" => "REPLACE n WITH 15\nGO 2\nREPLACE n WITH 25\nlimit=20",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        string body = kind == "onerror"
            ? "ON ERROR result=result+'ERR:'+TRANSFORM(ERROR())+'|'\n" + command + "\nON ERROR"
            : MicroVfpSqlDmlTests.Catch(command);
        body += "\nresult=result+TRANSFORM(n)+','+TRANSFORM(m)+','+ALLTRIM(k)+'|'";
        if (buffering > 1)
        {
            body += "\n" + MicroVfpSqlDmlTests.Catch("saved=TABLEUPDATE(.T.)\nresult=result+TRANSFORM(saved)+'|'");
            if (kind == "retry") body += "\nREPLACE n WITH 5\nlimit=10\n" + MicroVfpSqlDmlTests.Catch("saved=TABLEUPDATE(.T.)\nresult=result+TRANSFORM(saved)+'|'");
            body += "\nresult=result+TRANSFORM(TABLEREVERT(.T.))";
        }
        return prefix + body;
    }

    // Expected strings are pinned by the matching VFP9 oracle, including retained bad buffers.
    public static IEnumerable<object[]> RuleCases()
    {
        foreach (string kind in new[] { "cross", "replacecross", "bad", "insert", "append", "array", "memvar" })
        foreach (int buffering in new[] { 1, 3, 5 })
        foreach (bool record in new[] { false, true })
            yield return new object[] { kind, buffering, record };
        // VFP9 retains the already accepted record rule across the failed batch and external
        // memvar change. R2's proposed invalidation would reject a retry which VFP accepts.
        yield return new object[] { "retry", 5, true };
    }

    private static readonly Dictionary<string, string> RuleExpected = new()
    {
        ["cross/1/False"] = "ERR:1582|5,10,A|",
        ["cross/1/True"] = "5,10,B|",
        ["cross/3/False"] = "ERR:1582|15,10,A|.F.|1",
        ["cross/3/True"] = "5,10,B|.T.|0",
        ["cross/5/False"] = "ERR:1582|15,10,A|.F.|1",
        ["cross/5/True"] = "5,10,B|.T.|0",
        ["replacecross/1/False"] = "ERR:1582|5,10,A|",
        ["replacecross/1/True"] = "15,20,A|",
        ["replacecross/3/False"] = "ERR:1582|15,10,A|.F.|1",
        ["replacecross/3/True"] = "15,20,A|.T.|0",
        ["replacecross/5/False"] = "ERR:1582|15,10,A|.F.|1",
        ["replacecross/5/True"] = "15,20,A|.T.|0",
        ["bad/1/False"] = "ERR:1582|5,10,A|",
        ["bad/1/True"] = "ERR:1583|5,10,A|",
        ["bad/3/False"] = "ERR:1582|-1,10,A|.F.|1",
        ["bad/3/True"] = "ERR:1583|-1,10,X|.F.|1",
        ["bad/5/False"] = "ERR:1582|-1,10,A|.F.|1",
        ["bad/5/True"] = "ERR:1583|-1,10,X|.F.|1",
        ["insert/1/False"] = "ERR:1582|0,0,|",
        ["insert/1/True"] = "ERR:1583|0,0,|",
        ["insert/3/False"] = "ERR:1582|0,0,|.T.|0",
        ["insert/3/True"] = "-1,0,|.F.|1",
        ["insert/5/False"] = "ERR:1582|0,0,|.T.|0",
        ["insert/5/True"] = "-1,0,|.F.|1",
        ["append/1/False"] = "ERR:1582|0,0,|",
        ["append/1/True"] = "ERR:1583|0,0,|",
        ["append/3/False"] = "ERR:1582|-1,0,|.F.|1",
        ["append/3/True"] = "-1,0,|.F.|1",
        ["append/5/False"] = "ERR:1582|-1,0,|.F.|1",
        ["append/5/True"] = "-1,0,|.F.|1",
        ["retry/5/True"] = "25,10,B|.F.|.T.|0",
    };

    [Theory]
    [MemberData(nameof(RuleCases))]
    public void Rules_FailingCandidates(string kind, int buffering, bool record)
    {
        string actual = Run(RulesSetup(kind, record), RulesScenario(kind, buffering, record), RuleProcedure, true);
        output.WriteLine($"{kind}/{buffering}/{record}: {actual}");
        string expectedKind = kind is "array" or "memvar" ? "insert" : kind;
        Assert.Equal(RuleExpected[$"{expectedKind}/{buffering}/{record}"], actual);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void Rules_OnError(int buffering, bool record) => Assert.Equal(RuleExpected[$"bad/{buffering}/{record}"],
        Run(RulesSetup("bad", record), RulesScenario("onerror", buffering, record), RuleProcedure, true));

    [Theory]
    [InlineData("array", 1)]
    [InlineData("array", 3)]
    [InlineData("array", 5)]
    [InlineData("memvar", 1)]
    [InlineData("memvar", 3)]
    [InlineData("memvar", 5)]
    public void Rules_MemorySourcesKeepValues(string kind, int buffering) =>
        Assert.Equal("7,0,|" + (buffering > 1 ? ".T.|0" : ""), Run(RulesSetup(kind, false),
            RulesScenario(kind, buffering, false).Replace("=-1", "=7"), RuleProcedure, true));

    [Theory]
    [InlineData("DELETE FOR from = 1", "ERR:36|")]
    [InlineData("DELETE FOR from>1", "ERR:1|")]
    public void Parser_FromField(string command, string expected) => Assert.Equal(expected,
        Run("SET TABLEPROMPT OFF\nCREATE TABLE t (from I)\nINSERT INTO t VALUES (1)",
            "SET TABLEPROMPT OFF\nUSE t\n" + MicroVfpSqlDmlTests.Catch(command)));

    [Fact]
    public void Navigation_ScanTiming()
    {
        using var dir = new MicroVfpTestSupport.TempDir("nav_timing");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("CREATE TABLE t (id I)\nFOR i=1 TO 2000\nINSERT INTO t VALUES (i)\nENDFOR\nINDEX ON id TAG live FOR NOT DELETED()\nSET ORDER TO live");
        foreach (bool delete in new[] { false, true })
        {
            var times = new List<double>();
            for (int pass = 0; pass < 3; pass++)
            {
                vfp.Execute("SET ORDER TO 0\nRECALL ALL\nSET ORDER TO live\nGO TOP\nhits=0");
                var watch = Stopwatch.StartNew();
                vfp.Execute("SCAN\nhits=hits+1\n" + (delete ? "DELETE\n" : "") + "ENDSCAN");
                watch.Stop();
                Assert.Equal(2000, vfp.Memory.Get("hits").AsInteger);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60),
                    $"A 2,000-row SCAN exceeded the generous 60-second regression bound: {watch.Elapsed}.");
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"SCAN {(delete ? "DELETE" : "plain")}: {string.Join(", ", times.Select(t => t.ToString("F1")))} ms; median {times.Order().ElementAt(1):F1} ms");
        }
    }

    internal static string Run(string setup, string scenario, string procedures = "", bool enforce = false)
    {
        using var dir = new MicroVfpTestSupport.TempDir("consolidated");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(procedures + "\n" + setup + "\nCLOSE TABLES ALL");
        vfp.EnforceReferentialIntegrity = enforce;
        vfp.Execute("PUBLIC result\nresult=''\n" + scenario);
        return vfp.Memory.Get("result").AsString.Replace("\r", "");
    }
}
