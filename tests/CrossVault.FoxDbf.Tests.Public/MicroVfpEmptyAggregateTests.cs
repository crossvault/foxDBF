namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpEmptyAggregateTests
{
    public static IEnumerable<object[]> NonNumericCases => from type in new[] { "C(8)", "D", "T", "L" }
        from rows in new[] { "values", "null", "empty" }
        from filter in new[] { ".T.", ".F." }
        select new object[] { type, rows, filter };

    [Theory]
    [MemberData(nameof(NonNumericCases))]
    public void SumAverage_RejectNonNumericFieldsEvenWithoutMatches(string type, string rows, string filter)
    {
        string value = rows == "null" ? ".NULL." : type == "C(8)" ? "'abc'" : type == "D" ? "{^2024-02-03}"
            : type == "T" ? "{^2024-02-03 12:34:56}" : ".T.";
        string setup = $"CREATE TABLE t (n {type} NULL)\n" + (rows == "empty" ? "" : $"INSERT INTO t VALUES ({value})");
        string scenario = "USE t\n";
        foreach (string command in new[] { "SUM n", "AVERAGE n" })
            scenario += MicroVfpSqlDmlTests.Catch($"{command} FOR {filter} TO x") + "\n";
        Assert.Equal("ERR:27|ERR:27|", MicroVfpXbaseUpdateTests.Run(setup, scenario));
    }

    [Theory]
    [InlineData("'abc'")]
    [InlineData(".T.")]
    [InlineData("{}")]
    [InlineData("CTOT('')")]
    [InlineData(".NULL.")]
    [InlineData("m.v")]
    [InlineData("IIF(n>0,'abc',.NULL.)")]
    public void SumAverage_RejectNonNumericExpressionsWithoutMatches(string expression)
    {
        string scenario = "USE t\nm.v='abc'\n";
        foreach (string command in new[] { "SUM", "AVERAGE" })
            scenario += MicroVfpSqlDmlTests.Catch($"{command} {expression} FOR .F. TO x") + "\n";
        Assert.Equal("ERR:27|ERR:27|", MicroVfpXbaseUpdateTests.Run("CREATE TABLE t (n I)", scenario));
    }

    [Theory]
    [InlineData("SUM IIF(.F.,.NULL.,n) TO x", "3")]
    [InlineData("SUM IIF(.F.,'abc',n) TO x", "3")]
    [InlineData("SUM EVL('',n) TO x", "3")]
    [InlineData("SUM IIF(n=1,n,{}) TO x", "1")]
    [InlineData("AVERAGE IIF(n=1,n,{}) TO x", "0.50")]
    public void SumAverage_CheckEvaluatedTypeOnceAndSkipLaterNonNumericValues(string command, string expected)
    {
        // VFP9 (oracle SumAverageEvaluatedBranchProbe_MatchesVfp9): the type probe uses the evaluated branch; later non-numeric rows are ignored.
        string scenario = "USE t\n" + MicroVfpSqlDmlTests.Catch(command + "\nresult=TRANSFORM(x)");
        Assert.Equal(expected, MicroVfpXbaseUpdateTests.Run(
            "CREATE TABLE t (n I)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (2)", scenario));
    }
}
