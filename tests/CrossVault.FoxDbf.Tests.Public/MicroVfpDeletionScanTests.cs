using System.Diagnostics;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanTests(ITestOutputHelper output)
{
    internal const string Setup = """
        CREATE TABLE t (id I)
        INSERT INTO t VALUES (3)
        INSERT INTO t VALUES (1)
        INSERT INTO t VALUES (4)
        INSERT INTO t VALUES (2)
        """;

    internal static string Scenario(string operation, bool deleted, string index = "TAG live") => $"""
        USE t EXCLUSIVE
        SET DELETED OFF
        INDEX ON id {index} FOR NOT DELETED()
        SET ORDER TO live
        {(operation == "recall" ? "GO 4\nDELETE" : "")}
        SET DELETED {(deleted ? "ON" : "OFF")}
        SCAN
            IF LEN(result)>40
                EXIT
            ENDIF
            result=result+TRANSFORM(id)+','
            {(operation == "delete" ? "DELETE" : operation == "restore" ? "DELETE\nRECALL" : "IF id=1\nRECALL RECORD 4\nELSE\nRECALL\nENDIF")}
        ENDSCAN
        result=result+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())+'|'+TRANSFORM(BOF())
        SEEK 2
        result=result+'|'+TRANSFORM(FOUND())+'|'+TRANSFORM(RECNO())
        SET DELETED OFF
        SEEK 2
        result=result+'|'+TRANSFORM(FOUND())+'|'+TRANSFORM(RECNO())
        """;

    [Theory]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("recall", false)]
    [InlineData("recall", true)]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    public void Scan_DeletionMembership(string operation, bool deleted)
    {
        string expected = (operation == "recall" ? "1,3,4," : "1,2,3,4,") + "|5|.T.|.F.|" +
            (operation == "delete" ? ".F.|5|.F.|5" : ".T.|4|.T.|4");
        Assert.Equal(expected, MicroVfpSqlDmlConsolidatedTests.Run(Setup, Scenario(operation, deleted)));
    }

    internal const string ObserveProcedure = """
        FUNCTION observe
        result=result+TRANSFORM(id)+TRANSFORM(INDEXSEEK(1,.F.,'t','live'))+'|'
        RETURN .T.
        ENDFUNC
        """;

    internal const string ObserveScenario = """
        USE t EXCLUSIVE
        SET DELETED OFF
        INDEX ON id TAG live FOR NOT DELETED()
        SET ORDER TO live
        DELETE ALL FOR observe() WHILE .T.
        result=result+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())
        """;

    [Fact]
    public void Delete_PredicateSeesPreviousIndexEdit() => Assert.Equal("1.T.|2.F.|3.F.|4.F.|5|.T.",
        MicroVfpSqlDmlConsolidatedTests.Run(Setup, ObserveScenario, ObserveProcedure));

    [Theory]
    [InlineData(false, "3.T.|1.T.|4.T.|2.T.|5|.T.")]
    [InlineData(true, "1.T.|2.F.|3.F.|4.F.|5|.T.")]
    public void Delete_UnboundedPredicateOrder(bool filtered, string expected)
    {
        string scenario = ObserveScenario.Replace(" WHILE .T.", "");
        if (!filtered) scenario = scenario.Replace(" FOR NOT DELETED()", "");
        Assert.Equal(expected, MicroVfpSqlDmlConsolidatedTests.Run(Setup, scenario, ObserveProcedure));
    }

    internal const string TriggerProcedure = "FUNCTION __RI_DELETE_t\nRETURN id<>2\nENDFUNC";
    internal const string TriggerSetup = Setup + "\nCREATE TRIGGER ON t FOR DELETE AS __RI_DELETE_t()";
    internal const string TriggerScenario = """
        USE t EXCLUSIVE
        SET DELETED OFF
        INDEX ON id TAG live FOR NOT DELETED()
        SET ORDER TO live
        TRY
            SCAN
                result=result+TRANSFORM(id)+','
                DELETE
            ENDSCAN
        CATCH TO problem
            result=result+'ERR|'
        ENDTRY
        SEEK 1
        result=result+TRANSFORM(FOUND())+'|'
        SEEK 2
        result=result+TRANSFORM(FOUND())
        """;

    [Fact]
    public void Scan_TriggerRejectionRefreshesIndex() => Assert.Equal("1,2,ERR|.F.|.T.",
        MicroVfpSqlDmlConsolidatedTests.Run(Setup, TriggerScenario, TriggerProcedure, true));

    // Opt in to the wall-clock gate so normal test runs remain independent of machine load.
    [Fact]
    public void Scan_Timing_2000()
    {
        if (Environment.GetEnvironmentVariable("FOXDBF_DELETION_TIMING") != "1") return;
        var medians = new Dictionary<string, double>();
        foreach (string kind in new[] { "filtered-delete", "plain-delete", "plain-scan" })
        {
            using var dir = new MicroVfpTestSupport.TempDir("deletion_timing");
            using var session = new VfpSession();
            session.OpenDirectory(dir.Path);
            using var vfp = new VfpInterpreter(session);
            vfp.Execute("CREATE TABLE t (id I)\nFOR i=1 TO 2000\nINSERT INTO t VALUES (i)\nENDFOR\nINDEX ON id TAG live " +
                (kind == "filtered-delete" ? "FOR NOT DELETED()" : "") + "\nSET ORDER TO live");
            var times = new List<double>();
            for (int pass = -1; pass < 3; pass++)
            {
                vfp.Execute("SET ORDER TO 0\nSET DELETED OFF\nRECALL ALL\nSET ORDER TO live\nGO TOP\nhits=0");
                var watch = Stopwatch.StartNew();
                vfp.Execute("SCAN\nhits=hits+1\n" + (kind == "plain-scan" ? "" : "DELETE\n") + "ENDSCAN");
                watch.Stop();
                Assert.Equal(2000, vfp.Memory.Get("hits").AsInteger);
                if (pass >= 0) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            medians[kind] = times.Order().ElementAt(1);
            output.WriteLine($"{kind}: {string.Join(", ", times.Select(t => t.ToString("F1")))} ms; median {medians[kind]:F1} ms");
        }
        Assert.True(medians["filtered-delete"] <= 2 * medians["plain-delete"],
            $"Filtered DELETE {medians["filtered-delete"]:F1} ms exceeds 2x plain DELETE {medians["plain-delete"]:F1} ms");
    }
}
