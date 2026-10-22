using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Highlike;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase A of the Highlike accelerator: the <c>.stx</c> statistics sidecar (design §6) — NDV /
/// Min / Max / null / deleted counts, JSON round-trip, staleness detection, and the
/// NON-NEGOTIABLE HINT-ONLY invariant.
///
/// The sidecar is a pure HINT. Its numbers feed the cost-based planner (which only decides HOW a
/// query is computed) but can NEVER change WHAT it returns. The adversarial tests below pin that
/// down: even a deliberately WRONG or corrupt <c>.stx</c> leaves the result set byte-for-byte
/// equal to the Core optimizer and to a full scan.
///
/// SAFETY: every table + sidecar lives under a freshly created TEMP directory, deleted on dispose;
/// no committed fixture under data/ is ever touched.
/// </summary>
public sealed class StxStatisticsTests : IClassFixture<StxStatisticsTests.StxFixture>
{
    private readonly StxFixture _fx;
    public StxStatisticsTests(StxFixture fx) => _fx = fx;

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

    /// <summary>
    /// Independent brute-force distinct-count of a tag's DECODED key values (a HashSet over the
    /// enumerated entries) — a different algorithm than the "key change while walking sorted
    /// entries" the builder uses, so it is a genuine cross-check of NDV.
    /// </summary>
    private static long BruteForceNdv(CdxTag tag)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in tag.EnumerateEntries())
            set.Add(CanonKey(tag, e.Key));
        return set.Count;
    }

    private static string CanonKey(CdxTag tag, byte[] key) => tag.KeyType switch
    {
        IndexKeyType.Integer or IndexKeyType.Numeric or IndexKeyType.Date or IndexKeyType.DateTime
            => tag.DecodeKey(key).Value?.ToString() ?? "<null>",
        // Character (and composite character keys like UPPER(NAME)): the raw collated bytes,
        // trailing pad trimmed — exactly how IndexKey decodes a character key.
        _ => Encoding.Latin1.GetString(key).TrimEnd(' ', '\0'),
    };

    private static long BruteForceFieldNulls(DbfTable table, string field)
    {
        long n = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
            if (rec[field] is null) n++;
        return n;
    }

    private static int BruteForceDeleted(DbfTable table)
        => table.EnumerateAll(includeDeleted: true).Count(r => r.IsDeleted);

    /// <summary>Reads the DBF header last-update stamp (bytes 1..3 = yy/mm/dd) as <c>yyyy-MM-dd</c>.</summary>
    private static string ReadDbfUpdStamp(string dbfPath)
    {
        using var fs = File.OpenRead(dbfPath);
        var h = new byte[4];
        fs.ReadExactly(h, 0, 4);
        int year = 1900 + h[1];
        return $"{year:D4}-{h[2]:D2}-{h[3]:D2}";
    }

    // ============================================================ NDV / Min / Max correctness

    [Fact]
    public void Build_CapturesEveryTag()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        Assert.NotNull(stats.ForKey("ID"));
        Assert.NotNull(stats.ForKey("AMOUNT"));
        Assert.NotNull(stats.ForKey("UPPER(NAME)"));
        Assert.NotNull(stats.ForKey("CODE"));
    }

    [Fact]
    public void Ndv_EqualsBruteForceDistinctCount_PerTag()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        foreach (var (keyExpr, tagName) in new[] { ("ID", "IDTAG"), ("AMOUNT", "AMTTAG"), ("UPPER(NAME)", "UNAME") })
        {
            var tag = cdx.Tag(tagName)!;
            long expected = BruteForceNdv(tag);
            Assert.Equal(expected, stats.ForKey(keyExpr)!.Ndv);
        }

        // And the known cardinalities for this fixture (sanity on the brute force itself).
        Assert.Equal(_fx.RowCount, stats.ForKey("ID")!.Ndv);     // every ID is unique
        Assert.Equal(100, stats.ForKey("AMOUNT")!.Ndv);          // i % 100 → 0..99
        Assert.Equal(5, stats.ForKey("UPPER(NAME)")!.Ndv);       // 5 distinct upper-cased names
    }

    [Fact]
    public void MinMax_EqualTrueExtremes()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        // Integer tag: exact decimal strings.
        Assert.Equal("1", stats.ForKey("ID")!.Min);
        Assert.Equal(_fx.RowCount.ToString(CultureInfo.InvariantCulture), stats.ForKey("ID")!.Max);

        // Character / composite tag: the decoded strings (ordinal extremes).
        Assert.Equal("ALICE", stats.ForKey("UPPER(NAME)")!.Min);
        Assert.Equal("ERIC", stats.ForKey("UPPER(NAME)")!.Max);

        // Numeric tag: compare as numbers so the exact textual form (0 vs 0.00) is not over-fitted.
        var amt = stats.ForKey("AMOUNT")!;
        Assert.Equal(0d, double.Parse(amt.Min!, CultureInfo.InvariantCulture), 3);
        Assert.Equal(99d, double.Parse(amt.Max!, CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void NullCount_PerTag_EqualsDbfNullFlagCount()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        long expected = BruteForceFieldNulls(table, "CODE");
        Assert.Equal(_fx.NullCodeCount, expected); // sanity on the fixture
        Assert.Equal(expected, stats.ForKey("CODE")!.Nulls);

        // A field with no nulls reports zero.
        Assert.Equal(0, stats.ForKey("ID")!.Nulls);
    }

    [Fact]
    public void Source_CapturesRecCountAndDeletedCount()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        Assert.Equal(table.RecordCount, stats.Source.RecCount);
        Assert.Equal(_fx.RowCount, stats.Source.RecCount);

        int expectedDeleted = BruteForceDeleted(table);
        Assert.Equal(_fx.DeletedCount, expectedDeleted); // sanity on the fixture
        Assert.Equal(expectedDeleted, stats.Source.Deleted);
    }

    [Fact]
    public void Build_WithoutCdx_HasNoTagsButStillCapturesSource()
    {
        using var table = DbfTable.Open(_fx.Dbf);

        var stats = HighlikeStatistics.Build(table, cdx: null);

        Assert.Empty(stats.Tags);
        Assert.Equal(table.RecordCount, stats.Source.RecCount);
    }

    // ============================================================ JSON round-trip

    [Fact]
    public void Json_RoundTrips_AllValues()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var built = HighlikeStatistics.Build(table, cdx);
        string json = built.ToJson();
        var back = StxStatistics.FromJson(json);

        Assert.NotNull(back);
        Assert.Equal(built.Source.RecCount, back!.Source.RecCount);
        Assert.Equal(built.Source.Deleted, back.Source.Deleted);
        Assert.Equal(built.Source.UpdStamp, back.Source.UpdStamp);

        foreach (var key in new[] { "ID", "AMOUNT", "UPPER(NAME)", "CODE" })
        {
            var a = built.ForKey(key)!;
            var b = back.ForKey(key)!;
            Assert.Equal(a.Ndv, b.Ndv);
            Assert.Equal(a.Nulls, b.Nulls);
            Assert.Equal(a.Min, b.Min);
            Assert.Equal(a.Max, b.Max);
        }
    }

    [Fact]
    public void Json_IsSmallAndHumanReadable()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        string json = HighlikeStatistics.Build(table, cdx).ToJson();

        // Human-readable: the documented field names are present verbatim.
        Assert.Contains("\"src\"", json);
        Assert.Contains("\"reccount\"", json);
        Assert.Contains("\"tags\"", json);
        Assert.Contains("\"ndv\"", json);
        Assert.Contains("\"min\"", json);
        Assert.Contains("\"max\"", json);
        // PHASE C: histogram and MCV are part of the sidecar (additive).
        Assert.Contains("\"hist\"", json);
        Assert.Contains("\"mcv\"", json);

        // Small: a handful of tags with Phase C histogram/MCV is well under ~7 KB.
        Assert.True(json.Length < 8192, $"expected a compact sidecar, got {json.Length} chars");
    }

    [Fact]
    public void FromJson_NullOrGarbage_ReturnsNull_NeverThrows()
    {
        Assert.Null(StxStatistics.FromJson(null));
        Assert.Null(StxStatistics.FromJson(""));
        Assert.Null(StxStatistics.FromJson("this is not json {{{"));
    }

    // ============================================================ Analyze (UPDATE STATISTICS) + Load

    [Fact]
    public void Analyze_WritesSidecar_AndLoadReadsItBack()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        string stxPath = HighlikeStatistics.StxPath(table);
        try
        {
            var written = HighlikeStatistics.Analyze(table, cdx);
            Assert.True(File.Exists(stxPath), "Analyze must write the .stx sidecar");

            var loaded = HighlikeStatistics.Load(table, cdx);
            Assert.NotNull(loaded);
            Assert.Equal(written.Source.RecCount, loaded!.Source.RecCount);
            Assert.Equal(written.ForKey("ID")!.Ndv, loaded.ForKey("ID")!.Ndv);
            Assert.Equal(written.ForKey("UPPER(NAME)")!.Max, loaded.ForKey("UPPER(NAME)")!.Max);
        }
        finally
        {
            try { File.Delete(stxPath); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void Analyze_IsIdempotent()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        string stxPath = HighlikeStatistics.StxPath(table);
        try
        {
            var first = HighlikeStatistics.Analyze(table, cdx);
            var second = HighlikeStatistics.Analyze(table, cdx);

            Assert.Equal(first.Source.RecCount, second.Source.RecCount);
            Assert.Equal(first.Source.Deleted, second.Source.Deleted);
            foreach (var key in new[] { "ID", "AMOUNT", "UPPER(NAME)", "CODE" })
            {
                Assert.Equal(first.ForKey(key)!.Ndv, second.ForKey(key)!.Ndv);
                Assert.Equal(first.ForKey(key)!.Min, second.ForKey(key)!.Min);
                Assert.Equal(first.ForKey(key)!.Max, second.ForKey(key)!.Max);
                Assert.Equal(first.ForKey(key)!.Nulls, second.ForKey(key)!.Nulls);
            }
        }
        finally
        {
            try { File.Delete(stxPath); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void Load_WhenSidecarMissing_ReturnsNull()
    {
        using var local = new TinyTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        Assert.False(File.Exists(HighlikeStatistics.StxPath(table)));
        Assert.Null(HighlikeStatistics.Load(table, cdx));
    }

    // ============================================================ staleness

    [Fact]
    public void Staleness_AppendingARecord_MakesLoadedStatsStale_AndRebuildRefreshes()
    {
        using var local = new TinyTable();

        // Build + persist fresh stats.
        using (var table = DbfTable.Open(local.Dbf))
        using (var cdx = CdxFile.Open(local.Cdx, table))
        {
            var fresh = HighlikeStatistics.Analyze(table, cdx);
            Assert.True(fresh.IsFreshFor(table));
            Assert.NotNull(HighlikeStatistics.Load(table, cdx)); // fresh → returned
        }

        // Mutate the table out from under the sidecar: one more record → reccount changes.
        using (var w = DbfWriter.Open(local.Dbf))
        {
            w.AppendRecord(new object?[] { 9999, "Z" });
            w.Flush();
        }

        // The persisted stats are now STALE: detected and ignored on load…
        using (var table = DbfTable.Open(local.Dbf))
        using (var cdx = CdxFile.Open(local.Cdx, table))
        {
            var stale = StxStatistics.FromJson(File.ReadAllText(HighlikeStatistics.StxPath(table)));
            Assert.NotNull(stale);
            Assert.False(stale!.IsFreshFor(table));          // token mismatch
            Assert.Null(HighlikeStatistics.Load(table, cdx)); // stale → ignored

            // …and lazily rebuilt to match the new table.
            var rebuilt = HighlikeStatistics.GetOrBuild(table, cdx);
            Assert.True(rebuilt.IsFreshFor(table));
            Assert.Equal(table.RecordCount, rebuilt.Source.RecCount);
        }
    }

    // ============================================================ HINT-ONLY SAFETY (the invariant)

    /// <summary>
    /// The critical adversarial test: a deliberately WRONG but FRESH <c>.stx</c> (NDV collapsed to
    /// 1, Min/Max pushed outside the real range so equality pruning would wrongly say "0 rows")
    /// must NOT change the result set. Highlike == Core == full scan regardless — the planner may
    /// choose a terrible plan, but the residual still confirms every candidate.
    /// </summary>
    [Theory]
    [InlineData("ID = 123")]
    [InlineData("AMOUNT = 50 AND ID <= 200")]
    public void WrongStats_DoNotChangeResults(string filter)
    {
        using var local = new BigTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        // Write a corrupt-but-fresh sidecar (its src token matches the current table, so a future
        // planner would actually TRUST it — and must still be unable to change the answer).
        string stxPath = HighlikeStatistics.StxPath(table);
        string dbfName = Path.GetFileName(local.Dbf);
        string stamp = ReadDbfUpdStamp(local.Dbf);
        File.WriteAllText(stxPath, $$"""
        {
          "src": { "dbf": "{{dbfName}}", "reccount": {{table.RecordCount}}, "updstamp": "{{stamp}}", "deleted": 0 },
          "built": "2000-01-01T00:00:00Z",
          "tags": {
            "ID":     { "ndv": 1, "nulls": 0, "min": "999999", "max": "999999" },
            "AMOUNT": { "ndv": 1, "nulls": 0, "min": "999999", "max": "999999" }
          }
        }
        """);

        var expected = FullScan(table, filter, EvaluationContext.Default);
        var core = QueryOptimizer.FindRecords(table, cdx, filter);

        // Stats ON, with the WRONG sidecar present on disk.
        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });
        var hi = engine.FindRecords(table, cdx, filter);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));

        // Non-vacuous: the filter genuinely matches at least one record.
        Assert.NotEmpty(expected);
    }

    /// <summary>
    /// A syntactically CORRUPT <c>.stx</c> (not JSON) must be ignored: the query degrades to the
    /// Core path and still returns the full-scan set. Corrupt stats never throw, never change
    /// results.
    /// </summary>
    [Fact]
    public void CorruptStats_AreIgnored_ResultsUnchanged()
    {
        using var local = new BigTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        File.WriteAllText(HighlikeStatistics.StxPath(table), "}}} not json at all {{{");

        const string filter = "ID = 123";
        var expected = FullScan(table, filter, EvaluationContext.Default);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });
        var hi = engine.FindRecords(table, cdx, filter);

        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    // ============================================================ lazy auto-build via the engine

    /// <summary>
    /// With statistics ENABLED and no fresh sidecar, the first query lazily builds + persists the
    /// <c>.stx</c> — and (HINT-ONLY) the result is still exactly the full-scan set.
    /// </summary>
    [Fact]
    public void EnableStatistics_LazilyBuildsSidecar_OnFirstQuery()
    {
        using var local = new BigTable();
        using var table = DbfTable.Open(local.Dbf);

        string stxPath = HighlikeStatistics.StxPath(table);
        Assert.False(File.Exists(stxPath)); // none yet

        table.UseHighlike(new HighlikeOptions { EnableStatistics = true });

        const string filter = "ID = 123";
        var expected = FullScan(table, filter, EvaluationContext.Default);
        var hi = table.Query(filter);

        // The result is correct (HINT-ONLY) AND the sidecar now exists (lazy auto-build).
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
        Assert.True(File.Exists(stxPath), "first use with EnableStatistics must lazily build the .stx");
    }

    // ============================================================ edge: empty table

    [Fact]
    public void EmptyTable_Analyze_DoesNotThrow_AndReportsZeroes()
    {
        using var local = new EmptyTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        string stxPath = HighlikeStatistics.StxPath(table);
        try
        {
            var stats = HighlikeStatistics.Analyze(table, cdx);

            Assert.Equal(0, stats.Source.RecCount);
            Assert.Equal(0, stats.Source.Deleted);

            var idStats = stats.ForKey("ID");
            Assert.NotNull(idStats);
            Assert.Equal(0, idStats!.Ndv);
            Assert.Null(idStats.Min);
            Assert.Null(idStats.Max);
        }
        finally
        {
            try { File.Delete(stxPath); } catch { /* temp cleanup */ }
        }
    }

    // ============================================================ DESCENDING tag Min/Max (no swap)

    /// <summary>
    /// A DESCENDING tag must report the SAME bounds as its ascending twin: Min is the smallest stored
    /// key and Max the largest, INDEPENDENT of the (reversed) enumeration direction. Regression for the
    /// Min/Max swap bug — <c>CdxTag.EnumerateEntries()</c> yields a DESC tag in reverse stored order, so
    /// the naive "first key = Min, last key = Max" makes Min &gt; Max on every descending tag.
    /// </summary>
    [Fact]
    public void DescendingTag_MinMax_AreNotSwapped()
    {
        using var local = new VariantTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        var asc = stats.ForTag(cdx.Tag("IDASC")!)!;
        var desc = stats.ForTag(cdx.Tag("IDDESC")!)!;

        Assert.False(asc.Descending);
        Assert.True(desc.Descending);

        // Min is the SMALLEST key, Max the LARGEST — not reversed.
        Assert.Equal("1", desc.Min);
        Assert.Equal(local.RowCount.ToString(CultureInfo.InvariantCulture), desc.Max);
        Assert.True(int.Parse(desc.Min!, CultureInfo.InvariantCulture)
                    < int.Parse(desc.Max!, CultureInfo.InvariantCulture),
            $"Min must be < Max, got Min={desc.Min} Max={desc.Max}");

        // A descending tag reports identical bounds to its ascending twin.
        Assert.Equal(asc.Min, desc.Min);
        Assert.Equal(asc.Max, desc.Max);
    }

    // ============================================================ GENERAL collation char-key Min/Max

    /// <summary>
    /// For a non-MACHINE collation (here GENERAL) the stored CDX character-key bytes are collation
    /// WEIGHT bytes, not the original characters. The bound must NOT be rendered as a trimmed Latin1
    /// string (that emits weight-garbage and breaks comparability); it is persisted as the raw weight
    /// bytes (<c>0x…</c> hex), which stays comparable in collation-byte space. The MACHINE twin over the
    /// SAME key expression stays human-readable.
    /// </summary>
    [Fact]
    public void GeneralCollationCharKey_MinMax_AreComparableWeightBytes_NotGarbageText()
    {
        using var local = new VariantTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        var gen = stats.ForTag(cdx.Tag("NGEN")!)!;
        Assert.Equal("GENERAL", gen.Collation, ignoreCase: true);
        Assert.StartsWith("0x", gen.Min);
        Assert.StartsWith("0x", gen.Max);
        // Comparable in collation-byte space: Min sorts at or before Max.
        Assert.True(string.CompareOrdinal(gen.Min, gen.Max) <= 0,
            $"weight-byte Min must sort <= Max, got Min={gen.Min} Max={gen.Max}");

        // The MACHINE twin (identity collation) stays readable.
        var mach = stats.ForTag(cdx.Tag("NMACH")!)!;
        Assert.Equal("MACHINE", mach.Collation, ignoreCase: true);
        Assert.Equal("ALICE", mach.Min);
        Assert.Equal("EVE", mach.Max);
    }

    // ============================================================ tags that share a key expression

    /// <summary>
    /// Two tags may legally share a KEY expression but differ in collation or ascending/descending.
    /// Per-tag stats are keyed by tag NAME, so neither overwrites the other, and <see cref="StxStatistics.ForTag"/>
    /// returns the SPECIFIC tag's stats (disambiguated by collation / descending) rather than a single
    /// key-expression bucket.
    /// </summary>
    [Fact]
    public void TagsSharingAKeyExpression_DoNotCollide_AndForTagDisambiguates()
    {
        using var local = new VariantTable();
        using var table = DbfTable.Open(local.Dbf);
        using var cdx = CdxFile.Open(local.Cdx, table);

        var stats = HighlikeStatistics.Build(table, cdx);

        // All four tags captured — IDASC/IDDESC share key "ID", NMACH/NGEN share "UPPER(NAME)".
        Assert.Equal(4, stats.Tags.Count);

        var nmach = stats.ForTag(cdx.Tag("NMACH")!)!;
        var ngen = stats.ForTag(cdx.Tag("NGEN")!)!;
        Assert.Equal("MACHINE", nmach.Collation, ignoreCase: true);
        Assert.Equal("GENERAL", ngen.Collation, ignoreCase: true);
        Assert.False(string.Equals(nmach.Collation, ngen.Collation, StringComparison.OrdinalIgnoreCase));

        var idasc = stats.ForTag(cdx.Tag("IDASC")!)!;
        var iddesc = stats.ForTag(cdx.Tag("IDDESC")!)!;
        Assert.False(idasc.Descending);
        Assert.True(iddesc.Descending);

        // The disambiguated stats survive a JSON round-trip (keyed by tag name).
        var back = StxStatistics.FromJson(stats.ToJson())!;
        Assert.Equal(4, back.Tags.Count);
        Assert.True(back.ForTag(cdx.Tag("IDDESC")!)!.Descending);
        Assert.Equal("GENERAL", back.ForTag(cdx.Tag("NGEN")!)!.Collation, ignoreCase: true);
    }

    // ============================================================ staleness: in-place update

    /// <summary>
    /// The secondary (file-write-time) staleness signal: an in-place field UPDATE that changes an
    /// indexed key but neither inserts nor deletes a record leaves the record count — and, when done the
    /// same day, the DATE-ONLY header stamp — UNCHANGED. The STRONG DBF file last-write timestamp still
    /// catches it, so the stale sidecar is detected and ignored. (Closes the "stale-stats undetected"
    /// gap that the date-only stamp alone cannot see.)
    /// </summary>
    [Fact]
    public void Staleness_InPlaceUpdate_SameRecCount_IsDetected()
    {
        using var local = new TinyTable();

        // Build + persist fresh stats.
        using (var table = DbfTable.Open(local.Dbf))
        using (var cdx = CdxFile.Open(local.Cdx, table))
        {
            var fresh = HighlikeStatistics.Analyze(table, cdx);
            Assert.True(fresh.IsFreshFor(table));
            Assert.NotNull(HighlikeStatistics.Load(table, cdx));
        }

        int recCountBefore;
        using (var t = DbfTable.Open(local.Dbf)) recCountBefore = t.RecordCount;

        // In-place field UPDATE: rewrites record 0 but leaves the record COUNT unchanged.
        using (var w = DbfWriter.Open(local.Dbf))
        {
            w.UpdateRecord(0, new object?[] { 1, "Z" });
            w.Flush();
        }

        using (var table = DbfTable.Open(local.Dbf))
        using (var cdx = CdxFile.Open(local.Cdx, table))
        {
            // Precondition: the primary signal (record count) is BLIND to this edit.
            Assert.Equal(recCountBefore, table.RecordCount);

            var stale = StxStatistics.FromJson(File.ReadAllText(HighlikeStatistics.StxPath(table)));
            Assert.NotNull(stale);
            Assert.False(stale!.IsFreshFor(table)); // strong file-write-time signal trips
            Assert.Null(HighlikeStatistics.Load(table, cdx)); // stale → ignored

            // …and lazily rebuilt to match the edited table.
            var rebuilt = HighlikeStatistics.GetOrBuild(table, cdx);
            Assert.True(rebuilt.IsFreshFor(table));
        }
    }

    // ============================================================ shared + local temp tables

    /// <summary>
    /// The shared fixture (read-only): <see cref="RowCount"/> rows, four tags — IDTAG(ID, Integer),
    /// AMTTAG(AMOUNT, Numeric), UNAME(UPPER(NAME)), CODETAG(CODE, nullable Character). A handful of
    /// rows are deleted and every 10th CODE is NULL, so the deleted / null counts are exercised.
    /// TEMP only — deleted on dispose.
    /// </summary>
    public sealed class StxFixture : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 500;
        public int DeletedCount => 3;
        public int NullCodeCount { get; }

        private static readonly string[] Names = { "Alice", "Bob", "cherry", "David", "alice", "eric" };

        public StxFixture()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_stx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "s.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("CODE", 'C', 5, nullable: true),
            };

            int nulls = 0;
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                {
                    int amount = i % 100;                 // 0..99 → NDV 100
                    string name = Names[i % Names.Length];
                    object? code = (i % 10 == 0) ? null : "X";
                    if (code is null) nulls++;
                    w.AppendRecord(new object?[] { i, amount, name, code });
                }

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
                w.CreateTag(new CdxTagDefinition("CODETAG", "CODE"));

                // Delete three records AFTER the tags are built (their index entries remain).
                w.Delete(0);
                w.Delete(1);
                w.Delete(2);
            }

            NullCodeCount = nulls;
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A tiny private temp table (ID Integer + KIND Character) with one ID tag.</summary>
    private sealed class TinyTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        public TinyTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_stx_tiny_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "t.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("KIND", 'C', 2),
            };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= 10; i++)
                    w.AppendRecord(new object?[] { i, "A" });
                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A larger private temp table for the hint-only / lazy-build tests (ID + AMOUNT tags).</summary>
    private sealed class BigTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        public BigTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_stx_big_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "b.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
            };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= 1000; i++)
                    w.AppendRecord(new object?[] { i, i % 100 });
                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// A private temp table exercising DESCENDING and non-MACHINE (GENERAL) tags, plus two pairs of
    /// tags that SHARE a key expression: IDASC/IDDESC over <c>ID</c> (asc vs desc) and NMACH/NGEN over
    /// <c>UPPER(NAME)</c> (MACHINE vs GENERAL).
    /// </summary>
    private sealed class VariantTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 50;

        private static readonly string[] Names = { "Alice", "bob", "Carol", "dave", "Eve" };

        public VariantTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_stx_var_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "v.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NAME", 'C', 20),
            };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                    w.AppendRecord(new object?[] { i, Names[i % Names.Length] });

                w.CreateTag(new CdxTagDefinition("IDASC", "ID"));
                w.CreateTag(new CdxTagDefinition("IDDESC", "ID", descending: true));
                w.CreateTag(new CdxTagDefinition("NMACH", "UPPER(NAME)", collation: "MACHINE"));
                w.CreateTag(new CdxTagDefinition("NGEN", "UPPER(NAME)", collation: "GENERAL"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A private temp table with ZERO data records but a defined ID tag.</summary>
    private sealed class EmptyTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        public EmptyTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_stx_empty_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "e.dbf");

            var cols = new[] { new DbfColumnDef("ID", 'I', 4) };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                // No AppendRecord calls: an empty table. A tag over an empty index is still valid.
                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
