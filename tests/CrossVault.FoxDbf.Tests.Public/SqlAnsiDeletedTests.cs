using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase 1b (task 2): SET DELETED honoured by the SELECT executor; SQL/ANSI <c>=</c> semantics;
/// and a GUARD that the Xbase filter path (QueryOptimizer / LOCATE) keeps its SET EXACT,
/// order-DEPENDENT <c>=</c> unchanged.
///
/// SQL <c>=</c> rules (when <see cref="EvaluationContext.SqlSemantics"/> is on):
///   ANSI OFF → compare up to the SHORTER operand's length, ORDER-INDEPENDENT
///              ("Smith" = "Sm" and "Sm" = "Smith" both TRUE);
///   ANSI ON  → pad the shorter with blanks, full-length compare;
///   <c>==</c> is always exact.
/// Xbase <c>=</c> (SqlSemantics off) follows SET EXACT and is ORDER-DEPENDENT (compares up to the
/// RHS length): "Smith" = "Sm" TRUE but "Sm" = "Smith" FALSE.
///
/// The semantics tests are RED until the runtime consults <see cref="EvaluationContext.SqlSemantics"/>
/// / <see cref="EvaluationContext.Ansi"/> and the executor sets them; the GUARD test is GREEN today
/// and must STAY green. SAFETY: temp DBF only.
/// </summary>
public sealed class SqlAnsiDeletedTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();
    private readonly string _dbf;

    public SqlAnsiDeletedTests()
    {
        _dir = new SqlTestSupport.TempDir();
        _dbf = _dir.File("person.dbf");
        SqlTestSupport.CreatePersonTable(_dbf);
    }

    public void Dispose() => _dir.Dispose();

    private static HashSet<int> Ids(SqlResult r)
        => r.Rows.Select(row => Convert.ToInt32(row[0])).ToHashSet();

    // ======================================================================================
    //  SET DELETED
    // ======================================================================================

    [Fact]
    public void Deleted_On_Excludes_Deleted_Row()
    {
        using var s = new VfpSession(new EvaluationContext { Deleted = true });
        s.OpenDirectory(_dir.Path);
        // 10 rows, #7 deleted → 9 live.
        var result = s.Execute("SELECT * FROM person")!;
        Assert.Equal(9, SqlTestSupport.Materialize(result).Count);
    }

    [Fact]
    public void Deleted_Off_Includes_Deleted_Row()
    {
        using var s = new VfpSession(new EvaluationContext { Deleted = false });
        s.OpenDirectory(_dir.Path);
        var result = s.Execute("SELECT * FROM person")!;
        Assert.Equal(10, SqlTestSupport.Materialize(result).Count);
    }

    [Fact]
    public void Deleted_On_Excludes_From_Count()
    {
        using var s = new VfpSession(new EvaluationContext { Deleted = true });
        s.OpenDirectory(_dir.Path);
        var result = s.Execute("SELECT COUNT(*) FROM person")!;
        Assert.Equal(9m, SqlTestSupport.Norm(SqlTestSupport.Materialize(result)[0][0]));
    }

    // ======================================================================================
    //  SQL '=' semantics — operator unit tests (via the expression engine + a SQL context)
    // ======================================================================================

    private static VfpValue Eq(string expr, bool ansi)
        => VfpExpression.Parse(expr).Evaluate(TestRow.Empty,
            new EvaluationContext { SqlSemantics = true, Ansi = ansi });

    [Fact]
    public void SqlEq_AnsiOff_Compares_To_Shorter_And_Is_OrderIndependent()
    {
        Assert.True(Eq("'Smith' = 'Sm'", ansi: false).AsLogical);   // shorter = 'Sm', first 2 match
        Assert.True(Eq("'Sm' = 'Smith'", ansi: false).AsLogical);   // ORDER-INDEPENDENT
        Assert.False(Eq("'Smith' = 'Sx'", ansi: false).AsLogical);  // first 2 differ
        Assert.True(Eq("'abc' = 'abc'", ansi: false).AsLogical);    // equal length
    }

    [Fact]
    public void SqlEq_AnsiOn_Pads_Shorter_For_FullLength_Compare()
    {
        Assert.False(Eq("'Smith' = 'Sm'", ansi: true).AsLogical);   // 'Sm   ' != 'Smith'
        Assert.False(Eq("'Sm' = 'Smith'", ansi: true).AsLogical);
        Assert.True(Eq("'Smith' = 'Smith'", ansi: true).AsLogical);
        Assert.True(Eq("'ab' = 'ab '", ansi: true).AsLogical);      // trailing-blank pad equal
    }

    [Fact]
    public void SqlEq_DoubleEquals_Is_Always_Exact()
    {
        Assert.False(Eq("'Smith' == 'Sm'", ansi: false).AsLogical);
        Assert.False(Eq("'Smith' == 'Sm'", ansi: true).AsLogical);
        Assert.True(Eq("'Smith' == 'Smith'", ansi: false).AsLogical);
    }

    // ======================================================================================
    //  SQL '=' semantics — through the executor (WHERE), ANSI OFF vs ON
    // ======================================================================================

    [Fact]
    public void Executor_Where_AnsiOff_Is_OrderIndependent()
    {
        using var s = new VfpSession(new EvaluationContext { Ansi = false });
        s.OpenDirectory(_dir.Path);
        // NAME is C(20): the field is the LONGER operand, so 'Smithson' is compared over its 8 characters against
        // "Sm" + 18 blanks / "Smith" + 15 blanks — only "Smithson" matches, exactly as in VFP.
        Assert.Equal(new HashSet<int> { 3 }, Ids(s.Execute("SELECT id FROM person WHERE name = 'Smithson'")!));
        // The literal side shorter: compared over 'Sm' only, whichever side it stands on.
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, Ids(s.Execute("SELECT id FROM person WHERE 'Sm' = name")!));
        // 'Sm' matches every NAME whose first two chars are 'Sm'.
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, Ids(s.Execute("SELECT id FROM person WHERE name = 'Sm'")!));
    }

    [Fact]
    public void Executor_Where_AnsiOn_Is_FullLength()
    {
        using var s = new VfpSession(new EvaluationContext { Ansi = true });
        s.OpenDirectory(_dir.Path);
        Assert.Equal(new HashSet<int> { 3 }, Ids(s.Execute("SELECT id FROM person WHERE name = 'Smithson'")!));
        Assert.Equal(new HashSet<int> { 2 }, Ids(s.Execute("SELECT id FROM person WHERE name = 'Sm'")!));
    }

    // ======================================================================================
    //  GUARD — the Xbase filter path is UNCHANGED (SET EXACT governs it, order-DEPENDENT)
    // ======================================================================================

    [Fact]
    public void Xbase_Eq_Operator_Is_OrderDependent_Under_ExactOff()
    {
        var ctx = new EvaluationContext { Exact = false }; // SqlSemantics OFF (Xbase)
        Assert.True(VfpExpression.Parse("'Smith' = 'Sm'").Evaluate(TestRow.Empty, ctx).AsLogical);   // RHS prefix of LHS
        Assert.False(VfpExpression.Parse("'Sm' = 'Smith'").Evaluate(TestRow.Empty, ctx).AsLogical);  // ORDER-DEPENDENT
    }

    [Fact]
    public void Xbase_QueryOptimizer_Path_Keeps_ExactOff_Prefix_Semantics()
    {
        using var table = DbfTable.Open(_dbf);
        using var cdx = CdxFile.Open(System.IO.Path.ChangeExtension(_dbf, ".cdx"), table);
        var ctx = new EvaluationContext(); // EXACT OFF, DELETED ON, MACHINE, SqlSemantics OFF

        // 'Sm' is a prefix of Smith / Sm / Smithson → 3 hits.
        var a = QueryOptimizer.FindRecords(table, cdx, "NAME = 'Sm'", ctx);
        Assert.Equal(3, a.RecordNumbers.Count);

        // Order-DEPENDENT: 'Smithson' is a prefix only of NAME 'Smithson' (recno 3); the short
        // NAME 'Sm' (recno 2) does NOT match — exactly the opposite of SQL ANSI-OFF above.
        var b = QueryOptimizer.FindRecords(table, cdx, "NAME = 'Smithson'", ctx);
        Assert.Equal(new[] { 3 }, b.RecordNumbers.ToArray());
    }
}
