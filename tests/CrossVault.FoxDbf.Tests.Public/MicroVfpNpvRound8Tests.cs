using System.Runtime.CompilerServices;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class MicroVfpNpvRound8Tests
{
    internal sealed record Probe(string Name, string Body);
    internal const string Setup = "CREATE TABLE t FREE (n N(10,2), ii I, bb B, yy Y, cc C(8), ll L)\nINSERT INTO t VALUES (1,1,1,1,'text',.T.)";
    internal static Probe[] Matrix(int decimals)
    {
        var probes = new List<Probe>();
        void Add(string name, string expression, string setup = "")
        {
            string prefix = $"SET DECIMALS TO {decimals}\nx=1\n{setup}\n";
            probes.Add(new(name, prefix + "result=TRANSFORM(" + expression + ")"));
            probes.Add(new(name + "/stored", prefix + "v=" + expression + "\nresult=TRANSFORM(v)"));
        }
        foreach (string operand in new[] { "1", "x", "n", "ii", "bb", "yy" })
        {
            foreach (string producer in new[] { "{0}", "({0}+1)", "({0}-1)", "({0}-12345)", "({0}*2)", "({0}/2)", "({0}%2)", "INT({0})", "ROUND({0},1)", "ABS({0})", "-{0}", "+{0}" })
            {
                string expression = string.Format(producer, operand) + "/0";
                Add(expression, expression);
            }
        }
        foreach (string expression in new[] { "INT(1.23)/0", "ROUND(1.23,1)/0", "ABS(-1.23)/0", "0/0", "5/0", "1.00/0", "100/0", "1/0.00", "-(1/0)", "-(-1/0)", "1/0+1", "1/0-1", "(1/0)*2", "-(x/0)", "(x/0)*2", "EXP(1000)", "10^400", "-(10^400)" })
            Add(expression, expression);
        foreach (bool flag in new[] { false, true })
        {
            string logical = flag ? ".T." : ".F.";
            foreach (string function in new[] { "IIF", "ICASE" })
                Add(function + logical, $"{function}(flag,1,1234567890)/0", "flag=" + logical);
            Add("selected-field" + logical, "IIF(flag,n,ii)/0", "flag=" + logical);
            Add("selected-scale" + logical, "IIF(flag,1.00,12345.6789)/0", "flag=" + logical);
            Add("NVL" + logical, $"NVL({(flag ? "1" : ".NULL.")},1234567890)/0");
            Add("EVL" + logical, $"EVL({(flag ? "1" : "0")},1234567890)/0");
        }
        foreach (string expression in new[] { "TRANSFORM(1/0,'')", "TRANSFORM(0/0,'')", "TRANSFORM(1/0,'999.99')", "TRANSFORM((1/0))" })
            probes.Add(new(expression, $"SET DECIMALS TO {decimals}\nresult={expression}"));
        probes.Add(new("stateful-false", $"SET DECIMALS TO {decimals}\n=RAND(1)\nresult=TRANSFORM(IIF(RAND()<0,1,1234567890)/0)+'|'+ALLTRIM(STR(RAND(),30,10))"));
        probes.Add(new("stateful-true", $"SET DECIMALS TO {decimals}\n=RAND(1)\nresult=TRANSFORM(IIF(RAND()>0,1,2)/0)+'|'+ALLTRIM(STR(RAND(),30,10))"));
        return probes.ToArray();
    }

    internal static readonly string[] DateFunctions = ["DTOC", "DTOS", "TTOC", "TTOD", "QUARTER", "WEEK", "DMY", "MDY", "DAY", "MONTH", "YEAR", "DOW", "CDOW", "CMONTH", "DTOT", "HOUR", "MINUTE", "SEC"];
    internal static Probe[] DateArguments() => (from function in DateFunctions
        from argument in new[] { "1", "1/0", "'text'", ".T.", ".F.", "n", "ii", "bb", "yy", "cc", "ll" }
        let expression = function + "(" + argument + ")"
        select new Probe(expression, "result=TRANSFORM(" + expression + ")")).ToArray();

    internal static Probe[] DateKinds() => (from function in DateFunctions
        from argument in new[] { "{^2024-02-03}", "{^2024-02-03 14:15:16}", "{}", "{/:}", ".NULL." }
        let expression = function + "(" + argument + ")"
        select new Probe(expression, "v=" + expression + "\nresult=IIF(ISNULL(v),'NULL','OK')")).ToArray();

    public static IEnumerable<object[]> OverflowCases => from decimals in new[] { 0, 2, 4 }
        from probe in Matrix(decimals)
        where Pins.ContainsKey(decimals + "/" + probe.Name)
        select new object[] { decimals, probe.Name };

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void TransformOverflow_MatchesExpectedVfp(int decimals, string name)
    {
        var probe = Matrix(decimals).Single(p => p.Name == name);
        Check(probe, Pins[decimals + "/" + name]);
    }

    public static IEnumerable<object[]> DateCases => DateArguments().Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(DateCases))]
    public void DateFunctions_RejectNonDateArguments(string name)
        => Check(DateArguments().Single(p => p.Name == name), "ERR|11");

    private static void Check(Probe probe, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round8");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup);
        vfp.Execute(MicroVfpNpvRound6Tests.Catch(probe.Body));
        Assert.Equal(expected, vfp.Memory.Get("result").AsString);
    }

    public static IEnumerable<object[]> DateKindCases => DateKinds().Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(DateKindCases))]
    public void DateFunctions_AcceptOnlyVfpDateKinds(string name)
        => Check(DateKinds().Single(p => p.Name == name), DateKindPins[name]);

    [Fact]
    public void NonDatePayload_IsNotAnEmptyDate()
    {
        foreach (var value in new[] { VfpValue.Number(1m), VfpValue.Number(double.PositiveInfinity), VfpValue.Integer(1), VfpValue.Currency(1), VfpValue.Character("text"), VfpValue.Logical(false), VfpValue.Logical(true), VfpValue.Null })
        {
            Assert.False(value.IsEmptyDate);
            Assert.Equal(default, value.AsDate);
            Assert.Equal(default, value.AsDateTime);
        }
        Assert.True(VfpValue.Date(default).IsEmptyDate);
        Assert.True(VfpValue.DateTime(default).IsEmptyDate);
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("resolver")]
    [InlineData("compiled")]
    [InlineData("aot")]
    public void ExpressionModes_MatchExpectedVfp(string mode)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round8modes");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup);
        var row = new ProbeRow(vfp);
        var failures = new List<string>();
        foreach (int decimals in new[] { 0, 2, 4, -1, -2 })
        {
            var probes = decimals == -2 ? DateKinds() : decimals == -1 ? DateArguments() : Matrix(decimals);
            foreach (var probe in probes)
            {
                string expected;
                if (decimals == -2) expected = DateKindPins[probe.Name];
                else if (decimals == -1) expected = "ERR|11";
                else if (!Pins.TryGetValue(decimals + "/" + probe.Name, out expected!)) continue;
                // The final assignment is evaluated through the independent engine path.
                // For the date-kind probes, evaluate their first assignment and retain only type acceptance.
                int start = probe.Body.LastIndexOf("result=", StringComparison.Ordinal);
                string source = probe.Body[(start + 7)..];
                if (decimals == -2) source = probe.Body[2..probe.Body.IndexOf('\n')];
                string actual;
                try
                {
                    if (decimals != -2) vfp.Execute(probe.Body[..start]);
                    var expression = VfpExpression.Parse(source);
                    var value = mode switch
                    {
                        "tree" => expression.Evaluate(row, session.Context),
                        "resolver" => expression.EvaluateWithFunctionResolver(row, session.Context, Unresolved),
                        _ => expression.Compile(session.Context, preferInterpretation: mode == "aot")(row)
                    };
                    actual = decimals == -2 ? value.IsNull ? "NULL" : "OK" : value.AsString;
                }
                catch (ExpressionException error) { actual = "ERR|" + error.VfpErrorNumber; }
                catch (MicroVfpRuntimeException error) { actual = "ERR|" + error.VfpErrorNumber; }
                if (expected != actual) failures.Add(probe.Name + ": expected=" + expected + "; actual=" + actual);
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static bool Unresolved(string name, IReadOnlyList<ExpressionArgument> args, out VfpValue value)
    {
        value = default;
        return false;
    }

    // Only TRANSFORM needs the host formatter; date guards run in the shared engine.
    private sealed class ProbeRow(VfpInterpreter vfp) : IRowContext, IVfpValueRow, IVfpFunctionHost
    {
        public VfpValue GetValue(string name) => vfp.ResolveName(name);
        public bool TryInvoke(string name, VfpValue[] args, EvaluationContext context, out VfpValue result)
        {
            result = default;
            return name == "TRANSFORM" && vfp.HostInvoke(name, args, context, out result);
        }
        public object? GetField(string name) => GetValue(name);
        public int RecNo => 1;
        public bool Deleted => false;
        public int RecCount => 1;
    }

    [Fact]
    public void NumericValues_StayCompactAndAllocationFree()
    {
        Assert.Equal(48, Unsafe.SizeOf<VfpValue>());
        var evaluate = VfpExpression.Parse("(1+2)*3/0").Compile();
        for (int i = 0; i < 128; i++) evaluate(null!);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) evaluate(null!);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
