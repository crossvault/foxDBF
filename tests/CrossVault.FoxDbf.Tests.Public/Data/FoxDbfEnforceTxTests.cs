using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// PROVE/REFUTE (project-review finding 5.4): do <c>EnforceRules=on</c> writes participate in the ADO.NET
/// COPY-ON-WRITE transaction, or do they BYPASS it and hit the LIVE files (so a
/// <see cref="FoxDbfTransaction.Rollback"/> cannot undo them)?
/// <para>
/// The RAW DML path writes through <c>VfpSession.OpenWritableTarget → TxBeginWritePath</c>, which lazily
/// takes a private per-table working copy on first write and redirects the writer there (Rollback discards
/// the copy). With <c>EnforceRules=on</c> the provider routes INSERT/UPDATE/DELETE through
/// <c>FoxDbfEnforcedWriteModel</c> → the microVFP interpreter. The interpreter's UPDATE/DELETE
/// (<c>ExecReplace</c>/<c>WriteFlag</c>) and every RI-cascade child write open <c>DbfWriter.Open(SourcePath)</c>
/// DIRECTLY — never through the Tx seam — so on the first write no private copy exists and the write lands on
/// the LIVE file, escaping Rollback. (An enforced INSERT is the exception: <c>ExecInsert</c> delegates the
/// row append to <c>Session.Execute</c>, i.e. the raw DmlExecutor, which DOES go through the Tx seam.)
/// </para>
/// <para>
/// These tests assert the intended (correct) transaction semantics and are the finding's verdict:
/// (a) INSERT rollback, (b) cascade-UPDATE rollback, (c) commit, (d) no-transaction, (e) mixing a
/// seam-routed write with a bypass write under a single rollback. Where the hypothesis holds the write
/// survives Rollback and the test FAILS.
/// </para>
/// <para>
/// SAFETY: every test runs on a FRESH TEMP COPY of the committed TasTrade database (real DBC RI triggers);
/// the committed <c>Tastrade_VFPData/</c> original is read ONLY for planning, never mutated.
/// </para>
/// </summary>
public sealed class FoxDbfEnforceTxTests
{
    private static string TastradeDir => Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData");

    private static string DbcOf(string copyDir) => Path.Combine(copyDir, "tastrade.dbc");

    // A fresh key for the setup table (no RI insert trigger ⇒ the enforced INSERT delegates straight to the
    // raw DmlExecutor / Tx seam and actually lands — the clean subject for the INSERT rollback probe).
    private const string SetupKey = "ZKEY";

    private static FoxDbfConnection OpenEnforced(string copyDir)
    {
        var c = new FoxDbfConnection($"Data Source={DbcOf(copyDir)};EnforceRules=on");
        c.Open();
        return c;
    }

    private static int ExecNonQuery(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? ExecScalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    // ─────────────────────────── (a) INSERT + Rollback ⇒ the row is GONE from the live table ───────────────────────────

    [Fact]
    public void EnforceTx_Insert_Rollback_RowIsGoneFromDisk()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_ins_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        Assert.Equal(0, KeyCount(copy, "setup", "key_name", SetupKey)); // fixture precondition

        using (var conn = OpenEnforced(copy))
        {
            var tx = conn.BeginTransaction();

            Assert.Equal(1, ExecNonQuery(conn,
                $"INSERT INTO setup (key_name, value) VALUES ('{SetupKey}', '7')"));

            // read-your-writes: the pending enforced INSERT is visible inside the transaction (so the write
            // definitely happened — the post-rollback check below is therefore meaningful).
            Assert.Equal(1,
                Convert.ToInt32(ExecScalar(conn, $"SELECT COUNT(*) FROM setup WHERE key_name = '{SetupKey}'")));

            tx.Rollback();
        }

        // VERDICT: after Rollback the live setup table must be back to its pre-transaction state — the
        // inserted row GONE (and no UDF-default side effect, since setup has none, is left behind).
        Assert.Equal(0, KeyCount(copy, "setup", "key_name", SetupKey));
    }

    // ─────────────────────────── (b) cascade UPDATE + Rollback ⇒ parent AND cascaded children revert ───────────────────────────

    [Fact]
    public void EnforceTx_CascadeUpdate_Rollback_ParentAndChildrenRevert()
    {
        // category → products is a real TasTrade RI relation with a CASCADE update trigger: changing a
        // parent category_id cascades the new key to every child product.
        var (oldKey, childBefore) = ParentWithChildren("category", "category_id", "products", "category_id");
        Assert.True(childBefore > 0, "fixture precondition: the chosen category must have products");
        string newKey = FreshSameWidthKey("category", "category_id", oldKey);

        using var dir = new MicroVfpTestSupport.TempDir("enftx_updcascade_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        using (var conn = OpenEnforced(copy))
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, $"UPDATE category SET category_id = '{newKey}' WHERE category_id = '{oldKey}'");

            // read-your-writes: the cascade is visible inside the transaction.
            Assert.Equal(childBefore, ChildLiveVia(conn, "products", "category_id", newKey));

            tx.Rollback();
        }

        // VERDICT: Rollback must undo the parent key change AND the whole cascade — the old key is restored
        // on both the parent and every child, and the new key exists nowhere.
        Assert.Equal(1, KeyCount(copy, "category", "category_id", oldKey));
        Assert.Equal(0, KeyCount(copy, "category", "category_id", newKey));
        Assert.Equal(childBefore, KeyCount(copy, "products", "category_id", oldKey));
        Assert.Equal(0, KeyCount(copy, "products", "category_id", newKey));
    }

    // ─────────────────────────── (c) same ops + Commit ⇒ persisted (regression pin, passes either way) ───────────────────────────

    [Fact]
    public void EnforceTx_InsertAndCascadeUpdate_Commit_Persists()
    {
        var (oldKey, childBefore) = ParentWithChildren("category", "category_id", "products", "category_id");
        Assert.True(childBefore > 0, "fixture precondition: the chosen category must have products");
        string newKey = FreshSameWidthKey("category", "category_id", oldKey);

        using var dir = new MicroVfpTestSupport.TempDir("enftx_commit");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        using (var conn = OpenEnforced(copy))
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, $"INSERT INTO setup (key_name, value) VALUES ('{SetupKey}', '7')");
            ExecNonQuery(conn, $"UPDATE category SET category_id = '{newKey}' WHERE category_id = '{oldKey}'");
            tx.Commit();
        }

        // Commit persists both the INSERT and the cascade.
        Assert.Equal(1, KeyCount(copy, "setup", "key_name", SetupKey));
        Assert.Equal(childBefore, KeyCount(copy, "products", "category_id", newKey));
        Assert.Equal(0, KeyCount(copy, "products", "category_id", oldKey));
    }

    // ─────────────────────────── (d) enforced write with NO transaction ⇒ unchanged (regression guard) ───────────────────────────

    [Fact]
    public void EnforceTx_NoTransaction_Persists_Unchanged()
    {
        var (oldKey, childBefore) = ParentWithChildren("category", "category_id", "products", "category_id");
        Assert.True(childBefore > 0, "fixture precondition: the chosen category must have products");
        string newKey = FreshSameWidthKey("category", "category_id", oldKey);

        using var dir = new MicroVfpTestSupport.TempDir("enftx_notx");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        using (var conn = OpenEnforced(copy))
        {
            // NO BeginTransaction: autocommit enforced writes behave exactly as before.
            ExecNonQuery(conn, $"INSERT INTO setup (key_name, value) VALUES ('{SetupKey}', '7')");
            ExecNonQuery(conn, $"UPDATE category SET category_id = '{newKey}' WHERE category_id = '{oldKey}'");
        }

        Assert.Equal(1, KeyCount(copy, "setup", "key_name", SetupKey));
        Assert.Equal(childBefore, KeyCount(copy, "products", "category_id", newKey));   // cascade applied
        Assert.Equal(0, KeyCount(copy, "products", "category_id", oldKey));
    }

    // ─────────────────────────── (e) a seam-routed write + a bypass write, ONE Rollback ⇒ ALL revert ───────────────────────────

    [Fact]
    public void EnforceTx_SeamWriteAndBypassWrite_SingleRollback_UndoesBoth()
    {
        // Two writes in ONE transaction on an EnforceRules=on connection:
        //   • the setup INSERT delegates to the RAW DmlExecutor → the Tx COPY-ON-WRITE seam (isolated), and
        //   • the category→products cascade UPDATE runs through the interpreter's DIRECT DbfWriter (bypass).
        // A single Rollback must undo BOTH, atomically — no write may escape to the live files.
        var (oldKey, childBefore) = ParentWithChildren("category", "category_id", "products", "category_id");
        Assert.True(childBefore > 0, "fixture precondition: the chosen category must have products");
        string newKey = FreshSameWidthKey("category", "category_id", oldKey);

        using var dir = new MicroVfpTestSupport.TempDir("enftx_mixed_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        using (var conn = OpenEnforced(copy))
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, $"INSERT INTO setup (key_name, value) VALUES ('{SetupKey}', '7')");         // seam-routed
            ExecNonQuery(conn, $"UPDATE category SET category_id = '{newKey}' WHERE category_id = '{oldKey}'"); // bypass
            tx.Rollback();
        }

        // VERDICT: one Rollback undoes BOTH writes across both tables (and the cascade's children).
        Assert.Equal(0, KeyCount(copy, "setup", "key_name", SetupKey));                    // seam write rolled back
        Assert.Equal(childBefore, KeyCount(copy, "products", "category_id", oldKey));      // cascade rolled back
        Assert.Equal(0, KeyCount(copy, "products", "category_id", newKey));
        Assert.Equal(1, KeyCount(copy, "category", "category_id", oldKey));
    }

    // ─────────────────────────── (f) enforced DELETE + Rollback ⇒ the deletion mark is GONE from disk ───────────────────────────

    [Fact]
    public void EnforceTx_Delete_Rollback_RowIsRestoredOnDisk()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_del_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        // Seed a row in AUTOCOMMIT (a definite, trigger-less target for the transactional DELETE below).
        using (var conn = OpenEnforced(copy))
            Assert.Equal(1, ExecNonQuery(conn, $"INSERT INTO setup (key_name, value) VALUES ('{SetupKey}', '7')"));
        Assert.Equal(1, KeyCount(copy, "setup", "key_name", SetupKey));

        using (var conn = OpenEnforced(copy))
        {
            var tx = conn.BeginTransaction();
            // The enforced DELETE runs through the interpreter's ExecDelete → WriteFlag (a DIRECT DbfWriter),
            // which is the write path the finding proved bypassed the COW seam. It must now hit the private copy.
            Assert.Equal(1, ExecNonQuery(conn, $"DELETE FROM setup WHERE key_name = '{SetupKey}'"));
            tx.Rollback();
        }

        // VERDICT: Rollback discards the private copy, so the deletion mark never reached the live file — the
        // row is still live on disk.
        Assert.Equal(1, KeyCount(copy, "setup", "key_name", SetupKey));
    }

    // ─────────────────────── (g) PRG-TxnFrame ↔ COW interaction: a RESTRICT abort inside the tx composes ───────────────────────

    [Fact]
    public void EnforceTx_RestrictAbort_LeavesLiveUntouched_AndTxStillCommitsAllowedWrite()
    {
        // customer → orders is a real TasTrade RESTRICT-delete relation: __RI_DELETE_customer wraps its check
        // in the interpreter's OWN PRG BEGIN/END TRANSACTION and ROLLBACKs (riend(.F.)) when the customer still
        // has orders, returning .F. (VFP err 1539). Running that INSIDE an ADO.NET COW transaction exercises the
        // interaction of the two snapshot layers: the interpreter's PRG transaction frame must operate on the
        // copy (never the live files) and must not derail the enclosing COW transaction, which then commits an
        // unrelated ALLOWED write.
        var (custKey, ordersBefore) = ParentWithChildren("customer", "customer_id", "orders", "customer_id");
        Assert.True(ordersBefore > 0, "fixture precondition: the chosen customer must have orders");

        using var dir = new MicroVfpTestSupport.TempDir("enftx_restrict_compose");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        using (var conn = OpenEnforced(copy))
        {
            var tx = conn.BeginTransaction();

            // The RESTRICT delete must be raised (VFP err 1539) — a phantom success would mean the parent was
            // wrongly removed.
            var ex = Assert.Throws<FoxDbfException>(() =>
                ExecNonQuery(conn, $"DELETE FROM customer WHERE customer_id = '{custKey}'"));
            Assert.Equal(1539, ex.VfpErrorNumber);

            // The SAME transaction stays healthy and commits an unrelated ALLOWED write.
            Assert.Equal(1, ExecNonQuery(conn, $"INSERT INTO setup (key_name, value) VALUES ('{SetupKey}', '7')"));
            tx.Commit();
        }

        // VERDICT: the aborted RESTRICT left the live customer + its orders byte-untouched (its PRG transaction
        // rolled back within the copy, and it never even wrote — no copy escaped), while the allowed write in
        // the same transaction committed.
        Assert.Equal(1, KeyCount(copy, "customer", "customer_id", custKey));
        Assert.Equal(ordersBefore, KeyCount(copy, "orders", "customer_id", custKey));
        Assert.Equal(1, KeyCount(copy, "setup", "key_name", SetupKey));
    }

    // ═══════════════════════════ helpers (read the on-disk live tables of a copied DB) ═══════════════════════════

    private static int KeyCount(string dbDir, string table, string field, string val)
    {
        using var db = DbfDatabase.OpenFoxpro(DbcOf(dbDir));
        using var t = db.OpenTable(table);
        int live = 0;
        for (int i = 0; i < t.RecordCount; i++)
        {
            if (t.IsRecordDeleted(i)) continue;
            if (Field(t, i, field) == val) live++;
        }
        return live;
    }

    /// <summary>Count live rows with <paramref name="field"/> == <paramref name="val"/> THROUGH the open
    /// (transacting) connection — so it observes read-your-writes inside the transaction.</summary>
    private static int ChildLiveVia(DbConnection conn, string table, string field, string val)
        => Convert.ToInt32(ExecScalar(conn, $"SELECT COUNT(*) FROM {table} WHERE {field} = '{val}'"));

    // The key as a VFP WHERE sees it: TasTrade's ids are right-justified ('     1'), so the leading blanks are part of it.
    private static string Field(DbfTable t, int index, string col)
        => t.GetRecord(index)?.GetExpressionValue(col)?.ToString()?.TrimEnd() ?? string.Empty;

    /// <summary>Read-only over the COMMITTED TasTrade original: the first parent key that has live children,
    /// plus its live-child count.</summary>
    private static (string key, int liveChildren) ParentWithChildren(
        string parent, string parentKey, string child, string childKey)
    {
        using var db = DbfDatabase.OpenFoxpro(Path.Combine(TastradeDir, "tastrade.dbc"));
        using var p = db.OpenTable(parent);
        using var c = db.OpenTable(child);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < c.RecordCount; i++)
        {
            if (c.IsRecordDeleted(i)) continue;
            string k = Field(c, i, childKey);
            counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        for (int i = 0; i < p.RecordCount; i++)
        {
            if (p.IsRecordDeleted(i)) continue;
            string k = Field(p, i, parentKey);
            if (counts.TryGetValue(k, out var n) && n > 0) return (k, n);
        }
        return (string.Empty, 0);
    }

    /// <summary>A same-width key value not currently present in <paramref name="parent"/>.<paramref name="keyField"/>.</summary>
    private static string FreshSameWidthKey(string parent, string keyField, string oldKey)
    {
        using var db = DbfDatabase.OpenFoxpro(Path.Combine(TastradeDir, "tastrade.dbc"));
        using var p = db.OpenTable(parent);
        var existing = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < p.RecordCount; i++)
            if (!p.IsRecordDeleted(i)) existing.Add(Field(p, i, keyField));

        int width = Math.Max(1, oldKey.Length);
        for (char a = 'A'; a <= 'Z'; a++)
        {
            string cand = new string(a, width);
            if (!existing.Contains(cand)) return cand;
        }
        return new string('Z', width);
    }
}
