using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Data;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class TransformWidthRound3Tests
{
    internal sealed record Probe(string Name, string Body);
    internal const string Setup = MicroVfpNpvRound8Tests.Setup + "\nCREATE TABLE bigints FREE (ii I)\nINSERT INTO bigints VALUES (2000000000)\nINSERT INTO bigints VALUES (2000000000)\nSELECT t\nGO TOP";
    internal const string Functions = "FUNCTION udf(p)\nRETURN p\nENDFUNC\n";
    internal static Probe[] Probes(int decimals)
    {
        var probes = new List<Probe>();
        void Add(string name, string body, string table = "t") => probes.Add(new(name,
            $"IF USED('rr')\nUSE IN rr\nENDIF\nSELECT {table}\nGO TOP\nSET DECIMALS TO {decimals}\nx=1\n" + body));
        foreach (string operand in new[] { "1", "x", "n", "s", "h", "ii", "bb", "yy" })
        foreach (string template in new[] { "{0}/0", "{0}/(n-n)", "INT({0}/0)", "ROUND({0}/0,2)", "ABS({0}/0)", "SQRT({0}/0)", "LOG({0}/0)", "EXP({0}/0)", "MOD({0}/0,2)", "MAX({0}/0,1)", "MIN({0}/0,1)", "IIF(.T.,{0}/0,1)", "NVL(.NULL.,{0}/0)", "EVL(0,{0}/0)" })
        {
            string expression = string.Format(template, operand);
            Add(expression + "/direct", $"result=TRANSFORM({expression})");
            Add(expression + "/stored", $"v={expression}\nresult=TRANSFORM(v)");
            Add(expression + "/str", $"result=STR({expression},20,4)");
            Add(expression + "/sum", $"SUM {expression} TO v\nresult=TRANSFORM(v)");
            foreach (string aggregate in new[] { "SUM", "AVG", "MAX", "MIN" })
                Add(expression + "/calc-" + aggregate, $"CALCULATE {aggregate}({expression}) TO v\nresult=TRANSFORM(v)");
            foreach (string aggregate in new[] { "", "SUM", "AVG", "MAX", "MIN" })
            {
                string item = aggregate == "" ? expression : $"{aggregate}({expression})";
                Add(expression + "/array-" + aggregate, $"SELECT {item} AS val FROM t INTO ARRAY aa\nresult=TRANSFORM(aa[1])");
                Add(expression + "/cursor-" + aggregate, $"SELECT {item} AS val FROM t INTO CURSOR rr\nresult=TRANSFORM(rr.val)\nUSE IN rr");
            }
        }
        foreach (string aggregate in new[] { "SUM", "AVG", "MAX", "MIN", "CNT" })
        {
            Add("udf/" + aggregate, $"CALCULATE {aggregate}(udf(n)/3) TO v\nresult=TRANSFORM(v)");
            Add("compound/" + aggregate, $"CALCULATE {aggregate}((n+1)/(n-1)) TO v\nresult=TRANSFORM(v)");
            Add("sqrt/" + aggregate, $"CALCULATE {aggregate}(SQRT(1.234567)/(n-n)) TO v\nresult=TRANSFORM(v)");
        }
        foreach (string aggregate in new[] { "SUM", "AVG" })
        {
            Add("integer/sql-" + aggregate, $"SELECT {aggregate}(ii) AS val FROM bigints INTO CURSOR rr\nDIMENSION laCols[1]\n=AFIELDS(laCols,'rr')\nresult=TRANSFORM(rr.val)+'|'+laCols[1,2]+'|'+TRANSFORM(laCols[1,3])+'|'+TRANSFORM(laCols[1,4])\nUSE IN rr");
            Add("integer/calc-" + aggregate, $"CALCULATE {aggregate}(ii) TO v\nresult=TRANSFORM(v)", "bigints");
            Add("integer/array-" + aggregate, $"SELECT {aggregate}(ii) AS val FROM bigints INTO ARRAY aa\nresult=TRANSFORM(aa[1])");
        }
        Add("integer/fixture", "SELECT SUM(ii) AS val FROM bigints INTO TABLE intsum\nIF USED('intsum')\nUSE IN intsum\nENDIF\nUSE intsum IN 0\nresult=TRANSFORM(intsum.val)\nUSE IN intsum");
        Add("integer/sum", "SUM ii TO v\nresult=TRANSFORM(v)", "bigints");
        return probes.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void DifferentialCases_MatchExpectedVfp(int decimals)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthround3");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Functions + Setup);
        var failures = new List<string>();
        foreach (var p in Probes(decimals))
        {
            string key = decimals + "/" + p.Name;
            string expected = ExpectedVfp[key];
            string actual = Observe(vfp, p.Body);
            if (actual != expected) failures.Add(key + ": expected=" + expected + "; actual=" + actual);
        }
        Assert.NotEmpty(ExpectedVfp);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData("VAL(STRZERO(n,10))/3", 1)]
    [InlineData("LEN(STRZERO(n,10))/3+n", 2)]
    [InlineData("n/3+LEN(STRZERO(1,3))", 1)]
    [InlineData("udf(n)/3", 1)]
    public void FiniteFunctions_DoNotSampleOverflowMetadata(string source, int reads)
    {
        var expression = VfpExpression.Parse(source);
        var context = new EvaluationContext();
        var row = new FiniteRow();
        foreach (string mode in new[] { "tree", "compiled", "aot", "resolver" })
        {
            var compiled = expression.Compile(context, preferInterpretation: mode == "aot");
            row.Reads = row.MetadataReads = row.Calls = 0;
            VfpValue value = mode switch
            {
                "tree" => expression.Evaluate(row, context),
                "resolver" => expression.EvaluateWithFunctionResolver(row, context, Unresolved),
                _ => compiled(row)
            };
            Assert.True(double.IsFinite(value.AsDouble));
            Assert.Equal(reads, row.Reads);
            Assert.Equal(0, row.MetadataReads);
            Assert.Equal(source.StartsWith("udf") ? 1 : 0, row.Calls);
        }
    }

    private static bool Unresolved(string name, IReadOnlyList<ExpressionArgument> args, out VfpValue value)
    {
        value = default;
        return false;
    }

    private sealed class FiniteRow : IRowContext, IVfpValueRow, IVfpFunctionHost, ISchema
    {
        internal int Reads, MetadataReads, Calls;
        public int RecNo => 1;
        public int RecCount => 1;
        public bool Deleted => false;
        public VfpValue GetValue(string name) { Reads++; return VfpValue.Number(1m); }
        public object? GetField(string name) => GetValue(name);
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            MetadataReads++;
            type = 'N'; length = 10; decimals = 2;
            return true;
        }
        public bool TryInvoke(string name, VfpValue[] args, EvaluationContext context, out VfpValue result)
        {
            result = default;
            if (name != "UDF") return false;
            Calls++;
            result = args[0];
            return true;
        }
    }

    internal static string Observe(VfpInterpreter vfp, string body)
    {
        try
        {
            vfp.Execute(MicroVfpNpvRound6Tests.Catch(body));
            return vfp.Memory.Get("result").AsString;
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    [Theory]
    [InlineData(0, 2, 13, 0, 10)]
    [InlineData(2, 4, 15, 2, 13)]
    [InlineData(4, 6, 17, 4, 15)]
    public void FiniteQuotientSchemas_MatchExpectedVfp(int setting, int fieldScale, int fieldWidth, int constantScale, int constantWidth)
    {
        // Round3FiniteFunctionSchemas_MatchVfp9 records these AFIELDS descriptors.
        using var dir = new MicroVfpTestSupport.TempDir("twidthfinite");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE t (n N(10,2))");
        session.Execute("INSERT INTO t VALUES (1)");
        session.Context.NumericDecimals = setting;
        foreach (var (expression, width, scale) in new[] {
            ("LOG(n/3)", 20, fieldScale), ("EXP(n/3)", fieldWidth, fieldScale),
            ("LOG(1/3)", 20, constantScale), ("EXP(1/3)", constantWidth, constantScale) })
        {
            var column = session.Execute($"SELECT {expression} AS val FROM t")!.Columns.Single();
            Assert.Equal(('N', width, scale), (column.VfpType, column.Length, column.Decimals));
        }
    }

    [Fact]
    public void IntegerSum_SqlAndAdoExposeOverflowWithIntegerSchema()
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthinteger");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE t (ii I)");
        session.Execute("INSERT INTO t VALUES (2000000000)");
        session.Execute("INSERT INTO t VALUES (2000000000)");
        var result = session.Execute("SELECT SUM(ii) FROM t")!;
        Assert.Equal(('I', 4, 0), (result.Columns[0].VfpType, result.Columns[0].Length, result.Columns[0].Decimals));
        Assert.True(double.IsNaN(Assert.IsType<double>(result.Rows.Single()[0])));
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(ii) FROM t";
        Assert.True(double.IsNaN(Assert.IsType<double>(command.ExecuteScalar())));
    }

}
