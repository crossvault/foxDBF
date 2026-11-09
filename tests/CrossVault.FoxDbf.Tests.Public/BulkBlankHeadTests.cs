using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class BulkBlankHeadTests
{
    [Theory]
    [InlineData("GENERAL", 0x61, 0x62)]
    [InlineData("GERMAN", 0x61, 0x62)]
    [InlineData("SPANISH", 0x61, 0x62)]
    [InlineData("NORDAN", 0x61, 0x62)]
    [InlineData("SWEFIN", 0x61, 0x62)]
    [InlineData("ICELAND", 0x62, 0x63)]
    public void BulkDisplacedBlankPrefix_AndIncrementalSeek_MatchNativeFixture(string sequence, byte bHead, byte cHead)
    {
        // Synthetic VFP9 fixture: BulkPrefixBoundaryVfp9OracleTests, C(8), 160 controls.
        // The nearly full leaf inserts TAB keys before the shorter B key. On overflow
        // VFP carries B+control+C to the next leaf but retains B+TAB as its prefix cache.
        string[] rows = Enumerable.Range(0, 160).Select(i => "B" + (char)(i % 32))
            .Concat(new[] { "B\t", "B ", "B\u0001", "B\u0001C", "B\u00a0", "B!", "B\tC", "B C" }).ToArray();
        using var temp = new IndexMaintTestSupport.TempDir();
        foreach (bool incremental in new[] { false, true })
        {
            string path = Path.Combine(temp.Path, incremental ? "incremental.dbf" : "bulk.dbf");
            using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 8)], new DbfCreateOptions { CodePage = 3 }))
            {
                if (incremental) writer.CreateTag(new CdxTagDefinition("K", "name", collation: sequence));
                foreach (string row in rows) writer.AppendRecord(row);
                if (!incremental) writer.CreateTag(new CdxTagDefinition("K", "name", collation: sequence));
            }
            using var table = DbfTable.Open(path);
            using var index = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
            var tag = index.Tag("K")!;
            var entries = tag.EnumerateEntries().ToArray();
            var keys = entries.ToDictionary(e => e.RecordNumber, e => e.Key);
            byte[] expected = new byte[16];
            expected[0] = bHead;
            expected[1] = incremental ? (byte)0x11 : (byte)0x10;
            expected[2] = cHead;
            Assert.Equal(expected, keys[167]);
            Assert.Equal(incremental ? new uint[] { 162, 1, 2, 3, 4 } : new uint[] { 10, 42, 74, 106, 162 },
                entries.Take(5).Select(e => e.RecordNumber));
            Assert.Equal(incremental ? (uint?)10 : null, tag.Seek(tag.CreateCharacterSeekNeedle("B\t", exact: true)));
            Assert.Equal(incremental ? (uint?)10 : null, tag.Seek(tag.CreateCharacterSeekNeedle("B ", exact: false)));
            Assert.Equal(incremental ? (uint?)162 : null, tag.Seek(tag.CreateCharacterSeekNeedle("B ", exact: true)));
            if (sequence is not ("GENERAL" or "GERMAN"))
            {
                Assert.Equal(incremental ? (uint?)10 : null, tag.Seek(tag.CreateCharacterSeekNeedle("B\u00a0", exact: false)));
                Assert.Equal(incremental ? (uint?)165 : null, tag.Seek(tag.CreateCharacterSeekNeedle("B\u00a0", exact: true)));
            }
        }
    }
}
