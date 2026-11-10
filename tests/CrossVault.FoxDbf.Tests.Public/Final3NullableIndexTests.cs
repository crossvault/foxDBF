using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class Final3NullableIndexTests
{
    [Theory]
    [InlineData("N(8,2)")]
    [InlineData("F(8,2)")]
    public void NullableBlankNumber_IndexesZeroInsteadOfNull(string type)
        => Assert.Equal(".F.|.T.", MicroVfpXbaseUpdateTests.Run(
            $"CREATE TABLE t (n {type} NULL)\nAPPEND BLANK\nINDEX ON n TAG kk",
            "USE t ORDER kk\nresult=TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(SEEK(0))"));

    [Theory]
    [InlineData("TAG kk")]
    [InlineData("TO extra.idx")]
    [InlineData("TO extra.idx COMPACT")]
    public void NullableVarbinary_SeeksNullAndBinaryAfterMaintenance(string format)
        => Assert.Equal(".T.|.T.|.T.|2|.T.|1", MicroVfpXbaseUpdateTests.Run(
            $"CREATE TABLE t (c1 Q(8) NULL)\nINSERT INTO t VALUES (.NULL.)\nINSERT INTO t VALUES (CREATEBINARY('AB'))\nINDEX ON c1 {format}",
            "USE t" + (format.StartsWith("TAG") ? " ORDER kk" : " INDEX extra.idx") + "\nSET ORDER TO 1\n"
            + "result=TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(SEEK(CREATEBINARY('AB')))+'|'\nGO 2\nREPLACE c1 WITH .NULL.\nGO 1\nREPLACE c1 WITH CREATEBINARY('AB')\n"
            + "result=result+TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(SEEK(CREATEBINARY('AB')))+'|'+TRANSFORM(RECNO())"));

    [Fact]
    public void NullableDecode_AllocatesOnlyOneRawKeyCopy()
    {
        using var temp = new MicroVfpTestSupport.TempDir("final3_decode");
        using (var writer = DbfWriter.Create(temp.File("t.dbf"),
            [new DbfColumnDef("c", 'C', 8), new DbfColumnDef("nc", 'C', 8, nullable: true)]))
        {
            writer.AppendRecord(["alpha", "alpha"]);
            writer.CreateTag(new CdxTagDefinition("plain", "c"));
            writer.CreateTag(new CdxTagDefinition("nullable", "nc"));
        }
        using var table = DbfTable.Open(temp.File("t.dbf"));
        using var cdx = CdxFile.Open(temp.File("t.cdx"), table);
        long plain = Allocated(cdx.Tag("plain")!);
        long nullable = Allocated(cdx.Tag("nullable")!);
        Assert.InRange(nullable, 1, plain + 8192);

        static long Allocated(CdxTag tag)
        {
            byte[] key = tag.EnumerateEntries().Single().Key;
            for (int i = 0; i < 100; i++) _ = tag.DecodeKey(key);
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) _ = tag.DecodeKey(key);
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }

    [Theory]
    [InlineData("TAG id", "MACHINE")]
    [InlineData("TAG id", "GENERAL")]
    [InlineData("TO extra.idx", "MACHINE")]
    [InlineData("TO extra.idx COMPACT", "MACHINE")]
    public void NullSeek_NonNullableZeroKeysDoNotMatch(string format, string collate)
        => Assert.Equal(".F.|.F.|.F.|.F.", MicroVfpXbaseUpdateTests.Run(
            $"SET COLLATE TO '{collate}'\nCREATE TABLE t (id C(4))\nINSERT INTO t VALUES ('')\nINSERT INTO t VALUES (REPLICATE(CHR(0),4))\nINDEX ON id {format}",
            "USE t" + (format.StartsWith("TAG") ? " ORDER id" : " INDEX extra.idx")
            + "\nSET ORDER TO 1\nresult=TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(KEYMATCH(.NULL.))+'|'+TRANSFORM(INDEXSEEK(.NULL.))\nSEEK .NULL.\nresult=result+'|'+TRANSFORM(FOUND())"));

    [Theory]
    [InlineData("MACHINE", "TAG kk", 239, false)]
    [InlineData("MACHINE", "TAG kk", 240, true)]
    [InlineData("GENERAL", "TAG kk", 119, false)]
    [InlineData("GENERAL", "TAG kk", 120, true)]
    [InlineData("MACHINE", "TO extra.idx COMPACT", 239, false)]
    [InlineData("MACHINE", "TO extra.idx COMPACT", 240, true)]
    [InlineData("GENERAL", "TO extra.idx COMPACT", 120, true)]
    [InlineData("MACHINE", "TO extra.idx", 99, false)]
    [InlineData("MACHINE", "TO extra.idx", 100, true)]
    public void IndexOn_ValidatesFullEncodedWidth(string collate, string format, int width, bool error)
        => Assert.Equal(error ? "ERR:112|" : "", MicroVfpXbaseUpdateTests.Run(
            $"CREATE TABLE t (c C({width}) NULL)\nAPPEND BLANK",
            $"USE t\nSET COLLATE TO '{collate}'\n" + MicroVfpSqlDmlTests.Catch("INDEX ON c " + format)));

    [Theory]
    [InlineData("GENERAL")]
    [InlineData("SPANISH")]
    public void NullableWeightedEquality_ExaminesOnlyMatchingHeadRun(string collate)
    {
        using var temp = new MicroVfpTestSupport.TempDir("final3_bounds");
        using (var writer = DbfWriter.Create(temp.File("t.dbf"), [new DbfColumnDef("c", 'C', 12, nullable: true)]))
        {
            for (int i = 0; i < 2000; i++) writer.AppendRecord(["KEY" + i.ToString("D4")]);
            writer.CreateTag(new CdxTagDefinition("kk", "c", collation: collate));
        }
        using var table = DbfTable.Open(temp.File("t.dbf"));
        using var cdx = CdxFile.Open(temp.File("t.cdx"), table);
        int examined = 0;
        var result = QueryOptimizer.FindRecords(table, cdx, "c='KEY1000'",
            new EvaluationContext { Collation = VfpCollations.ByName(collate) }, onIndexEntryExamined: _ => examined++);
        Assert.Equal(new[] { 1001 }, result.RecordNumbers);
        // The cold B-tree walk may visit the earlier entries in its first leaf. Pin the
        // candidate count, which excludes that descent overhead, as well as bounded work.
        Assert.Equal(1, result.RecordsScanned);
        Assert.InRange(examined, 1, 70);
    }

    [Theory]
    [InlineData("c", 9)]
    [InlineData("UPPER(c)", 9)]
    [InlineData("c+c2", 11)]
    [InlineData("NVL(c,'x')", 9)]
    [InlineData("IIF(ISNULL(c),'x',c)", 9)]
    [InlineData("ISNULL(c)", 1)]
    public void NullableExpressionKeys_BuildAndMaintainMarker(string expression, int width)
    {
        using var temp = new MicroVfpTestSupport.TempDir("final3_expression");
        using (var writer = DbfWriter.Create(temp.File("t.dbf"),
            [new DbfColumnDef("c", 'C', 8, nullable: true), new DbfColumnDef("c2", 'C', 2)]))
        {
            writer.AppendRecord([null, ""]);
            writer.AppendRecord(["", ""]);
            writer.CreateTag(new CdxTagDefinition("kk", expression));
        }
        using (var table = DbfTable.Open(temp.File("t.dbf")))
        using (var cdx = CdxFile.Open(temp.File("t.cdx"), table))
        {
            Assert.Equal(width, cdx.Tag("kk")!.KeyLength);
            Assert.Equal(expression == "ISNULL(c)" ? IndexKeyType.Logical : IndexKeyType.Character, cdx.Tag("kk")!.KeyType);
        }
        using (var writer = DbfWriter.Open(temp.File("t.dbf")))
        {
            writer.UpdateRecord(0, new object?[] { "", "" });
            writer.UpdateRecord(1, new object?[] { null, "" });
        }
        using var updatedTable = DbfTable.Open(temp.File("t.dbf"));
        using var updatedCdx = CdxFile.Open(temp.File("t.cdx"), updatedTable);
        uint firstRecord = expression is "ISNULL(c)" or "NVL(c,'x')" or "IIF(ISNULL(c),'x',c)" ? 1u : 2u;
        Assert.Equal(firstRecord, updatedCdx.Tag("kk")!.EnumerateEntries().First().RecordNumber);
    }
}
