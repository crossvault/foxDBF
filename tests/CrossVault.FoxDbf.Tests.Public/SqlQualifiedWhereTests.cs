using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Qualified field references (<c>alias.field</c> / <c>table.field</c>) in the WHERE of a
/// SINGLE-table statement. Before the fix the fast WHERE path handed the raw text to
/// <see cref="QueryOptimizer.FindRecords"/>, whose residual row adapter resolves BARE field names
/// only; <c>DbfRecord</c>'s string indexer returns null for the unknown column "p.id" instead of
/// throwing, so every such query silently matched ZERO rows (VFP9 returns the rows — see
/// SqlQualifiedWhereVfp9OracleTests). DELETE / UPDATE ... WHERE table.field had the same defect.
/// <para>
/// Every SELECT is checked against its UNQUALIFIED twin (the known-good reference) AND against
/// explicit expected ids, across indexed / non-indexed tables and SET DELETED ON / OFF.
/// </para>
/// <para>SAFETY: throwaway temp tables only — never a committed fixture.</para>
/// </summary>
public sealed class SqlQualifiedWhereTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _indexed = new();
    private readonly SqlTestSupport.TempDir _plain = new();

    public SqlQualifiedWhereTests()
    {
        SqlTestSupport.CreatePersonTable(_indexed.File("person.dbf"), withIndex: true);
        SqlTestSupport.CreatePersonTable(_plain.File("person.dbf"), withIndex: false);
    }

    public void Dispose() { _indexed.Dispose(); _plain.Dispose(); }

    private string Dir(bool indexed) => indexed ? _indexed.Path : _plain.Path;

    private static VfpSession Open(string dir, bool deleted)
    {
        var s = new VfpSession(new EvaluationContext { Deleted = deleted });
        s.OpenDirectory(dir);
        return s;
    }

    private static List<object?[]> Rows(VfpSession s, string sql) => SqlTestSupport.Materialize(s.Execute(sql)!);

    private static int[] Ids(VfpSession s, string sql)
        => s.Execute(sql)!.Rows.Select(r => Convert.ToInt32(r[0])).ToArray();

    // =========================================================================================
    //  (1) SELECT — qualified WHERE on the fast (FindRecords) path. RED before the fix.
    // =========================================================================================

    public static IEnumerable<object[]> Matrix()
    {
        foreach (bool indexed in new[] { true, false })
            foreach (bool deleted in new[] { true, false })
                yield return new object[] { indexed, deleted };
    }

    /// <summary>(qualified SQL, unqualified twin, expected ids under DELETED ON, under DELETED OFF).</summary>
    private static readonly (string Qualified, string Bare, int[] On, int[] Off)[] Cases =
    {
        // numeric, alias-qualified
        ("SELECT id FROM person p WHERE p.id = 3", "SELECT id FROM person p WHERE id = 3", new[] { 3 }, new[] { 3 }),
        // numeric, table-name-qualified (no local alias)
        ("SELECT id FROM person WHERE person.id = 3", "SELECT id FROM person WHERE id = 3", new[] { 3 }, new[] { 3 }),
        // case-insensitive qualifier
        ("SELECT id FROM person P WHERE p.ID = 3", "SELECT id FROM person P WHERE id = 3", new[] { 3 }, new[] { 3 }),
        // character, alias-qualified
        ("SELECT id FROM person p WHERE p.name = 'Brown' ORDER BY 1",
         "SELECT id FROM person p WHERE name = 'Brown' ORDER BY 1", new[] { 5, 6 }, new[] { 5, 6 }),
        // character, table-name-qualified
        ("SELECT id FROM person WHERE person.city = 'Hamburg' ORDER BY 1",
         "SELECT id FROM person WHERE city = 'Hamburg' ORDER BY 1", new[] { 6, 10 }, new[] { 6, 7, 10 }),
        // the deleted row (#7): SET DELETED decides
        ("SELECT id FROM person p WHERE p.id = 7", "SELECT id FROM person p WHERE id = 7", Array.Empty<int>(), new[] { 7 }),
        // compound, mixed qualified / bare
        ("SELECT id FROM person p WHERE p.city = 'Berlin' AND amount > 100 ORDER BY 1",
         "SELECT id FROM person p WHERE city = 'Berlin' AND amount > 100 ORDER BY 1", new[] { 2, 4 }, new[] { 2, 4 }),
        ("SELECT id FROM person p WHERE p.id = 1 OR p.id = 9 ORDER BY 1",
         "SELECT id FROM person p WHERE id = 1 OR id = 9 ORDER BY 1", new[] { 1, 9 }, new[] { 1, 9 }),
        // single-letter aliases T / F must not mangle the .T. / .F. literals
        ("SELECT id FROM person t WHERE t.id = 3 AND .T.", "SELECT id FROM person t WHERE id = 3 AND .T.", new[] { 3 }, new[] { 3 }),
        ("SELECT id FROM person f WHERE f.id = 3 OR .F.", "SELECT id FROM person f WHERE id = 3 OR .F.", new[] { 3 }, new[] { 3 }),
        // the alias text inside a string literal is left alone
        ("SELECT id FROM person p WHERE p.name = 'p.x' OR p.id = 2", "SELECT id FROM person p WHERE name = 'p.x' OR id = 2",
         new[] { 2 }, new[] { 2 }),
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Qualified_Where_Matches_Unqualified_Twin_And_Expected_Ids(bool indexed, bool deleted)
    {
        using var s = Open(Dir(indexed), deleted);
        foreach (var (qualified, bare, on, off) in Cases)
        {
            var expected = deleted ? on : off;
            Assert.True(expected.SequenceEqual(Ids(s, bare)),
                $"reference query drifted: {bare} → [{string.Join(",", Ids(s, bare))}]");
            var actual = Ids(s, qualified);
            Assert.True(expected.SequenceEqual(actual),
                $"{qualified} (indexed={indexed}, deleted={deleted}) → [{string.Join(",", actual)}], expected [{string.Join(",", expected)}]");
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Qualified_Where_In_Uncorrelated_Subquery_Resolves(bool indexed, bool deleted)
    {
        // An UNCORRELATED subquery runs through a fresh executor with no outer row → the fast path.
        using var s = Open(Dir(indexed), deleted);
        Assert.Equal(new[] { 3, 5, 8 },
            Ids(s, "SELECT id FROM person WHERE id IN (SELECT q.id FROM person q WHERE q.city = 'Munich') ORDER BY 1"));
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Qualified_Where_In_Each_Union_Branch_Resolves(bool indexed, bool deleted)
    {
        using var s = Open(Dir(indexed), deleted);
        Assert.Equal(new[] { 1, 2 },
            Ids(s, "SELECT id FROM person p WHERE p.id = 1 UNION SELECT id FROM person q WHERE q.id = 2 ORDER BY 1"));
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Qualified_Where_GroupBy_Having_OrderBy_Match_Unqualified(bool indexed, bool deleted)
    {
        using var s = Open(Dir(indexed), deleted);
        var qualified = Rows(s,
            "SELECT p.city, COUNT(*) AS n, MAX(p.amount) AS mx FROM person p WHERE p.amount > 100 " +
            "GROUP BY p.city HAVING MAX(p.amount) > 250 ORDER BY p.city");
        var bare = Rows(s,
            "SELECT city, COUNT(*) AS n, MAX(amount) AS mx FROM person p WHERE amount > 100 " +
            "GROUP BY city HAVING MAX(amount) > 250 ORDER BY city");
        Assert.NotEmpty(bare);
        SqlTestSupport.AssertRowsEqualOrdered(bare, qualified);
    }

    [Fact]
    public void Reported_Idxtest_Repro_Returns_The_Row()
    {
        // The originally reported shapes, verbatim, on the committed VFP9-made fixture (read-only).
        using var s = new VfpSession();
        s.OpenDirectory(Fixtures.VfpTestDir);
        int bare = s.Execute("SELECT tid FROM idxtest WHERE tid = 1")!.Rows.Count();
        Assert.True(bare > 0);
        Assert.Equal(bare, s.Execute("SELECT i.tid FROM idxtest i WHERE i.tid = 1")!.Rows.Count());
        Assert.Equal(bare, s.Execute("SELECT tid FROM idxtest WHERE idxtest.tid = 1")!.Rows.Count());
    }

    // =========================================================================================
    //  (2) Regression guards — paths that were ALREADY alias-aware (green before the fix).
    // =========================================================================================

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Qualified_Where_With_Subquery_Atom_RowByRow_Path_Resolves(bool indexed, bool deleted)
    {
        // A WHERE carrying a subquery is a predicate TREE → the row-by-row RowContext path.
        using var s = Open(Dir(indexed), deleted);
        Assert.Equal(new[] { 1, 2, 4, 9 },
            Ids(s, "SELECT id FROM person p WHERE p.city = 'Berlin' AND p.id IN (SELECT id FROM person) ORDER BY 1"));
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Correlated_Subquery_Outer_Reference_Still_Resolves_To_The_Outer_Row(bool indexed, bool deleted)
    {
        using var s = Open(Dir(indexed), deleted);
        // Hamburg: 6 and 10 (and 7 only while DELETED OFF) share a city with another row.
        var expected = deleted
            ? new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10 }
            : new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Equal(expected, Ids(s,
            "SELECT id FROM person p WHERE EXISTS " +
            "(SELECT * FROM person q WHERE q.city = p.city AND q.id <> p.id) ORDER BY 1"));
    }

    [Fact]
    public void MicroVfp_Sql_Select_With_Qualified_Where_Resolves()
    {
        // The interpreter always binds its row as the outer context → the alias-aware row-by-row path.
        using var s = Open(_indexed.Path, deleted: true);
        var interp = new VfpInterpreter(s);
        interp.Execute("SELECT id FROM person p WHERE p.id = 3 INTO CURSOR c1");
        Assert.Equal(1m, interp.EvalExpression("RECCOUNT('c1')").AsNumber);
        interp.Execute("SELECT id FROM person WHERE person.name = 'Brown' INTO CURSOR c2");
        Assert.Equal(2m, interp.EvalExpression("RECCOUNT('c2')").AsNumber);
    }

    // =========================================================================================
    //  (3) Index evidence — the alias-stripped text is Rushmore-indexable, the raw text is not.
    // =========================================================================================

    [Fact]
    public void Stripped_Qualified_Filter_Is_Index_Resolvable_Raw_Text_Is_Not()
    {
        string path = _indexed.File("person.dbf");
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        var ctx = new EvaluationContext { Deleted = true, SqlSemantics = true };

        // The root cause, pinned: the raw qualified text silently matches nothing.
        var raw = QueryOptimizer.FindRecords(table, cdx, "p.id = 3", ctx);
        Assert.Empty(raw.RecordNumbers);

        string stripped = SelectExecutor.StripDrivingQualifier("p.id = 3", "p");
        Assert.Equal("id = 3", stripped);
        var qr = QueryOptimizer.FindRecords(table, cdx, stripped, ctx);
        Assert.True(qr.Optimized, "the alias-stripped single-table filter must be index-resolvable");
        Assert.True(qr.RecordsScanned < table.RecordCount);
        Assert.Equal(new[] { 3 }, qr.RecordNumbers);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Join_Pushdown_With_Single_Letter_Driving_Alias_Keeps_Logical_Literals(bool indexed, bool deleted)
    {
        // The join pushdown strips the driving qualifier from each pushed conjunct; a .T. / .F. literal
        // inside such a conjunct under driving alias T / F must survive intact (it used to become
        // "." → parse failure → FindRecords' empty result → zero rows). Parenthesized so the literal
        // stays in the same conjunct as the field reference (a literal-only conjunct is never pushed).
        using var s = Open(Dir(indexed), deleted);
        Assert.Equal(new[] { 3 }, Ids(s,
            "SELECT t.id FROM person t INNER JOIN person u ON t.id = u.id WHERE (t.id = 3 AND .T.)"));
        Assert.Equal(new[] { 3 }, Ids(s,
            "SELECT f.id FROM person f INNER JOIN person u ON f.id = u.id WHERE (f.id = 3 OR .F.)"));
    }

    [Theory]
    [InlineData("t.id = 3 AND .T.", "t", "id = 3 AND .T.")]
    [InlineData("T.ID = 3 AND .t.", "t", "ID = 3 AND .t.")]
    [InlineData("f.id = 3 OR .F.", "f", "id = 3 OR .F.")]
    [InlineData(".T. AND t.id = 3", "t", ".T. AND id = 3")]
    [InlineData("zz.id = 3", "p", "zz.id = 3")]                       // a non-source alias is left alone
    public void StripDrivingQualifier_Leaves_Logical_Literals_Intact(string text, string alias, string expected)
        => Assert.Equal(expected, SelectExecutor.StripDrivingQualifier(text, alias));

    // =========================================================================================
    //  (4) DML — DELETE / UPDATE ... WHERE table.field. RED before the fix.
    // =========================================================================================

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Delete_With_Table_Qualified_Where_Deletes_The_Row(bool indexed, bool deleted)
    {
        using var s = Open(Dir(indexed), deleted);
        Assert.Equal(1, s.Execute("DELETE FROM person WHERE person.id = 3")!.AffectedRecords);
        Assert.Equal(1, s.Execute("DELETE FROM person WHERE person.name = 'Jones'")!.AffectedRecords);
        s.Context.Deleted = true;
        Assert.Equal(new[] { 1, 2, 5, 6, 8, 9, 10 }, Ids(s, "SELECT id FROM person ORDER BY 1"));
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Update_With_Table_Qualified_Where_Updates_The_Row(bool indexed, bool deleted)
    {
        using var s = Open(Dir(indexed), deleted);
        Assert.Equal(1, s.Execute("UPDATE person SET amount = 1 WHERE person.id = 3")!.AffectedRecords);
        Assert.Equal(2, s.Execute("UPDATE person SET person.amount = 2 WHERE person.name = 'Brown'")!.AffectedRecords);
        Assert.Equal(new[] { 3 }, Ids(s, "SELECT id FROM person WHERE amount = 1"));
        Assert.Equal(new[] { 5, 6 }, Ids(s, "SELECT id FROM person WHERE amount = 2 ORDER BY 1"));
    }

    [Fact]
    public void Update_On_Open_Area_Alias_With_Alias_Qualified_Where_Updates_The_Row()
    {
        using var s = Open(_indexed.Path, deleted: true);
        s.Use("person", alias: "pp");
        Assert.Equal(1, s.Execute("UPDATE pp SET amount = 5 WHERE pp.id = 4")!.AffectedRecords);
        Assert.Equal(1, s.Execute("DELETE FROM pp WHERE pp.id = 9")!.AffectedRecords);
        Assert.Equal(new[] { 4 }, Ids(s, "SELECT id FROM pp WHERE amount = 5"));
        Assert.Empty(Ids(s, "SELECT id FROM pp WHERE id = 9"));
    }
}
