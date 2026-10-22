using System;
using System.Collections;
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
/// Phase C of the Highlike accelerator (design §6/§9): EQUI-DEPTH HISTOGRAMS + MOST-COMMON-VALUES
/// (MCV), both harvested from the SAME single sorted leaf walk that already produces NDV / Min / Max
/// (no second scan), persisted as ADDITIVE <c>.stx</c> JSON fields (older readers ignore them).
///
/// The four pillars, TDD:
/// <list type="number">
///   <item><b>BUILD CORRECTNESS:</b> on a KNOWN skewed distribution (one HOT value + a long unique
///   tail) the built histogram boundaries and the MCV (value,count) list match a brute-force
///   computation, AND they come from ONE walk (the entry stream is enumerated exactly once).</item>
///   <item><b>ESTIMATE ACCURACY (the value):</b> a range estimate via the histogram is TIGHTER than the
///   old linear interpolation, and a HOT (MCV) equality estimate beats the uniform reccount/ndv.</item>
///   <item><b>PLAN EFFECT:</b> a hot-value equality whose true count exceeds the index-vs-scan threshold
///   is now planned as a SCAN (was wrongly indexed under the uniform estimate); a selective range stays
///   indexed.</item>
///   <item><b>RESULT INVARIANCE (the must):</b> Highlike == Core == full scan with histograms/MCV
///   present, absent, AND deliberately WRONG; and the <c>.stx</c> round-trips the new fields while an old
///   <c>.stx</c> lacking them still loads (additive).</item>
/// </list>
///
/// SAFETY: every table + sidecar lives under a freshly created TEMP directory, deleted on dispose; no
/// committed fixture under data/ is ever touched.
/// </summary>
public sealed class HighlikeHistogramMcvTests
{
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

    /// <summary>Decode a tag's entries into the sorted (ascending) list of integer key values, one per record.</summary>
    private static long[] SortedKeyValues(CdxTag tag)
    {
        var vals = new List<long>();
        foreach (var e in tag.EnumerateEntries())
            vals.Add(Convert.ToInt64(tag.DecodeKey(e.Key).Value, CultureInfo.InvariantCulture));
        vals.Sort();
        return vals.ToArray();
    }

    /// <summary>
    /// The equi-depth histogram CONTRACT (design §9): for <paramref name="buckets"/> buckets there are
    /// <c>buckets + 1</c> boundaries, where <c>boundary[j]</c> is the key at sorted position
    /// <c>clamp(j · count / buckets, 0, count − 1)</c>. Each bucket then covers ≈ <c>count / buckets</c>
    /// entries. This is the SAME formula the builder must use — a brute-force cross-check.
    /// </summary>
    private static double[] BruteForceHistogram(long[] sorted, int buckets)
    {
        int count = sorted.Length;
        var b = new double[buckets + 1];
        for (int j = 0; j <= buckets; j++)
        {
            long pos = (long)j * count / buckets;
            if (pos < 0) pos = 0;
            if (pos > count - 1) pos = count - 1;
            b[j] = sorted[pos];
        }
        return b;
    }

    /// <summary>Brute-force top-K most-common values (count desc, value asc tiebreak) — a run of equal keys is a frequency.</summary>
    private static List<(long val, long cnt)> BruteForceMcv(long[] sorted, int k)
        => sorted.GroupBy(x => x)
                 .Select(g => (g.Key, (long)g.Count()))
                 .OrderByDescending(t => t.Item2).ThenBy(t => t.Item1)
                 .Take(k)
                 .ToList();

    private static HighlikeCostPlanner PlannerWith(DbfTable table, CdxFile cdx, HighlikeOptions opt)
        => new(HighlikeStatistics.Build(table, cdx, opt), table.RecordCount, opt, cdx);

    // ============================================================ (1) BUILD CORRECTNESS

    [Fact]
    public void Histogram_Boundaries_MatchBruteForce_OnSkewedData()
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        const int buckets = 10;
        var stats = HighlikeStatistics.Build(table, cdx, new HighlikeOptions { HistogramBuckets = buckets, McvCount = 3 });
        var v = stats.ForTag(cdx.Tag("VTAG")!)!;

        Assert.NotNull(v.Histogram);
        Assert.Equal(buckets + 1, v.Histogram!.Count);

        // First / last boundary are exactly Min / Max — proof they share the one walk's endpoints.
        Assert.Equal(double.Parse(v.Min!, CultureInfo.InvariantCulture),
                     double.Parse(v.Histogram[0], CultureInfo.InvariantCulture), 6);
        Assert.Equal(double.Parse(v.Max!, CultureInfo.InvariantCulture),
                     double.Parse(v.Histogram[^1], CultureInfo.InvariantCulture), 6);

        var expected = BruteForceHistogram(SortedKeyValues(cdx.Tag("VTAG")!), buckets);
        var actual = v.Histogram.Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(expected, actual);

        // Boundaries are non-decreasing (sorted equi-depth).
        for (int i = 1; i < actual.Length; i++)
            Assert.True(actual[i] >= actual[i - 1], $"boundary[{i}]={actual[i]} < boundary[{i - 1}]={actual[i - 1]}");
    }

    [Fact]
    public void Mcv_TopK_MatchesBruteForce_ValuesAndCounts()
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx, new HighlikeOptions { HistogramBuckets = 10, McvCount = 3 });
        var v = stats.ForTag(cdx.Tag("VTAG")!)!;

        Assert.NotNull(v.Mcv);
        var expected = BruteForceMcv(SortedKeyValues(cdx.Tag("VTAG")!), 3);

        // The fixture's three heavy hitters have DISTINCT counts, so the top-3 order is unambiguous.
        Assert.Equal(new (long, long)[] { (1000, 600), (1001, 300), (1002, 100) }, expected.ToArray());

        Assert.Equal(expected.Count, v.Mcv!.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].val, long.Parse(v.Mcv[i].Value!, CultureInfo.InvariantCulture));
            Assert.Equal(expected[i].cnt, v.Mcv[i].Count);
        }

        // MCV counts never exceed the record count and are descending.
        for (int i = 1; i < v.Mcv.Count; i++)
            Assert.True(v.Mcv[i].Count <= v.Mcv[i - 1].Count, "MCV must be ordered by descending count");
    }

    /// <summary>
    /// STRUCTURAL "no second scan": the harvester is handed the sorted entry stream and must enumerate it
    /// EXACTLY ONCE (timing-independent) while still producing BOTH the histogram and the MCV from that
    /// single pass. A counting wrapper proves the single walk; the harvested structures prove it was a
    /// productive one.
    /// </summary>
    [Fact]
    public void BuildTagStats_EnumeratesTheWalkExactlyOnce_AndYieldsHistogramAndMcv()
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);
        var tag = cdx.Tag("VTAG")!; // ascending integer tag

        var counting = new CountingEnumerable<IndexEntry>(tag.EnumerateEntries());
        const int buckets = 10;
        var stats = HighlikeStatistics.BuildTagStats(tag, counting, nulls: 0, histogramBuckets: buckets, mcvCount: 3);

        // Single sorted walk — the entry source's GetEnumerator was invoked once, no second scan.
        Assert.Equal(1, counting.EnumerationCount);

        // The same walk produced NDV / Min / Max AND the histogram AND the MCV.
        Assert.True(stats.Ndv > 0);
        Assert.NotNull(stats.Histogram);
        Assert.Equal(buckets + 1, stats.Histogram!.Count);
        Assert.NotNull(stats.Mcv);
        Assert.Equal((long)600, stats.Mcv![0].Count); // the hot value's true count

        // Cross-check the histogram against the independent brute force of the same tag.
        var expected = BruteForceHistogram(SortedKeyValues(tag), buckets);
        var actual = stats.Histogram.Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(expected, actual);
    }

    // ============================================================ (2) ESTIMATE ACCURACY (the value)

    /// <summary>
    /// The histogram's whole point: on the SKEWED column a range estimate is CLOSER to the true count
    /// than the old linear min/max interpolation (which assumes a uniform spread). New error &lt; old
    /// error, and the new estimate is within a tight factor of the truth.
    /// </summary>
    [Fact]
    public void EstimateRows_Range_Histogram_IsTighterThanLinear()
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        const string filter = "V <= 250"; // lands in the dense smooth region; linear is dragged by the hot tail
        double trueCount = FullScan(table, filter, EvaluationContext.Default).Count;
        Assert.True(trueCount > 0);

        // OLD: histogram OFF (buckets = 0) → the planner falls back to linear interpolation.
        var linear = PlannerWith(table, cdx, new HighlikeOptions { HistogramBuckets = 0, McvCount = 0 });
        double estOld = linear.EstimateRows(filter);

        // NEW: histogram ON.
        var hist = PlannerWith(table, cdx, new HighlikeOptions { HistogramBuckets = 24, McvCount = 0 });
        double estNew = hist.EstimateRows(filter);

        double errOld = Math.Abs(estOld - trueCount);
        double errNew = Math.Abs(estNew - trueCount);
        Assert.True(errNew < errOld,
            $"histogram estimate must beat linear: errNew={errNew} (est {estNew}) errOld={errOld} (est {estOld}) true={trueCount}");

        // And tight in absolute terms.
        Assert.True(estNew >= trueCount / 1.5 && estNew <= trueCount * 1.5,
            $"histogram estimate {estNew} not within 1.5x of true {trueCount}");
    }

    /// <summary>
    /// MCV's whole point: the HOT equality value is estimated near its true (high) count, and a RARE
    /// value near its true (low) count — both improvements over the uniform reccount/ndv.
    /// </summary>
    [Fact]
    public void EstimateRows_Equality_Mcv_BeatsUniform_ForHotAndRareValues()
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        var uniform = PlannerWith(table, cdx, new HighlikeOptions { HistogramBuckets = 0, McvCount = 0 });
        var mcv = PlannerWith(table, cdx, new HighlikeOptions { HistogramBuckets = 0, McvCount = 16 });

        // HOT value: V = 1000 occurs 600 times.
        double hotTrue = FullScan(table, "V = 1000", EvaluationContext.Default).Count;
        Assert.Equal(600, hotTrue);
        double hotUniform = uniform.EstimateRows("V = 1000");
        double hotMcv = mcv.EstimateRows("V = 1000");
        Assert.True(Math.Abs(hotMcv - hotTrue) < Math.Abs(hotUniform - hotTrue),
            $"MCV hot estimate {hotMcv} must beat uniform {hotUniform} (true {hotTrue})");
        Assert.True(hotMcv >= hotTrue / 1.5 && hotMcv <= hotTrue * 1.5,
            $"MCV hot estimate {hotMcv} not within 1.5x of true {hotTrue}");

        // RARE value: V = 250 occurs once.
        double rareTrue = FullScan(table, "V = 250", EvaluationContext.Default).Count;
        Assert.Equal(1, rareTrue);
        double rareUniform = uniform.EstimateRows("V = 250");
        double rareMcv = mcv.EstimateRows("V = 250");
        Assert.True(Math.Abs(rareMcv - rareTrue) <= Math.Abs(rareUniform - rareTrue),
            $"MCV rare estimate {rareMcv} must not be worse than uniform {rareUniform} (true {rareTrue})");
        Assert.True(rareMcv <= 5, $"a non-MCV rare value should estimate small, got {rareMcv}");
    }

    // ============================================================ (3) PLAN EFFECT

    /// <summary>
    /// The plan flips: under the uniform estimate the hot equality (V = 1000, ~30% of the table) looked
    /// ultra-selective (reccount/ndv ≈ 2) and was wrongly INDEXED. With the MCV the planner sees the real
    /// ~30% &gt; the index-vs-scan threshold and prefers a FULL SCAN. A genuinely selective range stays
    /// indexed. PLAN-only — the result set is unchanged (asserted in the invariance tests).
    /// </summary>
    [Fact]
    public void PlanEffect_HotEqualityBecomesScan_SelectiveRangeStaysIndexed()
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true, IndexVsScanThreshold = 0.03 });

        // Hot equality: ~30% > 3% → planned as a scan (NOT index-optimized).
        var hot = engine.ExplainPlan(table, cdx, "V = 1000").OrderedLeaves.Single();
        Assert.False(hot.UsesIndex, "the hot MCV value (~30%) must be planned as a full scan, not indexed");

        // Selective range: V <= 10 matches ~11 rows (≈0.2%) → stays indexed.
        var sel = engine.ExplainPlan(table, cdx, "V <= 10").OrderedLeaves.Single();
        Assert.True(sel.UsesIndex, "a selective range must stay indexed");
    }

    // ============================================================ (4) RESULT INVARIANCE (the must)

    public static IEnumerable<object[]> InvarianceFilters() => new[]
    {
        new object[] { "V = 1000" },                          // hot equality
        new object[] { "V = 999999" },                        // equality, empty result
        new object[] { "V <= 250" },                          // range (histogram-affected)
        new object[] { "V >= 1000" },                         // range over the hot region
        new object[] { "BETWEEN(V, 100, 1002)" },             // BETWEEN
        new object[] { "UPPER(NAME) = 'K3'" },                // character key
        new object[] { "V = 1000 AND UPPER(NAME) = 'K3'" },   // AND of hot + character
        new object[] { "V <= 250 OR V = 1001" },              // OR
        new object[] { "NOT (V = 1000)" },                    // NOT of the hot value
    };

    /// <summary>Histograms/MCV ABSENT (a fresh sidecar built WITHOUT the new fields): Highlike == Core == full scan.</summary>
    [Theory]
    [MemberData(nameof(InvarianceFilters))]
    public void Invariance_HistogramMcvAbsent(string filter)
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        // Persist a FRESH sidecar that lacks histogram/MCV (additive-absent) — the engine reuses it.
        HighlikeStatistics.Analyze(table, cdx, new HighlikeOptions { HistogramBuckets = 0, McvCount = 0 });

        AssertHighlikeEqualsCoreEqualsFullScan(table, cdx, filter, statsOn: true);
    }

    /// <summary>Histograms/MCV PRESENT (a real, fresh sidecar with both): Highlike == Core == full scan.</summary>
    [Theory]
    [MemberData(nameof(InvarianceFilters))]
    public void Invariance_HistogramMcvPresent(string filter)
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        HighlikeStatistics.Analyze(table, cdx, new HighlikeOptions { HistogramBuckets = 16, McvCount = 16 });

        AssertHighlikeEqualsCoreEqualsFullScan(table, cdx, filter, statsOn: true);
    }

    /// <summary>
    /// Histograms/MCV deliberately WRONG but FRESH (boundaries garbage, the hot value pruned away, a rare
    /// value falsely advertised as the most common): the planner may pick a terrible plan, but
    /// Highlike == Core == full scan regardless — the residual still confirms every candidate.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvarianceFilters))]
    public void Invariance_HistogramMcvWrong(string filter)
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        WriteWrongButFreshStx(table, t);

        AssertHighlikeEqualsCoreEqualsFullScan(table, cdx, filter, statsOn: true);
    }

    private static void AssertHighlikeEqualsCoreEqualsFullScan(DbfTable table, CdxFile cdx, string filter, bool statsOn)
    {
        var ctx = EvaluationContext.Default;
        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = statsOn });
        var hi = engine.FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    // ============================================================ (5) round-trip + additive load

    [Fact]
    public void Json_RoundTrips_HistogramAndMcv()
    {
        using var t = new SkewTable();
        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        var built = HighlikeStatistics.Build(table, cdx, new HighlikeOptions { HistogramBuckets = 10, McvCount = 3 });
        var v0 = built.ForTag(cdx.Tag("VTAG")!)!;
        Assert.NotNull(v0.Histogram); // the feature must actually have been built
        Assert.NotNull(v0.Mcv);

        string json = built.ToJson();
        Assert.Contains("\"hist\"", json);
        Assert.Contains("\"mcv\"", json);

        var back = StxStatistics.FromJson(json);
        Assert.NotNull(back);
        var v1 = back!.ForTag(cdx.Tag("VTAG")!)!;

        Assert.Equal(v0.Histogram, v1.Histogram);
        Assert.NotNull(v1.Mcv);
        Assert.Equal(v0.Mcv!.Count, v1.Mcv!.Count);
        for (int i = 0; i < v0.Mcv.Count; i++)
        {
            Assert.Equal(v0.Mcv[i].Value, v1.Mcv[i].Value);
            Assert.Equal(v0.Mcv[i].Count, v1.Mcv[i].Count);
        }
    }

    /// <summary>
    /// ADDITIVE back-compat: an OLD <c>.stx</c> that predates histogram/MCV (no <c>hist</c> / <c>mcv</c>
    /// fields) still loads cleanly — NDV / Min / Max intact, the new fields simply null. Never throws.
    /// </summary>
    [Fact]
    public void FromJson_OldSidecarWithoutHistogramOrMcv_StillLoads_NewFieldsNull()
    {
        const string oldJson = """
        {
          "src": { "dbf": "x.dbf", "reccount": 2000, "deleted": 0 },
          "built": "2026-06-27T01:10:00Z",
          "tags": {
            "VTAG": { "key": "V", "ndv": 1003, "nulls": 0, "min": "0", "max": "1002" }
          }
        }
        """;

        var stats = StxStatistics.FromJson(oldJson);
        Assert.NotNull(stats);
        var v = stats!.ForKey("V")!;
        Assert.Equal(1003, v.Ndv);
        Assert.Equal("0", v.Min);
        Assert.Equal("1002", v.Max);
        Assert.Null(v.Histogram); // absent fields → null (older format)
        Assert.Null(v.Mcv);
    }

    // ============================================================ helpers

    /// <summary>An <see cref="IEnumerable{T}"/> wrapper that counts how many times it is enumerated (single-walk proof).</summary>
    private sealed class CountingEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _inner;
        public int EnumerationCount { get; private set; }
        public CountingEnumerable(IEnumerable<T> inner) => _inner = inner;
        public IEnumerator<T> GetEnumerator() { EnumerationCount++; return _inner.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Write a corrupt-but-FRESH sidecar (its src token matches the table, so the planner TRUSTS it):
    /// the hot value pruned out of [min,max], the histogram boundaries garbage, and a rare value (250)
    /// falsely advertised as the single most common with an absurd count. None of this may change the
    /// result set — only the plan.
    /// </summary>
    private static void WriteWrongButFreshStx(DbfTable table, SkewTable t)
    {
        string stxPath = HighlikeStatistics.StxPath(table);
        string dbfName = Path.GetFileName(t.Dbf);
        string stamp;
        using (var fs = File.OpenRead(t.Dbf))
        {
            var h = new byte[4];
            fs.ReadExactly(h, 0, 4);
            stamp = $"{1900 + h[1]:D4}-{h[2]:D2}-{h[3]:D2}";
        }
        string dbfUtc = File.GetLastWriteTimeUtc(t.Dbf).ToString("O", CultureInfo.InvariantCulture);
        string cdxUtc = File.GetLastWriteTimeUtc(t.Cdx).ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllText(stxPath, $$"""
        {
          "src": { "dbf": "{{dbfName}}", "reccount": {{table.RecordCount}}, "updstamp": "{{stamp}}", "dbfutc": "{{dbfUtc}}", "cdxutc": "{{cdxUtc}}", "deleted": 0 },
          "built": "2000-01-01T00:00:00Z",
          "tags": {
            "VTAG": {
              "key": "V", "ndv": 1, "nulls": 0, "min": "999999", "max": "999999",
              "hist": ["999999","5","999999","3","999999"],
              "mcv": [ { "v": "250", "c": 999999 }, { "v": "1000", "c": 0 } ]
            },
            "NTAG": {
              "key": "UPPER(NAME)", "coll": "MACHINE", "ndv": 1, "nulls": 0,
              "mcv": [ { "v": "K3", "c": 999999 } ]
            }
          }
        }
        """);
    }

    /// <summary>
    /// A KNOWN skewed temp table (2000 rows) for histogram + MCV:
    /// <list type="bullet">
    ///   <item>a DENSE smooth region: V = 0..999, one row each (1000 rows) — where the histogram earns
    ///   its keep over linear interpolation;</item>
    ///   <item>three HEAVY HITTERS at the top with DISTINCT counts: V = 1000 (×600), 1001 (×300),
    ///   1002 (×100) — the MCV list (and the index-vs-scan flip on V = 1000).</item>
    /// </list>
    /// Rows are appended in a deterministically SHUFFLED order so a correct build must rely on the SORTED
    /// index walk, not insertion order. A NAME column (5 distinct) carries MACHINE + GENERAL char tags.
    /// TEMP only — deleted on dispose.
    /// </summary>
    private sealed class SkewTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 2000;

        public SkewTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_hist_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "h.dbf");

            var values = new List<int>(2000);
            for (int v = 0; v <= 999; v++) values.Add(v); // smooth region
            for (int i = 0; i < 600; i++) values.Add(1000); // heavy hitter A
            for (int i = 0; i < 300; i++) values.Add(1001); // heavy hitter B
            for (int i = 0; i < 100; i++) values.Add(1002); // heavy hitter C

            // Deterministic shuffle (fixed seed) so insertion order != sorted order.
            var rng = new Random(12345);
            for (int i = values.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }

            var cols = new[]
            {
                new DbfColumnDef("V", 'I', 4),
                new DbfColumnDef("NAME", 'C', 10),
            };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 0; i < values.Count; i++)
                    w.AppendRecord(new object?[] { values[i], "K" + (i % 5) });

                w.CreateTag(new CdxTagDefinition("VTAG", "V"));
                w.CreateTag(new CdxTagDefinition("NTAG", "UPPER(NAME)", collation: "MACHINE"));
                w.CreateTag(new CdxTagDefinition("NGEN", "UPPER(NAME)", collation: "GENERAL"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
