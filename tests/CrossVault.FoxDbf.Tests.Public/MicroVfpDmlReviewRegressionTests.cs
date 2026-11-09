using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

// Expected results are pinned by the internal GrokDmlReviewVfp9OracleTests.
public sealed class MicroVfpDmlReviewRegressionTests
{
    private sealed class Bench : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = new("dml_review");
        private readonly VfpSession _session = new();
        public VfpInterpreter Vfp { get; }

        public Bench()
        {
            _session.OpenDirectory(_dir.Path);
            Vfp = new VfpInterpreter(_session);
        }

        public void Dispose() { Vfp.Dispose(); _session.Dispose(); _dir.Dispose(); }
    }

    [Theory]
    [InlineData("all", false)]
    [InlineData("rest", false)]
    [InlineData("next", false)]
    [InlineData("record", false)]
    [InlineData("all", true)]
    [InlineData("rest", true)]
    [InlineData("next", true)]
    [InlineData("record", true)]
    public void Replace_ScopeNamedField_RemainsAnExpression(string field, bool predicate)
    {
        using var b = new Bench();
        b.Vfp.Execute($"CREATE TABLE t (n I, code I, {field} I)\nINSERT INTO t VALUES (0,7,7)\nINSERT INTO t VALUES (0,8,7)\nGO 1");
        b.Vfp.Execute($"REPLACE n WITH {(predicate ? "9 FOR code = " : "")}{field}");
        b.Vfp.Execute("GO 1");
        Assert.Equal(predicate ? 9 : 7, b.Vfp.EvalExpression("n").AsNumber);
        b.Vfp.Execute("GO 2");
        Assert.Equal(0, b.Vfp.EvalExpression("n").AsNumber);
    }

    [Theory]
    [InlineData("APPEND BLANK", "INSERT", 2)]
    [InlineData("REPLACE id WITH 9", "UPDATE", 1)]
    public void RejectedTrigger_Raises1539AndRestoresRow(string command, string kind, int recno)
    {
        using var b = new Bench();
        b.Vfp.Execute("CREATE TABLE t (id I)\nINSERT INTO t VALUES (1)\nGO 1\nINDEX ON id TAG id");
        b.Vfp.EnforceReferentialIntegrity = true;
        b.Vfp.Execute($"PROCEDURE __RI_{kind}_t\nRETURN .F.\nENDPROC");
        var error = Assert.Throws<MicroVfpRuntimeException>(() => b.Vfp.Execute(command));
        Assert.Equal(1539, error.VfpErrorNumber);
        Assert.Equal(1, b.Vfp.EvalExpression("RECCOUNT()").AsNumber);
        Assert.Equal(recno, b.Vfp.EvalExpression("RECNO()").AsNumber);
        Assert.Equal(0, b.Vfp.SnapshotTempFileCount());
        b.Vfp.Execute("SEEK 1");
        Assert.True(b.Vfp.EvalExpression("FOUND()").AsLogical);
        Assert.Equal(1, b.Vfp.EvalExpression("id").AsNumber);
        b.Vfp.Execute(kind == "INSERT" ? "SEEK 0" : "SEEK 9");
        Assert.False(b.Vfp.EvalExpression("FOUND()").AsLogical);
    }

    [Theory]
    [InlineData("t.id = 9")]
    [InlineData("t->id = 9")]
    [InlineData("STORE 9 TO t.id")]
    public void AliasAssignment_RejectsMissingObjectWith1923(string command)
    {
        using var b = new Bench();
        b.Vfp.Execute("CREATE TABLE t (id I)\nINSERT INTO t VALUES (1)\nGO 1");
        var error = Assert.Throws<MicroVfpRuntimeException>(() => b.Vfp.Execute(command));
        Assert.Equal(1923, error.VfpErrorNumber);
        Assert.Equal(1, b.Vfp.EvalExpression("id").AsNumber);
    }

    [Theory]
    [InlineData(2, "INSERT INTO t VALUES (6)", 6)]
    [InlineData(3, "INSERT INTO t VALUES (6)", 6)]
    [InlineData(2, "APPEND BLANK", 6)]
    [InlineData(3, "APPEND BLANK", 6)]
    [InlineData(4, "INSERT INTO t VALUES (6)", -1)]
    [InlineData(5, "INSERT INTO t VALUES (6)", -1)]
    public void BufferedAppend_RecnoDistinguishesRowAndTableBuffering(int mode, string command, int expected)
    {
        using var b = new Bench();
        b.Vfp.Execute($"CREATE TABLE t (id I)\nFOR i = 1 TO 5\nINSERT INTO t VALUES (i)\nENDFOR\nSET MULTILOCKS ON\n=CURSORSETPROP('Buffering', {mode})\n{command}");
        Assert.Equal(expected, b.Vfp.EvalExpression("RECNO()").AsNumber);
        b.Vfp.Execute("=TABLEUPDATE(.T.)");
        Assert.Equal(6, b.Vfp.EvalExpression("RECNO()").AsNumber);
    }

    [Theory]
    [InlineData("NEXT 1", 1)]
    [InlineData("REST", 4)]
    public void Replace_ExplicitStartIncludesDeletedRow(string scope, int recno)
    {
        using var b = new Bench();
        b.Vfp.Execute($"CREATE TABLE t (id I, n I)\nINSERT INTO t VALUES (1,0)\nINSERT INTO t VALUES (2,0)\nINSERT INTO t VALUES (3,0)\nGO 1\nDELETE\nSET DELETED ON\nREPLACE n WITH 1 {scope}");
        Assert.Equal(recno, b.Vfp.EvalExpression("RECNO()").AsNumber);
        b.Vfp.Execute("GO 1");
        Assert.Equal(1, b.Vfp.EvalExpression("n").AsNumber);
    }

    [Theory]
    [InlineData("SELECT g%2, COUNT(*) FROM ct GROUP BY 1 HAVING g%2>0", 1803)]
    [InlineData("SELECT DISTINCT g+1 AS x FROM ct ORDER BY g+1", 1808)]
    public void Sql_ExpressionHavingAndDistinctOrderRemainRejected(string sql, int expected)
    {
        using var b = new Bench();
        b.Vfp.Execute("CREATE TABLE ct (g I)\nINSERT INTO ct VALUES (1)");
        var error = Assert.Throws<FoxDbfSqlException>(() => b.Vfp.Execute(sql + " INTO CURSOR answer"));
        Assert.Equal(expected, error.VfpErrorNumber);
    }
}
