using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Highlike;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

// =====================================================================================
// §D8 / HIGHLIKE_ENGINE_DESIGN §12.2 — RANGE-WINDOW recognition: a two-sided range
// expressed as TWO leaves on the SAME tag must be ONE tight window seek, not two seeks.
//
// THE BUG (diagnosed on the 1M benchmark): for `AMOUNT >= lo AND AMOUNT <= hi` (one
// ordered tag, two bounds) the Core optimizer already FUSES the low+high into one window
// (deferred OrderedBound.Intersect in ParseAnd) and scans exactly the intersection. But
// once the Highlike COST PLANNER is in play, it judges the `>= lo` leaf IN ISOLATION:
// because that lower bound alone matches a large fraction of the table (> the index-vs-
// scan threshold) the planner DROPS it to the residual via IOptimizerPolicy.PreferFullScan
// — and that demotion happens PER-LEAF, BEFORE the AND fusion can recognise the pair as a
// window. The candidate then collapses to just the `<= hi` leaf (the one-sided, LARGER
// count), so Highlike scans the one-sided count while Core scans the (smaller) true
// intersection — Highlike is SLOWER than Core, an inversion.
//
// THE FIX (design §D8 "low+high on one tag -> a single window"): when an AND group holds a
// LOWER-bound leaf (>= or >) AND an UPPER-bound leaf (<= or <) on the SAME tag key, MERGE
// them into one window [lo,hi] and build the candidate with ONE range seek — sidestepping
// the per-leaf threshold so BOTH Core and the cost-planned Highlike path get the tight
// window candidate.
//
// THESE TESTS COME IN TWO HALVES (mirroring QueryOptimizerSeekTests):
//   (1) RESULT-INVARIANCE (the hard gate, ALWAYS green): for every shape the optimizer
//       result EXACTLY equals a full scan, on BOTH the Core path AND the Highlike path with
//       statistics ON (the cost-planner path that triggers the demotion). The window only
//       changes HOW the candidate is found, never WHICH records — so these never move.
//   (2) WINDOW PROOF (the RED gate, fails until the merge lands): for a two-sided same-tag
//       range over a LARGE table, the residual RecordsScanned (and a Core-level entry-visit
//       counter) is ~the true match count — NOT the one-sided (larger) count and NOT the
//       table size. Today the cost-planned path drops the lower bound and scans the
//       one-sided count, so these FAIL.
//
// SAFETY: every table is built fresh under the OS TEMP dir via the writer and deleted on
// dispose. No committed fixture is read or mutated. No new dependencies. (SeekOracle is the
// shared brute-force oracle from QueryOptimizerSeekTests, same test assembly + namespace.)
// =====================================================================================

// =====================================================================================
// (1) RESULT-INVARIANCE — Core AND cost-planned Highlike == full scan, for every shape.
// =====================================================================================

public sealed class QueryOptimizerRangeWindowInvarianceTests
    : IClassFixture<QueryOptimizerRangeWindowInvarianceTests.WindowTable>
{
    private readonly WindowTable _fx;
    public QueryOptimizerRangeWindowInvarianceTests(WindowTable fx) => _fx = fx;

    /// <summary>The four MACHINE contexts (EXACT on/off × DELETED on/off).</summary>
    private static IEnumerable<(string Label, EvaluationContext Ctx)> MachineContexts()
    {
        yield return ("EXACT off / DELETED on", new EvaluationContext { Exact = false, Deleted = true });
        yield return ("EXACT on  / DELETED on", new EvaluationContext { Exact = true, Deleted = true });
        yield return ("EXACT off / DELETED off", new EvaluationContext { Exact = false, Deleted = false });
        yield return ("EXACT on  / DELETED off", new EvaluationContext { Exact = true, Deleted = false });
    }

    /// <summary>The four GENERAL-collation contexts (EXACT on/off × DELETED on/off).</summary>
    private static IEnumerable<(string Label, EvaluationContext Ctx)> GeneralContexts()
    {
        yield return ("GEN EXACT off / DELETED on", new EvaluationContext { Exact = false, Deleted = true, Collation = VfpCollations.General });
        yield return ("GEN EXACT on  / DELETED on", new EvaluationContext { Exact = true, Deleted = true, Collation = VfpCollations.General });
        yield return ("GEN EXACT off / DELETED off", new EvaluationContext { Exact = false, Deleted = false, Collation = VfpCollations.General });
        yield return ("GEN EXACT on  / DELETED off", new EvaluationContext { Exact = true, Deleted = false, Collation = VfpCollations.General });
    }

    /// <summary>
    /// Assert the optimizer == brute force for <paramref name="filter"/> across the given
    /// contexts, on BOTH the Core path (no policy) AND the Highlike path with STATISTICS ON
    /// (the cost-planner path that demotes a wide one-sided bound). The window merge may only
    /// change HOW the candidate is built — the result set must equal a full scan either way.
    /// </summary>
    private void AssertInvariant(string filter, IEnumerable<(string Label, EvaluationContext Ctx)> contexts)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        using var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });

        foreach (var (label, ctx) in contexts)
        {
            var expected = SeekOracle.BruteForce(table, filter, ctx).OrderBy(x => x).ToArray();

            var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
            var hi = engine.FindRecords(table, cdx, filter, ctx);

            Assert.Equal(expected, core.RecordNumbers.OrderBy(x => x).ToArray());
            // The cost-planned path must AGREE with the scan even when it demotes a wide bound.
            Assert.Equal(expected, hi.RecordNumbers.OrderBy(x => x).ToArray());
        }
    }

    // ---- Integer two-sided windows: inclusive / exclusive / mixed, empty, single-point, dupes ----
    [Theory]
    [InlineData("QTY >= 50 AND QTY <= 60")]            // inclusive window over duplicated keys
    [InlineData("QTY > 50 AND QTY < 60")]              // both bounds exclusive
    // ---- Eq + wide range on the SAME tag (MUST-FIX 1: Eq must NOT defer into the wide bound) ----
    [InlineData("QTY = 50 AND QTY >= 40")]             // point AND wide lower bound
    [InlineData("QTY = 50 AND QTY <= 60")]             // point AND upper bound
    [InlineData("QTY >= 40 AND QTY = 50")]             // reversed order (Eq second)
    [InlineData("ID = 1500 AND ID >= 1")]              // unique point AND the WIDEST lower bound (regression shape)
    [InlineData("ID = 1500 AND ID <= 3000")]           // unique point AND the widest upper bound
    [InlineData("QTY = 50 AND QTY >= 40 AND QTY <= 60")] // point inside an explicit window
    [InlineData("AMOUNT = 10.0 AND AMOUNT >= 5.0")]    // numeric point AND lower bound
    [InlineData("QTY = 999 AND QTY >= 40")]            // point with NO matches (out of QTY range) AND lower
    // ---- two SAME-direction bounds on one tag (MUST-FIX 1: merged window estimate, not OR-ed cost) ----
    [InlineData("ID >= 100 AND ID >= 50")]             // two lower bounds → effective >= 100
    [InlineData("ID >= 50 AND ID >= 100")]             // reversed
    [InlineData("ID <= 2000 AND ID <= 2500")]          // two upper bounds → effective <= 2000
    [InlineData("QTY >= 50 AND QTY >= 40")]            // same-direction over duplicated keys
    [InlineData("QTY > 50 AND QTY >= 40")]             // mixed strictness, same direction
    [InlineData("QTY >= 50 AND QTY < 60")]             // mixed >= / <
    [InlineData("QTY > 50 AND QTY <= 60")]             // mixed > / <=
    [InlineData("QTY <= 60 AND QTY >= 50")]            // reversed source order (AND is commutative)
    [InlineData("QTY >= 60 AND QTY <= 50")]            // EMPTY window (lo > hi)
    [InlineData("QTY > 60 AND QTY < 60")]              // EMPTY window (open, lo == hi)
    [InlineData("QTY >= 50 AND QTY <= 50")]            // single-point window (lo == hi, dupes)
    [InlineData("QTY >= 0 AND QTY <= 199")]            // all-match window (full key span)
    [InlineData("QTY > 100000 AND QTY < 200000")]      // window entirely above max → empty
    // ---- extra leaves in the same AND (must still equal the scan) ----
    [InlineData("QTY >= 50 AND QTY <= 60 AND CAT = 'C1'")]   // + UNINDEXED residual leaf
    [InlineData("QTY >= 50 AND QTY <= 60 AND ID <= 1000")]   // + another INDEXED leaf
    [InlineData("ID >= 100 AND ID <= 200 AND QTY >= 5 AND QTY <= 9")] // two independent windows
    // ---- OR-nested window ----
    [InlineData("(QTY >= 50 AND QTY <= 60) OR CAT = 'C2'")]
    [InlineData("(QTY >= 50 AND QTY <= 60) OR (QTY >= 150 AND QTY <= 160)")]
    // ---- Numeric (decimals, negatives via the *0.5 scale) ----
    [InlineData("AMOUNT >= 10.0 AND AMOUNT <= 20.0")]
    [InlineData("AMOUNT > 10.5 AND AMOUNT < 20.5")]
    [InlineData("AMOUNT >= 100.0 AND AMOUNT <= 50.0")]      // empty
    [InlineData("AMOUNT >= 0.0 AND AMOUNT <= 0.0")]         // single value (0.0) with dupes
    // ---- Date ----
    [InlineData("DT >= {^2020-01-05} AND DT <= {^2020-01-31}")]
    [InlineData("DT > {^2020-02-01} AND DT < {^2020-02-15}")]
    [InlineData("DT >= {^2020-12-31} AND DT <= {^2020-01-01}")] // empty
    // ---- DateTime ----
    [InlineData("TS >= {^2020-01-01 00:10:00} AND TS <= {^2020-01-01 00:30:00}")]
    [InlineData("TS > {^2020-01-01 00:10:00} AND TS < {^2020-01-01 00:30:00}")]
    // ---- CHARACTER window (MACHINE collation) ----
    [InlineData("NAME >= 'N010' AND NAME <= 'N050'")]
    [InlineData("NAME > 'N010' AND NAME < 'N050'")]
    [InlineData("NAME >= 'N050' AND NAME <= 'N010'")]       // empty
    [InlineData("NAME >= 'N007' AND NAME <= 'N007'")]       // single bucket (dupes)
    public void Window_EqualsFullScan_Machine(string filter)
        => AssertInvariant(filter, MachineContexts());

    // ---- CHARACTER window under the GENERAL collation (collation-ordered key) ----
    [Theory]
    [InlineData("NAME >= 'N010' AND NAME <= 'N050'")]
    [InlineData("NAME > 'N010' AND NAME < 'N050'")]
    [InlineData("NAME >= 'N050' AND NAME <= 'N010'")]       // empty
    public void Window_EqualsFullScan_General(string filter)
        => AssertInvariant(filter, GeneralContexts());

    // -------------------------------------------------------------- shared temp table

    /// <summary>
    /// One temp table (3000 rows) with ordered-key tags on ID (Integer, unique), AMOUNT
    /// (Numeric w/ decimals + duplicates), QTY (Integer w/ duplicates), DT (Date), TS
    /// (DateTime) and CHARACTER tags on NAME in BOTH the MACHINE and GENERAL collations; CAT
    /// is deliberately UNINDEXED (the residual column). A handful of rows are DELETED before
    /// indexing (so the CDX excludes them, exercising the SET DELETED on/off split). Built via
    /// the writer; deleted on dispose.
    /// </summary>
    public sealed class WindowTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 3000;

        public WindowTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_window_inv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "win.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 12, 2),
                new DbfColumnDef("QTY", 'I', 4),
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
                    double amount = (i % 500) * 0.5;            // dupes + decimals, range [0, 249.5]
                    int qty = i % 200;                          // dupes, range 0..199
                    var dt = baseDate.AddDays(i % 365);
                    var ts = baseTime.AddMinutes(i % 600);
                    string name = "N" + (i % 100).ToString("D3"); // N000..N099 (dupes)
                    string cat = "C" + (i % 5);                 // C0..C4 (unindexed)
                    w.AppendRecord(new object?[] { i, amount, qty, dt, ts, name, cat });
                }

                // Delete a scattering of rows BEFORE indexing (CDX then excludes them).
                for (int idx = 0; idx < RowCount; idx += 50)
                    w.Delete(idx);

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("QTYTAG", "QTY"));
                w.CreateTag(new CdxTagDefinition("DTTAG", "DT"));
                w.CreateTag(new CdxTagDefinition("TSTAG", "TS"));
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME", collation: "MACHINE"));
                w.CreateTag(new CdxTagDefinition("NAMEGTAG", "NAME", collation: "GENERAL"));
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
// (2) WINDOW PROOF — the cost-planned path must scan ~the WINDOW, not the one-sided count.
// =====================================================================================

public sealed class QueryOptimizerRangeWindowProofTests
    : IClassFixture<QueryOptimizerRangeWindowProofTests.LargeWindowTable>
{
    private readonly LargeWindowTable _fx;
    public QueryOptimizerRangeWindowProofTests(LargeWindowTable fx) => _fx = fx;

    // The benchmark window: AMOUNT is unique 1..N, so the inclusive window [lo,hi] matches
    // exactly (hi-lo+1) rows. With N = 100_000 and the default IndexVsScanThreshold = 0.5:
    //   `AMOUNT >= 10000`  matches 90_001 rows (~90 %)  → ABOVE threshold → cost-planner DROPS it.
    //   `AMOUNT <= 20000`  matches 20_000 rows (~20 %)  → below threshold → kept (the one-sided count).
    //   window [10000,20000]                  matches 10_001 rows (the TRUE intersection).
    private const int Lo = 10_000;
    private const int Hi = 20_000;
    private const string WindowFilter = "AMOUNT >= 10000 AND AMOUNT <= 20000";

    private int MatchCount => Hi - Lo + 1;        // 10_001 — the true two-sided intersection
    private int OneSidedCount => Hi;              // 20_000 — AMOUNT <= hi (what Highlike scans today)

    // ---------------------------------------------------------- a brute-force ground truth

    private sealed class Row : IRowContext
    {
        private readonly DbfRecord _rec;
        public Row(DbfRecord rec, int recNo, int recCount) { _rec = rec; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name) => _rec.GetExpressionValue(name);
    }

    private static int[] FullScan(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            if (ctx.Deleted && rec.IsDeleted) continue;
            var v = compiled(new Row(rec, recno, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical) hits.Add(recno);
        }
        return hits.OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// A stand-in cost policy that reproduces EXACTLY the planner's pathological choice: it
    /// DEMOTES every LOWER-bound leaf (> or >=) to the residual (as the real
    /// <c>HighlikeCostPlanner.PrefersFullScan</c> does for a bound that matches &gt; the
    /// threshold of the table) while keeping the upper bound. With this policy the optimizer
    /// must STILL recognise the same-tag low+high pair as one window — otherwise the candidate
    /// collapses to the one-sided <c>&lt;= hi</c> run.
    /// </summary>
    private sealed class DropLowerBoundPolicy : IOptimizerPolicy
    {
        public bool LiftGuard(CdxTag tag, string conditionText) => false;
        // ">" appears only in ">" and ">=" (never in "<", "<=", "="), so this drops exactly the
        // lower-bound leaves and keeps the upper-bound leaf.
        public bool PreferFullScan(CdxTag tag, string conditionText) => conditionText.Contains('>');
    }

    // ---------------------------------------------------------- (2a) Highlike RecordsScanned

    /// <summary>
    /// THE INVERSION, pinned via <see cref="QueryResult.RecordsScanned"/>: with statistics ON the
    /// Highlike engine must scan ~the WINDOW (10_001), NOT the one-sided <c>&lt;= hi</c> count
    /// (20_000). Core (no cost planner) already fuses the window and scans ~10_001 (the control).
    /// Today the cost-planner drops the wide <c>&gt;= lo</c> leaf and Highlike scans 20_000 → RED.
    /// </summary>
    [Fact]
    public void Highlike_TwoSidedWindow_ScansTheWindow_NotTheOneSidedCount()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var ctx = EvaluationContext.Default; // SET DELETED ON → the candidate (late-materialization) path runs
        var expected = FullScan(table, WindowFilter, ctx);
        Assert.Equal(MatchCount, expected.Length); // fixture sanity: the window matches exactly hi-lo+1

        var core = QueryOptimizer.FindRecords(table, cdx, WindowFilter, ctx);
        using var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });
        var hi = engine.FindRecords(table, cdx, WindowFilter, ctx);

        // Result invariance holds regardless (the hard gate).
        Assert.Equal(expected, core.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.Equal(expected, hi.RecordNumbers.OrderBy(x => x).ToArray());

        // CONTROL (green today): Core fuses the two bounds → scans ~the true intersection.
        Assert.True(core.Optimized);
        Assert.Equal(MatchCount, core.RecordsScanned);

        // THE PROOF (red today): the cost-planned path must ALSO scan ~the window, not the
        // one-sided count. Today it drops `>= lo` and scans `<= hi` == OneSidedCount (20_000).
        Assert.True(hi.Optimized, "the two-sided window must drive the index");
        Assert.True(hi.RecordsScanned <= MatchCount + 50,
            $"Highlike scanned {hi.RecordsScanned}; a fused window should scan ~{MatchCount} (the intersection), not the one-sided count {OneSidedCount}");
        Assert.True(hi.RecordsScanned < OneSidedCount,
            $"Highlike scanned {hi.RecordsScanned} == the one-sided <= hi count ({OneSidedCount}); the low+high pair was NOT merged into one window");
    }

    // ---------------------------------------------------------- (2b) Core entry-visit counter

    /// <summary>
    /// THE INVERSION, pinned deterministically via the onIndexEntryExamined visit counter and a
    /// policy that demotes the lower bound (the cost-planner's choice). The candidate BUILD must
    /// examine ~the WINDOW (matches + log slack), NOT the one-sided <c>&lt;= hi</c> run. Today,
    /// with the lower bound demoted and no window merge, the build seeks <c>[open, hi]</c> and
    /// visits ~20_000 entries → RED. With the merge it seeks <c>[lo, hi]</c> and visits ~10_001.
    /// </summary>
    [Theory]
    [InlineData(false)] // COLD path (B-tree descend + forward walk)
    [InlineData(true)]  // WARM Highlike path (binary-search the cached decoded directory)
    public void Window_WithLowerBoundDemoted_VisitsTheWindow_NotTheOneSidedRun(bool warm)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, WindowFilter, ctx);

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
        var result = QueryOptimizer.FindRecords(table, cdx, WindowFilter, ctx,
            policy: new DropLowerBoundPolicy(), entrySource: src, onIndexEntryExamined: _ => visits++);

        // Result invariance holds even with the lower bound demoted (the hard gate).
        Assert.Equal(expected, result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(result.Optimized, "the two-sided window must drive the index even when one bound is cost-demoted");

        // THE PROOF (red today): a fused window seek touches ~matches + log slack, FAR below the
        // one-sided run. Today the demoted lower bound leaves only `<= hi`, walking ~OneSidedCount.
        Assert.True(visits <= MatchCount + 4096,
            $"[warm={warm}] examined {visits} entries; a fused window should visit ~{MatchCount} + log slack, not the one-sided run (~{OneSidedCount})");
        Assert.True(visits < OneSidedCount,
            $"[warm={warm}] examined {visits} entries (>= the one-sided <= hi run {OneSidedCount}); the low+high pair was NOT merged into one window");
    }

    // ------------------------------------------ (2c) MUST-FIX 1: Eq + demoted wide range

    /// <summary>
    /// REGRESSION GUARD for MUST-FIX 1 (Ordered() must NOT defer an Eq leaf into a same-tag wide
    /// bound). For <c>AMOUNT = v AND AMOUNT &gt;= lo</c> the wide lower bound is cost-demoted
    /// (DropLowerBoundPolicy drops every leaf containing '&gt;'), but the EQUALITY is a tight point
    /// lookup that must STILL drive the index — the intersection is the single point <c>{v}</c>, not
    /// the table. Before the fix the Eq was deferred, merged into the wide bound, inherited its
    /// PrefersScan and FULL-SCANNED the point: the index build was never entered, so the visit
    /// counter stayed at 0. The two assertions pin both halves: <c>visits &gt; 0</c> proves a real
    /// seek ran (NOT the full-table-scan demote), and <c>visits</c> stays far below the one-sided run.
    /// </summary>
    [Theory]
    [InlineData(false)] // COLD path (B-tree descend + forward walk)
    [InlineData(true)]  // WARM Highlike path (binary-search the cached decoded directory)
    public void EqPlusDemotedWideRange_SeeksThePoint_NotTheTable(bool warm)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        const int point = 15_000;
        const string filter = "AMOUNT = 15000 AND AMOUNT >= 10000";

        var ctx = EvaluationContext.Default; // SET DELETED ON → the candidate path runs
        var expected = FullScan(table, filter, ctx);
        Assert.Single(expected);                 // the point matches exactly one row
        Assert.Equal(point, expected[0]);        // AMOUNT is unique 1..N, so recno == value

        Func<CdxTag, IEnumerable<IndexEntry>>? src = WarmSource(warm);

        long visits = 0;
        var result = QueryOptimizer.FindRecords(table, cdx, filter, ctx,
            policy: new DropLowerBoundPolicy(), entrySource: src, onIndexEntryExamined: _ => visits++);

        // Hard gate: the result equals the full scan even with the wide bound demoted.
        Assert.Equal(expected, result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(result.Optimized, "the equality point must drive the index even when the sibling range is cost-demoted");

        // THE PROOF: a real point seek ran (visits > 0, i.e. NOT the full-table-scan demote) and it
        // touched only ~1 + log slack — far below the one-sided `>= lo` run the demote would walk.
        Assert.True(visits > 0,
            $"[warm={warm}] examined 0 index entries — the Eq leaf was deferred into the wide bound and full-scanned the point instead of seeking it");
        Assert.True(visits <= 4096,
            $"[warm={warm}] examined {visits} entries; a point lookup should visit ~1 + log slack, not the one-sided run (~{OneSidedCount})");
        Assert.True(result.RecordsScanned < OneSidedCount,
            $"[warm={warm}] residual scanned {result.RecordsScanned}; a point lookup scans ~1, not the one-sided count {OneSidedCount}");
    }

    // ------------------------------------ (2d) MUST-FIX 1: two same-direction bounds, merged estimate

    /// <summary>
    /// A stand-in policy for the two-same-direction case: it demotes ONLY the WIDE lower bound
    /// (literal <c>&gt;= 10</c>) while keeping the TIGHTER, selective lower bound (<c>&gt;= 90000</c>)
    /// — exactly the real cost-planner's verdict (the wide bound exceeds the index-vs-scan threshold,
    /// the tight one does not). With it, the optimizer must INTERSECT the two same-tag lower bounds
    /// into the tighter window and drive THAT; OR-ing the per-leaf PrefersScan (the pre-fix behaviour)
    /// instead let the wide sibling poison the selective one and collapse to a full scan.
    /// </summary>
    private sealed class DemoteWideLowerPolicy : IOptimizerPolicy
    {
        public bool LiftGuard(CdxTag tag, string conditionText) => false;
        // ">=10" appears in "AMOUNT>=10" but not in "AMOUNT>=90000" — drops only the wide bound.
        public bool PreferFullScan(CdxTag tag, string conditionText)
            => conditionText.Replace(" ", string.Empty).Contains(">=10");
    }

    /// <summary>
    /// REGRESSION GUARD for MUST-FIX 1 (the merged window estimate must drive the decision, not the
    /// OR-ed per-leaf PrefersScan). For two same-direction bounds <c>AMOUNT &gt;= 90000 AND AMOUNT
    /// &gt;= 10</c> the intersection is the TIGHTER bound <c>&gt;= 90000</c> (10_001 rows). The wide
    /// sibling <c>&gt;= 10</c> is cost-demoted, but because intersection only TIGHTENS the window the
    /// merged candidate is at least as selective as the kept bound and must be seeked. Pre-fix the
    /// OR-ed PrefersScan demoted the whole merge to a full table scan (the index build never ran →
    /// 0 visits); the fix AND-s the cost verdict, so the tighter window is driven.
    /// </summary>
    [Theory]
    [InlineData(false)] // COLD path
    [InlineData(true)]  // WARM Highlike path
    public void TwoSameDirectionLowerBounds_SeekTheTighter_NotTheTable(bool warm)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        const int tightLo = 90_000;
        const string filter = "AMOUNT >= 90000 AND AMOUNT >= 10";
        int tighterCount = _fx.RowCount - tightLo + 1; // 10_001 — the >= 90000 intersection
        int widerCount = _fx.RowCount - 10 + 1;        // 99_991 — the demoted wide bound alone

        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, filter, ctx);
        Assert.Equal(tighterCount, expected.Length); // fixture sanity

        Func<CdxTag, IEnumerable<IndexEntry>>? src = WarmSource(warm);

        long visits = 0;
        var result = QueryOptimizer.FindRecords(table, cdx, filter, ctx,
            policy: new DemoteWideLowerPolicy(), entrySource: src, onIndexEntryExamined: _ => visits++);

        // Hard gate: result equals the full scan regardless of the cost demote.
        Assert.Equal(expected, result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(result.Optimized, "the tighter same-direction bound must drive the index");

        // THE PROOF: a real seek over the TIGHTER window ran (visits > 0, not the full-table-scan
        // demote) and touched ~the tighter count, never the wide one-sided run / the whole table.
        Assert.True(visits > 0,
            $"[warm={warm}] examined 0 index entries — the OR-ed PrefersScan collapsed both same-direction bounds to a full scan");
        Assert.True(visits <= tighterCount + 4096,
            $"[warm={warm}] examined {visits} entries; the merged window should visit ~{tighterCount} + log slack, not the wide run (~{widerCount})");
        Assert.True(visits < widerCount,
            $"[warm={warm}] examined {visits} entries (>= the wide >= 10 run {widerCount}); the same-direction bounds were NOT merged on the tighter estimate");
    }

    /// <summary>The optional Highlike warm-cache entry source (a per-tag memoized decoded directory).</summary>
    private static Func<CdxTag, IEnumerable<IndexEntry>>? WarmSource(bool warm)
    {
        if (!warm) return null;
        var cache = new Dictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
        return tag =>
        {
            if (!cache.TryGetValue(tag.Name, out var list))
                cache[tag.Name] = list = tag.EnumerateEntries().ToList();
            return list;
        };
    }

    // -------------------------------------------------------------- shared large temp table

    /// <summary>
    /// One LARGE temp table (100_000 rows) whose AMOUNT (Numeric) is UNIQUE 1..N, so an inclusive
    /// window [lo,hi] matches exactly hi-lo+1 rows and the one-sided counts are known precisely.
    /// CAT is an UNINDEXED residual column. Big enough that the one-sided count (20_000) vs the
    /// window (10_001) vs the table (100_000) are unmistakably distinct. Built once via the writer;
    /// deleted on dispose. The single AMOUNT tag is enough — the cost planner's min/max come from it.
    /// </summary>
    public sealed class LargeWindowTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 100_000;

        public LargeWindowTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_window_big_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "winbig.dbf");

            var cols = new[]
            {
                new DbfColumnDef("AMOUNT", 'N', 12, 0), // integer-valued numeric, unique 1..N
                new DbfColumnDef("CAT", 'C', 4),         // unindexed residual column
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                    w.AppendRecord(new object?[] { (double)i, "C" + (i % 5) });

                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
