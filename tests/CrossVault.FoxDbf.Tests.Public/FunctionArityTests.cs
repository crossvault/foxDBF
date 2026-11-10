using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class FunctionArityTests
{
    public static IEnumerable<object[]> EngineFunctions => FunctionArityData.Entries
        .Select(e => new object[] { e.Name, e.Min, e.Max, e.Few, e.Many });

    [Fact]
    public void Table_CoversEveryEngineFunction()
        => Assert.Equal(VfpRuntime.EngineFunctionNames.OrderBy(n => n), FunctionArityData.Entries.Select(e => e.Name).OrderBy(n => n));

    [Theory]
    [MemberData(nameof(EngineFunctions))]
    public void EveryEngineFunction_UsesVfp9Arity(string name, int min, int max, int few, int many)
    {
        for (int count = 0; count <= max + 1; count++)
        {
            string expression = Call(name, count);
            int error = count < min ? count == 0 && name is "IIF" or "ICASE" ? 152 : few : count > max ? many : 0;
            if (error != 0)
            {
                Assert.Equal(error, Assert.Throws<ExpressionException>(() => VfpExpression.Parse(expression)).VfpErrorNumber);
                continue;
            }
            var parsed = VfpExpression.Parse(expression);
            int runtimeError = FunctionArityData.RuntimeError(name, count);
            if (runtimeError != 0)
            {
                Assert.Equal(runtimeError, Assert.Throws<ExpressionException>(() => parsed.Evaluate(TestRow.Empty)).VfpErrorNumber);
                Assert.Equal(runtimeError, Assert.Throws<ExpressionException>(() => parsed.Compile()(TestRow.Empty)).VfpErrorNumber);
            }
        }
    }

    [Fact]
    public void Abbreviations_UseCanonicalArity()
    {
        var entries = FunctionArityData.Entries.ToDictionary(e => e.Name);
        foreach (var (abbreviation, name) in VfpFunctionAbbreviations.All)
        {
            if (!entries.TryGetValue(name, out var entry)) continue;
            Assert.Equal(entry.Many, Assert.Throws<ExpressionException>(() => VfpExpression.Parse(Call(abbreviation.ToLowerInvariant(), entry.Max + 1))).VfpErrorNumber);
        }
    }

    private static string Call(string name, int count) => name + "(" + string.Join(",", Enumerable.Repeat("1", count)) + ")";

    [Theory]
    [InlineData("VAL()", 1229)]
    [InlineData("VAL('1', '2')", 1230)]
    [InlineData("UPPE()", 1229)]
    [InlineData("IIF(.T., 1)", 1306)]
    [InlineData("IIF()", 152)]
    [InlineData("IIF(.T.,1,2,3)", 1300)]
    [InlineData("IIF(.T., 1, VAL())", 1229)]
    public void InvalidCount_IsRejectedWhileParsing(string expression, int error)
        => Assert.Equal(error, Assert.Throws<ExpressionException>(() => VfpExpression.Parse(expression)).VfpErrorNumber);

    [Theory]
    [InlineData("VAL()", 1229)]
    [InlineData("VAL('1', '2')", 1230)]
    public void Sql_RejectsInvalidCountEvenOnEmptyTable(string expression, int error)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_sql");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE probe (id I)");
        var ex = Assert.Throws<FoxDbfSqlException>(() => session.Execute($"SELECT {expression} AS x FROM probe"));
        Assert.Equal(error, ex.VfpErrorNumber);
    }

    [Theory]
    [InlineData("VAL()", 1229)]
    [InlineData("VAL('1','2')", 1230)]
    public void Index_RejectsInvalidCountBeforeBuilding(string expression, int error)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_index");
        using var writer = DbfWriter.Create(dir.File("probe.dbf"), new[] { new DbfColumnDef("id", 'I') });
        writer.AppendRecord(1);
        Assert.Equal(error, Assert.Throws<ExpressionException>(() => writer.CreateTag(new CdxTagDefinition("bad", expression))).VfpErrorNumber);
    }

    [Fact]
    public void SqlAggregates_KeepSingleOperandWhileScalarMinMaxRequireTwo()
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_aggregate");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE probe (id I)");
        session.Execute("INSERT INTO probe VALUES (7)");
        var row = Assert.Single(session.Execute("SELECT MIN(id) AS lo, MAX(id) AS hi FROM probe")!.Rows);
        Assert.Equal(7, Convert.ToInt32(row[0]));
        Assert.Equal(7, Convert.ToInt32(row[1]));
        Assert.Equal(1229, Assert.Throws<ExpressionException>(() => VfpExpression.Parse("MAX(7)")).VfpErrorNumber);
    }

    [Fact]
    public void UnknownAndHostOnlyFunctions_AreOutsideEngineArityTable()
    {
        VfpExpression.Parse("customfn()");
        VfpExpression.Parse(Call("customfn", 28));
        VfpExpression.Parse("TRANSFORM()");
        VfpExpression.Parse("EVALUATE()");
    }

    [Theory]
    [InlineData("x = VAL()", 1229)]
    [InlineData("x = VAL('1', '2')", 1230)]
    [InlineData("SELECT VAL() AS x FROM probe INTO CURSOR results", 1229)]
    [InlineData("INDEX ON VAL() TAG bad", 1229)]
    public void MicroVfp_RaisesOnExecutedStatementAndContinuesAfterCatch(string statement, int error)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_prg");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE probe (id I)");
        session.Execute("INSERT INTO probe VALUES (1)");
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"USE probe\nreached=1\nTRY\n{statement}\nreached=2\nCATCH TO err\nnumber=err.ErrorNo\nENDTRY\nafter=3");
        Assert.Equal(1, interpreter.Memory.Get("reached").AsInteger);
        Assert.Equal(error, interpreter.Memory.Get("number").AsInteger);
        Assert.Equal(3, interpreter.Memory.Get("after").AsInteger);
    }

    [Fact]
    public void MicroVfp_DoesNotRejectUnexecutedStatements()
    {
        using var session = new VfpSession();
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("reached=1\nIF .F.\nx=VAL()\nSELECT VAL() FROM missing\nINDEX ON VAL() TAG bad\nENDIF\nreached=2\nPROCEDURE unused\nx=VAL()\nENDPROC");
        Assert.Equal(2, interpreter.Memory.Get("reached").AsInteger);
    }

    [Theory]
    [InlineData("DATE(2024)", 1229)]
    [InlineData("DATETIME(2024)", 1229)]
    [InlineData("BITSET(1)", 1229)]
    [InlineData("BITCLEAR(1,1,1)", 1230)]
    [InlineData("BITNOT(1,1)", 1230)]
    public void RuntimeOnlyArity_IsLazy(string expression, int error)
    {
        var lazy = VfpExpression.Parse($"IIF(.T.,1,{expression})");
        Assert.Equal(1, lazy.Evaluate(TestRow.Empty).AsInteger);
        Assert.Equal(1, lazy.Compile()(TestRow.Empty).AsInteger);
        var parsed = VfpExpression.Parse(expression);
        Assert.Equal(error, Assert.Throws<ExpressionException>(() => parsed.Evaluate(TestRow.Empty)).VfpErrorNumber);
        Assert.Equal(error, Assert.Throws<ExpressionException>(() => parsed.Compile()(TestRow.Empty)).VfpErrorNumber);
    }

    [Theory]
    [InlineData("BITSET(.NULL.)")]
    [InlineData("BITCLEAR(.NULL.,.NULL.,.NULL.)")]
    [InlineData("BITNOT(.NULL.,.NULL.)")]
    public void BitFunctions_PropagateNullBeforeRuntimeArityError(string expression)
    {
        var parsed = VfpExpression.Parse(expression);
        Assert.True(parsed.Evaluate(TestRow.Empty).IsNull);
        Assert.True(parsed.Compile()(TestRow.Empty).IsNull);
    }
}
