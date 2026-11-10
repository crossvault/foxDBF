using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNumericScaleTests
{
    internal static readonly string[] Commands = ["SUM n TO x", "AVERAGE n TO x", "CALCULATE AVG(n) TO x", "CALCULATE SUM(n) TO x",
        "CALCULATE STD(n) TO x", "CALCULATE VAR(n) TO x", "CALCULATE CNT(n) TO x", "CALCULATE MIN(n) TO x", "CALCULATE MAX(n) TO x"];

    public static IEnumerable<object[]> Cases => from type in new[] { "I", "N(8,2)", "N(10,4)", "B", "Y" }
        from decimals in new[] { 2, 4 } select new object[] { type, decimals };

    internal static string Setup(string type) => $"SET DECIMALS TO 2\nCREATE TABLE t (n {type})\nINSERT INTO t VALUES (2)\nINSERT INTO t VALUES (3)";

    internal static string Scenario(int decimals)
    {
        var script = new StringBuilder($"USE t\nSET DECIMALS TO {decimals}\n");
        foreach (string command in Commands)
            script.AppendLine(command + "\nresult=result+TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,30,10)+'|'+VARTYPE(x)+CHR(10)");
        return script.ToString();
    }

    internal static string Expected(string type, int decimals)
    {
        double[] values = [5, 2.5, 2.5, 5, 0.5, 0.25, 2, 2, 3];
        var text = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            bool currency = type == "Y" && i is not (4 or 5 or 6);
            int scale = i == 1 && type == "N(10,4)" ? 4 : decimals;
            string display = currency ? "$" + values[i].ToString("F" + decimals, CultureInfo.InvariantCulture)
                : values[i] == Math.Truncate(values[i]) ? values[i].ToString(CultureInfo.InvariantCulture)
                : values[i].ToString("F" + scale, CultureInfo.InvariantCulture);
            text.AppendLine(display + "|" + Math.Round(values[i], 0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture).PadLeft(10)
                + "|" + values[i].ToString("F10", CultureInfo.InvariantCulture).PadLeft(30) + "|" + (currency ? "Y" : "N"));
        }
        return text.ToString().Replace("\r", "");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AggregateScaleAndStrDefaults_MatchVfp9(string type, int decimals)
        => Assert.Equal(Expected(type, decimals), MicroVfpXbaseUpdateTests.Run(Setup(type), Scenario(decimals)));

    internal const string DoubleSetup = "SET DECIMALS TO 4\nCREATE TABLE t (n B)\nINSERT INTO t VALUES (1.2345)\nINSERT INTO t VALUES (3.4567)";
    internal const string DoubleScenario = "USE t\nSET DECIMALS TO 2\nSUM n TO x\nresult=TRANSFORM(x)";

    [Fact]
    public void SumDouble_UsesDecimalsAtFieldCreation()
        => Assert.Equal("4.6912", MicroVfpXbaseUpdateTests.Run(DoubleSetup, DoubleScenario));

    // VFP9 (MicroVfpNumericScaleVfp9OracleTests.CalculateScopesNullsAndSettings): a NEXT scope before the
    // CALCULATE list keeps the one-operand MIN/MAX aggregates; the scalar MIN/MAX arity does not apply.
    [Fact]
    public void CalculateNextScope_AcceptsOneOperandMinMax()
        => Assert.Equal("1.23|1.23|0|0|1|1.23|1.23", MicroVfpXbaseUpdateTests.Run(
            "CREATE TABLE t (n N(10,4) NULL)\nINSERT INTO t VALUES (.NULL.)\nINSERT INTO t VALUES (1.2345)\nINSERT INTO t VALUES (3.4567)\nUSE",
            "USE t\nSET DECIMALS TO 2\nCALCULATE NEXT 2 AVG(n),SUM(n),STD(n),VAR(n),CNT(n),MIN(n),MAX(n) TO a,b,c,d,e,f,g\n"
            +"result=TRANSFORM(a)+'|'+TRANSFORM(b)+'|'+TRANSFORM(c)+'|'+TRANSFORM(d)+'|'+TRANSFORM(e)+'|'+TRANSFORM(f)+'|'+TRANSFORM(g)"));
}
