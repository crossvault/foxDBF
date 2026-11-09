using System.Buffers.Binary;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class CdxControlStatisticsTests
{
    [Theory]
    [InlineData("MACHINE", 37u)]
    [InlineData("GENERAL", 7u)]
    [InlineData("DUTCH", 7u)]
    public void ControlCorpus_BuildAndMaintenance_PreserveVfp9Statistics(string sequence, uint distinct)
    {
        // CdxControlStatisticsVfp9OracleTests: INDEX ON, append, REPLACE and REINDEX.
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = Path.Combine(dir.Path, "stats.dbf");
        string[] rows = Enumerable.Range(0, 160).Select(i => "B" + (char)(i % 32))
            .Concat(new[] { "B\t", "B ", "B\u0001", "B\u0001C", "B\u00a0", "B!", "B\tC", "B C" }).ToArray();
        using var writer = DbfWriter.Create(dbf, [new DbfColumnDef("name", 'C', 12)], new DbfCreateOptions { CodePage = 3 });
        foreach (string row in rows) writer.AppendRecord(row);
        writer.CreateTag(new CdxTagDefinition("k", "name", collation: sequence));
        byte[] BuiltStatistics() => File.ReadAllBytes(Path.ChangeExtension(dbf, ".cdx")).AsSpan(1552, 20).ToArray();
        byte[] initial = BuiltStatistics();
        Assert.Equal(distinct, BinaryPrimitives.ReadUInt32LittleEndian(initial.AsSpan(4)));
        writer.AppendRecords(Enumerable.Range(0, 200).Select(i => new object?[] { $"Z{i:D3}" }));
        writer.Flush();
        Assert.Equal(initial, BuiltStatistics());
        writer.UpdateRecord(0, new object?[] { "Q" });
        writer.Flush();
        Assert.Equal(initial, BuiltStatistics());
        writer.Reindex();
        Assert.Equal(sequence == "MACHINE" ? 238u : 208u, BinaryPrimitives.ReadUInt32LittleEndian(BuiltStatistics().AsSpan(4)));
    }

    [Theory]
    [InlineData("\0", "\t", 2u)]
    [InlineData("\0", " ", 1u)]
    [InlineData("\t", " ", 1u)]
    [InlineData("A\0", "A", 1u)]
    [InlineData("A\t", "A", 1u)]
    [InlineData("A\0A", "A\0B", 2u)]
    [InlineData("A B", "A", 2u)]
    public void DistinctCount_ComparesOnlyTheCurrentUnpaddedPrefix(string first, string second, uint expected)
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        using var writer = DbfWriter.Create(Path.Combine(dir.Path, "pair.dbf"), [new DbfColumnDef("name", 'C', 12)]);
        writer.AppendRecord(first);
        writer.AppendRecord(second);
        writer.CreateTag(new CdxTagDefinition("k", "name"));
        writer.CreateCompactIdx(Path.Combine(dir.Path, "pair.idx"), "name");
        foreach (var (name, offset) in new[] { ("pair.cdx", 1536), ("pair.idx", 0) })
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Path.Combine(dir.Path, name)).AsSpan(offset + 20)));
    }
}
