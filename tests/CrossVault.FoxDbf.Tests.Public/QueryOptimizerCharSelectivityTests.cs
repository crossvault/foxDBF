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
/// §D8 — CHARACTER-predicate index resolution: the optimizer must be both CORRECT
/// (its candidate set is always a SUPERSET of the true matches, so the residual
/// reproduces a brute-force full scan EXACTLY) AND SELECTIVE (for a selective character
/// equality it scans a number of records proportional to the match count, not the table
/// size). The historical bug: equality on a CHARACTER tag built a far-too-wide candidate
/// (a first-byte guard + an OPEN-ENDED lower bound), so e.g. <c>NAME = 'NAME0007'</c> on a
/// 4000-row table scanned ~99% of rows — correct but PESSIMAL (slower than a full scan).
///
/// The fix bounds equality by a value-space "next prefix" — keys in
/// <c>[collated(literal), collated(nextPrefix(literal)))</c> — which, because the CDX
/// collation order is order-preserving, is a guaranteed superset of every prefix (EXACT
/// OFF) and exact (EXACT ON) match, for BOTH MACHINE and GENERAL collation.
///
/// CORRECTNESS is hammered against adversarial edges: EXACT OFF prefix, EXACT ON exact, a
/// character range, boundary values, GENERAL case/accent folding, a literal ending in
/// 'Z' / 'z' / 0xFF (bump/carry edge), an EMPTY literal, and a literal LONGER than the
/// field. SELECTIVITY is the new gate: RecordsScanned must be a few × the match count and
/// far below the table size.
///
/// SAFETY: a single temp table + CDX is built ONCE via the writer and deleted on dispose.
/// No committed fixture is touched.
/// </summary>
public sealed class QueryOptimizerCharSelectivityTests
    : IClassFixture<QueryOptimizerCharSelectivityTests.CharTable>
{
    private readonly CharTable _fx;
    public QueryOptimizerCharSelectivityTests(CharTable fx) => _fx = fx;

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

    /// <summary>The ground truth: the compiled filter evaluated on EVERY live record.</summary>
    private static List<int> BruteForce(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        for (int i = 0; i < table.RecordCount; i++)
        {
            var rec = table.GetRecord(i);
            if (rec is null) continue;
            var v = compiled(new DbfRow(rec.Value, i + 1, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical)
                hits.Add(i + 1);
        }
        return hits;
    }

    private (QueryResult result, List<int> expected) Run(string filter, EvaluationContext? ctx = null)
    {
        ctx ??= EvaluationContext.Default;
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var expected = BruteForce(table, filter, ctx);
        var result = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
        return (result, expected);
    }

    private void AssertSuperset(string filter, EvaluationContext? ctx = null)
    {
        var (result, expected) = Run(filter, ctx);
        Assert.Equal(
            expected.OrderBy(x => x).ToArray(),
            result.RecordNumbers.OrderBy(x => x).ToArray());
    }

    private static EvaluationContext Machine(bool exact = false)
        => new() { Collation = VfpCollations.Machine, Exact = exact };

    private static EvaluationContext General(bool exact = false)
        => new() { Collation = VfpCollations.General, Exact = exact };

    // ============================================================ (1) CORRECTNESS — superset == full scan

    [Theory]
    // equality, SET EXACT OFF (prefix semantics) — MACHINE
    [InlineData("NAME = 'NAME0007'")]
    [InlineData("NAME = 'NAME'")]              // prefix of EVERY NAMExxxx row
    // one-sided / range over the character tag
    [InlineData("NAME >= 'NAME0010' AND NAME <= 'NAME0012'")]
    [InlineData("NAME > 'NAME0100'")]
    [InlineData("NAME < 'NAME0005'")]
    // boundary values (first / last distinct key)
    [InlineData("NAME = 'NAME0000'")]
    [InlineData("NAME = 'NAME0199'")]
    // bump / carry edges: last char 'Z' and 'z'
    [InlineData("NAME = 'ZZZ'")]
    [InlineData("NAME = 'zebra'")]
    [InlineData("NAME = 'Zz'")]
    // a literal LONGER than the field (20) — must not throw and must stay a superset
    [InlineData("NAME = 'NAME0007 IS A VERY LONG LITERAL BEYOND TWENTY'")]
    // a no-match probe (empty result on both sides)
    [InlineData("NAME = 'NOPE'")]
    public void CharEquality_And_Range_EqualsBruteForce_Machine(string filter)
        => AssertSuperset(filter, Machine());

    [Theory]
    // SET EXACT ON (exact semantics) must remain a superset of the candidate too
    [InlineData("NAME = 'NAME0007'")]
    [InlineData("NAME == 'NAME0007'")]
    [InlineData("NAME == 'ZZZ'")]
    public void CharEquality_ExactOn_EqualsBruteForce_Machine(string filter)
        => AssertSuperset(filter, Machine(exact: true));

    [Fact]
    public void EmptyLiteral_FallsBackToResidual_ButStaysCorrect()
    {
        // EXACT OFF: '' is a prefix of everything → every row matches. The optimizer must
        // not narrow (it returns the residual) yet still equal a brute-force full scan.
        AssertSuperset("NAME = ''", Machine());
        AssertSuperset("NAME = ''", Machine(exact: true)); // EXACT ON: '' matches the all-blank rows only
    }

    [Fact]
    public void HighByteLiteral_0xFF_BumpOverflow_StaysCorrect()
    {
        // A literal whose last weight byte is 0xFF cannot be incremented: nextPrefix must
        // drop it (and fall back to an open upper bound when nothing can carry) — never
        // narrowing away a true match. 'ÿ' is CP1252 0xFF.
        AssertSuperset("NAME = 'ÿ'", Machine());
        AssertSuperset("NAME = 'Aÿ'", Machine());
    }

    // ---- GENERAL collation: case- and accent-folding must never drop a true match ----

    [Theory]
    [InlineData("NAME = 'muller'")]   // folds to the stored 'Müller' rows
    [InlineData("NAME = 'MÜLLER'")]
    [InlineData("NAME = 'arzte'")]    // folds to 'Ärzte'
    [InlineData("NAME = 'zürich'")]
    [InlineData("NAME = 'NAME0007'")]
    [InlineData("NAME >= 'NAME0010' AND NAME <= 'NAME0012'")]
    public void GeneralCollated_FoldingAndRange_EqualsBruteForce(string filter)
        => AssertSuperset(filter, General());

    [Theory]
    [InlineData("NAME == 'Müller'")]
    [InlineData("NAME = 'müller'")]
    public void GeneralCollated_ExactOn_EqualsBruteForce(string filter)
        => AssertSuperset(filter, General(exact: true));

    // ============================================================ (2) SELECTIVITY — the new gate

    [Fact]
    public void SelectiveCharEquality_Machine_ScansProportionalToMatches_NotTableSize()
    {
        // 'NAME0007' has ~RowCount/200 matches. The candidate window
        // [collated('NAME0007'), collated('NAME0008')) contains ONLY those rows, so the
        // residual touches a handful of records — NOT the ~99% of the table the old
        // open-ended lower bound scanned.
        var (result, expected) = Run("NAME = 'NAME0007'", Machine());

        Assert.Equal(
            expected.OrderBy(x => x).ToArray(),
            result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(result.Optimized, "a character equality on an indexed field must report optimized");
        Assert.NotEmpty(result.RecordNumbers);

        int matchCount = expected.Count;
        Assert.True(result.RecordsScanned <= matchCount * 4,
            $"selective char equality scanned {result.RecordsScanned}, expected <= {matchCount * 4} (~match count)");
        Assert.True(result.RecordsScanned < _fx.RowCount / 10,
            $"selective char equality scanned {result.RecordsScanned}, expected << {_fx.RowCount} (table size)");
    }

    [Fact]
    public void SelectiveCharEquality_General_ScansProportionalToMatches_NotTableSize()
    {
        var (result, expected) = Run("NAME = 'NAME0007'", General());

        Assert.Equal(
            expected.OrderBy(x => x).ToArray(),
            result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(result.Optimized, "a GENERAL-collated character equality must report optimized");
        Assert.NotEmpty(result.RecordNumbers);

        int matchCount = expected.Count;
        Assert.True(result.RecordsScanned <= matchCount * 4,
            $"selective GENERAL char equality scanned {result.RecordsScanned}, expected <= {matchCount * 4}");
        Assert.True(result.RecordsScanned < _fx.RowCount / 10,
            $"selective GENERAL char equality scanned {result.RecordsScanned}, expected << {_fx.RowCount}");
    }

    // ============================================================ shared temp table

    /// <summary>
    /// One temp table whose NAME column is dominated by a SELECTIVE pattern
    /// ("NAME" + (i % 200) → ~RowCount/200 rows per distinct value) plus a handful of
    /// adversarial fixed rows (accented 'Müller'/'Ärzte'/'Zürich' for GENERAL folding and
    /// 'ZZZ'/'zebra'/'Zz' for the bump/carry edges). Two CHARACTER tags index NAME:
    /// NAMETAG (MACHINE) and GNAME (GENERAL). Created via the writer; deleted on dispose.
    /// </summary>
    public sealed class CharTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount { get; }

        // Adversarial fixed rows appended after the selective pattern.
        private static readonly string[] Specials =
        {
            "Müller", "Müller", "Müller",
            "Ärzte",
            "Zürich",
            "ZZZ", "ZZZ",
            "zebra",
            "Zz",
        };

        public CharTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_charsel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "c.dbf");

            const int patternRows = 4000;

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NAME", 'C', 20),
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                int id = 1;
                for (int i = 0; i < patternRows; i++, id++)
                    w.AppendRecord(new object?[] { id, "NAME" + (i % 200).ToString("D4") });
                foreach (var s in Specials)
                    w.AppendRecord(new object?[] { id++, s });

                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
                w.CreateTag(new CdxTagDefinition("GNAME", "NAME", collation: "GENERAL"));
            }

            RowCount = patternRows + Specials.Length;
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
