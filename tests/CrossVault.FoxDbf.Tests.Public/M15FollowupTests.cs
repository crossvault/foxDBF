using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class M15FollowupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedArray_UnattendedWriteKeepsOldIndexButChangesData(bool cached)
    {
        using var temp = new MicroVfpTestSupport.TempDir("m15unattended");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("DIMENSION arr[3]\narr[1]=10\narr[2]=20\narr[3]=30\nCREATE TABLE t (id I, note M)\nINSERT INTO t VALUES (1,'a')\nINDEX ON arr(MAX(1,id)) TAG kk");
        if (cached) interpreter.Execute("REPLACE id WITH 2");
        interpreter.Execute("RELEASE arr\n=SYS(2335,0)");
        byte[] index = File.ReadAllBytes(temp.File("t.cdx"));
        interpreter.Execute("REPLACE id WITH 3, note WITH 'changed'");
        Assert.Equal(3, interpreter.EvalExpression("id").AsNumber);
        Assert.Equal("changed", interpreter.EvalExpression("note").AsString);
        Assert.Equal(index, File.ReadAllBytes(temp.File("t.cdx")));
    }

    [Fact]
    public void CreateBinary_UsesVarbinaryRepresentationAndRoundTripsBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var session = new VfpSession();
        session.Context.Encoding = Encoding.GetEncoding(1252);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("q=CREATEBINARY(CHR(0)+CHR(128)+CHR(255))");
        var value = interpreter.EvalExpression("CREATEBINARY(q)");
        Assert.Equal(VfpType.Character, value.Type);
        Assert.True(value.IsBinary);
        Assert.Equal(new byte[] { 0, 128, 255 }, Assert.IsType<byte[]>(value.ToClr()));
        Assert.Equal(VfpValue.Varbinary([0, 128, 255]), value);
        Assert.Equal(VfpValue.Varbinary([0, 128, 255]).GetHashCode(), value.GetHashCode());
        Assert.Equal("Q", interpreter.EvalExpression("TYPE('CREATEBINARY(q)')").AsString);
        Assert.Equal("0080FF", interpreter.EvalExpression("TRANSFORM(q)").AsString);
    }

    [Theory]
    [InlineData("TAG kk OF extra", "extra.cdx")]
    [InlineData("TO extra", "extra.idx")]
    public void AdditionalUniqueIndex_DoesNotResurrectSuppressedDuplicates(string clause, string file)
    {
        using var temp = new MicroVfpTestSupport.TempDir("m15unique");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"CREATE TABLE t (id I)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (1)\nINDEX ON id {clause} UNIQUE\nGO 1\nREPLACE id WITH 2\nSEEK 1");
        Assert.False(interpreter.EvalExpression("FOUND()").AsLogical);
        Assert.True(File.Exists(temp.File(file)));
    }
}
