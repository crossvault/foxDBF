using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// TRY … CATCH [TO oErr] [WHEN …] … FINALLY … ENDTRY and THROW. Every expectation is the VFP9 runtime's
/// (the internal MicroVfpTryCatchVfp9OracleTests runs the same programs in the real runtime and compares). Errors
/// are provoked with commands microVFP raises: USE of a missing table (1), CONTINUE without LOCATE (42),
/// an unknown verb (16) and THROW (2071).
/// </summary>
public sealed class MicroVfpTryCatchTests
{
    private static VfpInterpreter New() => new(new VfpSession());

    private static string Str(VfpInterpreter i, string expr) => i.EvalExpression(expr).AsString;
    private static decimal Num(VfpInterpreter i, string expr) => i.EvalExpression(expr).AsNumber;

    [Fact]
    public void ErrorInTry_SkipsRestOfTry_RunsCatch()
    {
        var i = New();
        i.Execute("x = 0\nTRY\n  x = 1\n  USE no_such_table_zz\n  x = 2\nCATCH\n  x = x + 10\nENDTRY");
        Assert.Equal(11m, Num(i, "x"));
    }

    [Fact]
    public void NoError_SkipsCatch_RunsFinally()
    {
        var i = New();
        i.Execute("t = ''\nTRY\n  t = t + 't,'\nCATCH\n  t = t + 'c,'\nFINALLY\n  t = t + 'f,'\nENDTRY");
        Assert.Equal("t,f,", Str(i, "t"));
    }

    [Fact]
    public void CatchTo_ExposesTheExceptionObject()
    {
        var i = New();
        i.Execute("TRY\n  USE no_such_table_zz\nCATCH TO oErr\n  n = oErr.ErrorNo\n  ln = oErr.LineNo\n" +
                  "  vt = VARTYPE(oErr)\n  ty = TYPE('oErr')\n  uv = VARTYPE(oErr.UserValue)\n  lc = oErr.LineContents\n" +
                  "  cl = oErr.Class\n  lv = oErr.StackLevel\nENDTRY");
        Assert.Equal(1m, Num(i, "n"));
        Assert.Equal(2m, Num(i, "ln"));            // the line of the failing USE
        Assert.Equal("O", Str(i, "vt"));
        Assert.Equal("O", Str(i, "ty"));
        Assert.Equal("C", Str(i, "uv"));           // UserValue defaults to ""
        Assert.Equal("", Str(i, "lc"));            // "" in the main program (VFP9) …
        Assert.Equal("Exception", Str(i, "cl"));
        Assert.Equal(1m, Num(i, "lv"));
        Assert.Equal("O", Str(i, "VARTYPE(oErr)"));  // the object outlives ENDTRY
    }

    [Fact]
    public void LineContents_IsTheFailingLine_InsideAProcedure()
    {
        var i = New();
        i.Execute("FUNCTION Probe\n  TRY\n    CONTINUE\n  CATCH TO o\n    lc = o.LineContents\n  ENDTRY\n  RETURN lc\nENDFUNC\n");
        Assert.Equal("CONTINUE", i.Call("Probe").AsString);   // … and the source line inside a procedure
    }

    [Fact]
    public void CatchTo_NeverAssignedWhenNoError_AndNullClearsIt()
    {
        var i = New();
        i.Execute("TRY\n  x = 1\nCATCH TO oNever\nENDTRY\nTRY\n  CONTINUE\nCATCH TO oE\nENDTRY\nb = VARTYPE(oE)\noE = .NULL.");
        Assert.Equal("U", Str(i, "TYPE('oNever')"));
        Assert.Equal("O", Str(i, "b"));
        Assert.Equal("X", Str(i, "VARTYPE(oE)"));
    }

    [Fact]
    public void InsideCatch_ErrorIsZero_MessageAndAerrorCarryTheError()
    {
        var i = New();
        i.Execute("DIMENSION laE[1]\nTRY\n  CONTINUE\nCATCH\n  e = ERROR()\n  m = MESSAGE()\n  n = AERROR(laE)\nENDTRY\nm2 = MESSAGE()");
        Assert.Equal(0m, Num(i, "e"));
        Assert.Equal("The LOCATE command must be issued before the CONTINUE command.", Str(i, "m"));
        Assert.Equal(1m, Num(i, "n"));
        Assert.Equal(42m, Num(i, "laE[1]"));
        Assert.Equal(7m, Num(i, "ALEN(laE, 2)"));
        Assert.Equal(Str(i, "m"), Str(i, "m2"));   // MESSAGE() keeps the caught text after ENDTRY
    }

    [Fact]
    public void ErrorInCatch_RunsInnerFinally_ThenOuterCatch()
    {
        var i = New();
        i.Execute("t = ''\nTRY\n  TRY\n    CONTINUE\n  CATCH\n    t = t + 'ic,'\n    FOOBAR 1\n    t = t + 'ic2,'\n" +
                  "  FINALLY\n    t = t + 'if,'\n  ENDTRY\n  t = t + 'after,'\nCATCH TO o\n  t = t + 'oc' + TRANSFORM(o.ErrorNo) + ','\n" +
                  "FINALLY\n  t = t + 'of,'\nENDTRY");
        Assert.Equal("ic,if,oc16,of,", Str(i, "t"));
    }

    [Fact]
    public void NoCatch_RunsFinally_ThenPropagates()
    {
        var i = New();
        i.Execute("t = ''\nTRY\n  TRY\n    CONTINUE\n  FINALLY\n    t = t + 'if,'\n  ENDTRY\n  t = t + 'after,'\n" +
                  "CATCH TO o\n  t = t + 'oc' + TRANSFORM(o.ErrorNo)\nENDTRY");
        Assert.Equal("if,oc42", Str(i, "t"));
    }

    [Fact]
    public void ErrorInFinally_Propagates()
    {
        var i = New();
        i.Execute("t = ''\nTRY\n  TRY\n    CONTINUE\n  CATCH\n    t = t + 'ic,'\n  FINALLY\n    FOOBAR 2\n  ENDTRY\n" +
                  "CATCH TO o\n  t = t + 'oc' + TRANSFORM(o.ErrorNo)\nENDTRY");
        Assert.Equal("ic,oc16", Str(i, "t"));
    }

    [Fact]
    public void BareThrowInCatch_ReRaisesTheCaughtError()
    {
        var i = New();
        i.Execute("TRY\n  TRY\n    CONTINUE\n  CATCH\n    THROW\n  ENDTRY\nCATCH TO o\n  n = o.ErrorNo\n  uv = VARTYPE(o.UserValue)\nENDTRY");
        Assert.Equal(42m, Num(i, "n"));
        Assert.Equal("C", Str(i, "uv"));
    }

    [Fact]
    public void ThrowValue_Raises2071_WithUserValue()
    {
        var i = New();
        i.Execute("DIMENSION laE[1]\nTRY\n  THROW 'abc'\nCATCH TO o\n  n = o.ErrorNo\n  m = o.Message\n  uv = o.UserValue\n" +
                  "  msg = MESSAGE()\n  =AERROR(laE)\nENDTRY");
        Assert.Equal(2071m, Num(i, "n"));
        Assert.Equal("User Thrown Error .", Str(i, "m"));
        Assert.Equal("abc", Str(i, "uv"));
        Assert.Equal("Unknown error code 2071", Str(i, "msg"));
        Assert.Equal(3071m, Num(i, "laE[1]"));
        Assert.Equal("Unknown error code 2071", Str(i, "laE[2]"));
    }

    [Fact]
    public void ThrowObject_WrapsItAsUserValue()
    {
        var i = New();
        i.Execute("TRY\n  TRY\n    CONTINUE\n  CATCH TO oI\n    THROW oI\n  ENDTRY\nCATCH TO oE\n  n = oE.ErrorNo\n" +
                  "  vt = VARTYPE(oE.UserValue)\n  inner = oE.UserValue.ErrorNo\nENDTRY");
        Assert.Equal(2071m, Num(i, "n"));
        Assert.Equal("O", Str(i, "vt"));
        Assert.Equal(42m, Num(i, "inner"));
    }

    [Fact]
    public void CatchWhen_PicksTheFirstMatchingClause()
    {
        var i = New();
        i.Execute("t = ''\nTRY\n  CONTINUE\nCATCH TO o WHEN .F.\n  t = 'c1'\nCATCH TO o WHEN o.ErrorNo = 42\n  t = 'c2'\nCATCH\n  t = 'c3'\nENDTRY");
        Assert.Equal("c2", Str(i, "t"));
    }

    [Fact]
    public void NoMatchingWhen_PropagatesToTheOuterTry()
    {
        var i = New();
        i.Execute("t = ''\nTRY\n  TRY\n    CONTINUE\n  CATCH WHEN .F.\n    t = 'inner'\n  ENDTRY\nCATCH\n  t = 'outer'\nENDTRY");
        Assert.Equal("outer", Str(i, "t"));
    }

    [Fact]
    public void TryWinsOverOnError_OnErrorStillTrapsOutside()
    {
        var i = New();
        i.Execute("t = ''\nON ERROR t = t + 'oe' + TRANSFORM(ERROR()) + ','\nTRY\n  CONTINUE\nCATCH\n  t = t + 'c,'\nENDTRY\n" +
                  "CONTINUE\nt = t + 'post,'\nON ERROR");
        Assert.Equal("c,oe42,post,", Str(i, "t"));
    }

    [Fact]
    public void ErrorInCalledProc_IsCaughtByCallersTry_EvenWithCalleeOnError()
    {
        var i = New();
        i.Execute("FUNCTION Boom\n  LOCAL lcLoc\n  lcLoc = 'x'\n  ON ERROR t = t + 'own,'\n  CONTINUE\n  t = t + 'boomafter,'\n  RETURN 1\nENDFUNC\n");
        i.Execute("t = ''\nTRY\n  =Boom()\n  t = t + 'after,'\nCATCH TO o\n  t = t + 'c' + TRANSFORM(o.ErrorNo) + '|' + o.Procedure + '|' + " +
                  "TRANSFORM(o.StackLevel) + '|' + PROGRAM() + '|' + TYPE('lcLoc')\nENDTRY\nON ERROR");
        Assert.Equal("c42|boom|2|(main)|U", Str(i, "t"));
    }

    [Fact]
    public void ExitInsideTry_LeavesOnlyTheTryStructure()
    {
        var i = New();
        i.Execute("t = ''\nFOR k = 1 TO 3\n  TRY\n    IF k = 2\n      EXIT\n    ENDIF\n    t = t + TRANSFORM(k) + ','\n" +
                  "  CATCH\n    t = t + 'c,'\n  FINALLY\n    t = t + 'f,'\n  ENDTRY\nENDFOR");
        Assert.Equal("1,f,f,3,f,", Str(i, "t"));
    }

    [Fact]
    public void ExitInsideCatch_LeavesTheStructure()
    {
        var i = New();
        i.Execute("t = ''\nTRY\n  CONTINUE\nCATCH\n  t = 'c,'\n  EXIT\n  t = t + 'afterexit,'\nFINALLY\n  t = t + 'f,'\nENDTRY");
        Assert.Equal("c,f,", Str(i, "t"));
    }

    [Fact]
    public void LoopInsideTry_Raises1939()
    {
        var i = New();
        i.Execute("t = ''\nFOR k = 1 TO 2\n  TRY\n    IF k = 1\n      LOOP\n    ENDIF\n  CATCH TO o\n" +
                  "    t = t + TRANSFORM(o.ErrorNo) + '|' + o.Message + ','\n  ENDTRY\nENDFOR");
        Assert.Equal("1939|WITH/ENDWITH mismatch.,", Str(i, "t"));
    }

    [Fact]
    public void ReturnInsideTry_Raises2060_AndTheProcedureContinues()
    {
        var i = New();
        i.Execute("FUNCTION F1\n  TRY\n    RETURN 5\n  CATCH TO o\n    n = o.ErrorNo\n    m = o.Message\n  ENDTRY\n  RETURN 7\nENDFUNC\n");
        i.Execute("PUBLIC n, m\nr = F1()");
        Assert.Equal(7m, Num(i, "r"));
        Assert.Equal(2060m, Num(i, "n"));
        Assert.Equal("RETURN/RETRY statement not allowed in TRY/CATCH.", Str(i, "m"));
    }

    [Fact]
    public void UnhandledErrorInTry_ReachesTheHost()
    {
        var i = New();
        var ex = Assert.Throws<MicroVfpRuntimeException>(() =>
            i.Execute("TRY\n  CONTINUE\nFINALLY\n  t = 'f'\nENDTRY"));
        Assert.Equal(42, ex.VfpErrorNumber);
        Assert.Equal("f", Str(i, "t"));
    }

    [Fact]
    public void UncaughtThrow_ReachesTheHostAs2071()
    {
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => New().Execute("THROW 'boom'"));
        Assert.Equal(2071, ex.VfpErrorNumber);
        Assert.Equal("User Thrown Error .", ex.Message);
    }

    [Fact]
    public void MissingEndTry_IsASyntaxError()
    {
        Assert.Throws<MicroVfpSyntaxException>(() => PrgParser.Parse("TRY\n  x = 1\nCATCH\n  x = 2\n"));
        Assert.Throws<MicroVfpSyntaxException>(() => PrgParser.Parse("CATCH\n"));
    }
}
