using System;
using System.Collections.Generic;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP WRITE HOT-PATH regression net (project-review 5.5). These pin the behaviours the planned
/// hot-path optimizations (cached per-area writer, record-level RI pre-images, INCREMENTAL ordered-cache
/// maintenance, compiled caches) could silently break — so they are GREEN on the CURRENT (unoptimized)
/// code and MUST stay green after the optimization. Named *WriteHotPath* so the fast filter
/// (--filter FullyQualifiedName~WriteHotPath) selects them during iteration.
///
/// The invariants pinned here:
///   • sibling visibility — a write through one handle is immediately visible through a USE..AGAIN sibling
///     (what ReopenFileAreas exists for; the incremental-cache change must preserve it);
///   • writer lifetime — after USE/close or a switch to another table the .dbf is NOT locked (a cached
///     writer must be released, exactly as the per-statement writer is disposed today);
///   • ordered navigation reflects a committed key REPLACE (incremental cdx + cache update must stay correct);
///   • snapshot/restore atomicity — a PRG BEGIN TRANSACTION rollback and an RI RESTRICT abort both restore
///     the pre-image (what the whole-file → record-level pre-image change touches);
///   • write-through correctness — a physical SCAN + REPLACE updates every row and lands at EOF.
///
/// SAFETY: every case builds a FRESH synthetic table in its own throwaway temp dir, or a FRESH TEMP COPY
/// of the public TasTrade sample; no committed fixture is mutated, no VFP9 oracle is invoked (public-safe).
/// Buffered TABLEUPDATE and EnforceRules-in-transaction are covered by the existing MicroVfpBufferingTests
/// and Data/FoxDbfEnforceTx*Tests suites (the net there is reused, not duplicated here).
/// </summary>
public sealed class MicroVfpWriteHotPathTests
{
    // ─────────────────────────── synthetic indexed-table scaffolding ───────────────────────────

    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public string DbfPath { get; }
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }

        public Bench(int rows = 50)
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_whp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbfPath = Path.Combine(Dir, "bench.dbf");

            using (var w = DbfWriter.Create(DbfPath, new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("name", 'C', 10),
                new DbfColumnDef("val", 'N', 10),
                new DbfColumnDef("city", 'C', 10),
            }))
            {
                for (int i = 1; i <= rows; i++)
                    w.AppendRecord(i, "n" + i, (decimal)i, "c" + (i % 10));   // id=i (distinct), val=i.
                w.CreateTag(new CdxTagDefinition("tid", "id"));
                w.Flush();
            }

            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public void Run(string prg) => Interp.Execute(prg);
        public decimal Num(string expr) => Interp.EvalExpression(expr).AsNumber;
        public bool Bool(string expr) => Interp.EvalExpression(expr).AsLogical;

        /// <summary>Read a field of physical record <paramref name="rec0"/> through an INDEPENDENT handle
        /// (never the live interpreter cache) so on-disk state is verified directly.</summary>
        public decimal DiskNum(int rec0, string col)
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            var v = t.GetRecord(rec0)?[col];
            return v is null ? 0m : Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public int DiskRecordCount()
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            return t.RecordCount;
        }

        /// <summary>True when the .dbf can be opened with FileShare.None — i.e. NO handle (read view OR a
        /// cached writer) lingers on it. Fails if any interpreter-held handle is still open.</summary>
        public bool DbfIsUnlocked() => IsUnlocked(DbfPath);

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static bool IsUnlocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
    }

    // ─────────────────────────── (1) write-through correctness ───────────────────────────

    [Fact]
    public void PhysicalScanReplace_UpdatesEveryRow_AndLandsAtEof()
    {
        using var b = new Bench(rows: 40);
        // Physical SCAN (no controlling order) + REPLACE — the runnable write-through path the cached-writer
        // change must keep byte-correct. Every row's val must become id + 100, and the pointer ends at EOF.
        b.Run("USE bench\nGO TOP\nSCAN\nREPLACE val WITH val + 100\nENDSCAN");
        Assert.True(b.Bool("EOF()"));
        for (int rec0 = 0; rec0 < 40; rec0++)
            Assert.Equal((rec0 + 1) + 100m, b.DiskNum(rec0, "val"));   // val started = id = rec0+1.
    }

    // ─────────────────────────── (2) sibling visibility (USE..AGAIN) ───────────────────────────

    [Fact]
    public void SiblingHandle_SeesWriteImmediately_ThroughUseAgain()
    {
        using var b = new Bench(rows: 10);
        // VFP shares one buffer across USE..AGAIN handles: a REPLACE through one handle must be visible
        // through the sibling IMMEDIATELY. This is exactly what ReopenFileAreas guarantees today and the
        // incremental ordered-cache maintenance must preserve (it must still refresh every sibling handle).
        b.Run("USE bench\nUSE bench AGAIN IN 0 ALIAS bench2");
        b.Run("SELECT bench\nGO 1\nREPLACE val WITH 777");
        b.Run("SELECT bench2\nGO 1");
        Assert.Equal(777m, b.Num("val"));           // the sibling handle sees the just-written value.
    }

    // ─────────────────────────── (3)+(4) writer lifetime — release on close / switch ───────────────────────────

    [Fact]
    public void WriterReleasedAfterReplaceThenClose_SecondExclusiveOpenSucceeds()
    {
        using var b = new Bench(rows: 10);
        b.Run("USE bench\nGO 1\nREPLACE val WITH 555");
        Assert.Equal(555m, b.DiskNum(0, "val"));    // write persisted.
        b.Run("USE");                                // close the work area.
        // After close NOTHING may hold the .dbf — a cached writer (post-optimization) MUST be disposed here.
        Assert.True(b.DbfIsUnlocked(), "bench.dbf is still locked after USE (a writer handle lingered)");
    }

    [Fact]
    public void WriterReleasedOnSwitchToOtherTable_PriorFileUnlocked()
    {
        using var b = new Bench(rows: 10);
        // A second table in the same dir; switching the current area to it must release the first table's
        // handles (incl. any cached writer keyed on the area).
        string otherPath = Path.Combine(b.Dir, "other.dbf");
        using (var w = DbfWriter.Create(otherPath, new[] { new DbfColumnDef("k", 'I'), new DbfColumnDef("v", 'N', 6) }))
        { w.AppendRecord(1, 1m); w.Flush(); }

        b.Run("USE bench\nGO 1\nREPLACE val WITH 222");
        b.Run("USE other");                          // repurpose the current area onto another table.
        Assert.True(IsUnlocked(b.DbfPath), "bench.dbf stayed locked after switching the area to another table");
    }

    // ─────────────────────────── (5) ordered navigation after a committed key REPLACE ───────────────────────────

    [Fact]
    public void OrderedSeekReflectsCommittedKeyReplace()
    {
        using var b = new Bench(rows: 20);
        b.Run("USE bench\nSET ORDER TO tid");
        // Change record 5's KEY (id 5 → 99999). The incremental cdx maintenance + the ordered-cache update
        // must leave the tag seekable at the NEW key and NOT at the old one.
        b.Run("GO 5\nREPLACE id WITH 99999");
        b.Run("=SEEK(99999, 'bench', 'tid')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(5m, b.Num("RECNO()"));          // the moved key still lives on physical record 5.
        b.Run("=SEEK(5, 'bench', 'tid')");
        Assert.False(b.Bool("FOUND()"));             // the old key is gone from the tag.
    }

    // ─────────────────────────── (6) PRG transaction rollback restores the pre-image ───────────────────────────

    [Fact]
    public void PrgTransactionRollback_RestoresReplacedRow()
    {
        using var b = new Bench(rows: 10);
        b.Run("USE bench\nGO 1");
        decimal orig = b.Num("val");                 // = 1
        b.Run("BEGIN TRANSACTION\nREPLACE val WITH 424242\nROLLBACK");
        b.Run("GO 1");
        Assert.Equal(orig, b.Num("val"));            // live pointer reflects the restored value.
        Assert.Equal(orig, b.DiskNum(0, "val"));     // and the on-disk record is restored.
    }

    [Fact]
    public void PrgTransactionRollback_PlainInsert_RestoresDbfAndCdx()
    {
        using var b = new Bench(rows: 1);
        b.Run("USE bench\nSET ORDER TO tid");

        b.Run("BEGIN TRANSACTION\nINSERT INTO bench (id, name, val, city) VALUES (99, 'new', 99, 'x')\nROLLBACK");

        Assert.Equal(1m, b.Num("RECCOUNT()"));
        Assert.Equal(1, b.DiskRecordCount());
        b.Run("=SEEK(99, 'bench', 'tid')");
        Assert.False(b.Bool("FOUND()"));
        Assert.Null(IndexMaintTestSupport.Seek(b.DbfPath, "tid", 99));
        Assert.Equal((uint)1, IndexMaintTestSupport.Seek(b.DbfPath, "tid", 1));
    }

    [Fact]
    public void PrgTransactionRollback_RiEnabledWithoutTriggerInsert_RestoresDbfAndCdx()
    {
        using var b = new Bench(rows: 1);
        b.Interp.EnforceReferentialIntegrity = true;
        b.Run("USE bench\nSET ORDER TO tid");

        b.Run("BEGIN TRANSACTION\nINSERT INTO bench (id, name, val, city) VALUES (99, 'new', 99, 'x')\nROLLBACK");

        Assert.Equal(1m, b.Num("RECCOUNT()"));
        Assert.Equal(1, b.DiskRecordCount());
        b.Run("=SEEK(99, 'bench', 'tid')");
        Assert.False(b.Bool("FOUND()"));
        Assert.Null(IndexMaintTestSupport.Seek(b.DbfPath, "tid", 99));
        Assert.Equal((uint)1, IndexMaintTestSupport.Seek(b.DbfPath, "tid", 1));
    }

    [Fact]
    public void PrgTransactionRollback_SuccessfulCandidateInsert_RestoresDbfAndCdx()
    {
        using var b = new Bench(rows: 1);
        b.Run("USE bench\nINDEX ON id TAG candtag CANDIDATE\nSET ORDER TO candtag");

        b.Run("BEGIN TRANSACTION\nINSERT INTO bench (id, name, val, city) VALUES (99, 'new', 99, 'x')\nROLLBACK");

        Assert.Equal(1m, b.Num("RECCOUNT()"));
        Assert.Equal(1, b.DiskRecordCount());
        b.Run("=SEEK(99, 'bench', 'candtag')");
        Assert.False(b.Bool("FOUND()"));
        Assert.Null(IndexMaintTestSupport.Seek(b.DbfPath, "candtag", 99));
        Assert.Equal((uint)1, IndexMaintTestSupport.Seek(b.DbfPath, "candtag", 1));
    }

    [Fact]
    public void PrgTransactionRollback_ReplaceThenInsert_PreservesEarliestPreimage()
    {
        using var b = new Bench(rows: 1);
        b.Run("USE bench\nSET ORDER TO tid\nGO 1");
        decimal original = b.Num("val");

        b.Run("BEGIN TRANSACTION\nREPLACE val WITH 777\nINSERT INTO bench (id, name, val, city) VALUES (99, 'new', 99, 'x')\nROLLBACK");

        b.Run("GO 1");
        Assert.Equal(original, b.Num("val"));
        Assert.Equal(original, b.DiskNum(0, "val"));
        Assert.Equal(1m, b.Num("RECCOUNT()"));
        Assert.Equal(1, b.DiskRecordCount());
        Assert.Null(IndexMaintTestSupport.Seek(b.DbfPath, "tid", 99));
        Assert.Equal((uint)1, IndexMaintTestSupport.Seek(b.DbfPath, "tid", 1));
    }

    // ─────────────────────────── (7) RI RESTRICT abort is atomic (record-level pre-image) ───────────────────────────

    [Fact]
    public void RiRestrictDelete_IsBlocked_ParentAndChildrenStayIntact()
    {
        var (recno, custId, orderCount) = FindCustomerWithOrders();
        Assert.True(orderCount > 0, "fixture precondition: a customer with live orders must exist");

        using var dir = new MicroVfpTestSupport.TempDir("whp_ri_restrict");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            interp.EnforceReferentialIntegrity = true;
            // Deleting a customer that still has orders is a RESTRICT — the auto-fired __RI_DELETE_customer
            // returns .F. and the parent DELETE must be ROLLED BACK (parent stays live, children untouched).
            Assert.Equal(1539, Assert.Throws<MicroVfpRuntimeException>(() =>
                interp.Execute($"USE tastrade!customer IN 0\nSELECT customer\nGO {recno}\nDELETE")).VfpErrorNumber);
        }
        string tempDbc = Path.Combine(dir.Path, "tastrade.dbc");
        Assert.False(CustomerDeleted(tempDbc, recno),
            "a RESTRICTed customer DELETE must leave the parent live");
        Assert.Equal(orderCount, LiveOrdersFor(tempDbc, custId));
    }

    // ── TasTrade planning helpers — read through the DBC (DbfDatabase resolves LONG field names; a raw
    //    DbfTable.Open would see the truncated 10-char physical name CUSTOMER_I instead of customer_id). ──

    private static string OriginalDbc => Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData", "tastrade.dbc");

    private static (int Recno, string CustId, int OrderCount) FindCustomerWithOrders()
    {
        using var db = DbfDatabase.OpenFoxpro(OriginalDbc);
        var withOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var o = db.OpenTable("orders"))
            for (int i = 0; i < o.RecordCount; i++)
                if (!o.IsRecordDeleted(i) && o.GetRecord(i) is { } r)
                    withOrders.Add(r["customer_id"]?.ToString()?.Trim() ?? string.Empty);

        using var c = db.OpenTable("customer");
        for (int i = 0; i < c.RecordCount; i++)
        {
            if (c.IsRecordDeleted(i) || c.GetRecord(i) is not { } r) continue;
            string id = r["customer_id"]?.ToString()?.Trim() ?? string.Empty;
            if (id.Length > 0 && withOrders.Contains(id))
                return (i + 1, id, LiveOrdersFor(OriginalDbc, id));
        }
        return (0, string.Empty, 0);
    }

    private static int LiveOrdersFor(string dbcPath, string custId)
    {
        int n = 0;
        using var db = DbfDatabase.OpenFoxpro(dbcPath);
        using var o = db.OpenTable("orders");
        for (int i = 0; i < o.RecordCount; i++)
            if (!o.IsRecordDeleted(i) && o.GetRecord(i) is { } r
                && string.Equals(r["customer_id"]?.ToString()?.Trim(), custId, StringComparison.OrdinalIgnoreCase))
                n++;
        return n;
    }

    private static bool CustomerDeleted(string dbcPath, int recno)
    {
        using var db = DbfDatabase.OpenFoxpro(dbcPath);
        using var c = db.OpenTable("customer");
        return c.IsRecordDeleted(recno - 1);
    }

    // ─────────────────────────── (8) documents the ordered-SCAN+REPLACE bug the fix must resolve ───────────────────────────

    // GREEN since 5.5 incremental ordered-cache maintenance: a non-key REPLACE inside an ordered SCAN no
    // longer drops the Ordered cache / OrderPos, so the SCAN's SKIP keeps walking the tag sequence and the
    // loop terminates (pre-5.5 it re-topped after every REPLACE → infinite loop; see WriteHotPathBench 'probe').
    [Fact]
    public void OrderedScanReplace_TerminatesAndUpdatesEveryRow()
    {
        using var b = new Bench(rows: 30);
        b.Run("USE bench\nSET ORDER TO tid\nGO TOP\nSCAN\nREPLACE val WITH val + 100\nENDSCAN");
        Assert.True(b.Bool("EOF()"));
        for (int rec0 = 0; rec0 < 30; rec0++)
            Assert.Equal((rec0 + 1) + 100m, b.DiskNum(rec0, "val"));
    }

    // ─────────────────────────── (9) MUST-FIX #1: aborted statement reverts FPT-backed content ───────────────────────────

    // 5.5 record-level RI/candidate pre-image MUST revert a MEMO field, not just the key. A REPLACE that
    // changes a CANDIDATE key to a duplicate AND rewrites a memo field is rolled back by the record-level
    // pre-image; the memo must revert to its old content. Pre-fix the pre-image stored DbfWriter.KeepValue
    // for M/G/P/W fields, so the restore kept the NEW memo block → a partial write survived an aborted
    // statement (a data-integrity regression vs the pre-5.5 whole-file snapshot). This pins the fix.
    [Fact]
    public void AbortedCandidateReplace_RevertsMemoContent_NotJustTheKey()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_whp_memo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbf = Path.Combine(dir, "cand.dbf");
        try
        {
            using (var w = DbfWriter.Create(dbf, new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("notes", 'M'),
            }))
            {
                w.AppendRecord(1, "note one");     // rec 1
                w.AppendRecord(2, "note two");     // rec 2 — the one we abort a REPLACE on.
                w.Flush();
            }

            using var s = new VfpSession();
            s.OpenDirectory(dir);
            var it = new VfpInterpreter(s);
            it.Execute("USE cand\nINDEX ON id TAG tid CANDIDATE");   // registers the candidate tag this session.

            // REPLACE rec 2's key to a DUPLICATE (1) AND its memo — a candidate violation ⇒ VFP err 1884.
            // The record-level pre-image must roll BOTH back: key → 2, notes → "note two".
            Assert.Throws<MicroVfpRuntimeException>(() =>
                it.Execute("GO 2\nREPLACE id WITH 1, notes WITH 'CHANGED MEMO'"));

            using var t = DbfTable.Open(dbf, new DbfOptions { LockMode = LockMode.Shared });
            var rec = t.GetRecord(1);
            Assert.Equal(2, Convert.ToInt32(rec?["id"], System.Globalization.CultureInfo.InvariantCulture)); // key reverted.
            Assert.Equal("note two", (rec?["notes"] as string)?.TrimEnd());                                   // MEMO reverted.
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void AbortedCandidateReplace_WithGeneralAndPicture_RestoresWholeFilesAndCleansSnapshot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_whp_gp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbf = Path.Combine(dir, "candgp.dbf");
        string fpt = Path.ChangeExtension(dbf, ".fpt");
        string cdx = Path.ChangeExtension(dbf, ".cdx");
        try
        {
            using (var writer = DbfWriter.Create(dbf,
            [
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("gen", 'G'),
                new DbfColumnDef("pic", 'P'),
            ]))
            {
                writer.AppendRecord(1, "general one", "picture one");
                writer.AppendRecord(2, "general two", "picture two");
            }

            using var session = new VfpSession();
            session.OpenDirectory(dir);
            using var interpreter = new VfpInterpreter(session);
            interpreter.Execute("USE candgp\nINDEX ON id TAG cid CANDIDATE");
            byte[] dbfBefore = File.ReadAllBytes(dbf);
            byte[] fptBefore = File.ReadAllBytes(fpt);
            byte[] cdxBefore = File.ReadAllBytes(cdx);

            var error = Assert.Throws<MicroVfpRuntimeException>(() => interpreter.Execute(
                "GO 2\nREPLACE id WITH 1, gen WITH 'changed general', pic WITH 'changed picture'"));

            Assert.Equal(1884, error.VfpErrorNumber);
            Assert.Equal(dbfBefore, File.ReadAllBytes(dbf));
            Assert.Equal(fptBefore, File.ReadAllBytes(fpt));
            Assert.Equal(cdxBefore, File.ReadAllBytes(cdx));
            Assert.Equal(0, interpreter.SnapshotTempFileCount());
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ─────────────────────────── (10) MUST-FIX #2: buffered-commit SCAN+REPLACE is correct ───────────────────────────

    // ROW buffering (mode 3): a SCAN+REPLACE auto-commits each row on the ENDSCAN pointer move (through
    // CommitExistingRow — the path the must-fix routes onto the record-level pre-image + in-place refresh
    // fast lane). Every row must land on disk and the loop must land at EOF, exactly like the autocommit
    // SCAN. Pins that the hot-path re-route did not drop or corrupt a buffered commit.
    [Fact]
    public void BufferedRowScanReplace_UpdatesEveryRow_UnderBuffering3()
    {
        using var b = new Bench(rows: 40);
        b.Run("USE bench\n=CURSORSETPROP('Buffering', 3)\nGO TOP\nSCAN\nREPLACE val WITH val + 100\nENDSCAN");
        Assert.True(b.Bool("EOF()"));
        for (int rec0 = 0; rec0 < 40; rec0++)
            Assert.Equal((rec0 + 1) + 100m, b.DiskNum(rec0, "val"));   // val started = id = rec0+1.
    }

    // TABLE buffering (mode 5): a SCAN buffers a REPLACE on every row, then one TABLEUPDATE(.T.) commits the
    // whole batch atomically (each row through CommitExistingRow inside the batch transaction frame). Every
    // row must land on disk; TABLEUPDATE returns .T.
    [Fact]
    public void BufferedTableUpdate_CommitsEveryRow_UnderBuffering5()
    {
        using var b = new Bench(rows: 40);
        b.Run("USE bench\n=CURSORSETPROP('Buffering', 5)\nGO TOP\nSCAN\nREPLACE val WITH val + 100\nENDSCAN");
        Assert.True(b.Bool("TABLEUPDATE(.T.)"));
        for (int rec0 = 0; rec0 < 40; rec0++)
            Assert.Equal((rec0 + 1) + 100m, b.DiskNum(rec0, "val"));
    }
}
