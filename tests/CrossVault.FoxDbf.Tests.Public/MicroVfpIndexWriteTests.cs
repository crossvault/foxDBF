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
/// microVFP P1 gap #1 — the INDEX / ORDER WRITE model (INDEX ON, SET ORDER, SET COLLATE, SET UNIQUE,
/// SET KEY, REINDEX). Written TESTS-FIRST: they pin the desired contract and are RED until the
/// interpreter is wired to the byte-exact CDX builder (DbfWriter.CreateTag / Reindex). The heavy
/// lifting already exists — these drive the PRG-level commands end to end.
///
/// SAFETY: every case builds a FRESH synthetic table in its own throwaway temp dir (never a committed
/// fixture); the dir is removed on dispose. All assertions run on synthetic data, so this suite is
/// public-safe (no customer data, no VFP9 oracle, no Internal-only fixtures accessors) — the byte-exact
/// VFP9-oracle golden lives in the Internal project instead.
/// </summary>
public sealed class MicroVfpIndexWriteTests
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
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_idxwr_" + Guid.NewGuid().ToString("N"));
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

        /// <summary>Re-open the structural .cdx from disk (fresh handle) to inspect what INDEX ON wrote.</summary>
        public CdxTag Tag(string name)
        {
            string cdxPath = Path.ChangeExtension(DbfPath, ".cdx");
            Assert.True(File.Exists(cdxPath), $"INDEX ON did not create a structural .cdx at {cdxPath}");
            using var dbf = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            using var cdx = CdxFile.Open(cdxPath, dbf);
            var t = cdx.Tag(name) ?? cdx.Tag(name.ToUpperInvariant());
            Assert.NotNull(t);
            return t!;
        }

        /// <summary>Count a tag's index entries with the .cdx/.dbf handles held OPEN for the walk
        /// (EnumerateEntries reads the stream, so it must not outlive the CdxFile).</summary>
        public int TagEntryCount(string name)
        {
            string cdxPath = Path.ChangeExtension(DbfPath, ".cdx");
            Assert.True(File.Exists(cdxPath), $"INDEX ON did not create a structural .cdx at {cdxPath}");
            using var dbf = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            using var cdx = CdxFile.Open(cdxPath, dbf);
            var t = cdx.Tag(name) ?? cdx.Tag(name.ToUpperInvariant());
            Assert.NotNull(t);
            return t!.EnumerateEntries().Count();
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── (1) INDEX ON — build a tag ───────────────────────────

    [Fact]
    public void IndexOn_Tag_CreatesTag_AndSeekFindsKey()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag");

        var tag = b.Tag("idtag");
        Assert.Equal("ID", tag.KeyExpression.ToUpperInvariant());

        b.Run("=SEEK(20, 'people', 'idtag')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(3m, b.Num("RECNO()"));   // rec 3 carries id = 20
    }

    [Fact]
    public void IndexOn_CompoundKey_Upper_OrdersCaseInsensitively()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON UPPER(name) TAG nm\nSET ORDER TO nm\nGO TOP");
        // UPPER(name) order: ALICE(2), AMY(4), BOB(3), CHARLIE(1) → top is rec 2 (NOT rec 3 as a plain
        // MACHINE char sort of 'Bob' < 'alice' would give).
        Assert.Equal(2m, b.Num("RECNO()"));
    }

    [Fact]
    public void IndexOn_ForFilter_ExcludesNonMatchingRecords()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG f FOR city = 'berlin'\nSET ORDER TO f\nGO TOP");
        // Only the two 'berlin' rows are indexed, ordered by id: id10(rec2), id30(rec1).
        Assert.Equal(2m, b.Num("RECNO()"));
        b.Run("SKIP");
        Assert.Equal(1m, b.Num("RECNO()"));
        b.Run("SKIP");
        Assert.True(b.Bool("EOF()"));
    }

    [Fact]
    public void IndexOn_Descending_ReversesOrder()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG d DESCENDING\nSET ORDER TO d\nGO TOP");
        Assert.Equal(30m, b.Num("id"));       // highest id first
        Assert.True(b.Tag("d").Descending);
    }

    [Fact]
    public void IndexOn_Unique_KeepsFirstRecordPerKey()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG u UNIQUE\nSET ORDER TO u\nGO TOP");
        Assert.True(b.Tag("u").IsUnique);
        // Distinct keys 10/20/30, first-per-key by recno: id10→rec2, id20→rec3, id30→rec1.
        Assert.Equal(2m, b.Num("RECNO()"));   // the id=10 duplicate at rec 4 is dropped
        b.Run("SKIP")   ; Assert.Equal(3m, b.Num("RECNO()"));
        b.Run("SKIP")   ; Assert.Equal(1m, b.Num("RECNO()"));
        b.Run("SKIP")   ; Assert.True(b.Bool("EOF()"));
    }

    [Fact]
    public void IndexOn_Candidate_RaisesOnDuplicateKey()
    {
        using var b = new Bench();
        // id has a duplicate (10 at rec 2 and rec 4) → CANDIDATE must raise a catchable runtime error.
        Assert.Throws<MicroVfpRuntimeException>(() =>
            b.Run("USE people\nINDEX ON id TAG c CANDIDATE"));
    }

    // ─────────────────────────── (2) SET ORDER / COLLATE / UNIQUE ───────────────────────────

    [Fact]
    public void SetOrderTo_Tag_MakesGoTopFollowTag_Then0RestoresRecordOrder()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nSET ORDER TO idtag\nGO TOP");
        Assert.Equal(2m, b.Num("RECNO()"));   // id order top = rec 2 (id 10)

        b.Run("SET ORDER TO 0\nGO TOP");
        Assert.Equal(1m, b.Num("RECNO()"));   // natural/record order top = rec 1
    }

    [Fact]
    public void SetCollateTo_General_IsBakedIntoTag_AndReadBackBySetGetter()
    {
        using var b = new Bench();
        b.Run("USE people\nSET COLLATE TO GENERAL\nINDEX ON name TAG g");
        Assert.Equal("GENERAL", b.Tag("g").Collation.ToUpperInvariant());
        Assert.Equal("GENERAL", b.Str("SET('COLLATE')").ToUpperInvariant());
    }

    [Fact]
    public void InterpreterGeneralCollatedSeek_FullPrefixAndLookupFindRows()
    {
        using var b = new Bench();
        b.Run("USE people\nSET COLLATE TO GENERAL\nINDEX ON name TAG g\nSET ORDER TO g");

        b.Run("=SEEK('alice', 'people', 'g')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(2m, b.Num("RECNO()"));

        // VFP9 defaults to EXACT OFF; explicitly switch ON to reject prefixes under every collation.
        b.Run("=SEEK('ali', 'people', 'g')");
        Assert.True(b.Bool("FOUND()"));
        b.Run("SET EXACT ON\n=SEEK('ali', 'people', 'g')");
        Assert.False(b.Bool("FOUND()"));

        b.Run("SET EXACT OFF\n=SEEK('ali', 'people', 'g')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(2m, b.Num("RECNO()"));

        Assert.Equal("alice", b.Str("LOOKUP(name, 'alice', name, 'g')").TrimEnd());
        Assert.True(b.Bool("FOUND()"));
    }

    [Fact]
    public void InterpreterGeneralCollatedSeek_DeletedEquivalentDescendingLandsOnVisibleRow()
    {
        using var b = new Bench();
        b.Run("USE people\nGO 2\nDELETE\nGO 4\nREPLACE name WITH 'ALICE'\n" +
              "SET DELETED OFF\nSET COLLATE TO GENERAL\nINDEX ON name TAG g DESCENDING\n" +
              "SET ORDER TO g\nSET DELETED ON");

        b.Run("=SEEK('alice', 'people', 'g')");

        Assert.True(b.Bool("FOUND()"));
        Assert.False(b.Bool("DELETED()"));
        Assert.Equal(4m, b.Num("RECNO()"));
    }

    [Fact]
    public void InterpreterGeneralCollatedSeek_SetNearUsesCollatedSortPosition()
    {
        using var b = new Bench();
        b.Run("USE people\nSET COLLATE TO GENERAL\nINDEX ON name TAG g\nSET ORDER TO g\nSET NEAR ON");

        b.Run("=SEEK('Bzz', 'people', 'g')");

        Assert.False(b.Bool("FOUND()"));
        Assert.False(b.Bool("EOF()"));
        Assert.Equal("Charlie", b.Str("name").TrimEnd());
    }

    [Fact]
    public void SetCollateTo_UnsupportedSequence_Raises()
    {
        using var b = new Bench();
        Assert.Throws<MicroVfpRuntimeException>(() =>
            b.Run("USE people\nSET COLLATE TO CROATIAN"));
    }

    [Fact]
    public void SetUnique_On_MakesPlainIndexBehaveUnique_AndReadBackBySetGetter()
    {
        using var b = new Bench();
        b.Run("USE people\nSET UNIQUE ON\nINDEX ON id TAG pu");
        Assert.Equal("ON", b.Str("SET('UNIQUE')").ToUpperInvariant());
        Assert.True(b.Tag("pu").IsUnique);    // no explicit UNIQUE clause, but SET UNIQUE ON applied
    }

    // ─────────────────────────── (3) SET KEY / REINDEX ───────────────────────────

    [Fact]
    public void SetKeyTo_LimitsNavigationToKeyValue_ThenNoArgClears()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nSET ORDER TO idtag\nSET KEY TO 10\nGO TOP");
        // Only id = 10 rows visible (rec 2 then rec 4).
        Assert.Equal(2m, b.Num("RECNO()"));
        b.Run("SKIP"); Assert.Equal(4m, b.Num("RECNO()"));
        b.Run("SKIP"); Assert.True(b.Bool("EOF()"));

        b.Run("SET KEY TO\nGO BOTTOM");
        // Range cleared → the whole table is visible again; bottom in id order is rec 1 (id 30).
        Assert.Equal(1m, b.Num("RECNO()"));
    }

    [Fact]
    public void Reindex_RebuildsTagsOverLiveDataAfterChange()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag\nSET ORDER TO idtag");
        // Change a key value, then REINDEX; the new key must be seekable afterwards.
        b.Run("GO TOP\nREPLACE id WITH 99\nREINDEX");
        b.Run("=SEEK(99, 'people', 'idtag')");
        Assert.True(b.Bool("FOUND()"));
    }

    // ─────────────────────────── standalone .idx / non-structural .cdx now BUILD (INDEX/ORDER MODEL) ───────────────────────────
    // (These forms USED to be catchable refusals; the multi-index pipeline now implements them — the full
    // behaviour is pinned in MicroVfpIndexModelTests. Here we only confirm the file is produced.)

    [Fact]
    public void IndexOn_ToStandaloneIdxFile_BuildsIdx()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TO people_id");
        Assert.True(File.Exists(Path.Combine(b.Dir, "people_id.idx")),
            "INDEX ON … TO people_id did not create the standalone .idx.");
    }

    [Fact]
    public void IndexOn_TagOfNonStructuralCdx_BuildsCdx()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG x OF other.cdx");
        Assert.True(File.Exists(Path.Combine(b.Dir, "other.cdx")),
            "INDEX ON … TAG x OF other.cdx did not create the non-structural .cdx.");
    }

    // ─────────────────────────── review MUST-FIX regressions ───────────────────────────

    // #1 — SET KEY over an expression tag (UPPER(name)) whose logical KeyType is Unknown must NOT
    // fail OPEN. A non-matching bound shows NOTHING (GO TOP hits EOF); a matching bound shows the row.
    [Fact]
    public void SetKey_OnUpperExprTag_NonMatchingBound_ShowsNothing_MatchingBound_ShowsRow()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON UPPER(name) TAG nm\nSET ORDER TO nm");

        b.Run("SET KEY TO 'ZZZZZZZ'\nGO TOP");
        Assert.True(b.Bool("EOF()"));   // no key equals 'ZZZZZZZ' → the range admits no record.

        b.Run("SET KEY TO 'ALICE'\nGO TOP");
        Assert.False(b.Bool("EOF()"));
        Assert.Equal(2m, b.Num("RECNO()"));   // UPPER(name) of rec 2 ('alice') = 'ALICE'.
    }

    // #2 — SET ORDER TO <tag> DESCENDING must honour the explicit per-call direction (not silently
    // follow the tag's baked-in ascending order). ASCENDING on the same tag restores ascending.
    [Fact]
    public void SetOrderTo_Tag_ExplicitDescending_ReversesTraversal()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag");

        b.Run("SET ORDER TO idtag DESCENDING\nGO TOP");
        Assert.Equal(30m, b.Num("id"));       // highest id first under the DESCENDING override.
        b.Run("GO BOTTOM");
        Assert.Equal(10m, b.Num("id"));       // lowest id last.

        b.Run("SET ORDER TO idtag ASCENDING\nGO TOP");
        Assert.Equal(10m, b.Num("id"));       // explicit ASCENDING → lowest id first again.
    }

    // #3 — INDEX / REINDEX honour SET DELETED: OFF indexes deleted rows (VFP keeps their CDX entries),
    // ON excludes them. The Bench has 4 records; deleting one changes the built tag's entry count.
    [Fact]
    public void IndexOn_HonoursSetDeleted_ForDeletedRowMembership()
    {
        using var b = new Bench();
        // Mark rec 1 deleted (DELETE = NEXT-1 at the current record).
        b.Run("USE people\nGO TOP\nDELETE");

        b.Run("SET DELETED OFF\nINDEX ON id TAG doff");
        Assert.Equal(4, b.TagEntryCount("doff"));   // deleted row still indexed.

        b.Run("SET DELETED ON\nINDEX ON id TAG don");
        Assert.Equal(3, b.TagEntryCount("don"));    // deleted row excluded.
    }

    // #4 — SET ORDER TO an unknown tag / an out-of-range index number raises (VFP 1683), instead of
    // silently degrading to natural order.
    [Fact]
    public void SetOrderTo_UnknownTagOrOutOfRangeNumber_Raises()
    {
        using var b = new Bench();
        b.Run("USE people\nINDEX ON id TAG idtag");

        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("SET ORDER TO nosuchtag"));
        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("SET ORDER TO 99"));

        // Blank / 0 remain natural order (no throw).
        b.Run("SET ORDER TO 0\nGO TOP");
        Assert.Equal(1m, b.Num("RECNO()"));
    }

    // #5 — SET KEY on a GENERAL-collated CHARACTER tag compares on the STORED collated weights, not on
    // weight-bytes decoded back to a bogus string. A matching bound shows the row (previously: empty).
    [Fact]
    public void SetKey_OnGeneralCollatedCharTag_MatchesOnCollatedBytes()
    {
        using var b = new Bench();
        b.Run("USE people\nSET COLLATE TO GENERAL\nINDEX ON name TAG g\nSET ORDER TO g");
        Assert.Equal("GENERAL", b.Tag("g").Collation.ToUpperInvariant());

        b.Run("SET KEY TO 'alice'\nGO TOP");
        Assert.False(b.Bool("EOF()"));        // the GENERAL weight key of 'alice' matches rec 2.
        Assert.Equal(2m, b.Num("RECNO()"));
    }

    // #6 — the live SET EXACT is propagated into KEY/FOR expression evaluation during the build. With
    // SET EXACT ON `city = 'berl'` matches NOTHING; with SET EXACT OFF (the default) it matches the two
    // 'berlin' rows (prefix compare) — so the two builds produce different tag membership.
    [Fact]
    public void IndexOn_ForFilter_HonoursLiveSetExact()
    {
        using var b = new Bench();
        b.Run("SET EXACT ON\nUSE people\nINDEX ON id TAG eon FOR city = 'berl'");
        Assert.Equal(0, b.TagEntryCount("eon"));    // EXACT ON: 'berlin' <> 'berl'.

        b.Run("SET EXACT OFF\nINDEX ON id TAG eoff FOR city = 'berl'");
        Assert.Equal(2, b.TagEntryCount("eoff"));   // EXACT OFF: prefix match → 2 rows.
    }
}
