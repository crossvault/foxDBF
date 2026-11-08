using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSmallBacklogTests
{
    // Pinned by MicroVfpSmallBacklogVfp9OracleTests: VFP9 accepts => and =<, and prints => and <=.
    [Theory]
    [InlineData("id >= 2", "ID=>2")]
    [InlineData("id => 2", "ID=>2")]
    [InlineData("id <= 2", "ID<=2")]
    [InlineData("id =< 2", "ID<=2")]
    [InlineData("id <> 2", "ID#2")]
    [InlineData("id # 2", "ID#2")]
    [InlineData("id != 2", "ID#2")]
    [InlineData("id == 2", "ID==2")]
    [InlineData("!x", ".NOT.X")]
    [InlineData("NOT x", ".NOT.X")]
    [InlineData("'a' $ name", "\"a\"$NAME")]
    [InlineData("id > 5 AND id < 10", "ID>5.AND.ID<10")]
    [InlineData("id > 5 .AND. id < 10", "ID>5.AND.ID<10")]
    [InlineData("id > 5 OR id < 2", "ID>5.OR.ID<2")]
    [InlineData("id > 5 .OR. id < 2", "ID>5.OR.ID<2")]
    [InlineData("NOT id > 5", ".NOT.ID>5")]
    [InlineData(".NOT. id > 5", ".NOT.ID>5")]
    [InlineData("id > 5 AND NOSUCHFN(id)", "ID>5.AND.NOSUCHFN(ID)")]
    [InlineData("name = 'a >= => <= =< b'", "NAME=\"a >= => <= =< b\"")]
    [InlineData("id >= 1 + 1", "ID=>2")]
    [InlineData("id => -2 AND id =< 3", "ID=>-2.AND.ID<=3")]
    public void ExpressionText_FilterNormalizeKeyAndFor_MatchVfp9(string expression, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("m5operators");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), new[]
               {
                   new DbfColumnDef("id", 'I'), new DbfColumnDef("x", 'L', 1),
                   new DbfColumnDef("name", 'C', 20),
               }))
        {
            writer.AppendRecord(1, false, "a >= => <= =< b");
            writer.CreateTag(new CdxTagDefinition("k", expression, expression));
        }
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"USE t ORDER k\nSET FILTER TO {expression}");
        string literal = string.Join("+", expression.Select(c => $"CHR({(int)c})"));
        Assert.Equal(expected, interpreter.EvalExpression("FILTER()").AsString);
        Assert.Equal(expected, interpreter.EvalExpression("SET('FILTER')").AsString);
        Assert.Equal(expected, interpreter.EvalExpression($"NORMALIZE({literal})").AsString);
        Assert.Equal(expected, interpreter.EvalExpression("KEY()").AsString);
        // FOR(), unlike KEY(), uppercases even string literal contents.
        Assert.Equal(expected.ToUpperInvariant(), interpreter.EvalExpression("FOR()").AsString);
        interpreter.Execute("SET FILTER TO");
        Assert.Equal("", interpreter.EvalExpression("FILTER()").AsString);
        Assert.Equal("", interpreter.EvalExpression("SET('FILTER')").AsString);
    }

    [Theory]
    [InlineData("MACHINE", "OFF")]
    [InlineData("MACHINE", "ON")]
    [InlineData("GENERAL", "OFF")]
    [InlineData("GENERAL", "ON")]
    public void IndexSeek_UsesSeekRulesAndOnlyMovesOnRequestedHit(string collation, string exact)
    {
        using var dir = new MicroVfpTestSupport.TempDir("m1");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), new[] { new DbfColumnDef("nm", 'C', 10) }))
        {
            foreach (string value in new[] { "Z", "Abc", "Abcd", "Abc" }) writer.AppendRecord(value);
            writer.Delete(1);
            writer.CreateTag(new CdxTagDefinition("kn", "nm", collation: collation), includeDeleted: true);
        }
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"USE t ORDER kn\nSET DELETED ON\nSET EXACT {exact}\nGO 1");
        Assert.Equal(exact == "OFF", interpreter.EvalExpression("INDEXSEEK('Ab')").AsLogical);
        Assert.Equal(1, interpreter.EvalExpression("RECNO()").AsNumber);
        Assert.True(interpreter.EvalExpression("INDEXSEEK('Abc', .T.)").AsLogical);
        Assert.Equal(4, interpreter.EvalExpression("RECNO()").AsNumber);
        Assert.True(interpreter.EvalExpression("FOUND()").AsLogical);
        Assert.False(interpreter.EvalExpression("INDEXSEEK('missing', .T.)").AsLogical);
        Assert.Equal(4, interpreter.EvalExpression("RECNO()").AsNumber);
        Assert.False(interpreter.EvalExpression("FOUND()").AsLogical);
        interpreter.Execute("SET KEY TO 'Z'");
        Assert.False(interpreter.EvalExpression("INDEXSEEK('Abc', .T.)").AsLogical);
        Assert.Equal(4, interpreter.EvalExpression("RECNO()").AsNumber);
    }

    [Theory]
    [InlineData("id > 5 AND id < 10", "ID>5.AND.ID<10")]
    [InlineData("id > 5 OR id < 2", "ID>5.OR.ID<2")]
    [InlineData("NOT id > 5", ".NOT.ID>5")]
    [InlineData("id > 5 AND NOSUCHFN(id)", "ID>5.AND.NOSUCHFN(ID)")]
    public void Filter_AndSetFilter_ReturnVfpLogicalOperators(string expression, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("m5");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), new[] { new DbfColumnDef("id", 'I') }))
            writer.AppendRecord(1);
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"USE t\nSET FILTER TO {expression}");
        Assert.Equal(expected, interpreter.EvalExpression("FILTER()").AsString);
        Assert.Equal(expected, interpreter.EvalExpression("SET('FILTER')").AsString);
    }

    [Theory]
    [InlineData("NOT lg", ".NOT.LG")]
    [InlineData("n45 > 3 AND n45 < 10", "N45>3.AND.N45<10")]
    [InlineData("UPPE(name)", "UPPER(NAME)")]
    [InlineData("n45 * (2 + 3)", "N45*5")]
    [InlineData("name = 'a b' OR name = [x\"y]", "NAME=\"a b\".OR.NAME='x\"y'")]
    [InlineData("DELE() = .f.", "DELETED()=.F.")]
    public void Normalize_UsesVfpDecompilerText(string expression, string expected)
    {
        using var session = new VfpSession();
        var interpreter = new VfpInterpreter(session);
        string literal = string.Join("+", expression.Select(c => $"CHR({(int)c})"));
        Assert.Equal(expected, interpreter.EvalExpression($"NORMALIZE({literal})").AsString);
    }
}
