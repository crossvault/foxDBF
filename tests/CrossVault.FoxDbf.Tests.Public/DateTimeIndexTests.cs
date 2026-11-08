using System.Globalization;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class DateTimeIndexTests
{
    // Key bytes from DateTimeIndexVfp9OracleTests, including dates with different double exponents.
    [Theory]
    [InlineData("1900-01-01T12:30:00", "C1426CD6C2AAAAAA")]
    [InlineData("2024-01-02T00:00:00", "C142C54C00000000")]
    [InlineData("2024-01-02T00:00:01", "C142C54C00006117")]
    [InlineData("2024-01-02T00:00:02", "C142C54C0000C22E")]
    [InlineData("2024-01-02T06:00:00", "C142C54C20000000")]
    [InlineData("2024-01-02T09:59:59", "C142C54C3554F43E")]
    [InlineData("2024-01-02T10:00:00", "C142C54C35555555")]
    [InlineData("2024-01-02T12:00:00", "C142C54C40000000")]
    [InlineData("2024-01-02T12:30:00", "C142C54C42AAAAAA")]
    [InlineData("2024-01-02T12:30:01", "C142C54C42AB0BC2")]
    [InlineData("2024-01-02T12:34:57", "C142C54C431B4E81")]
    [InlineData("2024-01-02T23:59:59", "C142C54C7FFF9EE9")]
    [InlineData("9998-12-30T00:00:01", "C1547F2F8000308B")]
    [InlineData("9998-12-30T10:00:00", "C1547F2F9AAAAAAA")]
    public void DateTimeKey_UsesVfp9Double(string text, string expected)
    {
        var value = DateTime.Parse(text, CultureInfo.InvariantCulture);
        Assert.Equal(expected, Convert.ToHexString(IndexKey.Encode(value, IndexKeyType.DateTime)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DateTimeKeys_MillisecondFieldsAndSeekUseVfp9Bytes(bool incremental)
    {
        using var temp = new MicroVfpTestSupport.TempDir("dtkey");
        string path = temp.File("times.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("STAMP", 'T', 8)]))
        {
            if (incremental) writer.CreateTag(new CdxTagDefinition("stamp", "stamp"));
            writer.AppendRecord(new DateTime(2024, 1, 2, 12, 30, 0, 499));
            writer.AppendRecord(new DateTime(2024, 1, 2, 12, 30, 0, 500));
            if (!incremental) writer.CreateTag(new CdxTagDefinition("stamp", "stamp"));
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        var tag = cdx.Tag("stamp")!;
        Assert.Equal(new[] { "C142C54C42AAAAAA", "C142C54C42AB0BC2" },
            tag.EnumerateEntries().Select(e => Convert.ToHexString(e.Key)));
        Assert.Equal(1u, tag.Seek(new DateTime(2024, 1, 2, 12, 30, 0)));
        Assert.Equal(2u, tag.Seek(new DateTime(2024, 1, 2, 12, 30, 1)));
    }
}
