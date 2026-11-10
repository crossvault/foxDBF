using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNpvRound7Tests
{
    internal sealed record Probe(string Name, string Body);
    internal const string Setup = "CREATE TABLE t FREE (n N(10,2), ii I, bb B, yy Y)\nINSERT INTO t VALUES (1,1,1,1)";
    internal static Probe[] Probes(int decimals)
    {
        var probes = new List<Probe>();
        void Add(string name, string setup, string expression) => probes.Add(new Probe(name,
            $"SET DECIMALS TO {decimals}\nx=1\n{setup}\nresult=TRANSFORM({expression})"));
        Add("memvar", "", "x/0");
        Add("memvar-multiply", "", "(x/0)*2");
        Add("memvar-negate", "", "-(x/0)");
        Add("memvar-stored", "v=x/0", "v");
        Add("literal", "", "1/0");
        Add("literal-stored", "v=1/0", "v");
        Add("literal-multiply", "", "(1/0)*2");
        Add("literal-negate", "", "-(1/0)");
        Add("literal-double-negate", "", "-(-1/0)");
        Add("literal-add", "", "1/0+1");
        Add("literal-subtract", "", "1/0-1");
        foreach (bool flag in new[] { false, true })
        {
            string logical = flag ? ".T." : ".F.";
            foreach (string function in new[] { "IIF", "ICASE" })
                Add(function + logical, "flag=" + logical, $"{function}(flag,1,1234567890)/0");
            Add("selected-field" + logical, "flag=" + logical, "IIF(flag,n,ii)/0");
            Add("selected-scale" + logical, "flag=" + logical, "IIF(flag,1.00,12345.6789)/0");
            Add("NVL" + logical, "", $"NVL({(flag ? "1" : ".NULL.")},1234567890)/0");
            Add("EVL" + logical, "", $"EVL({(flag ? "1" : "0")},1234567890)/0");
        }
        foreach (string field in new[] { "n", "ii", "bb", "yy" })
            Add("field-" + field, "", field + "/0");
        probes.Add(new Probe("stateful-false", $"SET DECIMALS TO {decimals}\n=RAND(1)\nresult=TRANSFORM(IIF(RAND()<0,1,1234567890)/0)+'|'+ALLTRIM(STR(RAND(),30,10))"));
        return probes.ToArray();
    }

    // Captured by MicroVfpNpvRound7Vfp9OracleTests (DECIMALS 0/2/4); negative entries are errors.
    private static readonly int[][] Widths =
    [
        [13, 15, 14, 13, 4, 13, 6, 5, 6, 5, 5, 13, 13, 14, 10, 13, 13, 4, 4, 10, 4, 4, 4, 10, 14, 21, -1988, 13],
        [15, 17, 16, 15, 6, 15, 8, 7, 8, 7, 7, 15, 15, 16, 10, 15, 15, 6, 6, 12, 6, 6, 6, 12, 16, 23, -1988, 15],
        [17, 19, 18, 17, 8, 17, 10, 9, 10, 9, 9, 17, 17, 18, 12, 17, 17, 8, 8, 14, 8, 8, 8, 14, 18, 25, -1988, 17],
    ];
    internal static string Expected(int decimals, int index)
    {
        int width = Widths[decimals / 2][index];
        if (width < 0) return "ERR|" + -width;
        return new string('*', width) + (index == Widths[0].Length - 1 ? "|0.7107791207" : "");
    }

    public static IEnumerable<object[]> GridCases => from decimals in new[] { 0, 2, 4 }
        from mode in new[] { "micro", "tree", "resolver", "compiled", "aot" }
        select new object[] { decimals, mode };

    [Theory]
    [MemberData(nameof(GridCases))]
    public void EvaluatedOverflowWidth_MatchesVfp9Pins(int decimals, string mode)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round7");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup);
        var row = new ProbeRow(vfp);
        var probes = Probes(decimals);
        var failures = new List<string>();
        for (int i = 0; i < probes.Length; i++)
        {
            string actual;
            if (mode == "micro")
            {
                vfp.Execute(MicroVfpNpvRound6Tests.Catch(probes[i].Body));
                actual = vfp.Memory.Get("result").AsString;
            }
            else
            {
                int start = probes[i].Body.LastIndexOf("result=", StringComparison.Ordinal);
                vfp.Execute(probes[i].Body[..start]);
                var expression = VfpExpression.Parse(probes[i].Body[(start + 7)..]);
                try
                {
                    actual = (mode switch
                    {
                        "tree" => expression.Evaluate(row, session.Context),
                        "resolver" => expression.EvaluateWithFunctionResolver(row, session.Context, Unresolved),
                        _ => expression.Compile(session.Context, preferInterpretation: mode == "aot")(row)
                    }).AsString;
                }
                catch (ExpressionException error) { actual = "ERR|" + error.VfpErrorNumber; }
            }
            string expected = Expected(decimals, i);
            if (actual != expected) failures.Add(probes[i].Name + ": expected=" + expected + "; actual=" + actual);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static bool Unresolved(string name, IReadOnlyList<ExpressionArgument> args, out VfpValue value)
    {
        value = default;
        return false;
    }

    private sealed class ProbeRow(VfpInterpreter vfp) : IRowContext, IVfpValueRow, IVfpFunctionHost
    {
        public VfpValue GetValue(string name) => vfp.ResolveName(name);
        public bool TryInvoke(string name, VfpValue[] args, EvaluationContext context, out VfpValue result)
            => vfp.HostInvoke(name, args, context, out result);
        public object? GetField(string name) => GetValue(name);
        public int RecNo => 1;
        public bool Deleted => false;
        public int RecCount => 1;
    }

    [Theory]
    [InlineData("(1+2)*3/4")]
    [InlineData("(1+2)*3/0")]
    public void EvaluatedNumericMetadata_DoesNotAllocate(string source)
    {
        var evaluate = VfpExpression.Parse(source).Compile();
        for (int i = 0; i < 128; i++) evaluate(null!);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) evaluate(null!);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData("x=1", "x/0")]
    [InlineData("flag=.F.", "IIF(flag,1,1234567890)/0")]
    public void Transform_UsesEvaluatedOperandWidth(string setup, string expression)
    {
        using var session = new VfpSession();
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("SET DECIMALS TO 2\n" + setup);
        Assert.Equal(new string('*', 15), vfp.EvalExpression("TRANSFORM(" + expression + ")").AsString);
    }
}
