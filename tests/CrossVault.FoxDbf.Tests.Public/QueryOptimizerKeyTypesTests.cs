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
/// §D8 — Rushmore optimizer coverage for the NON-numeric key types: CHARACTER (MACHINE
/// and GENERAL collation, bare field and <c>UPPER()</c> function key), DATE and DATETIME.
///
/// HEADLINE INVARIANT (every test): the optimized record set MUST equal a brute-force
/// full scan that compiles + evaluates the SAME filter on every non-deleted record under
/// the SAME <see cref="EvaluationContext"/> (SET EXACT + collation). The optimizer is a
/// pure speed-up; it may never add or drop a row.
///
/// OPTIMIZATION EVIDENCE (the tests that are RED until index-resolution is extended to
/// these key types): a selective indexed CHARACTER / DATE / DATETIME leaf must report
/// <see cref="QueryResult.Optimized"/> = true and touch FAR fewer records than the table.
/// Today every non-numeric predicate falls to a full residual scan (Optimized = false,
/// RecordsScanned == live count), so those assertions fail — exactly the gap being closed.
///
/// SEMANTIC EDGES: SET EXACT OFF equality is a PREFIX match; SET EXACT ON / <c>==</c> is an
/// exact match; GENERAL collation folds case + accents so it matches rows MACHINE would not;
/// a tag on <c>UPPER(NAME)</c> resolves <c>UPPER(NAME)='A'</c> but NOT a bare <c>NAME='a'</c>;
/// a contradiction yields the empty set.
///
/// SAFETY: every table is built fresh in TEMP via the writer (DbfWriter.Create + CreateTag)
/// and deleted on dispose. No committed fixture is read or mutated.
/// </summary>
public sealed class QueryOptimizerKeyTypesTests : IClassFixture<QueryOptimizerKeyTypesTests.KeyTypesTable>
{
    private readonly KeyTypesTable _fx;
    public QueryOptimizerKeyTypesTests(KeyTypesTable fx) => _fx = fx;

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

    /// <summary>The ground truth: evaluate the compiled filter on EVERY non-deleted record.</summary>
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

    private void AssertIndexedFindAndCountEqualBruteForce(string filter)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        var expected = BruteForce(table, filter, EvaluationContext.Default);

        var result = QueryOptimizer.FindRecords(table, cdx, filter);
        int count = QueryOptimizer.Count(table, cdx, filter);

        Assert.True(result.Optimized, $"expected indexed execution for: {filter}");
        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.Equal(expected.Count, count);
    }

    private static EvaluationContext MachineExactOn => new() { Exact = true, Collation = VfpCollations.Machine };
    private static EvaluationContext General => new() { Collation = VfpCollations.General };
    private static EvaluationContext GeneralExactOn => new() { Exact = true, Collation = VfpCollations.General };

    // ============================================================ correctness (== full scan)

    [Theory]
    // ---- CHARACTER, MACHINE collation, SET EXACT OFF (the VFP default) → PREFIX match ----
    [InlineData("NAME = 'Bob'")]
    [InlineData("NAME = 'A'")]              // prefix → Alice, Anna (NOT accented Ärzte under MACHINE)
    [InlineData("NAME = 'Z'")]             // prefix matches nothing → empty
    [InlineData("NAME = ''")]              // empty right operand → matches every row (EXACT OFF)
    // ---- CHARACTER, exact via the always-exact == operator ----
    [InlineData("NAME == 'Bob'")]
    [InlineData("NAME == 'Müller'")]
    [InlineData("NAME == 'A'")]            // exact: matches nothing (no row is literally "A")
    // ---- CHARACTER range / one-sided / BETWEEN / IN / not-equal on the CODE tag ----
    [InlineData("CODE >= 'X'")]
    [InlineData("CODE < 'C'")]
    [InlineData("CODE > 'M' AND CODE < 'P'")]
    [InlineData("BETWEEN(CODE, 'B', 'D')")]
    [InlineData("INLIST(CODE, 'A', 'M', 'Z')")]
    [InlineData("CODE <> 'B'")]
    // ---- DATE: equality, one-sided, two-sided range, BETWEEN, not-equal, NOT ----
    [InlineData("DT = {^2020-01-01}")]
    [InlineData("DT > {^2020-03-01}")]
    [InlineData("DT >= {^2020-01-01} AND DT <= {^2020-01-10}")]
    [InlineData("BETWEEN(DT, {^2020-01-05}, {^2020-01-20})")]
    [InlineData("DT <> {^2020-01-01}")]
    [InlineData("NOT (DT = {^2020-01-01})")]
    // ---- DATETIME: one-sided + window range ----
    [InlineData("TS > {^2020-01-01 10:00:00}")]
    [InlineData("TS >= {^2020-01-01 00:10:00} AND TS < {^2020-01-01 00:30:00}")]
    // ---- function key UPPER(NAME): prefix + exact ----
    [InlineData("UPPER(NAME) = 'ALICE'")]
    [InlineData("UPPER(NAME) = 'A'")]      // prefix on the function key → Alice, Anna, ...
    [InlineData("UPPER(NAME) == 'BOB'")]
    // ---- MIXED indexed character/date leaf AND/OR a numeric or unindexed-residual leaf ----
    [InlineData("DT = {^2020-01-01} AND CAT = 'X1'")]      // date indexed, CAT residual
    [InlineData("NAME = 'Bob' OR ID = 5")]                  // character OR numeric
    [InlineData("DT >= {^2020-01-01} AND ID <= 100")]       // date AND numeric
    [InlineData("CODE = 'B' AND DT = {^2020-01-01}")]       // two non-numeric indexed leaves
    // ---- contradictions → empty ----
    [InlineData("DT = {^2020-01-01} AND DT = {^2099-01-01}")]
    [InlineData("CODE > 'Z' AND CODE < 'A'")]
    public void Optimized_Equals_BruteForce_DefaultContext(string filter)
        => AssertEqualsBruteForce(filter);

    [Fact]
    public void CharacterEquality_ExactOn_MachinePrefixBecomesExact_EqualsBruteForce()
    {
        // Under SET EXACT ON, `NAME = 'A'` requires a full (trailing-blank) match, so the
        // prefix rows that EXACT OFF accepts must NOT appear. The optimizer's candidate set
        // must still be a superset; the residual (EXACT ON) trims it to the exact answer.
        AssertEqualsBruteForce("NAME = 'A'", MachineExactOn);   // → empty (no row is exactly "A")
        AssertEqualsBruteForce("NAME = 'Bob'", MachineExactOn); // → the Bob rows
    }

    [Fact]
    public void CharacterEquality_GeneralCollation_FoldsCaseAndAccents_EqualsBruteForce()
    {
        // GENERAL folds case + accents. 'müller' must match the stored 'Müller' rows, and the
        // optimizer must honour the GENERAL-collated tag, never dropping a fold-equal match.
        AssertEqualsBruteForce("NAME = 'müller'", General);
        AssertEqualsBruteForce("NAME == 'müller'", General);
        AssertEqualsBruteForce("NAME = 'müller'", GeneralExactOn);
    }

    [Fact]
    public void CharacterPrefix_GeneralVsMachine_CollationChangesTheMatchSet()
    {
        // MACHINE: 'a' (0x61) is the prefix of NO name (all start upper-case / accented) → empty.
        // GENERAL: 'a' folds to the A-class, so it matches Alice, Anna AND the accented Ärzte.
        // Both must equal their respective brute-force scans, and GENERAL must be the larger set —
        // proof the optimizer threads the active collation through, not just a byte compare.
        var (machine, machineExp) = Run("NAME = 'a'", EvaluationContext.Default);
        var (general, generalExp) = Run("NAME = 'a'", General);

        Assert.Equal(machineExp.OrderBy(x => x), machine.RecordNumbers.OrderBy(x => x));
        Assert.Equal(generalExp.OrderBy(x => x), general.RecordNumbers.OrderBy(x => x));

        Assert.Empty(machine.RecordNumbers);
        Assert.NotEmpty(general.RecordNumbers);
    }

    [Theory]
    [InlineData("TS = {^2020-01-01 5:30:00 PM}")]
    [InlineData("TS <= {^2020-01-01 5:30:00 PM}")]
    [InlineData("BETWEEN(TS, {^2020-01-01 5:00:00 PM}, {^2020-01-01 6:00:00 PM})")]
    [InlineData("TS = {^2020-01-01 12:00:00 AM}")]
    [InlineData("TS = {^2020-01-01 12:00:00 PM}")]
    public void DateTimeAmPm_IndexedFindAndCountEqualBruteForce(string filter)
        => AssertIndexedFindAndCountEqualBruteForce(filter);

    [Theory]
    [InlineData("TS = {^2020-01-01 5:30:00 AM}")]
    [InlineData("TS = {^2020-01-01 17:30:00}")]
    [InlineData("DT = {^2020-01-01}")]
    public void ExistingDateTimeLiteralFormats_IndexedFindAndCountEqualBruteForce(string filter)
        => AssertIndexedFindAndCountEqualBruteForce(filter);

    [Fact]
    public void InvalidDateTimeLiteral_IsSafeAndNotOptimized()
    {
        const string filter = "TS = {^2020-01-01 25:00:00}";
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        var findException = Record.Exception(() => QueryOptimizer.FindRecords(table, cdx, filter));
        var countException = Record.Exception(() => QueryOptimizer.Count(table, cdx, filter));
        var plan = QueryOptimizer.Explain(table, cdx, filter);

        Assert.Null(findException);
        Assert.Null(countException);
        Assert.Equal(OptimizationLevel.None, plan.Overall);
        Assert.Empty(plan.UsedTags);
    }

    // ============================================================ optimization evidence
    //
    // These are the RED tests: today a non-numeric leaf is never index-resolved, so the
    // optimizer reports Optimized = false and scans every live record. Once Character / Date /
    // DateTime resolution lands, each of these must report Optimized = true and a small scan.

    [Fact]
    public void CharacterEquality_IsOptimized_AndScansFarFewerThanTable()
    {
        // Only CODETAG (MACHINE) keys CODE, so the resolution is unambiguous.
        var (result, expected) = Run("CODE = 'B'");

        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.NotEmpty(result.RecordNumbers);
        Assert.True(result.Optimized, "an indexed character equality must report optimized");
        Assert.True(result.RecordsScanned < _fx.RowCount / 2,
            $"a selective character leaf must cut the scan, scanned {result.RecordsScanned} of {_fx.RowCount}");
    }

    [Fact]
    public void CharacterEquality_GeneralCollatedTag_IsOptimized()
    {
        // With a GENERAL evaluation context the optimizer must drive the GENERAL-collated GNAME tag.
        var (result, expected) = Run("NAME = 'Müller'", General);

        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.NotEmpty(result.RecordNumbers);
        Assert.True(result.Optimized, "an indexed GENERAL-collated equality must report optimized");
        Assert.True(result.RecordsScanned < _fx.RowCount,
            $"the GENERAL tag must narrow the scan, scanned {result.RecordsScanned} of {_fx.RowCount}");
    }

    [Fact]
    public void UpperFunctionKey_IsOptimized_AndScansFarFewerThanTable()
    {
        var (result, expected) = Run("UPPER(NAME) = 'ALICE'");

        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.NotEmpty(result.RecordNumbers);
        Assert.True(result.Optimized, "an indexed UPPER(NAME) equality must report optimized");
        Assert.True(result.RecordsScanned < _fx.RowCount / 2,
            $"a selective function-key leaf must cut the scan, scanned {result.RecordsScanned} of {_fx.RowCount}");
    }

    [Fact]
    public void DateEquality_IsOptimized_AndScansOnlyTheMatchingDay()
    {
        var (result, expected) = Run("DT = {^2020-01-01}");

        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.NotEmpty(result.RecordNumbers);
        Assert.True(result.Optimized, "an indexed date equality must report optimized");
        Assert.True(result.RecordsScanned < _fx.RowCount / 10,
            $"a single-day date equality must scan a tiny slice, scanned {result.RecordsScanned} of {_fx.RowCount}");
    }

    [Fact]
    public void DateRange_IsOptimized_AndScansOnlyTheWindow()
    {
        var (result, expected) = Run("DT >= {^2020-01-01} AND DT <= {^2020-01-05}");

        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.NotEmpty(result.RecordNumbers);
        Assert.True(result.Optimized, "an indexed date range must report optimized");
        Assert.True(result.RecordsScanned < _fx.RowCount / 2,
            $"a narrow date window must cut the scan, scanned {result.RecordsScanned} of {_fx.RowCount}");
    }

    [Fact]
    public void DateTimeRange_IsOptimized_AndScansOnlyTheWindow()
    {
        var (result, expected) = Run("TS >= {^2020-01-01 00:10:00} AND TS < {^2020-01-01 00:30:00}");

        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.NotEmpty(result.RecordNumbers);
        Assert.True(result.Optimized, "an indexed datetime range must report optimized");
        Assert.True(result.RecordsScanned < _fx.RowCount / 10,
            $"a 20-minute datetime window must scan a tiny slice, scanned {result.RecordsScanned} of {_fx.RowCount}");
    }

    [Fact]
    public void MixedDateAndUnindexed_IsPartlyOptimized_ResidualConfirmsTheUnindexedConjunct()
    {
        // DT pins a single day (indexed, ~RowCount/100); CAT is the unindexed residual.
        var (result, expected) = Run("DT = {^2020-01-01} AND CAT = 'X1'");

        Assert.Equal(expected.OrderBy(x => x).ToArray(), result.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(result.Optimized, "the indexed DATE conjunct should narrow the scan");
        Assert.True(result.RecordsScanned < _fx.RowCount / 10,
            $"the date conjunct should cut the scan hard, scanned {result.RecordsScanned} of {_fx.RowCount}");
    }

    // ============================================================ semantic edge: UPPER() key only

    /// <summary>
    /// A tag keyed on <c>UPPER(NAME)</c> must resolve a predicate whose LEFT side is exactly
    /// <c>UPPER(NAME)</c>, but must NOT be (mis)matched to a bare <c>NAME</c> predicate — the
    /// optimizer matches the tag KEY expression, not the underlying column. Here the ONLY
    /// character tag is on <c>UPPER(NAME)</c> (no bare NAME tag), so:
    ///   - <c>UPPER(NAME) = 'ALICE'</c> is index-resolved (Optimized = true), while
    ///   - <c>NAME = 'Alice'</c> has no matching tag → full scan (Optimized = false),
    /// and both must still equal brute force.
    /// </summary>
    [Fact]
    public void UpperKeyTag_ResolvesUpperPredicate_ButNotBareFieldPredicate()
    {
        using var t = new SmallTable(w =>
        {
            // Only an UPPER(NAME) tag — deliberately no bare NAME tag.
            w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
        });

        using var table = DbfTable.Open(t.Dbf);
        using var cdx = CdxFile.Open(t.Cdx, table);

        var upExp = BruteForce(table, "UPPER(NAME) = 'ALICE'", EvaluationContext.Default);
        var upRes = QueryOptimizer.FindRecords(table, cdx, "UPPER(NAME) = 'ALICE'");
        Assert.Equal(upExp.OrderBy(x => x).ToArray(), upRes.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.True(upRes.Optimized, "UPPER(NAME)='ALICE' must resolve to the UPPER(NAME) tag");

        var bareExp = BruteForce(table, "NAME = 'Alice'", EvaluationContext.Default);
        var bareRes = QueryOptimizer.FindRecords(table, cdx, "NAME = 'Alice'");
        Assert.Equal(bareExp.OrderBy(x => x).ToArray(), bareRes.RecordNumbers.OrderBy(x => x).ToArray());
        Assert.False(bareRes.Optimized, "a bare NAME predicate must NOT borrow the UPPER(NAME) tag");
    }

    /// <summary>A tiny single-purpose table (10 rows, a few names) built fresh in TEMP.</summary>
    private sealed class SmallTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }

        public SmallTable(Action<DbfWriter> addTags)
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_kt_small_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "s.dbf");
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NAME", 'C', 20),
            };
            string[] names = { "Alice", "Bob", "Cherry", "alice", "David" };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= 10; i++)
                    w.AppendRecord(new object?[] { i, names[i % names.Length] });
                addTags(w);
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ============================================================ shared temp table

    /// <summary>
    /// One temp table (2000 rows) with tags on ID (Integer), NAME (Character MACHINE),
    /// NAME (Character GENERAL), UPPER(NAME) (function key), CODE (Character MACHINE),
    /// DT (Date) and TS (DateTime). CAT is deliberately UNINDEXED (the residual column).
    /// Built via the writer; deleted on dispose.
    /// </summary>
    public sealed class KeyTypesTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 2000;

        // Mixed case + accents so MACHINE vs GENERAL and UPPER() diverge meaningfully.
        private static readonly string[] Names =
            { "Alice", "Bob", "Müller", "Cherry", "Ärzte", "David", "Anna", "Eric" };

        public KeyTypesTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_kt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "kt.dbf");

            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("CODE", 'C', 2),
                new DbfColumnDef("CAT", 'C', 4),
                new DbfColumnDef("DT", 'D', 8),
                new DbfColumnDef("TS", 'T', 8),
            };

            var baseDate = new DateOnly(2020, 1, 1);
            var baseTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                {
                    string name = Names[i % Names.Length];
                    string code = ((char)('A' + (i % 26))).ToString();   // A..Z
                    string cat = "X" + (i % 4);                            // X0..X3 (unindexed)
                    var dt = baseDate.AddDays(i % 100);                    // 100 distinct days
                    var ts = baseTime.AddMinutes(i);                       // all distinct instants
                    w.AppendRecord(new object?[] { i, name, code, cat, dt, ts });
                }

                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
                w.CreateTag(new CdxTagDefinition("GNAME", "NAME", collation: "GENERAL"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
                w.CreateTag(new CdxTagDefinition("CODETAG", "CODE"));
                w.CreateTag(new CdxTagDefinition("DTTAG", "DT"));
                w.CreateTag(new CdxTagDefinition("TSTAG", "TS"));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
