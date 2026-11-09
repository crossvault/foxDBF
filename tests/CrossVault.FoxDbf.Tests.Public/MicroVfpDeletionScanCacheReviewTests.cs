using System.Collections;
using System.Reflection;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeletionScanCacheReviewTests
{
    [Fact]
    public void OrderedScanDoesNotRetainKeysOrSlots()
    {
        using var dir = new MicroVfpTestSupport.TempDir("order_cache");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("k", 'C', 40)]))
            writer.AppendRecords(Enumerable.Range(1, 2000).Select(i => new object?[] { i.ToString("D10") }));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("USE t EXCLUSIVE\nINDEX ON k TAG k\nSET ORDER TO k\nSCAN\nENDSCAN");
        var metas = (IDictionary)typeof(VfpInterpreter).GetField("_meta", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vfp)!;
        var meta = metas[session.CurrentArea]!;
        Assert.Null(meta.GetType().GetField("OrderKeys")!.GetValue(meta));
        Assert.Null(meta.GetType().GetField("OrderSlots")!.GetValue(meta));
    }

    [Fact]
    public void RepeatedRecnoDoesNotThrowDuringOrderOrRemovedSlotSkip()
    {
        using var dir = new MicroVfpTestSupport.TempDir("duplicate_slot");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("k", 'C', 40)]))
            writer.AppendRecords(new[] { new object?[] { "A" }, new object?[] { "M" }, new object?[] { "T" } });
        using (var table = DbfTable.Open(dir.File("t.dbf")))
        {
            // Deliberately damaged CDX: two different keys point at record 1. Navigation must tolerate
            // that reader input; this test makes no claim about VFP's ordering of a corrupt index.
            CdxIndexBuilder.Build(dir.File("t.cdx"), table,
                [new(1, table.GetRecord(0)!.Value), new(1, table.GetRecord(1)!.Value), new(3, table.GetRecord(2)!.Value)],
                [new CdxTagDefinition("k", "k", forExpression: "NOT DELETED()")]);
        }
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("USE t EXCLUSIVE\nSET ORDER TO k\nGO 2\nSKIP\nGO TOP\nDELETE\nSKIP");
    }
}

