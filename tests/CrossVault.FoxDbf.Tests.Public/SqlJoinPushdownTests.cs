using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Coverage for the WHERE pushdown on the DRIVING source (src[0]) of a join chain — the change that
/// keeps a selective WHERE from paying one inner-side lookup per driving row.
/// <para>
/// Three layers, because they prove DIFFERENT things:
/// </para>
/// <list type="number">
/// <item>pure helper tests (the string/AST logic that is easy to get subtly wrong);</item>
/// <item>wiring tests through the <c>PushdownFilters</c> seam (the pushdown actually RAN — a
/// result-equivalence test alone would pass even if pushdown were a no-op);</item>
/// <item>differential tests against the independent brute-force oracle, across indexed /
/// non-indexed and SET DELETED ON / OFF (nothing regressed).</item>
/// </list>
/// <para>SAFETY: throwaway temp tables only — never a committed fixture.</para>
/// </summary>
public sealed class SqlJoinPushdownTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _indexed = new();
    private readonly SqlTestSupport.TempDir _plain = new();

    public SqlJoinPushdownTests()
    {
        SqlTestSupport.CreateJoinTables(_indexed.Path, withIndex: true);
        SqlTestSupport.CreateJoinTables(_plain.Path, withIndex: false);
        CreateFacturasClientes(_indexed.Path, withIndex: true);
        CreateFacturasClientes(_plain.Path, withIndex: false);
    }

    public void Dispose() { _indexed.Dispose(); _plain.Dispose(); }

    // ---- fixture: a driving table with a CHARACTER key, mirroring the reported workload --------

    private static readonly (string IdFactura, int ClienteId, decimal Importe)[] Facturas =
    {
        ("F0000001", 1, 100.50m),
        ("F0000002", 1, 200.00m),
        ("F0000003", 2, 300.25m),
        ("F0000004", 2, 400.00m),
        ("F0000005", 3, 500.75m),
        ("F0000006", 99, 600.00m),   // orphan cliente → LEFT JOIN yields NULL inner fields
        ("F0000007", 3, 700.00m),
        ("F0000008", 1, 800.00m),
    };

    private static readonly (int IdCliente, string Nombre, string Ciudad)[] Clientes =
    {
        (1, "Ana", "Madrid"),
        (2, "Bruno", "Lisboa"),
        (3, "Carla", "Paris"),
    };

    private static void CreateFacturasClientes(string dir, bool withIndex)
    {
        using (var w = DbfWriter.Create(Path.Combine(dir, "facturas.dbf"), new[]
        {
            new DbfColumnDef("IDFACTURA", 'C', 8),
            new DbfColumnDef("CLIENTEID", 'I', 4),
            new DbfColumnDef("IMPORTE", 'N', 12, 2),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var f in Facturas) w.AppendRecord(f.IdFactura, f.ClienteId, f.Importe);
            if (withIndex)
            {
                w.CreateTag(new CdxTagDefinition("TFID", "IDFACTURA")); // CHARACTER key → MakeCharBound
                w.CreateTag(new CdxTagDefinition("TCID", "CLIENTEID")); // numeric key → ordered seek
            }
            w.Flush();
        }

        using (var w = DbfWriter.Create(Path.Combine(dir, "clientes.dbf"), new[]
        {
            new DbfColumnDef("IDCLIENTE", 'I', 4),
            new DbfColumnDef("NOMBRE", 'C', 15),
            new DbfColumnDef("CIUDAD", 'C', 15),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var c in Clientes) w.AppendRecord(c.IdCliente, c.Nombre, c.Ciudad);
            if (withIndex) w.CreateTag(new CdxTagDefinition("TCID", "IDCLIENTE"));
            w.Flush();
        }
    }

    // ---- shared harness ----------------------------------------------------------------------

    private static EvaluationContext OracleCtx(VfpSession s) => new()
    {
        Deleted = s.Context.Deleted,
        Exact = s.Context.Exact,
        Ansi = s.Context.Ansi,
        Collation = s.Context.Collation,
        SqlSemantics = true,
    };

    private void Check(string dir, string sql, bool ordered = false, bool? deleted = null)
    {
        using var s = deleted is null
            ? new VfpSession()
            : new VfpSession(new EvaluationContext { Deleted = deleted.Value });
        s.OpenDirectory(dir);

        var stmt = (SelectStatement)SqlParser.Parse(sql);
        var expected = SqlTestSupport.RunJoinOracle(dir, OracleCtx(s), stmt);
        var actual = SqlTestSupport.Materialize(s.Execute(sql)!);

        if (ordered) SqlTestSupport.AssertRowsEqualOrdered(expected, actual);
        else SqlTestSupport.AssertRowsEqualUnordered(expected, actual);
    }

    /// <summary>Every correctness assertion runs against BOTH the indexed and the non-indexed copy of
    /// the same data — the differential that justifies passing drive.Cdx instead of null.</summary>
    private void CheckBoth(string sql, bool ordered = false, bool? deleted = null)
    {
        Check(_indexed.Path, sql, ordered, deleted);
        Check(_plain.Path, sql, ordered, deleted);
    }

    /// <summary>The WHERE conjuncts the executor actually pushed onto the driving source.</summary>
    private IReadOnlyList<string> Pushed(string sql)
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var ex = new SelectExecutor(s);
        ex.Run((SelectStatement)SqlParser.Parse(sql));
        return ex.PushdownFilters;
    }

    // =========================================================================================
    //  (1) PURE HELPER TESTS
    // =========================================================================================

    [Fact]
    public void SplitTopLevelAnd_Splits_Only_Top_Level_And()
    {
        var parts = SelectExecutor.SplitTopLevelAnd("emp.empid = 3 AND dept.dname = 'Engineering' AND emp.salary > 100");
        Assert.Equal(3, parts.Count);
        Assert.Equal("emp.empid = 3", parts[0]);
        Assert.Equal("dept.dname = 'Engineering'", parts[1]);
        Assert.Equal("emp.salary > 100", parts[2]);
    }

    [Fact]
    public void SplitTopLevelAnd_Does_Not_Descend_Into_Parens()
    {
        var parts = SelectExecutor.SplitTopLevelAnd("(emp.empid = 3 AND emp.salary > 100) AND dept.dname = 'X'");
        Assert.Equal(2, parts.Count);
        Assert.Equal("(emp.empid = 3 AND emp.salary > 100)", parts[0]);
        Assert.Equal("dept.dname = 'X'", parts[1]);
    }

    [Theory]
    [InlineData("name = 'a AND b' AND emp.empid = 3")]
    [InlineData("name = \"a AND b\" AND emp.empid = 3")]
    [InlineData("name = [a AND b] AND emp.empid = 3")]
    public void SplitTopLevelAnd_Ignores_And_Inside_Literals(string text)
    {
        var parts = SelectExecutor.SplitTopLevelAnd(text);
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("name = ", parts[0], StringComparison.Ordinal);
        Assert.Equal("emp.empid = 3", parts[1]);
    }

    [Fact]
    public void SplitTopLevelAnd_Does_Not_Split_Inside_A_Compound_Token()
    {
        // 'ANDROID' must not be mistaken for the AND keyword.
        var parts = SelectExecutor.SplitTopLevelAnd("emp.ename = 'ANDROID'");
        Assert.Single(parts);
        Assert.Equal("emp.ename = 'ANDROID'", parts[0]);
    }

    [Fact]
    public void SplitTopLevelAnd_No_And_Yields_Single_Piece()
    {
        Assert.Equal(new[] { "emp.empid = 3" }, SelectExecutor.SplitTopLevelAnd("  emp.empid = 3  "));
    }

    [Theory]
    [InlineData("facturas.idFactura = 'F0000003'", "facturas", "idFactura = 'F0000003'")]
    [InlineData("f.idfactura == 'F0000003'", "f", "idfactura == 'F0000003'")]
    [InlineData("emp.deptid = 1 AND emp.empid = 2", "emp", "deptid = 1 AND empid = 2")]
    [InlineData("facturas.x = 'facturas.y'", "facturas", "x = 'facturas.y'")]            // literal untouched
    [InlineData("facturas.x = [facturas.y]", "facturas", "x = [facturas.y]")]            // bracket literal untouched
    [InlineData("noalias.x = 1", "facturas", "noalias.x = 1")]                           // wrong alias → left alone
    [InlineData("emp.empid = 3", "facturas", "emp.empid = 3")]
    public void StripDrivingQualifier_Removes_Only_The_Driving_Qualifier(
        string text, string alias, string expected)
        => Assert.Equal(expected, SelectExecutor.StripDrivingQualifier(text, alias));

    [Fact]
    public void StripDrivingQualifier_Is_Case_Insensitive_On_The_Alias()
        => Assert.Equal("idfactura = 'X'", SelectExecutor.StripDrivingQualifier("FACTURAS.idfactura = 'X'", "facturas"));

    [Fact]
    public void ChainPreservesDriving_Allows_Inner_And_Left()
    {
        Assert.True(SelectExecutor.ChainPreservesDriving(
            Parse("SELECT * FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")));
        Assert.True(SelectExecutor.ChainPreservesDriving(
            Parse("SELECT * FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid")));
        Assert.True(SelectExecutor.ChainPreservesDriving(
            Parse("SELECT * FROM emp, dept WHERE emp.deptid = dept.deptid")));
        Assert.True(SelectExecutor.ChainPreservesDriving(
            Parse("SELECT * FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                  "LEFT JOIN company ON dept.compid = company.compid")));
    }

    [Fact]
    public void ChainPreservesDriving_Rejects_Right_And_Full()
    {
        Assert.False(SelectExecutor.ChainPreservesDriving(
            Parse("SELECT * FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid")));
        Assert.False(SelectExecutor.ChainPreservesDriving(
            Parse("SELECT * FROM emp FULL JOIN dept ON emp.deptid = dept.deptid")));
        // RIGHT only in a LATER step still poisons the chain (it NULLs src[0] for those rows).
        Assert.False(SelectExecutor.ChainPreservesDriving(
            Parse("SELECT * FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                  "RIGHT JOIN company ON dept.compid = company.compid")));
    }

    [Fact]
    public void CollectAndLeaves_Descends_And_And_Stops_At_Every_Other_Node()
    {
        // A predicate TREE only exists when the WHERE carries a sub-SELECT, so build it directly:
        //   (emp.salary > 100) AND (emp.empid = 1 OR dept.dname = 'X')
        // Only the AND-linked ExprPredicate leaves may be pushed; the OR subtree is left whole.
        var salary = new ExprPredicate(VfpExpression.Parse("emp.salary > 100"));
        var or = new OrPredicate(
            new ExprPredicate(VfpExpression.Parse("emp.empid = 1")),
            new ExprPredicate(VfpExpression.Parse("dept.dname = 'X'")));
        var and = new AndPredicate(salary, or);

        var into = new List<string>();
        SelectExecutor.CollectAndLeaves(and, into);
        Assert.Equal(new[] { "emp.salary > 100" }, into);

        // A NOT / subquery leaf is never descended into either.
        var not = new NotPredicate(new ExprPredicate(VfpExpression.Parse("emp.empid = 2")));
        var nested = new List<string>();
        SelectExecutor.CollectAndLeaves(new AndPredicate(salary, not), nested);
        Assert.Equal(new[] { "emp.salary > 100" }, nested);
    }

    private static SelectStatement Parse(string sql) => (SelectStatement)SqlParser.Parse(sql);

    // =========================================================================================
    //  (2) WIRING TESTS — the pushdown really runs, and with the right conjuncts
    // =========================================================================================

    [Fact]
    public void Pushes_The_Driving_Only_Conjunct()
    {
        var pushed = Pushed("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3");
        Assert.Single(pushed);
        Assert.Contains("empid", pushed[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pushes_A_Qualified_Conjunct_When_The_Alias_Matches_The_Driving_Source()
    {
        var pushed = Pushed("SELECT f.idfactura, c.nombre FROM facturas f " +
                            "LEFT JOIN clientes c ON f.clienteid = c.idcliente " +
                            "WHERE f.idfactura = 'F0000003'");
        Assert.Single(pushed);
        Assert.Contains("idfactura", pushed[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Does_Not_Push_When_The_Qualifier_Names_Another_Alias()
    {
        // 'emp' is not the driving alias (the driving alias is 'e'), so the conjunct is left entirely
        // to the post-join residual — exactly how CompositeRowContext would have resolved it.
        var pushed = Pushed("SELECT e.ename, d.dname FROM emp e LEFT JOIN dept d ON e.deptid = d.deptid " +
                            "WHERE emp.empid = 3");
        Assert.Empty(pushed);
    }

    [Fact]
    public void Does_Not_Push_A_Conjunct_Read_Only_By_The_Secondary_Table()
    {
        var pushed = Pushed("SELECT f.idfactura, c.nombre FROM facturas " +
                            "LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente " +
                            "WHERE clientes.nombre = 'Ana'");
        Assert.Empty(pushed);
    }

    [Fact]
    public void Pushes_Only_The_Driving_Half_Of_An_And()
    {
        var pushed = Pushed("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3 AND dept.dname = 'Engineering'");
        Assert.Single(pushed);
        Assert.Contains("empid", pushed[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pushed, p => p.Contains("dname", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pushes_Every_Driving_Conjunct_Of_A_Pure_And()
    {
        var pushed = Pushed("SELECT f.idfactura, c.nombre FROM facturas " +
                            "LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente " +
                            "WHERE facturas.idfactura = 'F0000003' AND facturas.importe > 100");
        Assert.Equal(2, pushed.Count);
    }

    [Fact]
    public void Does_Not_Push_An_Or()
    {
        var pushed = Pushed("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3 OR dept.dname = 'Engineering'");
        Assert.Empty(pushed);
    }

    [Fact]
    public void Does_Not_Push_A_Cross_Source_Only_Conjunct()
    {
        var pushed = Pushed("SELECT emp.ename, dept.dname FROM emp, dept " +
                            "WHERE emp.deptid = dept.deptid");
        Assert.Empty(pushed);
    }

    [Fact]
    public void Does_Not_Push_When_The_Chain_Has_A_Right_Or_Full_Step()
    {
        Assert.Empty(Pushed("SELECT emp.ename, dept.dname FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3"));
        Assert.Empty(Pushed("SELECT emp.ename, dept.dname FROM emp FULL JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3"));
    }

    [Fact]
    public void Does_Not_Push_A_Recno_Dependent_Conjunct_But_Pushes_Its_Sibling()
    {
        // RECNO() reads the ROW CONTEXT, which differs between the driving scan (real recno) and the
        // composite context (always 0) — so it must never be pushed, while its AND sibling still can.
        var pushed = Pushed("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3 AND RECNO() = 1");
        Assert.Single(pushed);
        Assert.Contains("empid", pushed[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RAND() < 0.5")]
    [InlineData("emp.empid < RAND() * 10")]
    [InlineData("NOT EOF()")]
    [InlineData("EVALUATE('dept.dname') = 'Engineering'")]
    [InlineData(".T.")]
    public void Does_Not_Push_A_Context_Dependent_Or_Field_Free_Conjunct_But_Pushes_Its_Sibling(string conjunct)
    {
        // Evaluated once on the driving table and again over the composites, a conjunct whose value
        // does not come from driving fields alone could disagree with itself (RAND twice, a host-state
        // function in two contexts) — and a field-free one gains nothing from being pushed.
        var pushed = Pushed("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3 AND " + conjunct);
        Assert.Single(pushed);
        Assert.Equal("emp.empid = 3", pushed[0]);
    }

    [Fact]
    public void No_Where_Means_No_Pushdown()
    {
        Assert.Empty(Pushed(
            "SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid"));
    }

    [Fact]
    public void Pushes_The_Driving_Half_Of_A_Where_That_Carries_A_Subquery()
    {
        // A WHERE containing a sub-SELECT is parsed into a SqlPredicate TREE, so
        // CollectPushdownFilters walks it instead of splitting text — and must still skip the
        // subquery atom itself (it needs the composite row context).
        var pushed = Pushed("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid " +
                            "WHERE emp.empid = 3 AND dept.dname = 'Engineering' " +
                            "AND emp.deptid IN (SELECT deptid FROM dept)");
        Assert.Single(pushed);
        Assert.Contains("empid", pushed[0], StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================================
    //  (3) INDEX EVIDENCE — the pushed, alias-stripped filter IS Rushmore-indexable (Decision 1)
    // =========================================================================================

    [Fact]
    public void Pushed_Character_Filter_Uses_The_Cdx_And_Scans_Few_Records()
    {
        string path = Path.Combine(_indexed.Path, "facturas.dbf");
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);

        // The exact text RestrictDriving hands to FindRecords for the user's own workload shape.
        string pushed = "facturas.idfactura = 'F0000003'";
        string stripped = SelectExecutor.StripDrivingQualifier(pushed, "facturas");
        Assert.Equal("idfactura = 'F0000003'", stripped);

        var ctx = new EvaluationContext { Deleted = true, SqlSemantics = true };
        var qr = QueryOptimizer.FindRecords(table, cdx, "(" + stripped + ")", ctx);

        Assert.True(qr.Optimized, "the alias-stripped driving filter must be index-resolvable");
        Assert.True(qr.RecordsScanned < table.RecordCount,
            $"expected an index-narrowed scan, scanned {qr.RecordsScanned} of {table.RecordCount}");
        Assert.Equal(new[] { 3 }, qr.RecordNumbers);   // F0000003 is physical record 3
    }

    [Fact]
    public void Pushed_Numeric_Filter_Uses_The_Cdx()
    {
        string path = Path.Combine(_indexed.Path, "facturas.dbf");
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);

        var ctx = new EvaluationContext { Deleted = true, SqlSemantics = true };
        var qr = QueryOptimizer.FindRecords(table, cdx, "(clienteid = 1)", ctx);

        Assert.True(qr.Optimized);
        Assert.Equal(3, qr.RecordsScanned);
        Assert.Equal(new[] { 1, 2, 8 }, qr.RecordNumbers);
    }

    // =========================================================================================
    //  (4) CORRECTNESS vs THE BRUTE-FORCE ORACLE — indexed AND non-indexed
    // =========================================================================================

    // ---- INNER JOIN --------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT emp.empid, emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3 AND dept.dname = 'Engineering'")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid WHERE emp.ename = 'Alice' OR emp.ename = 'Bob'")]
    public void InnerJoin_WithWhere_Matches_Oracle(string sql) => CheckBoth(sql);

    // ---- LEFT JOIN ---------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.salary >= 4500")]
    // WHERE reads only the SECONDARY table → nothing pushed, but the result must still match.
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE dept.dname = 'Engineering'")]
    // WHERE spans BOTH tables → only the driving half is pushed.
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3 AND dept.dname = 'Engineering'")]
    // WHERE on the driving side must still KEEP the orphan row when it passes (Frank has no dept).
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.ename = 'Frank'")]
    // A driving-side WHERE that REJECTS the orphan must drop it.
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 1")]
    public void LeftJoin_WithWhere_Matches_Oracle(string sql) => CheckBoth(sql);

    // ---- the reported workload shape: CHARACTER driving key + LEFT JOIN -----------------------

    [Theory]
    [InlineData("SELECT f.idfactura, c.nombre FROM facturas LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente WHERE facturas.idfactura = 'F0000003'")]
    [InlineData("SELECT f.idfactura, c.nombre FROM facturas f LEFT JOIN clientes c ON f.clienteid = c.idcliente WHERE f.idfactura = 'F0000006'")]
    [InlineData("SELECT f.idfactura, c.nombre FROM facturas LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente WHERE facturas.clienteid = 3")]
    [InlineData("SELECT f.idfactura, c.nombre FROM facturas LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente WHERE facturas.importe > 400")]
    [InlineData("SELECT f.idfactura, c.nombre FROM facturas LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente WHERE facturas.idfactura = 'F0000003' AND facturas.importe > 100")]
    [InlineData("SELECT f.idfactura, c.nombre FROM facturas LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente WHERE facturas.idfactura = 'ZZZZZZZZ'")]
    // the orphan factura (clienteid 99) must survive a passing WHERE with NULL inner fields.
    [InlineData("SELECT f.idfactura, c.nombre FROM facturas LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente WHERE facturas.idfactura = 'F0000006'")]
    public void CharacterKeyDriving_WithWhere_Matches_Oracle(string sql) => CheckBoth(sql);

    // ---- comma cross join --------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname FROM emp, dept WHERE emp.empid = 3")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp, dept WHERE emp.deptid = dept.deptid AND emp.empid = 3")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp, dept WHERE emp.deptid = dept.deptid")]
    public void CommaJoin_WithWhere_Matches_Oracle(string sql) => CheckBoth(sql);

    // ---- three-table chain -------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname, company.cname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid LEFT JOIN company ON dept.compid = company.compid WHERE emp.empid = 3")]
    [InlineData("SELECT emp.ename, dept.dname, company.cname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid LEFT JOIN company ON dept.compid = company.compid WHERE emp.ename = 'Alice' AND company.cname = 'Acme'")]
    public void ThreeTableChain_WithWhere_Matches_Oracle(string sql) => CheckBoth(sql);

    // ---- RIGHT / FULL: no pushdown, results must be unchanged --------------------------------

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid WHERE dept.dname = 'Engineering'")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp FULL JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp FULL JOIN dept ON emp.deptid = dept.deptid WHERE dept.dname = 'Sales'")]
    public void RightFull_WithWhere_Matches_Oracle_And_Pushes_Nothing(string sql)
    {
        CheckBoth(sql);
        Assert.Empty(Pushed(sql));
    }

    // ---- SET DELETED ON / OFF ----------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetDeleted_Driving_Where_Matches_Oracle(bool deleted)
    {
        const string sql = "SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3";
        Check(_indexed.Path, sql, deleted: deleted);
        Check(_plain.Path, sql, deleted: deleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetDeleted_Deleted_Driving_Row_WithWhere_Matches_Oracle(bool deleted)
    {
        // Delete Carol (EMPID 3) on the DRIVING side, then query exactly for her.
        using var tmp = new SqlTestSupport.TempDir();
        SqlTestSupport.CreateJoinTables(tmp.Path, withIndex: true);
        CreateFacturasClientes(tmp.Path, withIndex: true);
        using (var w = DbfWriter.Open(Path.Combine(tmp.Path, "emp.dbf")))
        {
            w.Delete(index: 2);
            w.Flush();
        }
        Check(tmp.Path,
            "SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3",
            deleted: deleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetDeleted_CharacterKey_Where_Matches_Oracle(bool deleted)
    {
        const string sql = "SELECT f.idfactura, c.nombre FROM facturas LEFT JOIN clientes ON facturas.clienteid = clientes.idcliente " +
                           "WHERE facturas.idfactura = 'F0000003'";
        Check(_indexed.Path, sql, deleted: deleted);
        Check(_plain.Path, sql, deleted: deleted);
    }

    // ---- a deleted INNER-side row still joins under SET DELETED OFF --------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetDeleted_Deleted_Secondary_Row_WithDrivingWhere_Matches_Oracle(bool deleted)
    {
        using var tmp = new SqlTestSupport.TempDir();
        SqlTestSupport.CreateJoinTables(tmp.Path, withIndex: true);
        CreateFacturasClientes(tmp.Path, withIndex: true);
        using (var w = DbfWriter.Open(Path.Combine(tmp.Path, "dept.dbf")))
        {
            w.Delete(index: 1);   // Engineering (DEPTID 2)
            w.Flush();
        }
        Check(tmp.Path,
            "SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 3",
            deleted: deleted);
    }
}
