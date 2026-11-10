using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class NullableIndexHeaderTests
{
    public static IEnumerable<object[]> Cases => from type in new[] { "C(8)", "N(8,2)", "D" }
        from format in new[] { "TAG kk", "TO extra.idx", "TO extra.idx COMPACT" }
        select new object[] { type, format };

    [Theory]
    [MemberData(nameof(Cases))]
    public void NullableKeyHeader_SetsVfp9NullMarkerOption(string type, string format)
    {
        using var temp = new MicroVfpTestSupport.TempDir("nullable_header");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute($"CREATE TABLE t FREE (c1 {type} NULL)\nINSERT INTO t VALUES (.NULL.)\nAPPEND BLANK\nINDEX ON c1 {format}\nUSE");
        string path = temp.File(format == "TAG kk" ? "t.cdx" : "extra.idx");
        int offset = 0;
        if (format == "TAG kk")
        {
            using var index = IndexFile.Open(path);
            var header = index.ReadCdxHeader(0);
            offset = (int)IndexTraversal.EnumerateCompact(index, header.Root, header.KeyLength, pad: 0x20).Single().Recno;
        }
        // Pinned by the complete native-header comparison in NullableIndexHeaderVfp9OracleTests.
        Assert.Equal(format == "TAG kk" ? 0x62 : format == "TO extra.idx" ? 0x02 : 0x22,
            File.ReadAllBytes(path)[offset + 14]);
    }
}
