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
// §D8 — Rushmore candidate build must SEEK an ordered key, not SCAN it (the perf fix).
//
// THE BUG these tests pin: for an ORDERED key (Integer / Numeric / Date / DateTime) the
// optimizer builds a leaf's candidate by walking EVERY index entry and testing the
// predicate on each — O(n). Because the entries are stored SORTED by the transformed key,
// a comparison/BETWEEN/range/one-sided predicate maps to a CONTIGUOUS run that should be
// reached by a SEEK (binary search on the cached decoded directory, or CdxTag.Seek + a
// forward leaf walk on the cold path), giving O(log n + matches).
//
// THESE TESTS COME IN TWO HALVES:
//   (1) RESULT-INVARIANCE (the hard gate, must always be GREEN): the optimizer result is
//       EXACTLY a full scan == the old behaviour, for every shape, with + without the
//       Highlike warm-cache entrySource, under SET EXACT / SET DELETED on+off, for
//       negatives / dates / datetimes / boundary / no-match / all-match. The fix only
//       changes HOW candidates are found, never WHICH — so these never move.
//   (2) O(log n) PROOF (the RED gate, fails until the seek lands): a deterministic,
//       timing-free entry-VISIT counter (the onIndexEntryExamined instrumentation seam)
//       proves a selective ordered-key leaf examines ~O(log n + matches) entries, FAR
//       below the table size. Today the scan visits ~n, so these FAIL. A CHARACTER or <>
//       leaf legitimately still visits ~n (out of scope) — asserted as a control.
//
// SAFETY: every table is built fresh under the OS TEMP dir via the writer and deleted on
// dispose. No committed fixture is read or mutated. No new dependencies.
// =====================================================================================

/// <summary>Shared residual row adapter + brute-force oracle (mirrors QueryOptimizer exactly).</summary>
internal static class SeekOracle
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
    /// The ground truth — a literal full scan that reproduces the optimizer's residual
    /// semantics byte-for-byte: every PHYSICAL record (recno = physical position), with the
    /// implicit AND NOT DELETED() applied only under SET DELETED ON. This is what BOTH the
    /// old full-scan build and the new seek build must equal.
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
// (1) RESULT-INVARIANCE — the optimizer result EQUALS a full scan, for every shape.
// =====================================================================================

public sealed class QueryOptimizerSeekInvarianceTests
    : IClassFixture<QueryOptimizerSeekInvarianceTests.OrderedTable>
{
    private readonly OrderedTable _fx;
    public QueryOptimizerSeekInvarianceTests(OrderedTable fx) => _fx = fx;

    /// <summary>The four ambient contexts the invariant must hold under (MACHINE collation).</summary>
    private static IEnumerable<(string Label, EvaluationContext Ctx)> Contexts()
    {
        yield return ("EXACT off / DELETED on", new EvaluationContext { Exact = false, Deleted = true });
        yield return ("EXACT on  / DELETED on", new EvaluationContext { Exact = true, Deleted = true });
        yield return ("EXACT off / DELETED off", new EvaluationContext { Exact = false, Deleted = false });
        yield return ("EXACT on  / DELETED off", new EvaluationContext { Exact = true, Deleted = false });
    }

    /// <summary>
    /// Assert the optimizer == brute force for <paramref name="filter"/> across all four
    /// contexts, AND that the COLD path (no entrySource) and the WARM Highlike path (a
    /// materialized decoded entrySource) agree with each other and with the scan.
    /// </summary>
    private void AssertInvariant(string filter)
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

        foreach (var (label, ctx) in Contexts())
        {
            var expected = SeekOracle.BruteForce(table, filter, ctx).OrderBy(x => x).ToArray();

            var cold = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
            var warm = QueryOptimizer.FindRecords(table, cdx, filter, ctx, entrySource: EntrySource);

            Assert.Equal(expected, cold.RecordNumbers.OrderBy(x => x).ToArray());
            Assert.Equal(expected, warm.RecordNumbers.OrderBy(x => x).ToArray());
        }
    }

    // ---- Integer: equality / one-sided / range / BETWEEN / INLIST, negatives, boundary ----
    [Theory]
    [InlineData("SVAL = 0")]
    [InlineData("SVAL = -1499")]                       // low boundary key
    [InlineData("SVAL = 1500")]                        // high boundary key
    [InlineData("SVAL = -7")]                          // a negative value
    [InlineData("ID = 999999")]                        // no-match (above every key)
    [InlineData("ID = -5")]                            // no-match (below every key)
    [InlineData("SVAL > 0")]
    [InlineData("SVAL >= 0")]
    [InlineData("SVAL < -1000")]
    [InlineData("SVAL <= -1490")]
    [InlineData("ID > 2999")]                          // one-sided near the top
    [InlineData("SVAL >= -10 AND SVAL <= 10")]         // two-sided window straddling zero
    [InlineData("BETWEEN(SVAL, -5, 5)")]
    [InlineData("INLIST(SVAL, -100, 0, 100)")]
    [InlineData("SVAL >= -1499")]                      // all-match (>= the minimum)
    [InlineData("ID >= 1")]                            // all-match
    [InlineData("SVAL > 100000")]                      // empty (above max)
    [InlineData("SVAL < -100000")]                     // empty (below min)
    // ---- Numeric (decimals + negatives) ----
    [InlineData("NUM = 0")]
    [InlineData("NUM > 100.0")]
    [InlineData("NUM <= -50.5")]
    [InlineData("BETWEEN(NUM, -10.0, 10.0)")]
    [InlineData("NUM >= -5.5 AND NUM <= 5.5")]
    // ---- Date ----
    [InlineData("DT = {^2020-01-01}")]
    [InlineData("DT > {^2020-06-01}")]
    [InlineData("DT < {^2020-01-05}")]
    [InlineData("DT >= {^2020-01-01} AND DT <= {^2020-01-31}")]
    [InlineData("BETWEEN(DT, {^2020-03-01}, {^2020-03-31})")]
    // ---- DateTime ----
    [InlineData("TS = {^2020-01-01 00:05:00}")]
    [InlineData("TS > {^2020-01-01 10:00:00}")]
    [InlineData("TS >= {^2020-01-01 00:10:00} AND TS < {^2020-01-01 00:30:00}")]
    // ---- AND / OR / NOT combinations (incl. an unindexed residual conjunct) ----
    [InlineData("SVAL > 0 AND DT < {^2020-06-01}")]
    [InlineData("SVAL = 0 OR ID = 1")]
    [InlineData("NOT (SVAL = 0)")]
    [InlineData("NOT (SVAL > 0)")]
    [InlineData("NOT (SVAL < 0)")]
    [InlineData("SVAL >= 0 AND NUM <= 0")]
    [InlineData("SVAL > 0 OR NUM < 0")]
    [InlineData("SVAL = 0 AND CAT = 'C0'")]            // indexed AND unindexed residual
    [InlineData("DT = {^2020-01-01} OR CAT = 'C3'")]   // OR with a residual collapses (still correct)
    // ---- <> (not-equal) and CHARACTER stay on the SCAN path — must still be correct ----
    [InlineData("SVAL <> 0")]
    [InlineData("ID <> 1500")]
    [InlineData("NAME = 'NM0007'")]
    [InlineData("NAME == 'NM0007'")]
    [InlineData("NAME > 'NM0050'")]
    public void OptimizerResult_EqualsFullScan_AllShapesAndContexts(string filter)
        => AssertInvariant(filter);

    // -------------------------------------------------------------- shared temp table

    /// <summary>
    /// One temp table (~3000 rows) with ORDERED-key tags on ID (Integer), SVAL (Integer,
    /// spanning negatives), NUM (Numeric w/ decimals + negatives), DT (Date), TS (DateTime)
    /// and a CHARACTER tag on NAME; CAT is deliberately UNINDEXED (the residual column). A
    /// handful of records are DELETED before the index is built (so the CDX excludes them,
    /// exercising the SET DELETED on/off split). Created via the writer; deleted on dispose.
    /// </summary>
    public sealed class OrderedTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 3000;

        public OrderedTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_seek_inv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "inv.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("SVAL", 'I', 4),
                new DbfColumnDef("NUM", 'N', 12, 2),
                new DbfColumnDef("DT", 'D', 8),
                new DbfColumnDef("TS", 'T', 8),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("CAT", 'C', 4),
            };

            var baseDate = new DateOnly(2020, 1, 1);
            var baseTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                {
                    int sval = i - 1500;                       // -1499 .. 1500 (negatives + positives)
                    double num = (i - 1500) * 0.5;             // -749.5 .. 750.0 (decimals + negatives)
                    var dt = baseDate.AddDays(i % 365);
                    var ts = baseTime.AddMinutes(i);
                    string name = "NM" + (i % 100).ToString("D4");
                    string cat = "C" + (i % 5);                // C0..C4 (unindexed)
                    w.AppendRecord(new object?[] { i, sval, num, dt, ts, name, cat });
                }

                // Delete a scattering of rows BEFORE indexing (CDX then excludes them).
                for (int idx = 0; idx < RowCount; idx += 50)
                    w.Delete(idx);

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("SVALTAG", "SVAL"));
                w.CreateTag(new CdxTagDefinition("NUMTAG", "NUM"));
                w.CreateTag(new CdxTagDefinition("DTTAG", "DT"));
                w.CreateTag(new CdxTagDefinition("TSTAG", "TS"));
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
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
// (1b) NULL-NUMERIC REGRESSION — the warm (cached) seek must NOT drop real matches when
// the ordered Numeric tag carries >=2 NULL keys.
//
// THE BUG this pins: a NULL Numeric/Float/Currency/Double key is stored as an all-zero
// byte[keyLen] that sorts FIRST. DecodeTransformedDouble maps that all-zero pattern to a
// NaN (bit63 clear → invert all bits → 0xFFFF… → Int64BitsToDouble = NaN). The OLD NumOf
// returned that NaN (not null), so the SeekCached null-guards never fired; every binary
// comparison against NaN is false, dragging BOTH bounds toward index 0. With >=2 leading
// NaN entries the [start,end) window collapsed to empty and the real matches that sort
// after the NULLs were silently lost — so the WARM result != the COLD/scan result.
//
// THE FIX: NumOf returns null for NaN, so the warm seek falls back to the (correct) full
// scan. These tests assert warm == cold == brute-force for =, <, <=, and BETWEEN, which
// FAIL on the old code (warm under-returns) and pass after the fix.
// =====================================================================================

public sealed class QueryOptimizerSeekNullNumericTests
    : IClassFixture<QueryOptimizerSeekNullNumericTests.NullNumericTable>
{
    private readonly NullNumericTable _fx;
    public QueryOptimizerSeekNullNumericTests(NullNumericTable fx) => _fx = fx;

    /// <summary>warm (Highlike entrySource binary-search) == cold (B-tree walk) == full scan.</summary>
    private void AssertWarmEqualsScan(string filter)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var cache = new Dictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<IndexEntry> EntrySource(CdxTag tag)
        {
            if (!cache.TryGetValue(tag.Name, out var list))
                cache[tag.Name] = list = tag.EnumerateEntries().ToList();
            return list;
        }

        foreach (var ctx in new[]
        {
            new EvaluationContext { Exact = false, Deleted = true },
            new EvaluationContext { Exact = true, Deleted = false },
        })
        {
            var expected = SeekOracle.BruteForce(table, filter, ctx).OrderBy(x => x).ToArray();
            var cold = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
            var warm = QueryOptimizer.FindRecords(table, cdx, filter, ctx, entrySource: EntrySource);

            Assert.Equal(expected, cold.RecordNumbers.OrderBy(x => x).ToArray());
            // The regression: the WARM path used to under-return here (NaN collapsed the window).
            Assert.Equal(expected, warm.RecordNumbers.OrderBy(x => x).ToArray());
        }
    }

    [Theory]
    [InlineData("PRICE = 5")]      // exact match on a duplicated value past the NULL block
    [InlineData("PRICE = -3")]     // exact match on the lone negative
    [InlineData("PRICE < 10")]     // one-sided upper bound: used to collapse end → 0
    [InlineData("PRICE <= 5")]
    [InlineData("PRICE > -10")]    // one-sided LOWER bound: start collapse is harmless (control)
    [InlineData("BETWEEN(PRICE, -5, 20)")]
    public void WarmSeek_WithLeadingNullNumericKeys_EqualsScan(string filter)
        => AssertWarmEqualsScan(filter);

    /// <summary>
    /// A small temp table whose Numeric PRICE column is NULLABLE and where the NULL rows are the
    /// MAJORITY — mirroring the bug's worked example [NaN,NaN,NaN,5]. NULL Numeric keys are
    /// stored as all-zero byte[keyLen] that decode to NaN and sort FIRST, so with NULLs filling
    /// the front >half of the directory EVERY binary-search probe starts inside the NaN block;
    /// on the old code each `<= hi` test was false (NaN), dragging the upper bound `end` to 0 and
    /// dropping the real matches that sort after the NULLs. Built via the writer; deleted on dispose.
    /// </summary>
    public sealed class NullNumericTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        public NullNumericTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_seek_null_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "nulnum.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("PRICE", 'N', 12, 2, nullable: true),
            };

            // 6 NULLs (the majority → keys are all-zero/NaN, sort first) + a few real values
            // (a negative, a duplicate, a high one). The NaN block dominates so the upper-bound
            // binary search probes land on NaN and the bug's window-collapse triggers.
            var prices = new double?[]
            {
                null, null, null, null, null, null,  // 6 leading NULLs (all-zero → NaN keys)
                -3, 5, 5, 7,                          // real values that sort AFTER the NULLs
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 0; i < prices.Length; i++)
                    w.AppendRecord(new object?[] { i + 1, prices[i] });

                w.CreateTag(new CdxTagDefinition("PRICETAG", "PRICE"));
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
// (2) O(log n) PROOF — the entry-VISIT counter shows a seek, not a scan.
// =====================================================================================

public sealed class QueryOptimizerSeekComplexityTests
    : IClassFixture<QueryOptimizerSeekComplexityTests.LargeTable>
{
    private readonly LargeTable _fx;
    public QueryOptimizerSeekComplexityTests(LargeTable fx) => _fx = fx;

    /// <summary>
    /// Run the optimizer counting how many index entries the candidate BUILD examines via the
    /// onIndexEntryExamined instrumentation seam, on the COLD path or the WARM Highlike path.
    /// Returns (visits, result, expected) so each test can assert BOTH correctness and cost.
    /// </summary>
    private (long visits, QueryResult result, int matchCount) Measure(string filter, bool warm)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var ctx = new EvaluationContext { Deleted = true }; // SET DELETED ON → the candidate path runs
        var expected = SeekOracle.BruteForce(table, filter, ctx);

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
    /// The O(log n + matches) gate: a selective ordered-key leaf must examine FAR fewer than
    /// the table's entry count — bounded by the match count plus a small log/leaf-page slack.
    /// Today the build scans every entry (~n), so this FAILS until the seek lands.
    /// </summary>
    private void AssertSeekNotScan(string filter, bool warm)
    {
        var (visits, result, matchCount) = Measure(filter, warm);
        int n = _fx.RowCount;

        Assert.True(result.Optimized, "an ordered-key leaf must report optimized");
        // Generous slack (B-tree depth + a few leaf pages) — still orders of magnitude below n.
        Assert.True(visits <= matchCount + 4096,
            $"[warm={warm}] '{filter}' examined {visits} entries; a seek should visit ~matches({matchCount}) + log slack");
        Assert.True(visits < n / 10,
            $"[warm={warm}] '{filter}' examined {visits} of {n} entries — must be << table size (a SEEK, not a SCAN)");
    }

    // ---- ordered-key equality: 1 (ID/NUM unique) or a small bucket (DT/TS) ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegerEquality_Seeks(bool warm) => AssertSeekNotScan("ID = 60000", warm);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericEquality_Seeks(bool warm) => AssertSeekNotScan("NUM = 60000", warm);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegerRange_Seeks(bool warm)
        => AssertSeekNotScan("ID >= 40000 AND ID <= 40010", warm);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegerOneSided_Seeks(bool warm)
        // A tiny one-sided slice near the top of the key space.
        => AssertSeekNotScan("ID > 119990", warm);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegerBetween_Seeks(bool warm)
        => AssertSeekNotScan("BETWEEN(ID, 50000, 50005)", warm);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DateEquality_Seeks(bool warm) => AssertSeekNotScan("DT = {^2020-01-01}", warm);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DateTimeRange_Seeks(bool warm)
        => AssertSeekNotScan("TS >= {^2020-01-01 00:10:00} AND TS < {^2020-01-01 00:15:00}", warm);

    // ---- CONTROL: out-of-scope shapes legitimately KEEP the full scan (visits ~ n) ----
    // <> is non-monotonic (a set-all-minus-range complement), so it stays a scan — green now
    // AND after the char-seek fix, proving the counter measures real work.
    //
    // NOTE (§12.1): CHARACTER-key equality is NO LONGER a scan control. The char-seek fix brings
    // CHARACTER keys into seek scope (collation-ordered range seek), so a selective char equality
    // must SEEK ~O(log n + matches), not scan ~n. The dedicated proof lives in
    // QueryOptimizerCharSeekComplexityTests; this assertion is RED until that fix lands.
    [Fact]
    public void CharacterEquality_Seeks_AfterCharSeekFix()
    {
        var (visits, result, matchCount) = Measure("NAME = 'K000007'", warm: false);
        Assert.True(result.Optimized, "a character-key equality must report optimized");
        Assert.True(visits <= matchCount + 4096,
            $"a CHARACTER equality should seek ~matches({matchCount}) + log slack, visited {visits}");
        Assert.True(visits < _fx.RowCount / 10,
            $"a CHARACTER equality must SEEK (<< {_fx.RowCount}), not scan — visited {visits}");
    }

    [Fact]
    public void NotEqual_StillScans_AsControl()
    {
        var (visits, _, _) = Measure("ID <> 60000", warm: false);
        Assert.True(visits >= _fx.RowCount - _fx.RowCount / 10,
            $"a <> leaf is out of seek scope and must still scan ~all entries, visited {visits} of {_fx.RowCount}");
    }

    // -------------------------------------------------------------- shared large temp table

    /// <summary>
    /// One LARGE temp table (120k rows) with ordered-key tags on ID (Integer, unique),
    /// NUM (Numeric, unique), DT (Date, ~120 rows/day), TS (DateTime, distinct) and a
    /// CHARACTER tag on NAME (the scan control). Big enough that O(n) vs O(log n) is
    /// unmistakable. Built once via the writer; deleted on dispose.
    /// </summary>
    public sealed class LargeTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 120_000;

        public LargeTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_seek_big_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "big.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NUM", 'N', 12, 0),
                new DbfColumnDef("DT", 'D', 8),
                new DbfColumnDef("TS", 'T', 8),
                new DbfColumnDef("NAME", 'C', 12),
            };

            var baseDate = new DateOnly(2020, 1, 1);
            var baseTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                {
                    var dt = baseDate.AddDays(i % 1000);          // ~120 rows per day
                    var ts = baseTime.AddSeconds(i);              // all distinct instants
                    string name = "K" + (i % 1000).ToString("D6");
                    w.AppendRecord(new object?[] { i, (double)i, dt, ts, name });
                }

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("NUMTAG", "NUM"));
                w.CreateTag(new CdxTagDefinition("DTTAG", "DT"));
                w.CreateTag(new CdxTagDefinition("TSTAG", "TS"));
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
