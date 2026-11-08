using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase 1b (task 1): the SELECT executor's rows must EQUAL an independent brute-force ORACLE
/// (<see cref="SqlTestSupport.RunOracle"/>, a naive full-scan interpreter) across the v1 feature
/// set — '*' vs explicit columns + aliases, WHERE (numeric/char/date, AND/OR/NOT), ORDER BY
/// (ASC/DESC + by ordinal, collation-correct), DISTINCT, TOP n, the aggregates COUNT(*) /
/// COUNT(col) / SUM / AVG / MIN / MAX, GROUP BY + HAVING, the empty result, and all rows.
///
/// These are RED until the executor exists (the <see cref="VfpSession"/> stub throws).
/// SAFETY: a throwaway temp DBF+CDX only — never a committed fixture.
/// </summary>
public sealed class SqlSelectExecutorTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();
    private readonly string _dbf;
    private readonly VfpSession _session;
    private readonly EvaluationContext _oracleCtx;

    public SqlSelectExecutorTests()
    {
        _dbf = _dir.File("person.dbf");
        SqlTestSupport.CreatePersonTable(_dbf);
        _session = new VfpSession();
        _session.OpenDirectory(_dir.Path);
        // The executor evaluates WHERE/HAVING with SQL '=' semantics; mirror that in the oracle.
        _oracleCtx = new EvaluationContext
        {
            Deleted = _session.Context.Deleted,
            Exact = _session.Context.Exact,
            Ansi = _session.Context.Ansi,
            Collation = _session.Context.Collation,
            SqlSemantics = true,
        };
    }

    public void Dispose() { _session.Dispose(); _dir.Dispose(); }

    private void CheckOrdered(string sql) => Check(sql, ordered: true);
    private void CheckUnordered(string sql) => Check(sql, ordered: false);

    private void Check(string sql, bool ordered)
    {
        var stmt = (SelectStatement)SqlParser.Parse(sql);
        using var table = DbfTable.Open(_dbf);
        var expected = SqlTestSupport.RunOracle(table, _oracleCtx, stmt);

        var result = _session.Execute(sql);
        Assert.NotNull(result);
        var actual = SqlTestSupport.Materialize(result!);

        if (ordered) SqlTestSupport.AssertRowsEqualOrdered(expected, actual);
        else SqlTestSupport.AssertRowsEqualUnordered(expected, actual);
    }

    // ---- projection: '*' vs explicit columns + aliases ------------------------------------

    [Theory]
    [InlineData("SELECT * FROM person")]
    [InlineData("SELECT id, name, amount FROM person")]
    [InlineData("SELECT name AS who, amount AS amt FROM person")]
    [InlineData("SELECT id, amount * 2 AS doubled FROM person")]
    [InlineData("SELECT UPPER(name) AS u FROM person")]
    public void Projection_Matches_Oracle(string sql) => CheckOrdered(sql);

    // ---- WHERE: numeric / char / date, AND / OR / NOT -------------------------------------

    [Theory]
    [InlineData("SELECT * FROM person WHERE amount > 200")]
    [InlineData("SELECT * FROM person WHERE amount >= 100 AND amount <= 300")]
    [InlineData("SELECT * FROM person WHERE city = 'Munich'")]
    [InlineData("SELECT * FROM person WHERE city = 'Berlin' OR city = 'Hamburg'")]
    [InlineData("SELECT * FROM person WHERE NOT (city = 'Berlin')")]
    [InlineData("SELECT * FROM person WHERE amount > 200 AND city = 'Munich'")]
    [InlineData("SELECT * FROM person WHERE hired >= {^2005-01-01}")]
    [InlineData("SELECT * FROM person WHERE hired < {^2003-01-01} OR amount > 350")]
    [InlineData("SELECT * FROM person WHERE id = 999")]   // empty result
    public void Where_Matches_Oracle(string sql) => CheckOrdered(sql);

    // ---- ORDER BY ASC/DESC + by ordinal (collation-correct) -------------------------------

    [Theory]
    [InlineData("SELECT * FROM person ORDER BY name")]
    [InlineData("SELECT * FROM person ORDER BY name DESC")]
    [InlineData("SELECT * FROM person ORDER BY amount ASC")]
    [InlineData("SELECT * FROM person ORDER BY city, amount DESC")]
    [InlineData("SELECT name, amount FROM person ORDER BY 2 DESC")]
    [InlineData("SELECT name, amount FROM person ORDER BY 1")]
    public void OrderBy_Matches_Oracle(string sql) => CheckOrdered(sql);

    [Fact]
    public void OrderBy_General_Collation_Matches_Oracle()
    {
        const string sql = "SELECT name FROM person ORDER BY name";
        // GENERAL collation sorts case/accent-insensitively — the oracle uses the same collation.
        var ctx = new EvaluationContext { Collation = VfpCollations.General, SqlSemantics = true };
        using var table = DbfTable.Open(_dbf);
        var expected = SqlTestSupport.RunOracle(table, ctx, (SelectStatement)SqlParser.Parse(sql));

        using var s = new VfpSession(new EvaluationContext { Collation = VfpCollations.General });
        s.OpenDirectory(_dir.Path);
        var actual = SqlTestSupport.Materialize(s.Execute(sql)!);
        SqlTestSupport.AssertRowsEqualOrdered(expected, actual);
    }

    // ---- DISTINCT -------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT DISTINCT city FROM person")]
    [InlineData("SELECT DISTINCT name, amount FROM person")]
    public void Distinct_Matches_Oracle(string sql) => CheckUnordered(sql);

    [Fact]
    public void Distinct_With_OrderBy_Matches_Oracle() => CheckOrdered("SELECT DISTINCT city FROM person ORDER BY city");

    // ---- TOP n ----------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT TOP 3 * FROM person ORDER BY amount DESC")]
    [InlineData("SELECT TOP 1 name, amount FROM person ORDER BY amount")]
    [InlineData("SELECT TOP 100 * FROM person ORDER BY id")]   // more than rows → all
    public void Top_Matches_Oracle(string sql) => CheckOrdered(sql);

    // ---- aggregates (no GROUP BY: whole-table) --------------------------------------------

    [Theory]
    [InlineData("SELECT COUNT(*) FROM person")]
    [InlineData("SELECT COUNT(name) FROM person")]
    [InlineData("SELECT SUM(amount) FROM person")]
    [InlineData("SELECT MIN(amount) FROM person")]
    [InlineData("SELECT MAX(amount) FROM person")]
    [InlineData("SELECT COUNT(*), SUM(amount), MIN(amount), MAX(amount) FROM person")]
    public void Aggregate_WholeTable_Matches_Oracle(string sql) => CheckUnordered(sql);

    [Fact]
    public void Average_UsesVfp9CursorScale()
    {
        // The VFP9 computed-column oracle runs this same fixture: AVG over N(10,2)
        // is stored as N(16,2), not the unrounded decimal returned by the LINQ oracle.
        var result = _session.Execute("SELECT AVG(amount) FROM person")!;
        Assert.Equal(222.22m, Assert.Single(Assert.Single(result.Rows)));
    }

    [Fact]
    public void CompoundAggregate_SumPlusOne_EmitsOneAliasedRow()
    {
        var result = _session.Execute("SELECT SUM(amount) + 1 AS adjusted FROM person")!;
        var rows = SqlTestSupport.Materialize(result);

        Assert.Equal("ADJUSTED", Assert.Single(result.Columns).Name.ToUpperInvariant());
        Assert.Equal(2001m, Assert.Single(Assert.Single(rows)));
    }

    [Fact]
    public void CompoundAggregate_TwoSums_AreEvaluatedOverTheWholeGroup()
    {
        var rows = SqlTestSupport.Materialize(
            _session.Execute("SELECT SUM(amount) + SUM(id) AS combined FROM person")!);

        Assert.Equal(2048m, Assert.Single(Assert.Single(rows)));
    }

    [Fact]
    public void CompoundAggregate_Grouped_IsEvaluatedPerGroup()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT city, SUM(amount) + 1 AS adjusted FROM person GROUP BY city ORDER BY city")!);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new object?[] { "Berlin    ", 501m }, rows[0]);
        Assert.Equal(new object?[] { "Hamburg   ", 651m }, rows[1]);
        Assert.Equal(new object?[] { "Munich    ", 851m }, rows[2]);
    }

    [Fact]
    public void CompoundAggregate_SumOfConditional_IgnoresNullArguments()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT SUM(IIF(active, amount, .NULL.)) + 1 AS adjusted FROM person")!);

        Assert.Equal(851m, Assert.Single(Assert.Single(rows)));
    }

    [Fact]
    public void CompoundAggregate_GroupedAllNullAverage_StaysNull()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT active, AVG(IIF(active, amount, .NULL.)) + 1 AS adjusted " +
            "FROM person GROUP BY active ORDER BY active")!);

        Assert.Equal(2, rows.Count);
        Assert.Equal(false, rows[0][0]);
        Assert.Null(rows[0][1]);
        Assert.Equal(true, rows[1][0]);
        Assert.Equal(171m, rows[1][1]);
    }

    [Fact]
    public void CompoundAggregate_EmptyGroup_PreservesPrimitiveEmptySemantics()
    {
        var sumRows = SqlTestSupport.Materialize(
            _session.Execute("SELECT SUM(amount) + 1 FROM person WHERE id = 99999")!);
        var avgRows = SqlTestSupport.Materialize(
            _session.Execute("SELECT AVG(amount) + 1 FROM person WHERE id = 99999")!);
        var countRows = SqlTestSupport.Materialize(
            _session.Execute("SELECT COUNT(amount) + 1 FROM person WHERE id = 99999")!);

        Assert.Equal(1m, Assert.Single(Assert.Single(sumRows)));
        Assert.Null(Assert.Single(Assert.Single(avgRows)));
        Assert.Equal(1m, Assert.Single(Assert.Single(countRows)));
    }

    [Fact]
    public void CompoundAggregate_Distinct_AppliesAfterGroupedProjection()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT DISTINCT SUM(IIF(active, amount, .NULL.)) + 1 AS adjusted " +
            "FROM person GROUP BY city")!);

        Assert.Equal(new[] { 251m, 301m }, rows.Select(r => Assert.IsType<decimal>(r[0])).OrderBy(x => x));
    }

    [Fact]
    public void ScalarMaxWithTwoArguments_RemainsRowwise()
    {
        var rows = SqlTestSupport.Materialize(
            _session.Execute("SELECT MAX(amount, 100) + 1 AS adjusted FROM person ORDER BY id")!);

        Assert.Equal(new[] { 101m, 201m, 301m, 151m, 251m, 251m, 301m, 101m, 401m },
            rows.Select(r => Assert.IsType<decimal>(r[0])));
    }

    // ---- GROUP BY + HAVING ----------------------------------------------------------------

    [Theory]
    [InlineData("SELECT city, COUNT(*) FROM person GROUP BY city")]
    [InlineData("SELECT city, SUM(amount) FROM person GROUP BY city")]
    [InlineData("SELECT city, COUNT(*) FROM person GROUP BY city HAVING COUNT(*) > 2")]
    [InlineData("SELECT city, SUM(amount) FROM person GROUP BY city HAVING SUM(amount) >= 500")]
    public void GroupBy_Matches_Oracle(string sql) => CheckUnordered(sql);

    [Fact]
    public void GroupBy_With_OrderBy_Matches_Oracle()
        => CheckOrdered("SELECT city, COUNT(*) FROM person GROUP BY city ORDER BY city");

    [Fact]
    public void Having_GroupedSumPlusOne_AppliesTheThresholdToTheCompoundValue()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT city, SUM(amount) + 1 AS adjusted FROM person " +
            "GROUP BY city HAVING SUM(amount) + 1 > 650 ORDER BY city")!);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new object?[] { "Hamburg   ", 651m }, rows[0]);
        Assert.Equal(new object?[] { "Munich    ", 851m }, rows[1]);
    }

    [Fact]
    public void Having_GroupedSumTimesTwo_AppliesTheThresholdToTheCompoundValue()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT city, SUM(amount) * 2 AS doubled FROM person " +
            "GROUP BY city HAVING SUM(amount) * 2 > 1000 ORDER BY city")!);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new object?[] { "Hamburg   ", 1300m }, rows[0]);
        Assert.Equal(new object?[] { "Munich    ", 1700m }, rows[1]);
    }

    [Fact]
    public void Having_GroupedSumPlusCount_CombinesAggregatesBeforeFiltering()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT city, SUM(amount) + COUNT(amount) AS combined FROM person " +
            "GROUP BY city HAVING SUM(amount) + COUNT(amount) > 650 ORDER BY city")!);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new object?[] { "Hamburg   ", 652m }, rows[0]);
        Assert.Equal(new object?[] { "Munich    ", 853m }, rows[1]);
    }

    [Fact]
    public void Having_NestedScalarAroundGroupedSum_IsEvaluatedBeforeFiltering()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT city, ABS(SUM(amount)) AS absoluteTotal FROM person " +
            "GROUP BY city HAVING ABS(SUM(amount)) > 700 ORDER BY city")!);

        Assert.Equal(new object?[] { "Munich    ", 850m }, Assert.Single(rows));
    }

    [Fact]
    public void Having_UngroupedSumPlusOne_EmitsTheSingleAggregateRow()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT SUM(amount) + 1 AS adjusted FROM person HAVING SUM(amount) + 1 > 2000")!);

        Assert.Equal(new object?[] { 2001m }, Assert.Single(rows));
    }

    [Fact]
    public void Having_AllNullAveragePlusOne_ExcludesThatGroup()
    {
        var rows = SqlTestSupport.Materialize(_session.Execute(
            "SELECT active, AVG(IIF(active, amount, .NULL.)) + 1 AS adjusted FROM person " +
            "GROUP BY active HAVING AVG(IIF(active, amount, .NULL.)) + 1 > 0 ORDER BY active")!);

        Assert.Equal(new object?[] { true, 171m }, Assert.Single(rows));
    }

    // ---- ORDER BY by an AGGREGATE column (must read the computed output, not re-evaluate) --

    [Theory]
    [InlineData("SELECT city, COUNT(*) FROM person GROUP BY city ORDER BY COUNT(*)")]
    [InlineData("SELECT city, COUNT(*) FROM person GROUP BY city ORDER BY COUNT(*) DESC")]
    [InlineData("SELECT city, COUNT(*) AS c FROM person GROUP BY city ORDER BY c")]
    [InlineData("SELECT city, COUNT(*) FROM person GROUP BY city ORDER BY 2 DESC")]
    [InlineData("SELECT city, SUM(amount) AS tot FROM person GROUP BY city ORDER BY SUM(amount) DESC")]
    public void OrderBy_Aggregate_Matches_Oracle(string sql) => CheckOrdered(sql);

    [Fact]
    public void OrderBy_Aggregate_Is_Actually_Sorted_By_The_Aggregate()
    {
        // Independent of the oracle: the COUNT column must come out non-decreasing.
        var r = _session.Execute("SELECT city, COUNT(*) FROM person GROUP BY city ORDER BY COUNT(*)")!;
        var counts = SqlTestSupport.Materialize(r).Select(row => Convert.ToInt32(row[1])).ToList();
        for (int i = 1; i < counts.Count; i++) Assert.True(counts[i - 1] <= counts[i]);
    }

    // ---- all rows / empty -----------------------------------------------------------------

    [Fact]
    public void AllRows_Matches_Oracle() => CheckOrdered("SELECT * FROM person");

    [Fact]
    public void EmptyResult_Has_Zero_Rows()
    {
        var result = _session.Execute("SELECT * FROM person WHERE id = 99999");
        Assert.NotNull(result);
        Assert.Empty(SqlTestSupport.Materialize(result!));
    }

    // ---- schema: column names + types -----------------------------------------------------

    [Fact]
    public void Star_Schema_Lists_All_Columns_In_Order()
    {
        var result = _session.Execute("SELECT * FROM person")!;
        Assert.Equal(new[] { "ID", "NAME", "CITY", "AMOUNT", "HIRED", "ACTIVE" },
            result.Columns.Select(c => c.Name.ToUpperInvariant()).ToArray());
    }

    [Fact]
    public void Alias_Becomes_Column_Name()
    {
        var result = _session.Execute("SELECT name AS who, amount AS amt FROM person")!;
        Assert.Equal(new[] { "WHO", "AMT" },
            result.Columns.Select(c => c.Name.ToUpperInvariant()).ToArray());
    }

    // ---- Phase-2 multi-table forms now execute (self-join on the unique ID key) ------------

    [Fact]
    public void SelfJoin_On_Unique_Key_Returns_Each_Surviving_Row_Once()
    {
        // ID is unique per surviving row → an INNER self-join on p.id = q.id pairs each row with
        // itself: one output row per non-deleted person (row 7 is deleted → 9 rows).
        var rows = SqlTestSupport.Materialize(
            _session.Execute("SELECT * FROM person p INNER JOIN person q ON p.id = q.id")!);
        Assert.Equal(9, rows.Count);
        Assert.Equal(12, rows[0].Length); // both sources' 6 columns (ID NAME CITY AMOUNT HIRED ACTIVE)
    }

    [Fact]
    public void CommaMultiTable_With_Where_Is_An_Equi_Join()
    {
        // A comma cross-join filtered by a = b on the unique ID is equivalent to the self-join above.
        var rows = SqlTestSupport.Materialize(
            _session.Execute("SELECT a.id FROM person a, person b WHERE a.id = b.id")!);
        Assert.Equal(9, rows.Count);
    }

    [Fact]
    public void RightJoin_Is_Now_Supported()
    {
        // RIGHT/FULL OUTER JOIN + UNION are implemented (see SqlRightFullUnionExecutorTests). A RIGHT
        // self-join on the unique id is an inner self-join (every q row matches exactly one p row): the
        // 9 surviving rows (row 7 is deleted) each pair with themselves.
        var rows = SqlTestSupport.Materialize(
            _session.Execute("SELECT * FROM person p RIGHT JOIN person q ON p.id = q.id")!);
        Assert.Equal(9, rows.Count);
        Assert.Equal(12, rows[0].Length); // both sources' 6 columns
    }
}
