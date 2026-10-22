using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Embedded SQL, <c>INDEX ON</c> and <c>SET FILTER TO</c> evaluate with the microVFP interpreter as their function
/// host: an unknown function raises VFP's error 1 there too (it used to read <c>.NULL.</c>), a UDF and a microVFP
/// function run inside an embedded SELECT, <c>SET FILTER TO</c> and <c>INDEX ON</c> evaluate their expression once
/// when the command runs, and an error inside a filter UDF propagates out of the navigating command. The plain SQL
/// path (no interpreter) is unchanged. Expected values are VFP9's (pinned by the internal
/// MicroVfpHostedExpressionVfp9OracleTests run) unless a test says it pins a documented deviation.
/// </summary>
public sealed class MicroVfpHostedExpressionTests : IDisposable
{
    private readonly MicroVfpTestSupport.TempDir _dir = new("hosted_expr");
    private readonly VfpSession _session;
    private readonly VfpInterpreter _i;

    public MicroVfpHostedExpressionTests()
    {
        _session = new VfpSession();
        _session.OpenDirectory(_dir.Path);
        _i = new VfpInterpreter(_session);
        _i.Execute("""
            CREATE TABLE ct (id I, nm C(5))
            INSERT INTO ct VALUES (1, "a")
            INSERT INTO ct VALUES (2, "b")
            INSERT INTO ct VALUES (3, "c")
            CREATE TABLE et (id I)
            USE
            PUBLIC gnCalls
            gnCalls = 0
            FUNCTION Udf1(n)
            gnCalls = gnCalls + 1
            RETURN n >= 2
            ENDFUNC
            FUNCTION Udf2(n)
            gnCalls = gnCalls + 1
            RETURN n * 10
            ENDFUNC
            FUNCTION Udf3(n)
            gnCalls = gnCalls + 1
            IF gnCalls > 1
              =NoSuchFn2(n)
            ENDIF
            RETURN .T.
            ENDFUNC
            """);
    }

    public void Dispose()
    {
        _i.Dispose();
        _session.Dispose();
        _dir.Dispose();
    }

    private MicroVfpRuntimeException Raises(string source)
        => Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute(source));

    private static void AssertError1(MicroVfpRuntimeException ex, string name = "nosuchfn")
    {
        Assert.Equal(1, ex.VfpErrorNumber);
        Assert.Equal($"File '{name}.prg' does not exist.", ex.Message);
        Assert.Equal(name + ".prg", ex.Details);
    }

    private decimal Num(string expression) => _i.EvalExpression(expression).AsNumber;

    // ─────────────────────────── embedded SQL ───────────────────────────

    [Theory]
    [InlineData("SELECT id FROM ct WHERE NoSuchFn(id) INTO CURSOR q")]
    [InlineData("SELECT NoSuchFn(id) AS v FROM ct INTO CURSOR q")]
    [InlineData("SELECT id FROM ct WHERE NoSuchFn(.NULL.) INTO CURSOR q")]
    [InlineData("SELECT a.id FROM ct a JOIN ct b ON a.id = b.id WHERE NoSuchFn(a.id) INTO CURSOR q")]
    [InlineData("SELECT a.id FROM ct a JOIN ct b ON NoSuchFn(a.id) INTO CURSOR q")]
    [InlineData("SELECT id FROM ct WHERE id IN (SELECT id FROM ct WHERE NoSuchFn(id)) INTO CURSOR q")]
    [InlineData("SELECT id, COUNT(*) AS n FROM ct GROUP BY id HAVING NoSuchFn(n) INTO CURSOR q")]
    public void Sql_UnknownFunction_Raises1(string sql) => AssertError1(Raises(sql));

    [Fact]
    public void Sql_UnknownFunction_IsCaughtByTry()
    {
        _i.Execute("""
            TRY
              SELECT id FROM ct WHERE NoSuchFn(id) INTO CURSOR q
              lcR = "noerr"
            CATCH TO oE
              lcR = TRANSFORM(oE.ErrorNo) + "|" + oE.Details
            ENDTRY
            """);
        Assert.Equal("1|nosuchfn.prg", _i.EvalExpression("lcR").AsString);
    }

    [Fact]
    public void Sql_UdfInWhere_Runs()
    {
        _i.Execute("SELECT id FROM ct WHERE Udf1(id) ORDER BY id INTO CURSOR q");
        Assert.Equal(2m, Num("RECCOUNT('q')"));
        _i.Execute("GO TOP IN q");
        Assert.Equal(2m, Num("q.id"));
        Assert.True(Num("gnCalls") >= 3);
    }

    [Fact]
    public void Sql_UdfAndMicroVfpFunctions_InTheFieldList()
    {
        _i.Execute("SELECT Udf2(id) AS v, TRANSFORM(id) AS t, VARTYPE(nm) AS k FROM ct ORDER BY 1 INTO CURSOR q");
        _i.Execute("GO BOTTOM IN q");
        Assert.Equal(30m, Num("q.v"));
        Assert.Equal("3", _i.EvalExpression("ALLTRIM(q.t)").AsString);
        Assert.Equal("C", _i.EvalExpression("q.k").AsString);
    }

    [Fact]
    public void Sql_UdfInJoinWhere_IsNotLostToThePushdown()
    {
        _i.Execute("SELECT a.id FROM ct a JOIN ct b ON a.id = b.id WHERE Udf1(a.id) INTO CURSOR q");
        Assert.Equal(2m, Num("RECCOUNT('q')"));
    }

    [Fact]
    public void Sql_UdfInSubquery_Runs()
    {
        _i.Execute("SELECT id FROM ct WHERE id IN (SELECT id FROM ct WHERE Udf1(id)) INTO CURSOR q");
        Assert.Equal(2m, Num("RECCOUNT('q')"));
    }

    [Theory]
    [InlineData("SELECT id FROM ct WHERE id IN (SELECT id FROM ct WHERE id = lnX) INTO CURSOR q")]
    [InlineData("SELECT id FROM ct WHERE id IN (SELECT id FROM ct WHERE id = m.lnX) INTO CURSOR q")]
    [InlineData("SELECT id FROM ct WHERE id = lnX INTO CURSOR q")]
    public void Sql_MemvarInsideASubquery_StillResolves(string sql)
    {
        _i.Execute("lnX = 2\n" + sql);
        Assert.Equal(1m, Num("RECCOUNT('q')"));
    }

    [Fact]
    public void Sql_ShortCircuit_DoesNotReachTheUnknownFunction()
    {
        _i.Execute("SELECT id FROM ct WHERE id > 5 AND NoSuchFn(id) INTO CURSOR q");
        Assert.Equal(0m, Num("RECCOUNT('q')"));
    }

    [Fact]
    public void Sql_RecnoAndDeleted_StillAnswerFromTheScanRow()
    {
        // The interpreter's own pointer sits on record 1 of ct; the SQL row is record 3.
        _i.Execute("""
            USE ct
            GO 1
            SELECT RECNO() AS r, DELETED() AS d, RECCOUNT() AS c FROM ct WHERE id = 3 INTO CURSOR q
            """);
        Assert.Equal(3m, Num("q.r"));
        Assert.False(_i.EvalExpression("q.d").AsLogical);
        Assert.Equal(3m, Num("q.c"));
    }

    [Fact]
    public void PlainSqlPath_UnknownFunction_IsStillNull()
    {
        var rows = _session.Execute("SELECT NoSuchFn(id) AS v FROM ct")!.Rows.ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Null(r[0]));
    }

    // ─────────────────────────── INDEX ON ───────────────────────────

    [Theory]
    [InlineData("USE ct EXCLUSIVE\nINDEX ON NoSuchFn(id) TAG nx")]
    [InlineData("USE ct EXCLUSIVE\nINDEX ON id TAG fx FOR NoSuchFn(id)")]
    [InlineData("USE et EXCLUSIVE\nINDEX ON NoSuchFn(id) TAG nx")]      // also on an empty table
    [InlineData("USE ct EXCLUSIVE\nINDEX ON NoSuchFn(id) TO nx")]
    public void IndexOn_UnknownFunction_Raises1(string source) => AssertError1(Raises(source));

    [Fact]
    public void IndexOn_UnknownFunction_LeavesTheIndexAlone()
    {
        _i.Execute("USE ct EXCLUSIVE\nINDEX ON id TAG byid");
        AssertError1(Raises("INDEX ON NoSuchFn(id) TAG nx"));
        Assert.Equal(1m, Num("TAGCOUNT()"));
        _i.Execute("SET ORDER TO byid\nll = SEEK(2)");
        Assert.True(_i.EvalExpression("ll").AsLogical);
    }

    [Theory]
    [InlineData("USE et EXCLUSIVE\nINDEX ON id TAG ft FOR id > 5 AND NoSuchFn(id)")]   // blank record at EOF: .F.
    [InlineData("USE et EXCLUSIVE\nINDEX ON IIF(.F., NoSuchFn(id), id) TAG ii")]
    public void IndexOn_UnevaluatedUnknownFunction_Builds(string source)
    {
        _i.Execute(source);
        Assert.Equal(1m, Num("TAGCOUNT()"));
    }

    [Theory]
    [InlineData("INDEX ON Udf2(id) TAG u2", "UDF2")]
    [InlineData("INDEX ON TRANSFORM(id) TAG tr", "TRANSFORM")]
    [InlineData("INDEX ON id TAG fu FOR Udf1(id)", "UDF1")]
    [InlineData("INDEX ON IIF(.F., Udf2(id), id) TAG iu", "UDF2")]   // a UDF is refused even where not evaluated
    public void IndexOn_FunctionOnlyMicroVfpEvaluates_IsRefused(string source, string name)
    {
        // Deviation (documented): VFP9 builds these; microVFP's index engine cannot call back into the interpreter.
        _i.Execute("USE ct EXCLUSIVE");
        var ex = Raises(source);
        Assert.Null(ex.VfpErrorNumber);
        Assert.Contains(name + "()", ex.Message);
        Assert.Equal(0m, Num("TAGCOUNT()"));
    }

    [Fact]
    public void IndexOn_ErrorInsideAUdf_Propagates()
    {
        _i.Execute("USE ct EXCLUSIVE\ngnCalls = 1");
        AssertError1(Raises("INDEX ON Udf3(id) TAG u3"), "nosuchfn2");
    }

    // ─────────────────────────── SET FILTER ───────────────────────────

    [Theory]
    [InlineData("USE ct\nSET FILTER TO NoSuchFn(id)")]
    [InlineData("USE et\nSET FILTER TO NoSuchFn(id)")]                 // empty table
    [InlineData("USE ct\nGO BOTTOM\nSKIP\nSET FILTER TO NoSuchFn(id)")] // at EOF
    public void SetFilter_UnknownFunction_RaisesAtSetTime(string source)
    {
        AssertError1(Raises(source));
        Assert.Equal("", _i.EvalExpression("FILTER()").AsString);
    }

    [Fact]
    public void SetFilter_ShortCircuitAtEof_DoesNotRaise()
    {
        _i.Execute("USE ct\nGO BOTTOM\nSKIP\nSET FILTER TO id > 5 AND NoSuchFn(id)");
        Assert.NotEqual("", _i.EvalExpression("FILTER()").AsString);
    }

    [Fact]
    public void SetFilter_CallsTheUdfOnceAtSetTime()
    {
        _i.Execute("USE ct\ngnCalls = 0\nSET FILTER TO Udf1(id)");
        Assert.Equal(1m, Num("gnCalls"));
        _i.Execute("GO TOP");
        Assert.Equal(2m, Num("RECNO()"));
    }

    [Fact]
    public void SetFilter_ErrorInsideTheUdf_PropagatesFromNavigation()
    {
        _i.Execute("USE ct\nGO 1\ngnCalls = 0\nSET FILTER TO Udf3(id)");
        AssertError1(Raises("GO TOP"), "nosuchfn2");
        Assert.Equal(2m, Num("gnCalls"));
    }
}
