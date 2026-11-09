using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Expressions;
namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlHReviewTests
{
    internal const string SeekContextSetup = "CREATE TABLE t (flag L)\nINSERT INTO t VALUES (.T.)\nINDEX ON flag TAG live\nCREATE TABLE other (n I)\nINSERT INTO other VALUES (1)";
    internal const string SeekContextProcedures = "FUNCTION switcharea\nSELECT other\nRETURN .T.\nENDFUNC";
    internal static string SeekContextScenario => "USE other IN 0\nUSE t IN 0\nSELECT t\nSET ORDER TO live\n" +
        MicroVfpSqlDmlTests.Catch("SEEK switcharea()") + "\nresult=result+ALIAS()+'|'+TRANSFORM(FOUND('t'))";

    [Fact]
    public void H1_SeekPreservesTargetBeforeEvaluatingKey() => Assert.Equal("OTHER|.T.",
        MicroVfpSqlDmlFinalReviewTests.Run(SeekContextSetup, SeekContextScenario, SeekContextProcedures));

    internal static IEnumerable<string> Operators(string left, string right)
    {
        foreach (string op in new[] { "=", "==", "<>", "#", "!=", "<", "<=", ">", ">=" }) yield return $"{left} {op} {right}";
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
        foreach (string expression in Operators(left, right).Distinct())
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

    [Theory]
    [InlineData("flag = 1")]
    [InlineData("d = 1")]
    [InlineData("n = {^2026-01-01}")]
    [InlineData("BETWEEN(d,0,1)")]
    [InlineData("INLIST(d,1)")]
    public void H1_RushmorePreservesTypeErrors(string predicate)
    {
        using var dir = new MicroVfpTestSupport.TempDir("h1_rushmore");
        using (var session = new VfpSession())
        {
            session.OpenDirectory(dir.Path);
            using var vfp = new VfpInterpreter(session);
            vfp.Execute("CREATE TABLE t (flag L, n I, d D)\nINSERT INTO t VALUES (.T.,1,{^2026-01-01})\nINDEX ON flag TAG flag\nINDEX ON n TAG n\nINDEX ON d TAG d");
        }
        using var table = DbfTable.Open(dir.File("t.dbf"));
        using var cdx = CdxFile.Open(dir.File("t.cdx"), table);
        foreach (var index in new[] { null, cdx })
        {
            Assert.Equal(107, Assert.Throws<ExpressionException>(() => QueryOptimizer.FindRecords(table, index, predicate, new EvaluationContext { Deleted = true })).VfpErrorNumber);
            Assert.Equal(107, Assert.Throws<ExpressionException>(() => QueryOptimizer.Count(table, index, predicate, new EvaluationContext { Deleted = true })).VfpErrorNumber);
        }
    }
}
