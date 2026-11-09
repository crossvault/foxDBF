using System.Buffers.Binary;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class IndexDecodeConsistencyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void YearOne_FieldKeys_RemainDistinctFromEmpty(bool timestamp, bool incremental)
    {
        using var dir = new MicroVfpTestSupport.TempDir("index_yearone");
        string path = dir.File("src.dbf");
        using (var writer = DbfWriter.Create(path,
            [new DbfColumnDef("VAL", timestamp ? 'T' : 'D', 8), new DbfColumnDef("ID", 'I', 4)]))
        {
            writer.AppendRecord(new object?[] { null, 1 });
            writer.AppendRecord(new object?[] { null, 2 });
        }
        // VFP9's real year-one bytes (FinalReviewSqlVfp9OracleTests); CLR MinValue is
        // also the legacy empty-date API sentinel, so seed the physical field explicitly.
        byte[] bytes = File.ReadAllBytes(path);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) + 1;
        if (timestamp) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), 1_721_426);
        else "00010101"u8.CopyTo(bytes.AsSpan(offset));
        File.WriteAllBytes(path, bytes);
        using (var writer = DbfWriter.Open(path))
        {
            writer.CreateTag(new CdxTagDefinition("VAL", "val"));
            writer.CreateCompactIdx(dir.File("compact.idx"), "val");
            writer.CreateStandaloneIdx(dir.File("plain.idx"), "val");
            if (incremental)
            {
                writer.Delete(0);
                writer.Recall(0);
            }
            else writer.Reindex();
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(dir.File("src.cdx"), table);
        using var compact = IdxFile.Open(dir.File("compact.idx"), table);
        using var plain = IdxFile.Open(dir.File("plain.idx"), table);
        var expected = new[] { (2u, "8000000000000000"), (1u, "C13A445200000000") };
        Assert.Equal(expected, cdx.Tag("VAL")!.EnumerateEntries().Select(e => (e.RecordNumber, Convert.ToHexString(e.Key))));
        Assert.Equal(expected, compact.EnumerateEntries().Select(e => (e.RecordNumber, Convert.ToHexString(e.Key))));
        Assert.Equal(expected, plain.EnumerateEntries().Select(e => (e.RecordNumber, Convert.ToHexString(e.Key))));
        Assert.Equal(1u, cdx.Tag("VAL")!.Seek(Convert.FromHexString(expected[1].Item2).AsSpan()));
        Assert.Equal(2u, cdx.Tag("VAL")!.Seek(Convert.FromHexString(expected[0].Item2).AsSpan()));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void CharacterKeys_UseTheFieldDecoder(bool detectUtf8, bool explicitEncoding, bool binary, bool incremental)
    {
        using var dir = new MicroVfpTestSupport.TempDir("index_decode");
        string path = dir.File("src.dbf");
        using (var writer = DbfWriter.Create(path,
            [new DbfColumnDef("NAME", 'C', 4) { Binary = binary }], new DbfCreateOptions { CodePage = 0x03 }))
            writer.AppendRecord("\u00c3\u00a9");

        var options = new DbfOptions
        {
            DetectUtf8 = detectUtf8,
            Encoding = explicitEncoding ? Encodings.ResolveEncoding(0x03) : null,
        };
        using (var writer = DbfWriter.Open(path, options))
        {
            writer.CreateTag(new CdxTagDefinition("NAME", "name"));
            writer.CreateTag(new CdxTagDefinition("EXPR", "name + '!'"));
            if (incremental) writer.AppendRecord("\u00c3\u00a9");
            writer.CreateCompactIdx(dir.File("compact.idx"), "name");
            writer.CreateStandaloneIdx(dir.File("plain.idx"), "name");
        }
        using var table = DbfTable.Open(path, options);
        using var cdx = CdxFile.Open(dir.File("src.cdx"), table);
        using var compact = IdxFile.Open(dir.File("compact.idx"), table);
        using var plain = IdxFile.Open(dir.File("plain.idx"), table);
        string value = detectUtf8 && !explicitEncoding && !binary ? "é" : "\u00c3\u00a9";
        Assert.Equal(value, table.Records.First()["NAME"]);
        Assert.Equal(1u, cdx.Tag("NAME")!.Seek(value));
        Assert.Equal(cdx.Tag("NAME")!.EnumerateEntries().Select(e => Convert.ToHexString(e.Key)),
            compact.EnumerateEntries().Select(e => Convert.ToHexString(e.Key)));
        Assert.Equal(cdx.Tag("NAME")!.EnumerateEntries().Select(e => Convert.ToHexString(e.Key)),
            plain.EnumerateEntries().Select(e => Convert.ToHexString(e.Key)));
        Assert.Equal(1u, cdx.Tag("EXPR")!.Seek(value + "  !"));
        var result = QueryOptimizer.FindRecords(table, cdx, $"name = '{value}'");
        Assert.True(result.Optimized);
        Assert.Equal(incremental ? new[] { 1, 2 } : new[] { 1 }, result.RecordNumbers);
        Assert.Equal(result.RecordNumbers, QueryOptimizer.FindRecords(table, null, $"name = '{value}'").RecordNumbers);
    }
}
