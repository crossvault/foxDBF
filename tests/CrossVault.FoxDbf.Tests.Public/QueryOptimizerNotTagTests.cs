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
/// §3.4 — NOT-TAG GUARD. VFP ignores any CDX tag whose KEY expression contains a <c>NOT</c>
/// operator. The shared tag-selection helper must SKIP such a tag and report the leaf as
/// RESIDUAL with the dedicated <see cref="PlanReason.KeyExpressionHasNot"/> reason — while
/// FindRecords still returns the correct full-scan result (the tag is simply not driven).
///
/// Word-boundary discipline: identifiers that merely CONTAIN the letters n-o-t (e.g. NOTES,
/// CANNOTS) must NOT be mistaken for the NOT operator — their tags stay fully optimizable.
///
/// The fixture carries a NOT-bearing tag whose KEY a comparison leaf can reproduce textually:
/// <c>IIF(NOT EMPTY(NAME),1,0)</c> (a numeric function key). A plain <c>AMOUNT</c> tag proves
/// the guard does not disturb normal tags.
///
/// SAFETY: one TEMP table + CDX built via the writer, deleted on dispose.
/// </summary>
public sealed class QueryOptimizerNotTagTests : IClassFixture<QueryOptimizerNotTagTests.NotTagTable>
{
    private readonly NotTagTable _fx;
    public QueryOptimizerNotTagTests(NotTagTable fx) => _fx = fx;

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

    private static List<int> BruteForce(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        for (int i = 0; i < table.RecordCount; i++)
        {
            var rec = table.GetRecord(i);
            if (rec is null) continue;
            var v = compiled(new DbfRow(rec.Value, i + 1, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical) hits.Add(i + 1);
        }
        return hits;
    }

    // The filter whose LEFT side textually matches the NOT-bearing tag key IIF(NOT EMPTY(NAME),1,0).
    private const string NotKeyFilter = "IIF(NOT EMPTY(NAME),1,0) = 1";

    // ============================================================ NOT tag → residual w/ reason

    [Fact]
    public void NotKeyTag_LeafReportedResidual_WithKeyExpressionHasNotReason()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var plan = QueryOptimizer.Explain(table, cdx, NotKeyFilter);

        var leaf = Assert.Single(plan.Conditions);
        Assert.False(leaf.Optimizable, "a NOT-bearing tag must not drive the leaf");
        Assert.Equal(PlanReason.KeyExpressionHasNot, leaf.Reason);
        // The tag is ignored, so no tag is reported as used and the overall level is None.
        Assert.Empty(plan.UsedTags);
        Assert.Equal(OptimizationLevel.None, plan.Overall);
    }

    [Fact]
    public void NotKeyTag_ShowPlanToString_ShowsTheNotReason()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        string text = QueryOptimizer.Explain(table, cdx, NotKeyFilter).ToString();

        // The human-readable plan must explain WHY the leaf is residual: the KEY contains NOT.
        Assert.Contains("contains NOT", text);
    }

    [Fact]
    public void NotKeyTag_FindRecords_DoesNotUseTag_ButStaysCorrect()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var expected = BruteForce(table, NotKeyFilter, EvaluationContext.Default);
        var result = QueryOptimizer.FindRecords(table, cdx, NotKeyFilter);

        Assert.False(result.Optimized, "the NOT-bearing tag must not be driven");
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        // Meaningful fixture: some rows match (non-empty NAME) and some do not (empty NAME).
        Assert.NotEmpty(result.RecordNumbers);
        Assert.True(result.RecordNumbers.Count < table.RecordCount, "empty-NAME rows must not match");
    }

    // ============================================================ normal tag unaffected

    [Fact]
    public void NormalTag_OnSameTable_IsStillOptimized()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var plan = QueryOptimizer.Explain(table, cdx, "AMOUNT = 100");

        var leaf = Assert.Single(plan.Conditions);
        Assert.True(leaf.Optimizable, "a normal (no-NOT) numeric tag must still optimize");
        Assert.Equal(PlanReason.Optimized, leaf.Reason);
        Assert.Contains("AMTTAG", plan.UsedTags);
    }

    // ============================================================ word-boundary: NOTES / CANNOTS

    [Theory]
    [InlineData("NOTES = 'ZZ'", "NOTESTAG")]   // NOTES: NOT followed by a letter → not the operator
    [InlineData("CANNOTS = 'YY'", "CANTAG")]   // CANNOTS: NOT preceded by a letter → not the operator
    public void IdentifierContainingNot_IsNotMistakenForTheNotOperator(string filter, string expectedTag)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var plan = QueryOptimizer.Explain(table, cdx, filter);

        var leaf = Assert.Single(plan.Conditions);
        Assert.True(leaf.Optimizable, $"{filter} must remain optimizable; NOT is only a whole-word operator");
        Assert.Equal(PlanReason.Optimized, leaf.Reason);
        Assert.Contains(expectedTag, plan.UsedTags);
    }

    // ============================================================ shared temp table

    /// <summary>
    /// One temp table with NAME (some empty), NOTES and CANNOTS character fields plus tags:
    /// AMTTAG (normal numeric), NOTTAG on <c>IIF(NOT EMPTY(NAME),1,0)</c> (NOT-bearing — must be
    /// ignored), NOTESTAG on NOTES and CANTAG on CANNOTS (NOT-substring identifiers — must stay
    /// optimizable). Built via the writer; deleted on dispose.
    /// </summary>
    public sealed class NotTagTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        public NotTagTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_nottag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "n.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("NOTES", 'C', 20),
                new DbfColumnDef("CANNOTS", 'C', 10),
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= 200; i++)
                {
                    string name = (i % 3 == 0) ? "" : "Name" + i; // every 3rd row has an EMPTY name
                    w.AppendRecord(new object?[] { i, i % 100, name, "ZZ", "YY" });
                }

                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("NOTTAG", "IIF(NOT EMPTY(NAME),1,0)"));
                w.CreateTag(new CdxTagDefinition("NOTESTAG", "NOTES"));
                w.CreateTag(new CdxTagDefinition("CANTAG", "CANNOTS"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
