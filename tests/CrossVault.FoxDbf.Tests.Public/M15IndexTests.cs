using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class M15IndexTests
{
    [Theory]
    [InlineData("keyfn(id)","keepfn(id)")]
    [InlineData("arr(MAX(1,id))", "id > 0")]
    [InlineData("ASCAN(arr,id*10)", "id > 0")]
    [InlineData("PADL(TRANSFORM(id),4)", "VARTYPE(id) = 'N'")]
    public void HostedKeyAndFor_BuildAndMaintain(string key,string filter)
    {
        using var temp = new MicroVfpTestSupport.TempDir("m15");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("""
            FUNCTION keyfn(n)
            RETURN n*10
            ENDFUNC
            FUNCTION keepfn(n)
            RETURN n > 0
            ENDFUNC
            DIMENSION arr[5]
            arr[1]=10
            arr[2]=20
            arr[3]=30
            arr[4]=40
            arr[5]=50
            CREATE TABLE t (id I)
            INSERT INTO t VALUES (1)
            INSERT INTO t VALUES (2)
            """);
        interpreter.Execute($"INDEX ON {key} TAG kk FOR {filter}");
        interpreter.Execute("GO 1\nREPLACE id WITH 3\nINSERT INTO t VALUES (4)\nSET ORDER TO kk");
        interpreter.Execute(key.StartsWith("PADL") ? "SEEK '   3'" : key.StartsWith("ASCAN") ? "SEEK 3" : "SEEK 30");
        Assert.True(interpreter.EvalExpression("FOUND()").AsLogical);
        Assert.Equal(1,interpreter.EvalExpression("RECNO()").AsInteger);
    }

    [Theory]
    [InlineData("CEILING(1.2)",2)]
    [InlineData("FLOOR(-1.2)",-2)]
    [InlineData("SQRT(9)",3)]
    [InlineData("BITAND(7,3)",3)]
    [InlineData("NVL(.NULL.,7)",7)]
    [InlineData("EVL(0,7)",7)]
    public void MissingScalar_IsAvailableWithoutInterpreter(string expression,int expected)
    {
        var e=VfpExpression.Parse(expression);
        Assert.Equal(expected,e.Evaluate(new EmptyRow()).AsInteger);
        Assert.Equal(expected,e.Compile()(new EmptyRow()).AsInteger);
    }

    [Theory]
    [InlineData("DTOS({^0001-01-01})", "00010101")]
    [InlineData("DTOS({^0001-01-01}+0)", "00010101")]
    [InlineData("TTOC({^0001-01-01 00:00:00},1)", "00010101000000")]
    public void YearOneLiteral_SurvivesCompiledExpressions(string text, string expected)
    {
        var expression = VfpExpression.Parse(text);
        Assert.Equal(expected, expression.Evaluate(new EmptyRow()).AsString);
        Assert.Equal(expected, expression.Compile()(new EmptyRow()).AsString);
    }

    [Fact]
    public void UnpackCompatibilityStub_DoesNotPropagateNull()
    {
        var expression = VfpExpression.Parse("UNPACK(.NULL.)");
        Assert.Equal(VfpValue.Logical(false), expression.Evaluate(new EmptyRow()));
        Assert.Equal(VfpValue.Logical(false), expression.Compile()(new EmptyRow()));
    }

    [Fact]
    public void YearOne_DtosIsNotEmpty_AtBuildAndUpdate()
    {
        using var temp = new MicroVfpTestSupport.TempDir("m15date");
        string path = temp.File("t.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("d", 'D', 8), new DbfColumnDef("x", 'I', 4)]))
        {
            writer.AppendRecord(null, 0);
            writer.AppendRecord(new DateOnly(1, 1, 2), 0);
        }
        // The CLR writer API uses MinValue as empty. Seed the valid VFP on-disk year-one date explicitly.
        var bytes = File.ReadAllBytes(path);
        int header = BitConverter.ToUInt16(bytes, 8), record = BitConverter.ToUInt16(bytes, 10);
        System.Text.Encoding.ASCII.GetBytes("00010101").CopyTo(bytes, header + record + 1);
        File.WriteAllBytes(path, bytes);
        using (var writer = DbfWriter.Open(path))
        {
            writer.CreateTag(new CdxTagDefinition("kk", "DTOS(d)"), includeDeleted: true);
            writer.UpdateRecord(1, [DbfWriter.KeepValue, 1]);
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        Assert.Equal(new[] { "        ", "00010101" }, cdx.Tag("kk")!.EnumerateEntries().Select(e => System.Text.Encoding.ASCII.GetString(e.Key)));
    }

    [Fact]
    public void ReleasedArray_RefusesCachedWriterBeforeDbfMemoOrCdxChanges()
    {
        using var temp = new MicroVfpTestSupport.TempDir("m15missing");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("DIMENSION arr[3]\narr[1]=10\narr[2]=20\narr[3]=30\nCREATE TABLE t (id I, note M)\nINSERT INTO t VALUES (1,'a')\nINDEX ON arr(MAX(1,id)) TAG kk\nREPLACE id WITH 2\nRELEASE arr");
        var before = new[] { "t.dbf", "t.fpt", "t.cdx" }.ToDictionary(temp.File, n => ReadShared(temp.File(n)));
        var error = Assert.Throws<MicroVfpRuntimeException>(() => interpreter.Execute("REPLACE id WITH 3, note WITH 'changed'"));
        Assert.Equal(1, error.VfpErrorNumber);
        Assert.Equal("File 'arr.prg' does not exist.", error.Message);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, ReadShared(path));
    }

    [Fact]
    public void NativeKey_DoesNotEnterInterpreterHost()
    {
        using var temp = new MicroVfpTestSupport.TempDir("m15native");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        session.Context.IndexEvaluator = (_, _, _) => throw new Exception("Native key entered host");
        interpreter.Execute("CREATE TABLE t (id I)\nINSERT INTO t VALUES (1)\nINDEX ON CEILING(id)+1 TAG kk\nREPLACE id WITH 2\nINSERT INTO t VALUES (3)");
        interpreter.Execute("SEEK 4");
        Assert.True(interpreter.EvalExpression("FOUND()").AsLogical);
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private sealed class EmptyRow : IRowContext
    {
        public object? GetField(string name)=>null;
        public int RecNo=>0;
        public int RecCount=>0;
        public bool Deleted=>false;
    }
}
