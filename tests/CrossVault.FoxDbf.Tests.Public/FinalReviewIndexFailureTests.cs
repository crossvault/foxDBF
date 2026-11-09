using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class FinalReviewIndexFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedWrite_PreservesAutoIncrementAndMemoReferences(bool update)
    {
        using var temp = new MicroVfpTestSupport.TempDir("memo_undo");
        string dbf = temp.File("t.dbf");
        using (var writer = DbfWriter.Create(dbf,
            [new DbfColumnDef("id", 'I', autoIncrement: new DbfAutoIncrement(1)),
             new DbfColumnDef("k", 'C', 10), new DbfColumnDef("note", 'M')]))
        {
            writer.AppendRecord(null, "ALPHA", "original memo");
            writer.CreateTag(new CdxTagDefinition("keytag", "k"));
        }
        byte[] before = File.ReadAllBytes(dbf);
        using (var writer = DbfWriter.Open(dbf))
        {
            writer.FailIndexMaintenanceForTests = true;
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (update) writer.UpdateRecord(0, [1, "CHARLIE", "failed memo"]);
                else writer.AppendRecord(null, "CHARLIE", "failed memo");
            });
        }
        Assert.Equal(before, File.ReadAllBytes(dbf));
        using (var writer = DbfWriter.Open(dbf)) writer.AppendRecord(null, "DELTA", "next memo");
        using var table = DbfTable.Open(dbf);
        Assert.Equal("original memo", table.GetRecord(0)!.Value["note"]);
        Assert.Equal(2, table.GetRecord(1)!.Value["id"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaintenanceFailure_ReopenPreservesConsistentDataAndTags(bool update)
    {
        using var temp = new MicroVfpTestSupport.TempDir("final_failure");
        string dbf = temp.File("t.dbf"), cdx = temp.File("t.cdx");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("k", 'C', 10)]))
        {
            writer.AppendRecord("ALPHA");
            writer.AppendRecord("BRAVO");
            writer.CreateTag(new CdxTagDefinition("keytag", "k"));
        }
        byte[] before = File.ReadAllBytes(dbf), indexBefore = File.ReadAllBytes(cdx);
        using (var writer = DbfWriter.Open(dbf))
        {
            writer.FailIndexMaintenanceForTests = true;
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (update) writer.UpdateRecord(0, ["CHARLIE"]);
                else writer.AppendRecord("CHARLIE");
            });
        }
        Assert.Equal(before, File.ReadAllBytes(dbf));
        Assert.Equal(indexBefore, File.ReadAllBytes(cdx));
        using var reopened = DbfWriter.Open(dbf);
        Assert.Equal(2, reopened.RecordCount);
        using var table = DbfTable.Open(dbf);
        using var index = CdxFile.Open(cdx, table);
        Assert.NotNull(index.Tag("keytag")!.Seek("ALPHA"));
        Assert.Null(index.Tag("keytag")!.Seek("CHARLIE"));
    }

    [Theory]
    [InlineData("append")]
    [InlineData("update")]
    [InlineData("smallbatch")]
    [InlineData("bulk")]
    public void PartialPageFailure_RestoresDbfAndAllTags(string operation)
    {
        using var temp = new MicroVfpTestSupport.TempDir("partial_cdx");
        string dbf = temp.File("t.dbf"), cdx = temp.File("t.cdx");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("k", 'C', 10)]))
        {
            for (int i = 0; i < 80; i++) writer.AppendRecord($"KEY{i:D6}");
            writer.CreateTag(new CdxTagDefinition("first", "k"));
            writer.CreateTag(new CdxTagDefinition("second", "k", unique: true));
        }
        byte[] before = File.ReadAllBytes(dbf), indexBefore = File.ReadAllBytes(cdx);
        using (var writer = DbfWriter.Open(dbf))
        {
            CdxTreeEditor.AfterPageWriteForTests = () => throw new IOException("Injected page-write failure");
            try
            {
                Assert.ThrowsAny<Exception>(() =>
                {
                    switch (operation)
                    {
                        case "append": writer.AppendRecord("CHARLIE"); break;
                        case "update": writer.UpdateRecord(0, ["CHARLIE"]); break;
                        case "smallbatch": writer.AppendRecords([new object?[] { "CHARLIE" }]); break;
                        // Bulk builds rewrite the complete file; fail after that write to prove undo.
                        default:
                            DbfWriter.AfterBulkIndexRebuildForTests = () => throw new IOException("Injected bulk failure");
                            writer.AppendRecords(Enumerable.Range(0, 80).Select(i => new object?[] { $"NEW{i:D6}" }));
                            break;
                    }
                });
            }
            finally
            {
                CdxTreeEditor.AfterPageWriteForTests = null;
                DbfWriter.AfterBulkIndexRebuildForTests = null;
            }
        }
        Assert.Equal(before, File.ReadAllBytes(dbf));
        Assert.Equal(indexBefore, File.ReadAllBytes(cdx));
        using var reopened = DbfWriter.Open(dbf);
        Assert.False(reopened.ReindexNeeded);
        reopened.AppendRecord("DELTA");
        Assert.NotNull(IndexMaintTestSupport.Seek(dbf, "first", "DELTA"));
    }

    [Fact]
    public void FailedRollback_ReopenRequiresReindex_WithoutDeletingCdx()
    {
        using var temp = new MicroVfpTestSupport.TempDir("failed_undo");
        string dbf = temp.File("t.dbf"), cdx = temp.File("t.cdx");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("k", 'C', 10)]))
        {
            writer.AppendRecord("ALPHA");
            writer.CreateTag(new CdxTagDefinition("keytag", "k"));
        }
        using (var writer = DbfWriter.Open(dbf))
        {
            CdxTreeEditor.AfterPageWriteForTests = () => throw new IOException("Injected page-write failure");
            CdxTreeEditor.BeforeRollbackForTests = () => throw new IOException("Injected undo failure");
            try { Assert.Throws<IOException>(() => writer.AppendRecord("CHARLIE")); }
            finally
            {
                CdxTreeEditor.AfterPageWriteForTests = null;
                CdxTreeEditor.BeforeRollbackForTests = null;
            }
        }
        Assert.True(File.Exists(cdx));
        Assert.Throws<DbfWriteException>(() => DbfTable.Open(dbf));
        Assert.Throws<DbfWriteException>(() => CdxFile.Open(cdx));
        using (var writer = DbfWriter.Open(dbf))
        {
            Assert.True(writer.ReindexNeeded);
            Assert.Throws<DbfWriteException>(() => writer.AppendRecord("DELTA"));
            writer.Reindex();
            Assert.False(writer.ReindexNeeded);
        }
        Assert.False(File.Exists(cdx + ".needs-reindex"));
        Assert.Equal((uint)2, IndexMaintTestSupport.Seek(dbf, "keytag", "CHARLIE"));
    }
}
