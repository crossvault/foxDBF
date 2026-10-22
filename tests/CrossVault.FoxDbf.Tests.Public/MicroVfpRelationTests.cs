using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P1 gap #2 — the MULTI-TABLE RELATION model (SET RELATION / SET RELATION OFF / SET SKIP and
/// the RELATION() / TARGET() / SET("RELATION") / SET("SKIP") introspection). Written over FRESH synthetic
/// parent→child→grandchild tables built in a throwaway temp dir (never a committed fixture); each test
/// builds its own tables and disposes them on Dispose.
///
/// SAFETY: synthetic data only — public-safe (no customer data, no VFP9 oracle, no Internal-only Fixtures
/// accessors). The byte/behaviour VFP9-oracle golden lives in the Internal project
/// (MicroVfpRelationOracleTests).
///
/// Semantics pinned here (hackfox s4g084 + the MICROVFP_EXTENSIONS_BACKLOG SET RELATION / SET SKIP
/// entries): on every parent move an automatic SEEK repositions each child on its active order; a miss
/// leaves the child at EOF; relations chain (parent→child→grandchild) and there can be several per
/// parent; ADDITIVE adds without clearing; a numeric key into a child with NO controlling order does an
/// implicit GOTO; SET RELATION OFF INTO removes just one; SET SKIP bounds a one-to-many child SKIP to the
/// current parent-key group.
/// </summary>
public sealed class MicroVfpRelationTests
{
    // ─────────────────────────── multi-table scaffolding ───────────────────────────

    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public VfpSession Session { get; private set; } = null!;
        public VfpInterpreter Interp { get; private set; } = null!;

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_rel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public void CreateTable(string name, DbfColumnDef[] cols, Action<DbfWriter> rows)
        {
            using var w = DbfWriter.Create(Path.Combine(Dir, name + ".dbf"), cols);
            rows(w);
            w.Flush();
        }

        public void Open()
        {
            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public void Run(string prg) => Interp.Execute(prg);
        public decimal Num(string e) => Interp.EvalExpression(e).AsNumber;
        public string Str(string e) => Interp.EvalExpression(e).AsString;
        public bool Bool(string e) => Interp.EvalExpression(e).AsLogical;

        public void Dispose()
        {
            try { Session?.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static DbfColumnDef C(string name, int len) => new(name, 'C', len);

    /// <summary>customer (A001,A002,A003) + orders (O1/A001, O2/A001, O3/A002) indexed on cust_id.</summary>
    private static Bench CustomerOrders()
    {
        var b = new Bench();
        b.CreateTable("customer", new[] { C("cust_id", 4), C("cname", 10) }, w =>
        {
            w.AppendRecord("A001", "Alice");   // rec 1
            w.AppendRecord("A002", "Bob");     // rec 2
            w.AppendRecord("A003", "Carol");   // rec 3 — has NO orders (miss → child EOF)
        });
        b.CreateTable("orders", new[] { C("ord_id", 6), C("cust_id", 4) }, w =>
        {
            w.AppendRecord("O1", "A001");      // rec 1
            w.AppendRecord("O2", "A001");      // rec 2
            w.AppendRecord("O3", "A002");      // rec 3
        });
        b.Open();
        b.Run("SELECT 0\nUSE customer\nSELECT 0\nUSE orders\nINDEX ON cust_id TAG custord");
        return b;
    }

    // ─────────────────────────── (1) basic relation: child follows parent ───────────────────────────

    [Fact]
    public void SetRelation_ChildFollowsParentMove_MissGoesEof_ClearDetaches()
    {
        using var b = CustomerOrders();
        b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders\nGO TOP");

        // Parent at A001 → child seeks the first A001 order (O1, rec 1).
        Assert.Equal("A001", b.Str("orders.cust_id"));
        Assert.Equal(1m, b.Num("RECNO('orders')"));
        Assert.False(b.Bool("EOF('orders')"));

        // Moving the parent (GO recno) repositions the child.
        b.Run("GO 2");   // customer A002
        Assert.Equal("A002", b.Str("orders.cust_id"));
        Assert.Equal(3m, b.Num("RECNO('orders')"));

        // A parent key with no child match leaves the child at EOF().
        b.Run("GO 3");   // customer A003 — no orders
        Assert.True(b.Bool("EOF('orders')"));

        // SET RELATION TO (no args) detaches: the child no longer follows the parent.
        b.Run("GO 1");   // A001 → orders back to O1 (rec 1)
        Assert.Equal(1m, b.Num("RECNO('orders')"));
        b.Run("SET RELATION TO\nGO 3");   // A003, but relation cleared → child stays put
        Assert.Equal(1m, b.Num("RECNO('orders')"));
        Assert.False(b.Bool("EOF('orders')"));
    }

    // ─────────────────────────── (2) multiple children / chaining / additive / off / recno ───────────

    [Fact]
    public void SetRelation_MultipleChildrenInOneStatement_BothFollow()
    {
        using var b = CustomerOrders();
        b.CreateTable("notes", new[] { C("note_id", 6), C("cust_id", 4) }, w =>
        {
            w.AppendRecord("N1", "A002");   // rec 1
            w.AppendRecord("N2", "A001");   // rec 2
        });
        b.Run("SELECT 0\nUSE notes\nINDEX ON cust_id TAG custnote");

        b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders, cust_id INTO notes\nGO 2");   // A002
        Assert.Equal(3m, b.Num("RECNO('orders')"));   // O3
        Assert.Equal(1m, b.Num("RECNO('notes')"));    // N1
        b.Run("GO 1");                                 // A001
        Assert.Equal(1m, b.Num("RECNO('orders')"));   // O1
        Assert.Equal(2m, b.Num("RECNO('notes')"));    // N2
    }

    [Fact]
    public void SetRelation_ChainedRelationRepositionsTransitively()
    {
        using var b = CustomerOrders();
        b.CreateTable("items", new[] { C("item_id", 6), C("ord_id", 6) }, w =>
        {
            w.AppendRecord("I1", "O1");    // rec 1 → order O1
            w.AppendRecord("I2", "O3");    // rec 2 → order O3
        });
        b.Run("SELECT 0\nUSE items\nINDEX ON ord_id TAG orditem");

        // customer → orders (cust_id) → items (ord_id): moving the customer repositions items transitively.
        b.Run("SELECT orders\nSET RELATION TO ord_id INTO items");
        b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders");

        b.Run("GO TOP");    // A001 → orders O1 → items I1
        Assert.Equal(1m, b.Num("RECNO('orders')"));
        Assert.Equal(1m, b.Num("RECNO('items')"));

        b.Run("GO 2");      // A002 → orders O3 → items I2
        Assert.Equal(3m, b.Num("RECNO('orders')"));
        Assert.Equal(2m, b.Num("RECNO('items')"));
    }

    [Fact]
    public void SetRelation_Additive_AddsWithoutClearing_OffIntoRemovesJustOne()
    {
        using var b = CustomerOrders();
        b.CreateTable("notes", new[] { C("note_id", 6), C("cust_id", 4) }, w =>
        {
            w.AppendRecord("N1", "A002");
            w.AppendRecord("N2", "A001");
        });
        b.Run("SELECT 0\nUSE notes\nINDEX ON cust_id TAG custnote");

        b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders");
        b.Run("SET RELATION TO cust_id INTO notes ADDITIVE");   // ADDITIVE keeps the orders relation.
        b.Run("GO 2");   // A002
        Assert.Equal(3m, b.Num("RECNO('orders')"));            // still related
        Assert.Equal(1m, b.Num("RECNO('notes')"));             // newly related
        // VFP9 orders relations most-recently-set first, so the ADDITIVE notes relation is #1, orders #2.
        Assert.Equal("cust_id", b.Str("RELATION(1)"));
        Assert.Equal("NOTES", b.Str("TARGET(1)"));
        Assert.Equal("ORDERS", b.Str("TARGET(2)"));

        // SET RELATION OFF INTO notes removes ONLY the notes relation; orders keeps following.
        b.Run("SET RELATION OFF INTO notes");
        b.Run("GO 1");   // A001 → orders O1; notes stays at N1 (no longer related)
        Assert.Equal(1m, b.Num("RECNO('orders')"));
        Assert.Equal(1m, b.Num("RECNO('notes')"));
        Assert.Equal(string.Empty, b.Str("TARGET(2)"));         // second relation gone
    }

    [Fact]
    public void SetRelation_NumericRecnoKey_DoesImplicitGoto()
    {
        using var b = CustomerOrders();
        // orders currently has a controlling order (custord). Clear it so a NUMERIC relation is a
        // record-number relation (implicit GOTO) rather than a SEEK.
        b.Run("SELECT orders\nSET ORDER TO 0");
        b.Run("SELECT customer\nSET RELATION TO RECNO() INTO orders");

        b.Run("GO 2");   // customer rec 2 → orders GOTO 2 (record number, not a key SEEK)
        Assert.Equal(2m, b.Num("RECNO('orders')"));
        b.Run("GO 3");   // customer rec 3 → orders GOTO 3
        Assert.Equal(3m, b.Num("RECNO('orders')"));
    }

    // ─────────────────────────── (3) SET SKIP one-to-many + introspection ───────────────────────────

    [Fact]
    public void SetSkip_OneToMany_ChildSkipStaysInParentGroup_ThenClearReleases()
    {
        using var b = CustomerOrders();
        b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders");
        b.Run("SET SKIP TO orders");
        b.Run("GO TOP");   // A001 → orders first match O1 (rec 1)
        Assert.Equal(1m, b.Num("RECNO('orders')"));

        // Under one-to-many, SKIP in the child walks the matching rows (O1 → O2), then stops (EOF).
        b.Run("SELECT orders\nSKIP");
        Assert.Equal(2m, b.Num("RECNO('orders')"));   // O2, still cust_id A001
        Assert.False(b.Bool("EOF('orders')"));
        b.Run("SKIP");
        Assert.True(b.Bool("EOF('orders')"));         // past the last A001 order → EOF, NOT into A002

        // SET SKIP TO (no args) clears one-to-many: SKIP then walks freely into the next group.
        b.Run("SELECT customer\nGO TOP");             // A001 → orders O1
        b.Run("SET SKIP TO");
        b.Run("SELECT orders\nSKIP\nSKIP");           // O1 → O2 → O3 (A002), no clamp
        Assert.Equal(3m, b.Num("RECNO('orders')"));
        Assert.False(b.Bool("EOF('orders')"));
    }

    [Fact]
    public void SetRelation_And_SetSkip_Getters_ReproduceState()
    {
        using var b = CustomerOrders();
        b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders");
        Assert.Equal("cust_id INTO ORDERS", b.Str("SET('RELATION')"));
        Assert.Equal(string.Empty, b.Str("SET('SKIP')"));       // no one-to-many yet
        Assert.Equal("cust_id", b.Str("RELATION(1)"));
        Assert.Equal("ORDERS", b.Str("TARGET(1)"));
        Assert.Equal(string.Empty, b.Str("RELATION(2)"));       // no second relation

        b.Run("SET SKIP TO orders");
        Assert.Equal("ORDERS", b.Str("SET('SKIP')"));
        b.Run("SET SKIP TO");
        Assert.Equal(string.Empty, b.Str("SET('SKIP')"));
    }

    // ─────────────────────────── (4) prefix-key relation under SET EXACT OFF ─────────────────────────

    [Fact]
    public void SetRelation_PrefixOfCompositeChildKey_MatchesUnderExactOff()
    {
        var b = new Bench();
        b.CreateTable("customer", new[] { C("cust_id", 4), C("cname", 10) }, w =>
        {
            w.AppendRecord("A001", "Alice");
            w.AppendRecord("A002", "Bob");
        });
        b.CreateTable("orders", new[] { C("ord_id", 6), C("cust_id", 4) }, w =>
        {
            w.AppendRecord("O1", "A001");   // rec 1
            w.AppendRecord("O2", "A002");   // rec 2
        });
        b.Open();
        using (b)
        {
            // Child tag is a COMPOSITE key cust_id+ord_id; the relation expression is only the cust_id
            // PREFIX of it — works with SET EXACT OFF (the default in VFP).
            b.Run("SELECT 0\nUSE customer\nSELECT 0\nUSE orders\nINDEX ON cust_id+ord_id TAG custord2");
            b.Run("SET EXACT OFF");
            b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders\nGO TOP");   // A001
            Assert.Equal(1m, b.Num("RECNO('orders')"));       // matched the A001... composite entry
            Assert.Equal("A001", b.Str("orders.cust_id"));
            b.Run("GO 2");                                     // A002
            Assert.Equal(2m, b.Num("RECNO('orders')"));
            Assert.Equal("A002", b.Str("orders.cust_id"));
        }
    }

    /// <summary>Under SET EXACT ON a relation key that is only the cust_id PREFIX of the composite cust_id+ord_id
    /// child key does NOT match — the child lands at EOF (hackfox quirk 1, VFP9-confirmed: EXACT ON → child EOF;
    /// the prefix match is EXACT-OFF-only).</summary>
    [Fact]
    public void SetRelation_PrefixOfCompositeChildKey_GoesEofUnderExactOn()
    {
        var b = new Bench();
        b.CreateTable("customer", new[] { C("cust_id", 4), C("cname", 10) }, w =>
        {
            w.AppendRecord("A001", "Alice");
            w.AppendRecord("A002", "Bob");
        });
        b.CreateTable("orders", new[] { C("ord_id", 6), C("cust_id", 4) }, w =>
        {
            w.AppendRecord("O1", "A001");   // rec 1
            w.AppendRecord("O2", "A002");   // rec 2
        });
        b.Open();
        using (b)
        {
            b.Run("SELECT 0\nUSE customer\nSELECT 0\nUSE orders\nINDEX ON cust_id+ord_id TAG custord2");
            b.Run("SET EXACT ON\nSELECT customer\nSET RELATION TO cust_id INTO orders\nGO TOP");   // A001
            Assert.True(b.Bool("EOF('orders')"));
            b.Run("GO 2");                                                          // A002
            Assert.True(b.Bool("EOF('orders')"));
        }
    }

    [Fact]
    public void InterpreterGeneralCollatedRelation_RepositionsOnEquivalentFullKeyUnderExactOn()
    {
        var b = new Bench();
        b.CreateTable("customer", new[] { C("cust_id", 4) }, w =>
        {
            w.AppendRecord("a001");
            w.AppendRecord("a002");
        });
        b.CreateTable("orders", new[] { C("ord_id", 6), C("cust_id", 4) }, w =>
        {
            w.AppendRecord("O1", "A001");
            w.AppendRecord("O2", "A002");
        });
        b.Open();
        using (b)
        {
            b.Run("SELECT 0\nUSE customer\nSELECT 0\nUSE orders\n" +
                  "SET COLLATE TO GENERAL\nINDEX ON cust_id TAG custord");
            b.Run("SET EXACT ON\nSELECT customer\nSET RELATION TO cust_id INTO orders\nGO TOP");

            Assert.False(b.Bool("EOF('orders')"));
            Assert.Equal(1m, b.Num("RECNO('orders')"));
            Assert.Equal("A001", b.Str("orders.cust_id").TrimEnd());

            b.Run("GO 2");
            Assert.False(b.Bool("EOF('orders')"));
            Assert.Equal(2m, b.Num("RECNO('orders')"));
        }
    }

    /// <summary>SET SKIP one-to-many clamp on a NUMERIC (integer) relation key: a programmatic SKIP in the
    /// child walks the matching rows, then goes EOF past the last match instead of leaking into the next
    /// parent group. Guards the fix where the group-boundary check compared numeric keys by full value
    /// (previously the string backing was always "" ⇒ the clamp never fired).</summary>
    [Fact]
    public void SetSkip_OneToMany_NumericKey_ChildClampsToEofPastLastMatch()
    {
        var b = new Bench();
        b.CreateTable("customer", new[] { new DbfColumnDef("cust_id", 'I', 4), C("cname", 10) }, w =>
        {
            w.AppendRecord(1, "Alice");   // rec 1
            w.AppendRecord(2, "Bob");     // rec 2
        });
        b.CreateTable("orders", new[] { new DbfColumnDef("ord_id", 'I', 4), new DbfColumnDef("cust_id", 'I', 4) }, w =>
        {
            w.AppendRecord(101, 1);   // rec 1 — cust 1
            w.AppendRecord(102, 1);   // rec 2 — cust 1
            w.AppendRecord(103, 2);   // rec 3 — cust 2
        });
        b.Open();
        using (b)
        {
            b.Run("SELECT 0\nUSE customer\nSELECT 0\nUSE orders\nINDEX ON cust_id TAG custord");
            b.Run("SELECT customer\nSET RELATION TO cust_id INTO orders");
            b.Run("SET SKIP TO orders");
            b.Run("GO TOP");   // cust 1 → orders first match rec 1
            Assert.Equal(1m, b.Num("RECNO('orders')"));

            b.Run("SELECT orders\nSKIP");
            Assert.Equal(2m, b.Num("RECNO('orders')"));   // rec 2, still cust 1
            Assert.False(b.Bool("EOF('orders')"));
            b.Run("SKIP");
            Assert.True(b.Bool("EOF('orders')"));         // past the last cust-1 order → EOF, NOT into cust 2
        }
    }
}
