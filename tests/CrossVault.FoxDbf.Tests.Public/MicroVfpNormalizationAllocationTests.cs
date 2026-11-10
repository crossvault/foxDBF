using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNormalizationAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void ScenarioB_Insert_ReportsSteadyStateAllocations()
    {
        const string sql = "INSERT INTO bench (id, name, val, city) VALUES (1234, 'n1234', 1234, 'c34')";
        using var dir = new MicroVfpTestSupport.TempDir("insert_alloc");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("CREATE TABLE bench FREE (id I, name C(20), val N(12,2), city C(20))");
        for (int i = 0; i < 128; i++) interpreter.Execute(sql);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) interpreter.Execute(sql);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Scenario-b INSERT: {allocated / 1024.0:F2} bytes/call (1024 warmed executions).");
        Assert.Equal(1152, interpreter.EvalExpression("RECCOUNT()").AsInteger);
    }

    [Theory]
    [InlineData("INSERT INTO bench (id, name, val, city) VALUES (1234, 'n1234', 1234, 'c34')")]
    [InlineData("SELECT ISBLANK(cc), IIF(n IN (1,2), 1, 0) FROM probe")]
    [InlineData("ABS(-2) + MAX(1, 2)")]
    public void Normalize_WithoutReferenceFunctions_DoesNotAllocate(string source)
    {
        for (int i = 0; i < 128; i++) MicroVfpExprRewrite.Normalize(source);

        // Parentheses alone must not rebuild the source for every array-reference function.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) MicroVfpExprRewrite.Normalize(source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Same(source, MicroVfpExprRewrite.Normalize(source));
    }

    [Theory]
    [InlineData("aerr(arr)", "aerr('arr')")]
    [InlineData("aCoP(arr, dest)", "aCoP('arr', 'dest')")]
    [InlineData("IIF(.T., aLen(arr), arr[1])", "IIF(.T., aLen('arr'), arr(1))")]
    [InlineData("look(n, 2, id)", "look('n', 2,'id')")]
    [InlineData("'ALEN(arr)' + [ACOPY(arr,dest)]", "'ALEN(arr)' + [ACOPY(arr,dest)]")]
    [InlineData("xALEN(arr) + AERROR_suffix(arr)", "xALEN(arr) + AERROR_suffix(arr)")]
    public void Normalize_PrefixCandidates_PreserveReferenceAndLiteralHandling(string source, string expected)
        => Assert.Equal(expected, MicroVfpExprRewrite.Normalize(source));
}
