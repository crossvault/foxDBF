using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class TransformWidthReviewTests
{
    internal sealed record Probe(string Name, string Body);
    internal const string Setup = MicroVfpNpvRound8Tests.Setup + "\nCREATE TABLE a FREE (n N(10,2))\nINSERT INTO a VALUES (1)\nINSERT INTO a VALUES (2)\nSELECT t";
    internal const string Functions = "FUNCTION setx()\nx=replacement\nRETURN .T.\nENDFUNC\n";
    internal static Probe[] Probes(int decimals)
    {
        var probes = new List<Probe>();
        void Add(string name, string expression, string setup = "")
        {
            string prefix = $"SET DECIMALS TO {decimals}\nx=1\n{setup}\n";
            probes.Add(new(name, prefix + "result=TRANSFORM(" + expression + ")"));
            probes.Add(new(name + "/stored", prefix + "v=" + expression + "\nresult=TRANSFORM(v)"));
        }
        foreach (string replacement in new[] { "1.234567", "12345", "1.5" })
        foreach (string expression in new[] { "x/IIF(setx(),0,0)", "(x+1)/IIF(setx(),0,0)", "x*2/IIF(setx(),0,0)", "-x/IIF(setx(),0,0)", "IIF(.T.,x,0)/IIF(setx(),0,0)", "IIF(setx(),x,x)/0" })
            Add(replacement + "/" + expression, expression, "replacement=" + replacement);
        foreach (string field in new[] { "n", "s", "h" })
            Add("nonliteral/" + field, $"({field}+1)/({field}-1)");
        foreach (string expression in new[] { "y/1", "IIF(.T.,1/0,2)/1", "NVL(.NULL.,1/0)/1", "EVL(0,1/0)/1" })
            Add("overflow/" + expression, expression, "y=-7/0");
        probes.Add(new("empty-icase-error", "result=TRANSFORM(ICASE(.F.,1)/SQRT(-1))"));
        probes.Add(new("empty-icase-rand", "=RAND(1)\nv=ICASE(.F.,1)/RAND()\nresult=IIF(ISNULL(v),'NULL',TRANSFORM(v))+'|'+ALLTRIM(STR(RAND(),30,10))"));
        foreach (string producer in new[] { "1/0", "-7/0", "(n+1)/(n-1)" })
        foreach (int initial in new[] { 0, 2, 4 })
            Add($"redivide/{initial}/{producer}", "y/1", $"SET DECIMALS TO {initial}\ny={producer}\nSET DECIMALS TO {decimals}");
        foreach (string field in new[] { "n", "s", "h" })
        foreach (string expression in new[] { $"({field}+1)/({field}-1)", $"{field}/0", $"({field}+1)/0" })
        foreach (bool calculate in new[] { false, true })
        {
            string command = calculate ? $"CALCULATE SUM({expression})" : $"SUM {expression}";
            probes.Add(new(command, $"SET DECIMALS TO {decimals}\nUSE t\n{command} TO v\nresult=TRANSFORM(v)"));
        }
        if (decimals == 2)
        {
            foreach (string aggregate in new[] { "SUM", "AVG", "MIN", "MAX" })
                probes.Add(new("sql/" + aggregate, "SET DECIMALS TO 2\nSELECT " + aggregate + "((n+1)/(n-1)) AS v FROM t INTO ARRAY aa\nresult=TRANSFORM(aa[1])"));
            foreach (string aggregate in new[] { "SUM", "AVG", "MIN", "MAX" })
                probes.Add(new("sql-mixed/" + aggregate, "SET DECIMALS TO 2\nSELECT " + aggregate + "((n+1)/(n-1)) AS v FROM a INTO ARRAY aa\nresult=TRANSFORM(aa[1])"));
            probes.Add(new("sum", "SET DECIMALS TO 2\nSUM (n+1)/(n-1) TO v\nresult=TRANSFORM(v)"));
            probes.Add(new("calculate", "SET DECIMALS TO 2\nCALCULATE SUM((n+1)/(n-1)) TO v\nresult=TRANSFORM(v)"));
        }
        return probes.ToArray();
    }
    public static IEnumerable<object[]> Cases => from decimals in new[] { 0, 2, 4 }
        from probe in Probes(decimals) select new object[] { decimals, probe.Name };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReviewCases_MatchExpectedVfp(int decimals, string name)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthreview");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Functions + Setup);
        vfp.Execute(MicroVfpNpvRound6Tests.Catch(Probes(decimals).Single(p => p.Name == name).Body));
        Assert.Equal(ExpectedVfp[decimals + "/" + name], vfp.Memory.Get("result").AsString);
    }
    [Theory]
    [InlineData("tree")]
    [InlineData("resolver")]
    [InlineData("compiled")]
    [InlineData("aot")]
    public void ExpressionModes_MatchExpectedVfp(string mode)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthreviewmodes");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Functions + Setup);
        var row = new ReviewRow(vfp);
        foreach (int decimals in new[] { 0, 2, 4 })
        foreach (var probe in Probes(decimals).Where(p => p.Body.Contains("x=1")))
        {
            bool stored = probe.Name.EndsWith("/stored", StringComparison.Ordinal);
            string marker = stored ? "v=" : "result=";
            int start = probe.Body.LastIndexOf(marker, StringComparison.Ordinal);
            vfp.Execute(probe.Body[..start]);
            string source = probe.Body[(start + marker.Length)..].Split('\n')[0];
            var expression = VfpExpression.Parse(source);
            var value = mode switch
            {
                "tree" => expression.Evaluate(row, session.Context),
                "resolver" => expression.EvaluateWithFunctionResolver(row, session.Context, Unresolved),
                _ => expression.Compile(session.Context, preferInterpretation: mode == "aot")(row)
            };
            if (stored)
            {
                vfp.Memory.Set("v", value);
                value = vfp.EvalExpression("TRANSFORM(v)");
            }
            Assert.True(ExpectedVfp[decimals + "/" + probe.Name] == value.AsString, $"{decimals}/{probe.Name}/{mode}: expected={ExpectedVfp[decimals + "/" + probe.Name]}; actual={value.AsString}");
        }
    }

    [Theory]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    public void SqlAndAdo_PropagateOverflowWithoutClrException(string aggregate)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthreviewsql");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup);
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        foreach (string table in new[] { "t", "a" })
        {
            string sql = $"SELECT {aggregate}((n+1)/(n-1)) AS v FROM {table}";
            object? value = session.Execute(sql)!.Rows.Single()[0];
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            object? adoValue = command.ExecuteScalar();
            string pin = ExpectedVfp["2/" + (table == "t" ? "sql/" : "sql-mixed/") + aggregate];
            foreach (var actual in new[] { value, adoValue })
                if (pin.StartsWith('*')) Assert.True(double.IsPositiveInfinity(Assert.IsType<double>(actual)));
                else Assert.Equal(decimal.Parse(pin, System.Globalization.CultureInfo.InvariantCulture), Convert.ToDecimal(actual));
        }
    }

    private static bool Unresolved(string name, IReadOnlyList<ExpressionArgument> args, out VfpValue value)
    {
        value = default;
        return false;
    }

    private sealed class ReviewRow(VfpInterpreter vfp) : IRowContext, IVfpValueRow, IVfpFunctionHost, ISchema
    {
        public VfpValue GetValue(string name) => vfp.ResolveName(name);
        public object? GetField(string name) => GetValue(name);
        public int RecNo => 1;
        public int RecCount => 1;
        public bool Deleted => false;
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            var column = vfp.Session.AreaAt(vfp.Session.CurrentArea)?.Table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            type = column?.Type ?? default;
            length = column?.Length ?? 0;
            decimals = column?.Decimal ?? 0;
            return column is not null;
        }
        public bool TryInvoke(string name, VfpValue[] args, EvaluationContext context, out VfpValue result)
        {
            if (name == "SETX")
            {
                vfp.Memory.Set("x", vfp.Memory.Get("replacement"));
                result = VfpValue.Logical(true);
                return true;
            }
            result = default;
            return name == "TRANSFORM" && vfp.HostInvoke(name, args, context, out result);
        }
    }

    internal static readonly Dictionary<string, string> ExpectedVfp = new()
    {
        ["0/1.234567/x/IIF(setx(),0,0)"] = "*************",
        ["0/1.234567/x/IIF(setx(),0,0)/stored"] = "*************",
        ["0/1.234567/(x+1)/IIF(setx(),0,0)"] = "**************",
        ["0/1.234567/(x+1)/IIF(setx(),0,0)/stored"] = "**************",
        ["0/1.234567/x*2/IIF(setx(),0,0)"] = "***************",
        ["0/1.234567/x*2/IIF(setx(),0,0)/stored"] = "***************",
        ["0/1.234567/-x/IIF(setx(),0,0)"] = "**************",
        ["0/1.234567/-x/IIF(setx(),0,0)/stored"] = "**************",
        ["0/1.234567/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "*************",
        ["0/1.234567/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "*************",
        ["0/1.234567/IIF(setx(),x,x)/0"] = "*****************",
        ["0/1.234567/IIF(setx(),x,x)/0/stored"] = "*****************",
        ["0/12345/x/IIF(setx(),0,0)"] = "*************",
        ["0/12345/x/IIF(setx(),0,0)/stored"] = "*************",
        ["0/12345/(x+1)/IIF(setx(),0,0)"] = "**************",
        ["0/12345/(x+1)/IIF(setx(),0,0)/stored"] = "**************",
        ["0/12345/x*2/IIF(setx(),0,0)"] = "***************",
        ["0/12345/x*2/IIF(setx(),0,0)/stored"] = "***************",
        ["0/12345/-x/IIF(setx(),0,0)"] = "**************",
        ["0/12345/-x/IIF(setx(),0,0)/stored"] = "**************",
        ["0/12345/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "*************",
        ["0/12345/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "*************",
        ["0/12345/IIF(setx(),x,x)/0"] = "*************",
        ["0/12345/IIF(setx(),x,x)/0/stored"] = "*************",
        ["0/1.5/x/IIF(setx(),0,0)"] = "*************",
        ["0/1.5/x/IIF(setx(),0,0)/stored"] = "*************",
        ["0/1.5/(x+1)/IIF(setx(),0,0)"] = "**************",
        ["0/1.5/(x+1)/IIF(setx(),0,0)/stored"] = "**************",
        ["0/1.5/x*2/IIF(setx(),0,0)"] = "***************",
        ["0/1.5/x*2/IIF(setx(),0,0)/stored"] = "***************",
        ["0/1.5/-x/IIF(setx(),0,0)"] = "**************",
        ["0/1.5/-x/IIF(setx(),0,0)/stored"] = "**************",
        ["0/1.5/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "*************",
        ["0/1.5/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "*************",
        ["0/1.5/IIF(setx(),x,x)/0"] = "*************",
        ["0/1.5/IIF(setx(),x,x)/0/stored"] = "*************",
        ["0/nonliteral/n"] = "*******************",
        ["0/nonliteral/n/stored"] = "*******************",
        ["0/nonliteral/s"] = "**************",
        ["0/nonliteral/s/stored"] = "**************",
        ["0/nonliteral/h"] = "*********************",
        ["0/nonliteral/h/stored"] = "*********************",
        ["0/overflow/y/1"] = "**************",
        ["0/overflow/y/1/stored"] = "**************",
        ["0/overflow/IIF(.T.,1/0,2)/1"] = "****",
        ["0/overflow/IIF(.T.,1/0,2)/1/stored"] = "*************",
        ["0/overflow/NVL(.NULL.,1/0)/1"] = "****",
        ["0/overflow/NVL(.NULL.,1/0)/1/stored"] = "*************",
        ["0/overflow/EVL(0,1/0)/1"] = "****",
        ["0/overflow/EVL(0,1/0)/1/stored"] = "*************",
        ["0/empty-icase-error"] = "ERR|61",
        ["0/empty-icase-rand"] = "NULL|0.7107791207",
        ["0/redivide/0/1/0"] = "*************",
        ["0/redivide/0/1/0/stored"] = "*************",
        ["0/redivide/2/1/0"] = "***************",
        ["0/redivide/2/1/0/stored"] = "***************",
        ["0/redivide/4/1/0"] = "*****************",
        ["0/redivide/4/1/0/stored"] = "*****************",
        ["0/redivide/0/-7/0"] = "**************",
        ["0/redivide/0/-7/0/stored"] = "**************",
        ["0/redivide/2/-7/0"] = "****************",
        ["0/redivide/2/-7/0/stored"] = "****************",
        ["0/redivide/4/-7/0"] = "******************",
        ["0/redivide/4/-7/0/stored"] = "******************",
        ["0/redivide/0/(n+1)/(n-1)"] = "*******************",
        ["0/redivide/0/(n+1)/(n-1)/stored"] = "*******************",
        ["0/redivide/2/(n+1)/(n-1)"] = "*********************",
        ["0/redivide/2/(n+1)/(n-1)/stored"] = "*********************",
        ["0/redivide/4/(n+1)/(n-1)"] = "***********************",
        ["0/redivide/4/(n+1)/(n-1)/stored"] = "***********************",
        ["0/SUM (n+1)/(n-1)"] = "*******************",
        ["0/CALCULATE SUM((n+1)/(n-1))"] = "**********************",
        ["0/SUM n/0"] = "*************",
        ["0/CALCULATE SUM(n/0)"] = "*************",
        ["0/SUM (n+1)/0"] = "*************",
        ["0/CALCULATE SUM((n+1)/0)"] = "**************",
        ["0/SUM (s+1)/(s-1)"] = "**************",
        ["0/CALCULATE SUM((s+1)/(s-1))"] = "*****************",
        ["0/SUM s/0"] = "*************",
        ["0/CALCULATE SUM(s/0)"] = "***********",
        ["0/SUM (s+1)/0"] = "*************",
        ["0/CALCULATE SUM((s+1)/0)"] = "************",
        ["0/SUM (h+1)/(h-1)"] = "*********************",
        ["0/CALCULATE SUM((h+1)/(h-1))"] = "************************",
        ["0/SUM h/0"] = "***************",
        ["0/CALCULATE SUM(h/0)"] = "***************",
        ["0/SUM (h+1)/0"] = "***************",
        ["0/CALCULATE SUM((h+1)/0)"] = "****************",
        ["2/1.234567/x/IIF(setx(),0,0)"] = "***************",
        ["2/1.234567/x/IIF(setx(),0,0)/stored"] = "***************",
        ["2/1.234567/(x+1)/IIF(setx(),0,0)"] = "****************",
        ["2/1.234567/(x+1)/IIF(setx(),0,0)/stored"] = "****************",
        ["2/1.234567/x*2/IIF(setx(),0,0)"] = "*****************",
        ["2/1.234567/x*2/IIF(setx(),0,0)/stored"] = "*****************",
        ["2/1.234567/-x/IIF(setx(),0,0)"] = "****************",
        ["2/1.234567/-x/IIF(setx(),0,0)/stored"] = "****************",
        ["2/1.234567/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "***************",
        ["2/1.234567/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "***************",
        ["2/1.234567/IIF(setx(),x,x)/0"] = "*****************",
        ["2/1.234567/IIF(setx(),x,x)/0/stored"] = "*****************",
        ["2/12345/x/IIF(setx(),0,0)"] = "***************",
        ["2/12345/x/IIF(setx(),0,0)/stored"] = "***************",
        ["2/12345/(x+1)/IIF(setx(),0,0)"] = "****************",
        ["2/12345/(x+1)/IIF(setx(),0,0)/stored"] = "****************",
        ["2/12345/x*2/IIF(setx(),0,0)"] = "*****************",
        ["2/12345/x*2/IIF(setx(),0,0)/stored"] = "*****************",
        ["2/12345/-x/IIF(setx(),0,0)"] = "****************",
        ["2/12345/-x/IIF(setx(),0,0)/stored"] = "****************",
        ["2/12345/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "***************",
        ["2/12345/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "***************",
        ["2/12345/IIF(setx(),x,x)/0"] = "***************",
        ["2/12345/IIF(setx(),x,x)/0/stored"] = "***************",
        ["2/1.5/x/IIF(setx(),0,0)"] = "***************",
        ["2/1.5/x/IIF(setx(),0,0)/stored"] = "***************",
        ["2/1.5/(x+1)/IIF(setx(),0,0)"] = "****************",
        ["2/1.5/(x+1)/IIF(setx(),0,0)/stored"] = "****************",
        ["2/1.5/x*2/IIF(setx(),0,0)"] = "*****************",
        ["2/1.5/x*2/IIF(setx(),0,0)/stored"] = "*****************",
        ["2/1.5/-x/IIF(setx(),0,0)"] = "****************",
        ["2/1.5/-x/IIF(setx(),0,0)/stored"] = "****************",
        ["2/1.5/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "***************",
        ["2/1.5/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "***************",
        ["2/1.5/IIF(setx(),x,x)/0"] = "***************",
        ["2/1.5/IIF(setx(),x,x)/0/stored"] = "***************",
        ["2/nonliteral/n"] = "*********************",
        ["2/nonliteral/n/stored"] = "*********************",
        ["2/nonliteral/s"] = "****************",
        ["2/nonliteral/s/stored"] = "****************",
        ["2/nonliteral/h"] = "*********************",
        ["2/nonliteral/h/stored"] = "*********************",
        ["2/overflow/y/1"] = "****************",
        ["2/overflow/y/1/stored"] = "****************",
        ["2/overflow/IIF(.T.,1/0,2)/1"] = "******",
        ["2/overflow/IIF(.T.,1/0,2)/1/stored"] = "***************",
        ["2/overflow/NVL(.NULL.,1/0)/1"] = "******",
        ["2/overflow/NVL(.NULL.,1/0)/1/stored"] = "***************",
        ["2/overflow/EVL(0,1/0)/1"] = "******",
        ["2/overflow/EVL(0,1/0)/1/stored"] = "***************",
        ["2/empty-icase-error"] = "ERR|61",
        ["2/empty-icase-rand"] = "NULL|0.7107791207",
        ["2/redivide/0/1/0"] = "***************",
        ["2/redivide/0/1/0/stored"] = "***************",
        ["2/redivide/2/1/0"] = "***************",
        ["2/redivide/2/1/0/stored"] = "***************",
        ["2/redivide/4/1/0"] = "*****************",
        ["2/redivide/4/1/0/stored"] = "*****************",
        ["2/redivide/0/-7/0"] = "****************",
        ["2/redivide/0/-7/0/stored"] = "****************",
        ["2/redivide/2/-7/0"] = "****************",
        ["2/redivide/2/-7/0/stored"] = "****************",
        ["2/redivide/4/-7/0"] = "******************",
        ["2/redivide/4/-7/0/stored"] = "******************",
        ["2/redivide/0/(n+1)/(n-1)"] = "*********************",
        ["2/redivide/0/(n+1)/(n-1)/stored"] = "*********************",
        ["2/redivide/2/(n+1)/(n-1)"] = "*********************",
        ["2/redivide/2/(n+1)/(n-1)/stored"] = "*********************",
        ["2/redivide/4/(n+1)/(n-1)"] = "***********************",
        ["2/redivide/4/(n+1)/(n-1)/stored"] = "***********************",
        ["2/SUM (n+1)/(n-1)"] = "*********************",
        ["2/CALCULATE SUM((n+1)/(n-1))"] = "************************",
        ["2/SUM n/0"] = "***************",
        ["2/CALCULATE SUM(n/0)"] = "***************",
        ["2/SUM (n+1)/0"] = "***************",
        ["2/CALCULATE SUM((n+1)/0)"] = "****************",
        ["2/SUM (s+1)/(s-1)"] = "****************",
        ["2/CALCULATE SUM((s+1)/(s-1))"] = "*******************",
        ["2/SUM s/0"] = "***************",
        ["2/CALCULATE SUM(s/0)"] = "*************",
        ["2/SUM (s+1)/0"] = "***************",
        ["2/CALCULATE SUM((s+1)/0)"] = "**************",
        ["2/SUM (h+1)/(h-1)"] = "*********************",
        ["2/CALCULATE SUM((h+1)/(h-1))"] = "************************",
        ["2/SUM h/0"] = "***************",
        ["2/CALCULATE SUM(h/0)"] = "***************",
        ["2/SUM (h+1)/0"] = "***************",
        ["2/CALCULATE SUM((h+1)/0)"] = "****************",
        ["2/sql/SUM"] = "********************",
        ["2/sql/AVG"] = "********************",
        ["2/sql/MIN"] = "********************",
        ["2/sql/MAX"] = "********************",
        ["2/sql-mixed/SUM"] = "********************",
        ["2/sql-mixed/AVG"] = "********************",
        ["2/sql-mixed/MIN"] = "3",
        ["2/sql-mixed/MAX"] = "********************",
        ["2/sum"] = "*********************",
        ["2/calculate"] = "************************",
        ["4/1.234567/x/IIF(setx(),0,0)"] = "*****************",
        ["4/1.234567/x/IIF(setx(),0,0)/stored"] = "*****************",
        ["4/1.234567/(x+1)/IIF(setx(),0,0)"] = "******************",
        ["4/1.234567/(x+1)/IIF(setx(),0,0)/stored"] = "******************",
        ["4/1.234567/x*2/IIF(setx(),0,0)"] = "*******************",
        ["4/1.234567/x*2/IIF(setx(),0,0)/stored"] = "*******************",
        ["4/1.234567/-x/IIF(setx(),0,0)"] = "******************",
        ["4/1.234567/-x/IIF(setx(),0,0)/stored"] = "******************",
        ["4/1.234567/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "*****************",
        ["4/1.234567/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "*****************",
        ["4/1.234567/IIF(setx(),x,x)/0"] = "*****************",
        ["4/1.234567/IIF(setx(),x,x)/0/stored"] = "*****************",
        ["4/12345/x/IIF(setx(),0,0)"] = "*****************",
        ["4/12345/x/IIF(setx(),0,0)/stored"] = "*****************",
        ["4/12345/(x+1)/IIF(setx(),0,0)"] = "******************",
        ["4/12345/(x+1)/IIF(setx(),0,0)/stored"] = "******************",
        ["4/12345/x*2/IIF(setx(),0,0)"] = "*******************",
        ["4/12345/x*2/IIF(setx(),0,0)/stored"] = "*******************",
        ["4/12345/-x/IIF(setx(),0,0)"] = "******************",
        ["4/12345/-x/IIF(setx(),0,0)/stored"] = "******************",
        ["4/12345/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "*****************",
        ["4/12345/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "*****************",
        ["4/12345/IIF(setx(),x,x)/0"] = "*****************",
        ["4/12345/IIF(setx(),x,x)/0/stored"] = "*****************",
        ["4/1.5/x/IIF(setx(),0,0)"] = "*****************",
        ["4/1.5/x/IIF(setx(),0,0)/stored"] = "*****************",
        ["4/1.5/(x+1)/IIF(setx(),0,0)"] = "******************",
        ["4/1.5/(x+1)/IIF(setx(),0,0)/stored"] = "******************",
        ["4/1.5/x*2/IIF(setx(),0,0)"] = "*******************",
        ["4/1.5/x*2/IIF(setx(),0,0)/stored"] = "*******************",
        ["4/1.5/-x/IIF(setx(),0,0)"] = "******************",
        ["4/1.5/-x/IIF(setx(),0,0)/stored"] = "******************",
        ["4/1.5/IIF(.T.,x,0)/IIF(setx(),0,0)"] = "*****************",
        ["4/1.5/IIF(.T.,x,0)/IIF(setx(),0,0)/stored"] = "*****************",
        ["4/1.5/IIF(setx(),x,x)/0"] = "*****************",
        ["4/1.5/IIF(setx(),x,x)/0/stored"] = "*****************",
        ["4/nonliteral/n"] = "***********************",
        ["4/nonliteral/n/stored"] = "***********************",
        ["4/nonliteral/s"] = "******************",
        ["4/nonliteral/s/stored"] = "******************",
        ["4/nonliteral/h"] = "***********************",
        ["4/nonliteral/h/stored"] = "***********************",
        ["4/overflow/y/1"] = "******************",
        ["4/overflow/y/1/stored"] = "******************",
        ["4/overflow/IIF(.T.,1/0,2)/1"] = "********",
        ["4/overflow/IIF(.T.,1/0,2)/1/stored"] = "*****************",
        ["4/overflow/NVL(.NULL.,1/0)/1"] = "********",
        ["4/overflow/NVL(.NULL.,1/0)/1/stored"] = "*****************",
        ["4/overflow/EVL(0,1/0)/1"] = "********",
        ["4/overflow/EVL(0,1/0)/1/stored"] = "*****************",
        ["4/empty-icase-error"] = "ERR|61",
        ["4/empty-icase-rand"] = "NULL|0.7107791207",
        ["4/redivide/0/1/0"] = "*****************",
        ["4/redivide/0/1/0/stored"] = "*****************",
        ["4/redivide/2/1/0"] = "*****************",
        ["4/redivide/2/1/0/stored"] = "*****************",
        ["4/redivide/4/1/0"] = "*****************",
        ["4/redivide/4/1/0/stored"] = "*****************",
        ["4/redivide/0/-7/0"] = "******************",
        ["4/redivide/0/-7/0/stored"] = "******************",
        ["4/redivide/2/-7/0"] = "******************",
        ["4/redivide/2/-7/0/stored"] = "******************",
        ["4/redivide/4/-7/0"] = "******************",
        ["4/redivide/4/-7/0/stored"] = "******************",
        ["4/redivide/0/(n+1)/(n-1)"] = "***********************",
        ["4/redivide/0/(n+1)/(n-1)/stored"] = "***********************",
        ["4/redivide/2/(n+1)/(n-1)"] = "***********************",
        ["4/redivide/2/(n+1)/(n-1)/stored"] = "***********************",
        ["4/redivide/4/(n+1)/(n-1)"] = "***********************",
        ["4/redivide/4/(n+1)/(n-1)/stored"] = "***********************",
        ["4/SUM (n+1)/(n-1)"] = "***********************",
        ["4/CALCULATE SUM((n+1)/(n-1))"] = "**************************",
        ["4/SUM n/0"] = "*****************",
        ["4/CALCULATE SUM(n/0)"] = "*****************",
        ["4/SUM (n+1)/0"] = "*****************",
        ["4/CALCULATE SUM((n+1)/0)"] = "******************",
        ["4/SUM (s+1)/(s-1)"] = "******************",
        ["4/CALCULATE SUM((s+1)/(s-1))"] = "*********************",
        ["4/SUM s/0"] = "*****************",
        ["4/CALCULATE SUM(s/0)"] = "***************",
        ["4/SUM (s+1)/0"] = "*****************",
        ["4/CALCULATE SUM((s+1)/0)"] = "****************",
        ["4/SUM (h+1)/(h-1)"] = "***********************",
        ["4/CALCULATE SUM((h+1)/(h-1))"] = "**************************",
        ["4/SUM h/0"] = "*****************",
        ["4/CALCULATE SUM(h/0)"] = "*****************",
        ["4/SUM (h+1)/0"] = "*****************",
        ["4/CALCULATE SUM((h+1)/0)"] = "******************",
    };
}
