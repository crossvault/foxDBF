using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase-2 MUST-FIX guards for the multi-table / JOIN executor:
/// <list type="number">
/// <item>SET DELETED OFF: the join must KEEP deleted rows (the old <c>GetRecords</c> path silently
/// dropped every deleted physical record, unlike the single-table path) — on BOTH the driving side
/// and the index-accelerated inner side.</item>
/// <item><c>SELECT *</c> over sources that share a column name must produce UNIQUE result-column
/// names (VFP9: <c>NAME_A</c>, <c>NAME_B</c>, …) so the 2nd same-named column stays reachable.</item>
/// <item>A duplicate source alias in FROM/JOIN must be REJECTED (a silently-wrong self-join otherwise).</item>
/// </list>
/// All assertions ride the independent brute-force oracle. SAFETY: throwaway temp tables only.
/// </summary>
public sealed class SqlJoinMustFixTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _indexed = new();
    private readonly SqlTestSupport.TempDir _plain = new();

    public SqlJoinMustFixTests()
    {
        SqlTestSupport.CreateJoinTables(_indexed.Path, withIndex: true);
        SqlTestSupport.CreateJoinTables(_plain.Path, withIndex: false);
    }

    public void Dispose() { _indexed.Dispose(); _plain.Dispose(); }

    private static EvaluationContext Ctx(VfpSession s) => new()
    {
        Deleted = s.Context.Deleted,
        Exact = s.Context.Exact,
        Ansi = s.Context.Ansi,
        Collation = s.Context.Collation,
        SqlSemantics = true,
    };

    private static void DeleteRow(string dir, string table, int index)
    {
        using var w = DbfWriter.Open(Path.Combine(dir, table));
        w.Delete(index);
        w.Flush();
    }

    private void CheckVsOracle(string dir, bool deleted, string sql, bool ordered = false)
    {
        using var s = new VfpSession(new EvaluationContext { Deleted = deleted });
        s.OpenDirectory(dir);

        var stmt = (SelectStatement)SqlParser.Parse(sql);
        var expected = SqlTestSupport.RunJoinOracle(dir, Ctx(s), stmt);
        var actual = SqlTestSupport.Materialize(s.Execute(sql)!);

        if (ordered) SqlTestSupport.AssertRowsEqualOrdered(expected, actual);
        else SqlTestSupport.AssertRowsEqualUnordered(expected, actual);
    }

    // ---- (1) SET DELETED OFF: deleted rows must participate -------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeletedDrivingRow_Honours_SetDeleted_Indexed(bool withIndex)
    {
        // Delete Carol (EMPID 3, deptid 2) on the DRIVING (outer) side.
        string dir = withIndex ? _indexed.Path : _plain.Path;
        DeleteRow(dir, "emp.dbf", index: 2);

        const string sql = "SELECT emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid";

        // SET DELETED OFF → Carol still joins Engineering (executor == oracle, which keeps her).
        CheckVsOracle(dir, deleted: false, sql);
        // SET DELETED ON → Carol is gone (executor == oracle, which drops her).
        CheckVsOracle(dir, deleted: true, sql);
    }

    [Fact]
    public void DeletedDrivingRow_Off_Keeps_Row_On_Is_Gone()
    {
        DeleteRow(_indexed.Path, "emp.dbf", index: 2); // Carol

        bool Has(bool deleted)
        {
            using var s = new VfpSession(new EvaluationContext { Deleted = deleted });
            s.OpenDirectory(_indexed.Path);
            var rows = SqlTestSupport.Materialize(
                s.Execute("SELECT emp.ename FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")!);
            return rows.Any(r => ((string)r[0]!).TrimEnd() == "Carol");
        }

        Assert.True(Has(deleted: false));  // SET DELETED OFF: deleted driver still joins
        Assert.False(Has(deleted: true));  // SET DELETED ON: excluded
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeletedInnerRow_Honours_SetDeleted(bool withIndex)
    {
        // Delete the Engineering dept (DEPTID 2) on the INNER (looked-up) side. With an index this
        // exercises the index-accelerated inner path, which previously dropped the deleted inner row.
        string dir = withIndex ? _indexed.Path : _plain.Path;
        DeleteRow(dir, "dept.dbf", index: 1);

        const string sql = "SELECT emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid";

        CheckVsOracle(dir, deleted: false, sql); // OFF: Carol/Dave still match deleted Engineering
        CheckVsOracle(dir, deleted: true, sql);  // ON: Engineering gone → those rows drop
    }

    [Fact]
    public void DeletedInnerRow_Off_Index_Path_Keeps_Match()
    {
        DeleteRow(_indexed.Path, "dept.dbf", index: 1); // Engineering (DEPTID 2)

        using var s = new VfpSession(new EvaluationContext { Deleted = false });
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")!);

        // Carol & Dave (deptid 2) still resolve to the (deleted) Engineering row under DELETED OFF.
        var eng = rows.Where(r => ((string)r[1]!).TrimEnd() == "Engineering").Select(r => ((string)r[0]!).TrimEnd()).ToList();
        Assert.Contains("Carol", eng);
        Assert.Contains("Dave", eng);
    }

    // ---- (2) SELECT * duplicate column names are suffixed _A/_B & unique -----------------

    [Fact]
    public void SelectStar_DuplicateColumnNames_Are_Suffixed_And_Unique()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var r = s.Execute("SELECT * FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")!;

        // 4 emp columns + 3 dept columns.
        Assert.Equal(7, r.Columns.Count);

        // Every result-column name is unique (case-insensitive) so all stay reachable by name.
        var names = r.Columns.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // DEPTID exists in BOTH sources → VFP9 suffixes the collision in column order (emp first).
        Assert.Contains("DEPTID_A", names);
        Assert.Contains("DEPTID_B", names);
        Assert.True(names.IndexOf("DEPTID_A") < names.IndexOf("DEPTID_B"));
        // Non-colliding names are kept verbatim.
        Assert.Contains(names, n => n.Equals("EMPID", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Equals("DNAME", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectStar_DuplicateColumns_Values_Match_Oracle()
        => CheckVsOracle(_indexed.Path, deleted: true,
            "SELECT * FROM emp INNER JOIN dept ON emp.deptid = dept.deptid");

    // ---- (3) duplicate source alias is rejected ------------------------------------------

    [Fact]
    public void DuplicateAlias_CommaFrom_Is_Rejected()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var ex = Assert.Throws<FoxDbfSqlException>(() =>
            s.Execute("SELECT * FROM emp, emp"));
        Assert.Contains("alias", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateAlias_ExplicitJoin_Is_Rejected()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        Assert.Throws<FoxDbfSqlException>(() =>
            s.Execute("SELECT * FROM emp e JOIN dept e ON e.deptid = e.deptid"));
    }

    [Fact]
    public void DuplicateAlias_DefaultNames_Is_Rejected()
    {
        // emp aliased explicitly to DEPT collides with the dept table's default alias.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        Assert.Throws<FoxDbfSqlException>(() =>
            s.Execute("SELECT * FROM emp dept JOIN dept ON dept.deptid = dept.deptid"));
    }
}
