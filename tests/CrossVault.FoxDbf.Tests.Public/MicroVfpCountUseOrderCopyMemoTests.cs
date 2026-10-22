using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// COUNT, <c>USE … ORDER [TAG] … [OF cdx] [ASCENDING|DESCENDING]</c> and <c>COPY MEMO … ADDITIVE</c>. The
/// expected values are the VFP9 runtime's (pinned by the internal MicroVfpTryCatchVfp9OracleTests run).
/// Table: id 1..5, nm a/b/a/c/a, record 2 deleted, SET DELETED ON; tag nm ascending, tag id DESCENDING.
/// </summary>
public sealed class MicroVfpCountUseOrderCopyMemoTests : IDisposable
{
    private readonly MicroVfpTestSupport.TempDir _dir = new("count_useorder");
    private readonly VfpSession _session;
    private readonly VfpInterpreter _i;

    public MicroVfpCountUseOrderCopyMemoTests()
    {
        using (var w = DbfWriter.Create(_dir.File("ct.dbf"), [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("NM", 'C', 5)]))
        {
            w.AppendRecord(1, "a");
            w.AppendRecord(2, "b");
            w.AppendRecord(3, "a");
            w.AppendRecord(4, "c");
            w.AppendRecord(5, "a");
        }
        using (var w = DbfWriter.Create(_dir.File("mt.dbf"), [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("MM", 'M', 4)]))
            w.AppendRecord(1, "hello");
        _session = new VfpSession();
        _session.OpenDirectory(_dir.Path);
        _i = new VfpInterpreter(_session);
        _i.Execute("USE ct EXCLUSIVE\nINDEX ON nm TAG nm\nINDEX ON id TAG id DESCENDING\nGO 2\nDELETE\nSET ORDER TO\nUSE");
    }

    public void Dispose()
    {
        _i.Dispose();
        _session.Dispose();
        _dir.Dispose();
    }

    private string Probe(string setup, string count)
    {
        _i.Execute("USE ct\n" + setup + "\n" + count);
        return _i.EvalExpression("TRANSFORM(nCnt) + '|' + TRANSFORM(RECNO()) + '|' + TRANSFORM(EOF())").AsString;
    }

    [Theory]
    [InlineData("GO 3", "COUNT TO nCnt", "4|6|.T.")]
    [InlineData("", "COUNT FOR nm = 'a' TO nCnt", "3|6|.T.")]
    [InlineData("", "COUNT TO nCnt FOR id > 1", "3|6|.T.")]           // clause order is free
    [InlineData("GO 3", "COUNT REST TO nCnt", "3|6|.T.")]
    [InlineData("GO 1", "COUNT NEXT 3 TO nCnt", "3|4|.F.")]           // deleted rec 2 is not visited
    [InlineData("GO 1", "COUNT WHILE id < 4 TO nCnt", "2|4|.F.")]     // parks on the failing record
    [InlineData("", "COUNT RECORD 4 TO nCnt", "1|4|.F.")]
    [InlineData("", "COUNT RECORD 2 TO nCnt", "1|2|.F.")]             // RECORD n ignores SET DELETED
    [InlineData("", "COUNT FOR id > 100 TO nCnt", "0|6|.T.")]
    [InlineData("GO 3", "COUNT NEXT 0 TO nCnt", "0|3|.F.")]
    [InlineData("SET FILTER TO id > 2", "COUNT TO nCnt", "3|6|.T.")]
    [InlineData("SET ORDER TO nm\nSET KEY TO 'a'", "COUNT TO nCnt", "3|6|.T.")]
    [InlineData("SET DELETED OFF", "COUNT TO nCnt", "5|6|.T.")]
    [InlineData("GO 2", "COUNT NEXT 1 TO nCnt", "1|2|.F.")]               // REST/NEXT process the current record
    [InlineData("GO 2", "COUNT NEXT 2 TO nCnt", "2|3|.F.")]               //   even when it is deleted …
    [InlineData("GO 2", "COUNT REST TO nCnt", "4|6|.T.")]                 //   … only the skipped-to records
    [InlineData("GO 2", "COUNT NEXT 2 FOR nm = 'a' TO nCnt", "1|3|.F.")]  //   honour SET DELETED
    public void Count_MatchesVfp9(string setup, string count, string expected)
    {
        try { Assert.Equal(expected, Probe(setup, count)); }
        finally { _i.Execute("SET DELETED ON"); }
    }

    [Fact]
    public void Count_ResultIsNumeric_AndNeedsAnOpenTable()
    {
        Probe("", "COUNT TO nCnt");
        Assert.Equal("N", _i.EvalExpression("VARTYPE(nCnt)").AsString);
        _i.Execute("USE");
        Assert.Equal(52, Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute("COUNT TO nCnt")).VfpErrorNumber);
    }

    [Fact]
    public void Count_IsNoLongerSilentlySkipped()
    {
        _i.Execute("nCnt = .NULL.\nUSE ct\nCOUNT TO nCnt");
        Assert.Equal(4m, _i.EvalExpression("nCnt").AsNumber);
    }

    [Theory]
    [InlineData("USE ct ORDER TAG nm", "NM|1|.F.")]
    [InlineData("USE ct ORDER TAG id", "ID|5|.T.")]
    [InlineData("USE ct ORDER TAG id ASCENDING", "ID|1|.F.")]
    [InlineData("USE ct ORDER TAG nm DESCENDING", "NM|4|.T.")]
    [InlineData("USE ct ORDER TAG nm OF ct.cdx", "NM|1|.F.")]
    [InlineData("USE ct ORDER nm DESCENDING", "NM|4|.T.")]
    [InlineData("USE ct ORDER 2", "ID|5|.T.")]
    public void UseOrderTag_MatchesVfp9(string use, string expected)
    {
        _i.Execute(use);
        Assert.Equal(expected, _i.EvalExpression("ORDER() + '|' + TRANSFORM(RECNO()) + '|' + TRANSFORM(DESCENDING())").AsString);
    }

    [Fact]
    public void UseOrderTag_UnknownTag_Raises12_AndLeavesTheTableOpen()
    {
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute("USE ct ORDER TAG nosuch"));
        Assert.Equal(12, ex.VfpErrorNumber);
        Assert.Equal("Variable 'NOSUCH' is not found.", ex.Message);
        Assert.Equal("CT", _i.EvalExpression("ALIAS()").AsString);
    }

    [Fact]
    public void CopyMemoAdditive_AppendsToTheExistingFile()
    {
        File.WriteAllText(_dir.File("mout.txt"), "pre-");
        _i.Execute("USE mt\nCOPY MEMO mm TO mout.txt ADDITIVE");
        Assert.Equal("pre-hello", File.ReadAllText(_dir.File("mout.txt")));
        _i.Execute("COPY MEMO mm TO mout.txt");
        Assert.Equal("hello", File.ReadAllText(_dir.File("mout.txt")));
    }

    [Fact]
    public void CopyMemoAdditive_MissingFile_RaisesError1()
    {
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => _i.Execute("USE mt\nCOPY MEMO mm TO mnew.txt ADDITIVE"));
        Assert.Equal(1, ex.VfpErrorNumber);
        Assert.False(File.Exists(_dir.File("mnew.txt")));
    }
}
