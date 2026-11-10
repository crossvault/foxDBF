using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class ArityReviewTests
{
    public static IEnumerable<object[]> Errors => ArityReviewData.Errors().Select(c => new object[] { c.Code, c.Error });
    public static IEnumerable<object[]> Blanks => ArityReviewData.IsBlank().Select(c => new object[] { c.Code, c.Expected });
    public static IEnumerable<object[]> Arrays => ArityReviewData.Arrays().Select(c => new object[] { c.Code, c.Expected });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidIndexArity_Use_MatchesVfp9(bool unattended)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_index_use");
        using (var writer = DbfWriter.Create(dir.File("probe.dbf"), new[] { new DbfColumnDef("cc", 'C', 10) }))
        {
            writer.AppendRecord("abc");
            writer.CreateTag(new CdxTagDefinition("bad", "LEFT(cc,1)"));
        }
        PatchIndex(dir.File("probe.cdx"));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        if (unattended) interpreter.Execute("=SYS(2335,0)");
        interpreter.Execute("number=0\nTRY\nUSE probe\nCATCH TO err\nnumber=err.ErrorNo\nENDTRY");
        Assert.Equal(114, interpreter.Memory.Get("number").AsInteger);
        Assert.False(interpreter.EvalExpression("USED('probe')").AsLogical);
    }

    internal static void PatchIndex(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        byte[] old = Encoding.ASCII.GetBytes("LEFT(cc,1)");
        int offset = bytes.AsSpan().IndexOf(old);
        Assert.True(offset >= 0);
        Encoding.ASCII.GetBytes("LEFT(cc)  ").CopyTo(bytes, offset);
        File.WriteAllBytes(path, bytes);
    }

    [Theory]
    [MemberData(nameof(Errors))]
    public void ArrayAndScopeErrors_MatchVfp9(string code, int expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_review");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute(ArityReviewData.Setup);
        interpreter.Execute($"number=0\nTRY\n{code}\nCATCH TO err\nnumber=err.ErrorNo\nENDTRY");
        Assert.Equal(expected, interpreter.Memory.Get("number").AsInteger);
        interpreter.Execute($"number=0\nON ERROR number=ERROR()\n{code}\nON ERROR");
        Assert.Equal(expected, interpreter.Memory.Get("number").AsInteger);
    }

    [Theory]
    [MemberData(nameof(Blanks))]
    [MemberData(nameof(Arrays))]
    public void IsBlank_SqlScanAndXbase_MatchVfp9(string code, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_blank");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute(ArityReviewData.Setup + "\n" + code);
        Assert.Equal(expected, interpreter.Memory.Get("v").AsString);
    }

    [Fact]
    public void IsBlank_SqlWithoutInterpreter_UsesScanValues()
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_sql_plain");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using (var interpreter = new VfpInterpreter(session)) interpreter.Execute(ArityReviewData.Setup);
        var rows = session.Execute("SELECT ISBLANK(cc) AS b FROM probe")!.Rows;
        Assert.Equal(new[] { false, true }, rows.Select(r => (bool)r[0]!));
        Assert.Single(session.Execute("SELECT * FROM probe WHERE ISBLANK(cc)")!.Rows);
        Assert.Single(session.Execute("SELECT p.id FROM probe p JOIN src s ON p.id=s.sid WHERE ISBLANK(p.cc)")!.Rows);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("abc", false)]
    public void IsBlank_SqlCompiledAndInterpreted_Agree(string value, bool expected)
    {
        var expression = VfpExpression.ParseSql("ISBLANK(cc)");
        var row = new TestRow().Set("cc", value);
        Assert.Equal(expected, expression.Evaluate(row).AsLogical);
        Assert.Equal(expected, expression.Compile()(row).AsLogical);
        Assert.Equal(new[] { "cc" }, expression.ReferencedFields());
    }
}
