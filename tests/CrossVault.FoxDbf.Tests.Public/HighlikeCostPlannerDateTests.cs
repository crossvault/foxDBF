using System;
using System.Collections.Generic;
using System.Globalization;
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

/// <summary>
/// Phase A regression suite for the TWO Highlike cost-planner fixes the benchmark surfaced:
///
/// <list type="number">
///   <item><b>DATE / DATETIME selectivity:</b> <see cref="HighlikeCostPlanner.EstimateRows"/> must
///   estimate Date / DateTime predicates the SAME way Numeric ones are — equality ≈
///   <c>reccount / max(ndv,1)</c> with exact min/max pruning, range / BETWEEN ≈ linear min/max
///   interpolation — reading the <c>ndv</c> + ISO-date <c>min</c>/<c>max</c> the <c>.stx</c> already
///   stores and parsing the VFP date/datetime literal form <c>{^yyyy-mm-dd[ hh:mm:ss]}</c> as a
///   constant. A selective date equality must therefore be OPTIMIZED (the date tag drives the
///   candidate set), not wrongly demoted to a full scan.</item>
///
///   <item><b>The PRINCIPLE — ignorance must never DISABLE a usable index:</b>
///   <see cref="PlanReason.CostPrefersScan"/> may fire ONLY when the high estimate is BACKED BY REAL
///   STATISTICS (a tag with a usable <c>.stx</c> entry whose stats-based fraction exceeds the
///   threshold). When a leaf is NOT estimatable (no stats for that tag / unknown literal / the
///   <see cref="HighlikeOptions.DefaultLeafFraction"/> is used) the planner must KEEP the index — the
///   default fraction is for ORDERING only, never for the index-vs-scan cutoff.</item>
/// </list>
///
/// INVARIANT (never violated): Highlike result == Core result == full scan, with stats present,
/// absent, or stale. These fixes only change the PLAN (which index is driven), never the result set.
///
/// SAFETY: every table + sidecar lives under a freshly created TEMP directory, deleted on dispose;
/// no committed fixture under data/ is ever touched.
/// </summary>
public sealed class HighlikeCostPlannerDateTests : IClassFixture<HighlikeCostPlannerDateTests.DateTable>
{
    private readonly DateTable _fx;
    public HighlikeCostPlannerDateTests(DateTable fx) => _fx = fx;

    // ============================================================ brute-force ground truth

    private sealed class DbfRow : IRowContext
    {
        private readonly DbfRecord _rec;
        public DbfRow(DbfRecord rec, int recNo, int recCount) { _rec = rec; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name) => _rec.GetExpressionValue(name);
    }

    private static List<int> FullScan(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            if (ctx.Deleted && rec.IsDeleted) continue;
            var v = compiled(new DbfRow(rec, recno, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical)
                hits.Add(recno);
        }
        return hits;
    }

    private static int[] Sorted(IEnumerable<int> xs) => xs.OrderBy(x => x).ToArray();

    private static HighlikeCostPlanner Planner(DbfTable table, CdxFile cdx, HighlikeOptions? options = null)
    {
        var stats = HighlikeStatistics.Build(table, cdx);
        return new HighlikeCostPlanner(stats, table.RecordCount, options ?? new HighlikeOptions(), cdx);
    }

    /// <summary>Assert <paramref name="estimate"/> is within <paramref name="factor"/>× of the true count (both directions).</summary>
    private static void AssertWithinFactor(double trueCount, double estimate, double factor = 3.0)
    {
        Assert.True(estimate >= 0, $"estimate must be non-negative, got {estimate}");
        if (trueCount <= 0)
        {
            Assert.True(estimate <= factor, $"expected ~0 (<= {factor}), got {estimate}");
            return;
        }
        double lo = trueCount / factor, hi = trueCount * factor;
        Assert.True(estimate >= lo && estimate <= hi,
            $"estimate {estimate} not within {factor}x of true {trueCount} (expected [{lo:0.##},{hi:0.##}])");
    }

    // The shared fixture's base instants (50000 → 5000 rows, date = base + i%365 → NDV 365).
    private static readonly DateOnly DateBase = new(2000, 1, 1);
    private static readonly DateTime TsBase = new(2000, 1, 1, 0, 0, 0);

    private static string DateLit(DateOnly d) => "{^" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "}";
    private static string TsLit(DateTime t) => "{^" + t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "}";

    // ============================================================ (1) DATE / DATETIME — EstimateRows UNIT

    /// <summary>
    /// A selective DATE equality must estimate ≈ <c>reccount / ndv</c> (the date tag has NDV 365 over
    /// 5000 rows → ~14 rows), NOT the <see cref="HighlikeOptions.DefaultLeafFraction"/> (~1650). TODAY
    /// the planner does not parse <c>{^…}</c>, falls back to the default fraction, and FAILS this.
    /// </summary>
    [Fact]
    public void EstimateRows_DateEquality_NearTrueCount()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        var d = DateBase.AddDays(60);
        double trueCount = FullScan(table, $"ADATE = {DateLit(d)}", EvaluationContext.Default).Count;
        Assert.True(trueCount > 0, "fixture sanity: the chosen date must match some rows");

        AssertWithinFactor(trueCount, planner.EstimateRows($"ADATE = {DateLit(d)}"));
    }

    /// <summary>A selective DATETIME equality must likewise estimate ≈ <c>reccount / ndv</c> (~14 rows).</summary>
    [Fact]
    public void EstimateRows_DateTimeEquality_NearTrueCount()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        var t = TsBase.AddDays(60);
        double trueCount = FullScan(table, $"ATS = {TsLit(t)}", EvaluationContext.Default).Count;
        Assert.True(trueCount > 0, "fixture sanity: the chosen instant must match some rows");

        AssertWithinFactor(trueCount, planner.EstimateRows($"ATS = {TsLit(t)}"));
    }

    /// <summary>
    /// EXACT min/max pruning for dates: a date equality on a constant OUTSIDE the observed
    /// <c>[min,max]</c> (the stored ISO-date bounds) estimates exactly 0 — design §7.
    /// </summary>
    [Fact]
    public void EstimateRows_DateEqualityOutsideMinMax_IsZero()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        // The fixture's dates are all in calendar year 2000; these are provably outside [min,max].
        Assert.Equal(0d, planner.EstimateRows($"ADATE = {DateLit(new DateOnly(1990, 1, 1))}"), 6);
        Assert.Equal(0d, planner.EstimateRows($"ADATE = {DateLit(new DateOnly(2010, 12, 31))}"), 6);
    }

    /// <summary>A narrow date BETWEEN must estimate by linear min/max interpolation (~the window's day-fraction).</summary>
    [Fact]
    public void EstimateRows_NarrowDateBetween_LinearInterpolation()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        var lo = DateBase.AddDays(31);  // 2000-02-01
        var hi = DateBase.AddDays(35);  // 2000-02-05 — a 5-day window of a ~365-day span
        string filter = $"BETWEEN(ADATE, {DateLit(lo)}, {DateLit(hi)})";

        double trueCount = FullScan(table, filter, EvaluationContext.Default).Count;
        Assert.True(trueCount > 0, "fixture sanity: the BETWEEN window must match some rows");

        AssertWithinFactor(trueCount, planner.EstimateRows(filter), factor: 4.0);
    }

    // ============================================================ (1) DATE / DATETIME — index-vs-scan (UNIT)

    /// <summary>
    /// A selective date equality (~1/365 ≈ 0.27% &lt;&lt; the 3% threshold) must KEEP the index
    /// (<see cref="HighlikeCostPlanner.PrefersFullScan"/> false), while a date range that covers MOST of
    /// the table (~91% &gt;&gt; 3%) correctly prefers a scan. TODAY the selective equality is wrongly
    /// reported as preferring a scan (default fraction 0.33 &gt; 0.03).
    /// </summary>
    [Fact]
    public void PrefersFullScan_SelectiveDate_KeepsIndex_WideDateRange_PrefersScan()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx, new HighlikeOptions { IndexVsScanThreshold = 0.03 });

        // Selective equality → keep the index.
        Assert.False(planner.PrefersFullScan($"ADATE = {DateLit(DateBase.AddDays(60))}"));
        Assert.False(planner.PrefersFullScan($"ATS = {TsLit(TsBase.AddDays(60))}"));
        // Narrow BETWEEN (~5/365) → keep the index.
        Assert.False(planner.PrefersFullScan(
            $"BETWEEN(ADATE, {DateLit(DateBase.AddDays(31))}, {DateLit(DateBase.AddDays(35))})"));

        // A range matching most of the table → prefer a scan (this part is CORRECT, must stay true).
        Assert.True(planner.PrefersFullScan($"ADATE >= {DateLit(DateBase.AddDays(31))}"));
    }

    // ============================================================ (1) DATE / DATETIME — PLAN EFFECT (engine)

    /// <summary>
    /// THE bug, end to end: with statistics ON, a selective DATE equality must be OPTIMIZED — the date
    /// tag drives the candidate set (ShowPlan Overall = Full, the date tag among the driving tags), and
    /// the residual confirms ≈ the match count, NOT the whole table. TODAY it is wrongly planned as a
    /// full scan (None / CostPrefersScan), so this FAILS. Result invariance holds regardless.
    /// </summary>
    [Fact]
    public void PlanEffect_SelectiveDateEquality_IsOptimized_NotFullScanned()
    {
        using var local = new IsolatedDateTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);
        HighlikeStatistics.Analyze(table, cdx); // a real, fresh sidecar (NDV 365, ISO min/max)

        string filter = $"ADATE = {DateLit(DateBase.AddDays(60))}";
        var expected = FullScan(table, filter, EvaluationContext.Default);
        var core = QueryOptimizer.FindRecords(table, cdx, filter);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });

        // ShowPlan: the date tag drives the query (the leaf is optimized, not residual).
        var plan = engine.ExplainPlan(table, cdx, filter);
        Assert.Equal(OptimizationLevel.Full, plan.CorePlan.Overall);
        Assert.Contains("TDATE", plan.DrivingTags);

        var hi = engine.FindRecords(table, cdx, filter);

        // The selective index drove the candidate set — far fewer than a full scan.
        Assert.True(hi.Optimized, "a selective date equality must be index-optimized, not full-scanned");
        Assert.True(hi.RecordsScanned <= 100,
            $"the date index should keep the residual ~the match count, got {hi.RecordsScanned}");
        Assert.True(hi.RecordsScanned * 5 < local.RowCount,
            $"expected far fewer than a full scan, got {hi.RecordsScanned} of {local.RowCount}");

        // HINT-ONLY invariant: same answer as Core and a full scan.
        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    /// <summary>Mirror of the date-equality plan effect for a selective DATETIME equality (the <c>TTS</c> tag).</summary>
    [Fact]
    public void PlanEffect_SelectiveDateTimeEquality_IsOptimized_NotFullScanned()
    {
        using var local = new IsolatedDateTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);
        HighlikeStatistics.Analyze(table, cdx);

        string filter = $"ATS = {TsLit(TsBase.AddDays(60))}";
        var expected = FullScan(table, filter, EvaluationContext.Default);
        var core = QueryOptimizer.FindRecords(table, cdx, filter);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });

        var plan = engine.ExplainPlan(table, cdx, filter);
        Assert.Equal(OptimizationLevel.Full, plan.CorePlan.Overall);
        Assert.Contains("TTS", plan.DrivingTags);

        var hi = engine.FindRecords(table, cdx, filter);
        Assert.True(hi.Optimized, "a selective datetime equality must be index-optimized");
        Assert.True(hi.RecordsScanned * 5 < local.RowCount,
            $"expected far fewer than a full scan, got {hi.RecordsScanned} of {local.RowCount}");

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    /// <summary>A narrow date BETWEEN must also be index-optimized (selective window), with invariance.</summary>
    [Fact]
    public void PlanEffect_NarrowDateBetween_IsOptimized()
    {
        using var local = new IsolatedDateTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);
        HighlikeStatistics.Analyze(table, cdx);

        string filter = $"BETWEEN(ADATE, {DateLit(DateBase.AddDays(31))}, {DateLit(DateBase.AddDays(35))})";
        var expected = FullScan(table, filter, EvaluationContext.Default);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });
        var plan = engine.ExplainPlan(table, cdx, filter);
        Assert.Equal(OptimizationLevel.Full, plan.CorePlan.Overall);
        Assert.Contains("TDATE", plan.DrivingTags);

        var hi = engine.FindRecords(table, cdx, filter);
        Assert.True(hi.Optimized, "a narrow date BETWEEN must be index-optimized");
        Assert.True(hi.RecordsScanned * 5 < local.RowCount,
            $"expected far fewer than a full scan, got {hi.RecordsScanned} of {local.RowCount}");
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    /// <summary>
    /// CONTROL (correct behaviour, must stay): a date range matching MOST of the table SHOULD prefer a
    /// full scan — the index-vs-scan threshold is rightly exceeded. Result invariance holds.
    /// </summary>
    [Fact]
    public void PlanEffect_WideDateRange_PrefersFullScan()
    {
        using var local = new IsolatedDateTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);
        HighlikeStatistics.Analyze(table, cdx);

        string filter = $"ADATE >= {DateLit(DateBase.AddDays(31))}"; // ~91% of the table
        var expected = FullScan(table, filter, EvaluationContext.Default);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });
        var hi = engine.FindRecords(table, cdx, filter);

        Assert.False(hi.Optimized, "a date range matching most of the table should prefer a full scan");
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    // ============================================================ (2) IGNORANCE DOES NOT DISABLE THE INDEX

    /// <summary>
    /// THE PRINCIPLE at the UNIT level: a leaf the planner CANNOT estimate (no usable <c>.stx</c> entry
    /// for the key → the <see cref="HighlikeOptions.DefaultLeafFraction"/> is used) must NOT prefer a
    /// scan — the default fraction is for ORDERING only, never the index-vs-scan cutoff. Only a
    /// STATS-BACKED over-threshold estimate may. TODAY <c>PrefersFullScan</c> is purely
    /// <c>defaultFraction (0.33) &gt; threshold (0.03)</c> and so wrongly returns true here.
    /// </summary>
    [Fact]
    public void PrefersFullScan_NonEstimatableLeaf_KeepsIndex_StatsBackedOnly_PrefersScan()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx, new HighlikeOptions { IndexVsScanThreshold = 0.03, DefaultLeafFraction = 0.33 });

        // Ignorance: no tag/stat backs this key → default fraction → must NOT disable the index.
        Assert.False(planner.PrefersFullScan("NOPE = 5"),
            "an un-estimatable leaf (default fraction) must never trigger a cost-based full scan");

        // Stats-backed AND over threshold (HOT has NDV 2 → ~50%) → a full scan is legitimately preferred.
        Assert.True(planner.PrefersFullScan("HOT = 1"),
            "a STATS-BACKED over-threshold estimate may prefer a scan");

        // Stats-backed AND selective (ID is unique) → keep the index.
        Assert.False(planner.PrefersFullScan("ID = 1234"));
    }

    /// <summary>
    /// THE PRINCIPLE end to end: with a FRESH but EMPTY <c>.stx</c> (the staleness token matches, so the
    /// planner trusts it, but it carries NO per-tag stats), a usable indexed equality (<c>ID = 1234</c>)
    /// must STILL be driven by its index — ignorance must not turn it into a cost-based full scan. TODAY
    /// the empty stats yield the default fraction → <see cref="PlanReason.CostPrefersScan"/> → a full
    /// scan, so this FAILS. Result invariance holds.
    /// </summary>
    [Fact]
    public void PlanEffect_FreshButStatlessTag_StillDrivesIndex_NotCostScan()
    {
        using var local = new IsolatedDateTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        WriteFreshEmptyStx(table, local); // fresh token, ZERO tag stats

        const string filter = "ID = 1234";
        var expected = FullScan(table, filter, EvaluationContext.Default);
        var core = QueryOptimizer.FindRecords(table, cdx, filter);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });

        var plan = engine.ExplainPlan(table, cdx, filter);
        Assert.Equal(OptimizationLevel.Full, plan.CorePlan.Overall);
        Assert.Contains("TID", plan.DrivingTags);
        Assert.DoesNotContain(plan.CorePlan.Conditions, c => c.Reason == PlanReason.CostPrefersScan);

        var hi = engine.FindRecords(table, cdx, filter);
        Assert.True(hi.Optimized, "ignorance (no stats for the tag) must not disable a usable index");
        Assert.True(hi.RecordsScanned * 5 < local.RowCount,
            $"the index should still narrow the candidates, got {hi.RecordsScanned} of {local.RowCount}");

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    // ============================================================ (3) RESULT INVARIANCE (the must)

    public static IEnumerable<object[]> DateInvarianceFilters() => new[]
    {
        new object[] { "ADATE = {^2000-03-01}" },                          // date equality (selective)
        new object[] { "ADATE = {^1990-01-01}" },                          // date equality, empty result
        new object[] { "ATS = {^2000-03-01 00:00:00}" },                   // datetime equality
        new object[] { "BETWEEN(ADATE, {^2000-02-01}, {^2000-02-05})" },   // narrow date BETWEEN
        new object[] { "ADATE >= {^2000-02-01}" },                         // wide date range (scan)
        new object[] { "ADATE = {^2000-03-01} AND HOT = 1" },              // date AND a low-selectivity leaf
        new object[] { "ADATE = {^2000-03-01} OR ID = 1" },                // date OR
        new object[] { "NOT (ADATE = {^2000-03-01})" },                    // NOT over a date equality
    };

    /// <summary>Stats ABSENT (EnableStatistics off): Highlike == Core == full scan for every date filter.</summary>
    [Theory]
    [MemberData(nameof(DateInvarianceFilters))]
    public void Invariance_StatsAbsent(string filter)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine().FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    /// <summary>Stats PRESENT (a real, fresh sidecar): Highlike == Core == full scan for every date filter.</summary>
    [Theory]
    [MemberData(nameof(DateInvarianceFilters))]
    public void Invariance_StatsPresent(string filter)
    {
        using var local = new IsolatedDateTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);
        HighlikeStatistics.Analyze(table, cdx);

        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true })
            .FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    /// <summary>Stats FRESH but EMPTY (no per-tag entries — the ignorance case): result still Core / full scan.</summary>
    [Theory]
    [MemberData(nameof(DateInvarianceFilters))]
    public void Invariance_StatsFreshButEmpty(string filter)
    {
        using var local = new IsolatedDateTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);
        WriteFreshEmptyStx(table, local);

        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true })
            .FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    // ============================================================ helpers + temp tables

    /// <summary>
    /// Persist a FRESH-token <c>.stx</c> with ZERO per-tag stats: the <c>src</c> block matches every
    /// staleness signal (reccount + header date stamp + dbfutc + cdxutc) so the planner TRUSTS and
    /// reuses it (no lazy rebuild), but it has no per-tag selectivity — the canonical "no stats for this
    /// tag" ignorance case.
    /// </summary>
    private static void WriteFreshEmptyStx(DbfTable table, IsolatedDateTable local)
    {
        string stxPath = HighlikeStatistics.StxPath(table);
        string dbfName = Path.GetFileName(local.Dbf);

        string stamp;
        using (var fs = File.OpenRead(local.Dbf))
        {
            var h = new byte[4];
            fs.ReadExactly(h, 0, 4);
            stamp = $"{1900 + h[1]:D4}-{h[2]:D2}-{h[3]:D2}";
        }
        string dbfUtc = File.GetLastWriteTimeUtc(local.Dbf).ToString("O", CultureInfo.InvariantCulture);
        string cdxUtc = File.GetLastWriteTimeUtc(local.Cdx).ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllText(stxPath, $$"""
        {
          "src": { "dbf": "{{dbfName}}", "reccount": {{table.RecordCount}}, "updstamp": "{{stamp}}", "dbfutc": "{{dbfUtc}}", "cdxutc": "{{cdxUtc}}", "deleted": 0 },
          "built": "2000-01-01T00:00:00Z",
          "tags": {}
        }
        """);
    }

    /// <summary>
    /// A read-only shared fixture: 5000 rows with high-NDV DATE / DATETIME tags plus an integer ID tag
    /// and a low-selectivity HOT tag —
    /// <list type="bullet">
    ///   <item>ID (Integer, UNIQUE) — high selectivity, the ignorance control.</item>
    ///   <item>HOT (Integer, 2 distinct ~50/50) — LOW selectivity, the stats-backed scan control.</item>
    ///   <item>ADATE (Date) — date = 2000-01-01 + (i % 365) → NDV 365, ~14 rows/date.</item>
    ///   <item>ATS (DateTime) — midnight instants, 2000-01-01 + (i % 365) days → NDV 365, ~14/instant.</item>
    /// </list>
    /// TEMP only — deleted on dispose.
    /// </summary>
    public sealed class DateTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 5000;

        public DateTable() => (Dir, Dbf, Cdx) = Build("foxdbf_planner_date_");
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }

        internal static (string dir, string dbf, string cdx) Build(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dbf = Path.Combine(dir, "pd.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("HOT", 'I', 4),
                new DbfColumnDef("ADATE", 'D', 8),
                new DbfColumnDef("ATS", 'T', 8),
            };

            var dateBase = new DateOnly(2000, 1, 1);
            var tsBase = new DateTime(2000, 1, 1, 0, 0, 0);

            using (var w = DbfWriter.Create(dbf, cols))
            {
                for (int i = 1; i <= 5000; i++)
                {
                    int hot = i % 2;                       // NDV 2, ~50% each
                    var adate = dateBase.AddDays(i % 365); // NDV 365, ~14 rows/date
                    var ats = tsBase.AddDays(i % 365);     // NDV 365, midnight instants
                    w.AppendRecord(new object?[] { i, hot, adate, ats });
                }

                w.CreateTag(new CdxTagDefinition("TID", "ID"));
                w.CreateTag(new CdxTagDefinition("THOT", "HOT"));
                w.CreateTag(new CdxTagDefinition("TDATE", "ADATE"));
                w.CreateTag(new CdxTagDefinition("TTS", "ATS"));
            }

            return (dir, dbf, Path.ChangeExtension(dbf, ".cdx"));
        }
    }

    /// <summary>A private per-test clone of <see cref="DateTable"/> for tests that PERSIST a sidecar. TEMP only.</summary>
    private sealed class IsolatedDateTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 5000;

        public IsolatedDateTable() => (Dir, Dbf, Cdx) = DateTable.Build("foxdbf_planner_date_iso_");
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }
}
