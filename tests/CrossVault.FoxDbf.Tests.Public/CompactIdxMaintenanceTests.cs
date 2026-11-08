using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using CrossVault.FoxDbf.Index;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class CompactIdxMaintenanceTests
{
    [Theory]
    [InlineData("MACHINE", false)]
    [InlineData("MACHINE", true)]
    [InlineData("GENERAL", false)]
    [InlineData("GENERAL", true)]
    public void OpenCompactIdx_AppendReplaceDeleteRecallPack(string collation, bool filtered)
    {
        using var temp = new MicroVfpTestSupport.TempDir("idxmaint");
        using (var w = DbfWriter.Create(temp.File("t.dbf"), [new DbfColumnDef("k", 'C', 10), new DbfColumnDef("active", 'L')]))
        {
            w.AppendRecord("BRAVO", true);
            w.AppendRecord("ALPHA", true);
            w.CreateCompactIdx(temp.File("x.idx"), "k", filtered ? "active AND NOT DELETED()" : null, collation: collation);
        }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        var vfp = new VfpInterpreter(session);
        vfp.Execute("SET DELETED OFF\nUSE t EXCLUSIVE INDEX x.idx\nSET ORDER TO 1\nGO 1\nREPLACE k WITH 'ZULU'");
        Assert.True(vfp.EvalExpression("SEEK('ZULU')").AsLogical);
        vfp.Execute("INSERT INTO t VALUES ('DELTA', .T.)");
        Assert.True(vfp.EvalExpression("SEEK('DELTA')").AsLogical);
        vfp.Execute("GO 1\nDELETE");
        Assert.Equal(!filtered, vfp.EvalExpression("SEEK('ZULU')").AsLogical);
        vfp.Execute("GO 1\nRECALL");
        Assert.True(vfp.EvalExpression("SEEK('ZULU')").AsLogical);
        vfp.Execute("GO 2\nDELETE\nPACK");
        Assert.False(vfp.EvalExpression("SEEK('ALPHA')").AsLogical);
        Assert.True(vfp.EvalExpression("SEEK('DELTA')").AsLogical);
        Assert.Equal(2m, vfp.EvalExpression("RECNO()").AsNumber);
    }
    [Theory]
    [InlineData("MACHINE", false)]
    [InlineData("GENERAL", true)]
    public void Writer_OpenCompactIdx_MaintainsAllMutations(string collation, bool filtered)
    {
        using var temp = new MicroVfpTestSupport.TempDir("idxapi");
        using var writer = DbfWriter.Create(temp.File("t.dbf"), [new DbfColumnDef("k", 'C', 10), new DbfColumnDef("active", 'L')]);
        writer.AppendRecord("BRAVO", true);
        writer.AppendRecord("ALPHA", true);
        string path = temp.File("x.idx");
        writer.CreateCompactIdx(path, "k", filtered ? "active AND NOT DELETED()" : null, collation: collation);
        writer.OpenCompactIdx(path);
        writer.AppendRecords(new[] { new object?[] { "DELTA", true }, new object?[] { "ECHO", true } });
        writer.UpdateRecord(0, new object?[] { "ZULU", true });
        Assert.Equal(new uint[] { 2, 3, 4, 1 }, Records());
        writer.Delete(0);
        Assert.Equal(filtered ? new uint[] { 2, 3, 4 } : new uint[] { 2, 3, 4, 1 }, Records());
        writer.Recall(0);
        Assert.Equal(new uint[] { 2, 3, 4, 1 }, Records());
        writer.Delete(1);
        writer.Pack();
        Assert.Equal(new uint[] { 2, 3, 1 }, Records());
        writer.CloseCompactIdx(path);
        byte[] before = File.ReadAllBytes(path);
        writer.AppendRecord("FOXTROT", true);
        Assert.Equal(before, File.ReadAllBytes(path));

        uint[] Records()
        {
            using var idx = IdxFile.Open(path);
            return idx.EnumerateEntries().Select(e => e.RecordNumber).ToArray();
        }
    }

    [Fact]
    public void UnsupportedOpenCompactIdx_RefusesBeforeChangingFiles()
    {
        using var temp = new MicroVfpTestSupport.TempDir("idxrefuse");
        string dbf = temp.File("t.dbf"), idx = temp.File("x.idx");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("k", 'C', 10)]))
        {
            writer.AppendRecord("ALPHA");
            writer.CreateCompactIdx(idx, "k");
        }
        byte[] bytes = File.ReadAllBytes(idx);
        bytes[512] = (byte)'q'; // a key field absent from the table; same key width on disk.
        File.WriteAllBytes(idx, bytes);
        byte[] tableBefore = File.ReadAllBytes(dbf);
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        var vfp = new VfpInterpreter(session);
        vfp.Execute("USE t INDEX x.idx");
        Assert.ThrowsAny<Exception>(() => vfp.Execute("GO 1\nREPLACE k WITH 'ZULU'"));
        vfp.Execute("USE");
        Assert.Equal(tableBefore, File.ReadAllBytes(dbf));
        Assert.Equal(bytes, File.ReadAllBytes(idx));
        vfp.Execute("USE t INDEX x.idx");
        Assert.ThrowsAny<Exception>(() => vfp.Execute("INSERT INTO t VALUES ('DELTA')"));
        vfp.Execute("USE");
        Assert.Equal(tableBefore, File.ReadAllBytes(dbf));
        Assert.Equal(bytes, File.ReadAllBytes(idx));
    }

    [Fact]
    public void SetIndexTo_ClosesCachedWritersIndexRegistration()
    {
        using var temp = new MicroVfpTestSupport.TempDir("idxclose");
        using (var writer = DbfWriter.Create(temp.File("t.dbf"), [new DbfColumnDef("k", 'C', 10)]))
        {
            writer.AppendRecord("ALPHA");
            writer.CreateCompactIdx(temp.File("x.idx"), "k");
        }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        var vfp = new VfpInterpreter(session);
        vfp.Execute("USE t INDEX x.idx\nGO 1\nREPLACE k WITH 'BRAVO'");
        byte[] indexed = File.ReadAllBytes(temp.File("x.idx"));
        vfp.Execute("SET INDEX TO\nREPLACE k WITH 'DELTA'");
        Assert.Equal(indexed, File.ReadAllBytes(temp.File("x.idx")));
    }

}
