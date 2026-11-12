using System.Runtime.CompilerServices;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class MicroVfpNpvRound8Tests
{
    internal sealed record Probe(string Name, string Body);
    internal const string Setup = "CREATE TABLE t FREE (n N(10,2), ii I, bb B, yy Y, cc C(8), ll L, s N(5,0), h N(12,4))\nINSERT INTO t VALUES (1,1,1,1,'text',.T.,1,1)";
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

    internal static Probe[] Extensions(int decimals)
    {
        var probes = new List<Probe>();
        foreach (string operand in new[] { "s", "h", "x", "z", "q" })
        foreach (string producer in new[] { "{0}", "({0}+1)", "({0}-12345)", "({0}*2)", "({0}/2)", "({0}%2)", "INT({0})", "ROUND({0},1)", "ABS({0})", "-{0}", "(({0}+1)*2)", "INT({0}+1)", "ROUND({0}*2,3)" })
        {
            string expression = string.Format(producer, operand) + "/0";
            string prefix = $"SET DECIMALS TO {decimals}\nx=12345\nz=1.5\nq=12345.6789\n";
            probes.Add(new(expression, prefix + "result=TRANSFORM(" + expression + ")"));
            probes.Add(new(expression + "/stored", prefix + "v=" + expression + "\nresult=TRANSFORM(v)"));
        }
        foreach (string expression in new[] { "(n+1.5)/0", "(n*1.5)/0", "(n/1.5)/0", "(1.5+n)/0", "(1.5-n)/0", "ROUND(n,0)/0", "ROUND(n,4)/0", "INT(n+1)/0", "ABS(1.00)/0", "ABS(12345)/0", "ABS(1.5)/0", "(IIF(RAND()>0,n,ii)+1)/0", "IIF(RAND()>0,n+1,ii*2)/0", "ICASE(RAND()<0,1,RAND()>0,h,ii)/0", "NVL(.NULL.,n)/0", "EVL(0,h)/0", "IIF(RAND()>0,1,2)/z", "(1-(-12345))/0", "(1-(-1.23))/0", "((0-1)-(-2))/0" })
        {
            string prefix = $"SET DECIMALS TO {decimals}\n=RAND(1)\nz=0\n";
            probes.Add(new(expression, prefix + "result=TRANSFORM(" + expression + ")+ '|' + ALLTRIM(STR(RAND(),30,10))"));
            probes.Add(new(expression + "/stored", prefix + "v=" + expression + "\nresult=TRANSFORM(v)+ '|' + ALLTRIM(STR(RAND(),30,10))"));
        }
        if (decimals == 0)
        foreach (int setting in new[] { 16, 18 })
        foreach (string expression in new[] { "1/0", "n/0", "bb/0" })
        {
            string prefix = $"SET DECIMALS TO {setting}\n";
            probes.Add(new($"D{setting}/" + expression, prefix + "result=TRANSFORM(" + expression + ")"));
            probes.Add(new($"D{setting}/" + expression + "/stored", prefix + "v=" + expression + "\nresult=TRANSFORM(v)"));
        }
        return probes.Select(p => new Probe("extension/" + p.Name, p.Body)).ToArray();
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
        from probe in Matrix(decimals).Concat(Extensions(decimals))
        select new object[] { decimals, probe.Name };

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void TransformOverflow_MatchesExpectedVfp(int decimals, string name)
    {
        var probe = Matrix(decimals).Concat(Extensions(decimals)).Single(p => p.Name == name);
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
            var probes = decimals == -2 ? DateKinds() : decimals == -1 ? DateArguments() : Matrix(decimals).Concat(Extensions(decimals)).ToArray();
            foreach (var probe in probes)
            {
                string expected;
                if (decimals == -2) expected = DateKindPins[probe.Name];
                else if (decimals == -1) expected = "ERR|11";
                else expected = Pins[decimals + "/" + probe.Name];
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
    private sealed class ProbeRow(VfpInterpreter vfp) : IRowContext, IVfpValueRow, IVfpFunctionHost, ISchema
    {
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            var column = vfp.Session.AreaAt(vfp.Session.CurrentArea)?.Table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            type = column?.Type ?? default;
            length = column?.Length ?? 0;
            decimals = column?.Decimal ?? 0;
            return column is not null;
        }
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

    [Theory]
    [InlineData("n*1.5+2", 1)]
    [InlineData("-n/3+n%7", 2)]
    [InlineData("IIF(n>0,n,1)/3", 2)]
    [InlineData("ICASE(.F.,1)/n", 1)]
    public void FiniteArithmetic_DoesNotReadMetadataOrAllocate(string source, int reads)
    {
        var row = new CountingRow();
        var expression = VfpExpression.Parse(source);
        var context = new EvaluationContext();
        foreach (var evaluate in new Func<IRowContext, VfpValue>[] { r => expression.Evaluate(r, context), expression.Compile(context) })
        {
            for (int i = 0; i < 1024; i++) evaluate(row);
            row.Reads = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) evaluate(row);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(1024 * reads, row.Reads);
            Assert.Equal(0, row.MetadataReads);
        }
    }

    private sealed class CountingRow : IRowContext, IVfpValueRow, ISchema
    {
        private readonly VfpValue _value = VfpValue.Number(1m).WithDisplayDecimals(2);
        internal int Reads;
        internal int MetadataReads;
        public VfpValue GetValue(string name) { Reads++; return _value; }
        public object? GetField(string name) => throw new InvalidOperationException("Typed field read expected.");
        public int RecNo => 1;
        public int RecCount => 1;
        public bool Deleted => false;
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            MetadataReads++;
            type = 'N'; length = 10; decimals = 2;
            return true;
        }
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
