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

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase A foundation of the Highlike accelerator sub-project (assembly
/// <c>CrossVault.FoxDbf.Highlike</c>). This task is the SCAFFOLD + Core opt-in SEAM +
/// an IDENTICAL baseline — NOT new behavior.
///
/// The NON-NEGOTIABLE INVARIANT under test: with Highlike enabled, a query returns the
/// EXACT same record numbers as the Core <see cref="QueryOptimizer.FindRecords"/> — which in
/// turn equals a brute-force full scan. Statistics are HINTS ONLY (added later): they may only
/// change the plan (speed), never the result set.
///
/// Coverage:
///   - Highlike == Core == full scan for numeric / character / AND / OR / NOT / mixed filters.
///   - The Core opt-in seam: an attached <see cref="IQueryAccelerator"/> routes
///     <see cref="DbfTable.Query"/> through it; with none attached the Core path is identical.
///   - Layering: the Core assembly does NOT reference Highlike (dependency direction Highlike → Core).
///
/// SAFETY: a single temp table + CDX is built once via the writer and deleted on dispose; no
/// committed fixture is touched.
/// </summary>
public sealed class HighlikeEngineTests : IClassFixture<HighlikeEngineTests.HiTable>
{
    private readonly HiTable _fx;
    public HighlikeEngineTests(HiTable fx) => _fx = fx;

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
        // Enumerate ALL physical records (including deleted) so recno = physical record number,
        // exactly as Core's QueryOptimizer does. GetRecord() hides deleted rows, which would make
        // the ground truth dishonest for SET DELETED OFF — so we must NOT use it here.
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            // SET DELETED ON (ctx.Deleted == true): VFP excludes deleted rows via an implicit
            // AND NOT DELETED(). With SET DELETED OFF deleted rows participate like any other.
            if (ctx.Deleted && rec.IsDeleted) continue;
            var v = compiled(new DbfRow(rec, recno, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical)
                hits.Add(recno);
        }
        return hits;
    }

    private static int[] Sorted(IEnumerable<int> xs) => xs.OrderBy(x => x).ToArray();

    // ============================================================ THE invariant: Highlike == Core == full scan

    [Theory]
    // numeric / integer
    [InlineData("ID = 1234")]
    [InlineData("AMOUNT >= 100 AND AMOUNT <= 200")]
    [InlineData("AMOUNT > 990")]
    // character (function key UPPER(NAME))
    [InlineData("UPPER(NAME) = 'ALICE'")]
    // AND across two indexed fields
    [InlineData("AMOUNT >= 100 AND ID <= 50")]
    // OR across two indexed fields
    [InlineData("ID = 5 OR AMOUNT = 999")]
    // NOT over an indexed exact condition
    [InlineData("NOT (AMOUNT = 500)")]
    // mixed: one indexed conjunct + one unindexed residual
    [InlineData("AMOUNT >= 900 AND CATEGORY = 'C0'")]
    // tag-less field → full-scan fallback, still correct
    [InlineData("CATEGORY = 'C1'")]
    public void Highlike_Equals_Core_Equals_FullScan(string filter)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var expected = FullScan(table, filter, EvaluationContext.Default);
        var core = QueryOptimizer.FindRecords(table, cdx, filter);

        var engine = new HighlikeEngine();
        var hi = engine.FindRecords(table, cdx, filter);

        // Highlike == Core == full scan — the result set is never changed by the accelerator.
        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    // ---------------------------------------------------------- invariant across EvaluationContext SHAPE knobs
    // The non-negotiable invariant must hold REGARDLESS of the options the caller provides — the two
    // EvaluationContext knobs that change query SHAPE (not just speed) are SET OPTIMIZE and SET DELETED.

    /// <summary>
    /// SET OPTIMIZE OFF (<c>Optimize = false</c>) must bypass ALL index use and still equal a full
    /// scan: Highlike == Core == deleted-aware full scan, with neither path reporting "optimized".
    /// </summary>
    [Theory]
    [InlineData("ID = 1234")]                       // exact integer tag
    [InlineData("AMOUNT >= 100 AND ID <= 50")]      // AND over two tags
    [InlineData("UPPER(NAME) = 'ALICE'")]           // function/character tag
    public void OptimizeOff_Highlike_Equals_Core_Equals_FullScan(string filter)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var ctx = new EvaluationContext { Optimize = false };

        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine().FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));

        // SET OPTIMIZE OFF → no index consulted on either path (the plan changed, the result did not).
        Assert.False(core.Optimized);
        Assert.False(hi.Optimized);
    }

    /// <summary>
    /// SET DELETED in BOTH positions on a table that actually CONTAINS a deleted row — the branch the
    /// shared 3000-row fixture (zero deleted rows) can never exercise. With <c>Deleted = true</c> the
    /// deleted matching row must be excluded (Core's bitmap path is used); with <c>Deleted = false</c>
    /// it must participate (Core intentionally bypasses the bitmap, see QueryOptimizer.FindRecords).
    /// Highlike == Core == deleted-aware full scan in BOTH cases.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeletedHandling_Highlike_Equals_Core_Equals_FullScan(bool deleted)
    {
        using var del = new DeletedTable();
        using var table = DbfTable.Open(del.Dbf);
        using var cdx = CdxFile.Open(del.Cdx, table);

        // KIND = 'X' matches BOTH a live row and the deleted row, so the two SET DELETED modes
        // genuinely diverge on the result set — exactly what we want to pin down.
        const string filter = "KIND = 'X'";
        var ctx = new EvaluationContext { Deleted = deleted };

        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        var hi = new HighlikeEngine().FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));

        // Sanity: the two modes really do differ on the deleted matching row (otherwise the test
        // would be vacuously green). SET DELETED ON excludes it; SET DELETED OFF includes it.
        Assert.Equal(!deleted, Sorted(expected).Contains(del.DeletedRecNo));
        // And a LIVE matching row is always present, so neither mode is empty.
        Assert.Contains(del.LiveRecNo, Sorted(expected));
    }

    /// <summary>
    /// The documented Phase A stats switch (<c>HighlikeOptions.EnableStatistics = true</c>) must NOT
    /// change the result set: statistics are HINTS ONLY. Highlike (stats ON) == Core == full scan,
    /// across SET OPTIMIZE ON and OFF, on an indexed filter.
    /// </summary>
    [Theory]
    [InlineData("ID = 1234", true)]
    [InlineData("ID = 1234", false)]
    [InlineData("AMOUNT >= 100 AND ID <= 50", true)]
    [InlineData("AMOUNT >= 900 AND CATEGORY = 'C0'", true)]   // indexed conjunct + unindexed residual
    public void EnableStatistics_DoesNotChangeResult(string filter, bool optimize)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var ctx = new EvaluationContext { Optimize = optimize };

        var expected = FullScan(table, filter, ctx);
        var core = QueryOptimizer.FindRecords(table, cdx, filter, ctx);

        var engine = new HighlikeEngine(new HighlikeOptions { EnableStatistics = true });
        Assert.True(engine.Options.EnableStatistics);
        var hi = engine.FindRecords(table, cdx, filter, ctx);

        Assert.Equal(Sorted(expected), Sorted(core.RecordNumbers));
        Assert.Equal(Sorted(core.RecordNumbers), Sorted(hi.RecordNumbers));
        Assert.Equal(Sorted(expected), Sorted(hi.RecordNumbers));
    }

    [Fact]
    public void Highlike_Explain_Matches_Core_OverallLevel()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var core = QueryOptimizer.Explain(table, cdx, "ID = 1234");
        var hi = new HighlikeEngine().Explain(table, cdx, "ID = 1234");

        Assert.Equal(core.Overall, hi.Overall);
        Assert.Equal(core.UsedTags.OrderBy(x => x), hi.UsedTags.OrderBy(x => x));
    }

    // ============================================================ opt-in via the table API

    [Fact]
    public void UseHighlike_EnablesAccelerator_AndQueryEqualsCorePath()
    {
        using var table = DbfTable.Open(_fx.Dbf);

        // Not enabled yet → the Core path runs.
        Assert.Null(table.Accelerator);
        var corePath = table.Query("AMOUNT >= 100 AND ID <= 50");

        // Opt in. UseHighlike attaches a HighlikeEngine and routes Query through it.
        var same = table.UseHighlike();
        Assert.Same(table, same);
        Assert.IsType<HighlikeEngine>(table.Accelerator);

        var hiPath = table.Query("AMOUNT >= 100 AND ID <= 50");

        Assert.Equal(Sorted(corePath.RecordNumbers), Sorted(hiPath.RecordNumbers));
    }

    [Fact]
    public void NotEnabled_TableQuery_IsIdenticalToCoreOptimizer()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var probe = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, probe);

        Assert.Null(table.Accelerator);
        var viaTable = table.Query("UPPER(NAME) = 'ALICE'");
        var viaCore = QueryOptimizer.FindRecords(probe, cdx, "UPPER(NAME) = 'ALICE'");

        Assert.Equal(Sorted(viaCore.RecordNumbers), Sorted(viaTable.RecordNumbers));
    }

    // ============================================================ the Core seam itself (no Highlike needed)

    /// <summary>A spy accelerator proving the Core plug-in point routes through whatever is attached.</summary>
    private sealed class SpyAccelerator : IQueryAccelerator
    {
        public int FindCalls;
        public int ExplainCalls;
        public QueryResult FindRecords(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
        {
            FindCalls++;
            return QueryOptimizer.FindRecords(table, cdx, filter, context);
        }
        public QueryPlan Explain(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
        {
            ExplainCalls++;
            return QueryOptimizer.Explain(table, cdx, filter, context);
        }
    }

    [Fact]
    public void UseAccelerator_RoutesQueryAndExplainThroughTheSeam()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        var spy = new SpyAccelerator();

        Assert.Same(table, table.UseAccelerator(spy));
        Assert.Same(spy, table.Accelerator);

        var q = table.Query("ID = 5");
        var p = table.ExplainQuery("ID = 5", null, accelerator: null); // null arg → attached spy

        Assert.Equal(1, spy.FindCalls);
        Assert.Equal(1, spy.ExplainCalls);
        Assert.Contains(5, q.RecordNumbers);

        // Detach → back to the Core path (the spy is not called again).
        table.UseAccelerator(null);
        Assert.Null(table.Accelerator);
        _ = table.Query("ID = 5");
        Assert.Equal(1, spy.FindCalls);
    }

    [Fact]
    public void ExplicitAcceleratorArgument_OverridesNoAttachment()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        var spy = new SpyAccelerator();

        Assert.Null(table.Accelerator);
        var q = table.Query("ID = 5", context: null, accelerator: spy);

        Assert.Equal(1, spy.FindCalls);
        Assert.Contains(5, q.RecordNumbers);
    }

    // Highlike and Core were merged into a single CrossVault.FoxDbf assembly for NuGet packaging
    // (one package instead of two) — the cross-assembly layering tests that used to live here no
    // longer apply (there's no assembly boundary left to check).

    // ============================================================ shared temp table

    /// <summary>
    /// One temp table (3000 rows) with CDX tags on ID (Integer), AMOUNT (Numeric) and
    /// UPPER(NAME) (function/character key); CATEGORY deliberately UNINDEXED. Built via the
    /// writer; deleted on dispose. TEMP only — no committed fixture is touched.
    /// </summary>
    public sealed class HiTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 3000;

        private static readonly string[] Names = { "Alice", "Bob", "cherry", "David", "alice", "eric" };

        public HiTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_highlike_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "h.dbf");

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
                    int amount = i % 1000;
                    string name = Names[i % Names.Length];
                    string category = "C" + (i % 4);
                    w.AppendRecord(new object?[] { i, amount, name, category });
                }

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// A tiny dedicated temp table that actually CONTAINS a deleted row, with an indexed KIND tag.
    /// Used to cover Core's distinct SET DELETED ON/OFF paths (the shared fixture has zero deleted
    /// rows). Both a LIVE row and the DELETED row match KIND = 'X', so the two SET DELETED modes
    /// produce genuinely different result sets. TEMP only — deleted on dispose.
    /// </summary>
    private sealed class DeletedTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int DeletedRecNo { get; } // 1-based physical recno of the deleted matching row
        public int LiveRecNo { get; }    // 1-based physical recno of a live matching row

        public DeletedTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_highlike_del_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "d.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("KIND", 'C', 2),
            };

            // 5 rows: KIND = 'X' on rows 1 (live) and 3 (will be deleted); 'Y' elsewhere.
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                w.AppendRecord(new object?[] { 1, "X" }); // recno 1, live, matches
                w.AppendRecord(new object?[] { 2, "Y" }); // recno 2, live, no match
                w.AppendRecord(new object?[] { 3, "X" }); // recno 3, will be deleted, matches
                w.AppendRecord(new object?[] { 4, "Y" }); // recno 4, live, no match
                w.AppendRecord(new object?[] { 5, "X" }); // recno 5, live, matches

                w.CreateTag(new CdxTagDefinition("KINDTAG", "KIND"));

                w.Delete(2); // 0-based index 2 → physical recno 3
            }

            DeletedRecNo = 3;
            LiveRecNo = 1;
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
