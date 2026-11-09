using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNonStructuralTxnTests
{
    public static IEnumerable<object[]> Cases =>
        from format in new[] { "legacy", "compact", "cdx", "structural" }
        from timing in new[] { "before", "set", "use" }
        from free in new[] { false, true }
        select new object[] { format, timing, free };

    internal static string VfpExpected(string format, string timing, bool free) => timing == "before" && !free && format != "structural"
        ? "ERR:1548|0|.T.|WRITE|2" : "BEGIN|" + (timing == "before" ? "" : "OPEN|") + "1|.T.|WRITE|2";

    [Theory]
    [MemberData(nameof(Cases))]
    public void BeginChecksDatabaseMembers_AndAllowsLaterWrites(string format, string timing, bool free)
        => Assert.Equal(VfpExpected(format, timing, free), MicroVfpXbaseUpdateTests.Run(Setup(format, free), Scenario(format, timing)));

    internal static readonly Dictionary<string, string> ReviewExpected = new()
    {
        ["tableupdate"] = ".T.|2|0",
        ["no_database"] = "ERR:1548|0",
        ["other_database"] = "ERR:1548|0",
        ["compact_rollback"] = "2|1",
    };

    public static IEnumerable<object[]> ReviewCases => ReviewExpected.Select(p => new object[] { p.Key, p.Value });

    [Theory]
    [MemberData(nameof(ReviewCases))]
    public void Review_TransactionBoundariesMatchVfp9(string kind, string expected)
    {
        var (setup, scenario) = ReviewScenario(kind);
        Assert.Equal(expected, MicroVfpXbaseUpdateTests.Run(setup, EngineScenario(kind, scenario),
            engineDatabase: kind == "no_database" ? null : kind == "other_database" ? "other" : "probe"));
    }

    internal static (string Setup, string Scenario) ReviewScenario(string kind)
    {
        string setup = Setup(kind == "compact_rollback" ? "compact" : "legacy", false);
        string scenario = kind switch
        {
            "tableupdate" => "USE t INDEX extra.idx\nSET MULTILOCKS ON\n=CURSORSETPROP('Buffering',5)\nAPPEND BLANK\nREPLACE id WITH 2\nresult=TRANSFORM(TABLEUPDATE(.T.))+'|'+TRANSFORM(RECCOUNT())+'|'+TRANSFORM(TXNLEVEL())",
            "no_database" => "CLOSE DATABASES ALL\nUSE t INDEX extra.idx\n" + MicroVfpSqlDmlTests.Catch("BEGIN TRANSACTION") + "\nresult=result+TRANSFORM(TXNLEVEL())\nIF TXNLEVEL()>0\nROLLBACK\nENDIF",
            "other_database" => "OPEN DATABASE other\nUSE t INDEX extra.idx\n" + MicroVfpSqlDmlTests.Catch("BEGIN TRANSACTION") + "\nresult=result+TRANSFORM(TXNLEVEL())\nIF TXNLEVEL()>0\nROLLBACK\nENDIF",
            _ => "USE t\nBEGIN TRANSACTION\nSET INDEX TO extra.idx\nREPLACE id WITH 2\nresult=TRANSFORM(id)+'|'\nROLLBACK\nUSE\nUSE t INDEX extra.idx\nresult=result+TRANSFORM(id)"
        };

        return (setup, "SET TABLEPROMPT OFF\n" + scenario);
    }

    internal static string EngineScenario(string kind, string scenario)
        => kind == "other_database" ? scenario.Replace("OPEN DATABASE other\n", "") : scenario;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactRollback_RestoresExactIndexBytes(bool writeBeforeOpening)
    {
        using var dir = new MicroVfpTestSupport.TempDir("compact_txn_bytes");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        CrossVault.FoxDbf.Write.DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<CrossVault.FoxDbf.Write.DbcTableSpec>());
        session.OpenDatabase(dir.File("probe.dbc"));
        vfp.Execute(Setup("compact", false) + "\nUSE t\nBEGIN TRANSACTION");
        if (writeBeforeOpening) vfp.Execute("REPLACE id WITH 3");
        vfp.Execute("SET INDEX TO extra.idx");
        byte[] before = File.ReadAllBytes(dir.File("extra.idx"));
        vfp.Execute("REPLACE id WITH 2");
        Assert.False(before.SequenceEqual(File.ReadAllBytes(dir.File("extra.idx"))));
        vfp.Execute("ROLLBACK\nUSE");
        Assert.Equal(before, File.ReadAllBytes(dir.File("extra.idx")));
    }

    internal static string Setup(string format, bool free) => $"""
        SET TABLEPROMPT OFF
        CREATE TABLE t {(free ? "FREE" : "")} (id I)
        INSERT INTO t VALUES (1)
        INDEX ON id {(format == "structural" ? "TAG id" : format == "cdx" ? "TAG extra OF extra.cdx" : "TO extra.idx" + (format == "compact" ? " COMPACT" : ""))}
        USE
        """;

    internal static string Scenario(string format, string timing)
    {
        string file = format == "structural" ? "t.cdx" : "extra." + (format == "cdx" ? "cdx" : "idx");
        string open = timing == "use" ? "USE t INDEX " + file : "SET INDEX TO " + file;
        return "SET TABLEPROMPT OFF\nUSE t\n" + (timing == "before" ? open + "\n" : "") +
            MicroVfpSqlDmlTests.Catch("BEGIN TRANSACTION\nresult=result+'BEGIN|'") + "\n" +
            (timing == "before" ? "" : MicroVfpSqlDmlTests.Catch(open + "\nresult=result+'OPEN|'") + "\n") +
            "result=result+TRANSFORM(TXNLEVEL())+'|'+TRANSFORM(USED('t'))+'|'\n" +
            MicroVfpSqlDmlTests.Catch("REPLACE id WITH 2\nresult=result+'WRITE|'") + "\n" +
            "result=result+TRANSFORM(id)\nIF TXNLEVEL()>0\nROLLBACK\nENDIF\nUSE";
    }
}
