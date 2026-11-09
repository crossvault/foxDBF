using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpRiErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamedDbcMember_InsertRestrict_UsesLogicalTableName(bool onError)
    {
        // VFP9 evidence: AutoFire_Restrict_ErrorHandlers_MatchVfp9Aerror. The DBC member's
        // name differs from both its custom alias and its physical file name (orditems.dbf).
        using var dir = new MicroVfpTestSupport.TempDir("ri_alias_insert");
        using var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using (session)
        {
            interp.EnforceReferentialIntegrity = true;
            interp.Execute("USE order_line_items IN 5 ALIAS MixedAlias\nSELECT MixedAlias\nDIMENSION errors[1]\ntrapped = 0");
            byte[] before = File.ReadAllBytes(dir.File("orditems.dbf"));
            string action = "INSERT INTO MixedAlias (order_id, product_id, unit_price, quantity) VALUES ('1','ZZZZZZ',10,1)";
            interp.Execute(onError
                ? $"ON ERROR trapped = AERROR(errors)\n{action}\nON ERROR"
                : $"TRY\n{action}\nCATCH TO problem\ntrapped = AERROR(errors)\nENDTRY");

            Assert.Equal(1m, interp.EvalExpression("trapped").AsNumber);
            Assert.Equal(1539m, interp.EvalExpression("errors[1,1]").AsNumber);
            Assert.Equal("Mixedalias", interp.EvalExpression("errors[1,3]").AsString);
            Assert.Equal(5m, interp.EvalExpression("errors[1,4]").AsNumber);
            Assert.Equal(1m, interp.EvalExpression("errors[1,5]").AsNumber);
            Assert.Equal(before, File.ReadAllBytes(dir.File("orditems.dbf")));
            Assert.Single(interp.FiredTriggerLevels);
            Assert.Equal(1, interp.TriggerLevel);
            Assert.Equal(0, interp.SnapshotTempFileCount());
        }
    }

    // Error 1539 and trigger kinds are pinned by MicroVfpRiAutoFireTests against VFP9.
    [Theory]
    [InlineData(false, "host")]
    [InlineData(false, "catch")]
    [InlineData(false, "onerror")]
    [InlineData(true, "host")]
    [InlineData(true, "catch")]
    [InlineData(true, "onerror")]
    public void RejectedDml_RaisesTriggerError_AfterRollback(bool insert, string handler)
    {
        using var dir = new MicroVfpTestSupport.TempDir("ri_errors");
        using (var writer = DbfWriter.Create(dir.File("items.dbf"), [new DbfColumnDef("id", 'I', 4)]))
            writer.AppendRecord(1);
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interp = new VfpInterpreter(session) { EnforceReferentialIntegrity = true };
        string kind = insert ? "INSERT" : "DELETE";
        interp.Execute($"PROCEDURE __RI_{kind}_items\nRETURN .F.\nENDPROC\nUSE items\nGO 1");
        string action = insert ? "INSERT INTO items (id) VALUES (2)" : "DELETE";
        if (handler == "host")
        {
            var ex = Assert.Throws<MicroVfpRuntimeException>(() => interp.Execute(action));
            Assert.Equal(1539, ex.VfpErrorNumber);
        }
        else
        {
            interp.Execute("DIMENSION errors[1]\ntrapped = 0\ncontinued = .F.");
            interp.Execute(handler == "catch"
                ? $"TRY\n{action}\nCATCH TO problem\ntrapped = AERROR(errors)\nENDTRY\ncontinued = .T."
                : $"ON ERROR trapped = AERROR(errors)\n{action}\ncontinued = .T.\nON ERROR");
            Assert.Equal(1m, interp.EvalExpression("trapped").AsNumber);
            Assert.Equal(1539m, interp.EvalExpression("errors[1,1]").AsNumber);
            Assert.Equal(insert ? 1m : 3m, interp.EvalExpression("errors[1,5]").AsNumber);
            Assert.True(interp.EvalExpression("continued").AsLogical);
        }
        Assert.Equal(1m, interp.EvalExpression("RECCOUNT()").AsNumber);
        interp.Execute("GO 1");
        Assert.False(interp.EvalExpression("DELETED()").AsLogical);
        Assert.Equal(1m, interp.EvalExpression("id").AsNumber);
        Assert.Equal(0, interp.SnapshotTempFileCount());
        Assert.Equal(1, interp.TriggerLevel);
    }
}
