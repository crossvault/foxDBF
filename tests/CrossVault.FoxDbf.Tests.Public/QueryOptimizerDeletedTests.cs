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
/// §3.4 — IMPLICIT <c>SET DELETED</c> CONTRACT for the Rushmore optimizer.
///
/// VFP appends an implicit <c>AND NOT DELETED()</c> to every optimizable query when
/// <c>SET DELETED</c> is ON. <see cref="EvaluationContext.Deleted"/> carries that SET state:
/// <c>true</c> (ON, the library DEFAULT) EXCLUDES deleted rows; <c>false</c> (OFF) lets them
/// participate like any other record.
///
/// The CDX indexes deleted rows too, so the index candidate set can legitimately contain
/// deleted recnos — they must be filtered at the residual confirmation, never leaked into an
/// ON result. The headline INVARIANT every test enforces: under the SAME SET DELETED setting,
/// the optimized (indexed) result EQUALS a full-scan brute force that honours that setting —
/// AND the cdx=null full-scan path agrees with the indexed path.
///
/// SAFETY: each test builds its own TEMP table + CDX via the writer and deletes it on exit.
/// </summary>
public sealed class QueryOptimizerDeletedTests
{
    // ============================================================ infrastructure

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
    /// The ground truth, honouring SET DELETED: evaluate the compiled filter over EVERY physical
    /// record (deleted included, via <see cref="DbfTable.EnumerateAll"/>) in recno order. When
    /// <see cref="EvaluationContext.Deleted"/> is ON (true) the implicit <c>AND NOT DELETED()</c>
    /// skips deleted rows BEFORE the filter; when OFF (false) they participate.
    /// </summary>
    private static List<int> BruteForce(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++; // EnumerateAll(includeDeleted:true) yields all physical records in order.
            if (ctx.Deleted && rec.IsDeleted) continue; // SET DELETED ON: implicit AND NOT DELETED().
            var v = compiled(new DbfRow(rec, recno, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical)
                hits.Add(recno);
        }
        return hits;
    }

    private static EvaluationContext On() => new() { Deleted = true };
    private static EvaluationContext Off() => new() { Deleted = false };

    private static int[] Sorted(IEnumerable<int> xs) => xs.OrderBy(x => x).ToArray();

    /// <summary>
    /// Builds a temp table of <paramref name="rows"/> records (AMOUNT = recno*10) with tags on
    /// ID and AMOUNT, deletes the given 0-based physical indices, and returns the dbf/cdx paths.
    /// </summary>
    private static (string dir, string dbf, string cdx) BuildTable(int rows, params int[] deleteIndices)
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_setdel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbf = Path.Combine(dir, "d.dbf");
        var cols = new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
        };
        using (var w = DbfWriter.Create(dbf, cols))
        {
            for (int i = 1; i <= rows; i++)
                w.AppendRecord(new object?[] { i, i * 10 }); // AMOUNT 10,20,...; amount>100 ⇒ recno>10
            foreach (int idx in deleteIndices)
                w.Delete(idx);
            w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
            w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
        }
        return (dir, dbf, Path.ChangeExtension(dbf, ".cdx"));
    }

    // ============================================================ SET DELETED OFF (include)

    [Fact]
    public void DeletedOff_Indexed_IncludesDeletedMatches_EqualsFullScan()
    {
        // Delete recnos 12 and 15 (indices 11, 14) — both have AMOUNT 120/150 > 100, so under
        // SET DELETED OFF they MUST appear in the result of amount>100.
        var (dir, dbf, cdx) = BuildTable(20, 11, 14);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var ctx = Off();

            var expected = BruteForce(table, "AMOUNT > 100", ctx);
            var result = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100", ctx);

            // The deleted-but-matching rows participate under OFF.
            Assert.Contains(12, expected);
            Assert.Contains(15, expected);
            Assert.Equal(Sorted(expected), Sorted(result.RecordNumbers));
            Assert.Contains(12, result.RecordNumbers);
            Assert.Contains(15, result.RecordNumbers);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void DeletedOff_FullScan_NullCdx_AgreesWithIndexedPath()
    {
        var (dir, dbf, cdx) = BuildTable(20, 11, 14);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var ctx = Off();

            var expected = BruteForce(table, "AMOUNT > 100", ctx);
            var indexed = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100", ctx);
            var fullScan = QueryOptimizer.FindRecords(table, null, "AMOUNT > 100", ctx);

            // Both paths must include the deleted matches and agree with each other + brute force.
            Assert.Equal(Sorted(expected), Sorted(fullScan.RecordNumbers));
            Assert.Equal(Sorted(indexed.RecordNumbers), Sorted(fullScan.RecordNumbers));
            Assert.Contains(12, fullScan.RecordNumbers);
            Assert.Contains(15, fullScan.RecordNumbers);
        }
        finally { TryDelete(dir); }
    }

    // ============================================================ SET DELETED ON (exclude)

    [Fact]
    public void DeletedOn_Indexed_ExcludesDeletedMatches_EqualsFullScan()
    {
        var (dir, dbf, cdx) = BuildTable(20, 11, 14);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var ctx = On();

            var expected = BruteForce(table, "AMOUNT > 100", ctx);
            var result = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100", ctx);

            Assert.Equal(Sorted(expected), Sorted(result.RecordNumbers));
            // The deleted-but-matching rows must NOT leak through under ON.
            Assert.DoesNotContain(12, result.RecordNumbers);
            Assert.DoesNotContain(15, result.RecordNumbers);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void DeletedOn_FullScan_NullCdx_AgreesWithIndexedPath()
    {
        var (dir, dbf, cdx) = BuildTable(20, 11, 14);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var ctx = On();

            var expected = BruteForce(table, "AMOUNT > 100", ctx);
            var indexed = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100", ctx);
            var fullScan = QueryOptimizer.FindRecords(table, null, "AMOUNT > 100", ctx);

            Assert.Equal(Sorted(expected), Sorted(fullScan.RecordNumbers));
            Assert.Equal(Sorted(indexed.RecordNumbers), Sorted(fullScan.RecordNumbers));
            Assert.DoesNotContain(12, fullScan.RecordNumbers);
            Assert.DoesNotContain(15, fullScan.RecordNumbers);
        }
        finally { TryDelete(dir); }
    }

    // ============================================================ default = exclude

    [Fact]
    public void DefaultContext_ExcludesDeleted_LikeSetDeletedOn()
    {
        var (dir, dbf, cdx) = BuildTable(20, 11, 14);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            // No context at all and a blank EvaluationContext must both EXCLUDE deleted
            // (consistent with DbfTable.Records). Compare to the ON ground truth.
            var expected = BruteForce(table, "AMOUNT > 100", On());
            var nullCtx = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100");
            var blankCtx = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100", EvaluationContext.Default);

            Assert.True(EvaluationContext.Default.Deleted, "library default must be SET DELETED ON");
            Assert.Equal(Sorted(expected), Sorted(nullCtx.RecordNumbers));
            Assert.Equal(Sorted(expected), Sorted(blankCtx.RecordNumbers));
            Assert.DoesNotContain(12, nullCtx.RecordNumbers);
            Assert.DoesNotContain(15, nullCtx.RecordNumbers);
        }
        finally { TryDelete(dir); }
    }

    // ============================================================ explicit NOT DELETED()

    [Fact]
    public void ExplicitNotDeletedFilter_StillBehaves_UnderBothSettings()
    {
        var (dir, dbf, cdx) = BuildTable(20, 11, 14);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            const string filter = "AMOUNT > 100 AND NOT DELETED()";
            foreach (var ctx in new[] { On(), Off() })
            {
                var expected = BruteForce(table, filter, ctx);
                var indexed = QueryOptimizer.FindRecords(table, cdxFile, filter, ctx);
                var fullScan = QueryOptimizer.FindRecords(table, null, filter, ctx);

                // The explicit NOT DELETED() conjunct excludes the deleted rows regardless of the
                // SET DELETED state, and both paths agree with brute force.
                Assert.Equal(Sorted(expected), Sorted(indexed.RecordNumbers));
                Assert.Equal(Sorted(expected), Sorted(fullScan.RecordNumbers));
                Assert.DoesNotContain(12, indexed.RecordNumbers);
                Assert.DoesNotContain(15, indexed.RecordNumbers);
            }
        }
        finally { TryDelete(dir); }
    }

    // ============================================================ edge: all-deleted table

    [Fact]
    public void AllDeletedTable_On_IsEmpty_Off_ReturnsMatches()
    {
        // Delete every physical record.
        var (dir, dbf, cdx) = BuildTable(20, Enumerable.Range(0, 20).ToArray());
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            // ON: nothing survives the implicit AND NOT DELETED().
            var on = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100", On());
            Assert.Empty(on.RecordNumbers);
            Assert.Empty(QueryOptimizer.FindRecords(table, null, "AMOUNT > 100", On()).RecordNumbers);

            // OFF: the matching (recnos 11..20) reappear even though every row is deleted.
            var expectedOff = BruteForce(table, "AMOUNT > 100", Off());
            var offIndexed = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT > 100", Off());
            var offFull = QueryOptimizer.FindRecords(table, null, "AMOUNT > 100", Off());

            Assert.NotEmpty(expectedOff);
            Assert.Equal(Sorted(expectedOff), Sorted(offIndexed.RecordNumbers));
            Assert.Equal(Sorted(expectedOff), Sorted(offFull.RecordNumbers));
        }
        finally { TryDelete(dir); }
    }

    // ============================================================ edge: index-reachable deleted

    [Fact]
    public void DeletedReachableViaIndex_DoesNotLeakIntoOnResult()
    {
        // amount>=10 matches every row, so the AMOUNT-index candidate set spans all recnos,
        // INCLUDING the deleted ones (the CDX indexes deleted rows). Under SET DELETED ON the
        // residual must drop them — they must never leak through the index path.
        var (dir, dbf, cdx) = BuildTable(20, 11, 14);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);
            var ctx = On();

            var expected = BruteForce(table, "AMOUNT >= 10", ctx);
            var result = QueryOptimizer.FindRecords(table, cdxFile, "AMOUNT >= 10", ctx);

            Assert.True(result.Optimized, "an indexed AMOUNT range should drive the AMOUNT tag");
            Assert.Equal(Sorted(expected), Sorted(result.RecordNumbers));
            Assert.DoesNotContain(12, result.RecordNumbers);
            Assert.DoesNotContain(15, result.RecordNumbers);
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
