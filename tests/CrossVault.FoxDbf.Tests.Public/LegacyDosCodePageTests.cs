using System.Globalization;
using System.Text;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class LegacyDosCodePageTests
{
    private static string Fixture(int cp, string extension)
        => Fixtures.VfpTest($"codepages/cp{cp}.{extension}");

    // Independent published tables plus the orchestrator's VFP9 precedence rule.
    private static char[] ExpectedTable(int cp)
    {
        char[] standard = File.ReadAllText(Fixture(cp, "standard.txt"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => (char)int.Parse(s, NumberStyles.HexNumber)).ToArray();
        Assert.Equal(256, standard.Length);
        string windows = Encoding.GetEncoding(1250).GetString(File.ReadAllBytes(Fixture(cp, "1250")));
        string dos = Encoding.GetEncoding(852).GetString(File.ReadAllBytes(Fixture(cp, "852")));
        byte[] backWindows = File.ReadAllBytes(Fixture(cp, "back1250"));
        byte[] backDos = File.ReadAllBytes(Fixture(cp, "back"));
        var overrides = new List<int>();
        for (int i = 0; i < 256; i++)
        {
            if (windows[i] == dos[i] && backWindows[i] == i && backDos[i] == i)
            {
                if (standard[i] != windows[i]) overrides.Add(i);
                standard[i] = windows[i];
            }
        }
        Assert.Equal(cp == 620 ? new[] { 0xa8 } : new[] { 0xb6, 0xdd }, overrides);
        return standard;
    }

    [Theory]
    [InlineData(620, 0x69)]
    [InlineData(895, 0x68)]
    public void PerByteTable_MatchesStandardWithReversibleVfpAgreement(int cp, int id)
    {
        var encoding = Encodings.ResolveEncoding((byte)id);
        char[] expected = ExpectedTable(cp);
        byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Assert.Equal(new string(expected), encoding.GetString(bytes));
        Assert.Equal(bytes, encoding.GetBytes(expected));
        Assert.Equal(256, expected.Distinct().Count());
        for (int i = 0; i < 128; i++) Assert.Equal((char)i, expected[i]);
        // Exercise the span paths used by DBF fields as well as the array APIs.
        char[] chars = new char[258];
        byte[] encoded = new byte[258];
        Assert.Equal(256, encoding.GetChars(bytes.AsSpan(), chars.AsSpan(1, 256)));
        Assert.Equal(expected, chars.AsSpan(1, 256).ToArray());
        Assert.Equal(256, encoding.GetBytes(chars.AsSpan(1, 256), encoded.AsSpan(1, 256)));
        Assert.Equal(bytes, encoded.AsSpan(1, 256).ToArray());
    }

    [Theory]
    [InlineData(620)]
    [InlineData(895)]
    public void Read_VfpFile_UsesDocumentedUnicode(int cp)
    {
        char[] expected = ExpectedTable(cp);
        using var table = DbfTable.Open(Fixture(cp, "dbf"));
        Assert.Equal(cp, table.Encoding.CodePage);
        Assert.Equal(cp, table.CodePage);
        for (int i = 0; i < 256; i++)
        {
            var record = table.GetRecord(i)!.Value;
            Assert.Equal(new byte[] { (byte)i, (byte)'X' }, record.GetRawField(0).ToArray());
            Assert.Equal((expected[i] + "X").Trim('\0', '\t', '\n', '\v', '\f', '\r', ' '),
                record.GetString("TXT"));
        }
    }

    [Theory]
    [InlineData(620)]
    [InlineData(895)]
    public void Write_VfpFile_PreservesEveryRecordByte(int cp)
    {
        using var dir = new SqlTestSupport.TempDir();
        string file = dir.File("write.dbf");
        File.Copy(Fixture(cp, "dbf"), file);
        byte[] before = File.ReadAllBytes(file);
        char[] expected = ExpectedTable(cp);
        using (var writer = DbfWriter.Open(file))
            for (int i = 0; i < 256; i++)
                writer.UpdateRecord(i, new object?[] { expected[i] + "X" });
        byte[] after = File.ReadAllBytes(file);
        before.AsSpan(1, 3).CopyTo(after.AsSpan(1, 3));
        Assert.Equal(before, after);
    }
}
