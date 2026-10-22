using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Highlike;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// PERF-FIX of the Highlike accelerator found by the 1,000,000-row benchmark: the engine must MEMOISE the
/// parsed / built <c>.stx</c> statistics across queries instead of re-reading + re-parsing (Load) or
/// re-walking the whole index (Build) on EVERY <see cref="HighlikeEngine.FindRecords"/> / <c>Explain</c>.
/// Today each query rebuilds the cost policy via <c>TryBuildPolicy → HighlikeStatistics.GetOrBuild</c>, a
/// constant per-query floor (≈12ms on a 1M-row, 6-tag table) that is independent of how few rows match —
/// it makes Highlike slower than VFP9 on selective queries.
///
/// THE PROOF IS DETERMINISTIC, NOT TIMING: the engine exposes
/// <see cref="HighlikeEngine.StatsCacheStatistics"/> whose <see cref="HighlikeStatsCacheStatistics.Computes"/>
/// counts every expensive Load/Build. Across MANY queries on an UNCHANGED table it must increment ONCE
/// (then reuse); after a write moves the change-token it must increment exactly once more.
///
/// INVARIANT (the hard gate): statistics are HINTS ONLY. With the memo cold, warm, after a change, and
/// with EnableStatistics on/off, the Highlike result == the Core result == a full scan, ALWAYS.
///
/// SAFETY: TEMP files only; every fixture is deleted on dispose. No committed fixture is touched.
///
/// STATE: these tests are written FIRST against a compile-only STUB stats memo (which does NOT yet
/// memoise) — the perf tests (1)/(2) are EXPECTED TO FAIL until the in-engine stats cache is implemented.
/// </summary>
public sealed class HighlikeStatsCacheTests
{
    // ============================================================ temp fixture (TEMP files ONLY)

    /// <summary>
    /// A throwaway temp DBF + structural CDX with three single-field tags (IDTAG / AMTTAG / UNAME) plus an
    /// UNINDEXED CATEGORY column — enough for the cost planner to consult statistics. Deleted on dispose.
    /// </summary>
    private sealed class TempTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount { get; }

        private static readonly string[] Names = { "Alice", "Bob", "cherry", "David", "alice", "eric" };

        public TempTable(int rows = 2000)
        {
            RowCount = rows;
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_histats_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "s.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("CATEGORY", 'C', 10),
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                    w.AppendRecord(new object?[] { i, i % 1000, Names[i % Names.Length], "C" + (i % 4) });

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ============================================================ ground-truth helpers

    private sealed class Row : IRowContext
    {
        private readonly DbfRecord _rec;
        public Row(DbfRecord rec, int recNo, int recCount) { _rec = rec; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name) => _rec.GetExpressionValue(name);
    }

    private static int[] Sorted(IEnumerable<int> xs) => xs.OrderBy(x => x).ToArray();

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
        hits.Sort();
        return hits.ToArray();
    }

    private static int[] BruteForce(string dbf, string filter)
    {
        using var t = DbfTable.Open(dbf);
        return FullScan(t, filter, EvaluationContext.Default);
    }

    /// <summary>The cold Core baseline (no accelerator) for a freshly opened table + cdx.</summary>
    private static int[] Cold(string dbf, string cdx, string filter)
    {
        using var t = DbfTable.Open(dbf);
        using var c = CdxFile.Open(cdx, t);
        return Sorted(QueryOptimizer.FindRecords(t, c, filter).RecordNumbers);
    }

    /// <summary>One query through the engine on FRESH handles (same path) — proves the memo is keyed by path, not handle.</summary>
    private static int[] Query(HighlikeEngine engine, TempTable fx, string filter)
    {
        using var t = DbfTable.Open(fx.Dbf);
        using var c = CdxFile.Open(fx.Cdx, t);
        return Sorted(engine.FindRecords(t, c, filter).RecordNumbers);
    }

    private static HighlikeEngine StatsEngine(HighlikeDriveKind drive = HighlikeDriveKind.Network)
        => new(new HighlikeOptions { EnableStatistics = true, DriveKind = drive });

    // ============================================================ (1) STATS REUSED — the perf proof (deterministic)

    /// <summary>
    /// THE perf proof. With statistics enabled, running the SAME query MANY times on an UNCHANGED table
    /// must Load/Build the <c>.stx</c> EXACTLY ONCE — then reuse the memoised stats. Today it is once PER
    /// query (the constant ≈12ms floor the 1M-row benchmark caught), so this FAILS against the stub.
    /// </summary>
    [Fact]
    public void ManyQueries_SameTable_BuildStatsOnce()
    {
        using var fx = new TempTable();
        using var engine = StatsEngine();

        const string filter = "ID = 1500";
        const int queries = 25;

        for (int i = 0; i < queries; i++)
            Query(engine, fx, filter);

        var stats = engine.StatsCacheStatistics;

        // The expensive Load/Build ran ONCE across all 25 queries (then the memo served the rest).
        Assert.Equal(1, stats.Computes);
        // Every query after the first reused the memoised StxStatistics — no re-parse, no re-walk.
        Assert.True(stats.Reuses >= queries - 1,
            $"expected at least {queries - 1} reuses; got {stats.Reuses} (stats re-acquired per query)");
    }

    [Fact]
    public void StreamOpenedIndexes_AreNeverStoredInTheStatsCache()
    {
        using var fx = new TempTable(rows: 50);
        using var engine = StatsEngine();
        byte[] cdxBytes = File.ReadAllBytes(fx.Cdx);

        for (int i = 0; i < 2; i++)
        {
            using var table = DbfTable.Open(fx.Dbf);
            using var stream = new MemoryStream(cdxBytes, writable: false);
            using var cdx = CdxFile.Open(stream, table);
            _ = engine.FindRecords(table, cdx, "ID = 10");
        }

        var stats = engine.StatsCacheStatistics;
        Assert.Equal(2, stats.Computes);
        Assert.Equal(0, stats.Reuses);
        Assert.Equal(0, stats.EntryCount);
    }

    /// <summary>
    /// The memo is shared across DIFFERENT filters on the same table (the statistics describe the TABLE,
    /// not the predicate): a mix of queries still Loads/Builds the stats once.
    /// </summary>
    [Fact]
    public void DifferentFilters_SameTable_StillBuildStatsOnce()
    {
        using var fx = new TempTable();
        using var engine = StatsEngine();

        string[] filters =
        {
            "ID = 1500",
            "AMOUNT >= 100 AND AMOUNT <= 200",
            "UPPER(NAME) = 'ALICE'",
            "AMOUNT > 990",
            "CATEGORY = 'C1'",
        };

        for (int i = 0; i < 20; i++)
            Query(engine, fx, filters[i % filters.Length]);

        Assert.Equal(1, engine.StatsCacheStatistics.Computes);
    }

    // ============================================================ (2) REFRESH ON CHANGE

    /// <summary>
    /// After the table changes (our own writer append + reindex, so the change-token moves), the NEXT
    /// query must REBUILD the statistics ONCE — and then reuse them again. The counter increments by
    /// exactly one across many post-change queries (today it increments per query → FAILS against stub).
    /// </summary>
    [Fact]
    public void AfterWrite_RebuildsStatsOnce_ThenReuses()
    {
        using var fx = new TempTable();
        using var engine = StatsEngine();

        const string filter = "AMOUNT >= 100 AND AMOUNT <= 200";

        // Warm the memo: several queries → stats Loaded/Built ONCE.
        for (int i = 0; i < 5; i++) Query(engine, fx, filter);
        long computesBefore = engine.StatsCacheStatistics.Computes;
        Assert.Equal(1, computesBefore);

        // Mutate the table (token moves; the .stx also goes stale).
        AppendAndReindex(fx, newId: 150, amount: 150, name: "NEWROW", category: "C0");

        // Several queries AFTER the change: exactly ONE fresh Load/Build, then reuse.
        for (int i = 0; i < 5; i++) Query(engine, fx, filter);

        long delta = engine.StatsCacheStatistics.Computes - computesBefore;
        Assert.Equal(1, delta);
    }

    /// <summary>
    /// REFRESH must pick up the FRESH distribution: a freshly Analyze-d stat after the write reflects the
    /// new data (the appended matching row appears) — and the result still equals the Core + full scan.
    /// </summary>
    [Fact]
    public void AfterWrite_FreshDistribution_ResultReflectsChange()
    {
        using var fx = new TempTable();
        using var engine = StatsEngine();

        const string filter = "AMOUNT >= 100 AND AMOUNT <= 200";

        for (int i = 0; i < 3; i++) Query(engine, fx, filter);
        int[] staleBefore = Cold(fx.Dbf, fx.Cdx, filter);

        AppendAndReindex(fx, newId: 150, amount: 150, name: "NEWROW", category: "C0");

        int[] freshBrute = BruteForce(fx.Dbf, filter);
        int[] freshCold = Cold(fx.Dbf, fx.Cdx, filter);
        int[] warm = Query(engine, fx, filter);

        Assert.NotEqual(staleBefore, freshCold);   // the change is observable
        Assert.Equal(freshBrute, freshCold);       // Core == full scan
        Assert.Equal(freshCold, warm);             // engine reflects the change
        Assert.NotEqual(staleBefore, warm);        // never the stale answer
    }

    // ============================================================ (3) RESULT INVARIANCE (must — the hard gate)

    public static IEnumerable<object[]> Filters()
    {
        yield return new object[] { "ID = 1234" };
        yield return new object[] { "AMOUNT >= 100 AND AMOUNT <= 200" };
        yield return new object[] { "AMOUNT > 990" };
        yield return new object[] { "UPPER(NAME) = 'ALICE'" };
        yield return new object[] { "AMOUNT >= 100 AND ID <= 50" };
        yield return new object[] { "ID = 5 OR AMOUNT = 999" };
        yield return new object[] { "NOT (AMOUNT = 500)" };
        yield return new object[] { "AMOUNT >= 900 AND CATEGORY = 'C0'" };
        yield return new object[] { "CATEGORY = 'C1'" };
    }

    /// <summary>Cold (first query) AND warm (memo reused): Highlike(stats ON) == Core == full scan.</summary>
    [Theory]
    [MemberData(nameof(Filters))]
    public void ColdAndWarm_Equals_Core_Equals_FullScan(string filter)
    {
        using var fx = new TempTable();
        using var engine = StatsEngine();

        int[] brute = BruteForce(fx.Dbf, filter);
        int[] cold = Cold(fx.Dbf, fx.Cdx, filter);

        int[] first = Query(engine, fx, filter);   // cold: stats Loaded/Built
        int[] second = Query(engine, fx, filter);  // warm: stats reused from RAM

        Assert.Equal(brute, cold);
        Assert.Equal(cold, first);
        Assert.Equal(cold, second);
    }

    /// <summary>With EnableStatistics OFF the engine never touches the stats memo, yet the result is identical.</summary>
    [Theory]
    [MemberData(nameof(Filters))]
    public void StatsDisabled_Equals_Core_Equals_FullScan(string filter)
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = false });

        int[] brute = BruteForce(fx.Dbf, filter);
        int[] cold = Cold(fx.Dbf, fx.Cdx, filter);
        int[] got = Query(engine, fx, filter);

        Assert.Equal(brute, cold);
        Assert.Equal(cold, got);

        // Stats are never acquired when disabled.
        Assert.Equal(0, engine.StatsCacheStatistics.Computes);
    }

    /// <summary>Result invariance ACROSS a change: warm, mutate, query — equal to fresh Core + full scan.</summary>
    [Fact]
    public void ResultInvariance_AcrossChange()
    {
        using var fx = new TempTable();
        using var engine = StatsEngine();

        const string filter = "UPPER(NAME) = 'ALICE'";

        for (int i = 0; i < 3; i++) Query(engine, fx, filter);

        AppendAndReindex(fx, newId: 99999, amount: 1, name: "alice", category: "C2");

        int[] freshBrute = BruteForce(fx.Dbf, filter);
        int[] freshCold = Cold(fx.Dbf, fx.Cdx, filter);
        int[] warm = Query(engine, fx, filter);

        Assert.Equal(freshBrute, freshCold);
        Assert.Equal(freshCold, warm);
    }

    // ============================================================ (4) THREAD-SAFETY + DISPOSAL

    /// <summary>
    /// Many concurrent queries on ONE engine (sharing the stats memo, stats enabled) must all return the
    /// correct result set — no torn read, no lost/duplicated build — and the stats are still acquired only
    /// ONCE for the unchanged table.
    /// </summary>
    [Fact]
    public void ConcurrentQueries_AreCorrect_AndBuildStatsOnce()
    {
        using var fx = new TempTable();
        using var engine = StatsEngine();

        string[] filters =
        {
            "ID = 1500",
            "AMOUNT >= 100 AND AMOUNT <= 200",
            "UPPER(NAME) = 'ALICE'",
            "ID = 5 OR AMOUNT = 999",
            "AMOUNT > 990",
        };

        var expected = filters.ToDictionary(f => f, f => BruteForce(fx.Dbf, f));
        var failures = new ConcurrentBag<string>();

        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            string filter = filters[i % filters.Length];
            try
            {
                int[] got = Query(engine, fx, filter);
                if (!got.SequenceEqual(expected[filter]))
                    failures.Add($"{filter}: got {got.Length}, expected {expected[filter].Length}");
            }
            catch (Exception ex)
            {
                failures.Add($"{filter}: {ex.GetType().Name} {ex.Message}");
            }
        });

        Assert.Empty(failures);
        // Even under concurrency the unchanged table's stats are acquired exactly once.
        Assert.Equal(1, engine.StatsCacheStatistics.Computes);
    }

    /// <summary>Disposing the engine drops the stats memo (idempotent, never throws); the snapshot empties.</summary>
    [Fact]
    public void Dispose_DropsStatsMemo_Idempotent()
    {
        using var fx = new TempTable();
        var engine = StatsEngine();

        for (int i = 0; i < 3; i++) Query(engine, fx, "ID = 1500");
        Assert.True(engine.StatsCacheStatistics.Computes >= 1);

        engine.Dispose();
        engine.Dispose(); // idempotent

        var afterDispose = engine.StatsCacheStatistics;
        Assert.Equal(0, afterDispose.EntryCount);
    }

    // ============================================================ mutation helper (TEMP files only)

    private static void AppendAndReindex(TempTable fx, int newId, double amount, string name, string category)
    {
        using var w = DbfWriter.Open(fx.Dbf);
        w.AppendRecord(new object?[] { newId, amount, name, category });
        w.Reindex();
        w.Flush();
    }
}
