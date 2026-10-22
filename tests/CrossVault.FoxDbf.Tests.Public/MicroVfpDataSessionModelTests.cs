using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

// FileStream.Lock/Unlock are Windows byte-range locks (§D3 VFP coexistence); these tests run on Windows.
#pragma warning disable CA1416

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP finding 5.14 — the REAL multi-DATA-SESSION model (replaces the single-session SET DATASESSION
/// stub). A DATA SESSION is an isolated set of work areas PLUS its own session-scoped SET state; the host
/// API <see cref="VfpInterpreter.CreateDataSession"/> / <see cref="VfpInterpreter.ReleaseDataSession"/> is
/// microVFP's headless equivalent of a form with <c>DataSession=2</c> (a .prg cannot create one, matching
/// VFP), and <c>SET DATASESSION TO n</c> SWITCHES among existing sessions. These pin the OBSERVABLE
/// isolation entirely with INTRA-interpreter sessions (one interpreter, many data sessions) — the .NET
/// embedder's view — so no VFP9 oracle is needed here; the authoritative VFP9 golden for the same
/// isolation sequence lives in the Internal <c>MicroVfpDataSessionModelOracleTests</c>.
///
/// These were RED against the stub (no CreateDataSession API; SET("DATASESSION") a constant 1; SET
/// DATASESSION TO 2 always raised 1540; ASESSIONS always [1]). SAFETY: every case builds a FRESH synthetic
/// free table in its own throwaway temp dir; no committed fixture is mutated. Named *DataSessionModel* for
/// the fast filter.
/// </summary>
public sealed class MicroVfpDataSessionModelTests
{
    // ─────────────────────────── synthetic two-table scaffolding ───────────────────────────

    private sealed class Fixture : IDisposable
    {
        public string Dir { get; }
        public string BtPath { get; }
        public string CtPath { get; }

        public Fixture(int rows = 5)
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_dsmodel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);

            BtPath = Path.Combine(Dir, "bt.dbf");
            using (var w = DbfWriter.Create(BtPath, new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("name", 'C', 20),
                new DbfColumnDef("amount", 'N', 10, 2),
            }))
            {
                for (int i = 1; i <= rows; i++) w.AppendRecord(i, "ROW" + i, (decimal)i);
                w.CreateTag(new CdxTagDefinition("tid", "id"));   // structural .cdx ⇒ structural lock scheme.
                w.Flush();
            }

            CtPath = Path.Combine(Dir, "ct.dbf");
            using (var w = DbfWriter.Create(CtPath, new[]
            {
                new DbfColumnDef("k", 'I'),
                new DbfColumnDef("v", 'N', 10, 2),
            }))
            {
                for (int i = 1; i <= 3; i++) w.AppendRecord(i, (decimal)(i * 10));
                w.Flush();
            }
        }

        /// <summary>One interpreter over the shared directory — its default data session is #1.</summary>
        public VfpInterpreter New(out VfpSession session)
        {
            session = new VfpSession();
            session.OpenDirectory(Dir);
            return new VfpInterpreter(session);
        }

        public static long RecByte(int recNo) => 0x7FFFFFFEL - recNo;   // structural cdx record-lock byte.

        public static bool RecordLockedOnDisk(string path, int recNo)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            try { fs.Lock(RecByte(recNo), 1); fs.Unlock(RecByte(recNo), 1); return false; }
            catch (IOException) { return true; }
        }

        public decimal DiskAmount(string path, int rec0, string col)
        {
            using var t = DbfTable.Open(path, new DbfOptions { LockMode = LockMode.Shared });
            var v = t.GetRecord(rec0)?[col];
            return v is null ? 0m : Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static string Str(VfpInterpreter it, string e) => it.EvalExpression(e).AsString;
    private static decimal Num(VfpInterpreter it, string e) => it.EvalExpression(e).AsNumber;
    private static bool Bool(VfpInterpreter it, string e) => it.EvalExpression(e).AsLogical;
    private static int Int(VfpInterpreter it, string e) => it.EvalExpression(e).AsInteger;

    // ═══════════════════════════ (1) host API: create + switch + isolation ═══════════════════════════

    [Fact]
    public void CreateDataSession_SwitchesAndIsolates_WorkAreas_RecordPointers_SetState()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            // Session 1: open bt, position on record 3, SET DELETED/EXACT to a known state.
            it.Execute("USE bt");
            it.Execute("GO 3");
            it.Execute("SET DELETED ON");
            it.Execute("SET EXACT ON");
            Assert.Equal("BT", Str(it, "ALIAS()"));
            Assert.Equal(3, Int(it, "RECNO()"));

            // CreateDataSession returns the next id but does NOT switch (still on 1).
            int id = it.CreateDataSession();
            Assert.Equal(2, id);
            Assert.Equal(VfpType.Numeric, it.EvalExpression("SET('DATASESSION')").Type);
            Assert.Equal(1m, Num(it, "SET('DATASESSION')"));

            // Switch to session 2: a FRESH, EMPTY work-area set — bt is NOT open here.
            it.Execute("SET DATASESSION TO 2");
            Assert.Equal(2m, Num(it, "SET('DATASESSION')"));
            Assert.False(Bool(it, "USED('bt')"));

            // Session 2 opens bt on its OWN handle and moves to record 5; changes SET DELETED/EXACT.
            it.Execute("USE bt");
            it.Execute("GO 5");
            it.Execute("SET DELETED OFF");
            it.Execute("SET EXACT OFF");
            Assert.Equal(5, Int(it, "RECNO()"));
            // Session 2 also opens ct — a table session 1 never sees.
            it.Execute("USE ct IN 0");
            Assert.True(Bool(it, "USED('ct')"));

            // Back to session 1: bt still open on record 3; ct invisible; SET state untouched by session 2.
            it.Execute("SET DATASESSION TO 1");
            Assert.True(Bool(it, "USED('bt')"));
            Assert.False(Bool(it, "USED('ct')"));               // ct is isolated to session 2.
            Assert.Equal(3, Int(it, "RECNO()"));                // record pointer preserved across the round-trip.
            Assert.Equal("ON", Str(it, "SET('DELETED')"));      // session 2's SET DELETED OFF did not leak.
            Assert.Equal("ON", Str(it, "SET('EXACT')"));

            // And session 2 kept ITS own SET state + record pointer.
            it.Execute("SET DATASESSION TO 2");
            Assert.Equal(5, Int(it, "RECNO()"));
            Assert.Equal("OFF", Str(it, "SET('DELETED')"));
            Assert.Equal("OFF", Str(it, "SET('EXACT')"));
        }
    }

    [Fact]
    public void NewDataSession_StartsWithDefaultSetState_NotACopyOfCurrent()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            // Mutate session 1 AWAY from the microVFP baseline (DELETED ON / EXACT OFF).
            it.Execute("SET DELETED OFF");
            it.Execute("SET EXACT ON");
            Assert.Equal("OFF", Str(it, "SET('DELETED')"));

            // A newly created session starts at the DEFAULT baseline — NOT a copy of session 1's mutated state.
            int id = it.CreateDataSession();
            it.Execute("SET DATASESSION TO " + id);
            Assert.Equal("ON", Str(it, "SET('DELETED')"));      // default, not the copied OFF.
            Assert.Equal("OFF", Str(it, "SET('EXACT')"));       // default, not the copied ON.
        }
    }

    [Fact]
    public void ASessions_And_SetDatasession_TrackCreatedSessions()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            Assert.Equal(1, Int(it, "ASESSIONS(sa)"));
            Assert.Equal(1, Int(it, "sa(1)"));

            int id2 = it.CreateDataSession();
            int id3 = it.CreateDataSession();
            Assert.Equal(2, id2);
            Assert.Equal(3, id3);

            Assert.Equal(3, Int(it, "ASESSIONS(sa)"));          // {1,2,3}, ascending.
            Assert.Equal(1, Int(it, "sa(1)"));
            Assert.Equal(2, Int(it, "sa(2)"));
            Assert.Equal(3, Int(it, "sa(3)"));

            // AUSED(arr, n) lists a GIVEN session's aliases: session 2 opens bt, session 1 has none.
            it.Execute("SET DATASESSION TO 2");
            it.Execute("USE bt");
            it.Execute("SET DATASESSION TO 1");
            Assert.Equal(0, Int(it, "AUSED(u1, 1)"));           // session 1 has no open area.
            Assert.Equal(1, Int(it, "AUSED(u2, 2)"));           // session 2 has bt open.
            Assert.Equal("BT", Str(it, "u2(1,1)").ToUpperInvariant());
        }
    }

    // ═══════════════════════════ (2) lifecycle: release ═══════════════════════════

    [WindowsOnlyFact]
    public void ReleaseDataSession_ClosesAreas_ReleasesLocks_RecordLockableFromSession1Afterwards()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            it.Execute("USE bt");                               // session 1 opens bt.

            int id = it.CreateDataSession();
            it.Execute("SET DATASESSION TO 2");
            it.Execute("USE bt");                               // session 2 opens bt on its OWN handle.
            it.Execute("GO 3");
            Assert.True(Bool(it, "RLOCK()"));                   // session 2 locks record 3.
            Assert.True(Fixture.RecordLockedOnDisk(f.BtPath, 3));

            // Session 1 is DENIED record 3 while session 2 holds it — intra-interpreter lock isolation.
            it.Execute("SET DATASESSION TO 1");
            it.Execute("USE bt\nSET REPROCESS TO 1\nGO 3");
            Assert.False(Bool(it, "RLOCK()"));                  // denied.

            // Releasing session 2 closes its areas and frees its byte-range lock.
            it.ReleaseDataSession(id);
            Assert.False(Fixture.RecordLockedOnDisk(f.BtPath, 3));
            Assert.True(Bool(it, "RLOCK()"));                   // session 1 can now lock record 3.
            it.Execute("UNLOCK");
        }
    }

    [Fact]
    public void ReleaseDataSession_RemovesFromAsessions_AndSwitchingToItRaises1540()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            int id = it.CreateDataSession();
            Assert.Equal(2, Int(it, "ASESSIONS(sa)"));

            it.ReleaseDataSession(id);
            Assert.Equal(1, Int(it, "ASESSIONS(sa)"));          // gone from the list.
            Assert.Equal(1, Int(it, "sa(1)"));

            var ex = Assert.Throws<MicroVfpRuntimeException>(() => it.Execute("SET DATASESSION TO 2"));
            Assert.Contains("session number is invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReleasingCurrentDataSession_FallsBackToSession1()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            int id = it.CreateDataSession();
            it.Execute("SET DATASESSION TO 2");
            Assert.Equal(2m, Num(it, "SET('DATASESSION')"));

            it.ReleaseDataSession(id);                          // releasing the CURRENT session…
            Assert.Equal(1m, Num(it, "SET('DATASESSION')"));    // …falls back to the default session 1.
        }
    }

    [Fact]
    public void Release_Session1_OrNonexistent_Throws_AndStubFactsPreserved()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            Assert.Throws<ArgumentException>(() => it.ReleaseDataSession(1));   // session 1 is permanent.
            Assert.Throws<ArgumentException>(() => it.ReleaseDataSession(9));   // does not exist.

            // Oracle-pinned stub facts that DID NOT change (no session 2 exists here):
            Assert.Equal(1m, Num(it, "SET('DATASESSION')"));                    // numeric current id.
            it.Execute("SET DATASESSION TO 1");                                 // TO current is a no-op.
            foreach (var prg in new[] { "SET DATASESSION TO 0", "SET DATASESSION TO 2", "SET DATASESSION TO 5" })
            {
                var ex = Assert.Throws<MicroVfpRuntimeException>(() => it.Execute(prg));
                Assert.Contains("session number is invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ═══════════════════════════ (3) cross-feature isolation ═══════════════════════════

    [Fact]
    public void TableBuffering_Mode5_PendingEditsInSession2_InvisibleAndUncommittedFromSession1()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            it.Execute("USE bt");                               // session 1.
            it.Execute("GO 2");
            Assert.Equal(2.00m, Num(it, "amount"));

            int id = it.CreateDataSession();
            it.Execute("SET DATASESSION TO 2");
            it.Execute("USE bt");
            Assert.True(Bool(it, "CURSORSETPROP('Buffering', 5)"));   // optimistic TABLE buffering.
            it.Execute("GO 2\nREPLACE amount WITH 999.00");          // pending in session 2's buffer.
            Assert.Equal(999.00m, Num(it, "amount"));                // session 2 sees its own buffered edit.

            // Session 1: the buffered edit is invisible (its own handle reads disk) and uncommitted on disk.
            it.Execute("SET DATASESSION TO 1");
            it.Execute("GO 2");
            Assert.Equal(2.00m, Num(it, "amount"));
            Assert.Equal(2.00m, f.DiskAmount(f.BtPath, 1, "amount"));   // never reached disk.
        }
    }

    [Fact]
    public void PrgTransaction_InSession1_DoesNotSnapshotSession2Tables_AndTxnlevelIsIsolated()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            it.Execute("USE bt");
            it.Execute("BEGIN TRANSACTION");
            it.Execute("GO 1\nREPLACE amount WITH 111.00");     // snapshot bt inside session 1's transaction.
            Assert.Equal(1, Int(it, "TXNLEVEL()"));

            int id = it.CreateDataSession();
            it.Execute("SET DATASESSION TO 2");
            Assert.Equal(0, Int(it, "TXNLEVEL()"));             // the transaction is isolated to session 1.
            it.Execute("USE ct");
            it.Execute("GO 1\nREPLACE v WITH 77.00");           // AUTOCOMMIT in session 2 (its _txn is empty).
            Assert.Equal(77.00m, f.DiskAmount(f.CtPath, 0, "v"));   // persisted immediately.

            // Back in session 1: ROLLBACK reverts bt but never touched session 2's ct.
            it.Execute("SET DATASESSION TO 1");
            it.Execute("ROLLBACK");
            Assert.Equal(0, Int(it, "TXNLEVEL()"));
            it.Execute("GO 1");
            Assert.Equal(1.00m, Num(it, "amount"));             // bt rolled back to its pre-image (record 1 = 1.00).
            Assert.Equal(77.00m, f.DiskAmount(f.CtPath, 0, "v"));   // ct survived — not in session 1's snapshot.
        }
    }

    [Fact]
    public void FunctionExecution_OperatesOnTheActiveDataSession()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            // A UDF that reports the CURRENT area's alias — proves execution follows the ACTIVE session.
            it.Load(PrgParser.Parse("FUNCTION curalias\nRETURN ALIAS()\nENDFUNC"));

            it.Execute("USE bt");                               // session 1's current alias = BT.
            Assert.Equal("BT", Str(it, "curalias()").ToUpperInvariant());

            int id = it.CreateDataSession();
            it.Execute("SET DATASESSION TO 2");
            it.Execute("USE ct");                               // session 2's current alias = CT.
            Assert.Equal("CT", Str(it, "curalias()").ToUpperInvariant());

            it.Execute("SET DATASESSION TO 1");
            Assert.Equal("BT", Str(it, "curalias()").ToUpperInvariant());   // back to session 1's area.
        }
    }

    // ═══════════════════════════ (4) session-scoped SETs: REPROCESS / UNIQUE / MULTILOCKS (5.14 MUST-FIX) ═════
    //
    // These three SETs used to live on the single shared RuntimeState and were NOT swapped by SET DATASESSION,
    // so a private session's SET UNIQUE/MULTILOCKS/REPROCESS leaked into session 1's next INDEX build / lock &
    // retry policy. They are now saved/restored per data session (their live copies still ride RuntimeState,
    // mirroring the ACTIVE session, so RuntimeState.LockFailFast keeps computing from the active value). VFP9
    // treats all three as data-session-scoped; the isolation is oracle-pinned for UNIQUE in the Internal
    // MicroVfpDataSessionModelOracleTests.

    [Fact]
    public void SessionScopedSets_ReprocessUniqueMultilocks_IsolatedAndFreshSessionUsesDefaults()
    {
        using var f = new Fixture();
        var it = f.New(out var s);
        using (s)
        {
            // Session 1: flip all three away from their VFP defaults.
            it.Execute("SET UNIQUE ON");
            it.Execute("SET MULTILOCKS ON");
            it.Execute("SET REPROCESS TO 5");
            Assert.Equal("ON", Str(it, "SET('UNIQUE')"));
            Assert.Equal("ON", Str(it, "SET('MULTILOCKS')"));
            Assert.Equal("5", Str(it, "SET('REPROCESS')"));

            // A newly created session starts at the VFP DEFAULTS (0 / OFF / OFF) — NOT a copy of session 1's
            // mutated values.
            int id = it.CreateDataSession();
            it.Execute("SET DATASESSION TO " + id);
            Assert.Equal("OFF", Str(it, "SET('UNIQUE')"));
            Assert.Equal("OFF", Str(it, "SET('MULTILOCKS')"));
            Assert.Equal("0", Str(it, "SET('REPROCESS')"));

            // Mutate all three in session 2; the changes must NOT leak back into session 1.
            it.Execute("SET UNIQUE ON");
            it.Execute("SET MULTILOCKS ON");
            it.Execute("SET REPROCESS TO 9");

            it.Execute("SET DATASESSION TO 1");
            Assert.Equal("ON", Str(it, "SET('UNIQUE')"));       // session 1 kept its OWN value…
            Assert.Equal("ON", Str(it, "SET('MULTILOCKS')"));
            Assert.Equal("5", Str(it, "SET('REPROCESS')"));     // …not session 2's 9.

            it.Execute("SET DATASESSION TO " + id);             // and session 2 kept ITS own value too.
            Assert.Equal("9", Str(it, "SET('REPROCESS')"));
            Assert.Equal("ON", Str(it, "SET('UNIQUE')"));
        }
    }

    // ═══════════════════════════ (5) quiesce tears down NON-active sessions too (5.14 MUST-FIX) ══════════════
    //
    // An ADO.NET copy-on-write transaction Commit quiesces the WHOLE VfpSession (VfpSession.CloseAllHandles +
    // the interpreter's HandlesClosing hook) before it swaps each private copy over its live file. Before the
    // fix only the ACTIVE session was torn down, so a NON-active private session holding an open work-area
    // handle / cached DbfWriter / byte-range lock on the target .dbf blocked PromoteFile's File.Replace and the
    // Commit threw. The fix releases EVERY session's handles + locks at the quiesce boundary.

    [WindowsOnlyFact]
    public void AdoTransactionCommit_ReleasesNonActivePrivateSessionHandlesAndLocks_AreaReopensFresh()
    {
        using var f = new Fixture();
        using var conn = new FoxDbfConnection($"Data Source={f.Dir}");
        conn.Open();
        var it = conn.Interpreter;   // the connection's session-bound interpreter (shares the connection's VfpSession).

        // A PRIVATE data session opens bt and takes a byte-range lock on record 3 — holding an open work-area
        // handle, a cached DbfWriter AND a byte-range lock on the LIVE bt.dbf, all in a session that is NOT the
        // one the transaction commits from.
        int id = it.CreateDataSession();
        it.Execute("SET DATASESSION TO " + id);
        it.Execute("USE bt\nGO 3");
        Assert.True(Bool(it, "RLOCK()"));
        Assert.True(Fixture.RecordLockedOnDisk(f.BtPath, 3));

        // Back on the default session, an ADO.NET COW transaction writes bt and COMMITS.
        it.Execute("SET DATASESSION TO 1");
        var tx = conn.BeginTransaction();
        try
        {
            it.Execute("USE bt\nGO 2\nREPLACE amount WITH 321.00");   // first write ⇒ private COW copy of bt.
            tx.Commit();   // MUST succeed — the non-active private session's live handle+lock is released at quiesce.
        }
        finally { try { tx.Dispose(); } catch { } }

        Assert.Equal(321.00m, f.DiskAmount(f.BtPath, 1, "amount"));   // the write committed to the live file.
        Assert.False(Fixture.RecordLockedOnDisk(f.BtPath, 3));        // the private session's lock was released.

        // The private session's area re-opens on FRESH (committed) content on next access — not a stale handle.
        it.Execute("SET DATASESSION TO " + id);
        Assert.False(Bool(it, "ISRLOCKED(3)"));                       // lock bookkeeping cleared in that session too.
        it.Execute("USE bt\nGO 2");
        Assert.Equal(321.00m, Num(it, "amount"));
        conn.Close();
    }
}
