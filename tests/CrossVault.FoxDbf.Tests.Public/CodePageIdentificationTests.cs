using System.Text;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Write;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class CodePageIdentificationTests
{
    [Theory]
    [InlineData(0x04, 10000)]
    [InlineData(0x68, 895)]
    [InlineData(0x69, 620)]
    [InlineData(0x7d, 1255)]
    [InlineData(0x7e, 1256)]
    [InlineData(0x96, 10007)]
    [InlineData(0x97, 10029)]
    [InlineData(0x98, 10006)]
    public void MissingVfpCodePages_AreResolved(int id, int cp)
    {
        Assert.Equal(cp, Encodings.ResolveCodePage((byte)id));
        Assert.Equal(cp, Encodings.ResolveEncoding((byte)id).CodePage);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xaf)]
    public void UnknownId_CpDbfIsZero(int id)
    {
        using var dir = new SqlTestSupport.TempDir();
        using (var w = DbfWriter.Create(dir.File("probe.dbf"),
            [new DbfColumnDef("TXT", 'C', 2)], new DbfCreateOptions { CodePage = (byte)id }))
            w.AppendRecord("AB");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var vfp = new VfpInterpreter(session);
        vfp.Execute("USE probe");
        Assert.Equal(0m, vfp.EvalExpression("CPDBF()").AsNumber);
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x03)]
    public void CharacterBytes_AreNotImplicitlyUtf8(int version)
    {
        using var dir = new SqlTestSupport.TempDir();
        using (var w = DbfWriter.Create(dir.File("probe.dbf"),
            [new DbfColumnDef("TXT", 'C', 2)], new DbfCreateOptions { CodePage = 3 }))
            w.AppendRecord("\u00c3\u00a4");
        byte[] bytes = File.ReadAllBytes(dir.File("probe.dbf"));
        bytes[0] = (byte)version;
        File.WriteAllBytes(dir.File("probe.dbf"), bytes);
        using var table = DbfTable.Open(dir.File("probe.dbf"));
        Assert.Equal("\u00c3\u00a4", table.GetRecord(0)!.Value.GetString("TXT"));
    }
    [Fact]
    public void Utf8Detection_IsOptInAndExplicitEncodingWins()
    {
        string path = Fixtures.VfpTest("codepages/id3.dbf");
        using var detected = DbfTable.Open(path, new DbfOptions { DetectUtf8 = true });
        Assert.Equal("\u00e4", detected.GetRecord(228)!.Value.GetString("TXT"));
        using var forced = DbfTable.Open(path, new DbfOptions
        {
            DetectUtf8 = true,
            Encoding = Encoding.GetEncoding(1251),
        });
        Assert.Equal(Encoding.GetEncoding(1251).GetString(new byte[] { 195, 164 }),
            forced.GetRecord(228)!.Value.GetString("TXT"));
        Assert.Equal(1252, forced.CodePage);
    }

}
