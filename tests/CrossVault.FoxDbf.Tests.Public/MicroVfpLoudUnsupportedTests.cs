using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// BREAKING (0.11): a statement microVFP cannot execute raises a catchable VFP error instead of being skipped
/// silently. Unknown verb ⇒ 16 "Unrecognized command verb.", a modelled verb with an unparsed clause ⇒ 36
/// "Command contains unrecognized phrase/keyword.", DO of an unknown procedure ⇒ 1, an expression or SQL
/// statement the parsers cannot represent ⇒ 10. Only the documented headless allow-list stays a no-op.
/// </summary>
public sealed class MicroVfpLoudUnsupportedTests
{
    private static VfpInterpreter New() => new(new VfpSession());

    private static MicroVfpRuntimeException Raises(VfpInterpreter i, string source)
        => Assert.Throws<MicroVfpRuntimeException>(() => i.Execute(source));

    [Fact]
    public void UnknownVerb_Raises16_WithStatementAndLine()
    {
        var ex = Raises(New(), "x = 1\nFOOBAR 1, 2");
        Assert.Equal(16, ex.VfpErrorNumber);
        Assert.Equal("Unrecognized command verb.", ex.Message);
        Assert.Equal("FOOBAR 1, 2", ex.Statement);
        Assert.Equal(2, ex.LineNo);
    }

    [Theory]
    [InlineData("COPY garbage zz")]
    [InlineData("APPEND nonsense")]
    [InlineData("BEGIN SOMETHING")]
    [InlineData("CLOSE NONSENSE")]
    public void KnownVerbWithUnparsedClause_Raises36(string command)
    {
        var ex = Raises(New(), command);
        Assert.Equal(36, ex.VfpErrorNumber);
        Assert.Equal("Command contains unrecognized phrase/keyword.", ex.Message);
    }

    [Theory]
    [InlineData("QUIT")]
    [InlineData("CANCEL")]
    [InlineData("RETRY")]
    [InlineData("DEFINE CLASS foo AS Custom")]
    [InlineData("REPL a WITH 1")]          // 4-letter verb abbreviations are not modelled
    public void NotModelled_Raises16(string command)
        => Assert.Equal(16, Raises(New(), command).VfpErrorNumber);

    [Fact]
    public void LoudErrors_AreTrappedByOnErrorAndTry()
    {
        var i = New();
        i.Execute("n = 0\nON ERROR n = ERROR()\nFOOBAR\nON ERROR\nTRY\n  COPY garbage zz\nCATCH TO o\n  m = o.ErrorNo\nENDTRY");
        Assert.Equal(16m, i.EvalExpression("n").AsNumber);
        Assert.Equal(36m, i.EvalExpression("m").AsNumber);
    }

    [Fact]
    public void DoUnknownProcedure_RaisesError1()
    {
        var ex = Raises(New(), "DO NoSuchProc WITH 1");
        Assert.Equal(1, ex.VfpErrorNumber);
        Assert.Equal("File 'nosuchproc.prg' does not exist.", ex.Message);
    }

    [Fact]
    public void UnparseableExpression_Raises10_InsteadOfNull()
    {
        var i = New();
        var ex = Raises(i, "x = (1 +");
        Assert.Equal(10, ex.VfpErrorNumber);
        Assert.Equal(10, Raises(i, "IF 1 +\n  y = 1\nENDIF").VfpErrorNumber);
    }

    [Fact]
    public void UnparseableSql_Raises()
    {
        var i = New();
        Assert.Equal(10, Raises(i, "SELECT FROM").VfpErrorNumber);
        Assert.NotNull(Raises(i, "INSERT INTO t VALUES (").VfpErrorNumber);
    }

    [Theory]
    [InlineData("? 'hello'")]
    [InlineData("?? 'hello'")]
    [InlineData("@ 1,1 SAY 'x'")]
    [InlineData("ACTIVATE WINDOW w")]
    [InlineData("DEACTIVATE WINDOW w")]
    [InlineData("DEFINE WINDOW w FROM 1,1 TO 2,2")]
    [InlineData("DEFINE MENU m")]
    [InlineData("KEYBOARD 'x'")]
    [InlineData("ON KEY LABEL F2 x = 1")]
    [InlineData("ON SHUTDOWN QUIT")]
    [InlineData("DECLARE INTEGER GetTickCount IN kernel32")]
    [InlineData("DECLARE INTEGER CoCreateGuid IN OLE32.DLL STRING @lcBuffer")]
    [InlineData("DISPLAY")]
    [InlineData("LIST")]
    [InlineData("READ EVENTS")]
    [InlineData("DOEVENTS")]
    [InlineData("ASSERT .F.")]
    [InlineData("DEBUGOUT 'x'")]
    [InlineData("EXTERNAL PROCEDURE foo")]
    [InlineData("CLOSE ALL")]
    [InlineData("CLOSE TABLES ALL")]
    [InlineData("CLOSE DATABASES")]
    [InlineData("CLOSE FORMAT")]
    [InlineData("SET TALK OFF")]
    public void HeadlessAllowList_StaysANoOp(string command)
        => New().Execute(command);

    [Fact]
    public void Declare_WithoutIn_IsTheDimensionSynonym()
    {
        var i = New();
        i.Execute("DECLARE laArr[3]\nlaArr[2] = 'b'");
        Assert.Equal(3m, i.EvalExpression("ALEN(laArr)").AsNumber);
        Assert.Equal("b", i.EvalExpression("laArr[2]").AsString);
    }

    [Fact]
    public void CurrencyLiteral_Parses()
    {
        var i = New();
        i.Execute("y = $12.50\nb = -1 < $0");
        Assert.Equal("Y", i.EvalExpression("VARTYPE($12.50)").AsString);
        Assert.Equal(12.5m, i.EvalExpression("y").AsNumber);
        Assert.True(i.EvalExpression("b").AsLogical);
        Assert.True(i.EvalExpression("'a' $ 'cab'").AsLogical);   // '$' in infix position is still "contained in"
    }

    [Fact]
    public void CloseTables_ClosesEveryWorkArea()
    {
        using var dir = new MicroVfpTestSupport.TempDir("close_tables");
        using (var w = DbfWriter.Create(dir.File("a.dbf"), [new DbfColumnDef("ID", 'I', 4)])) w.AppendRecord(1);
        using (var w = DbfWriter.Create(dir.File("b.dbf"), [new DbfColumnDef("ID", 'I', 4)])) w.AppendRecord(1);
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var i = new VfpInterpreter(session);
        i.Execute("USE a IN 0\nUSE b IN 0\nSELECT b");
        Assert.True(i.EvalExpression("USED('a') AND USED('b')").AsLogical);
        i.Execute("CLOSE TABLES");
        Assert.False(i.EvalExpression("USED('a') OR USED('b')").AsLogical);
        Assert.Equal(1m, i.EvalExpression("SELECT()").AsNumber);
    }
}
