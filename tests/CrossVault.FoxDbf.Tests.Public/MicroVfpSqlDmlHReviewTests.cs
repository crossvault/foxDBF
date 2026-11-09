using CrossVault.FoxDbf.Expressions;
namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlHReviewTests
{
    internal static IEnumerable<string> Operators(string left, string right)
    {
        foreach (string op in new[] { "=", "==", "<>", "#", "<", "<=", ">", ">=" }) yield return $"{left} {op} {right}";
        yield return $"INLIST({left},{right})";
        yield return $"INLIST({right},{left})";
        yield return $"BETWEEN({left},{right},{right})";
        yield return $"MAX({left},{right})";
        yield return $"MIN({right},{left})";
    }
    public static IEnumerable<object[]> ComparisonCases()
    {
        foreach (string left in new[] { ".T.", "flag", ".NULL.", "'x'", "{^2026-01-01}" })
        foreach (string right in new[] { "1", "{^2026-01-01}", "{^2026-01-01 12:00:00}" })
        foreach (string expression in Operators(left, right))
            yield return new object[] { expression, left is "flag" or ".NULL." ? 0 : left.StartsWith('{') && right.StartsWith('{') ? -1 : 107 };
    }
    [Theory]
    [MemberData(nameof(ComparisonCases))]
    public void H1_Comparisons(string expression, int error)
    {
        var parsed = VfpExpression.Parse(expression);
        foreach (var evaluate in new Func<VfpValue>[] { () => parsed.Evaluate(TestRow.Empty), () => parsed.Compile()(TestRow.Empty), () => parsed.Compile(null, true)(TestRow.Empty) })
        {
            if (error > 0) Assert.Equal(error, Assert.Throws<ExpressionException>(() => evaluate()).VfpErrorNumber);
            else if (error == 0) Assert.True(evaluate().IsNull);
            else Assert.False(evaluate().IsNull);
        }
    }
    internal static string ComparisonScenario(string command, bool indexed) => "USE t EXCLUSIVE\n" +
        (indexed ? "INDEX ON flag TAG flag\nSET ORDER TO flag\n" : "") + MicroVfpSqlDmlTests.Catch(command);
    public static IEnumerable<object[]> ComparisonCommands()
    {
        foreach (bool indexed in new[] { false, true })
        foreach (string command in new[] { "SELECT * FROM t WHERE flag = 1 INTO CURSOR foundrows", "SELECT * FROM t WHERE flag <> 1 INTO CURSOR foundrows", "SET FILTER TO flag = 1\nGO TOP", "INDEX ON flag TAG test FOR flag = 1", "LOCATE FOR flag = 1" })
            yield return new object[] { command, indexed, "ERR:107|" };
        yield return new object[] { "SEEK 1", true, "ERR:9|" };
    }
    [Theory]
    [MemberData(nameof(ComparisonCommands))]
    public void H1_Commands(string command, bool indexed, string expected) => Assert.Equal(expected,
        MicroVfpSqlDmlFinalReviewTests.Run("CREATE TABLE t (flag L NULL)\nINSERT INTO t VALUES (.T.)",
            ComparisonScenario(command, indexed)));
    internal const string NestedProcedure = "FUNCTION nested\nINSERT INTO child VALUES (-1)\nRETURN -1\nENDFUNC";
    internal static string H2Setup(bool array) => "CREATE TABLE child (n I CHECK n>0)\nCREATE TABLE parent (id I, n I DEFAULT nested(), tail I DEFAULT 9)\nCREATE TABLE src (id I)\nINSERT INTO src VALUES (1)\nINSERT INTO src VALUES (2)";
    internal static string H2Scenario(bool array, int buffering) => $"SET MULTILOCKS ON\nUSE parent\n=CURSORSETPROP('Buffering',{buffering})\n" +
        (array ? "DIMENSION laRows[2,1]\nlaRows[1,1]=1\nlaRows[2,1]=2\n" : "") +
        MicroVfpSqlDmlTests.Catch(array ? "INSERT INTO parent FROM ARRAY laRows" : "INSERT INTO parent (id) SELECT id FROM src") +
        "\nSELECT parent\nresult=result+TRANSFORM(RECCOUNT())\nGO TOP\nresult=result+'|'+TRANSFORM(id)+'|'+TRANSFORM(n)+'|'+TRANSFORM(tail)" +
        (buffering > 1 ? "\n=TABLEREVERT(.T.)" : "");
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(false, 5)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    [InlineData(true, 5)]
    public void H2_NestedDefaultStopsSource(bool array, int buffering) => Assert.Equal(array ? "2|1|0|0" : buffering == 1 ? "ERR:1582|1|1|0|0" : "ERR:1582|0|0|0|0",
        MicroVfpSqlDmlFinalReviewTests.Run(H2Setup(array), H2Scenario(array, buffering), NestedProcedure));
}
