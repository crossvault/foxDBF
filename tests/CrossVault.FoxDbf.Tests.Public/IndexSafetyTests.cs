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
    public void UnsupportedBuiltin_UseSucceeds_SeekAndWriteRefuseWithoutChangingFiles()
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = CreateFixture(temp.Path, "builtin");
        var before = new[] { dbf, Path.ChangeExtension(dbf, ".fpt"), Path.ChangeExtension(dbf, ".cdx") }
            .ToDictionary(p => p, File.ReadAllBytes);
        using (var session = new VfpSession())
        {
            session.OpenDirectory(temp.Path);
            using var interpreter = new VfpInterpreter(session);
            interpreter.Execute("USE t");
            Assert.True(interpreter.EvalExpression("USED('t')").AsLogical);
        }
        using (var table = DbfTable.Open(dbf))
        using (var index = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table))
        {
            var error = Assert.Throws<NotSupportedException>(() => index.Tag("BAD")!.Seek(1));
            Assert.Contains("CEILING", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        using (var writer = DbfWriter.Open(dbf))
        {
            var error = Assert.Throws<DbfWriteException>(() => writer.AppendRecord("BRAVO", "memo"));
            Assert.Contains("CEILING", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("approximate")]
    [InlineData("german")]
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
    [InlineData("german")]
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
            Assert.Contains(kind == "approximate" ? "852" : "1252", error.Message);
            Assert.Contains(kind == "approximate" ? "GENERAL" : kind == "german" ? "GERMAN" : "MACHINE", error.Message);
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
        Assert.Throws<NotSupportedException>(() => index.Tag("BAD")!.Seek("ALPHA"));
    }

    [Theory]
    [InlineData(false, "GENERAL")]
    [InlineData(true, "GENERAL")]
    [InlineData(false, "SPANISH")]
    [InlineData(true, "SPANISH")]
    public void ApproximateBuild_RefusesBeforeCreatingIndex(bool compact, string collation)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string dbf = Path.Combine(temp.Path, "t.dbf");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("k", 'C', 10)]))
            writer.AppendRecord("ALPHA");
        byte[] bytes = File.ReadAllBytes(dbf);
        bytes[29] = 0x64;
        File.WriteAllBytes(dbf, bytes);
        string index = Path.ChangeExtension(dbf, compact ? ".idx" : ".cdx");
        using (var writer = DbfWriter.Open(dbf))
            Assert.Throws<DbfWriteException>(() =>
            {
                if (compact) writer.CreateCompactIdx(index, "k", collation: collation);
                else writer.CreateTag(new CdxTagDefinition("BAD", "k", collation: collation));
            });
        Assert.Equal(bytes, File.ReadAllBytes(dbf));
        Assert.False(File.Exists(index));
    }

    private static string CreateFixture(string dir, string kind)
    {
        string dbf = IndexMaintTestSupport.CreateTable(dir, "t.dbf",
            [new DbfColumnDef("k", 'C', 10), new DbfColumnDef("note", 'M')],
            [new object?[] { "ALPHA", "original memo" }],
            new CdxTagDefinition("GOOD", "k"),
            new CdxTagDefinition("BAD", "UPPER(k)", collation: kind is "udf" or "builtin" ? "MACHINE" : "GENERAL"));
        string cdx = Path.ChangeExtension(dbf, ".cdx");
        byte[] bytes = File.ReadAllBytes(cdx);
        if (kind == "udf") Replace(bytes, "UPPER(k)", "e1udf(k)");
        if (kind == "builtin") Replace(bytes, "UPPER(k)", "CEIL(1) ");
        if (kind == "german") Replace(bytes, "GENERAL", "GERMAN\0");
        File.WriteAllBytes(cdx, bytes);
        if (kind == "approximate")
        {
            bytes = File.ReadAllBytes(dbf);
            bytes[29] = 0x64;
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

