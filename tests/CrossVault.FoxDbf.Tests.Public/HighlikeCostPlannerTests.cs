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
/// Phase A of the Highlike accelerator: the COST-BASED PLANNER (design §7) — <c>EstimateRows</c>,
/// AND-ordering, index-vs-scan, and low-selectivity guard lifting.
///
/// TDD focus is EDGE / ADVERSARIAL with the INVARIANT first:
/// <list type="bullet">
///   <item><b>RESULT-INVARIANCE (the must):</b> for equality / range / AND / OR / NOT / mixed filters
///   (MACHINE + GENERAL, SET EXACT / SET DELETED variants) the Highlike-planned result == the Core
///   <see cref="QueryOptimizer"/> result == a full scan — with stats PRESENT, ABSENT, and deliberately
///   WRONG. The planner may only change the PLAN (speed), never the result set.</item>
///   <item><b>PLAN EFFECT (the value):</b> measured via <see cref="QueryResult.RecordsScanned"/> and the
///   <see cref="HighlikeQueryPlan"/> report — selective-first AND ordering, a >threshold predicate
///   handled as a full scan, and a Core-skipped NOT-keyed tag the stats prove selective being USED
///   (guard lifted), while a non-selective one stays skipped.</item>
///   <item><b>EstimateRows UNIT:</b> equality / range / AND / OR within a sane factor of the true
///   counts; equality on a constant outside <c>[min,max]</c> estimates 0.</item>
/// </list>
///
/// SAFETY: every table + sidecar lives under a freshly created TEMP directory, deleted on dispose;
/// no committed fixture under data/ is ever touched.
/// </summary>
public sealed class HighlikeCostPlannerTests : IClassFixture<HighlikeCostPlannerTests.PlannerTable>
{
    private readonly PlannerTable _fx;
    public HighlikeCostPlannerTests(PlannerTable fx) => _fx = fx;

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

    // ============================================================ EstimateRows UNIT tests

    [Fact]
    public void EstimateRows_Equality_NearTrueCount()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        // ID is unique → reccount / ndv ≈ 1; AMOUNT = 50 matches exactly 50 rows.
        AssertWithinFactor(1, planner.EstimateRows("ID = 1234"));
        AssertWithinFactor(50, planner.EstimateRows("AMOUNT = 50"));
        AssertWithinFactor(100, planner.EstimateRows("UPPER(NAME) = 'N007'"));
    }

    [Fact]
    public void EstimateRows_EqualityOutsideMinMax_IsZero()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        // ID ∈ [1, 5000]; a constant outside the observed range prunes to exactly 0 (design §7).
        Assert.Equal(0d, planner.EstimateRows("ID = 999999"), 6);
        Assert.Equal(0d, planner.EstimateRows("ID = -5"), 6);
    }

    [Fact]
    public void EstimateRows_Range_LinearInterpolation()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        // AMOUNT ∈ [0,99]; BETWEEN 25..74 covers 50/100 of values → ~2500 rows.
        AssertWithinFactor(2500, planner.EstimateRows("BETWEEN(AMOUNT, 25, 74)"));
        // One-sided range: AMOUNT >= 90 → ~10% → ~500 rows.
        AssertWithinFactor(500, planner.EstimateRows("AMOUNT >= 90"));
    }

    [Fact]
    public void EstimateRows_And_ProductOfFractions()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        // AMOUNT = 50 (≈50 rows) AND ID <= 2500 (≈half) → product ≈ 25.
        AssertWithinFactor(25, planner.EstimateRows("AMOUNT = 50 AND ID <= 2500"));
        // An AND is never larger than its most selective leaf.
        double and = planner.EstimateRows("AMOUNT = 50 AND ID <= 2500");
        double sel = planner.EstimateRows("AMOUNT = 50");
        Assert.True(and <= sel + 1e-6, $"AND estimate {and} must not exceed most-selective leaf {sel}");
    }

    [Fact]
    public void EstimateRows_Or_InclusionExclusion()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx);

        // ID = 1 (1 row) OR AMOUNT = 50 (50 rows), disjoint → ≈51 rows.
        AssertWithinFactor(51, planner.EstimateRows("ID = 1 OR AMOUNT = 50"));
        // An OR is at least as large as its largest leaf.
        double or = planner.EstimateRows("ID = 1 OR AMOUNT = 50");
        double big = planner.EstimateRows("AMOUNT = 50");
        Assert.True(or >= big - 1e-6, $"OR estimate {or} must be >= largest leaf {big}");
    }

    [Fact]
    public void EstimateRows_NonOptimizableLeaf_UsesDefaultFraction()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var opts = new HighlikeOptions { DefaultLeafFraction = 0.33 };
        var planner = Planner(table, cdx, opts);

        // CAT is UNINDEXED → no stat → the planner falls back to the fixed default fraction (§7 "Residual").
        double est = planner.EstimateRows("CAT = 'C1'");
        Assert.Equal(table.RecordCount * opts.DefaultLeafFraction, est, 3);
    }

    // ============================================================ index-vs-scan threshold (UNIT)

    [Fact]
    public void PrefersFullScan_AboveThreshold_True_BelowThreshold_False()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var planner = Planner(table, cdx, new HighlikeOptions { IndexVsScanThreshold = 0.03 });

        // HOT = 1 matches ~50% (>> 3%) → prefer a full scan over driving that index.
        Assert.True(planner.PrefersFullScan("HOT = 1"));
        // ID = 1234 matches ~0.02% (<< 3%) → keep the index.
        Assert.False(planner.PrefersFullScan("ID = 1234"));
        // UPPER(NAME) = 'N007' matches 2% (< 3%) → keep the index.
        Assert.False(planner.PrefersFullScan("UPPER(NAME) = 'N007'"));
    }

    // ============================================================ ExplainPlan: AND-ordering

    [Fact]
    public void ExplainPlan_And_OrdersSelectiveLeafFirst()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        // UPPER(NAME) = 'N007' (~100 rows) is far more selective than HOT = 1 (~2500 rows).
        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });
        var plan = engine.ExplainPlan(table, cdx, "HOT = 1 AND UPPER(NAME) = 'N007'");

        Assert.Equal(2, plan.OrderedLeaves.Count);
        // Cheapest (smallest EstimateRows) first.
        Assert.True(plan.OrderedLeaves[0].EstimatedRows <= plan.OrderedLeaves[1].EstimatedRows,
            "selective leaf must be ordered first");
        Assert.Contains("NAME", plan.OrderedLeaves[0].Condition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HOT", plan.OrderedLeaves[1].Condition, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================ ExplainPlan: index-vs-scan

    [Fact]
    public void ExplainPlan_LowSelectivityLeaf_PrefersScan_SelectiveLeaf_UsesIndex()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });

        var hot = engine.ExplainPlan(table, cdx, "HOT = 1").OrderedLeaves.Single();
        Assert.False(hot.UsesIndex); // ~50% > threshold → scan

        var id = engine.ExplainPlan(table, cdx, "ID = 1234").OrderedLeaves.Single();
        Assert.True(id.UsesIndex);   // ~0.02% < threshold → index
    }

    // ============================================================ ExplainPlan: guard lifting

    [Fact]
    public void ExplainPlan_SelectiveNotKeyedTag_GuardLifted_AndDriven()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });

        // The Core default SKIPS this NOT-keyed tag (KeyExpressionHasNot); but its NDV (≈50) proves it
        // selective, so the cost planner LIFTS the guard and drives it.
        var plan = engine.ExplainPlan(table, cdx, "IIF(NOT EMPTY(NAME),NAME,'~') = 'N007'");
        var leaf = plan.OrderedLeaves.Single();

        Assert.True(leaf.GuardLifted, "a selective NOT-keyed tag must have its guard lifted");
        Assert.True(leaf.UsesIndex);
        Assert.Equal("TNOTSEL", leaf.TagName);
        Assert.Contains("TNOTSEL", plan.DrivingTags);
    }

    [Fact]
    public void ExplainPlan_NonSelectiveNotKeyedTag_StaysSkipped()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });

        // This NOT-keyed tag has NDV ≈ 1 (every row → 'A') → non-selective → the guard stays in place.
        var plan = engine.ExplainPlan(table, cdx, "IIF(NOT EMPTY(NAME),'A','B') = 'A'");
        var leaf = plan.OrderedLeaves.Single();

        Assert.False(leaf.GuardLifted);
        Assert.False(leaf.UsesIndex);
        Assert.DoesNotContain("TNOTNON", plan.DrivingTags);
    }

    // ============================================================ PLAN EFFECT via RecordsScanned

    /// <summary>
    /// Guard lifting is observable: with stats ON, Highlike DRIVES the selective NOT-keyed tag and the
    /// residual confirms only ~100 candidates — FAR fewer than the Core path, which skips the tag and
    /// full-scans all 5000 rows. The result set is identical regardless.
    /// </summary>
    [Fact]
    public void PlanEffect_GuardLifting_ScansFarFewerThanCore()
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        const string filter = "IIF(NOT EMPTY(NAME),NAME,'~') = 'N007'";
        var expected = FullScan(table, filter, EvaluationContext.Default);
        var core = QueryOptimizer.FindRecords(table, cdx, filter);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });
        var hi = engine.FindRecords(table, cdx, filter);

        // Same answer (HINT-ONLY invariant).
        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));

        // Core skipped the NOT tag → full residual scan over every row.
        Assert.False(core.Optimized);
        Assert.Equal(local.RowCount, core.RecordsScanned);

        // Highlike lifted the guard → an index drove the candidate set, scanning DRAMATICALLY fewer.
        Assert.True(hi.Optimized, "Highlike should drive the selective NOT-keyed tag");
        Assert.True(hi.RecordsScanned * 5 < core.RecordsScanned,
            $"expected far fewer scans, got hi={hi.RecordsScanned} core={core.RecordsScanned}");
    }

    /// <summary>
    /// Index-vs-scan is observable: a predicate the planner estimates to match more than the threshold
    /// (HOT = 1 ≈ 50%) is handled as a FULL SCAN by Highlike (not index-optimized), whereas Core would
    /// drive the low-selectivity index. The result set is identical.
    /// </summary>
    [Fact]
    public void PlanEffect_IndexVsScan_HighSelectivityPredicate_IsFullScanned()
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        const string filter = "HOT = 1";
        var expected = FullScan(table, filter, EvaluationContext.Default);
        var core = QueryOptimizer.FindRecords(table, cdx, filter);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });
        var hi = engine.FindRecords(table, cdx, filter);

        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));

        // Core drives the index for the low-selectivity equality; Highlike prefers the full scan.
        Assert.True(core.Optimized);
        Assert.False(hi.Optimized, "a >threshold predicate must be planned as a full scan");
    }

    /// <summary>
    /// AND-ordering keeps the residual small: an AND of a SELECTIVE indexed leaf (UPPER(NAME), ~100
    /// rows) and a NON-selective UNINDEXED residual leaf (CAT) scans only ~the selective cardinality —
    /// far fewer than the whole table. The result is identical to a full scan.
    /// </summary>
    [Fact]
    public void PlanEffect_AndOrdering_ScansApproximatelyTheSelectiveCardinality()
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        const string filter = "UPPER(NAME) = 'N007' AND CAT = 'C1'";
        var expected = FullScan(table, filter, EvaluationContext.Default);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });
        var hi = engine.FindRecords(table, cdx, filter);

        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));

        // ~100 rows match UPPER(NAME) = 'N007'; the residual (CAT) is confirmed only on those.
        Assert.True(hi.Optimized);
        Assert.True(hi.RecordsScanned <= 300,
            $"selective-first ordering should keep the scan ~the selective cardinality, got {hi.RecordsScanned}");
        Assert.True(hi.RecordsScanned * 5 < local.RowCount,
            $"expected far fewer than a full scan, got {hi.RecordsScanned} of {local.RowCount}");
    }

    // ============================================================ RESULT-INVARIANCE (the must)

    public static IEnumerable<object[]> InvarianceFilters() => new[]
    {
        new object[] { "ID = 1234" },                              // equality
        new object[] { "ID = 999999" },                            // equality, empty result
        new object[] { "AMOUNT >= 25 AND AMOUNT <= 74" },          // range
        new object[] { "BETWEEN(AMOUNT, 25, 74)" },                // BETWEEN range
        new object[] { "UPPER(NAME) = 'N007'" },                   // character
        new object[] { "UPPER(NAME) = 'N007' AND HOT = 1" },       // AND of selective + non-selective
        new object[] { "ID = 1 OR AMOUNT = 50" },                  // OR
        new object[] { "NOT (AMOUNT = 50)" },                      // NOT
        new object[] { "UPPER(NAME) = 'N007' AND CAT = 'C1'" },    // mixed indexed + residual
        new object[] { "IIF(NOT EMPTY(NAME),NAME,'~') = 'N007'" }, // NOT-keyed tag (guard-lift candidate)
        new object[] { "HOT = 1" },                                // low selectivity (scan candidate)
    };

    /// <summary>Stats ABSENT (EnableStatistics off): Highlike == Core == full scan for every filter shape.</summary>
    [Theory]
    [MemberData(nameof(InvarianceFilters))]
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

    /// <summary>Stats PRESENT (real .stx built lazily): Highlike == Core == full scan for every filter shape.</summary>
    [Theory]
    [MemberData(nameof(InvarianceFilters))]
    public void Invariance_StatsPresent(string filter)
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        // Build a real, fresh sidecar up front.
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

    /// <summary>
    /// Stats deliberately WRONG but FRESH (NDV collapsed, min/max pushed outside the real range so a
    /// naive planner would prune to 0, and the NOT tag falsely advertised as ultra-selective): the
    /// planner may pick a terrible plan, but Highlike == Core == full scan regardless.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvarianceFilters))]
    public void Invariance_StatsWrong(string filter)
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        WriteWrongButFreshStx(table, local);

        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true })
            .FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    /// <summary>
    /// Invariance across the EvaluationContext SHAPE knobs (SET EXACT, SET DELETED, SET OPTIMIZE) and
    /// the GENERAL collation — with WRONG-but-fresh stats present, so the planner is actively (mis)led.
    /// Highlike == Core == full scan in every combination.
    /// </summary>
    [Theory]
    [InlineData("UPPER(NAME) = 'N007'", true, true, true, "GENERAL")]
    [InlineData("UPPER(NAME) = 'N007'", false, true, true, "MACHINE")]
    [InlineData("AMOUNT = 50 AND HOT = 1", true, false, true, "MACHINE")]   // SET DELETED OFF
    [InlineData("ID = 1234", true, true, false, "MACHINE")]                 // SET OPTIMIZE OFF
    [InlineData("NOT (UPPER(NAME) = 'N007')", false, true, true, "GENERAL")]
    public void Invariance_AcrossContextKnobs_WithWrongStats(
        string filter, bool exact, bool deleted, bool optimize, string collation)
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        WriteWrongButFreshStx(table, local);

        var ctx = new EvaluationContext
        {
            Exact = exact,
            Deleted = deleted,
            Optimize = optimize,
            Collation = VfpCollations.ByName(collation),
        };

        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true })
            .FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    // ============================================================ STRONG staleness (dbfutc / cdxutc)

    /// <summary>
    /// The STRONG staleness signal (design §6): a real sidecar carries the DBF file last-write UTC
    /// (<c>dbfutc</c>). Altering ONLY that stored timestamp by a second — neither the record count nor
    /// the header date stamp change — must make <see cref="StxStatistics.IsFreshFor"/> report STALE.
    /// This is the same-day in-place key-edit / reindex case the date-only stamp cannot see. The engine
    /// then degrades to Core: the result set is unchanged.
    /// </summary>
    [Fact]
    public void Staleness_DbfWriteUtc_BumpedBySecond_IsStale_ResultStillCore()
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        // (1) Real, fresh sidecar containing dbfutc/cdxutc.
        var fresh = HighlikeStatistics.Analyze(table, cdx);
        Assert.False(string.IsNullOrEmpty(fresh.Source.DbfWriteUtc), "sidecar must capture dbfutc");
        Assert.True(StxStatistics.FromJson(File.ReadAllText(HighlikeStatistics.StxPath(table)))!.IsFreshFor(table),
            "the freshly built sidecar must be fresh");

        // (2) Read the JSON back, alter ONLY dbfutc by a second, rewrite.
        string stxPath = HighlikeStatistics.StxPath(table);
        var stale = WithBumpedDbfUtc(StxStatistics.FromJson(File.ReadAllText(stxPath))!);
        string staleJson = stale.ToJson();
        File.WriteAllText(stxPath, staleJson);

        // (3) The strong signal alone (reccount + updstamp unchanged) marks it stale.
        Assert.False(StxStatistics.FromJson(staleJson)!.IsFreshFor(table),
            "a dbfutc that no longer matches the file must mark the stats STALE");

        // (4) Engine degrades to Core — the result set is byte-for-byte the full-scan answer.
        AssertEngineEqualsCore(table, cdx, "UPPER(NAME) = 'N007'");
    }

    /// <summary>
    /// Mirror of <see cref="Staleness_DbfWriteUtc_BumpedBySecond_IsStale_ResultStillCore"/> for the CDX
    /// file last-write UTC (<c>cdxutc</c>): a reindex that rebuilt the CDX without rewriting the DBF.
    /// Altering ONLY <c>cdxutc</c> marks the stats stale; the engine still returns the Core result.
    /// </summary>
    [Fact]
    public void Staleness_CdxWriteUtc_BumpedBySecond_IsStale_ResultStillCore()
    {
        using var local = new IsolatedPlannerTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        var fresh = HighlikeStatistics.Analyze(table, cdx);
        Assert.False(string.IsNullOrEmpty(fresh.Source.CdxWriteUtc), "sidecar must capture cdxutc");

        string stxPath = HighlikeStatistics.StxPath(table);
        var stale = WithBumpedCdxUtc(StxStatistics.FromJson(File.ReadAllText(stxPath))!);
        string staleJson = stale.ToJson();
        File.WriteAllText(stxPath, staleJson);

        Assert.False(StxStatistics.FromJson(staleJson)!.IsFreshFor(table),
            "a cdxutc that no longer matches the file must mark the stats STALE");

        AssertEngineEqualsCore(table, cdx, "UPPER(NAME) = 'N007'");
    }

    /// <summary>HINT-only invariant: HighlikeEngine (stats ON) == Core == full scan for <paramref name="filter"/>.</summary>
    private static void AssertEngineEqualsCore(DbfTable table, CdxFile cdx, string filter)
    {
        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true })
            .FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    /// <summary>Return a copy of <paramref name="stats"/> with ONLY the stored dbfutc bumped by a second.</summary>
    private static StxStatistics WithBumpedDbfUtc(StxStatistics stats)
        => new()
        {
            Source = CopySource(stats.Source, dbfUtc: BumpUtc(stats.Source.DbfWriteUtc)),
            Built = stats.Built,
            Tags = stats.Tags,
        };

    /// <summary>Return a copy of <paramref name="stats"/> with ONLY the stored cdxutc bumped by a second.</summary>
    private static StxStatistics WithBumpedCdxUtc(StxStatistics stats)
        => new()
        {
            Source = CopySource(stats.Source, cdxUtc: BumpUtc(stats.Source.CdxWriteUtc)),
            Built = stats.Built,
            Tags = stats.Tags,
        };

    private static StxSource CopySource(StxSource s, string? dbfUtc = null, string? cdxUtc = null)
        => new()
        {
            Dbf = s.Dbf,
            RecCount = s.RecCount,
            UpdStamp = s.UpdStamp,
            DbfWriteUtc = dbfUtc ?? s.DbfWriteUtc,
            CdxWriteUtc = cdxUtc ?? s.CdxWriteUtc,
            Deleted = s.Deleted,
        };

    /// <summary>Parse a round-trip UTC stamp and shift it forward by exactly one second.</summary>
    private static string BumpUtc(string? utc)
    {
        var dt = DateTime.Parse(utc!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        return dt.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture);
    }

    // Write a corrupt-but-FRESH sidecar (its src token matches the table, so a planner would TRUST it).
    // The token populates ALL THREE staleness layers — reccount + updstamp + the STRONG file last-write
    // timestamps (dbfutc / cdxutc) — so the wrong-stats invariance tests exercise every layer at once
    // rather than silently skipping the strong one (which a missing dbfutc/cdxutc would short-circuit as fresh).
    private static void WriteWrongButFreshStx(DbfTable table, IsolatedPlannerTable local)
    {
        string stxPath = HighlikeStatistics.StxPath(table);
        string dbfName = Path.GetFileName(local.Dbf);
        // Read the header stamp so the token matches exactly.
        string stamp;
        using (var fs = File.OpenRead(local.Dbf))
        {
            var h = new byte[4];
            fs.ReadExactly(h, 0, 4);
            stamp = $"{1900 + h[1]:D4}-{h[2]:D2}-{h[3]:D2}";
        }

        // Match the STRONG signals exactly: the same File.GetLastWriteTimeUtc(...).ToString("O") the
        // engine reads, so IsFreshFor sees a fresh token across all layers.
        string dbfUtc = File.GetLastWriteTimeUtc(local.Dbf).ToString("O", CultureInfo.InvariantCulture);
        string cdxUtc = File.GetLastWriteTimeUtc(local.Cdx).ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllText(stxPath, $$"""
        {
          "src": { "dbf": "{{dbfName}}", "reccount": {{table.RecordCount}}, "updstamp": "{{stamp}}", "dbfutc": "{{dbfUtc}}", "cdxutc": "{{cdxUtc}}", "deleted": 0 },
          "built": "2000-01-01T00:00:00Z",
          "tags": {
            "TID":     { "key": "ID",     "ndv": 1, "nulls": 0, "min": "999999", "max": "999999" },
            "TAMT":    { "key": "AMOUNT", "ndv": 1, "nulls": 0, "min": "999999", "max": "999999" },
            "THOT":    { "key": "HOT",    "ndv": 9999, "nulls": 0, "min": "0", "max": "1" },
            "TUNAME":  { "key": "UPPER(NAME)", "coll": "MACHINE", "ndv": 1, "nulls": 0 },
            "TNOTSEL": { "key": "IIF(NOT EMPTY(NAME),NAME,'~')", "coll": "MACHINE", "ndv": 999999, "nulls": 0 }
          }
        }
        """);
    }

    // ============================================================ shared + local temp tables

    /// <summary>
    /// The shared read-only fixture: 5000 rows with a spread of selectivities and tags —
    /// <list type="bullet">
    ///   <item>ID (Integer, UNIQUE) — high selectivity.</item>
    ///   <item>AMOUNT (Numeric, 100 distinct) — mid selectivity, for range estimates.</item>
    ///   <item>HOT (Integer, 2 distinct ~50/50) — LOW selectivity, the index-vs-scan candidate.</item>
    ///   <item>UPPER(NAME) (50 distinct) — MACHINE and GENERAL tags.</item>
    ///   <item>TNOTSEL = IIF(NOT EMPTY(NAME),NAME,'~') — a NOT-keyed but SELECTIVE (NDV≈50) char tag the
    ///   Core default skips (KeyExpressionHasNot) → the guard-LIFT candidate.</item>
    ///   <item>TNOTNON = IIF(NOT EMPTY(NAME),'A','B') — a NOT-keyed NON-selective (NDV≈1) tag that stays skipped.</item>
    ///   <item>CAT (Character) — deliberately UNINDEXED (the residual leaf).</item>
    /// </list>
    /// TEMP only — deleted on dispose.
    /// </summary>
    public sealed class PlannerTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 5000;

        public PlannerTable() => (Dir, Dbf, Cdx) = Build("foxdbf_planner_");
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }

        internal static (string dir, string dbf, string cdx) Build(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dbf = Path.Combine(dir, "p.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("HOT", 'I', 4),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("CAT", 'C', 10),
            };

            using (var w = DbfWriter.Create(dbf, cols))
            {
                for (int i = 1; i <= 5000; i++)
                {
                    int amount = i % 100;            // 0..99 → NDV 100
                    int hot = i % 2;                 // 0/1 → NDV 2, ~50% each
                    string name = "N" + (i % 50).ToString("D3"); // 50 distinct ("N000".."N049")
                    string cat = "C" + (i % 8);      // 8 distinct, UNINDEXED
                    w.AppendRecord(new object?[] { i, amount, hot, name, cat });
                }

                w.CreateTag(new CdxTagDefinition("TID", "ID"));
                w.CreateTag(new CdxTagDefinition("TAMT", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("THOT", "HOT"));
                w.CreateTag(new CdxTagDefinition("TUNAME", "UPPER(NAME)", collation: "MACHINE"));
                w.CreateTag(new CdxTagDefinition("TUNAMEG", "UPPER(NAME)", collation: "GENERAL"));
                w.CreateTag(new CdxTagDefinition("TNOTSEL", "IIF(NOT EMPTY(NAME),NAME,'~')"));
                w.CreateTag(new CdxTagDefinition("TNOTNON", "IIF(NOT EMPTY(NAME),'A','B')"));
            }

            return (dir, dbf, Path.ChangeExtension(dbf, ".cdx"));
        }
    }

    /// <summary>
    /// A private per-test clone of <see cref="PlannerTable"/> for tests that PERSIST a sidecar (correct
    /// or deliberately wrong) — keeping the shared read-only fixture's directory pristine. TEMP only.
    /// </summary>
    private sealed class IsolatedPlannerTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 5000;

        public IsolatedPlannerTable() => (Dir, Dbf, Cdx) = PlannerTable.Build("foxdbf_planner_iso_");
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { } }
    }
}
