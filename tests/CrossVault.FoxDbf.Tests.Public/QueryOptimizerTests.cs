using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// §D8 — Rushmore-style query OPTIMIZER. The headline INVARIANT every test enforces:
/// the optimized record set MUST equal a brute-force full scan that compiles + evaluates
/// the SAME filter on every non-deleted record. The optimizer is a pure speed-up — it may
/// never change the result.
///
/// EDGE / ADVERSARIAL focus: exact / one-sided / two-sided / BETWEEN ranges, OR over two
/// indexed fields, AND of two indexed conditions, NOT, INLIST, a MIXED indexed+unindexed
/// filter (residual scan path), GENERAL-collated string equality, empty result, all-rows,
/// a contradiction (low &gt; high), a tag-less field falling back to a full scan, and a
/// non-bare-field shape (<c>ID + 1 = k</c>) that must also fall back yet stay correct.
///
/// OPTIMIZATION EVIDENCE: a selective indexed query reports Optimized and scans FAR fewer
/// records than the table; an unindexed filter reports NOT optimized / full scan but still
/// returns the correct set.
///
/// SAFETY: a single temp table + CDX is built ONCE via the writer (DbfWriter.Create +
/// CreateTag) for the whole class and deleted on dispose. No committed fixture is touched.
/// </summary>
public sealed class QueryOptimizerTests : IClassFixture<QueryOptimizerTests.OptimizerTable>
{
    private readonly OptimizerTable _fx;
    public QueryOptimizerTests(OptimizerTable fx) => _fx = fx;

    // ============================================================ infrastructure

    /// <summary>A single DBF record adapted to the engine row contract for brute force.</summary>
    private sealed class DbfRow : IRowContext
    {
        private readonly DbfRecord _rec;
        public DbfRow(DbfRecord rec, int recNo, int recCount) { _rec = rec; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name) => _rec.GetExpressionValue(name);
    }

    /// <summary>
    /// The ground truth: evaluate the compiled filter on EVERY non-deleted record and collect
    /// the 1-based recnos where it is logically true. The optimizer must reproduce this set exactly.
    /// </summary>
    private static List<int> BruteForce(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        for (int i = 0; i < table.RecordCount; i++)
        {
            var rec = table.GetRecord(i);
            if (rec is null) continue; // deleted record: outside the universe.
            var v = compiled(new DbfRow(rec.Value, i + 1, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical)
                hits.Add(i + 1);
        }
        return hits;
    }

    private static int NonDeletedCount(DbfTable table)
    {
        int n = 0;
        for (int i = 0; i < table.RecordCount; i++)
            if (table.GetRecord(i) is not null) n++;
        return n;
    }

    /// <summary>Open the shared table + cdx, run the optimizer, and return (result, expected set).</summary>
    private (QueryResult result, List<int> expected) Run(string filter, EvaluationContext? ctx = null)
    {
        ctx ??= EvaluationContext.Default;
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var expected = BruteForce(table, filter, ctx);
        var result = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        return (result, expected);
    }

    private void AssertEqualsBruteForce(string filter, EvaluationContext? ctx = null)
    {
        var (result, expected) = Run(filter, ctx);
        Assert.Equal(
            expected.OrderBy(x => x).ToArray(),
            result.RecordNumbers.OrderBy(x => x).ToArray());
    }

    // ============================================================ correctness (== full scan)

    [Theory]
    // exact match on an indexed field (integer / numeric)
    [InlineData("ID = 1234")]
    [InlineData("AMOUNT = 500")]
    // one-sided ranges
    [InlineData("AMOUNT >= 100")]
    [InlineData("AMOUNT > 100")]
    [InlineData("AMOUNT < 250")]
    [InlineData("AMOUNT <= 250")]
    [InlineData("ID > 2990")]
    // two-sided range / BETWEEN (two conjuncts on one tag, and the BETWEEN() form)
    [InlineData("AMOUNT >= 100 AND AMOUNT <= 200")]
    [InlineData("BETWEEN(AMOUNT, 100, 200)")]
    // not-equal (set-all minus the matched range)
    [InlineData("AMOUNT <> 500")]
    // OR over two indexed fields
    [InlineData("ID = 5 OR AMOUNT = 999")]
    // AND of two indexed conditions
    [InlineData("AMOUNT >= 100 AND ID <= 50")]
    // NOT over an indexed exact condition
    [InlineData("NOT (AMOUNT = 500)")]
    // INLIST over an indexed field
    [InlineData("INLIST(ID, 1, 2, 3, 4000)")]
    // a function-keyed tag (UPPER(NAME))
    [InlineData("UPPER(NAME) = 'ALICE'")]
    // MIXED: one conjunct indexed, the other unindexed → residual scan path
    [InlineData("AMOUNT >= 100 AND CATEGORY = 'ZZ'")]
    [InlineData("AMOUNT >= 100 AND CATEGORY = 'C0'")]
    // a tag-less field → full-scan fallback, still correct
    [InlineData("CATEGORY = 'C1'")]
    // a non-bare-field shape on an indexed column → must fall back yet stay correct
    [InlineData("ID + 1 = 1235")]
    // empty / all / contradiction
    [InlineData("ID = 999999")]
    [InlineData("ID >= 1")]
    [InlineData("AMOUNT > 500 AND AMOUNT < 100")]
    public void Optimized_Equals_BruteForce(string filter)
        => AssertEqualsBruteForce(filter);

    [Fact]
    public void GeneralCollatedStringEquality_Equals_BruteForce_WithSameCollation()
    {
        // The GNAME tag is GENERAL-collated; equality must honour the tag collation. Compare
        // against a full scan that uses the SAME collation so the only thing under test is that
        // the optimizer never drops a match. 'müller' must match the stored 'Müller' rows.
        var ctx = new EvaluationContext { Collation = VfpCollations.General };
        AssertEqualsBruteForce("NAME = 'müller'", ctx);
    }

    // ============================================================ optimization evidence

    [Fact]
    public void ExactIndexedQuery_IsOptimized_AndScansFarFewerThanTableSize()
    {
        var (result, expected) = Run("ID = 1234");

        Assert.Equal(new[] { 1234 }, result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());

        Assert.True(result.Optimized, "an exact indexed equality must report optimized");
        Assert.True(result.FullyOptimized, "an exact indexed equality needs no non-optimizable residual");
        // The whole point: we touched a tiny fraction of the 3000-row table.
        Assert.True(result.RecordsScanned < 50,
            $"expected far fewer than the table size, scanned {result.RecordsScanned}");
    }

    [Fact]
    public void OneSidedRange_IsOptimized_AndScansOnlyTheMatchingTail()
    {
        // AMOUNT > 990 → amounts 991..999 → ~9/1000 of rows. Indexed range, small scan.
        var (result, expected) = Run("AMOUNT > 990");
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(result.Optimized);
        Assert.True(result.RecordsScanned < _fx.RowCount / 10,
            $"a selective range should scan a small slice, scanned {result.RecordsScanned}");
        Assert.NotEmpty(result.RecordNumbers);
    }

    [Fact]
    public void UnindexedField_IsNotOptimized_FullScans_ButStillCorrect()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        int live = NonDeletedCount(table);

        var expected = BruteForce(table, "CATEGORY = 'C2'", EvaluationContext.Default);
        var result = QueryOptimizer.FindRecords(table, cdx, "CATEGORY = 'C2'");

        Assert.False(result.Optimized, "a filter on an unindexed field cannot be optimized");
        Assert.False(result.FullyOptimized);
        Assert.Equal(live, result.RecordsScanned); // it had to look at every live record
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.NotEmpty(result.RecordNumbers);
    }

    [Fact]
    public void MixedFilter_IsPartlyOptimized_ResidualConfirmsTheUnindexedConjunct()
    {
        // AMOUNT range narrows the candidates (indexed); CATEGORY is the residual (unindexed).
        var (result, expected) = Run("AMOUNT >= 900 AND CATEGORY = 'C0'");
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());

        Assert.True(result.Optimized, "the indexed AMOUNT conjunct should narrow the scan");
        Assert.False(result.FullyOptimized, "the unindexed CATEGORY conjunct leaves a residual");
        // Narrowed by AMOUNT>=900 (~10%), so far fewer than the whole table.
        Assert.True(result.RecordsScanned < _fx.RowCount / 2,
            $"the indexed conjunct should cut the scan, scanned {result.RecordsScanned}");
    }

    // ============================================================ edge cases

    [Fact]
    public void EmptyResult_ScansNothing()
    {
        var (result, expected) = Run("ID = 999999");
        Assert.Empty(expected);
        Assert.Empty(result.RecordNumbers);
        Assert.Equal(0, result.RecordsScanned); // index proved there is nothing to confirm
    }

    [Fact]
    public void Contradiction_LowGreaterThanHigh_IsEmpty_AndScansNothing()
    {
        var (result, expected) = Run("AMOUNT > 500 AND AMOUNT < 100");
        Assert.Empty(expected);
        Assert.Empty(result.RecordNumbers);
        Assert.Equal(0, result.RecordsScanned);
    }

    [Fact]
    public void FilterMatchingAllRows_ReturnsEveryLiveRecord()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        int live = NonDeletedCount(table);

        var result = QueryOptimizer.FindRecords(table, cdx, "ID >= 1");
        var expected = BruteForce(table, "ID >= 1", EvaluationContext.Default);

        Assert.Equal(live, result.RecordNumbers.Count);
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void GetRecords_BridgesMatchedRecnosBackToRecords()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var result = QueryOptimizer.FindRecords(table, cdx, "ID = 1234");
        var recs = result.GetRecords(table).ToList();

        Assert.Single(recs);
        Assert.Equal(1234, Convert.ToInt32(recs[0]["ID"]));
    }

    [Fact]
    public void NullCdx_FallsBackToFullScan_ButStaysCorrect()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        var expected = BruteForce(table, "ID = 1234", EvaluationContext.Default);

        var result = QueryOptimizer.FindRecords(table, null, "ID = 1234");

        Assert.False(result.Optimized, "with no index there is nothing to optimize");
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
    }

    // ============================================================ deleted-record handling

    [Fact]
    public void DeletedRecords_AreExcluded_AndDELETED_Filter_IsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_del_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dbf = Path.Combine(dir, "d.dbf");
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
            };
            using (var w = DbfWriter.Create(dbf, cols))
            {
                for (int i = 1; i <= 500; i++)
                    w.AppendRecord(new object?[] { i, i % 100 });
                // Delete a handful of physical records (0-based indices).
                w.Delete(10);
                w.Delete(11);
                w.Delete(250);
                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
            }
            string cdx = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            // An indexed range must agree with brute force, which skips deleted records.
            var expected = BruteForce(table, "AMOUNT >= 0", EvaluationContext.Default);
            var result = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT >= 0");
            Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
            // The three deleted recnos (11, 12, 251) must NOT appear.
            Assert.DoesNotContain(11, result.RecordNumbers);
            Assert.DoesNotContain(12, result.RecordNumbers);
            Assert.DoesNotContain(251, result.RecordNumbers);

            // DELETED() over the non-deleted universe is always empty.
            var del = QueryOptimizer.FindRecords(table, cdxFile, "DELETED()");
            Assert.Empty(del.RecordNumbers);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ============================================================ partial-index safety (FOR / UNIQUE)

    /// <summary>
    /// A FOR-filtered tag indexes ONLY the rows that passed FOR at build time, so it is a strict
    /// SUBSET of all records. If the optimizer trusted it for a positive equality/range leaf, it
    /// would drop the FOR-false rows that a full scan still returns. Here AMOUNT has ONLY a
    /// FOR AMOUNT&gt;500 tag (no complete AMOUNT tag), so the unguarded optimizer would seek into
    /// that partial index. The guard must skip it and fall back to a full scan.
    ///   - AMOUNT = 600 lives inside the FOR set: index and full scan happen to agree, but the
    ///     guard must not regress it.
    ///   - AMOUNT &lt; 0 (here written as a negative-amount probe) and AMOUNT = 0 live OUTSIDE the
    ///     FOR set: the partial index has NONE of them, so an unguarded optimizer returns empty
    ///     while the full scan returns the real matches → the bug. Must equal brute force.
    /// </summary>
    [Theory]
    [InlineData("AMOUNT = 600")]
    [InlineData("AMOUNT = 0")]
    [InlineData("AMOUNT < 100")]
    public void ForFilteredTag_IsNotTrusted_FallsBackToFullScan(string filter)
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_for_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dbf = Path.Combine(dir, "f.dbf");
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
            };
            using (var w = DbfWriter.Create(dbf, cols))
            {
                for (int i = 1; i <= 1000; i++)
                    w.AppendRecord(new object?[] { i, i % 1000 }); // AMOUNT 0..999, many below 500
                // ONLY a FOR-filtered tag on AMOUNT — no complete AMOUNT index exists.
                w.CreateTag(new CdxTagDefinition("AMTPOS", "AMOUNT", forExpression: "AMOUNT>500"));
            }
            string cdx = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            var expected = BruteForce(table, filter, EvaluationContext.Default);
            var result = QueryOptimizer.FindRecords(table, cdxFile, filter);
            Assert.Equal(
                expected.OrderBy(x => x).ToArray(),
                result.RecordNumbers.OrderBy(x => x).ToArray());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// A UNIQUE tag keeps only ONE entry (lowest recno) per distinct key, so it under-represents
    /// duplicate-key records. Here AMOUNT has ONLY a UNIQUE tag and the data has many duplicates
    /// per AMOUNT value. An unguarded optimizer seeking AMOUNT = k would return just the first
    /// match and drop the rest; a range would likewise return one representative per value. The
    /// guard must skip the unique tag and full-scan instead. Must equal brute force.
    /// </summary>
    [Theory]
    [InlineData("AMOUNT = 5")]        // 100 rows share AMOUNT=5; unique index would yield 1
    [InlineData("AMOUNT >= 3 AND AMOUNT <= 6")]
    public void UniqueTag_IsNotTrusted_FallsBackToFullScan(string filter)
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_uniq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dbf = Path.Combine(dir, "u.dbf");
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
            };
            using (var w = DbfWriter.Create(dbf, cols))
            {
                for (int i = 1; i <= 1000; i++)
                    w.AppendRecord(new object?[] { i, i % 10 }); // AMOUNT 0..9 → 100 dups each
                // ONLY a UNIQUE tag on AMOUNT — keeps just the lowest recno per distinct value.
                w.CreateTag(new CdxTagDefinition("AMTUNQ", "AMOUNT", unique: true));
            }
            string cdx = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            var expected = BruteForce(table, filter, EvaluationContext.Default);
            var result = QueryOptimizer.FindRecords(table, cdxFile, filter);
            // Sanity: duplicates exist, so the correct set is far larger than the distinct-key count.
            Assert.True(expected.Count > 10, "fixture must have duplicate-key rows for this to bite");
            Assert.Equal(
                expected.OrderBy(x => x).ToArray(),
                result.RecordNumbers.OrderBy(x => x).ToArray());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ============================================================ shared temp table

    /// <summary>
    /// Builds ONE temp table (3000 rows) with CDX tags on ID (Integer), AMOUNT (Numeric),
    /// NAME (Character, MACHINE), UPPER(NAME) (function key) and NAME (GENERAL collation),
    /// leaving CATEGORY deliberately UNINDEXED. Created via the writer; deleted on dispose.
    /// </summary>
    public sealed class OptimizerTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 3000;

        // Mixed case + accented names so UPPER() and GENERAL collation paths are exercised.
        private static readonly string[] Names =
            { "Alice", "Bob", "Müller", "cherry", "Ärzte", "David", "alice", "eric" };

        public OptimizerTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "q.dbf");

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
                {
                    int amount = i % 1000;                 // 0..999, many duplicates → range scans
                    string name = Names[i % Names.Length];
                    string category = "C" + (i % 4);       // C0..C3, deliberately unindexed
                    w.AppendRecord(new object?[] { i, amount, name, category });
                }

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
                w.CreateTag(new CdxTagDefinition("GNAME", "NAME", collation: "GENERAL"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
