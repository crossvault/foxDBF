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

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// PHASE B-1 of the Highlike accelerator (design HIGHLIKE_ENGINE_DESIGN.md §2/§5, Peak 2): the
/// CROSS-QUERY in-memory cache of the DECODED per-tag index. Today every query re-walks the CDX
/// B-tree (<see cref="CdxTag.EnumerateEntries"/>); the cache should DECODE each tag once and reuse it
/// across queries — "tree-walk once, then serve from RAM" — owned by the <see cref="HighlikeEngine"/>,
/// keyed by table path + tag.
///
/// THE HARD GATE (invariant): a query served from the WARM cache returns EXACTLY the same record set as
/// the COLD Core path (<see cref="QueryOptimizer.FindRecords"/>) == a full scan. The cache is a SPEED
/// structure only: it must never change a result, and never serve a STALE recno after a detected change.
///
/// INVALIDATION (the correctness core): each cache entry is validated at query entry against a cheap
/// CHANGE-TOKEN = (reccount + the dbf last-update stamp + file length + last-write time); on mismatch it
/// is EVICTED + rebuilt. On a Fixed drive a <see cref="FileSystemWatcher"/> also evicts proactively
/// (disposed with the engine); on a Network path the token poll is the only guard.
///
/// SAFETY: TEMP files only; every fixture is deleted on dispose. No committed fixture is touched.
///
/// STATE: these tests are written FIRST against minimal compile-only stubs and are EXPECTED TO FAIL
/// until the warm cache + change-token + FileSystemWatcher are implemented.
/// </summary>
public sealed class HighlikeIndexCacheTests
{
    // ============================================================ temp fixture (TEMP files ONLY)

    /// <summary>
    /// A throwaway temp DBF + structural CDX with three single-field tags (IDTAG / AMTTAG / UNAME) so a
    /// filter can be written that drives EXACTLY ONE tag — making the per-tag hit/miss counters
    /// deterministic. Deleted (recursively) on dispose.
    /// </summary>
    private sealed class TempTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount { get; }

        private static readonly string[] Names = { "Alice", "Bob", "cherry", "David", "alice", "eric" };

        public TempTable(int rows = 2000, bool idTagOnly = false)
        {
            RowCount = rows;
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_hicache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "c.dbf");

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
                if (!idTagOnly)
                {
                    w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                    w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
                }
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ============================================================ ground truth helpers

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
        hits.Sort();
        return hits.ToArray();
    }

    private static int[] Sorted(IEnumerable<int> xs) => xs.OrderBy(x => x).ToArray();

    /// <summary>The cold Core baseline (no accelerator, no cache) for a freshly opened table+cdx.</summary>
    private static int[] Cold(string dbf, string cdx, string filter)
    {
        using var t = DbfTable.Open(dbf);
        using var c = CdxFile.Open(cdx, t);
        return Sorted(QueryOptimizer.FindRecords(t, c, filter).RecordNumbers);
    }

    private static int[] BruteForce(string dbf, string filter)
    {
        using var t = DbfTable.Open(dbf);
        return FullScan(t, filter, EvaluationContext.Default);
    }

    // ============================================================ (1) INVARIANT: WARM == COLD == Core == full scan

    [Theory]
    [InlineData("ID = 1234")]                              // numeric / integer, single tag
    [InlineData("AMOUNT >= 100 AND AMOUNT <= 200")]        // numeric range
    [InlineData("AMOUNT > 990")]                           // numeric open range
    [InlineData("UPPER(NAME) = 'ALICE'")]                  // character function tag
    [InlineData("AMOUNT >= 100 AND ID <= 50")]             // AND across two tags
    [InlineData("ID = 5 OR AMOUNT = 999")]                 // OR across two tags
    [InlineData("NOT (AMOUNT = 500)")]                     // NOT over an indexed leaf
    [InlineData("AMOUNT >= 900 AND CATEGORY = 'C0'")]      // one indexed + one residual
    [InlineData("CATEGORY = 'C1'")]                        // tag-less → full-scan fallback
    public void Warm_Equals_Cold_Equals_FullScan(string filter)
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine();

        int[] brute = BruteForce(fx.Dbf, filter);
        int[] cold = Cold(fx.Dbf, fx.Cdx, filter);

        // Run the SAME query twice through the engine: the 1st decodes (cold→cache), the 2nd is served
        // WARM from the cache. Both must equal the cold Core path and a full scan, byte-for-byte.
        int[] first, second;
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            first = Sorted(engine.FindRecords(t, c, filter).RecordNumbers);

        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            second = Sorted(engine.FindRecords(t, c, filter).RecordNumbers);

        Assert.Equal(brute, cold);          // sanity: Core == full scan
        Assert.Equal(cold, first);          // cold (first, decode) == Core
        Assert.Equal(cold, second);         // WARM (second, from RAM) == Core — the hard gate
    }

    // ============================================================ (3) CACHE-HIT determinism (perf proof, no timing)

    /// <summary>
    /// A single-tag filter touches EXACTLY ONE cached tag: the 1st query is a MISS (decoded once), the
    /// 2nd is a HIT (served from RAM). Proven via the exposed counters, not timing.
    /// </summary>
    [Fact]
    public void SecondQuery_IsHit_FirstIsMiss()
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine();

        var before = engine.CacheStatistics;

        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");
        var afterFirst = engine.CacheStatistics;

        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");
        var afterSecond = engine.CacheStatistics;

        // 1st query: at least one MISS (the IDTAG was decoded), no new HIT.
        Assert.True(afterFirst.Misses > before.Misses, "first query on a tag must be a MISS");
        Assert.Equal(before.Hits, afterFirst.Hits);

        // 2nd query: a HIT, no new MISS (the decoded tag is reused from RAM).
        Assert.True(afterSecond.Hits > afterFirst.Hits, "second query on a tag must be a HIT");
        Assert.Equal(afterFirst.Misses, afterSecond.Misses);

        // The decoded tag is resident.
        Assert.True(afterSecond.EntryCount >= 1);
    }

    [Fact]
    public void StreamOpenedIndexes_AreNeverStoredInTheWarmIndexCache()
    {
        using var fx = new TempTable(rows: 50, idTagOnly: true);
        using var engine = new HighlikeEngine(new HighlikeOptions { DriveKind = HighlikeDriveKind.Network });
        byte[] cdxBytes = File.ReadAllBytes(fx.Cdx);

        for (int i = 0; i < 2; i++)
        {
            using var table = DbfTable.Open(fx.Dbf);
            using var stream = new MemoryStream(cdxBytes, writable: false);
            using var cdx = CdxFile.Open(stream, table);
            _ = engine.FindRecords(table, cdx, "ID = 10");
        }

        var stats = engine.CacheStatistics;
        Assert.Equal(0, stats.Hits);
        Assert.Equal(0, stats.Misses);
        Assert.Equal(0, stats.EntryCount);
    }

    [Fact]
    public void RecreatedTag_WithDifferentDescendingFlag_IsNotServedFromWarmCache()
    {
        using var fx = new TempTable(rows: 50, idTagOnly: true);
        using var engine = new HighlikeEngine(new HighlikeOptions { DriveKind = HighlikeDriveKind.Network });

        using (var table = DbfTable.Open(fx.Dbf))
        using (var cdx = CdxFile.Open(fx.Cdx, table))
            _ = engine.FindRecords(table, cdx, "ID >= 10 AND ID <= 20");
        var warm = engine.CacheStatistics;

        byte[] dbfHeader = ReadDbfHeader8(fx.Dbf);
        long dbfLength = new FileInfo(fx.Dbf).Length;
        DateTime dbfWrite = File.GetLastWriteTimeUtc(fx.Dbf);
        long cdxLength = new FileInfo(fx.Cdx).Length;
        DateTime cdxWrite = File.GetLastWriteTimeUtc(fx.Cdx);

        using (var writer = DbfWriter.Open(fx.Dbf))
        {
            Assert.Equal(0, writer.DeleteTagsIn(fx.Cdx, structural: true, names: null));
            writer.CreateTag(new CdxTagDefinition("IDTAG", "ID", descending: true));
        }

        File.SetLastWriteTimeUtc(fx.Dbf, dbfWrite);
        File.SetLastWriteTimeUtc(fx.Cdx, cdxWrite);

        Assert.Equal(dbfLength, new FileInfo(fx.Dbf).Length);
        Assert.Equal(cdxLength, new FileInfo(fx.Cdx).Length);
        Assert.Equal(dbfWrite, File.GetLastWriteTimeUtc(fx.Dbf));
        Assert.Equal(cdxWrite, File.GetLastWriteTimeUtc(fx.Cdx));
        Assert.Equal(dbfHeader, ReadDbfHeader8(fx.Dbf));

        using (var table = DbfTable.Open(fx.Dbf))
        using (var cdx = CdxFile.Open(fx.Cdx, table))
        {
            Assert.True(Assert.IsType<CdxTag>(cdx.Tag("IDTAG")).Descending);
            _ = engine.FindRecords(table, cdx, "ID >= 10 AND ID <= 20");
        }
        var after = engine.CacheStatistics;

        Assert.True(after.Evictions > warm.Evictions, "changed tag identity must evict the cached entries");
        Assert.True(after.Misses > warm.Misses, "changed tag identity must be decoded again");
    }

    /// <summary>After a detected change the entry is EVICTED and the next query is a MISS again (re-decode).</summary>
    [Fact]
    public void AfterChange_EvictsThenMiss()
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine();

        // Warm the IDTAG.
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");
        var warm = engine.CacheStatistics;
        Assert.True(warm.Hits >= 1);

        // Mutate the table under the warm cache (our own writer), keeping the cdx consistent.
        AppendAndReindex(fx, newId: 99999, amount: 12345, name: "ZZTOP", category: "C2");

        // The next query (fresh handles, same path) must DETECT the change: an eviction + a fresh MISS.
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");
        var afterChange = engine.CacheStatistics;

        Assert.True(afterChange.Evictions > warm.Evictions, "a detected change must EVICT the stale entry");
        Assert.True(afterChange.Misses > warm.Misses, "the query after a change must re-decode (MISS)");
    }

    // ============================================================ (2) STALENESS — the killer

    public static IEnumerable<object[]> Mutations()
    {
        yield return new object[] { "append" };
        yield return new object[] { "delete" };
        yield return new object[] { "update" };
    }

    /// <summary>
    /// (2a) After modifying the table UNDER a warm cache via OUR OWN <see cref="DbfWriter"/>
    /// (append / delete / update), the NEXT query must NOT serve stale recnos: it reflects the change.
    /// A deleted / updated row must never leak from the cache. Verified against a fresh full scan + Core.
    /// </summary>
    [Theory]
    [MemberData(nameof(Mutations))]
    public void OwnWriterChange_NeverServesStale(string kind)
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine();

        const string filter = "AMOUNT >= 100 AND AMOUNT <= 200";

        // Warm the cache on the original data.
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, filter);

        int[] staleBefore = Cold(fx.Dbf, fx.Cdx, filter); // the pre-change answer

        // Mutate via our own writer and rebuild the cdx so dbf + cdx stay consistent.
        ApplyMutation(fx, kind, filter);

        int[] freshBrute = BruteForce(fx.Dbf, filter);
        int[] freshCold = Cold(fx.Dbf, fx.Cdx, filter);

        int[] warm;
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            warm = Sorted(engine.FindRecords(t, c, filter).RecordNumbers);

        // The mutation must actually have changed the answer (otherwise the test proves nothing).
        Assert.NotEqual(staleBefore, freshCold);

        // The warm engine reflects the CHANGE — equal to the fresh cold path and a fresh full scan,
        // and NOT the stale pre-change answer.
        Assert.Equal(freshBrute, freshCold);
        Assert.Equal(freshCold, warm);
        Assert.NotEqual(staleBefore, warm);
    }

    /// <summary>
    /// (2b) A simulated EXTERNAL change (the engine is NOT notified) on a forced NETWORK drive — where no
    /// FileSystemWatcher is attached — must still be caught by the change-token poll: the next query
    /// reflects the change / rebuilds and never serves a stale recno.
    /// </summary>
    [Fact]
    public void ExternalChange_NetworkMode_TokenStillCatchesIt()
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine(new HighlikeOptions { DriveKind = HighlikeDriveKind.Network });

        const string filter = "AMOUNT >= 100 AND AMOUNT <= 200";

        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, filter);

        int[] staleBefore = Cold(fx.Dbf, fx.Cdx, filter);

        // EXTERNAL change: append + reindex out-of-band, then push the last-write time forward so the
        // token (reccount + stamp + length + last-write) differs even on a coarse clock.
        AppendAndReindex(fx, newId: 150, amount: 150, name: "EXTERN", category: "C0");
        var future = DateTime.Now.AddMinutes(5);
        File.SetLastWriteTime(fx.Dbf, future);
        File.SetLastWriteTime(fx.Cdx, future);

        int[] freshCold = Cold(fx.Dbf, fx.Cdx, filter);
        int[] warm;
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            warm = Sorted(engine.FindRecords(t, c, filter).RecordNumbers);

        Assert.NotEqual(staleBefore, freshCold); // the change is observable
        Assert.Equal(freshCold, warm);           // token poll caught it — no stale recno
        Assert.NotEqual(staleBefore, warm);

        // No FileSystemWatcher is ever attached in forced Network mode.
        Assert.Equal(0, engine.CacheStatistics.ActiveWatchers);
    }

    // ============================================================ (2c) CDX-ONLY change with a BYTE-IDENTICAL dbf token

    /// <summary>
    /// THE cdx-blind killer (MUST-FIX #1/#3): warm the cache, then perform an IN-PLACE update + reindex so
    /// the answer changes, but force the <c>.dbf</c> change-token to be BYTE-IDENTICAL afterwards — an
    /// in-place <c>UpdateRecord</c> changes neither reccount nor length, the 3-byte stamp is day-granularity
    /// (same day), and we RESTORE the dbf last-write time (simulating a coarse-granularity / same-bucket
    /// clock). The ONLY observable change is the <c>.cdx</c> (a reindex rewrites it and bumps its last-write).
    /// A cache whose token reads only the <c>.dbf</c> would MATCH and serve STALE recnos; the cache MUST fold
    /// the <c>.cdx</c> in, detect the change, evict, re-decode, and return warm == cold == full scan.
    /// </summary>
    [Fact]
    public void CdxOnlyChange_IdenticalDbfToken_NeverServesStale()
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine(new HighlikeOptions { DriveKind = HighlikeDriveKind.Network });

        const string filter = "AMOUNT >= 100 AND AMOUNT <= 200";

        // Warm the cache (token recorded with the original cdx).
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, filter);
        var warmStats = engine.CacheStatistics;

        int[] staleBefore = Cold(fx.Dbf, fx.Cdx, filter);

        // Capture the FULL dbf-side token BEFORE the mutation.
        long dbfLenBefore = new FileInfo(fx.Dbf).Length;
        byte[] hdrBefore = ReadDbfHeader8(fx.Dbf);
        DateTime dbfWriteBefore = File.GetLastWriteTimeUtc(fx.Dbf);
        var cdxBefore = (Len: new FileInfo(fx.Cdx).Length, Write: File.GetLastWriteTimeUtc(fx.Cdx));

        // Resolve a matching row, then UPDATE IT IN PLACE so AMOUNT drops OUT of [100,200].
        int idx = FirstMatchingIndex(fx, filter);
        Assert.True(idx >= 0, "fixture must contain a matching row to update");
        object?[] replacement;
        using (var probe = DbfTable.Open(fx.Dbf))
        {
            var rec = probe.GetRecord(idx)!.Value;
            replacement = new object?[] { rec["ID"], 999999, rec["NAME"], rec["CATEGORY"] };
        }
        MutateAndReindex(fx, w => w.UpdateRecord(idx, replacement));

        // Force the dbf token to be IDENTICAL again: an in-place update leaves reccount/length/stamp
        // unchanged; restore the last-write time to erase the only remaining dbf-side signal.
        File.SetLastWriteTimeUtc(fx.Dbf, dbfWriteBefore);

        // SANITY: the dbf-side token is now byte-identical — a dbf-only cache would NOT detect this.
        Assert.Equal(dbfLenBefore, new FileInfo(fx.Dbf).Length);
        Assert.Equal(hdrBefore, ReadDbfHeader8(fx.Dbf));
        Assert.Equal(dbfWriteBefore, File.GetLastWriteTimeUtc(fx.Dbf));

        // SANITY: the cdx DID change (the reindex rewrote it) — so detection must come via the cdx.
        var cdxAfter = (Len: new FileInfo(fx.Cdx).Length, Write: File.GetLastWriteTimeUtc(fx.Cdx));
        Assert.True(cdxAfter != cdxBefore, "the reindex must have changed the cdx (length and/or last-write)");

        int[] freshBrute = BruteForce(fx.Dbf, filter);
        int[] freshCold = Cold(fx.Dbf, fx.Cdx, filter);

        int[] warm;
        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            warm = Sorted(engine.FindRecords(t, c, filter).RecordNumbers);

        var afterStats = engine.CacheStatistics;

        // The mutation actually changed the answer (otherwise the test proves nothing).
        Assert.NotEqual(staleBefore, freshCold);

        // The hard gate: warm reflects the change via the CDX token — equal to fresh cold + full scan,
        // never the stale pre-change answer, even though the dbf token is byte-identical.
        Assert.Equal(freshBrute, freshCold);
        Assert.Equal(freshCold, warm);
        Assert.NotEqual(staleBefore, warm);

        // The stale entry was evicted and re-decoded (detected purely via the cdx).
        Assert.True(afterStats.Evictions > warmStats.Evictions, "the cdx change must EVICT the stale entry");
    }

    /// <summary>Reads the dbf header bytes that feed the change-token: byte0 + last-update stamp (1..3) + reccount (4..7).</summary>
    private static byte[] ReadDbfHeader8(string dbf)
    {
        using var fs = new FileStream(dbf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[8];
        int total = 0;
        while (total < buf.Length)
        {
            int n = fs.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }
        return buf;
    }

    // ============================================================ (4) THREAD-SAFETY: concurrent queries

    /// <summary>
    /// Many concurrent queries on ONE engine (sharing the warm cache) must all return the correct result
    /// set — no torn reads, no lost decode, no stale entry. Parallel stress over a mix of filters.
    /// </summary>
    [Fact]
    public void ConcurrentQueries_AreCorrect()
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine();

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
                using var t = DbfTable.Open(fx.Dbf);
                using var c = CdxFile.Open(fx.Cdx, t);
                var got = Sorted(engine.FindRecords(t, c, filter).RecordNumbers);
                if (!got.SequenceEqual(expected[filter]))
                    failures.Add($"{filter}: got {got.Length}, expected {expected[filter].Length}");
            }
            catch (Exception ex)
            {
                failures.Add($"{filter}: {ex.GetType().Name} {ex.Message}");
            }
        });

        Assert.Empty(failures);
    }

    // ============================================================ (4b) BOUNDED: LRU slot eviction + watcher cap

    /// <summary>
    /// MUST-FIX #4: a long-lived engine querying MANY distinct tables must NOT grow slots or OS watcher
    /// handles without bound. With a small <see cref="HighlikeOptions.MaxCachedTables"/> /
    /// <see cref="HighlikeOptions.MaxWatchers"/> cap, querying more distinct tables than the cap keeps the
    /// resident slot count (and watcher count) bounded — the least-recently-used slots are dropped and their
    /// watchers disposed. Speed-only: a dropped slot just re-decodes on its next query.
    /// </summary>
    [Fact]
    public void Cache_IsBounded_LruEvictsSlots_AndCapsWatchers()
    {
        var tables = new List<TempTable>();
        try
        {
            using var engine = new HighlikeEngine(new HighlikeOptions
            {
                DriveKind = HighlikeDriveKind.Fixed,
                MaxCachedTables = 2,
                MaxWatchers = 1,
            });

            for (int i = 0; i < 6; i++)
            {
                var fx = new TempTable(rows: 50);
                tables.Add(fx);
                using var t = DbfTable.Open(fx.Dbf);
                using var c = CdxFile.Open(fx.Cdx, t);
                engine.FindRecords(t, c, "ID = 10"); // drives exactly one tag (IDTAG) per table
            }

            var s = engine.CacheStatistics;
            // At most MaxCachedTables slots survive, each holding the single driven tag → entries bounded.
            Assert.True(s.EntryCount <= 2, $"slots must be LRU-bounded to the cap; {s.EntryCount} resident");
            // Live watchers never exceed the cap (beyond it, new slots fall back to the token poll).
            Assert.True(s.ActiveWatchers <= 1, $"watchers must be capped; {s.ActiveWatchers} active");
        }
        finally
        {
            foreach (var f in tables) f.Dispose();
        }
    }

    // ============================================================ (5) DISPOSAL releases the FileSystemWatcher

    /// <summary>
    /// On a Fixed drive the engine attaches a <see cref="FileSystemWatcher"/> per watched table; disposing
    /// the engine MUST release them all (no leaked watcher / handle). Proven via the ActiveWatchers counter.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesFileSystemWatchers()
    {
        using var fx = new TempTable();
        var engine = new HighlikeEngine(new HighlikeOptions { DriveKind = HighlikeDriveKind.Fixed });

        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");

        // Fixed drive → at least one proactive watcher is live while the entry is cached.
        Assert.True(engine.CacheStatistics.ActiveWatchers >= 1, "Fixed drive must attach a FileSystemWatcher");

        engine.Dispose();

        // After disposal every watcher is released.
        Assert.Equal(0, engine.CacheStatistics.ActiveWatchers);
    }

    [Fact]
    public void TableDispose_ReleasesWatcherOwnedByUseHighlike()
    {
        using var fx = new TempTable();
        var table = DbfTable.Open(fx.Dbf).UseHighlike(
            new HighlikeOptions { DriveKind = HighlikeDriveKind.Fixed });
        using var engine = Assert.IsType<HighlikeEngine>(table.Accelerator);
        _ = table.Query("ID = 1500");
        Assert.True(engine.CacheStatistics.ActiveWatchers >= 1);

        table.Dispose();

        Assert.Equal(0, engine.CacheStatistics.ActiveWatchers);
    }

    [Fact]
    public void ReplacingAndDetachingUseHighlike_DisposesEachPreviousOwnedEngine()
    {
        using var fx = new TempTable();
        using var table = DbfTable.Open(fx.Dbf);
        table.UseHighlike(new HighlikeOptions { DriveKind = HighlikeDriveKind.Fixed });
        using var first = Assert.IsType<HighlikeEngine>(table.Accelerator);
        _ = table.Query("ID = 1500");
        Assert.True(first.CacheStatistics.ActiveWatchers >= 1);

        table.UseHighlike(new HighlikeOptions { DriveKind = HighlikeDriveKind.Fixed });
        using var second = Assert.IsType<HighlikeEngine>(table.Accelerator);
        Assert.Equal(0, first.CacheStatistics.ActiveWatchers);
        _ = table.Query("ID = 1500");
        Assert.True(second.CacheStatistics.ActiveWatchers >= 1);

        table.UseAccelerator(null);

        Assert.Null(table.Accelerator);
        Assert.Equal(0, second.CacheStatistics.ActiveWatchers);
    }

    [Fact]
    public void CallerOwnedAccelerator_RemainsActiveUntilCallerDisposesIt()
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine(
            new HighlikeOptions { DriveKind = HighlikeDriveKind.Fixed });
        var table = DbfTable.Open(fx.Dbf).UseAccelerator(engine);
        _ = table.Query("ID = 1500");
        Assert.True(engine.CacheStatistics.ActiveWatchers >= 1);

        table.Dispose();

        Assert.True(engine.CacheStatistics.ActiveWatchers >= 1);
        engine.Dispose();
        Assert.Equal(0, engine.CacheStatistics.ActiveWatchers);
    }

    [Fact]
    public void SameReferencePublicReattach_PreservesOwnershipAcrossDoubleTableDispose()
    {
        using var fx = new TempTable();
        var table = DbfTable.Open(fx.Dbf).UseHighlike(
            new HighlikeOptions { DriveKind = HighlikeDriveKind.Fixed });
        using var engine = Assert.IsType<HighlikeEngine>(table.Accelerator);
        _ = table.Query("ID = 1500");
        Assert.True(engine.CacheStatistics.ActiveWatchers >= 1);

        table.UseAccelerator(engine);
        table.Dispose();
        table.Dispose();

        Assert.Equal(0, engine.CacheStatistics.ActiveWatchers);
    }

    /// <summary>(6) Forced Network/Shared mode never attaches a watcher, yet warm reuse still works (token poll).</summary>
    [Fact]
    public void NetworkMode_NoWatcher_StillWarmReuses()
    {
        using var fx = new TempTable();
        using var engine = new HighlikeEngine(new HighlikeOptions { DriveKind = HighlikeDriveKind.Network });

        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");
        var afterFirst = engine.CacheStatistics;

        using (var t = DbfTable.Open(fx.Dbf))
        using (var c = CdxFile.Open(fx.Cdx, t))
            engine.FindRecords(t, c, "ID = 1500");
        var afterSecond = engine.CacheStatistics;

        Assert.Equal(0, afterFirst.ActiveWatchers);                       // no FSW over the network
        Assert.Equal(0, afterSecond.ActiveWatchers);
        Assert.True(afterSecond.Hits > afterFirst.Hits, "warm reuse must still work via the token poll");
    }

    // ============================================================ mutation helpers (TEMP files only)

    private static void ApplyMutation(TempTable fx, string kind, string filter)
    {
        switch (kind)
        {
            case "append":
                // Add a row that MATCHES the filter (150 ∈ [100,200]) — it must appear after the change.
                AppendAndReindex(fx, newId: 150, amount: 150, name: "NEWROW", category: "C0");
                break;

            case "delete":
            {
                // Delete a row that currently MATCHES the filter — it must disappear from the result.
                // Resolve the target BEFORE opening the writer (the writer locks the file).
                int idx = FirstMatchingIndex(fx, filter);
                Assert.True(idx >= 0, "fixture must contain a matching row to delete");
                MutateAndReindex(fx, w => w.Delete(idx));
                break;
            }

            case "update":
            {
                // Change a MATCHING row's AMOUNT to OUT of range — it must drop out of the result.
                // Read the row + locate it BEFORE opening the writer.
                int idx = FirstMatchingIndex(fx, filter);
                Assert.True(idx >= 0, "fixture must contain a matching row to update");
                object?[] replacement;
                using (var probe = DbfTable.Open(fx.Dbf))
                {
                    var rec = probe.GetRecord(idx)!.Value;
                    replacement = new object?[] { rec["ID"], 999999, rec["NAME"], rec["CATEGORY"] };
                }
                MutateAndReindex(fx, w => w.UpdateRecord(idx, replacement));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static int FirstMatchingIndex(TempTable fx, string filter)
    {
        using var t = DbfTable.Open(fx.Dbf);
        var compiled = VfpExpression.Parse(filter).Compile(EvaluationContext.Default);
        int recno = 0;
        foreach (var rec in t.EnumerateAll(includeDeleted: true))
        {
            int idx = recno++;
            var v = compiled(new Row(rec, recno, t.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical) return idx; // 0-based physical index
        }
        return -1;
    }

    private static void AppendAndReindex(TempTable fx, int newId, double amount, string name, string category)
        => MutateAndReindex(fx, w => w.AppendRecord(new object?[] { newId, amount, name, category }));

    private static void MutateAndReindex(TempTable fx, Action<DbfWriter> mutate)
    {
        using var w = DbfWriter.Open(fx.Dbf);
        mutate(w);
        w.Reindex();   // keep the structural cdx consistent with the mutated data
        w.Flush();
    }
}
