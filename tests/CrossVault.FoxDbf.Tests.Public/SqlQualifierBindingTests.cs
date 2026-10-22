using System;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The xBase qualifier <c>alias-&gt;field</c> and the bind-time check of qualifiers that are not a source
/// of the query. Every expectation is the VFP9 behaviour pinned by SqlArrowQualifierVfp9OracleTests
/// (internal suite): the arrow is the dot; an unknown alias is error 13 and an unknown column of a known
/// alias 1806, raised before any row is read; in microVFP another work area's qualifier reads that area's
/// CURRENT record (blank at EOF); the pure SQL session, which has no record pointer, refuses it.
/// <para>Before: <c>zz.id</c> in a WHERE silently matched zero rows, <c>SELECT zz.id FROM person</c>
/// silently returned person.id, and <c>-&gt;</c> did not parse.</para>
/// <para>SAFETY: throwaway temp tables only — never a committed fixture.</para>
/// </summary>
public sealed class SqlQualifierBindingTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public SqlQualifierBindingTests()
    {
        SqlTestSupport.CreatePersonTable(_dir.File("person.dbf"), withIndex: true);
        SqlTestSupport.CreatePersonTable(_dir.File("other.dbf"), withIndex: false);
        using var w = DbfWriter.Create(_dir.File("empt.dbf"),
            new[] { new DbfColumnDef("ID", 'I', 4) }, new DbfCreateOptions { Overwrite = true });
    }

    public void Dispose() => _dir.Dispose();

    private VfpSession Open()
    {
        var s = new VfpSession(new EvaluationContext { Deleted = true });
        s.OpenDirectory(_dir.Path);
        return s;
    }

    private static int[] Ids(VfpSession s, string sql)
        => s.Execute(sql)!.Rows.Select(r => Convert.ToInt32(r[0])).ToArray();

    // ---- alias->field ---------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT id FROM person p WHERE p->id = 3", "SELECT id FROM person p WHERE p.id = 3")]
    [InlineData("SELECT id FROM person WHERE person -> id = 3", "SELECT id FROM person WHERE person.id = 3")]
    [InlineData("SELECT p->id FROM person p WHERE p->city = 'Munich' ORDER BY p->id DESC",
                "SELECT p.id FROM person p WHERE p.city = 'Munich' ORDER BY p.id DESC")]
    [InlineData("SELECT COUNT(*), p->city FROM person p GROUP BY p->city HAVING COUNT(*) > 2 ORDER BY 2",
                "SELECT COUNT(*), p.city FROM person p GROUP BY p.city HAVING COUNT(*) > 2 ORDER BY 2")]
    [InlineData("SELECT p->id FROM person p JOIN other o ON p->id = o->id WHERE o->city = 'Berlin' ORDER BY 1",
                "SELECT p.id FROM person p JOIN other o ON p.id = o.id WHERE o.city = 'Berlin' ORDER BY 1")]
    [InlineData("SELECT id FROM person WHERE id IN (SELECT q->id FROM other q WHERE q->name = 'Brown') ORDER BY 1",
                "SELECT id FROM person WHERE id IN (SELECT q.id FROM other q WHERE q.name = 'Brown') ORDER BY 1")]
    [InlineData("SELECT id FROM person p WHERE EXISTS (SELECT * FROM other q WHERE q->id = p->id AND q->city = 'Hamburg') ORDER BY 1",
                "SELECT id FROM person p WHERE EXISTS (SELECT * FROM other q WHERE q.id = p.id AND q.city = 'Hamburg') ORDER BY 1")]
    public void Arrow_Is_The_Dot(string arrow, string dot)
    {
        using var s = Open();
        var expected = Ids(s, dot);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Ids(s, arrow));
    }

    [Fact]
    public void Arrow_Does_Not_Swallow_Arithmetic()
    {
        using var s = Open();
        Assert.Equal(new[] { 3, 5, 6, 8, 10 }, Ids(s, "SELECT id FROM person WHERE amount - id > 240 ORDER BY 1"));
        Assert.Equal(new[] { 3, 5, 6, 8, 10 }, Ids(s, "SELECT id FROM person WHERE amount-id>240 ORDER BY 1"));
    }

    [Fact]
    public void Arrow_Projection_Is_Named_After_The_Field()
    {
        using var s = Open();
        var r = s.Execute("SELECT p->name, p->city FROM person p WHERE p->id = 1")!;
        Assert.Equal(new[] { "NAME", "CITY" }, r.Columns.Select(c => c.Name.ToUpperInvariant()));
    }

    [Fact]
    public void Arrow_In_Dml()
    {
        using var s = Open();
        Assert.Equal(1, s.Execute("UPDATE person SET amount = 1 WHERE person->id = 3")!.AffectedRecords);
        Assert.Equal(1, s.Execute("UPDATE person SET person->amount = 2 WHERE person -> id = 4")!.AffectedRecords);
        Assert.Equal(1, s.Execute("DELETE FROM person WHERE person->name = 'Jones'")!.AffectedRecords);
        // A bare NULL in SET is the null literal, not an unknown column (VFP9: SqlDmlDbcLongNamesVfp9OracleTests UP_NULL).
        Assert.Equal(1, s.Execute("UPDATE person SET city = NULL WHERE id = 3")!.AffectedRecords);
        Assert.Equal(new[] { 3 }, Ids(s, "SELECT id FROM person WHERE amount = 1"));
        Assert.Empty(Ids(s, "SELECT id FROM person WHERE id = 4"));
    }

    [Theory]
    [InlineData("SELECT id FROM person WHERE name = 'a->b'", 0)]          // literal untouched
    [InlineData("SELECT id FROM person WHERE name = [p->x]", 0)]
    public void Arrow_Inside_Literals_Is_Untouched(string sql, int rows)
    {
        using var s = Open();
        Assert.Equal(rows, Ids(s, sql).Length);
    }

    [Theory]
    [InlineData("a->b", "a.b")]
    [InlineData("a -> b", "a.b")]
    [InlineData("x = a ->b AND c-> d", "x = a.b AND c.d")]
    [InlineData("a - b > c", "a - b > c")]
    [InlineData("a->b->c", "a.b->c")]                // second arrow left for the expression lexer to reject
    [InlineData("m.a->b", "m.a->b")]                 // dotted head is never an arrow alias
    [InlineData("'a->b' + [c->d] + {^2001-01-01}", "'a->b' + [c->d] + {^2001-01-01}")]
    [InlineData("a->b && c->d", "a.b && c->d")]      // && comment untouched
    public void NormalizeArrowQualifiers(string sql, string expected)
        => Assert.Equal(expected, SqlLexer.NormalizeArrowQualifiers(sql));

    // ---- qualifiers that are not a source: loud at bind time ------------------------------------

    [Theory]
    [InlineData("SELECT id FROM person WHERE zz.id = 3", 13)]
    [InlineData("SELECT id FROM person WHERE zz->id = 3", 13)]
    [InlineData("SELECT zz.id FROM person", 13)]                                    // used to return person.id
    [InlineData("SELECT id FROM empt WHERE zz.id = 1", 13)]                         // empty table: still bound
    [InlineData("SELECT id FROM person WHERE .F. AND zz.id = 1", 13)]
    [InlineData("SELECT id FROM person GROUP BY id HAVING zz.id > 0", 13)]
    [InlineData("SELECT p.id FROM person p JOIN other o ON p.id = zz.id", 13)]
    [InlineData("SELECT p.id FROM person p JOIN other o ON p.id = o.id WHERE zz.id = 1", 13)]
    [InlineData("SELECT id FROM person p WHERE p.zz = 3", 1806)]
    [InlineData("SELECT p.id FROM person p JOIN other o ON p.id = o.id WHERE o.zz = 1", 1806)]
    [InlineData("SELECT id FROM person ORDER BY zz.id", 1806)]
    [InlineData("SELECT COUNT(*) FROM person GROUP BY zz.id", 1806)]
    [InlineData("SELECT id FROM person WHERE id = m.nosuch", 1806)]
    [InlineData("SELECT id FROM person p WHERE person.id = 3", 1806)]               // table name, no work area
    [InlineData("UPDATE person SET name = 'X' WHERE zz.id = 1", 13)]
    [InlineData("UPDATE person SET name = zz.name WHERE id = 1", 13)]
    [InlineData("DELETE FROM person WHERE zz.id = 1", 13)]
    [InlineData("DELETE FROM empt WHERE zz->id = 1", 13)]
    [InlineData("UPDATE person SET name = 'X' WHERE nosuch = 1", 1806)]             // bare: read null before
    [InlineData("UPDATE person SET name = nosuch WHERE id = 1", 1806)]
    public void Foreign_Qualifier_Is_A_Bind_Error(string sql, int vfpError)
    {
        using var s = Open();
        var ex = Assert.Throws<FoxDbfSqlException>(() => s.Execute(sql));
        Assert.Equal(vfpError, ex.VfpErrorNumber);
        s.Context.Deleted = false;
        Assert.Equal(10, Ids(s, "SELECT id FROM person").Length);   // nothing was written
    }

    [Fact]
    public void Pure_Session_Refuses_Another_Work_Areas_Current_Record()
    {
        // VFP9 reads the open work area's current record (constant for the query); the pure session keeps
        // no record pointer, so it refuses instead of guessing (decision: SqlQualifiedWhereVfp9OracleTests).
        using var s = Open();
        s.Use("person");
        s.Use("other", inArea: 2);
        var ex = Assert.Throws<FoxDbfSqlException>(() => s.Execute("SELECT id FROM person p WHERE person.id = 3"));
        Assert.Null(ex.VfpErrorNumber);
        Assert.Contains("current record", ex.Message);
        Assert.Throws<FoxDbfSqlException>(() => s.Execute("SELECT id FROM person WHERE id = other->id"));
    }

    // ---- microVFP: the work area's current record, as VFP9 ---------------------------------------

    private (VfpSession S, VfpInterpreter It) Interp()
    {
        var s = Open();
        var it = new VfpInterpreter(s);
        it.Execute("SET DELETED ON\nUSE person IN 0\nUSE other IN 0");
        return (s, it);
    }

    private static int Count(VfpInterpreter it, string sql)
    {
        it.Execute("RELEASE la\n" + sql + " INTO ARRAY la");
        return (int)it.EvalExpression("_TALLY").AsNumber;
    }

    [Fact]
    public void MicroVfp_Table_Name_Under_Local_Alias_Reads_The_Current_Record()
    {
        var (s, it) = Interp();
        using (s)
        {
            it.Execute("SELECT person\nGO 3");
            Assert.Equal(9, Count(it, "SELECT id FROM person p WHERE person.id = 3"));   // constant .T.
            it.Execute("SELECT person\nGO 5");
            Assert.Equal(0, Count(it, "SELECT id FROM person p WHERE person.id = 3"));   // constant .F.
            Assert.Equal(9, Count(it, "SELECT id FROM person p WHERE person->id = 5"));
            it.Execute("SELECT person\nGO 4");
            Assert.Equal(new[] { 5, 6, 8, 9, 10 }, Rows(it, "SELECT id FROM person p WHERE p.id > person.id ORDER BY 1"));
            it.Execute("SELECT other\nGO 2");
            Assert.Equal(new[] { 2 }, Rows(it, "SELECT id FROM person WHERE id = other->id"));
            Assert.Equal(4m, it.EvalExpression("RECNO('person')").AsNumber);   // SQL leaves the pointer alone
        }
    }

    [Fact]
    public void MicroVfp_Eof_Reads_The_Blank_Record()
    {
        var (s, it) = Interp();
        using (s)
        {
            it.Execute("SELECT person\nGO BOTTOM\nSKIP");
            Assert.True(it.EvalExpression("EOF()").AsLogical);
            Assert.Equal(0m, it.EvalExpression("person.id").AsNumber);
            Assert.Equal("", it.EvalExpression("person.name").AsString);
            Assert.Equal(9, Count(it, "SELECT id FROM person p WHERE person.id = 0"));
        }
    }

    [Fact]
    public void MicroVfp_Unknown_Alias_Is_Error_13()
    {
        var (s, it) = Interp();
        using (s)
        {
            Assert.Equal(13, Trap(it, "lnX = EVALUATE('zz->id')"));
            Assert.Equal(13, Trap(it, "lnX = zz.id"));
            Assert.Equal(12, Trap(it, "lnX = person->zz"));
            Assert.Equal(13, Trap(it, "SELECT id FROM person WHERE zz.id = 1 INTO ARRAY la"));
            Assert.Equal(1806, Trap(it, "SELECT id FROM person WHERE id = m.nosuch INTO ARRAY la"));
            it.Execute("lnHit = 3");
            Assert.Equal(0, Trap(it, "SELECT id FROM person WHERE id = m->lnHit INTO ARRAY la"));
            Assert.Equal(1m, it.EvalExpression("_TALLY").AsNumber);
            Assert.NotEqual(0, Trap(it, "lnX = EVALUATE('5->3')"));     // malformed: raises, not .NULL.
        }
    }

    [Fact]
    public void MicroVfp_Arrow_Targets()
    {
        var (s, it) = Interp();
        using (s)
        {
            it.Execute("STORE 7 TO m->q\nm->r = 8\nm -> t = 9");
            Assert.Equal(7m, it.EvalExpression("q").AsNumber);
            Assert.Equal(8m, it.EvalExpression("r").AsNumber);
            Assert.Equal(9m, it.EvalExpression("t").AsNumber);
            it.Execute("USE other AGAIN IN 0 ALIAS oo\nSELECT oo\nLOCATE FOR oo->id = 3\nREPLACE oo->name WITH 'Omega'");
            Assert.Equal(3m, it.EvalExpression("RECNO()").AsNumber);
            Assert.Equal("Omega", it.EvalExpression("oo.name").AsString);
        }
    }

    private static int Trap(VfpInterpreter it, string stmt)
    {
        it.Execute("lnTrapErr = 0\nON ERROR lnTrapErr = ERROR()");
        try { it.Execute(stmt); }
        catch (Exception ex) { return VfpInterpreter.ErrorNumberOf(ex); }
        finally { it.Execute("ON ERROR"); }
        return (int)it.EvalExpression("lnTrapErr").AsNumber;
    }

    private static int[] Rows(VfpInterpreter it, string sql)
    {
        int n = Count(it, sql);
        return Enumerable.Range(1, n).Select(i => (int)it.EvalExpression($"la[{i}, 1]").AsNumber).ToArray();
    }
}
