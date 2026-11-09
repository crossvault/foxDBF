using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class IndexSafetyTests
{
    [Fact]
    public void CeilingIndex_UseSeekAndWriteSucceed()
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = Path.Combine(temp.Path, "t.dbf");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("k", 'N', 8, 2)]))
        {
            writer.AppendRecord(0.5);
            writer.CreateTag(new CdxTagDefinition("bad", "CEILING(k)"));
        }
        using (var session = new VfpSession())
        {
            session.OpenDirectory(temp.Path);
            using var interpreter = new VfpInterpreter(session);
            interpreter.Execute("USE t\nSET ORDER TO bad\nSEEK 1");
            Assert.True(interpreter.EvalExpression("FOUND()").AsLogical);
        }
        using (var writer = DbfWriter.Open(dbf)) writer.AppendRecord(1.5);
        using var table = DbfTable.Open(dbf);
        using var index = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table);
        Assert.Equal(2, index.Tag("BAD")!.EnumerateEntries().Count());
        Assert.Equal(1u, index.Tag("BAD")!.Seek(1));
    }

    [Theory]
    [InlineData("approximate")]
    [InlineData("thai")]
    [InlineData("udf")]
    public void UnmaintainableTag_AlterPreservesFiles(string kind)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = CreateFixture(temp.Path, kind);
        var before = new[] { dbf, Path.ChangeExtension(dbf, ".fpt"), Path.ChangeExtension(dbf, ".cdx") }
            .ToDictionary(p => p, File.ReadAllBytes);
        using (var writer = DbfWriter.Open(dbf))
            Assert.Throws<DbfWriteException>(() => writer.AddColumn(new DbfColumnDef("extra", 'C', 3)));
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void MissingUdf_UseRaisesOne_WhileReadOnlyApiStillWorks()
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = CreateFixture(temp.Path, "udf");
        using (var table = DbfTable.Open(dbf))
            Assert.Equal("ALPHA", ((string)table.GetRecord(0)!.Value["k"]!).Trim());
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        var interpreter = new VfpInterpreter(session);
        Assert.Equal(1, Assert.Throws<MicroVfpRuntimeException>(() => interpreter.Execute("USE t")).VfpErrorNumber);
        Assert.False(interpreter.EvalExpression("USED('t')").AsLogical);
    }

    [Theory]
    [InlineData("approximate")]
    [InlineData("thai")]
    [InlineData("udf")]
    public void UnmaintainableTag_AppendPreservesDbfMemoAndAllTags(string kind)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = CreateFixture(temp.Path, kind);
        string[] paths = [dbf, Path.ChangeExtension(dbf, ".fpt"), Path.ChangeExtension(dbf, ".cdx")];
        var before = paths.ToDictionary(p => p, File.ReadAllBytes);
        using (var writer = DbfWriter.Open(dbf))
        {
            var error = Assert.Throws<DbfWriteException>(() => writer.AppendRecord("DELTA", "new memo"));
            Assert.Contains("BAD", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(kind == "approximate" ? "1250" : kind == "thai" ? "874" : "1252", error.Message);
            Assert.Contains(kind == "approximate" ? "SPANISH" : kind == "thai" ? "THAI" : "MACHINE", error.Message);
            Assert.Equal(1, writer.RecordCount);
        }
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ApproximateSeek_RefusesToEncodeFallbackWeights()
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = CreateFixture(temp.Path, "approximate");
        using var table = DbfTable.Open(dbf);
        using var index = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table);
        var error = Assert.Throws<NotSupportedException>(() => index.Tag("BAD")!.Seek("ALPHA"));
        Assert.Contains("SPANISH", error.Message);
    }

    [Fact]
    public void UnsupportedSeek_MicroVfpRefusesWithSequenceName()
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        CreateFixture(temp.Path, "thai");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("USE t ORDER bad");
        var error = Assert.Throws<MicroVfpRuntimeException>(() => interpreter.Execute("SEEK 'ALPHA'"));
        Assert.Contains("THAI", error.Message);
    }

    [Theory]
    [InlineData(false, "GENERAL", 0xC9)]
    [InlineData(true, "GENERAL", 0xC9)]
    [InlineData(false, "SPANISH", 0xC8)]
    [InlineData(true, "SPANISH", 0xC8)]
    [InlineData(false, "THAI", 0x7C)]
    [InlineData(true, "THAI", 0x7C)]
    public void NonExactBuild_RefusesBeforeCreatingIndex(bool compact, string collation, byte mark)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = Path.Combine(temp.Path, "t.dbf");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("k", 'C', 10)]))
            writer.AppendRecord("ALPHA");
        byte[] bytes = File.ReadAllBytes(dbf);
        bytes[29] = mark;
        File.WriteAllBytes(dbf, bytes);
        string index = Path.ChangeExtension(dbf, compact ? ".idx" : ".cdx");
        using (var writer = DbfWriter.Open(dbf))
        {
            var error = Assert.Throws<DbfWriteException>(() =>
            {
                if (compact) writer.CreateCompactIdx(index, "k", collation: collation);
                else writer.CreateTag(new CdxTagDefinition("BAD", "k", collation: collation));
            });
            Assert.Contains(collation, error.Message);
        }
        Assert.Equal(bytes, File.ReadAllBytes(dbf));
        Assert.False(File.Exists(index));
    }

    private static string CreateFixture(string dir, string kind)
    {
        string dbf = IndexMaintTestSupport.CreateTable(dir, "t.dbf",
            [new DbfColumnDef("k", 'C', 10), new DbfColumnDef("note", 'M')],
            [new object?[] { "ALPHA", "original memo" }],
            new CdxTagDefinition("GOOD", "k"),
            new CdxTagDefinition("BAD", "UPPER(k)", collation: kind is "udf" or "builtin" ? "MACHINE" : kind == "approximate" ? "SPANISH" : "GENERAL"));
        string cdx = Path.ChangeExtension(dbf, ".cdx");
        byte[] bytes = File.ReadAllBytes(cdx);
        if (kind == "udf") Replace(bytes, "UPPER(k)", "e1udf(k)");
        if (kind == "builtin") Replace(bytes, "UPPER(k)", "CEIL(1) ");
        if (kind == "thai") Replace(bytes, "GENERAL", "THAI\0\0\0");
        File.WriteAllBytes(cdx, bytes);
        if (kind is "approximate" or "thai")
        {
            bytes = File.ReadAllBytes(dbf);
            // No VFP9-producible approximate cell remains: SPANISH/1250 is deliberately header-patched.
            // THAI/874 is deferred in Phase 4; the internal oracle builds real tags in a CP874 session.
            bytes[29] = kind == "thai" ? (byte)0x7C : (byte)0xC8;
            File.WriteAllBytes(dbf, bytes);
        }
        return dbf;
    }

    private static void Replace(byte[] bytes, string from, string to)
    {
        int offset = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(from));
        Assert.True(offset >= 0);
        Encoding.ASCII.GetBytes(to).CopyTo(bytes, offset);
    }
}

