using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// §D7 — CDX index WRITING (build / rebuild = REINDEX) — the inverse of the Teil C
/// reader. Every test BUILDS a compact compound <c>.cdx</c> for a temp table via
/// <see cref="DbfWriter.CreateTag"/> / <see cref="DbfWriter.Reindex"/> and proves it
/// by ROUND-TRIP through our OWN <see cref="CdxFile"/> reader: the reader's
/// <see cref="CdxTag.EnumerateEntries"/> must yield the records in the correct key
/// order (== a sorted table scan by the KEY value under the tag collation) with the
/// right record numbers, and <see cref="CdxTag.Seek(object)"/> must resolve a known
/// value and report an absent one.
///
/// EDGE / ADVERSARIAL focus (not happy-path): a NEGATIVE numeric (the invert-all-64-bits
/// branch sorts first), a NEGATIVE integer (sign-bit flip), GENERAL collation order
/// (the GetCollatedKey path differs from MACHINE byte order), MULTI-LEAF (a branch
/// level + sibling-chain walk), DESCENDING (reverse traversal over ascending bytes),
/// a FOR-filtered sparse tag, a UNIQUE tag, deleted-record skipping and an EMPTY table.
///
/// SAFETY: every test writes ONLY into a throwaway temp dir (deleted in a finally).
/// No fixture under data/ or vfp_test/ is ever created or written. Expected record
/// orders + decoded values are HARDCODED from the documented §C5 transforms.
/// </summary>
public sealed class CdxWriterTests
{
    // ---- temp plumbing (never touch committed fixtures) ------------------------

    private static string FreshTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_cdxwrite_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// Create a temp table from <paramref name="cols"/>, append <paramref name="rows"/> (one
    /// object?[] per record, physical order), build a tag for every <paramref name="tags"/>
    /// entry, then close the writer. Returns the .dbf / .cdx paths.
    /// </summary>
    private static (string dbf, string cdx) CreateWithTags(
        string dir, DbfColumnDef[] cols, IEnumerable<object?[]> rows, params CdxTagDefinition[] tags)
    {
        string dbf = Path.Combine(dir, "t.dbf");
        using (var w = DbfWriter.Create(dbf, cols))
        {
            foreach (var r in rows)
                w.AppendRecord(r);
            foreach (var t in tags)
                w.CreateTag(t);
        }
        return (dbf, Path.ChangeExtension(dbf, ".cdx"));
    }

    private static int[] Recnos(CdxTag tag)
        => tag.EnumerateEntries().Select(e => (int)e.RecordNumber).ToArray();

    private static List<object?[]> Rows(params object?[][] r) => r.ToList();

    // ====================================================================
    //  CHARACTER tag (MACHINE) — byte order == ASCII, recno tiebreak
    // ====================================================================

    [Fact]
    public void CharacterTag_Machine_EnumeratesInByteOrder_WithRecnoTiebreak()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 20) };
            var rows = Rows(
                new object?[] { "BANANA" },   // recno 1
                new object?[] { "APPLE" },    // recno 2
                new object?[] { "CHERRY" },   // recno 3
                new object?[] { "APPLE" });   // recno 4 (duplicate → dup front-coding + recno tiebreak)

            var (dbf, cdx) = CreateWithTags(dir, cols, rows, new CdxTagDefinition("NAMETAG", "NAME"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("NAMETAG")!;

            // APPLE(2), APPLE(4), BANANA(1), CHERRY(3): equal keys break by ascending recno.
            Assert.Equal(new[] { 2, 4, 1, 3 }, Recnos(tag));

            var decoded = tag.EnumerateEntries().Select(e => tag.DecodeKey(e.Key).AsString).ToArray();
            Assert.Equal(new[] { "APPLE", "APPLE", "BANANA", "CHERRY" }, decoded);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CharacterTag_Seek_ResolvesKnownValue_AndReportsAbsent()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 20) };
            var rows = Rows(
                new object?[] { "BANANA" },
                new object?[] { "APPLE" },
                new object?[] { "CHERRY" },
                new object?[] { "APPLE" });

            var (dbf, cdx) = CreateWithTags(dir, cols, rows, new CdxTagDefinition("NAMETAG", "NAME"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("NAMETAG")!;

            Assert.Equal((uint)1, tag.Seek((object)"BANANA"));   // present → its recno
            Assert.Equal((uint)2, tag.Seek((object)"APPLE"));    // first of the duplicates (lowest recno)
            Assert.Null(tag.Seek((object)"ZEBRA"));              // absent → not found
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  GENERAL collation — order differs from MACHINE (case folding)
    // ====================================================================

    [Fact]
    public void CharacterTag_General_SortsByCollation_NotByRawBytes()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            // MACHINE (ASCII bytes): 'B'(66) < 'C'(67) < 'a'(97) ⇒ [1,3,2].
            // GENERAL (case-insensitive): apple < Banana < Cherry      ⇒ [2,1,3].
            var rows = Rows(
                new object?[] { "Banana" },   // recno 1
                new object?[] { "apple" },    // recno 2
                new object?[] { "Cherry" });  // recno 3

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("GTAG", "NAME", collation: "GENERAL"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("GTAG")!;

            Assert.Equal("GENERAL", tag.Collation);
            Assert.Equal(new[] { 2, 1, 3 }, Recnos(tag));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CharacterTag_General_ValueSeek_UsesNaturalCollationWeights()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            var rows = Rows(
                new object?[] { "BANANA" },
                new object?[] { "APPLE" });

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("GTAG", "NAME", collation: "GENERAL"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("GTAG")!;

            Assert.Equal(IndexKeyType.Character, tag.KeyType);
            Assert.Equal((uint)1, tag.Seek((object)"BANANA"));
            Assert.Equal((uint)1, tag.Seek((object)"BAN"));
            Assert.Null(tag.Seek((object)"MISSING"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CharacterTag_General_RawSeek_RemainsCallerEncoded()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            var rows = Rows(new object?[] { "BANANA" });

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("GTAG", "NAME", collation: "GENERAL"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("GTAG")!;
            byte[] full = VfpCollations.General.GetCollatedKey("BANANA".AsSpan());
            byte[] prefix = VfpCollations.General.GetCollatedKey("BAN".AsSpan());
            byte[] rawLatin1 = System.Text.Encoding.Latin1.GetBytes("BANANA");

            Assert.Equal((uint)1, tag.Seek(full.AsSpan(), exact: true));
            Assert.Null(tag.Seek(prefix.AsSpan(), exact: true));
            Assert.Null(tag.Seek(rawLatin1.AsSpan()));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CharacterTag_Machine_ValueSeek_RemainsCaseSensitive()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            var rows = Rows(new object?[] { "BANANA" });
            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("MTAG", "NAME"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("MTAG")!;

            Assert.Equal((uint)1, tag.Seek((object)"BANANA"));
            Assert.Null(tag.Seek((object)"banana"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void NumericAndIntegerTag_ValueSeek_AndNullRemainSupported()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[]
            {
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("COUNT", 'I', 4),
            };
            var rows = Rows(new object?[] { 12.5m, 7 });
            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("NTAG", "AMOUNT"),
                new CdxTagDefinition("ITAG", "COUNT"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var numeric = cdxFile.Tag("NTAG")!;
            var integer = cdxFile.Tag("ITAG")!;

            Assert.Equal((uint)1, numeric.Seek((object)12.5m));
            Assert.Equal((uint)1, integer.Seek((object)7));
            Assert.Null(numeric.Seek((object)null!));
            Assert.Null(integer.Seek((object)null!));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  NUMERIC tag incl. a NEGATIVE value (invert-all-64-bits sorts first)
    // ====================================================================

    [Fact]
    public void NumericTag_WithNegative_SortsNumerically()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("VAL", 'N', 10, 2) };
            var rows = Rows(
                new object?[] { 5.00m },     // recno 1
                new object?[] { -3.50m },    // recno 2  (negative → must sort FIRST)
                new object?[] { 0.00m },     // recno 3
                new object?[] { 100.25m });  // recno 4

            var (dbf, cdx) = CreateWithTags(dir, cols, rows, new CdxTagDefinition("VTAG", "VAL"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("VTAG")!;

            Assert.Equal(new[] { 2, 3, 1, 4 }, Recnos(tag));

            var vals = tag.EnumerateEntries().Select(e => tag.DecodeKey(e.Key).AsDouble!.Value).ToArray();
            Assert.Equal(-3.50, vals[0], 2);
            Assert.Equal(0.00, vals[1], 2);
            Assert.Equal(5.00, vals[2], 2);
            Assert.Equal(100.25, vals[3], 2);
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  DATE tag
    // ====================================================================

    [Fact]
    public void DateTag_SortsChronologically()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("DT", 'D', 8) };
            var rows = Rows(
                new object?[] { new DateOnly(2020, 6, 15) },   // recno 1
                new object?[] { new DateOnly(2019, 1, 1) },    // recno 2
                new object?[] { new DateOnly(2021, 12, 31) }); // recno 3

            var (dbf, cdx) = CreateWithTags(dir, cols, rows, new CdxTagDefinition("DTAG", "DT"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("DTAG")!;

            Assert.Equal(new[] { 2, 1, 3 }, Recnos(tag));

            var dates = tag.EnumerateEntries().Select(e => tag.DecodeKey(e.Key).AsDate!.Value).ToArray();
            Assert.Equal(new DateOnly(2019, 1, 1), dates[0]);
            Assert.Equal(new DateOnly(2020, 6, 15), dates[1]);
            Assert.Equal(new DateOnly(2021, 12, 31), dates[2]);
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  DATETIME tag (Julian day + fractional day)
    // ====================================================================

    [Fact]
    public void DateTimeTag_SortsByInstant()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("TS", 'T', 8) };
            var rows = Rows(
                new object?[] { new DateTime(2020, 6, 15, 12, 0, 0) },  // recno 1
                new object?[] { new DateTime(2020, 6, 15, 8, 30, 0) },  // recno 2 (same day, earlier)
                new object?[] { new DateTime(2019, 1, 1, 0, 0, 0) });   // recno 3 (earliest)

            var (dbf, cdx) = CreateWithTags(dir, cols, rows, new CdxTagDefinition("TTAG", "TS"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("TTAG")!;

            Assert.Equal(new[] { 3, 2, 1 }, Recnos(tag));

            var dts = tag.EnumerateEntries().Select(e => tag.DecodeKey(e.Key).AsDateTime!.Value).ToArray();
            Assert.True(Math.Abs((new DateTime(2019, 1, 1, 0, 0, 0) - dts[0]).TotalMilliseconds) <= 1);
            Assert.True(Math.Abs((new DateTime(2020, 6, 15, 8, 30, 0) - dts[1]).TotalMilliseconds) <= 1);
            Assert.True(Math.Abs((new DateTime(2020, 6, 15, 12, 0, 0) - dts[2]).TotalMilliseconds) <= 1);
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  INTEGER tag incl. a NEGATIVE value (sign-bit flip)
    // ====================================================================

    [Fact]
    public void IntegerTag_WithNegative_SortsNumerically()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NUM", 'I', 4) };
            var rows = Rows(
                new object?[] { 7 },          // recno 1
                new object?[] { -2 },         // recno 2 (negative → sorts first via sign-bit flip)
                new object?[] { 0 },          // recno 3
                new object?[] { 1_000_000 }); // recno 4

            var (dbf, cdx) = CreateWithTags(dir, cols, rows, new CdxTagDefinition("ITAG", "NUM"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("ITAG")!;

            Assert.Equal(new[] { 2, 3, 1, 4 }, Recnos(tag));

            var ints = tag.EnumerateEntries().Select(e => tag.DecodeKey(e.Key).AsInt32!.Value).ToArray();
            Assert.Equal(new[] { -2, 0, 7, 1_000_000 }, ints);
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  MULTI-LEAF: many records ⇒ several leaf pages + ≥1 branch level
    // ====================================================================

    [Fact]
    public void MultiLeaf_LargeTable_EnumeratesFullyInOrder_AcrossSiblingChain()
    {
        string dir = FreshTempDir();
        try
        {
            const int n = 500;
            var cols = new[] { new DbfColumnDef("KEY", 'C', 8) };

            // Append in REVERSE key order so the physical layout is NOT already sorted:
            // recno r gets key "K{(n+1-r):D5}", so the index must reorder them.
            var rows = new List<object?[]>(n);
            for (int r = 1; r <= n; r++)
                rows.Add(new object?[] { $"K{(n + 1 - r):D5}" });

            var (dbf, cdx) = CreateWithTags(dir, cols, rows, new CdxTagDefinition("KTAG", "KEY"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("KTAG")!;

            var entries = tag.EnumerateEntries().ToList();
            Assert.Equal(n, entries.Count);

            // Sorted ascending by key ⇒ recnos descend from n down to 1.
            Assert.Equal(Enumerable.Range(1, n).Reverse().ToArray(),
                entries.Select(e => (int)e.RecordNumber).ToArray());

            // Keys come out fully ordered "K00001".."K00500".
            Assert.Equal("K00001", tag.DecodeKey(entries[0].Key).AsString);
            Assert.Equal("K00500", tag.DecodeKey(entries[^1].Key).AsString);

            // 500 entries cannot fit one 512-byte leaf ⇒ the root must be a BRANCH node.
            var root = cdxFile.Index.ReadNodeHeader(tag.RootPageOffset);
            Assert.NotNull(root);
            Assert.False(root!.Value.IsLeaf, "a 500-row tag must have a branch (interior) root node");
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  COMPOUND .cdx: several tags, each round-trips
    // ====================================================================

    [Fact]
    public void Compound_ListsAllTagNames_AndEachTagRoundTrips()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[]
            {
                new DbfColumnDef("NAME", 'C', 10),
                new DbfColumnDef("VAL",  'I', 4),
            };
            var rows = Rows(
                new object?[] { "M", 30 },   // recno 1
                new object?[] { "X", 10 },   // recno 2
                new object?[] { "A", 20 });  // recno 3

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("BYNAME", "NAME"),
                new CdxTagDefinition("BYVAL", "VAL"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            Assert.Equal(new[] { "BYNAME", "BYVAL" }, cdxFile.TagNames.OrderBy(s => s).ToArray());

            // BYNAME: A(3) < M(1) < X(2).
            Assert.Equal(new[] { 3, 1, 2 }, Recnos(cdxFile.Tag("BYNAME")!));
            // BYVAL: 10(2) < 20(3) < 30(1).
            Assert.Equal(new[] { 2, 3, 1 }, Recnos(cdxFile.Tag("BYVAL")!));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  DESCENDING: ascending bytes on disk, reverse traversal on read
    // ====================================================================

    [Fact]
    public void DescendingTag_EnumeratesHighToLow()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("VAL", 'N', 10, 2) };
            var rows = Rows(
                new object?[] { 3m },   // recno 1
                new object?[] { 1m },   // recno 2
                new object?[] { 2m });  // recno 3

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("DESCTAG", "VAL", descending: true));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("DESCTAG")!;

            Assert.True(tag.Descending);
            // Ascending would be [2,3,1] (1,2,3); descending reverses ⇒ [1,3,2] (3,2,1).
            Assert.Equal(new[] { 1, 3, 2 }, Recnos(tag));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  FOR-filtered (sparse) tag — only qualifying records are indexed
    // ====================================================================

    [Fact]
    public void ForFilteredTag_IndexesOnlyQualifyingRecords()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("VAL", 'N', 10, 2) };
            var rows = Rows(
                new object?[] { 5m },    // recno 1  (kept)
                new object?[] { -3m },   // recno 2  (filtered)
                new object?[] { 10m },   // recno 3  (kept)
                new object?[] { -1m },   // recno 4  (filtered)
                new object?[] { 2m });   // recno 5  (kept)

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("FTAG", "VAL", forExpression: "VAL > 0"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("FTAG")!;

            // Qualifying sorted ascending: 2(rec5) < 5(rec1) < 10(rec3).
            Assert.Equal(new[] { 5, 1, 3 }, Recnos(tag));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  UNIQUE tag — one entry per distinct key (lowest recno wins)
    // ====================================================================

    [Fact]
    public void UniqueTag_KeepsOneEntryPerKey()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            var rows = Rows(
                new object?[] { "X" },   // recno 1  (kept)
                new object?[] { "Y" },   // recno 2  (kept)
                new object?[] { "X" },   // recno 3  (dropped: duplicate of recno 1)
                new object?[] { "Z" });  // recno 4  (kept)

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("UTAG", "NAME", unique: true));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("UTAG")!;

            Assert.Equal(new[] { 1, 2, 4 }, Recnos(tag));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  DELETED records are skipped
    // ====================================================================

    [Fact]
    public void DeletedRecords_AreNotIndexed()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "t.dbf");
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };

            using (var w = DbfWriter.Create(dbf, cols))
            {
                w.AppendRecord("B");   // recno 1
                w.AppendRecord("A");   // recno 2
                w.AppendRecord("C");   // recno 3
                w.Delete(0);           // logically delete recno 1
                w.CreateTag(new CdxTagDefinition("NTAG", "NAME"));
            }
            string cdx = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("NTAG")!;

            // recno 1 ("B") is deleted ⇒ only A(2) and C(3) are indexed, in order.
            Assert.Equal(new[] { 2, 3 }, Recnos(tag));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  EMPTY table ⇒ a valid root-only (empty) index
    // ====================================================================

    [Fact]
    public void EmptyTable_BuildsValidEmptyIndex()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "t.dbf");
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };

            using (var w = DbfWriter.Create(dbf, cols))
            {
                Assert.Equal(0, w.RecordCount);
                w.CreateTag(new CdxTagDefinition("NTAG", "NAME"));
            }
            string cdx = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            Assert.Contains("NTAG", cdxFile.TagNames);
            var tag = cdxFile.Tag("NTAG")!;
            Assert.Empty(tag.EnumerateEntries());
            Assert.Null(tag.Seek((object)"ANY"));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  REINDEX reproduces the correct enumeration after a data change
    // ====================================================================

    [Fact]
    public void Reindex_AfterChange_ReproducesCorrectOrder()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "t.dbf");
            var cols = new[] { new DbfColumnDef("VAL", 'N', 10, 2) };

            using (var w = DbfWriter.Create(dbf, cols))
            {
                w.AppendRecord(3m);   // recno 1
                w.AppendRecord(1m);   // recno 2
                w.AppendRecord(2m);   // recno 3
                w.CreateTag(new CdxTagDefinition("VTAG", "VAL"));

                // Change recno 1's value from 3 to 0 (now the smallest), then rebuild.
                w.UpdateRecord(0, new object?[] { 0m });
                w.Reindex();
            }
            string cdx = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("VTAG")!;

            // After REINDEX: 0(rec1) < 1(rec2) < 2(rec3).
            Assert.Equal(new[] { 1, 2, 3 }, Recnos(tag));
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  GENERAL over a WIDE field whose collated keys EXCEED the field width
    //  (ligature / diacritic expansion) — KeyLength must be the 2× expanded
    //  width and no tail/diacritic weight byte may be truncated away.
    // ====================================================================

    [Fact]
    public void CharacterTag_General_ExpandsKeyLength_AndDoesNotTruncateCollatedKey()
    {
        string dir = FreshTempDir();
        try
        {
            // C(6): the GENERAL collated key of each value below is LONGER than 6 bytes —
            // "STRAßE" → S T R A S S E (ß expands to SS) = 7 head bytes; "JMÖLCK" → 6 heads
            // + 1 diacritic tail = 7 bytes. With the field-width key length (6) the builder
            // would chop the trailing weight bytes and mis-order the tag; VFP reserves 2×.
            var cols = new[] { new DbfColumnDef("NAME", 'C', 6) };
            var rows = Rows(
                new object?[] { "JMÖLCK" },  // recno 1  (O-umlaut) -> 6b 6f 72 6d 62 6c 04
                new object?[] { "STRAßE" },  // recno 2  (ringel-s) -> 76 77 75 60 76 76 66
                new object?[] { "ADMIN" },        // recno 3             -> 60 64 6f 6a 70
                new object?[] { "Æ" });      // recno 4  (AE-lig)   -> 60 66

            var (dbf, cdx) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("GWIDE", "NAME", collation: "GENERAL"));

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var tag = cdxFile.Tag("GWIDE")!;

            Assert.Equal("GENERAL", tag.Collation);
            // KeyLength is the 2× expanded width (matches the VFP9-built vfp_test/ligtest.CDX
            // formula: C(10) GENERAL ⇒ 20), NOT the raw field width of 6.
            Assert.Equal(12, tag.KeyLength);

            // Collated order: ADMIN(60 64) < Æ(60 66) < JMÖLCK(6b) < STRAßE(76).
            Assert.Equal(new[] { 3, 4, 1, 2 }, Recnos(tag));

            // The two over-width keys must survive WHOLE (7 weight bytes), padded to 12 with 0x00 (VFP9 pads a
            // weighted key with 0x00) — proof the variable-length collated key was not truncated to the field width.
            var byRec = tag.EnumerateEntries().ToDictionary(e => (int)e.RecordNumber, e => e.Key);
            Assert.Equal(
                new byte[] { 0x76, 0x77, 0x75, 0x60, 0x76, 0x76, 0x66, 0x00, 0x00, 0x00, 0x00, 0x00 },
                byRec[2]);
            Assert.Equal(
                new byte[] { 0x6b, 0x6f, 0x72, 0x6d, 0x62, 0x6c, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00 },
                byRec[1]);
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData("GENERAL")]
    [InlineData("SPANISH")]
    public void CharacterTag_Weighted_PadsWithZero_PrefixKeysSortShorterFirst_BulkAndIncremental(string collation)
    {
        // VFP9 pads a weighted key with 0x00 (MACHINE: 0x20). So a key that is a prefix of another sorts first
        // (Aa = 60 60 before áa = 60 60 01, Muller before Müller, the empty key before everything) and the trailing
        // weight 0x20 of '/' is data. VFP9 SEEK (EXACT OFF) compares only the head weights of the needle.
        string dir = FreshTempDir();
        try
        {
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            string[] values = { "áa", "Aa", "Müller", "Muller", "", "ab/", "ab", "/" };
            var (dbf, cdx) = CreateWithTags(dir, cols, values.Select(v => new object?[] { v }),
                new CdxTagDefinition("W", "NAME", collation: collation),
                new CdxTagDefinition("M", "NAME"));

            string incr = Path.Combine(dir, "incr.dbf");
            using (var w = DbfWriter.Create(incr, cols))
            {
                w.CreateTag(new CdxTagDefinition("W", "NAME", collation: collation));
                w.CreateTag(new CdxTagDefinition("M", "NAME"));
            }
            using (var w = DbfWriter.Open(incr))
            {
                foreach (var v in values) w.AppendRecord(v == "Aa" ? "zz" : v);
                w.UpdateRecord(1, new object?[] { "Aa" });   // delete + re-insert in both trees
            }

            foreach (var path in new[] { dbf, incr })
            {
                using var table = DbfTable.Open(path);
                using var cdxFile = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
                var w = cdxFile.Tag("W")!;
                Assert.Equal(new[] { 5, 8, 2, 1, 7, 6, 4, 3 }, Recnos(w));
                var byRec = w.EnumerateEntries().ToDictionary(e => (int)e.RecordNumber, e => e.Key);
                Assert.Equal(new byte[20], byRec[5]);
                Assert.Equal(new byte[] { 0x20 }.Concat(new byte[19]).ToArray(), byRec[8]);
                Assert.Equal(new byte[] { 0x60, 0x61, 0x20 }.Concat(new byte[17]).ToArray(), byRec[6]);

                Assert.Equal(2u, w.Seek("áa"));                                    // heads only: lands on Aa
                Assert.Equal(1u, w.Seek(w.EncodeCharacterKey("áa"), exact: true)); // full key: áa itself
                Assert.Equal(4u, w.Seek("Müller"));
                Assert.Equal(4u, w.Seek("Mü"));
                Assert.Equal(3u, w.Seek(w.EncodeCharacterKey("Müller"), exact: true));
                Assert.Null(w.Seek(w.EncodeCharacterKey("Mü"), exact: true));

                // MACHINE keeps the blank pad.
                var m = cdxFile.Tag("M")!;
                Assert.Equal(new[] { 5, 8, 2, 4, 3, 7, 6, 1 }, Recnos(m));
                Assert.Equal(Enumerable.Repeat((byte)0x20, 10).ToArray(),
                    m.EnumerateEntries().Single(e => e.RecordNumber == 5).Key);
                Assert.Equal(3u, m.Seek("Müller"));
            }
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  Structural-CDX header flag (byte 28 bit 0) + Pack invalidation:
    //  CreateTag on a fresh table must advertise the new structural .cdx
    //  so VFP auto-opens it, and a later Pack must invalidate it (delete +
    //  ReindexNeeded) instead of leaving a stale sidecar behind.
    // ====================================================================

    [Fact]
    public void CreateTag_OnFreshTable_SetsStructuralCdxHeaderFlag()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "t.dbf");
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            using (var w = DbfWriter.Create(dbf, cols))
            {
                w.AppendRecord(new object?[] { "ALPHA" });
                w.AppendRecord(new object?[] { "BETA" });
                w.AppendRecord(new object?[] { "GAMMA" });
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
            }

            Assert.True(File.Exists(Path.ChangeExtension(dbf, ".cdx")));
            // Fresh non-memo table is created with header byte 28 = 0x00; CreateTag must OR in 0x01
            // (the structural-CDX flag) so a real VFP runtime auto-opens the sidecar on USE.
            byte flagByte = File.ReadAllBytes(dbf)[28];
            Assert.True((flagByte & 0x01) != 0);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void CreateTag_ThenPack_RaisesReindexNeeded_AndRemovesStaleCdx()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "t.dbf");
            string cdx = Path.ChangeExtension(dbf, ".cdx");
            var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
            // §D2: PACK requires an EXCLUSIVE open (VFP err 110 otherwise) — create the writer exclusive.
            using (var w = DbfWriter.Create(dbf, cols, new DbfCreateOptions { LockMode = LockMode.Exclusive }))
            {
                w.AppendRecord(new object?[] { "ALPHA" });
                w.AppendRecord(new object?[] { "BETA" });   // recno 2 → deleted below
                w.AppendRecord(new object?[] { "GAMMA" });
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));

                Assert.True(File.Exists(cdx));
                Assert.False(w.ReindexNeeded);

                w.Delete(1); // mark BETA deleted so Pack physically compacts

                // Pack must now recognise the table OWNS a structural .cdx (CreateTag flipped the
                // tracking flag): delete the now-stale sidecar and raise ReindexNeeded — rather than
                // early-returning on a stale _hasStructuralCdx and leaving a broken index on disk.
                w.Pack();

                Assert.True(w.ReindexNeeded);
                Assert.False(File.Exists(cdx));
            }
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void ExpressionPool_Exactly512Bytes_IsAccepted()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "boundary.dbf");
            string forExpression = ".T." + new string(' ', 503); // NAME + NUL + FOR + NUL = 512
            using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("NAME", 'C', 8)]))
            {
                writer.AppendRecord(["ALPHA"]);
                writer.CreateTag(new CdxTagDefinition("BOUNDARY", "NAME", forExpression));
            }

            using var table = DbfTable.Open(dbf);
            using var cdx = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table);
            Assert.NotNull(cdx.Tag("BOUNDARY"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void ExpressionPool_513Bytes_IsRejectedBeforeCdxEmission()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "overflow.dbf");
            string cdx = Path.ChangeExtension(dbf, ".cdx");
            string forExpression = ".T." + new string(' ', 504); // NAME + NUL + FOR + NUL = 513
            using var writer = DbfWriter.Create(dbf, [new DbfColumnDef("NAME", 'C', 8)]);
            writer.AppendRecord(["ALPHA"]);

            var error = Assert.Throws<DbfWriteException>(() =>
                writer.CreateTag(new CdxTagDefinition("OVERFLOW", "NAME", forExpression)));

            Assert.Contains("512-byte expression pool", error.Message);
            Assert.False(File.Exists(cdx));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void DescendingSeekFindsExactValue_AndExactCompositeRejectsBlankPaddedPrefix()
    {
        string dir = FreshTempDir();
        try
        {
            var cols = new[]
            {
                new DbfColumnDef("NAME", 'C', 3),
                new DbfColumnDef("CODE", 'C', 2),
            };
            var rows = Rows(
                new object?[] { "AB", "X" },
                new object?[] { "ZZ", "Y" });
            var (dbf, cdxPath) = CreateWithTags(dir, cols, rows,
                new CdxTagDefinition("SINGLE", "NAME"),
                new CdxTagDefinition("COMPOSITE", "NAME + CODE"),
                new CdxTagDefinition("DESCNAME", "NAME", descending: true));

            using var table = DbfTable.Open(dbf);
            using var cdx = CdxFile.Open(cdxPath, table);
            byte[] needle = Encoding.ASCII.GetBytes("AB");

            var descending = cdx.Tag("DESCNAME")!;
            Assert.True(descending.Descending);
            Assert.Equal((uint)1, descending.Seek((object)"AB"));

            Assert.Equal((uint)1, cdx.Tag("SINGLE")!.Seek(needle, exact: true));
            Assert.Equal((uint)1, cdx.Tag("COMPOSITE")!.Seek(needle, exact: false));
            Assert.Null(cdx.Tag("COMPOSITE")!.Seek(needle, exact: true));
        }
        finally { Cleanup(dir); }
    }
}
