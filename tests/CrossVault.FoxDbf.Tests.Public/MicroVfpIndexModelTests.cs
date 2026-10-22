using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP INDEX/ORDER MODEL — the multi-index-per-area pipeline (SET INDEX / USE … INDEX, standalone
/// <c>.idx</c> write, non-structural <c>.cdx</c> tags, DELETE TAG, the index-introspection functions,
/// SET NEAR + LOOKUP). Written TESTS-FIRST: every case pins the desired PRG-level contract and is RED
/// until the interpreter is wired to hold an ORDERED LIST of open index files per work area and resolve
/// tags/orders across the FULL open set.
///
/// SAFETY: every case builds a FRESH synthetic table in its own throwaway temp dir (never a committed
/// fixture); the dir is removed on dispose. Public-safe — synthetic data only, no VFP9 runtime, no
/// Internal-only fixtures accessors. The byte-exact VFP9-oracle golden lives in the Internal project.
/// </summary>
public sealed class MicroVfpIndexModelTests
{
    // ─────────────────────────── synthetic-table scaffolding ───────────────────────────

    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public string DbfPath { get; }
        public VfpSession Session { get; private set; }
        public VfpInterpreter Interp { get; private set; }

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_idxmodel_" + Guid.NewGuid().ToString("N"));
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
                // recno → (id, name, city). Mixed case + a duplicate id (10) for UNIQUE/CANDIDATE.
                w.AppendRecord(30, "Charlie", "berlin");   // rec 1
                w.AppendRecord(10, "alice",   "berlin");   // rec 2
                w.AppendRecord(20, "Bob",     "aachen");   // rec 3
                w.AppendRecord(10, "amy",     "aachen");   // rec 4
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

        /// <summary>Drop the session and re-open a fresh one over the SAME temp directory — proves an
        /// index file written in one session is re-openable via SET INDEX / USE … INDEX in the next.</summary>
        public void Reopen()
        {
            Session.Dispose();
            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public string File(string name) => Path.Combine(Dir, name);

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── (1) SET INDEX TO / USE … INDEX ───────────────────────────

    [Fact]
    public void SetIndexTo_OpensExtraCdx_SetOrderSelectsItsTag_SeekUsesIt()
    {
        using var b = new Bench();
        // Build a NON-structural more.cdx (city order) in one session, then re-open it additionally.
        b.Run("USE people\nINDEX ON city TAG citytag OF more.cdx");
        Assert.True(File.Exists(b.File("more.cdx")), "INDEX ON … OF more.cdx did not create the file.");

        b.Reopen();
        b.Run("USE people\nSET INDEX TO more.cdx\nSET ORDER TO citytag\nGO TOP");
        // city order (MACHINE): aachen(rec3), aachen(rec4), berlin(rec1), berlin(rec2) → top = rec 3.
        Assert.Equal(3m, b.Num("RECNO()"));

        b.Run("=SEEK('aachen', 'people', 'citytag')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(3m, b.Num("RECNO()"));
    }

    [Fact]
    public void SetIndexTo_NoArgs_ClosesNonStructuralIndexes()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON city TAG citytag OF more.cdx");
        b.Reopen();
        b.Run("USE people\nSET INDEX TO more.cdx");
        b.Run("SET ORDER TO citytag");             // resolvable while more.cdx is open.

        b.Run("SET INDEX TO");                       // closes every non-structural index.
        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("SET ORDER TO citytag"));
    }

    [Fact]
    public void SetIndexTo_Additive_KeepsPriorIndexes()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON city TAG citytag OF cityidx.cdx\nSET INDEX TO");
        b.Run("INDEX ON UPPER(name) TAG nametag OF nameidx.cdx\nSET INDEX TO");
        b.Reopen();

        b.Run("USE people\nSET INDEX TO cityidx.cdx");
        b.Run("SET INDEX TO nameidx.cdx ADDITIVE");   // ADDITIVE ⇒ cityidx stays open.
        b.Run("SET ORDER TO citytag");                // still resolvable (not closed by the 2nd SET INDEX).
        b.Run("SET ORDER TO nametag");                // and the newly-added one resolves too.
    }

    [Fact]
    public void UseIndex_OpensAndTracksList()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON city TAG citytag OF more.cdx");
        b.Reopen();

        // USE … INDEX <list> opens+tracks the named index files (was parse-and-discard).
        b.Run("USE people INDEX more.cdx\nSET ORDER TO citytag\nGO TOP");
        Assert.Equal(3m, b.Num("RECNO()"));
    }

    // ─────────────────────────── (2) standalone .idx / non-structural .cdx / DELETE TAG ───────────────────────────

    [Fact]
    public void IndexOnToIdx_BuildsStandaloneIdx_SeekAndSetOrderUseIt()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TO peopleid");
        string idxPath = b.File("peopleid.idx");
        Assert.True(File.Exists(idxPath), "INDEX ON … TO peopleid did not create peopleid.idx");

        using (var idx = IdxFile.Open(idxPath))
            Assert.Equal("ID", idx.Header.KeyExpression.Trim().ToUpperInvariant());

        // Re-open the standalone .idx and navigate/seek through it.
        b.Reopen();
        b.Run("USE people INDEX peopleid.idx\nGO TOP");
        Assert.Equal(2m, b.Num("RECNO()"));   // id order top = rec 2 (id 10).

        b.Run("=SEEK(20, 'people')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(3m, b.Num("RECNO()"));   // rec 3 carries id = 20.
    }

    [Fact]
    public void IndexOnTagOfCdx_BuildsNonStructuralTag()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG x OF other.cdx");

        string cdxPath = b.File("other.cdx");
        Assert.True(File.Exists(cdxPath), "INDEX ON … TAG x OF other.cdx did not create other.cdx");

        using var dbf = DbfTable.Open(b.DbfPath, new DbfOptions { LockMode = LockMode.Shared });
        using var cdx = CdxFile.Open(cdxPath, dbf);
        var tag = cdx.Tag("x") ?? cdx.Tag("X");
        Assert.NotNull(tag);
        Assert.Equal("ID", tag!.KeyExpression.Trim().ToUpperInvariant());
    }

    [Fact]
    public void DeleteTag_RemovesNamedTag_LeavesTheOthers()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nINDEX ON city TAG citytag");
        Assert.Equal(2m, b.Num("TAGCOUNT()"));

        b.Run("DELETE TAG idtag");
        Assert.Equal(1m, b.Num("TAGCOUNT()"));

        // The structural cdx on disk no longer carries idtag, still carries citytag.
        using var dbf = DbfTable.Open(b.DbfPath, new DbfOptions { LockMode = LockMode.Shared });
        using var cdx = CdxFile.Open(Path.ChangeExtension(b.DbfPath, ".cdx"), dbf);
        Assert.Null(cdx.Tag("idtag") ?? cdx.Tag("IDTAG"));
        Assert.NotNull(cdx.Tag("citytag") ?? cdx.Tag("CITYTAG"));
    }

    [Fact]
    public void DeleteTagAll_RemovesEveryTag_AndDeletesEmptyStructuralCdx()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nINDEX ON city TAG citytag");
        b.Run("DELETE TAG ALL");
        Assert.Equal(0m, b.Num("TAGCOUNT()"));
        // VFP deletes the structural .cdx when its last tag is removed.
        Assert.False(File.Exists(Path.ChangeExtension(b.DbfPath, ".cdx")),
            "DELETE TAG ALL left an empty structural .cdx behind.");
    }

    [Fact]
    public void SetKey_Works_WhenControllingOrderIsInNonStructuralCdx()
    {
        using var b = new Bench();
        // Controlling order sourced from a NON-structural .cdx (there is NO structural .cdx at all) — the
        // MasterTag path must resolve it across the full open set, else SET KEY throws 'no controlling
        // index is active'. Restrict the visible set to id ∈ [20, 30] → recs 3 (id 20) and 1 (id 30).
        b.Run("USE people\nINDEX ON id TAG idtag OF extra.cdx\nSET ORDER TO idtag\nSET KEY TO 20, 30");

        b.Run("GO TOP");                     // ascending idtag over the visible set → id 20 = rec 3.
        Assert.False(b.Bool("EOF()"));
        Assert.Equal(3m, b.Num("RECNO()"));

        b.Run("SKIP");                       // next visible in idtag order → id 30 = rec 1.
        Assert.Equal(1m, b.Num("RECNO()"));

        b.Run("SKIP");                       // no more visible records in range → EOF.
        Assert.True(b.Bool("EOF()"));
    }

    [Fact]
    public void SetOrderDescending_OnNonStructuralCdx_ReversesTraversal()
    {
        using var b = new Bench();
        // A DESCENDING override on an order sourced from a non-structural .cdx must actually reverse it
        // (MasterDescending resolving through the full open set), so GO TOP lands on the LARGEST id.
        b.Run("USE people\nINDEX ON id TAG idtag OF extra.cdx\nSET ORDER TO idtag DESCENDING\nGO TOP");
        Assert.Equal(1m, b.Num("RECNO()"));   // descending id top = id 30 = rec 1.
    }

    // ─────────────────────────── (3) index-introspection functions ───────────────────────────

    [Fact]
    public void Introspection_TagCount_Tag_TagNo()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nINDEX ON UPPER(name) TAG nm\nSET ORDER TO nm");
        Assert.Equal(2m, b.Num("TAGCOUNT()"));
        Assert.Equal("IDTAG", b.Str("TAG(1)").ToUpperInvariant());
        Assert.Equal("NM", b.Str("TAG(2)").ToUpperInvariant());
        Assert.Equal(2m, b.Num("TAGNO()"));   // nm is the 2nd tag and the controlling order.
    }

    [Fact]
    public void Introspection_Cdx_Mdx_Ndx_Order()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nSET ORDER TO idtag");
        // Structural cdx name (CDX(1)==MDX(1)); NDX(1) is empty (no standalone .idx open).
        Assert.EndsWith("PEOPLE.CDX", b.Str("CDX(1)").ToUpperInvariant());
        Assert.EndsWith("PEOPLE.CDX", b.Str("MDX(1)").ToUpperInvariant());
        Assert.Equal(string.Empty, b.Str("NDX(1)"));
        Assert.Equal("IDTAG", b.Str("ORDER()").ToUpperInvariant());
    }

    [Fact]
    public void Introspection_Key_Unique_Descending_For_Candidate_Primary_IdxCollate()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nINDEX ON id TAG d DESCENDING\n" +
              "INDEX ON id TAG u UNIQUE\nINDEX ON id TAG f FOR city = 'berlin'");

        // KEY() and SYS(14) BOTH return the key expression ALL-CAPS (s4g266) — assert the raw all-caps
        // result (no masking .ToUpperInvariant()) so the contract is pinned identically for both.
        Assert.Equal("ID", b.Str("KEY(1)").Trim());
        Assert.Equal("ID", b.Str("SYS(14, 1)").Trim());          // SYS(14) is uppercase per s4g266.

        Assert.False(b.Bool("DESCENDING(1)"));                   // idtag ascending.
        Assert.True(b.Bool("DESCENDING(2)"));                    // d descending.
        Assert.True(b.Bool("UNIQUE(3)"));                        // u unique.
        Assert.False(b.Bool("UNIQUE(1)"));

        // FOR() is ALL-CAPS (s4g266), un-masked — of the DECOMPILED text VFP stores (`city="berlin"`); VFP9
        // answers CITY="BERLIN" here (verified: FOR "name = 'a b'" reads back as NAME="A B").
        Assert.Equal("CITY=\"BERLIN\"", b.Str("FOR(4)").Trim());
        Assert.Equal(string.Empty, b.Str("FOR(1)").Trim());     // no FOR filter on idtag.

        Assert.False(b.Bool("CANDIDATE(1)"));                    // free-table INDEX ⇒ no candidate/primary.
        Assert.False(b.Bool("PRIMARY(1)"));
        Assert.Equal("MACHINE", b.Str("IDXCOLLATE(1)").Trim().ToUpperInvariant());
    }

    [Fact]
    public void Introspection_Sys14_OutOfRange_IsEmptyString()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag");
        Assert.Equal("ID", b.Str("SYS(14, 1)").Trim());
        Assert.Equal(string.Empty, b.Str("SYS(14, 99)"));       // out-of-range (1-based) ⇒ empty string.
    }

    [Fact]
    public void Introspection_ATagInfo_FillsArray_AndReturnsTagCount()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nINDEX ON city TAG citytag");
        b.Run("DIMENSION arr(1, 1)");
        Assert.Equal(2m, b.Num("ATAGINFO(arr)"));               // return value = number of tags.
        Assert.Equal("IDTAG", b.Str("arr(1, 1)").ToUpperInvariant());   // col 1 = tag name.
    }

    // ─────────────────────────── (4) SET NEAR + LOOKUP ───────────────────────────

    [Fact]
    public void SetNearOn_FailedSeek_LeavesPointerJustPastSortPosition()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nSET ORDER TO idtag\nSET NEAR ON");
        Assert.Equal("ON", b.Str("SET('NEAR')").ToUpperInvariant());

        // 15 is absent and sorts between id 10 and id 20 → the pointer lands on the first key > 15 (rec 3).
        b.Run("=SEEK(15, 'people', 'idtag')");
        Assert.False(b.Bool("FOUND()"));
        Assert.False(b.Bool("EOF()"));
        Assert.Equal(3m, b.Num("RECNO()"));

        // Past the end → EOF (RECNO() == RECCOUNT()+1).
        b.Run("=SEEK(999, 'people', 'idtag')");
        Assert.True(b.Bool("EOF()"));
        Assert.Equal(5m, b.Num("RECNO()"));
    }

    [Fact]
    public void SetNearOff_ReadsBackOff_AndFailedSeekParksAtEof()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nSET ORDER TO idtag\nSET NEAR ON\nSET NEAR OFF");
        Assert.Equal("OFF", b.Str("SET('NEAR')").ToUpperInvariant());   // toggle reads back through SET().
        b.Run("=SEEK(15, 'people', 'idtag')");
        Assert.True(b.Bool("EOF()"));   // NEAR OFF (default) parks at EOF on a miss.
    }

    [Fact]
    public void Lookup_ReturnsMappedFieldForSoughtKey()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag");
        // LOOKUP(rReturn, eSearch, rSearched [, cTag]) — seek id=20 via idtag, return name → 'Bob'.
        Assert.Equal("Bob", b.Str("LOOKUP(name, 20, id, 'idtag')").TrimEnd());
        Assert.True(b.Bool("FOUND()"));
    }
}
