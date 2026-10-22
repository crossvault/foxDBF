using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

// =====================================================================================
// §12.1 (HIGHLIKE_ENGINE_DESIGN) — extend the Rushmore SEEK-based candidate build to
// CHARACTER keys. The ORDERED-key seek already landed (QueryOptimizerSeekTests); CHARACTER
// keys were deliberately left on the O(n) full scan. But CHARACTER index keys are stored
// COLLATION-ORDERED (sorted by the active collation's GetCollatedKey bytes — MACHINE
// identity / GENERAL byte-exact), so a character comparison maps to a CONTIGUOUS collated-
// key range that can be reached by a SEEK (binary search over the cached sorted directory
// on the warm path; CdxTag seek + forward leaf walk on the cold path), giving
// O(log n + matches) instead of O(n).
//
// THESE TESTS COME IN TWO HALVES:
//   (1) RESULT-INVARIANCE (the hard gate, must ALWAYS be GREEN — before and after the fix):
//       for every seekable CHARACTER shape the optimizer result is EXACTLY a full scan ==
//       the prior behaviour, with + without the Highlike warm-cache entrySource, under
//       BOTH MACHINE and GENERAL collation, SET EXACT on/off, SET DELETED on/off, for
//       prefix (EXACT OFF) + exact (EXACT ON) equality, ranges/BETWEEN-style windows,
//       one-sided, a UPPER(field) FUNCTION key, accented / case / ligature (ß/ä/œ) values
//       under GENERAL, DUPLICATE keys (all matching recnos returned), boundaries, no-match,
//       all-match, and <> (which legitimately stays on the scan, still correct). The fix
//       only changes HOW candidates are found, never WHICH — so these never move.
//   (2) O(log n) PROOF (the RED gate — FAILS until the char seek lands): a deterministic,
//       timing-free entry-VISIT counter (the onIndexEntryExamined instrumentation seam)
//       proves a selective CHARACTER equality/prefix/range/one-sided leaf on a large table
//       (120k rows) examines ~O(log n + matches) entries, FAR below n. TODAY the character
//       build scans every entry (~n), so these FAIL. <> stays a scan (control, green).
//
// SAFETY: every table is built fresh under the OS TEMP dir via the writer and deleted on
// dispose. No committed fixture is read or mutated. No new dependencies.
// =====================================================================================

/// <summary>Shared brute-force oracle (mirrors QueryOptimizer's residual semantics exactly).</summary>
internal static class CharSeekOracle
{
    private sealed class Row : IRowContext
    {
        private readonly DbfRecord _rec;
        public Row(DbfRecord rec, int recNo, int recCount) { _rec = rec; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name) => _rec.GetExpressionValue(name);
    }

    /// <summary>
    /// The ground truth — a literal full scan over every PHYSICAL record (recno = physical
    /// position), with the implicit AND NOT DELETED() applied only under SET DELETED ON. This
    /// is what BOTH the old full-scan char build and the new char seek build must equal.
    /// </summary>
    public static List<int> BruteForce(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            if (ctx.Deleted && rec.IsDeleted) continue;
            var v = compiled(new Row(rec, recno, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical)
                hits.Add(recno);
        }
        return hits;
    }
}

// =====================================================================================
// (1) RESULT-INVARIANCE — the optimizer result EQUALS a full scan, for every CHARACTER shape,
// under MACHINE + GENERAL collation, EXACT on/off, DELETED on/off, cold + warm.
// =====================================================================================

public sealed class QueryOptimizerCharSeekInvarianceTests
    : IClassFixture<QueryOptimizerCharSeekInvarianceTests.CharTable>
{
    private readonly CharTable _fx;
    public QueryOptimizerCharSeekInvarianceTests(CharTable fx) => _fx = fx;

    /// <summary>
    /// Assert optimizer == brute force for <paramref name="filter"/> under the given collation,
    /// across all four EXACT×DELETED contexts, AND that the COLD path (no entrySource) and the
    /// WARM Highlike path (a materialized decoded entrySource) agree with each other and the scan.
    /// </summary>
    private void AssertInvariant(string filter, IVfpCollation collation)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        // Warm-cache simulation: the SAME decoded entries CdxTag.EnumerateEntries yields,
        // materialized once per tag (what HighlikeIndexCache serves the optimizer).
        var cache = new Dictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<IndexEntry> EntrySource(CdxTag tag)
        {
            if (!cache.TryGetValue(tag.Name, out var list))
                cache[tag.Name] = list = tag.EnumerateEntries().ToList();
            return list;
        }

        foreach (bool exact in new[] { false, true })
        foreach (bool deleted in new[] { true, false })
        {
            var ctx = new EvaluationContext { Collation = collation, Exact = exact, Deleted = deleted };
            var expected = CharSeekOracle.BruteForce(table, filter, ctx).OrderBy(x => x).ToArray();

            var cold = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
            var warm = QueryOptimizer.FindRecords(table, cdx, filter, ctx, entrySource: EntrySource);

            Assert.Equal(expected, cold.RecordNumbers.OrderBy(x => x).ToArray());
            Assert.Equal(expected, warm.RecordNumbers.OrderBy(x => x).ToArray());
        }
    }

    // ---- MACHINE collation: prefix/exact equality, ranges, one-sided, dup keys, boundary, edges ----
    [Theory]
    [InlineData("NAME = 'Nm0007'")]                          // prefix (EXACT OFF), ~30 duplicate-key recnos
    [InlineData("NAME == 'Nm0007'")]                         // exact (EXACT ON) — same key, all recnos
    [InlineData("NAME = 'Nm'")]                              // prefix of EVERY NmXXXX row (all-match-ish)
    [InlineData("NAME = 'Nm0000'")]                          // low boundary key
    [InlineData("NAME = 'Nm0099'")]                          // high boundary key
    [InlineData("NAME >= 'Nm0010' AND NAME <= 'Nm0012'")]    // two-sided window
    [InlineData("NAME > 'Nm0050'")]                          // one-sided lower
    [InlineData("NAME < 'Nm0005'")]                          // one-sided upper
    [InlineData("NAME >= 'A'")]                              // all-match (>= below every key)
    [InlineData("NAME = 'ZZZ'")]                             // bump/carry edge
    [InlineData("NAME = 'Zz'")]
    [InlineData("NAME = 'zebra'")]
    [InlineData("NAME = 'NOPE'")]                            // no-match
    [InlineData("NAME = 'zzzzzz'")]                          // no-match above every key
    [InlineData("NAME = ''")]                                // empty literal (residual, but still correct)
    [InlineData("NAME = 'Nm0007 LONGER THAN THE TWENTY CHAR FIELD'")] // literal longer than the field
    [InlineData("NAME <> 'Nm0007'")]                         // <> stays a scan — must stay correct
    // ---- UPPER(field) FUNCTION key (MACHINE) ----
    [InlineData("UPPER(NAME) = 'NM0007'")]                   // function-key prefix
    [InlineData("UPPER(NAME) == 'NM0007'")]                  // function-key exact
    [InlineData("UPPER(NAME) >= 'NM0010' AND UPPER(NAME) <= 'NM0012'")]
    // ---- AND / OR / NOT combinations involving a character leaf ----
    [InlineData("NAME = 'Nm0007' AND ID > 0")]              // char AND ordered
    [InlineData("NAME = 'Nm0007' AND CAT = 'C1'")]          // char AND unindexed residual
    [InlineData("NAME = 'Nm0007' OR ID = 1")]
    [InlineData("NOT (NAME = 'Nm0007')")]                   // NOT over an inexact char superset
    public void Char_MachineCollation_EqualsFullScan(string filter)
        => AssertInvariant(filter, VfpCollations.Machine);

    // ---- GENERAL collation: case- / accent- / ligature-folding must never drop a true match ----
    [Theory]
    [InlineData("NAME = 'Nm0007'")]
    [InlineData("NAME == 'Nm0007'")]
    [InlineData("NAME = 'muller'")]      // folds to the stored 'Müller' rows
    [InlineData("NAME = 'MÜLLER'")]
    [InlineData("NAME == 'Müller'")]
    [InlineData("NAME = 'arzte'")]       // folds to 'Ärzte'
    [InlineData("NAME = 'ärzte'")]
    [InlineData("NAME = 'strasse'")]     // ß ligature vs 'Straße'
    [InlineData("NAME = 'STRASSE'")]
    [InlineData("NAME = 'Straße'")]
    [InlineData("NAME = 'oeuvre'")]      // œ ligature vs 'Œuvre'
    [InlineData("NAME = 'Œuvre'")]
    [InlineData("NAME = 'cafe'")]        // folds to 'Café'
    [InlineData("NAME = 'zurich'")]      // folds to 'Zürich'
    [InlineData("NAME >= 'M' AND NAME <= 'O'")]   // range over the accented region
    [InlineData("NAME > 'Nm0050'")]
    [InlineData("NAME = 'NOPE'")]        // no-match
    [InlineData("NAME <> 'Nm0007'")]     // <> stays a scan — must stay correct under GENERAL
    public void Char_GeneralCollation_EqualsFullScan(string filter)
        => AssertInvariant(filter, VfpCollations.General);

    // -------------------------------------------------------------- shared temp table

    /// <summary>
    /// One temp table (~3000 rows) whose NAME column is a SELECTIVE mixed-case pattern
    /// ("Nm" + (i % 100) → ~30 DUPLICATE-key recnos per distinct value) plus adversarial
    /// fixed rows (accented 'Müller'/'Ärzte'/'Zürich'/'Café', ligatures 'Straße'/'Œuvre' for
    /// GENERAL folding, and 'ZZZ'/'zebra'/'Zz' for the bump/carry edges). THREE character tags
    /// index it: NAMETAG (NAME, MACHINE), GNAME (NAME, GENERAL) and UNAME (UPPER(NAME), MACHINE
    /// function key). CAT is deliberately UNINDEXED (the residual column). A scattering of rows
    /// is DELETED before indexing (CDX then excludes them, exercising the SET DELETED on/off
    /// split). Created via the writer; deleted on dispose.
    /// </summary>
    public sealed class CharTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        private static readonly string[] Specials =
        {
            "Müller", "Müller", "Müller", "müller", "MÜLLER",
            "Ärzte", "ärzte",
            "Straße", "STRASSE",
            "Œuvre", "œuvre",
            "Café", "café",
            "Zürich",
            "ZZZ", "ZZZ",
            "zebra",
            "Zz",
        };

        public CharTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_charseek_inv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "cinv.dbf");

            const int patternRows = 3000;

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("CAT", 'C', 4),     // UNINDEXED residual column
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                int id = 1;
                for (int i = 0; i < patternRows; i++, id++)
                    w.AppendRecord(new object?[] { id, "Nm" + (i % 100).ToString("D4"), "C" + (i % 5) });
                foreach (var s in Specials)
                    w.AppendRecord(new object?[] { id++, s, "CX" });

                // Delete a scattering of rows BEFORE indexing (CDX then excludes them).
                for (int idx = 0; idx < patternRows; idx += 50)
                    w.Delete(idx);

                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
                w.CreateTag(new CdxTagDefinition("GNAME", "NAME", collation: "GENERAL"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}

// =====================================================================================
// (2) O(log n) PROOF — the entry-VISIT counter shows a CHARACTER seek, not a scan.
// =====================================================================================

public sealed class QueryOptimizerCharSeekComplexityTests
    : IClassFixture<QueryOptimizerCharSeekComplexityTests.LargeCharTable>
{
    private readonly LargeCharTable _fx;
    public QueryOptimizerCharSeekComplexityTests(LargeCharTable fx) => _fx = fx;

    /// <summary>
    /// Run the optimizer counting how many index entries the candidate BUILD examines via the
    /// onIndexEntryExamined instrumentation seam, on the COLD path or the WARM Highlike path.
    /// Returns (visits, result, matchCount) so each test can assert BOTH correctness and cost.
    /// </summary>
    private (long visits, QueryResult result, int matchCount) Measure(string filter, IVfpCollation collation, bool warm)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        // SET DELETED ON → the optimized candidate path runs (the CDX excludes deleted rows).
        var ctx = new EvaluationContext { Collation = collation, Deleted = true };
        var expected = CharSeekOracle.BruteForce(table, filter, ctx);

        Func<CdxTag, IEnumerable<IndexEntry>>? src = null;
        if (warm)
        {
            var cache = new Dictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
            src = tag =>
            {
                if (!cache.TryGetValue(tag.Name, out var list))
                    cache[tag.Name] = list = tag.EnumerateEntries().ToList();
                return list;
            };
        }

        long visits = 0;
        var result = QueryOptimizer.FindRecords(table, cdx, filter, ctx,
            entrySource: src, onIndexEntryExamined: _ => visits++);

        Assert.Equal(
            expected.OrderBy(x => x).ToArray(),
            result.RecordNumbers.OrderBy(x => x).ToArray());

        return (visits, result, expected.Count);
    }

    /// <summary>
    /// The O(log n + matches) gate: a selective CHARACTER leaf must examine FAR fewer than the
    /// table's entry count — bounded by the match count plus a small log/leaf-page slack. Today
    /// the character build scans every entry (~n), so this FAILS until the char seek lands.
    /// </summary>
    private void AssertSeekNotScan(string filter, IVfpCollation collation, bool warm)
    {
        var (visits, result, matchCount) = Measure(filter, collation, warm);
        int n = _fx.RowCount;

        Assert.True(result.Optimized, "a character-key leaf must report optimized");
        // Generous slack (B-tree depth + a few leaf pages) — still orders of magnitude below n.
        Assert.True(visits <= matchCount + 4096,
            $"[warm={warm}] '{filter}' examined {visits} entries; a seek should visit ~matches({matchCount}) + log slack");
        Assert.True(visits < n / 10,
            $"[warm={warm}] '{filter}' examined {visits} of {n} entries — must be << table size (a SEEK, not a SCAN)");
    }

    // ---- selective character equality (prefix EXACT OFF): ~120 duplicate-key matches ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharEquality_Machine_Seeks(bool warm)
        => AssertSeekNotScan("NAME = 'K000007'", VfpCollations.Machine, warm);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharEquality_General_Seeks(bool warm)
        => AssertSeekNotScan("NAME = 'K000007'", VfpCollations.General, warm);

    // ---- a short prefix bounding a small contiguous bucket (K000000..K000009) ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharPrefix_Machine_Seeks(bool warm)
        => AssertSeekNotScan("NAME = 'K00000'", VfpCollations.Machine, warm);

    // ---- a character RANGE / two-sided window ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharRange_Machine_Seeks(bool warm)
        => AssertSeekNotScan("NAME >= 'K000010' AND NAME <= 'K000012'", VfpCollations.Machine, warm);

    // ---- a one-sided slice near the top of the key space ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharOneSided_Machine_Seeks(bool warm)
        => AssertSeekNotScan("NAME > 'K000998'", VfpCollations.Machine, warm);

    // ---- UPPER(field) FUNCTION key seeks too ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpperFunctionKey_Machine_Seeks(bool warm)
        => AssertSeekNotScan("UPPER(NAME) = 'K000007'", VfpCollations.Machine, warm);

    // ---- CONTROL: character <> is non-monotonic and out of seek scope ----
    // Unlike an ORDERED <> (which set-all-minus-range walks the whole index), a CHARACTER <>
    // cannot be narrowed safely (BuildCharCmp returns null for Ne), so the leaf falls to the
    // RESIDUAL full-table scan — it drives NO index and examines NO index entries. Green now
    // AND after the char-seek fix (which only adds seekable monotonic shapes). The result is
    // still confirmed == brute force inside Measure, so correctness is locked either way.
    [Fact]
    public void NotEqual_IsResidual_NotSeeked_AsControl()
    {
        var (visits, result, _) = Measure("NAME <> 'K000007'", VfpCollations.Machine, warm: false);
        Assert.False(result.Optimized,
            "a character <> is non-monotonic — it must NOT drive the index (residual full scan)");
        Assert.Equal(0, visits); // no index entry is examined: the candidate is the all-records universe
    }

    // -------------------------------------------------------------- shared large temp table

    /// <summary>
    /// One LARGE temp table (120k rows) whose NAME char(12) column is a SELECTIVE pattern
    /// ("K" + (i % 1000) → ~120 DUPLICATE-key recnos per distinct value). THREE character tags
    /// index it: NAMETAG (NAME, MACHINE), GNAME (NAME, GENERAL) and UNAME (UPPER(NAME), MACHINE).
    /// Big enough that O(n) vs O(log n) is unmistakable. Built once via the writer; deleted on
    /// dispose.
    /// </summary>
    public sealed class LargeCharTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 120_000;

        public LargeCharTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_charseek_big_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "cbig.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NAME", 'C', 12),
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                    w.AppendRecord(new object?[] { i, "K" + (i % 1000).ToString("D6") });

                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
                w.CreateTag(new CdxTagDefinition("GNAME", "NAME", collation: "GENERAL"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
