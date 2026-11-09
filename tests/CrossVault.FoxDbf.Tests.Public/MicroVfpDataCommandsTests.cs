using System;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDataCommandsTests
{
    private sealed class Bench : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = new("data_commands");
        public VfpSession Session { get; } = new();
        public VfpInterpreter Vfp { get; }
        public Bench()
        {
            Session.OpenDirectory(_dir.Path);
            Vfp = new VfpInterpreter(Session);
        }
        public void Dispose() { Vfp.Dispose(); Session.Dispose(); _dir.Dispose(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void AppendBlank_PositionsNewRecord_AndMaintainsIndex(int buffering)
    {
        using var b = new Bench();
        b.Vfp.Execute($"CREATE TABLE t (id I, nm C(5))\nUSE t\nINDEX ON nm TAG nm\nSET MULTILOCKS ON\n=CURSORSETPROP('Buffering', {buffering})\nAPPEND BLANK");
        Assert.False(b.Vfp.EvalExpression("EOF()").AsLogical);
        Assert.Equal(1, b.Vfp.EvalExpression("RECCOUNT()").AsNumber);
        b.Vfp.Execute("REPLACE id WITH 7, nm WITH 'key'");
        if (buffering > 1) b.Vfp.Execute("=TABLEUPDATE(.T.)");
        b.Vfp.Execute("SEEK 'key'");
        Assert.True(b.Vfp.EvalExpression("FOUND()").AsLogical);
        Assert.Equal(7, b.Vfp.EvalExpression("id").AsNumber);
    }

    [Fact]
    public void CreateTable_LeavesSelectedAliasOpen()
    {
        using var b = new Bench();
        b.Vfp.Execute("CREATE TABLE t (id I)");
        Assert.Equal("T", b.Vfp.EvalExpression("ALIAS()").AsString.ToUpperInvariant());
        Assert.True(b.Vfp.EvalExpression("USED('t')").AsLogical);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Insert_PositionsNewRecord(int buffering)
    {
        using var b = new Bench();
        b.Vfp.Execute($"CREATE TABLE t (id I)\nUSE t\nSET MULTILOCKS ON\n=CURSORSETPROP('Buffering', {buffering})\nINSERT INTO t VALUES (7)");
        Assert.False(b.Vfp.EvalExpression("EOF()").AsLogical);
        Assert.Equal(7, b.Vfp.EvalExpression("id").AsNumber);
    }

    [Theory]
    [InlineData("ON")]
    [InlineData("OFF")]
    public void SeekEmpty_FindsBlankKey(string exact)
    {
        using var b = new Bench();
        b.Vfp.Execute($"CREATE TABLE t (nm C(5))\nINSERT INTO t VALUES ('')\nUSE t\nINDEX ON nm TAG nm\nSET EXACT {exact}\nSEEK ''");
        Assert.True(b.Vfp.EvalExpression("FOUND()").AsLogical);
        Assert.Equal(1, b.Vfp.EvalExpression("RECNO()").AsNumber);
    }

    [Fact]
    public void ScatterName_PropertiesGatherBlankAdditiveMemoFields()
    {
        using var b = new Bench();
        b.Vfp.Execute("CREATE TABLE t (id I, nm C(5), note M)\nINSERT INTO t VALUES (1, 'one', 'memo')\nUSE t\nSCATTER NAME obj MEMO\ncopy = obj\nobj.nm = 'two'\nGATHER NAME obj MEMO");
        Assert.Equal("two", b.Vfp.EvalExpression("ALLTRIM(nm)").AsString);
        Assert.Equal("two", b.Vfp.EvalExpression("copy.nm").AsString);
        b.Vfp.Execute("SCATTER NAME obj BLANK FIELDS id ADDITIVE");
        Assert.Equal(0, b.Vfp.EvalExpression("obj.id").AsNumber);
        Assert.Equal("two", b.Vfp.EvalExpression("obj.nm").AsString);
        Assert.Equal("memo", b.Vfp.EvalExpression("obj.note").AsString);
    }

    [Fact]
    public void CatchObject_PropertyAssignment_UpdatesObject()
    {
        using var b = new Bench();
        b.Vfp.Execute("TRY\nTHROW 'original'\nCATCH TO problem\nproblem.Message = 'changed'\nENDTRY");
        Assert.Equal("changed", b.Vfp.EvalExpression("problem.Message").AsString);
        Assert.False(b.Vfp.Memory.IsDefined("problem.Message"));
    }

    [Theory]
    [InlineData("ALL", 3)]
    [InlineData("FOR id > 1", 2)]
    [InlineData("WHILE id < 3", 1)]
    [InlineData("NEXT 1", 1)]
    [InlineData("REST", 2)]
    public void ReplaceScope_UpdatesExpectedRows(string scope, int count)
    {
        using var b = new Bench();
        b.Vfp.Execute("CREATE TABLE t (id I, n I)\nINSERT INTO t VALUES (1,0)\nINSERT INTO t VALUES (2,0)\nINSERT INTO t VALUES (3,0)\nUSE t\nGO 2");
        b.Vfp.Execute($"REPLACE n WITH 1 {scope}\nCOUNT FOR n = 1 TO matches");
        Assert.Equal(count, b.Vfp.Memory.Get("matches").AsNumber);
    }

    [Fact]
    public void InsertValues_UsesInterpreter_ExactlyOnce()
    {
        using var b = new Bench();
        b.Vfp.Load(PrgParser.Parse("FUNCTION bump\n calls = calls + 1\n RETURN calls\nENDFUNC"));
        b.Vfp.Execute("CREATE TABLE t (id I, nm C(5), area I)\nPUBLIC calls, text\ncalls = 0\ntext = 'value'\nINSERT INTO t VALUES (bump(), m.text, SELECT())\nUSE t");
        Assert.Equal(1, b.Vfp.Memory.Get("calls").AsNumber);
        Assert.Equal(1, b.Vfp.EvalExpression("id").AsNumber);
        Assert.Equal("value", b.Vfp.EvalExpression("nm").AsString);
        Assert.True(b.Vfp.EvalExpression("area > 0").AsLogical);
    }
}
