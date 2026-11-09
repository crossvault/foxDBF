using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNonStructuralTxnTests
{
    public static IEnumerable<object[]> Cases =>
        from format in new[] { "legacy", "compact", "cdx" }
        from timing in new[] { "before", "set", "use" }
        from free in new[] { false, true }
        select new object[] { format, timing, free };

    internal static string VfpExpected(string timing, bool free) => timing == "before" && !free
        ? "ERR:1548|0|.T.|WRITE|2" : "BEGIN|" + (timing == "before" ? "" : "OPEN|") + "1|.T.|WRITE|2";

    // VFP allows opening indexes after BEGIN and ignores free tables at BEGIN. Keep the
    // existing compact-index write protection in those cases, as required by the write contract.
    internal static string EngineExpected(string format, string timing, bool free)
        => format == "compact" && (timing != "before" || free)
            ? VfpExpected(timing, free).Replace("WRITE|2", "ERR:1|1") : VfpExpected(timing, free);

    [Theory]
    [MemberData(nameof(Cases))]
    public void BeginChecksDatabaseMembers_AndRetainsLaterWriteGuards(string format, string timing, bool free)
        => Assert.Equal(EngineExpected(format, timing, free), MicroVfpXbaseUpdateTests.Run(Setup(format, free), Scenario(format, timing)));

    internal static string Setup(string format, bool free) => $"""
        SET TABLEPROMPT OFF
        CREATE TABLE t {(free ? "FREE" : "")} (id I)
        INSERT INTO t VALUES (1)
        INDEX ON id {(format == "cdx" ? "TAG extra OF extra.cdx" : "TO extra.idx" + (format == "compact" ? " COMPACT" : ""))}
        USE
        """;

    internal static string Scenario(string format, string timing)
    {
        string file = "extra." + (format == "cdx" ? "cdx" : "idx");
        string open = timing == "use" ? "USE t INDEX " + file : "SET INDEX TO " + file;
        return "SET TABLEPROMPT OFF\nUSE t\n" + (timing == "before" ? open + "\n" : "") +
            MicroVfpSqlDmlTests.Catch("BEGIN TRANSACTION\nresult=result+'BEGIN|'") + "\n" +
            (timing == "before" ? "" : MicroVfpSqlDmlTests.Catch(open + "\nresult=result+'OPEN|'") + "\n") +
            "result=result+TRANSFORM(TXNLEVEL())+'|'+TRANSFORM(USED('t'))+'|'\n" +
            MicroVfpSqlDmlTests.Catch("REPLACE id WITH 2\nresult=result+'WRITE|'") + "\n" +
            "result=result+TRANSFORM(id)\nIF TXNLEVEL()>0\nROLLBACK\nENDIF\nUSE";
    }
}
