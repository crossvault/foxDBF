using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Project-review finding 5.6 — PAGE-LEVEL tag DDL. Today's <see cref="DbfWriter.CreateTag"/> /
/// <see cref="DbfWriter.DeleteTagsIn"/> whole-file rebuild re-reads EVERY record and rebuilds ALL tags even
/// to delete or add ONE. The fast paths must instead:
/// <list type="bullet">
///   <item>DELETE TAG — unlink the tag's directory entry and abandon its pages, leaving every SURVIVING tag's
///   tree byte-for-byte untouched and NEVER reading a table row;</item>
///   <item>CREATE TAG (new name) — build ONLY the new tag's tree (one scan for its keys), append it at the
///   file end and splice its directory entry, leaving the SIBLING tags byte-for-byte untouched.</item>
/// </list>
///
/// RED-vs-today contract pins (fail against the rebuild-everything behaviour):
///  • DELETE one-of-N: a survivor that FOLLOWS the deleted tag in creation order RELOCATES under a full
///    rebuild (different page offsets + sibling pointers) — the fast path leaves its pages identical.
///  • The structural guard: the DELETE fast path completes with row materialization DISABLED (a full rebuild
///    throws); a timing assertion is deliberately avoided.
/// The equivalence checks (enumeration/seek unchanged, tag set correct) are the NET — some already hold under
/// today's rebuild, which is fine.
///
/// SAFETY: every test writes ONLY into a throwaway temp dir (deleted in a finally). No committed fixture is
/// ever touched.
/// </summary>
public sealed class CdxTagDdlTests
{
    // ---- temp plumbing ---------------------------------------------------------

    private static string FreshTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_tagddl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static readonly DbfColumnDef[] ThreeCharCols =
    {
        new("A", 'C', 6),
        new("B", 'C', 6),
        new("C", 'C', 6),
    };

    /// <summary>Six rows whose A/B/C values are all distinct across records so each single-field tag has a
    /// non-trivial, fully-ordered entry set (recno tiebreaks never collapse them).</summary>
    private static readonly object?[][] SixRows =
    {
        new object?[] { "A3", "B5", "C2" },   // recno 1
        new object?[] { "A1", "B2", "C6" },   // recno 2
        new object?[] { "A5", "B1", "C4" },   // recno 3
        new object?[] { "A2", "B6", "C1" },   // recno 4
        new object?[] { "A6", "B4", "C5" },   // recno 5
        new object?[] { "A4", "B3", "C3" },   // recno 6
    };

    /// <summary>Create a temp table with <see cref="ThreeCharCols"/> + <see cref="SixRows"/> and build the
    /// three tags TA(A), TB(B), TC(C) in that creation order. Returns the .dbf + .cdx paths.</summary>
    private static (string dbf, string cdx) BuildThreeTagTable(string dir)
    {
        string dbf = Path.Combine(dir, "t.dbf");
        using (var w = DbfWriter.Create(dbf, ThreeCharCols))
        {
            foreach (var r in SixRows)
                w.AppendRecord(r);
            w.CreateTag(new CdxTagDefinition("TA", "A"));   // first tag → whole-file build
            w.CreateTag(new CdxTagDefinition("TB", "B"));   // fast append
            w.CreateTag(new CdxTagDefinition("TC", "C"));   // fast append
        }
        return (dbf, Path.ChangeExtension(dbf, ".cdx"));
    }

    /// <summary>37 tag names, each 10 chars with a DISTINCT first byte ('0'-'9', 'A'-'Z', '_') so no two share a
    /// prefix — the directory leaf then packs at the maximum 13 bytes/entry (kby=3, no dup/trail compression) and
    /// 37 of them nearly fill a single 512-byte leaf. Widening the recno field to kby=4 (14 B/entry) would
    /// overflow it — the exact near-full geometry the 5.6 MUST-FIX regression needs.</summary>
    private static string[] ManyDistinctTagNames()
    {
        var firsts = new List<char>();
        for (char c = '0'; c <= '9'; c++) firsts.Add(c);   // 10
        for (char c = 'A'; c <= 'Z'; c++) firsts.Add(c);   // 26
        firsts.Add('_');                                    // 1 → 37
        return firsts.Select(c => c + "ZZZZZZZZZ").ToArray();
    }

    /// <summary>A deterministic 40-char all-letters key with almost no shared prefix — an "anti-compressible"
    /// key so each tag tree splits often and a modest row count grows the cdx well past the 64 KiB record-field
    /// band (via 5.1 row-append maintenance) WITHOUT the directory being edited.</summary>
    private static string AntiCompressibleKey(Random rng)
    {
        var chars = new char[40];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = (char)('A' + rng.Next(26));
        return new string(chars);
    }

    private static int[] Recnos(CdxTag tag) => tag.EnumerateEntries().Select(e => (int)e.RecordNumber).ToArray();

    private static string[] TagNamesSorted(CdxFile cdx) => cdx.TagNames.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    /// <summary>Canonical (offset → page-bytes) fingerprint of every page reachable from a tag's B-tree root:
    /// a sorted list of <c>offset:hex</c> strings. Two runs match iff the tag's tree occupies the SAME page
    /// offsets AND every page (keys, recnos, sibling pointers, geometry) is byte-identical — exactly the
    /// "surviving/sibling tag untouched" contract, and provably different after a whole-file rebuild relocates
    /// the tag.</summary>
    private static List<string> TreeFingerprint(CdxFile cdx, CdxTag tag)
    {
        var idx = cdx.Index;
        var fp = new List<string>();
        var seen = new HashSet<long>();
        var stack = new Stack<long>();
        stack.Push(tag.RootPageOffset);
        while (stack.Count > 0)
        {
            long off = stack.Pop();
            if (!seen.Add(off)) continue;
            var page = idx.ReadPage(off);
            if (page is null) continue;
            fp.Add($"{off}:{Convert.ToHexString(page)}");
            var hdr = idx.ReadNodeHeader(off);
            if (hdr is { } h && !h.IsLeaf)
                foreach (var e in idx.ReadBranchEntries(off, tag.KeyLength))
                    stack.Push(e.ChildPointer);
        }
        fp.Sort(StringComparer.Ordinal);
        return fp;
    }

    // ====================================================================
    //  (1) DELETE TAG one-of-N — survivors untouched, deleted tag gone
    // ====================================================================

    [Fact]
    public void DeleteTag_OneOfThree_LeavesSurvivorsByteIdentical_AndRemovesTheTag()
    {
        string dir = FreshTempDir();
        try
        {
            var (dbf, cdx) = BuildThreeTagTable(dir);

            // BEFORE: fingerprint + enumeration of the survivors. TC FOLLOWS the deleted TB in creation order,
            // so a whole-file rebuild would relocate it — that is the RED-vs-today signal.
            List<string> fpTaBefore, fpTcBefore;
            int[] recTaBefore, recTcBefore;
            using (var table = DbfTable.Open(dbf))
            using (var before = CdxFile.Open(cdx, table))
            {
                Assert.Equal(new[] { "TA", "TB", "TC" }, TagNamesSorted(before));
                fpTaBefore = TreeFingerprint(before, before.Tag("TA")!);
                fpTcBefore = TreeFingerprint(before, before.Tag("TC")!);
                recTaBefore = Recnos(before.Tag("TA")!);
                recTcBefore = Recnos(before.Tag("TC")!);
            }

            // Unlink the MIDDLE tag via the writer's DELETE-TAG surface.
            using (var w = DbfWriter.Open(dbf))
            {
                int remaining = w.DeleteTagsIn(cdx, structural: true, new[] { "TB" });
                Assert.Equal(2, remaining);
            }

            // AFTER: TB gone, TA + TC survive; both survivors' trees are byte-for-byte identical (same offsets,
            // same pages) and enumerate identically; both remain seekable.
            using (var table = DbfTable.Open(dbf))
            using (var after = CdxFile.Open(cdx, table))
            {
                Assert.Equal(new[] { "TA", "TC" }, TagNamesSorted(after));
                Assert.Null(after.Tag("TB"));

                Assert.Equal(fpTaBefore, TreeFingerprint(after, after.Tag("TA")!));
                Assert.Equal(fpTcBefore, TreeFingerprint(after, after.Tag("TC")!));

                Assert.Equal(recTaBefore, Recnos(after.Tag("TA")!));
                Assert.Equal(recTcBefore, Recnos(after.Tag("TC")!));

                Assert.Equal((uint)3, after.Tag("TA")!.Seek((object)"A5"));   // recno 3
                Assert.Equal((uint)5, after.Tag("TC")!.Seek((object)"C5"));   // recno 5
                Assert.Null(after.Tag("TA")!.Seek((object)"ZZ"));
            }
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void DeleteTag_LastTag_DeletesFile_AndClearsStructuralHeaderFlag()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "one.dbf");
            string cdx = Path.ChangeExtension(dbf, ".cdx");
            using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("NM", 'C', 8) }))
            {
                w.AppendRecord("ALPHA");
                w.AppendRecord("BETA");
                w.CreateTag(new CdxTagDefinition("ONLYTAG", "NM"));
            }
            Assert.True(File.Exists(cdx));
            Assert.True((File.ReadAllBytes(dbf)[28] & 0x01) != 0);   // structural bit advertised

            using (var w = DbfWriter.Open(dbf))
            {
                int remaining = w.DeleteTagsIn(cdx, structural: true, new[] { "ONLYTAG" });
                Assert.Equal(0, remaining);
            }

            Assert.False(File.Exists(cdx));                          // last tag → file deleted (VFP behaviour)
            Assert.True((File.ReadAllBytes(dbf)[28] & 0x01) == 0);   // structural bit cleared
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void DeleteTag_NonExistentName_DoesNotThrow_AndLeavesTagSetUnchanged()
    {
        string dir = FreshTempDir();
        try
        {
            var (dbf, cdx) = BuildThreeTagTable(dir);

            using (var w = DbfWriter.Open(dbf))
            {
                // A name that matches no tag: behaves as today (silently ignored — the surviving set is the
                // full existing set), no exception.
                int remaining = w.DeleteTagsIn(cdx, structural: true, new[] { "NOPE" });
                Assert.Equal(3, remaining);
            }

            using var table = DbfTable.Open(dbf);
            using var after = CdxFile.Open(cdx, table);
            Assert.Equal(new[] { "TA", "TB", "TC" }, TagNamesSorted(after));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  (1b) DELETE TAG — MUST-FIX: directory recno geometry is DATA-derived,
    //       not file-length-derived (near-full leaf + cdx grown past a band)
    // ====================================================================

    /// <summary>
    /// Project-review 5.6 MUST-FIX (CONFIRMED live repro). A near-full single directory leaf (37
    /// distinct-first-byte 10-char tag names ⇒ 13 B/entry at kby=3) plus a cdx GROWN PAST the 64 KiB
    /// record-field band by ROW APPENDS ONLY — 5.1 maintenance splits the tag trees and appends pages, the tag
    /// DIRECTORY is never edited, so its leaf stays packed at kby=3 on disk while the FILE LENGTH crosses into
    /// the kby=4 band. The buggy fast paths sized the directory recno field from <c>rw.Length</c>: the delete
    /// then re-packed the leaf one byte-per-entry WIDER, overflowed it, and <c>CdxTreeEditor.DeleteNode</c>'s
    /// <c>image[0]</c>-drop SILENTLY LOST the tail survivors (tag count fell below 36). The DATA-derived basis
    /// (max tag-header offset ≈ 56 KiB, an 8-bit band below the file length) keeps kby=3, so the delete never
    /// overflows and EVERY survivor is intact — asserted at the strongest level: each survivor's tree is
    /// byte-for-byte identical and enumerates unchanged.
    /// </summary>
    [Fact]
    public void DeleteTag_NearFullDirectory_AfterCdxGrewPastRecnoBand_KeepsEverySurvivor()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "many.dbf");
            string cdx = Path.ChangeExtension(dbf, ".cdx");
            string[] names = ManyDistinctTagNames();
            Assert.Equal(37, names.Length);

            // Create the 37 tags on the EMPTY table so their header pages sit at compact offsets (max ≈ 56 KiB,
            // BELOW the 64 KiB band the file length will reach): the directory leaf packs at kby=3.
            using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("K", 'C', 40) }))
                foreach (var n in names)
                    w.CreateTag(new CdxTagDefinition(n, "K"));

            // Grow the cdx PAST 64 KiB with row appends only (directory untouched).
            using (var w = DbfWriter.Open(dbf))
            {
                var rng = new Random(20250703);
                for (int i = 0; i < 120; i++)
                    w.AppendRecord(AntiCompressibleKey(rng));
            }

            // Band guard: if the cdx no longer crosses the 64 KiB band the row count is too low and the test
            // would stop exercising the bug (a length-derived basis must land in a WIDER band than the tags).
            long cdxLen = new FileInfo(cdx).Length;
            Assert.True(cdxLen > 65536, $"cdx must exceed the 64 KiB recno band to exercise the fix (was {cdxLen}).");

            // Snapshot every survivor's tree (byte fingerprint + entry enumeration) BEFORE the delete.
            string victim = names[18];   // an interior tag; deleting it re-packs the whole single directory leaf
            var survivorNames = names.Where(n => n != victim).ToArray();
            Assert.Equal(36, survivorNames.Length);
            var fpBefore = new Dictionary<string, List<string>>();
            var recBefore = new Dictionary<string, int[]>();
            using (var table = DbfTable.Open(dbf))
            using (var before = CdxFile.Open(cdx, table))
            {
                Assert.Equal(37, before.TagNames.Count);
                foreach (var n in survivorNames)
                {
                    fpBefore[n] = TreeFingerprint(before, before.Tag(n)!);
                    recBefore[n] = Recnos(before.Tag(n)!);
                }
            }

            // Unlink ONE tag through the fast path.
            using (var w = DbfWriter.Open(dbf))
                Assert.Equal(36, w.DeleteTagsIn(cdx, structural: true, new[] { victim }));

            // Exactly the 36 survivors remain; each survivor's tree is byte-for-byte untouched and enumerates
            // identically. (Under the bug the tag count silently fell below 36 — tail survivors vanished.)
            using (var table = DbfTable.Open(dbf))
            using (var after = CdxFile.Open(cdx, table))
            {
                Assert.Null(after.Tag(victim));
                Assert.Equal(36, after.TagNames.Count);
                Assert.Equal(
                    survivorNames.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    after.TagNames.OrderBy(s => s, StringComparer.Ordinal).ToArray());
                foreach (var n in survivorNames)
                {
                    var tag = after.Tag(n);
                    Assert.NotNull(tag);
                    Assert.Equal(fpBefore[n], TreeFingerprint(after, tag!));
                    Assert.Equal(recBefore[n], Recnos(tag!));
                }
            }
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  (2) CREATE TAG new name / replace / maintenance after unlink
    // ====================================================================

    [Fact]
    public void CreateTag_NewName_OnMultiTagCdx_LeavesSiblingsUntouched_AndNewTagSeekable()
    {
        string dir = FreshTempDir();
        try
        {
            var (dbf, cdx) = BuildThreeTagTable(dir);

            List<string> fpTaBefore, fpTbBefore, fpTcBefore;
            using (var table = DbfTable.Open(dbf))
            using (var before = CdxFile.Open(cdx, table))
            {
                fpTaBefore = TreeFingerprint(before, before.Tag("TA")!);
                fpTbBefore = TreeFingerprint(before, before.Tag("TB")!);
                fpTcBefore = TreeFingerprint(before, before.Tag("TC")!);
            }

            using (var w = DbfWriter.Open(dbf))
                w.CreateTag(new CdxTagDefinition("TD", "A"));   // NEW name, keyed on A (fast append)

            using (var table = DbfTable.Open(dbf))
            using (var after = CdxFile.Open(cdx, table))
            {
                Assert.Equal(new[] { "TA", "TB", "TC", "TD" }, TagNamesSorted(after));

                // Siblings' trees byte-for-byte untouched (equivalence net + the fast-path guarantee).
                Assert.Equal(fpTaBefore, TreeFingerprint(after, after.Tag("TA")!));
                Assert.Equal(fpTbBefore, TreeFingerprint(after, after.Tag("TB")!));
                Assert.Equal(fpTcBefore, TreeFingerprint(after, after.Tag("TC")!));

                // The new tag is complete: it enumerates the same recno order as the existing A tag TA, and
                // seeks known values / rejects an absent one.
                var td = after.Tag("TD")!;
                Assert.Equal(Recnos(after.Tag("TA")!), Recnos(td));
                Assert.Equal((uint)2, td.Seek((object)"A1"));   // recno 2
                Assert.Equal((uint)5, td.Seek((object)"A6"));   // recno 5
                Assert.Null(td.Seek((object)"ZZ"));
            }
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CreateTag_ReplaceExistingName_ReplacesKey_OldGone_NewCorrect()
    {
        string dir = FreshTempDir();
        try
        {
            var (dbf, cdx) = BuildThreeTagTable(dir);

            int[] recTcBefore;
            using (var table = DbfTable.Open(dbf))
            using (var before = CdxFile.Open(cdx, table))
                recTcBefore = Recnos(before.Tag("TC")!);   // TC currently keyed on C

            // REPLACE TC's key expression: same tag name, now keyed on A.
            using (var w = DbfWriter.Open(dbf))
                w.CreateTag(new CdxTagDefinition("TC", "A"));

            using (var table = DbfTable.Open(dbf))
            using (var after = CdxFile.Open(cdx, table))
            {
                Assert.Equal(new[] { "TA", "TB", "TC" }, TagNamesSorted(after));
                var tc = after.Tag("TC")!;
                Assert.Equal("a", tc.KeyExpression.Trim());   // VFP stores the decompiled KEY: field names lower-case

                // The old C-order is gone; TC now enumerates in A order (== the A tag TA), and seeks A values.
                Assert.Equal(Recnos(after.Tag("TA")!), Recnos(tc));
                Assert.NotEqual(recTcBefore, Recnos(tc));
                Assert.Equal((uint)4, tc.Seek((object)"A2"));   // recno 4
            }
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CreateTag_ReplaceOnlyTag_WhenFastInsertFails_RebuildsFromPreMutationSnapshot()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "one.dbf");
            string cdx = Path.ChangeExtension(dbf, ".cdx");
            using (var seed = DbfWriter.Create(dbf, ThreeCharCols))
            {
                foreach (var r in SixRows)
                    seed.AppendRecord(r);
                seed.CreateTag(new CdxTagDefinition("TONE", "C"));
            }

            using (var w = DbfWriter.Open(dbf))
            {
                w.FailFastTagDirectoryInsertForTests = true;
                w.CreateTag(new CdxTagDefinition("TONE", "A"));
            }

            using var table = DbfTable.Open(dbf);
            using var after = CdxFile.Open(cdx, table);
            Assert.Equal(new[] { "TONE" }, TagNamesSorted(after));

            var tone = after.Tag("TONE");
            Assert.NotNull(tone);
            Assert.Equal("a", tone!.KeyExpression.Trim());   // VFP stores the decompiled KEY: field names lower-case
            Assert.Equal(new[] { 2, 4, 1, 6, 3, 5 }, Recnos(tone));
            Assert.Equal((uint)4, tone.Seek((object)"A2"));
            Assert.Null(tone.Seek((object)"C1"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void IncrementalMaintenance_StillWorks_OnCdxWithDeadPages_FromAnUnlink()
    {
        string dir = FreshTempDir();
        try
        {
            var (dbf, cdx) = BuildThreeTagTable(dir);

            // Unlink TB → leaves DEAD pages (TB's abandoned header + tree) in the middle of the file.
            using (var w = DbfWriter.Open(dbf))
                Assert.Equal(2, w.DeleteTagsIn(cdx, structural: true, new[] { "TB" }));

            // Append a new record through the writer: 5.1 incremental maintenance must edit the surviving tags
            // in place, allocating any split pages at the file END — past the dead pages, no collision. Then a
            // further CREATE-TAG fast-appends onto the same dead-page file.
            using (var w = DbfWriter.Open(dbf))
            {
                w.AppendRecord("A7", "B7", "C7");             // recno 7 — new keys for TA (A) and TC (C)
                w.CreateTag(new CdxTagDefinition("TD", "C")); // fast append onto the dead-page cdx
            }

            using (var table = DbfTable.Open(dbf))
            using (var after = CdxFile.Open(cdx, table))
            {
                Assert.Equal(new[] { "TA", "TC", "TD" }, TagNamesSorted(after));

                // Maintenance added recno 7 to the survivors; SEEK finds the appended keys.
                Assert.Equal((uint)7, after.Tag("TA")!.Seek((object)"A7"));
                Assert.Equal((uint)7, after.Tag("TC")!.Seek((object)"C7"));
                Assert.Contains(7, Recnos(after.Tag("TA")!));
                Assert.Contains(7, Recnos(after.Tag("TC")!));

                // The freshly appended tag covers every record incl. the maintained one.
                Assert.Equal(7, Recnos(after.Tag("TD")!).Length);
                Assert.Equal((uint)7, after.Tag("TD")!.Seek((object)"C7"));
            }
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  (3) Structural guard — DELETE fast path performs NO row materialization
    // ====================================================================

    [Fact]
    public void DeleteTag_FastPath_PerformsNoRowMaterialization()
    {
        string dir = FreshTempDir();
        try
        {
            var (dbf, cdx) = BuildThreeTagTable(dir);

            using var w = DbfWriter.Open(dbf);

            // Arm the seam: any table-row materialization now throws. This models "the record stream is
            // inaccessible" without a flaky timing assertion.
            w.FailMaterializeRowsForTests = true;

            // Positive control: a whole-file rebuild (REINDEX) DOES read rows → it throws.
            Assert.Throws<InvalidOperationException>(() => w.Reindex());

            // The DELETE-TAG fast path must NOT read a single row: it completes and reports the survivor count
            // even with materialization disabled.
            int remaining = w.DeleteTagsIn(cdx, structural: true, new[] { "TB" });
            Assert.Equal(2, remaining);

            // ... and it really did unlink TB (directory correct) without ever touching the table.
            using var table = DbfTable.Open(dbf);
            using var after = CdxFile.Open(cdx, table);
            Assert.Equal(new[] { "TA", "TC" }, TagNamesSorted(after));
            Assert.Null(after.Tag("TB"));
        }
        finally { Cleanup(dir); }
    }
}
