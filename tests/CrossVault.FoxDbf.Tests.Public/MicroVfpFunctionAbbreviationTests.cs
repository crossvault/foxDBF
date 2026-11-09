using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// VFP9 accepts an abbreviation of a built-in function name: <c>SUBS()</c> is SUBSTR(), <c>RECC()</c> is
/// RECCOUNT(). Four letters at least; when a prefix fits several functions the older one wins (SUBS is SUBSTR,
/// not SUBSTRC; MESS is MESSAGE, not MESSAGEBOX); some later functions need 10 letters or the full name. The
/// engine resolves an abbreviation before dispatch, so it works in microVFP, in SQL and without a
/// host. SCATTER NAME / GATHER NAME (record objects) are not modelled and raise error 36 instead of doing nothing.
/// Expected values are VFP9's (pinned by the internal MicroVfpFunctionAbbreviationVfp9OracleTests run).
/// </summary>
public sealed class MicroVfpFunctionAbbreviationTests : IDisposable
{
    private readonly MicroVfpTestSupport.TempDir _dir = new("fn_abbrev");
    private readonly VfpSession _session;
    private readonly VfpInterpreter _i;

    public MicroVfpFunctionAbbreviationTests()
    {
        using (var w = DbfWriter.Create(_dir.File("ct.dbf"), new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 5) }))
        {
            w.AppendRecord(1, "a");
            w.AppendRecord(2, "b");
            w.Flush();
        }
        _session = new VfpSession();
        _session.OpenDirectory(_dir.Path);
        _i = new VfpInterpreter(_session);
    }

    public void Dispose()
    {
        _i.Dispose();
        _session.Dispose();
        _dir.Dispose();
    }

    private string Show(string expression)
    {
        _i.Execute("lcResult = TRANSFORM(" + expression + ")");
        return _i.EvalExpression("lcResult").AsString;
    }

    // ─────────────────────────── microVFP ───────────────────────────

    [Theory]
    [InlineData("SUBS('abcdef', 2, 3)", "bcd")]
    [InlineData("SUBST('abcdef', 2, 3)", "bcd")]
    [InlineData("subs('abc', 2)", "bc")]                       // case-insensitive
    [InlineData("UPPE('ab')", "AB")]
    [InlineData("TRAN(5)", "5")]
    [InlineData("TRANSF(12.5, '999.99')", " 12.50")]
    [InlineData("RIGH('abc', 1)", "c")]                        // RIGHT, not RIGHTC
    [InlineData("STRT('aXb', 'X', 'Y')", "aYb")]              // STRTRAN, not STRTOFILE
    [InlineData("STUF('abc', 2, 1, 'Z')", "aZc")]             // STUFF, not STUFFC
    [InlineData("CHRT('abc', 'b', 'X')", "aXc")]              // CHRTRAN, not CHRTRANC
    [InlineData("ALLT('  a  ')", "a")]
    [InlineData("ICAS(.F., 1, .T., 2)", "2")]                 // the lazy special form
    [InlineData("SUBS(.NULL., 2)", ".NULL.")]                 // .NULL. propagates like SUBSTR's
    [InlineData("ISNU(.NULL.)", ".T.")]                       // ISNULL sees the .NULL. itself
    [InlineData("EVALUATE(\"SUBS('abc', 2)\")", "bc")]
    [InlineData("TYPE(\"SUBS('abc', 2)\")", "C")]
    [InlineData("LEFTC('abc', 1)", "a")]                      // a full name stays itself
    public void Abbreviation_CallsTheFunctionItNames(string expression, string expected)
        => Assert.Equal(expected, Show(expression));

    [Fact]
    public void Abbreviation_WorksOnTheCurrentTable_InAMacro_AndInEmbeddedSql()
    {
        _i.Execute("USE ct\nGO 2");
        Assert.Equal("2", Show("RECC()"));
        Assert.Equal("2", Show("RECN()"));
        _i.Execute("lc = \"SUBS('abc', 2)\"\nlcMacro = &lc");
        Assert.Equal("bc", _i.EvalExpression("lcMacro").AsString);

        _i.Execute("SELECT SUBS(nm, 1, 1) AS c FROM ct WHERE UPPE(nm) = 'A' INTO CURSOR qq");
        Assert.Equal("1|a", Show("RECCOUNT('qq')") + "|" + _i.EvalExpression("qq.c").AsString);

        _i.Execute("SELECT ct\nSET FILTER TO SUBS(nm, 1, 1) = 'b'\nGO TOP");
        Assert.Equal("2", Show("id"));
    }

    [Fact]
    public void Abbreviation_OfAnArrayFunction_PassesTheArrayByName()
    {
        _i.Execute("USE ct\nlnF = AFIE(laF)\nlnL = ALIN(laL, 'x,y,z', ',')");
        Assert.Equal("2|ID|3|z", Show("lnF") + "|" + Show("laF(1,1)") + "|" + Show("lnL") + "|" + Show("laL(3)"));
    }

    [Theory]
    [InlineData("x = SUB('abc', 2)", "sub")]                   // 3 letters call a user function
    [InlineData("x = SUBSTRX('abc', 2)", "substrx")]           // so does a name longer than the function
    [InlineData("x = JUSTS('c:\\a\\b.txt')", "justs")]         // JUSTSTEM cannot be abbreviated
    [InlineData("x = GETWORDC('a b c')", "getwordc")]          // GETWORDCOUNT needs 10 letters
    public void NotAnAbbreviation_IsAnUnknownFunction(string source, string name)
    {
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute(source));
        Assert.Equal(1, ex.VfpErrorNumber);
        Assert.Equal($"File '{name}.prg' does not exist.", ex.Message);
    }

    [Fact]
    public void Abbreviation_OfAFunctionMicroVfpLacks_ReportsTheFullName()
    {
        // CREA is CREATEOBJECT (not CREATEBINARY or CREATEOFFLINE), which microVFP does not model.
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute("o = CREA('Empty')"));
        Assert.Equal(1, ex.VfpErrorNumber);
        Assert.Equal("File 'createobject.prg' does not exist.", ex.Message);
    }

    [Fact]
    public void UserProcedure_NamedLikeAnAbbreviation_DoesNotOverrideBuiltin()
    {
        // VFP9's compiler resolves SUBST to SUBSTR even when a procedure SUBST exists.
        _i.Execute("PROCEDURE subst(a, b)\nRETURN 'udf'\nENDPROC");
        Assert.Equal("bcdef", Show("SUBST('abcdef', 2)"));
    }

    // ─────────────────────────── the engine, without a host ───────────────────────────

    [Theory]
    [InlineData("SUBS('abcdef', 2, 3)", "bcd")]
    [InlineData("ICAS(.F., 'x', .T., 'y')", "y")]
    [InlineData("UPPE(SUBS('abc', 2))", "BC")]
    public void Engine_ResolvesAbbreviations_InterpretedAndCompiled(string expression, string expected)
    {
        var parsed = VfpExpression.Parse(expression);
        Assert.Equal(expected, parsed.Evaluate(TestRow.Empty).AsString);
        Assert.Equal(expected, parsed.Compile()(TestRow.Empty).AsString);
    }

    [Fact]
    public void Engine_ResolvesAbbreviations_InTypeInference_AndLeavesUnknownNamesNull()
    {
        var schema = new TestSchema().Add("nm", 'C', 5);
        Assert.Equal(VfpType.Character, VfpExpression.Parse("UPPE(nm)").InferType(schema).Type);
        Assert.Equal(VfpType.Numeric, VfpExpression.Parse("RECC()").InferType(schema).Type);
        Assert.True(VfpExpression.Parse("SUB('abc', 2)").Evaluate(TestRow.Empty).IsNull);
    }

    // ─────────────────────────── the table ───────────────────────────

    [Fact]
    public void Table_ResolvesOnce_AndFollowsVfp9sRules()
    {
        var all = VfpFunctionAbbreviations.All;
        // Expanding never yields another abbreviation, so the unknown-function path cannot loop.
        Assert.DoesNotContain(all.Values, all.ContainsKey);
        // No name the engine dispatches itself is an abbreviation of something else.
        Assert.DoesNotContain(VfpRuntime.EngineFunctionNames, all.ContainsKey);
        Assert.All(all.Keys, k => Assert.True(k.Length >= 4 || k == "CDX", k));

        Assert.Equal("SUBSTR", all["SUBS"]);
        Assert.Equal("MESSAGE", all["MESS"]);
        Assert.Equal("MESSAGEBOX", all["MESSAGEB"]);
        Assert.Equal("CURSORGETPROP", all["CURS"]);
        Assert.Equal("STRTOFILE", all["STRTO"]);
        Assert.Equal("GETWORDCOUNT", all["GETWORDCOU"]);
        Assert.Equal("FIELD", all["FIELDS"]);
        Assert.Equal("TYPE", all["TYPEAH"]);
        Assert.DoesNotContain("GETWORDC", all.Keys);
        Assert.DoesNotContain("SUBSTRC", all.Keys);
        Assert.DoesNotContain(all.Keys, k => k.StartsWith("JUST", StringComparison.Ordinal));
    }

    // ─────────────────────────── SCATTER / GATHER NAME ───────────────────────────

    [Theory]
    [InlineData("SCATTER NAME oRec")]
    [InlineData("SCATTER NAME oRec FIELDS id MEMO")]
    [InlineData("SCATTER NAME oRec ADDITIVE")]
    [InlineData("SCATTER NAME oRec BLANK")]
    [InlineData("GATHER NAME oRec")]
    [InlineData("GATHER NAME oRec FIELDS nm")]
    public void ScatterGatherName_RaisesError36(string command)
    {
        _i.Execute("USE ct");
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute(command));
        Assert.Equal(36, ex.VfpErrorNumber);
        Assert.Equal("Command contains unrecognized phrase/keyword.", ex.Message);
        Assert.Equal(command, ex.Statement);
    }

    [Fact]
    public void ScatterName_IsCatchable_AndLeavesTheOtherFormsWorking()
    {
        _i.Execute("""
            USE ct
            GO 2
            lnErr = 0
            TRY
              SCATTER NAME oRec
            CATCH TO oE
              lnErr = oE.ErrorNo
            ENDTRY
            SCATTER TO laRow
            SCATTER MEMVAR
            """);
        Assert.Equal("36|U|2|b|2", Show("lnErr") + "|" + _i.EvalExpression("TYPE('oRec')").AsString + "|"
            + Show("laRow(1)") + "|" + _i.EvalExpression("ALLTRIM(laRow(2))").AsString + "|" + Show("m.id"));
    }
}
