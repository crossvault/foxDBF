namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpFinal3AggregateTests
{
    [Theory]
    [InlineData("n*1", "1.250000")]
    [InlineData("1.25", "1.25")]
    [InlineData("m.y", "1.234567")]
    [InlineData("n/2", "0.625000")]
    [InlineData("1.234567", "1.234567")]
    public void SumAndAverage_KeepExpressionPrecision(string expression, string expected)
        => Assert.Equal(expected + "|" + expected, MicroVfpXbaseUpdateTests.Run(
            "CREATE TABLE t (n N(12,6))\nINSERT INTO t VALUES (1.25)",
            $"USE t\nm.y=1.234567\nSUM {expression} TO x\nresult=TRANSFORM(x)\nAVERAGE {expression} TO x\nresult=result+'|'+TRANSFORM(x)"));

    [Theory]
    [InlineData("C(8)", "'beta'", "'alpha'", "C:alpha   |C:beta    ")]
    [InlineData("D", "{^2024-02-01}", "{^2024-01-01}", "D:01/01/2024|D:02/01/2024")]
    [InlineData("T", "{^2024-02-01 12:00:00}", "{^2024-01-01 12:00:00}", "T:01/01/2024 12:00:00 PM|T:02/01/2024 12:00:00 PM")]
    [InlineData("L", ".T.", ".F.", "L:.F.|L:.T.")]
    public void CalculateMinMax_ReturnTypedValuesAndSkipNull(string type, string a, string b, string expected)
        => Assert.Equal(expected, MicroVfpXbaseUpdateTests.Run(
            $"CREATE TABLE t (n {type} NULL)\nINSERT INTO t VALUES (.NULL.)\nINSERT INTO t VALUES ({a})\nINSERT INTO t VALUES ({b})",
            "USE t\nSET CENTURY ON\nCALCULATE MIN(n),MAX(n) TO a,b\nresult=VARTYPE(a)+':'+TRANSFORM(a)+'|'+VARTYPE(b)+':'+TRANSFORM(b)"));

    [Theory]
    [InlineData("C(8)")]
    [InlineData("D")]
    [InlineData("T")]
    [InlineData("L")]
    public void CalculateNumericFunctions_RejectNonNumericEvenWhenEmpty(string type)
    {
        string script = "USE t\n";
        foreach (string fn in new[] { "SUM", "AVG", "STD", "VAR", "CNT" })
            script += MicroVfpSqlDmlTests.Catch($"CALCULATE {fn}(n) TO x") + "\n";
        Assert.Equal(string.Concat(Enumerable.Repeat("ERR:27|", 5)),
            MicroVfpXbaseUpdateTests.Run($"CREATE TABLE t (n {type} NULL)", script));
    }

    [Theory]
    [InlineData("'abc'", "ERR:27|")]
    [InlineData(".T.", "ERR:27|")]
    [InlineData(".NULL.", "ERR:27|")]
    [InlineData("*", "ERR:152|")]
    public void CalculateCount_RejectsInvalidArguments(string argument, string expected)
        => Assert.Equal(expected, MicroVfpXbaseUpdateTests.Run("CREATE TABLE t (n I)\nINSERT INTO t VALUES (1)",
            "USE t\n" + MicroVfpSqlDmlTests.Catch($"cmd=[CALCULATE CNT({argument}) TO x]\n&cmd")));

    [Fact]
    public void CurrencySum_IsExactAcrossManyRows()
        => Assert.Equal("            1000000000001.0000|            1000000000001.0000", MicroVfpXbaseUpdateTests.Run(
            "CREATE TABLE t (n Y)\nFOR i=1 TO 10000\nINSERT INTO t VALUES (100000000.0001)\nENDFOR",
            "USE t\nSUM n TO x\nresult=STR(x,30,4)\nCALCULATE SUM(n) TO x\nresult=result+'|'+STR(x,30,4)"));
}
