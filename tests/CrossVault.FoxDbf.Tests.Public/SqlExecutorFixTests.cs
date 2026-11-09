using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Targeted regression tests for the Phase-1b SELECT-executor MUST-FIX items:
/// (1) whole-table aggregate over an EMPTY set still emits one row; (2) projected row CLR types
/// match <see cref="SqlColumn.ClrType"/> on both <c>*</c> and expression paths; (4) HAVING <c>=</c>
/// honours SQL/ANSI semantics; (5) GROUP BY / DISTINCT honour the session collation; (6) TOP n limits
/// tied ORDER BY results; (7) a compound HAVING is rejected (not silently mis-run). SAFETY: temp tables only.
/// </summary>
public sealed class SqlExecutorFixTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private VfpSession PersonSession(EvaluationContext? ctx = null)
    {
        SqlTestSupport.CreatePersonTable(_dir.File("person.dbf"));
        var s = new VfpSession(ctx);
        s.OpenDirectory(_dir.Path);
        return s;
    }

    // ======================================================================================
    //  FIX 1 — aggregate over an EMPTY set returns one row (COUNT 0), never throws
    // ======================================================================================

    [Fact]
    public void CountStar_Over_No_Matching_Rows_Returns_One_Row_With_Zero()
    {
        using var s = PersonSession();
        var rows = SqlTestSupport.Materialize(s.Execute("SELECT COUNT(*) FROM person WHERE id = 99999")!);
        Assert.Single(rows);
        Assert.Equal(0m, SqlTestSupport.Norm(rows[0][0]));
    }

    [Fact]
    public void Aggregates_Over_Empty_Table_Return_One_Row()
    {
        // a freshly created table with ZERO records.
        string dbf = _dir.File("empty.dbf");
        using (DbfWriter.Create(dbf, new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
        }, new DbfCreateOptions { Overwrite = true })) { }

        using var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT COUNT(*), SUM(amount), AVG(amount), MIN(amount), MAX(amount) FROM empty")!);

        Assert.Single(rows);
        Assert.Equal(0m, SqlTestSupport.Norm(rows[0][0])); // COUNT(*) → 0
        Assert.Null(rows[0][1]);                            // SUM → null (SqlBacklogS2Vfp9OracleTests)
        Assert.Null(rows[0][2]);                            // AVG → null
        Assert.Null(rows[0][3]);                            // MIN → null
        Assert.Null(rows[0][4]);                            // MAX → null
    }

    // ======================================================================================
    //  FIX 2 — boxed row value types match SqlColumn.ClrType (I, N(n,0), F, B)
    // ======================================================================================

    private string CreateTypesTable()
    {
        string dbf = _dir.File("types.dbf");
        using var w = DbfWriter.Create(dbf, new[]
        {
            new DbfColumnDef("AI", 'I', 4),       // Integer  → int
            new DbfColumnDef("AN", 'N', 8, 0),    // N(8,0)   → decimal
            new DbfColumnDef("AF", 'F', 10, 2),   // Float    → decimal
            new DbfColumnDef("AB", 'B', 8, 2),    // Double   → double
        }, new DbfCreateOptions { Overwrite = true });
        w.AppendRecord(7, 123m, 4.5m, 6.25);
        w.Flush();
        return dbf;
    }

    [Fact]
    public void Star_Projection_Row_Types_Match_Column_ClrType()
    {
        CreateTypesTable();
        using var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        var r = s.Execute("SELECT * FROM types")!;

        // the schema we expect for '*'.
        Assert.Equal(new[] { typeof(int), typeof(decimal), typeof(decimal), typeof(double) },
            r.Columns.Select(c => c.ClrType).ToArray());

        var row = SqlTestSupport.Materialize(r).Single();
        for (int i = 0; i < r.Columns.Count; i++)
            Assert.Equal(r.Columns[i].ClrType, row[i]!.GetType());
    }

    [Fact]
    public void SimpleField_Projection_Row_Types_Match_Column_ClrType()
    {
        CreateTypesTable();
        using var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        var r = s.Execute("SELECT ai, an, af, ab FROM types")!;
        var row = SqlTestSupport.Materialize(r).Single();
        for (int i = 0; i < r.Columns.Count; i++)
            Assert.Equal(r.Columns[i].ClrType, row[i]!.GetType());
    }

    [Fact]
    public void Aliased_Expression_Projection_Row_Types_Match_Column_ClrType()
    {
        CreateTypesTable();
        using var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        // aliased exprs route through the inference path (a different code path than '*'/simple field).
        var r = s.Execute("SELECT ai AS x, an AS y, af AS z, ab AS w FROM types")!;
        var row = SqlTestSupport.Materialize(r).Single();
        for (int i = 0; i < r.Columns.Count; i++)
            Assert.Equal(r.Columns[i].ClrType, row[i]!.GetType());
        // the integer column specifically must be a CLR int (not boxed decimal).
        Assert.Equal(typeof(int), r.Columns[0].ClrType);
        Assert.IsType<int>(row[0]);
    }

    // ======================================================================================
    //  FIX 4 — HAVING '=' follows SQL/ANSI semantics (same as WHERE), '==' stays exact
    // ======================================================================================

    [Fact]
    public void Having_Eq_AnsiOff_Is_Prefix_Match()
    {
        using var s = PersonSession(new EvaluationContext { Ansi = false });
        // 'Berlin' starts with 'Ber' → under ANSI OFF the Berlin group survives.
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT city, COUNT(*) FROM person GROUP BY city HAVING city = 'Ber'")!);
        Assert.Single(rows);
        Assert.Equal("Berlin", (rows[0][0] as string)!.Trim());
    }

    [Fact]
    public void Having_Eq_AnsiOn_Is_FullLength_Match()
    {
        using var s = PersonSession(new EvaluationContext { Ansi = true });
        // 'Ber' padded to full length never equals 'Berlin' → no group survives.
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT city, COUNT(*) FROM person GROUP BY city HAVING city = 'Ber'")!);
        Assert.Empty(rows);
    }

    [Fact]
    public void Having_DoubleEquals_Is_Exact()
    {
        using var s = PersonSession(new EvaluationContext { Ansi = false });
        // '==' is exact regardless of ANSI: 'Ber' is not exactly 'Berlin'.
        Assert.Empty(SqlTestSupport.Materialize(
            s.Execute("SELECT city, COUNT(*) FROM person GROUP BY city HAVING city == 'Ber'")!));
        Assert.Single(SqlTestSupport.Materialize(
            s.Execute("SELECT city, COUNT(*) FROM person GROUP BY city HAVING city == 'Berlin'")!));
    }

    // ======================================================================================
    //  FIX 5 — GROUP BY key equality + DISTINCT dedup honour the session collation
    // ======================================================================================

    private string CreateCaseVariantTable()
    {
        string dbf = _dir.File("cv.dbf");
        using var w = DbfWriter.Create(dbf, new[]
        {
            new DbfColumnDef("CITY", 'C', 10),
        }, new DbfCreateOptions { Overwrite = true });
        foreach (var c in new[] { "Berlin", "BERLIN", "berlin", "Munich" })
            w.AppendRecord(c);
        w.Flush();
        return dbf;
    }

    [Fact]
    public void GroupBy_And_Distinct_Are_CaseSensitive_Under_Machine()
    {
        CreateCaseVariantTable();
        using var s = new VfpSession(new EvaluationContext { Collation = VfpCollations.Machine });
        s.OpenDirectory(_dir.Path);
        // MACHINE = ordinal: the 3 Berlin casings are distinct → 4 groups / 4 DISTINCT rows.
        Assert.Equal(4, SqlTestSupport.Materialize(
            s.Execute("SELECT city, COUNT(*) FROM cv GROUP BY city")!).Count);
        Assert.Equal(4, SqlTestSupport.Materialize(
            s.Execute("SELECT DISTINCT city FROM cv")!).Count);
    }

    [Fact]
    public void GroupBy_And_Distinct_Are_CaseInsensitive_Under_General()
    {
        CreateCaseVariantTable();
        using var s = new VfpSession(new EvaluationContext { Collation = VfpCollations.General });
        s.OpenDirectory(_dir.Path);
        // GENERAL = case-insensitive: the 3 Berlin casings collapse → 2 groups / 2 DISTINCT rows.
        Assert.Equal(2, SqlTestSupport.Materialize(
            s.Execute("SELECT city, COUNT(*) FROM cv GROUP BY city")!).Count);
        Assert.Equal(2, SqlTestSupport.Materialize(
            s.Execute("SELECT DISTINCT city FROM cv")!).Count);
    }

    // ======================================================================================
    //  FIX 6 — VFP9 TOP n does not extend an ORDER BY boundary tie
    // ======================================================================================

    [Fact]
    public void Top_DoesNotExtend_OrderBy_Ties()
    {
        using var s = PersonSession(); // default SET DELETED ON → 9 live rows
        // live amounts DESC: 400, 300, 300, 250, 250, 200, 150, 100, 50.
        var all = SqlTestSupport.Materialize(s.Execute("SELECT amount FROM person ORDER BY amount DESC")!);
        Assert.Equal(9, all.Count);

        // FinalReviewSqlVfp9OracleTests probes this exact table/query: VFP9 returns 400, 300.
        var top = SqlTestSupport.Materialize(s.Execute("SELECT TOP 2 amount FROM person ORDER BY amount DESC")!);
        Assert.Equal(2, top.Count);
        Assert.Equal(400m, SqlTestSupport.Norm(top[0][0]));
        Assert.Equal(300m, SqlTestSupport.Norm(top[1][0]));
    }

    [Fact]
    public void Top_Without_Boundary_Tie_Returns_Exactly_N()
    {
        using var s = PersonSession();
        // TOP 1 → just 400; the next key (300) does not tie the boundary.
        var top = SqlTestSupport.Materialize(s.Execute("SELECT TOP 1 amount FROM person ORDER BY amount DESC")!);
        Assert.Single(top);
        Assert.Equal(400m, SqlTestSupport.Norm(top[0][0]));
    }

    // ======================================================================================
    //  FIX 7 — a compound HAVING (AND / OR over aggregates) is evaluated as one expression, as VFP9
    // ======================================================================================

    [Theory]
    [InlineData("SELECT city, COUNT(*) FROM person GROUP BY city HAVING COUNT(*) > 2 AND SUM(amount) < 800", "Berlin:4")]
    [InlineData("SELECT city, COUNT(*) FROM person GROUP BY city HAVING COUNT(*) > 3 OR MAX(amount) = 300", "Berlin:4 Munich:3")]
    public void Compound_Having_Is_Evaluated(string sql, string expected)
    {
        using var s = PersonSession();
        var rows = SqlTestSupport.Materialize(s.Execute(sql)!);
        Assert.Equal(expected, string.Join(" ", rows.Select(r =>
            ((string)r[0]!).TrimEnd() + ":" + SqlTestSupport.Norm(r[1])!.ToString())));
    }
}
