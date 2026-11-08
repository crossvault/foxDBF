using System.Text;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class CodePageFixtureTests
{
    public static TheoryData<int> CodePages => new()
    {
        437, 850, 1252, 10000, 852, 866, 865, 861, 737, 857,
        874, 1250, 1251, 1253, 1254, 1255, 1256, 1257, 10007, 10029, 10006
    };
    private static string Fixture(string name) => Fixtures.VfpTest("codepages/" + name);

    [Theory]
    [MemberData(nameof(CodePages))]
    public void Read_VfpFile_UsesVfpUnicode(int cp)
    {
        string expected = Encoding.Unicode.GetString(File.ReadAllBytes(Fixture($"cp{cp}.unicode")));
        Assert.Equal(256, expected.Length);
        using var table = DbfTable.Open(Fixture($"cp{cp}.dbf"));
        Assert.Equal(cp, table.Encoding.CodePage);
        Assert.Equal(cp, table.CodePage);
        for (int i = 0; i < 256; i++)
        {
            var record = table.GetRecord(i)!.Value;
            Assert.Equal(new byte[] { (byte)i, (byte)'X' }, record.GetRawField(0).ToArray());
            Assert.Equal((expected[i] + "X").Trim('\0','\t','\n','\v','\f','\r',' '), record.GetString("TXT"));
        }
    }

    [Theory]
    [MemberData(nameof(CodePages))]
    public void Write_VfpFile_PreservesEveryRecordByte(int cp)
    {
        using var dir = new SqlTestSupport.TempDir();
        string file = dir.File("write.dbf");
        File.Copy(Fixture($"cp{cp}.dbf"), file);
        byte[] before = File.ReadAllBytes(file);
        string expected = Encoding.Unicode.GetString(File.ReadAllBytes(Fixture($"cp{cp}.unicode")));
        using (var writer = DbfWriter.Open(file))
            for (int i = 0; i < 256; i++)
                writer.UpdateRecord(i, new object?[] { expected[i] + "X" });
        AssertUnchangedExceptDate(before, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData(932)]
    [InlineData(936)]
    [InlineData(949)]
    [InlineData(950)]
    public void Read_DbcsVfpFile_UsesVfpUnicode(int cp)
    {
        using var table = DbfTable.Open(Fixture($"cp{cp}.dbf"));
        Assert.Equal(cp, table.Encoding.CodePage);
        Assert.Equal("\u4e00", table.GetRecord(0)!.Value.GetString("TXT"));
        Assert.Equal(File.ReadAllBytes(Fixture($"cp{cp}.sample")), table.GetRecord(0)!.Value.GetRawField(0).ToArray());
    }

    [Theory]
    [InlineData(932)]
    [InlineData(936)]
    [InlineData(949)]
    [InlineData(950)]
    public void Write_DbcsVfpFile_PreservesRecordBytes(int cp)
    {
        using var dir = new SqlTestSupport.TempDir();
        string file = dir.File("write.dbf");
        File.Copy(Fixture($"cp{cp}.dbf"), file);
        byte[] before = File.ReadAllBytes(file);
        using (var writer = DbfWriter.Open(file)) writer.UpdateRecord(0, new object?[] { "\u4e00" });
        AssertUnchangedExceptDate(before, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData(0, 1252)]
    [InlineData(175, 1252)]
    [InlineData(174, 1252)]
    [InlineData(0, 1251)]
    [InlineData(175, 1251)]
    [InlineData(174, 1251)]
    public void UnknownOrUnavailable_PreservesBytesAndIdentifier(int id, int sessionCodePage)
    {
        // .NET has Hebrew/Arabic even when VFP's table driver does not. Register an
        // unavailable page to exercise that same fallback independently of the OS.
        if (id == 174) Encodings.RegisterCodePage(174, int.MaxValue);
        using var dir = new SqlTestSupport.TempDir();
        string file = dir.File("write.dbf");
        File.Copy(Fixture($"id{id}.dbf"), file);
        byte[] before = File.ReadAllBytes(file);
        var encoding = Encoding.GetEncoding(sessionCodePage);
        var options = new DbfOptions { DefaultEncoding = encoding };
        string value;
        using (var table = DbfTable.Open(file, options))
        {
            Assert.Equal(0, table.CodePage);
            Assert.Equal(sessionCodePage, table.Encoding.CodePage);
            value = table.GetRecord(228)!.Value.GetString("TXT")!;
            Assert.Equal(encoding.GetString(new byte[] { 195, 164 }), value);
        }
        using (var writer = DbfWriter.Open(file, options)) writer.UpdateRecord(228, new object?[] { value });
        byte[] after = File.ReadAllBytes(file);
        Assert.Equal((byte)id, after[29]);
        AssertUnchangedExceptDate(before, after);
    }

    private static void AssertUnchangedExceptDate(byte[] before, byte[] after)
    {
        // Only the last-update date is allowed to change, even across a midnight run.
        before.AsSpan(1, 3).CopyTo(after.AsSpan(1, 3));
        Assert.Equal(before, after);
    }
}
