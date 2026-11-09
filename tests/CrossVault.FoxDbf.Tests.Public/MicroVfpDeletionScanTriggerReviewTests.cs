using System.Collections;
using System.Reflection;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanTriggerReviewTests
{
    internal const string Before = "USE t EXCLUSIVE\nSET DELETED OFF\nINDEX ON k TAG live FOR NOT DELETED()\nSET ORDER TO live\nGO 2\n";
    internal const string After = """
        TRY
            DELETE
        CATCH TO problem
            result='ERR'
        ENDTRY
        result=result+'|'
        GO TOP
        SCAN
            result=result+TRANSFORM(id)
        ENDSCAN
        SEEK 'M'
        result=result+'|'+TRANSFORM(FOUND())
        GO 2
        result=result+'|'+TRANSFORM(DELETED())
        """;
    internal static string Procedure(bool reject) => "FUNCTION __RI_DELETE_t\nRETURN " + (reject ? "id<>2" : ".T.") + "\nENDFUNC";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TriggerRefreshesOrder(bool reject, bool mapped) =>
        Assert.Equal(reject ? "ERR|123|.T.|.F." : "|13|.F.|.T.", Run(reject, mapped));

    internal static string Run(bool reject, bool mapped)
    {
        using var dir = new MicroVfpTestSupport.TempDir("trigger_order");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Procedure(reject) + "\n" + MicroVfpDeletionScanReviewTests.Setup + "\nCLOSE TABLES ALL");
        vfp.EnforceReferentialIntegrity = true;
        vfp.Execute("PUBLIC result\nresult=''\n" + Before);
        if (mapped) MapCurrentArea(session);
        vfp.Execute(After);
        return vfp.Memory.Get("result").AsString;
    }

    internal static void MapCurrentArea(VfpSession session)
    {
        // The session API opens FileStream readers; inject the supported mapped handles to exercise reopen.
        var old = session.AreaAt(session.CurrentArea)!;
        string path = old.Table.SourcePath!;
        var table = DbfTable.Open(path, new DbfOptions { ReadBackend = DbfReadBackend.MemoryMapped });
        var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table, DbfReadBackend.MemoryMapped);
        Assert.True(table.IsMemoryMapped);
        Assert.True(cdx.IsMemoryMapped);
        var areas = (IDictionary)typeof(VfpSession).GetProperty("_areas", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        areas[old.Area] = new VfpSession.WorkArea(old.Area, table, cdx, old.Alias, old.Exclusive, old.NoUpdate);
        old.Dispose();
    }
}
