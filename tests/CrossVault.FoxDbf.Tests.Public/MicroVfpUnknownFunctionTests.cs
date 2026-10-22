using System.Text.RegularExpressions;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// BREAKING (0.11): a call of a function that neither microVFP nor the expression engine implements raises VFP
/// error 1 "File 'name.prg' does not exist." (VFP9 looks for a program file of that name) instead of evaluating
/// silently to .NULL. — also with a .NULL. argument, through EVALUATE() and a macro. AND/OR short-circuit like
/// VFP9, so `.F. AND NoSuchFn()` stays .F. Without a microVFP host (SQL / ADO.NET / index expressions) an unknown
/// function still yields .NULL. FILE() is implemented. Expected values are VFP9's (pinned by the internal
/// MicroVfpUnknownFunctionVfp9OracleTests run).
/// </summary>
public sealed class MicroVfpUnknownFunctionTests : IDisposable
{
    private readonly MicroVfpTestSupport.TempDir _dir = new("unknown_fn");
    private readonly VfpSession _session;
    private readonly VfpInterpreter _i;

    public MicroVfpUnknownFunctionTests()
    {
        File.WriteAllText(_dir.File("Exist.TXT"), "x");
        Directory.CreateDirectory(_dir.File("sub"));
        File.WriteAllText(Path.Combine(_dir.File("sub"), "inner.txt"), "y");
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

    private MicroVfpRuntimeException Raises(string source)
        => Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute(source));

    private string Str(string expression)
    {
        _i.Execute("lcResult = " + expression);
        return _i.EvalExpression("lcResult").AsString;
    }

    // ─────────────────────────── unknown function ⇒ error 1 ───────────────────────────

    [Theory]
    [InlineData("x = NoSuchFn(1)", "nosuchfn")]
    [InlineData("x = NoSuchFn(.NULL.)", "nosuchfn")]               // a .NULL. argument does not hide it
    [InlineData("=NoSuchFn()", "nosuchfn")]
    [InlineData("IF NoSuchFn(1, 'a') > 0\n  x = 1\nENDIF", "nosuchfn")]
    [InlineData("x = 1 + NOSUCH_LONG_FUNCTION_NAME_XY(2)", "nosuch_long_function_name_xy")]   // never truncated
    [InlineData("x = EVALUATE('NoSuchFn(1)')", "nosuchfn")]
    [InlineData("lc = 'NoSuchFn'\nx = &lc.(1)", "nosuchfn")]
    [InlineData("x = 5\ny = x(1)", "x")]                             // a scalar is not an array: a call
    [InlineData("x = SUB('abc', 2)", "sub")]                         // 3 letters are not an abbreviation
    [InlineData("x = BLANK()", "blank")]                             // not a VFP9 function either
    public void UnknownFunction_Raises1(string source, string name)
    {
        var ex = Raises(source);
        Assert.Equal(1, ex.VfpErrorNumber);
        Assert.Equal($"File '{name}.prg' does not exist.", ex.Message);
        Assert.Equal(name + ".prg", ex.Details);
    }

    [Theory]
    [InlineData("y = oErr.nosuch(1)", 1734, "Property NOSUCH is not found.", "NOSUCH")]
    [InlineData("y = loUndef.foo(1)", 1923, "Object LOUNDEF is not found.", "LOUNDEF")]
    [InlineData("lnX = 5\ny = lnX.foo(1)", 1924, "LNX is not an object.", "LNX")]
    public void UnknownMethodCall_RaisesTheObjectError(string source, int error, string message, string details)
    {
        _i.Execute("TRY\n  x = NoSuchFn(1)\nCATCH TO oErr\nENDTRY");
        var ex = Raises(source);
        Assert.Equal(error, ex.VfpErrorNumber);
        Assert.Equal(message, ex.Message);
        Assert.Equal(details, ex.Details);
    }

    [Fact]
    public void ExceptionProperty_CalledWithASubscript_IsThePropertyValue()
    {
        _i.Execute("TRY\n  x = NoSuchFn(1)\nCATCH TO oErr\nENDTRY\ny = oErr.message(1)\nz = oErr.errorno(2)");
        Assert.Equal("File 'nosuchfn.prg' does not exist.", _i.EvalExpression("y").AsString);
        Assert.Equal(1m, _i.EvalExpression("z").AsNumber);
    }

    [Fact]
    public void UnknownFunction_IsCaughtByTry_WithDetailsAndAerror()
    {
        _i.Execute("""
            DIMENSION laE[1]
            TRY
              x = NoSuchFn(1)
            CATCH TO oE
              lcCaught = TRANSFORM(oE.ErrorNo) + '|' + oE.Message + '|' + oE.Details
              =AERROR(laE)
              lcAerror = TRANSFORM(laE[1]) + '|' + laE[3]
            ENDTRY
            """);
        Assert.Equal("1|File 'nosuchfn.prg' does not exist.|nosuchfn.prg", _i.EvalExpression("lcCaught").AsString);
        Assert.Equal("1|nosuchfn.prg", _i.EvalExpression("lcAerror").AsString);
    }

    [Fact]
    public void UnknownFunction_IsTrappedByOnError()
    {
        _i.Execute("n = 0\nm = ''\nON ERROR DO Trap\nx = NoSuchFn(3)\nON ERROR\n" +
                   "PROCEDURE Trap\n  n = ERROR()\n  m = MESSAGE()\nENDPROC");
        Assert.Equal(1m, _i.EvalExpression("n").AsNumber);
        Assert.Equal("File 'nosuchfn.prg' does not exist.", _i.EvalExpression("m").AsString);
    }

    [Fact]
    public void KnownFunctions_StillPropagateNull()
    {
        _i.Execute("x = UPPER(.NULL.)\ny = SUBSTR(.NULL., 1)\nz = ISNULL(.NULL.)");
        Assert.True(_i.EvalExpression("x").IsNull);
        Assert.True(_i.EvalExpression("y").IsNull);
        Assert.True(_i.EvalExpression("z").AsLogical);
    }

    [Fact]
    public void UserFunction_ShadowsNothing_AndStillWorks()
    {
        _i.Execute("x = MyFn(2)\nFUNCTION MyFn(n)\nRETURN n * 3\nENDFUNC");
        Assert.Equal(6m, _i.EvalExpression("x").AsNumber);
    }

    [Fact]
    public void Lenc_IsLen()
        => Assert.Equal("4|0|.NULL.", Str("TRANSFORM(LENC('abc ')) + '|' + TRANSFORM(LENC('')) + '|' + TRANSFORM(LENC(.NULL.))"));

    // ─────────────────────────── TYPE() ───────────────────────────

    [Theory]
    [InlineData("NoSuchFn(1)", "U")]
    [InlineData("FILE('x')", "L")]                    // a built-in reports its result type
    [InlineData("lF AND NoSuchFn(1)", "L")]           // short-circuit: the call is never made
    [InlineData("gaUndefined(1)", "U")]               // undefined array
    [InlineData("gaDefined(1)", "L")]                 // defined array element (still .F.)
    public void Type_ReportsUForErrors(string expression, string expected)
    {
        _i.Execute("lF = .F.\nDIMENSION gaDefined(2)");
        Assert.Equal(expected, _i.EvalExpression($"TYPE(\"{expression}\")").AsString);
    }

    // ─────────────────────────── AND / OR short-circuit ───────────────────────────

    [Theory]
    [InlineData("lF AND NoSuchFn(1)", ".F.")]
    [InlineData("lT OR NoSuchFn(1)", ".T.")]
    [InlineData("NOT lT AND NoSuchFn(1)", ".F.")]
    [InlineData("lF AND 1", ".F.")]
    [InlineData("lF AND lN", ".F.")]
    [InlineData("lN AND lF", ".F.")]
    [InlineData("lT OR lN", ".T.")]
    [InlineData("lN OR lT", ".T.")]
    [InlineData("lF AND lT OR lT", ".T.")]
    public void AndOr_ShortCircuit(string expression, string expected)
    {
        _i.Execute("lF = .F.\nlT = .T.\nlN = .NULL.");
        Assert.Equal(expected, Str($"TRANSFORM({expression})"));
    }

    [Theory]
    [InlineData("x = lN AND NoSuchFn(1)")]       // .NULL. does not decide
    [InlineData("x = lN OR NoSuchFn(1)")]
    [InlineData("x = lT AND NoSuchFn(1)")]
    [InlineData("x = lF OR NoSuchFn(1)")]
    public void AndOr_EvaluateTheRightOperand_WhenTheLeftDoesNotDecide(string source)
    {
        _i.Execute("lF = .F.\nlT = .T.\nlN = .NULL.");
        Assert.Equal(1, Raises(source).VfpErrorNumber);
    }

    [Fact]
    public void AndOr_DoNotCallAUdfOnTheRight_WhenTheLeftDecides()
    {
        _i.Execute("""
            PUBLIC gnCalls
            gnCalls = 0
            x = .F. AND Bump()
            y = .T. OR Bump()
            z = .T. AND Bump()
            w = IIF(.F. AND Bump(), 1, 2)
            FUNCTION Bump
            gnCalls = gnCalls + 1
            RETURN .T.
            ENDFUNC
            """);
        Assert.Equal(1m, _i.EvalExpression("gnCalls").AsNumber);
    }

    // ─────────────────────────── FILE() ───────────────────────────

    [Theory]
    [InlineData("FILE('exist.txt')", ".T.")]
    [InlineData("FILE('EXIST.TXT')", ".T.")]            // case-insensitive, also on a case-sensitive file system
    [InlineData("FILE('nope.txt')", ".F.")]
    [InlineData("FILE('')", ".F.")]
    [InlineData("FILE('  exist.txt  ')", ".T.")]        // blanks are trimmed
    [InlineData("FILE('*.txt')", ".F.")]                // no wildcards
    [InlineData("FILE('exis?.txt')", ".F.")]
    [InlineData("FILE('sub\\inner.txt')", ".T.")]       // a relative path below the data directory
    [InlineData("FILE('inner.txt')", ".F.")]            // sub-directories are not searched
    [InlineData("FILE('sub')", ".F.")]                  // a directory is not a file
    [InlineData("FILE('exist')", ".F.")]                // no default extension
    [InlineData("FILE('exist.txt', 1)", ".T.")]         // nFlags is accepted
    [InlineData("VARTYPE(FILE('exist.txt'))", "L")]
    public void File_MatchesVfp9(string expression, string expected)
        => Assert.Equal(expected, Str($"TRANSFORM({expression})"));

    [Fact]
    public void File_AcceptsAnAbsolutePath()
    {
        _i.Execute($"x = FILE('{_dir.File("Exist.TXT")}')");
        Assert.True(_i.EvalExpression("x").AsLogical);
    }

    [Fact]
    public void File_DrivesAnIf()
    {
        _i.Execute("n = 0\nIF FILE('exist.txt')\n  n = 1\nENDIF\nIF FILE('nope.txt')\n  n = n + 10\nENDIF");
        Assert.Equal(1m, _i.EvalExpression("n").AsNumber);
    }

    [Theory]
    [InlineData("x = FILE(.NULL.)", 11)]
    [InlineData("x = FILE(123)", 11)]
    [InlineData("x = FILE()", 1229)]
    public void File_BadArguments_Raise(string source, int error)
        => Assert.Equal(error, Raises(source).VfpErrorNumber);

    // ─────────────────────────── the engine without a microVFP host is unchanged ───────────────────────────

    [Theory]
    [InlineData("NOSUCHFN(1)")]
    [InlineData("NOSUCHFN(.NULL.)")]
    [InlineData("FILE('x')")]                           // FILE() is a microVFP function, not an engine one
    public void EngineWithoutHost_UnknownFunctionIsNull(string expression)
    {
        var e = VfpExpression.Parse(expression);
        Assert.True(e.Evaluate(TestRow.Empty, EvaluationContext.Default).IsNull);
        Assert.True(e.Compile(EvaluationContext.Default)(TestRow.Empty).IsNull);
    }

    [Fact]
    public void HostWithoutOverride_KeepsTheNullDefault()
    {
        var host = new CountingHost();
        Assert.True(VfpExpression.Parse("NOSUCHFN(1)").Evaluate(host, EvaluationContext.Default).IsNull);
    }

    [Theory]
    [InlineData(".F. AND BUMP()", false, 0)]
    [InlineData(".T. OR BUMP()", true, 0)]
    [InlineData(".T. AND BUMP()", true, 1)]
    [InlineData(".F. OR BUMP()", true, 1)]
    [InlineData(".NULL. AND BUMP()", null, 1)]
    [InlineData(".F. AND .F. AND BUMP()", false, 0)]
    public void Engine_AndOr_ShortCircuit_InterpretedAndCompiled(string expression, bool? expected, int calls)
    {
        var e = VfpExpression.Parse(expression);
        var interpreted = new CountingHost();
        var r1 = e.Evaluate(interpreted, EvaluationContext.Default);
        var compiled = new CountingHost();
        var r2 = e.Compile(EvaluationContext.Default)(compiled);
        foreach (var (r, host) in new[] { (r1, interpreted), (r2, compiled) })
        {
            if (expected is null) Assert.True(r.IsNull);
            else Assert.Equal(expected.Value, r.AsLogical);
            Assert.Equal(calls, host.Calls);
        }
    }

    /// <summary>A host that owns BUMP() (counts calls, returns .T.) and keeps the default unknown-function
    /// behaviour.</summary>
    private sealed class CountingHost : IRowContext, IVfpFunctionHost
    {
        public int Calls { get; private set; }
        public object? GetField(string name) => null;
        public int RecNo => 1;
        public bool Deleted => false;
        public int RecCount => 0;

        public bool TryInvoke(string upperName, VfpValue[] args, EvaluationContext ctx, out VfpValue result)
        {
            if (upperName == "BUMP") { Calls++; result = VfpValue.Logical(true); return true; }
            result = VfpValue.Null;
            return false;
        }
    }

    // ─────────────────────────── guard: the .NULL.-argument name list stays in step ───────────────────────────

    /// <summary>A .NULL. argument short-circuits before the dispatch switch, so the engine decides "known or not"
    /// from <c>VfpRuntime.EngineFunctionNames</c>. A name added to a switch but not to the set would raise error 1
    /// for a .NULL. argument — this scans the source's <c>case</c> labels against the set.</summary>
    [Fact]
    public void EngineFunctionNames_MatchTheDispatchSwitches()
    {
        string src = File.ReadAllText(FindSource("CrossVault.FoxDbf.Expressions", "VfpRuntime.cs"));
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (string method in new[] { "public static VfpValue CallFunction(", "private static VfpValue CallStringExtras(" })
        {
            int start = src.IndexOf(method, StringComparison.Ordinal);
            Assert.True(start >= 0, method);
            int end = src.IndexOf("\n    }", start, StringComparison.Ordinal);
            foreach (Match m in Regex.Matches(src[start..end], "case \"([A-Z_]+)\""))
                labels.Add(m.Groups[1].Value);
        }
        labels.Add("ICASE");   // a lazy special form in the AST, never dispatched by name
        Assert.Equal(labels.OrderBy(n => n, StringComparer.Ordinal),
            VfpRuntime.EngineFunctionNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static string FindSource(string project, string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (string root in new[] { "src", Path.Combine("public", "src") })
            {
                string candidate = Path.Combine(dir.FullName, root, project, file);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException($"{project}/{file} not found above {AppContext.BaseDirectory}");
    }
}
