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

/// <summary>
/// §12.3 — COUNT path CORRECTNESS (the must). A cheap COUNT must return the SAME number the full
/// <see cref="QueryOptimizer.FindRecords"/> would, computed without materializing the recno list /
/// full records.
///
/// HARD GATE (the invariant under test):
///   <c>Count(filter) == FindRecords(filter).RecordNumbers.Count == brute-force full-scan count</c>
/// ALWAYS — for EVERY filter shape (point / equality / range / window / AND / OR / NOT / &lt;&gt; /
/// INLIST / unindexed / character / date), with AND without the Highlike accelerator, under EVERY
/// SET EXACT × SET DELETED combination, and at the boundaries (empty → 0, all-match → the non-deleted
/// or full count). A deleted row is NOT counted under SET DELETED ON and IS counted under OFF.
///
/// This suite drives THREE count surfaces that must all agree:
///   - <see cref="QueryOptimizer.Count"/> (Core),
///   - <see cref="HighlikeEngine.Count"/> (accelerator),
///   - <see cref="DbfTable.Count(string, EvaluationContext?, IQueryAccelerator?)"/> (the bridge).
///
/// SAFETY: every fixture is a TEMP file, deleted on dispose. No committed fixture is touched.
/// </summary>
public sealed class QueryCountCorrectnessTests : IClassFixture<QueryCountCorrectnessTests.CountTable>
{
    private readonly CountTable _fx;
    public QueryCountCorrectnessTests(CountTable fx) => _fx = fx;

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

    /// <summary>
    /// Ground-truth count honouring SET DELETED: compile under <paramref name="ctx"/> and evaluate over
    /// EVERY physical record (deleted included) so recno = physical record number; with SET DELETED ON
    /// the implicit <c>AND NOT DELETED()</c> drops deleted rows before the filter.
    /// </summary>
    private static int BruteForceCount(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        int recno = 0, hits = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            if (ctx.Deleted && rec.IsDeleted) continue;
            var v = compiled(new DbfRow(rec, recno, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical) hits++;
        }
        return hits;
    }

    // ============================================================ (1) the invariant across shapes

    /// <summary>
    /// The headline gate. Across every filter shape × SET EXACT × SET DELETED, the count returned by
    /// the Core optimizer, the Highlike accelerator AND the <see cref="DbfTable"/> bridge each equals
    /// the <see cref="QueryOptimizer.FindRecords"/> record-count AND the brute-force full-scan count.
    /// </summary>
    [Theory]
    // point / equality
    [InlineData("ID = 30")]
    [InlineData("ID = 999")]                         // no such row → 0
    // range / one-sided
    [InlineData("ID >= 40")]
    [InlineData("AMOUNT < 100")]
    // window (low+high fuse on one tag)
    [InlineData("AMOUNT >= 100 AND AMOUNT <= 300")]
    [InlineData("BETWEEN(AMOUNT, 50, 150)")]
    // AND / OR / NOT / <> / INLIST
    [InlineData("ID = 5 OR AMOUNT = 200")]
    [InlineData("NOT (ID = 30)")]
    [InlineData("AMOUNT <> 200")]
    [InlineData("INLIST(ID, 3, 7, 30)")]
    [InlineData("ID >= 10 AND ID <= 50 AND CODE = 'X'")] // mixed: indexed window + residual
    // character (collation-transformed)
    [InlineData("NAME = 'al'")]
    [InlineData("NAME = 'alice'")]
    // date
    [InlineData("DT >= {^2020-01-10}")]
    [InlineData("DT >= {^2020-01-05} AND DT <= {^2020-01-20}")]
    // unindexed → full-scan fallback
    [InlineData("CODE = 'X'")]
    [InlineData("CODE = 'Z'")]                        // unindexed, no match → 0
    public void Count_Equals_FindRecordsCount_Equals_BruteForce_AcrossExactAndDeleted(string filter)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        using var engine = new HighlikeEngine();

        foreach (bool exact in new[] { false, true })
        foreach (bool deleted in new[] { false, true })
        {
            var ctx = new EvaluationContext { Exact = exact, Deleted = deleted };

            int expected = BruteForceCount(table, filter, ctx);
            int viaFind = QueryOptimizer.FindRecords(table, cdx, filter, ctx).RecordNumbers.Count;
            Assert.Equal(expected, viaFind); // sanity: the anchor itself agrees with brute force

            string why = $"filter='{filter}' exact={exact} deleted={deleted}";

            // Core count-only path.
            Assert.Equal(expected, QueryOptimizer.Count(table, cdx, filter, ctx));
            // Highlike accelerator count-only path.
            Assert.Equal(expected, engine.Count(table, cdx, filter, ctx));
            // DbfTable bridge — Core and accelerator-routed.
            Assert.True(expected == table.Count(filter, ctx), why);
            Assert.True(expected == table.Count(filter, ctx, engine), why);
        }
    }

    // ============================================================ (2) boundary cases

    /// <summary>Empty result → exactly 0 (both an unindexed miss and an indexed miss).</summary>
    [Theory]
    [InlineData("ID = 999")]   // indexed, no key matches
    [InlineData("CODE = 'Z'")] // unindexed, no row matches
    public void Count_EmptyResult_IsZero(string filter)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        Assert.Equal(0, QueryOptimizer.Count(table, cdx, filter));
        Assert.Equal(0, table.Count(filter));
    }

    /// <summary>
    /// An always-true filter counts the NON-deleted rows under SET DELETED ON and ALL physical rows
    /// under SET DELETED OFF — the deleted rows (7, 15, 30) are the difference.
    /// </summary>
    [Fact]
    public void Count_AllMatch_IsNonDeletedCount_On_FullCount_Off()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        const string all = "ID >= 1"; // every row qualifies
        int reccount = table.RecordCount; // 60

        var on = new EvaluationContext { Deleted = true };
        var off = new EvaluationContext { Deleted = false };

        Assert.Equal(reccount - 3, QueryOptimizer.Count(table, cdx, all, on)); // 3 deleted excluded
        Assert.Equal(reccount, QueryOptimizer.Count(table, cdx, all, off));    // deleted included

        // And both agree with FindRecords on the same query.
        Assert.Equal(QueryOptimizer.FindRecords(table, cdx, all, on).RecordNumbers.Count,
                     QueryOptimizer.Count(table, cdx, all, on));
        Assert.Equal(QueryOptimizer.FindRecords(table, cdx, all, off).RecordNumbers.Count,
                     QueryOptimizer.Count(table, cdx, all, off));
    }

    /// <summary>
    /// A deleted row that IS an index candidate (recno 30 = ID 30): not counted under SET DELETED ON,
    /// counted under OFF — proving the count honours the per-candidate deletion check, not just the
    /// raw index candidate set.
    /// </summary>
    [Fact]
    public void Count_DeletedCandidate_ExcludedOn_IncludedOff()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        Assert.Equal(0, QueryOptimizer.Count(table, cdx, "ID = 30", new EvaluationContext { Deleted = true }));
        Assert.Equal(1, QueryOptimizer.Count(table, cdx, "ID = 30", new EvaluationContext { Deleted = false }));
    }

    /// <summary>SET OPTIMIZE OFF (no index consulted) must still produce the identical count.</summary>
    [Fact]
    public void Count_SetOptimizeOff_StillEqualsFindRecords()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var ctx = new EvaluationContext { Optimize = false };

        foreach (var f in new[] { "ID = 30", "AMOUNT >= 100 AND AMOUNT <= 300", "NAME = 'al'" })
            Assert.Equal(QueryOptimizer.FindRecords(table, cdx, f, ctx).RecordNumbers.Count,
                         QueryOptimizer.Count(table, cdx, f, ctx));
    }

    /// <summary>A null CDX (no index at all) still counts correctly via the full-scan fallback.</summary>
    [Fact]
    public void Count_NullCdx_FullScanFallback_EqualsFindRecords()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        foreach (var f in new[] { "ID = 30", "AMOUNT >= 100 AND AMOUNT <= 300", "CODE = 'X'" })
            Assert.Equal(QueryOptimizer.FindRecords(table, null, f).RecordNumbers.Count,
                         QueryOptimizer.Count(table, null, f));
    }

    // ============================================================ (3) NOT over a NULL/undecodable key

    /// <summary>
    /// REGRESSION (the must-fix): a NOT over an EXACT positive leaf produces an exact *complement*
    /// candidate (<c>Bitmap.Not()</c> flips every bit in <c>[0, recordCount)</c>), so it INCLUDES the
    /// recno whose Integer key is NULL/undecodable (absent from the positive set, hence present after
    /// Not()). Under VFP three-valued logic that row evaluates to .NULL. (<c>NOT (.NULL. = 3)</c> is
    /// .NULL., not .T.), so <see cref="QueryOptimizer.FindRecords"/> — which ALWAYS runs the residual —
    /// EXCLUDES it. Count's no-read fast path must do the same: it may NOT trust a complemented
    /// candidate. With table {1,2,3,NULL,5} on an Integer tag, <c>NOT (ID = 3)</c>, SET DELETED ON the
    /// answer is 3 (not 4). Verified across the NOT shapes (=, range, BETWEEN, INLIST) and both
    /// SET DELETED states, against FindRecords AND the brute force.
    /// </summary>
    [Theory]
    [InlineData("NOT (ID = 3)")]
    [InlineData("NOT (ID >= 3)")]
    [InlineData("NOT (ID <= 3)")]
    [InlineData("NOT BETWEEN(ID, 2, 4)")]
    [InlineData("NOT INLIST(ID, 2, 3, 5)")]
    public void Count_NotOverNullKey_MatchesFindRecords_NotComplementFastPath(string filter)
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_count_null_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbf = Path.Combine(dir, "nul.dbf");
        try
        {
            var cols = new[] { new DbfColumnDef("ID", 'I', 4, nullable: true) };
            using (var w = DbfWriter.Create(dbf, cols))
            {
                w.AppendRecord(new object?[] { 1 });
                w.AppendRecord(new object?[] { 2 });
                w.AppendRecord(new object?[] { 3 });
                w.AppendRecord(new object?[] { null }); // NULL Integer key → undecodable, absent from any positive set
                w.AppendRecord(new object?[] { 5 });
                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
            }
            string cdxPath = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdx = CdxFile.Open(cdxPath, table);
            using var engine = new HighlikeEngine();

            foreach (bool deleted in new[] { true, false }) // SET DELETED ON engages the candidate fast path
            {
                var ctx = new EvaluationContext { Deleted = deleted };
                string why = $"filter='{filter}' deleted={deleted}";

                int expected = BruteForceCount(table, filter, ctx);
                int viaFind = QueryOptimizer.FindRecords(table, cdx, filter, ctx).RecordNumbers.Count;
                Assert.True(expected == viaFind, "anchor disagrees: " + why);

                Assert.True(expected == QueryOptimizer.Count(table, cdx, filter, ctx), "Core: " + why);
                Assert.True(expected == engine.Count(table, cdx, filter, ctx), "Highlike: " + why);
                Assert.True(expected == table.Count(filter, ctx), "bridge: " + why);
                Assert.True(expected == table.Count(filter, ctx, engine), "bridge+engine: " + why);
            }

            // The specific number the must-fix calls out: NOT (ID = 3) over {1,2,3,NULL,5}, DELETED ON → 3.
            if (filter == "NOT (ID = 3)")
                Assert.Equal(3, QueryOptimizer.Count(table, cdx, filter, new EvaluationContext { Deleted = true }));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ============================================================ fixture

    /// <summary>
    /// A 60-row temp table: ID (Integer, tag), AMOUNT (Numeric, tag, duplicates), NAME (Character
    /// MACHINE, tag), DT (Date, tag), CODE (Character, UNINDEXED). Recnos 7, 15 and 30 are deleted
    /// (recno 30 = ID 30 is a deleted indexable candidate). Built once; deleted on dispose.
    /// </summary>
    public sealed class CountTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        public CountTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_count_corr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "cnt.dbf");
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("DT", 'D', 8),
                new DbfColumnDef("CODE", 'C', 4),
            };
            var epoch = new DateTime(2020, 1, 1);
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= 60; i++)
                {
                    int amount = (i % 30) * 10;                   // 0..290, duplicates
                    string name = (i % 2 == 0) ? "alice" : "bob"; // 'al' prefix matches even rows
                    DateTime dt = epoch.AddDays(i);
                    string code = (i % 5 == 0) ? "X" : "Y";       // unindexed
                    w.AppendRecord(new object?[] { i, amount, name, dt, code });
                }
                w.Delete(6);   // recno 7
                w.Delete(14);  // recno 15
                w.Delete(29);  // recno 30 (ID = 30) — a deleted indexable candidate

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
                w.CreateTag(new CdxTagDefinition("DTTAG", "DT"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
