using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Project-review finding 5.1 — group (4): the maintenance reaches every caller that funnels through
/// <see cref="DbfWriter"/>. The SQL DML executor (UPDATE / INSERT) and buffered microVFP (REPLACE under
/// optimistic table buffering + TABLEUPDATE, which commits at TABLEUPDATE time) both end with a
/// maintained structural index. Plus the crash-safety backstop: if tag maintenance throws, the row is
/// rolled back together with the changed pages, <see cref="DbfWriter.ReindexNeeded"/> is raised on the
/// failing handle, and the exception propagates. VFP9 reopens the original state in
/// FinalReviewFailureVfp9OracleTests.
///
/// RED until write-path maintenance exists: today these writes update the row(s) but leave the tag
/// stale, and no maintenance failure can be observed (so the backstop never fires).
///
/// SAFETY: throwaway temp dir only; no committed fixture is touched.
/// </summary>
public sealed class IndexMaintDmlBufferingTests
{
    [Fact]
    public void SqlUpdate_MaintainsStructuralCdx()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = Path.Combine(dir.Path, "person.dbf");
        SqlTestSupport.CreatePersonTable(dbf);   // tags TID/TNAME/…; NAME is C(20)

        using (var s = new VfpSession())
        {
            s.OpenDirectory(dir.Path);
            var r = s.Execute("UPDATE person SET name = 'ZZZ' WHERE id = 1");   // rec 1: Smith → ZZZ
            Assert.Equal(1, r!.AffectedRecords);
        }

        Assert.Equal((uint)1, IndexMaintTestSupport.Seek(dbf, "TNAME", (object)"ZZZ"));
        Assert.Null(IndexMaintTestSupport.Seek(dbf, "TNAME", (object)"Smith"));
    }

    [Fact]
    public void SqlInsert_MaintainsStructuralCdx()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = Path.Combine(dir.Path, "person.dbf");
        SqlTestSupport.CreatePersonTable(dbf);   // 10 rows → TID has 10 entries

        using (var s = new VfpSession())
        {
            s.OpenDirectory(dir.Path);
            var r = s.Execute(
                "INSERT INTO person (id, name, city, amount, hired, active) " +
                "VALUES (11, 'Newman', 'Bonn', 555.50, {^2020-05-05}, .T.)");
            Assert.Equal(1, r!.AffectedRecords);
        }

        // The new record (recno 11) must have entered the ID tag.
        Assert.Equal(11, IndexMaintTestSupport.EntryCount(dbf, "TID"));
        Assert.Contains(11, IndexMaintTestSupport.Recnos(dbf, "TID"));
    }

    [Fact]
    public void BufferedReplace_ThenTableUpdate_MaintainsStructuralCdx_AtCommit()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("name", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[]
            {
                new object?[] { 1, "ALPHA" },
                new object?[] { 2, "BRAVO" },
                new object?[] { 3, "CHARLIE" },
            });

        using (var b = new IndexMaintTestSupport.Bench(dir.Path))
        {
            b.Run("USE t\nINDEX ON name TAG nametag\nSET ORDER TO nametag");
            b.Run("=CURSORSETPROP('Buffering', 5)");
            b.Run("GO 2\nREPLACE name WITH 'ZULU'");   // buffered — deferred
            Assert.True(b.Bool("TABLEUPDATE(.T.)"));   // commit → maintenance happens here

            b.Run("=SEEK('ZULU', 't', 'nametag')");
            Assert.True(b.Bool("FOUND()"));
            b.Run("=SEEK('BRAVO', 't', 'nametag')");
            Assert.False(b.Bool("FOUND()"));
        }

        Assert.Equal(new[] { 1, 3, 2 }, IndexMaintTestSupport.Recnos(dbf, "nametag"));
    }

    [Fact]
    public void TagMaintenanceFailure_SetsReindexNeeded_AndRethrows_RowRolledBack()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { "ALPHA" }, new object?[] { "BRAVO" } },
            new CdxTagDefinition("NAMETAG", "NAME"));

        using var w = DbfWriter.Open(dbf);
        w.FailIndexMaintenanceForTests = true;

        // The row is written FIRST; then maintenance throws → it must propagate…
        Assert.ThrowsAny<Exception>(() => w.AppendRecord("CHARLIE"));
        // …with the reindex backstop raised so the caller knows an explicit rebuild is required…
        Assert.True(w.ReindexNeeded);
        // …and the failed append has been removed, keeping the DBF consistent with the preserved tags.
        Assert.Equal(2, w.RecordCount);
    }
}
