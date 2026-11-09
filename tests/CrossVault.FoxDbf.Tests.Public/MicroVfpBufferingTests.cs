using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P1 gap #4 — REAL table/row BUFFERING (CURSORSETPROP/CURSORGETPROP "Buffering", TABLEUPDATE,
/// TABLEREVERT, OLDVAL/CURVAL/GETFLDSTATE consistent with the buffer). Written TESTS-FIRST: they pin the
/// desired contract and are RED until the interpreter grows a per-work-area buffer (today CURSORGETPROP
/// is a constant 1, CURSORSETPROP/TABLEUPDATE/TABLEREVERT are stubs, and REPLACE/DELETE/INSERT write
/// straight through to disk). The Buffering=1 default REGRESSION guard (test 4) is GREEN now and must
/// stay green — it proves the write-through path is byte-for-byte unchanged when buffering is off.
///
/// SAFETY: every case builds a FRESH synthetic free table in its own throwaway temp dir (never a
/// committed fixture); the dir is removed on dispose. All assertions run on synthetic data, so this
/// suite is public-safe (no customer data, no oracle, no Internal-only fixtures) — the VFP9-oracle
/// golden for the identical buffered sequence lives in the Internal project instead.
/// </summary>
public sealed class MicroVfpBufferingTests
{
    // ─────────────────────────── synthetic-table scaffolding ───────────────────────────

    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public string DbfPath { get; }
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_buf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbfPath = Path.Combine(Dir, "people.dbf");

            var cols = new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("name", 'C', 10),
                new DbfColumnDef("city", 'C', 10),
            };
            using (var w = DbfWriter.Create(DbfPath, cols))
            {
                w.AppendRecord(30, "Charlie", "berlin");   // rec 1
                w.AppendRecord(10, "alice",   "berlin");   // rec 2
                w.AppendRecord(20, "Bob",     "aachen");   // rec 3
                w.Flush();
            }

            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public void Run(string prg) => Interp.Execute(prg);
        public decimal Num(string expr) => Interp.EvalExpression(expr).AsNumber;
        public string Str(string expr) => Interp.EvalExpression(expr).AsString;
        public bool Bool(string expr) => Interp.EvalExpression(expr).AsLogical;

        // ── on-DISK inspection via an INDEPENDENT second handle (never the live interpreter cache) ──

        public string DiskField(int rec0, string col)
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            return t.GetRecord(rec0)?[col]?.ToString()?.TrimEnd() ?? string.Empty;
        }

        public int DiskCount()
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            return t.RecordCount;
        }

        public bool DiskDeleted(int rec0)
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            return t.IsRecordDeleted(rec0);
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── (1) TABLE buffering (mode 5) — commit / revert ───────────────────────────

    [Fact]
    public void TableBuffering_Mode5_DefersReplaceDeleteAppend_UntilTableUpdate()
    {
        using var b = new Bench();
        b.Run("USE people");

        // Default is 1 (no buffering); CURSORSETPROP switches this area to optimistic table buffering (5).
        Assert.Equal(1m, b.Num("CURSORGETPROP('Buffering')"));
        Assert.True(b.Bool("CURSORSETPROP('Buffering', 5)"));
        Assert.Equal(5m, b.Num("CURSORGETPROP('Buffering')"));

        // A REPLACE, an appended row, and a DELETE — all buffered, NONE visible on disk yet.
        b.Run("GO 1\nREPLACE name WITH 'ZZZ'");
        b.Run("INSERT INTO people (id, name, city) VALUES (99, 'New', 'nowhere')");
        b.Run("GO 2\nDELETE");

        Assert.Equal("Charlie", b.DiskField(0, "name"));   // rec 1 edit not flushed
        Assert.Equal(3, b.DiskCount());                    // append not flushed
        Assert.False(b.DiskDeleted(1));                    // delete not flushed

        // TABLEUPDATE(.T.) commits every buffered row atomically.
        Assert.True(b.Bool("TABLEUPDATE(.T.)"));

        Assert.Equal("ZZZ", b.DiskField(0, "name"));
        Assert.Equal(4, b.DiskCount());
        Assert.True(b.DiskDeleted(1));
    }

    [Fact]
    public void TableBuffering_Mode5_TableRevert_DiscardsBufferedChanges()
    {
        using var b = new Bench();
        b.Run("USE people\n=CURSORSETPROP('Buffering', 5)");

        b.Run("GO 1\nREPLACE name WITH 'ZZZ'");
        b.Run("INSERT INTO people (id, name, city) VALUES (99, 'New', 'nowhere')");

        // TABLEREVERT(.T.) drops the buffered edit and the buffered append; disk stays pristine.
        Assert.True(b.Num("TABLEREVERT(.T.)") >= 1m);      // returns the number of rows reverted

        Assert.Equal("Charlie", b.DiskField(0, "name"));
        Assert.Equal(3, b.DiskCount());
        // The live cursor no longer shows the reverted edit either.
        b.Run("GO 1");
        Assert.Equal("Charlie", b.Str("ALLTRIM(name)"));
    }

    // ─────────────────────────── (2) ROW buffering (mode 3) — implicit commit on move ───────────────────────────

    [Fact]
    public void RowBuffering_Mode3_ImplicitCommitOnPointerMove_RevertBeforeMoveDiscards()
    {
        using var b = new Bench();
        b.Run("USE people\n=CURSORSETPROP('Buffering', 3)");

        // Edit rec 1: buffered, not on disk while the pointer stays put.
        b.Run("GO 1\nREPLACE name WITH 'AAA'");
        Assert.Equal("Charlie", b.DiskField(0, "name"));

        // Moving the pointer implicitly commits the pending row.
        b.Run("GO 2");
        Assert.Equal("AAA", b.DiskField(0, "name"));

        // Edit rec 2 then TABLEREVERT() BEFORE moving discards just that row's edit.
        b.Run("REPLACE name WITH 'BBB'");
        Assert.Equal("alice", b.DiskField(1, "name"));     // still buffered
        b.Run("=TABLEREVERT()");
        b.Run("GO 3");                                      // move: nothing to commit now
        Assert.Equal("alice", b.DiskField(1, "name"));     // the reverted edit never reached disk
    }

    // ─────────────────────────── (3) OLDVAL / CURVAL / GETFLDSTATE consistent with the buffer ───────────────────────────

    [Fact]
    public void Buffer_OldValCurValFldState_ReflectPendingEdits()
    {
        using var b = new Bench();
        b.Run("USE people\n=CURSORSETPROP('Buffering', 5)");

        b.Run("GO 1\nREPLACE name WITH 'XXX'");

        // OLDVAL = value at the last commit/arrival (pre-edit); CURVAL = the ON-DISK value; both 'Charlie'
        // while the edit is buffered. GETFLDSTATE for the changed field = 2 (changed).
        Assert.Equal("Charlie", b.Str("ALLTRIM(OLDVAL('name'))"));
        Assert.Equal("Charlie", b.Str("ALLTRIM(CURVAL('name'))"));
        Assert.Equal("XXX", b.Str("ALLTRIM(name)"));       // the live buffered value
        Assert.Equal(2m, b.Num("GETFLDSTATE(2)"));         // field 2 = name → changed(2)

        // A buffered append reports the record as appended (3) or appended+changed (4).
        b.Run("INSERT INTO people (id, name, city) VALUES (99, 'New', 'nowhere')");
        b.Run("GO 4");
        Assert.True(b.Num("GETFLDSTATE(2)") >= 3m);        // appended row

        Assert.True(b.Bool("TABLEUPDATE(.T.)"));           // commit succeeds
        Assert.Equal(1m, b.Num("GETFLDSTATE(2)"));         // after commit: unchanged(1)
    }

    // ─────────────────────────── (4) Buffering = 1 (default) REGRESSION — write straight through ───────────────────────────

    [Fact]
    public void DefaultBuffering1_WritesStraightToDisk_AsToday()
    {
        using var b = new Bench();
        b.Run("USE people");                               // no CURSORSETPROP → mode 1 (write-through)

        Assert.Equal(1m, b.Num("CURSORGETPROP('Buffering')"));

        b.Run("GO 1\nREPLACE name WITH 'DEF'");
        Assert.Equal("DEF", b.DiskField(0, "name"));       // visible IMMEDIATELY (no buffer)

        b.Run("GO 2\nDELETE");
        Assert.True(b.DiskDeleted(1));                      // deleted IMMEDIATELY

        b.Run("INSERT INTO people (id, name, city) VALUES (99, 'New', 'nowhere')");
        Assert.Equal(4, b.DiskCount());                    // appended IMMEDIATELY
    }

    // ─────────────────────────── (5) ROW buffering (mode 3) buffers APPENDs too ───────────────────────────

    [Fact]
    public void RowBuffering_Mode3_AppendIsBuffered_RevertBeforeMoveDiscards()
    {
        using var b = new Bench();
        b.Run("USE people\n=CURSORSETPROP('Buffering', 3)");

        // Under ROW buffering an INSERT is buffered too (not just under table buffering): the on-disk count
        // is unchanged and the pending append is visible only on the live cursor.
        b.Run("INSERT INTO people (id, name, city) VALUES (99, 'New', 'nowhere')");
        Assert.Equal(3, b.DiskCount());                    // append NOT flushed

        Assert.Equal(4m, b.Num("RECNO()"));                 // INSERT already positions here; GO would commit the row.
        Assert.Equal(99m, b.Num("id"));
        Assert.True(b.Num("GETFLDSTATE(1)") >= 3m);         // appended (3) / appended+changed (4)

        // TABLEREVERT() on the pending append (before moving off it) discards the row entirely.
        b.Run("=TABLEREVERT()");
        b.Run("GO 1");
        Assert.Equal(3, b.DiskCount());                     // the reverted append never reached disk
    }

    [Fact]
    public void RowBuffering_Mode3_Append_ImplicitCommitOnPointerMove()
    {
        using var b = new Bench();
        b.Run("USE people\n=CURSORSETPROP('Buffering', 3)");

        b.Run("INSERT INTO people (id, name, city) VALUES (99, 'New', 'nowhere')");
        Assert.Equal(4m, b.Num("RECNO()"));                 // VFP9 row buffering uses a positive record number.
        Assert.Equal(3, b.DiskCount());                    // still buffered

        b.Run("GO 1");                                      // MOVE off it → implicit commit of the append
        Assert.Equal(4, b.DiskCount());
        Assert.Equal("New", b.DiskField(3, "name"));
    }

    // ─────────────────────────── (6) switching Buffering with pending edits is rejected ───────────────────────────

    [Fact]
    public void SwitchingBuffering_WithPendingEdits_IsRejected_ModeAndBufferUntouched()
    {
        using var b = new Bench();
        b.Run("USE people\n=CURSORSETPROP('Buffering', 5)");
        b.Run("GO 1\nREPLACE name WITH 'ZZZ'");            // pending buffered edit

        // VFP9 refuses a mode change while edits are pending: CURSORSETPROP returns .F., the mode stays 5,
        // and the buffered edit is untouched (NOT force-committed to disk).
        Assert.False(b.Bool("CURSORSETPROP('Buffering', 1)"));
        Assert.Equal(5m, b.Num("CURSORGETPROP('Buffering')"));
        Assert.Equal("ZZZ", b.Str("ALLTRIM(name)"));       // still buffered/live
        Assert.Equal("Charlie", b.DiskField(0, "name"));   // NOT flushed to disk

        // Committing first, THEN switching to 1, is allowed.
        Assert.True(b.Bool("TABLEUPDATE(.T.)"));
        Assert.True(b.Bool("CURSORSETPROP('Buffering', 1)"));
        Assert.Equal(1m, b.Num("CURSORGETPROP('Buffering')"));
    }

    // ─────────────────────────── (7) unset numeric on a buffered append reads as 0, not .NULL. ───────────────────────────

    [Fact]
    public void BufferedAppend_UnsetNumericField_ReadsAsTypedBlank_NotNull()
    {
        using var b = new Bench();
        b.Run("USE people\n=CURSORSETPROP('Buffering', 5)");

        // INSERT omits the numeric id column; the live pre-commit view must show its typed blank (0), not
        // .NULL. — matching what DbfWriter writes for an omitted column.
        b.Run("INSERT INTO people (name, city) VALUES ('New', 'nowhere')");
        b.Run("GO 4");
        Assert.Equal(0m, b.Num("id"));
        Assert.False(b.Bool("ISNULL(id)"));

        // After commit the on-disk row agrees (blank id = 0).
        Assert.True(b.Bool("TABLEUPDATE(.T.)"));
        Assert.Equal("0", b.DiskField(3, "id"));
    }

    // ─────────────────────────── (8) TABLEUPDATE/TABLEREVERT on a never-buffered area errors ───────────────────────────

    [Fact]
    public void TableUpdateRevert_OnUnbufferedArea_RaisesError_ButNoOpWhenBufferedIdle()
    {
        using var b = new Bench();
        b.Run("USE people");                               // mode 1, never buffered

        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=TABLEUPDATE(.T.)"));
        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=TABLEREVERT(.T.)"));

        // But once buffering is enabled, an idle TABLEUPDATE/TABLEREVERT (nothing pending) is a no-op.
        b.Run("=CURSORSETPROP('Buffering', 5)");
        Assert.True(b.Bool("TABLEUPDATE(.T.)"));
        Assert.Equal(0m, b.Num("TABLEREVERT(.T.)"));
    }
}
