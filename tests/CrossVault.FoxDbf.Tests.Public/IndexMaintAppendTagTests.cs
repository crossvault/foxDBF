using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Project-review finding 5.1 — group (2): APPEND is immediately visible under an active order and via
/// SEEK; EVERY open tag on a table is maintained; a FOR-filter tag moves a record INTO and OUT OF the
/// filtered set on update; a DESCENDING tag keeps its stored order after append; a CANDIDATE tag raises
/// on a duplicate key produced by a write; and Delete()/Recall() (delete-flag writes) leave the index
/// entries UNCHANGED (VFP keeps deleted entries in the CDX until PACK).
///
/// RED until write-path maintenance exists (append/update do not touch tags today) — except the
/// Delete/Recall case, which is a GREEN regression guard: it already holds (delete-flag writes do no
/// index work) and MUST keep holding after the feature lands.
///
/// SAFETY: throwaway temp dir only; no committed fixture is touched.
/// </summary>
public sealed class IndexMaintAppendTagTests
{
    [Fact]
    public void Append_UnderActiveOrder_IsImmediatelyVisible_InOrderAndSeek()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { "ALPHA" }, new object?[] { "BRAVO" }, new object?[] { "CHARLIE" } },
            new CdxTagDefinition("NAMETAG", "NAME"));

        using (var w = DbfWriter.Open(dbf))
            w.AppendRecord("DELTA");   // rec 4 — must appear in the order without a REINDEX

        Assert.Equal((uint)4, IndexMaintTestSupport.Seek(dbf, "NAMETAG", (object)"DELTA"));
        Assert.Equal(new[] { 1, 2, 3, 4 }, IndexMaintTestSupport.Recnos(dbf, "NAMETAG"));
    }

    [Fact]
    public void Update_MaintainsEveryOpenTag_OnOneTable()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("ID", 'I'), new DbfColumnDef("NAME", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[]
            {
                new object?[] { 1, "ALPHA" },
                new object?[] { 2, "BRAVO" },
                new object?[] { 3, "CHARLIE" },
            },
            new CdxTagDefinition("IDTAG", "ID"),
            new CdxTagDefinition("NAMETAG", "NAME"));

        using (var w = DbfWriter.Open(dbf))
            w.UpdateRecord(1, new object?[] { 20, "ZULU" });   // rec 2: id 2→20, name BRAVO→ZULU

        // NAMETAG maintained (ALPHA < CHARLIE < ZULU).
        Assert.Equal((uint)2, IndexMaintTestSupport.Seek(dbf, "NAMETAG", (object)"ZULU"));
        Assert.Null(IndexMaintTestSupport.Seek(dbf, "NAMETAG", (object)"BRAVO"));
        Assert.Equal(new[] { 1, 3, 2 }, IndexMaintTestSupport.Recnos(dbf, "NAMETAG"));
        // IDTAG maintained (ids now 1, 20, 3 → sorted 1, 3, 20 → recnos 1, 3, 2).
        Assert.Equal(new[] { 1, 3, 2 }, IndexMaintTestSupport.Recnos(dbf, "IDTAG"));
    }

    [Fact]
    public void Update_MovesRecord_IntoAndOutOf_ForFilterSet()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("VAL", 'N', 10, 2) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { 5m }, new object?[] { -3m } },   // rec1 IN (>0), rec2 OUT
            new CdxTagDefinition("FTAG", "VAL", forExpression: "VAL > 0"));

        Assert.Equal(new[] { 1 }, IndexMaintTestSupport.Recnos(dbf, "FTAG"));   // baseline

        using (var w = DbfWriter.Open(dbf))
        {
            w.UpdateRecord(1, new object?[] { 10m });   // rec2: -3 → 10  ⇒ INTO the filter set
            w.UpdateRecord(0, new object?[] { -1m });   // rec1:  5 → -1  ⇒ OUT of the filter set
        }

        Assert.Equal(new[] { 2 }, IndexMaintTestSupport.Recnos(dbf, "FTAG"));
    }

    [Fact]
    public void Incremental_updates_that_empty_a_tag_turn_its_root_into_an_empty_leaf_in_place()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("VAL", 'N', 10, 2) };
        var rows = Enumerable.Range(1, 200).Select(i => new object?[] { (decimal)i }).ToArray();
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "empty.dbf", cols,
            rows,
            new CdxTagDefinition("FTAG", "VAL", forExpression: "VAL > 0"));

        uint oldRootOffset;
        using (var beforeTable = DbfTable.Open(dbf))
        using (var beforeCdx = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), beforeTable))
        {
            var beforeTag = Assert.IsType<CdxTag>(beforeCdx.Tag("FTAG"));
            var beforeRoot = Assert.IsType<IndexNodeHeader>(
                beforeCdx.Index.ReadNodeHeader(beforeTag.RootPageOffset));
            oldRootOffset = beforeTag.RootPageOffset;
            Assert.True(beforeRoot.IsRoot);
            Assert.True(beforeRoot.IsBranch);
            Assert.True(beforeRoot.KeyCount > 0);
        }

        using (var w = DbfWriter.Open(dbf))
        {
            for (int i = 0; i < rows.Length; i++)
                w.UpdateRecord(i, new object?[] { -(decimal)(i + 1) });
        }

        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table);
        var tag = Assert.IsType<CdxTag>(cdx.Tag("FTAG"));
        var root = Assert.IsType<IndexNodeHeader>(cdx.Index.ReadNodeHeader(tag.RootPageOffset));

        Assert.Empty(tag.EnumerateEntries());
        Assert.Equal(oldRootOffset, tag.RootPageOffset);   // VFP9: the root never moves
        Assert.True(root.IsRoot);
        Assert.True(root.IsLeaf);
        Assert.Equal(0, root.KeyCount);
        Assert.Null(root.LeftSibling);
        Assert.Null(root.RightSibling);
    }

    [Fact]
    public void Append_UnderDescendingTag_KeepsStoredDescendingOrder()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("VAL", 'N', 10, 2) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { 3m }, new object?[] { 1m }, new object?[] { 2m } },
            new CdxTagDefinition("DTAG", "VAL", descending: true));

        using (var w = DbfWriter.Open(dbf))
            w.AppendRecord(4m);   // rec 4

        // Descending by value: 4(rec4) > 3(rec1) > 2(rec3) > 1(rec2).
        Assert.Equal(new[] { 4, 1, 3, 2 }, IndexMaintTestSupport.Recnos(dbf, "DTAG"));
    }

    [WindowsOnlyFact]
    public async Task TwoSharedWriters_ConcurrentAppends_WithStructuralTag_DoNotLoseIndexEntries()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("ID", 'I'), new DbfColumnDef("NAME", 'C', 12) };
        const int seedRows = 96;
        const int perWriter = 160;
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            Enumerable.Range(1, seedRows).Select(Row),
            new CdxTagDefinition("IDTAG", "ID"));

        using (var left = DbfWriter.Open(dbf, new DbfOptions { LockMode = LockMode.Shared }))
        using (var right = DbfWriter.Open(dbf, new DbfOptions { LockMode = LockMode.Shared }))
        using (var start = new ManualResetEventSlim())
        {
            var a = Task.Run(() => AppendRangeWithRetry(left, start, 10_000, perWriter));
            var b = Task.Run(() => AppendRangeWithRetry(right, start, 20_000, perWriter));
            start.Set();
            await Task.WhenAll(a, b);
        }

        using var table = DbfTable.Open(dbf, new DbfOptions { LockMode = LockMode.Shared });
        int expectedCount = seedRows + perWriter * 2;
        Assert.Equal(expectedCount, table.RecordCount);
        Assert.Equal(expectedCount, IndexMaintTestSupport.EntryCount(dbf, "IDTAG"));

        foreach (int id in Enumerable.Range(1, seedRows)
                     .Concat(Enumerable.Range(10_000, perWriter))
                     .Concat(Enumerable.Range(20_000, perWriter)))
        {
            uint? recNo = IndexMaintTestSupport.Seek(dbf, "IDTAG", id);
            Assert.NotNull(recNo);
            Assert.InRange((int)recNo.Value, 1, expectedCount);
            Assert.Equal(id, table.GetRecord((int)recNo.Value - 1)!.Value.GetInt32("ID"));
        }
    }

    [Fact]
    public void CandidateTag_DuplicateKeyOnInsert_Raises()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("name", 'C', 10) };
        IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { 1, "A" }, new object?[] { 2, "B" }, new object?[] { 3, "C" } });

        using var b = new IndexMaintTestSupport.Bench(dir.Path);
        b.Run("USE t\nINDEX ON id TAG c CANDIDATE");   // builds fine over the distinct ids

        // A write that introduces a duplicate key must raise the same catchable error INDEX ON
        // CANDIDATE raises — not silently corrupt the candidate tag.
        Assert.Throws<MicroVfpRuntimeException>(() =>
            b.Run("INSERT INTO t (id, name) VALUES (1, 'DUP')"));
    }

    [Fact]
    public void CandidateTag_DuplicateKeyOnReplace_Raises()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("name", 'C', 10) };
        IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { 1, "A" }, new object?[] { 2, "B" }, new object?[] { 3, "C" } });

        using var b = new IndexMaintTestSupport.Bench(dir.Path);
        b.Run("USE t\nINDEX ON id TAG c CANDIDATE");

        // A key-changing REPLACE that duplicates a candidate key must raise the same catchable error, not
        // silently leave two records sharing the key (review item 3 — ExecReplace was unguarded).
        Assert.Throws<MicroVfpRuntimeException>(() =>
            b.Run("GO 2\nREPLACE id WITH 1"));
    }

    [Fact]
    public void CandidateTag_DuplicateKeyOnBufferedAppend_TableUpdate_Raises()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("name", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { 1, "A" }, new object?[] { 2, "B" }, new object?[] { 3, "C" } });

        using var b = new IndexMaintTestSupport.Bench(dir.Path);
        b.Run("USE t\nINDEX ON id TAG c CANDIDATE");
        byte[] dbfBefore = File.ReadAllBytes(dbf);
        byte[] cdxBefore = File.ReadAllBytes(Path.ChangeExtension(dbf, ".cdx"));
        b.Run("=CURSORSETPROP('Buffering', 5)");
        b.Run("INSERT INTO t (id, name) VALUES (1, 'DUP')");   // buffered — deferred to TABLEUPDATE

        // The candidate violation must surface at COMMIT (CommitAppend), matching the write-through INSERT
        // path (review item 3 — buffered appends were unguarded). Invoked as a statement (=TABLEUPDATE(...))
        // so the raise propagates rather than being swallowed by the fail-soft expression evaluator.
        var error = Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=TABLEUPDATE(.T.)"));
        Assert.Equal(1884, error.VfpErrorNumber);
        Assert.Equal(dbfBefore, File.ReadAllBytes(dbf));
        Assert.Equal(cdxBefore, File.ReadAllBytes(Path.ChangeExtension(dbf, ".cdx")));
        Assert.Equal(0, b.Interp.SnapshotTempFileCount());

        _ = b.Bool("TABLEREVERT(.T.)"); // discard the rejected pending row before proving the area remains usable.
        b.Run("INSERT INTO t (id, name) VALUES (4, 'VALID')");
        Assert.True(b.Bool("TABLEUPDATE(.T.)"));
        Assert.NotNull(IndexMaintTestSupport.Seek(dbf, "c", 4));
    }

    [Fact]
    public void DeleteAndRecall_LeaveIndexEntriesUnchanged()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { "ALPHA" }, new object?[] { "BRAVO" }, new object?[] { "CHARLIE" } },
            new CdxTagDefinition("NAMETAG", "NAME"));

        using (var w = DbfWriter.Open(dbf))
            w.Delete(1);   // VFP keeps a deleted record's index entry until PACK
        Assert.Equal(new[] { 1, 2, 3 }, IndexMaintTestSupport.Recnos(dbf, "NAMETAG"));

        using (var w = DbfWriter.Open(dbf))
            w.Recall(1);
        Assert.Equal(new[] { 1, 2, 3 }, IndexMaintTestSupport.Recnos(dbf, "NAMETAG"));
    }

    private static object?[] Row(int id) => new object?[] { id, "N" + id.ToString("D8") };

    private static void AppendRangeWithRetry(DbfWriter writer, ManualResetEventSlim start, int firstId, int count)
    {
        start.Wait();
        for (int i = 0; i < count; i++)
        {
            int id = firstId + i;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    writer.AppendRecord(Row(id));
                    break;
                }
                catch (IOException) when (attempt < 200)
                {
                    Thread.Sleep(1);
                }
            }

            if ((i & 7) == 0)
                Thread.Yield();
        }
    }
}
