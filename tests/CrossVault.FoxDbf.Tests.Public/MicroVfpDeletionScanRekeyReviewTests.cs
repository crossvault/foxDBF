using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanRekeyReviewTests(ITestOutputHelper output)
{
    internal const string Setup = """
        SET TABLEPROMPT OFF
        CREATE TABLE t (id I, k C(40))
        FOR i=1 TO 1000
            INSERT INTO t VALUES (i, PADL(TRANSFORM(i),4,'0'))
        ENDFOR
        """;

    internal static string Scenario(bool next) => $$"""
        USE t EXCLUSIVE
        SET DELETED OFF
        INDEX ON IIF(DELETED(),'Z',k) TAG x
        SET ORDER TO x
        GO 10
        DELETE {{(next ? "NEXT 50" : "ALL FOR id<500")}}
        GO TOP
        SCAN
            result=result+ALLTRIM(STR(id))+','
        ENDSCAN
        SEEK 'Z'
        result=result+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(FOUND())
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RekeyWithinStatement(bool next)
    {
        var live = Enumerable.Range(1, 1000).Where(i => next ? i != 10 : i >= 500);
        var deleted = Enumerable.Range(1, 1000).Except(live);
        string expected = string.Concat(live.Concat(deleted).Select(i => i + ",")) + (next ? "|10|.T." : "|1|.T.");
        Assert.Equal(expected, MicroVfpSqlDmlConsolidatedTests.Run(Setup, Scenario(next)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RekeyDoesNotRebuildPerRow(bool all)
    {
        using var dir = new MicroVfpTestSupport.TempDir("rekey_alloc");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("id", 'I'), new DbfColumnDef("k", 'C', 40)]))
            writer.AppendRecords(Enumerable.Range(1, 2000).Select(i => new object?[] { i, i.ToString("D4") }));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("USE t EXCLUSIVE\nINDEX ON IIF(DELETED(),'Z',k) TAG x\nSET ORDER TO x\nGO TOP");
        long before = GC.GetAllocatedBytesForCurrentThread();
        vfp.Execute(all ? "DELETE ALL FOR id<=50" : "DELETE NEXT 50");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"DELETE {(all ? "ALL" : "NEXT 50")}, 2000-row order: {allocated} allocated bytes");
        // Rebuilding all 2,000 entries on each row used tens of MB, independent of machine timing.
        Assert.True(allocated < 12_000_000, $"Per-row order rebuild: {allocated} allocated bytes");
    }
}
