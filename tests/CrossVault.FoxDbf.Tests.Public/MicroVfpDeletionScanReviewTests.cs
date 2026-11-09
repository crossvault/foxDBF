namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanReviewTests
{
    internal const string Setup = """
        SET TABLEPROMPT OFF
        CREATE TABLE t (id I, k C(40), n I)
        INSERT INTO t VALUES (1, 'A', 0)
        INSERT INTO t VALUES (2, 'M', 0)
        INSERT INTO t VALUES (3, 'T', 0)
        """;

    internal static string RemovedScenario(string command, bool deleted = false, bool rebuild = false) => $$"""
        USE t EXCLUSIVE
        SET DELETED OFF
        INDEX ON k TAG live FOR NOT DELETED()
        SET ORDER TO live
        LOCATE FOR id=2
        DELETE
        {{(rebuild ? "SET ORDER TO 0\nSET ORDER TO live" : "")}}
        SET DELETED {{(deleted ? "ON" : "OFF")}}
        value=-99
        {{command}}
        result=result+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(EOF())+'|'+TRANSFORM(FOUND())+'|'+TRANSFORM(value)
        SET ORDER TO 0
        GO TOP
        SCAN
            result=result+'|'+TRANSFORM(n)+TRANSFORM(DELETED())
        ENDSCAN
        """;

    public static IEnumerable<object[]> RemovedCases()
    {
        for (int i = 0; i < RemovedCommands.Length; i++)
            foreach (bool deleted in new[] { false, true })
                yield return new object[] { i, deleted };
    }

    [Theory]
    [MemberData(nameof(RemovedCases))]
    public void RemovedSlotScopes(int command, bool deleted) => AssertRemoved(command, deleted, false);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RemovedSlotScopesAfterRebuild(int command) => AssertRemoved(command, false, true);

    private static void AssertRemoved(int command, bool deleted, bool rebuild)
    {
        string prefix = command switch
        {
            0 => "|4|.T.|.F.|2", 2 => "|2|.F.|.T.|-99", 3 => "|4|.T.|.F.|5",
            4 => "|4|.T.|.F.|250", 5 => "23|4|.T.|.F.|-99", _ => "|4|.T.|.F.|-99"
        };
        string rows = command switch
        {
            6 => deleted ? "|0.F.|1.F." : "|0.F.|1.T.|1.F.",
            7 => deleted ? "|0.F." : "|0.F.|0.T.|0.T.",
            8 => "|0.F.|0.F.|0.F.",
            _ => deleted ? "|0.F.|0.F." : "|0.F.|0.T.|0.F."
        };
        Assert.Equal(prefix + rows, MicroVfpSqlDmlConsolidatedTests.Run(Setup, RemovedScenario(RemovedCommands[command], deleted, rebuild)));
    }

    [Fact]
    public void ContinueAfterRemovedSlot() => Assert.Equal("3|.T.",
        MicroVfpSqlDmlConsolidatedTests.Run(Setup, """
            USE t EXCLUSIVE
            INDEX ON k TAG live FOR NOT DELETED()
            LOCATE FOR id>=2
            DELETE
            CONTINUE
            result=TRANSFORM(RECNO())+'|'+TRANSFORM(FOUND())
            """));

    internal static readonly string[] RemovedCommands =
    [
        "COUNT REST TO value", "CONTINUE", "LOCATE REST", "SUM id REST TO value",
        // Compare the numeric average independently of TRANSFORM's existing numeric-scale limitation.
        "AVERAGE id REST TO value\nvalue=value*100", "SCAN REST\nresult=result+TRANSFORM(id)\nENDSCAN",
        "REPLACE REST n WITH n+1", "DELETE REST", "RECALL REST"
    ];
}
