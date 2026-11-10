using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlJ1Tests
{
    [Theory]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    [InlineData("compact", false)]
    [InlineData("compact", true)]
    [InlineData("cdx", false)]
    [InlineData("cdx", true)]
    public void LateOpenedIndex_OuterSqlSnapshotSurvivesInnerBoundary(string format, bool commitInner)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_j1_outer");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup(format) + "\nUSE t");
        string[] files = ["t.dbf", "t.fpt", "t.cdx", IndexFile(format)];
        var before = files.ToDictionary(f => f, f => File.ReadAllBytes(dir.File(f)));
        vfp.Execute("BEGIN TRANSACTION");
        session.Execute("UPDATE t SET note='outer'");
        vfp.Execute("BEGIN TRANSACTION\nSET INDEX TO " + IndexFile(format));
        session.Execute("UPDATE t SET id=2,note='inner'");
        vfp.Execute((commitInner ? "END TRANSACTION" : "ROLLBACK") + "\nROLLBACK\nUSE");
        foreach (string file in files) Assert.Equal(before[file], File.ReadAllBytes(dir.File(file)));
        vfp.Execute("USE t INDEX " + IndexFile(format) + "\nSET ORDER TO 2");
        Assert.True(vfp.EvalExpression("SEEK(1)").AsLogical);
        Assert.False(vfp.EvalExpression("SEEK(2)").AsLogical);
    }

    public static IEnumerable<object[]> Cases =>
        from format in new[] { "legacy", "compact", "cdx" }
        from form in new[] { "array", "memvar", "select", "update", "delete" }
        from finish in new[] { "commit", "rollback", "nested", "innercommit" }
        select new object[] { format, form, finish };

    internal static string IndexFile(string format) => "extra." + (format == "cdx" ? "cdx" : "idx");

    internal static string Setup(string format) => $"""
        SET TABLEPROMPT OFF
        SET DELETED OFF
        CREATE TABLE t (id I, note M)
        INSERT INTO t VALUES (1,'before')
        INDEX ON id TAG id
        INDEX ON id {(format == "cdx" ? "TAG extra OF extra.cdx" : "TO extra.idx" + (format == "compact" ? " COMPACT" : ""))}
        USE
        CREATE TABLE src (id I, note M)
        INSERT INTO src VALUES (2,'after')
        USE
        """;

    internal static string Start(string format, string finish) => "USE t\nBEGIN TRANSACTION\n"
        + (finish is "nested" or "innercommit" ? "BEGIN TRANSACTION\n" : "")
        + "SET INDEX TO " + IndexFile(format) + "\nSET ORDER TO 2\n"
        + "DIMENSION aa[1,2]\naa[1,1]=2\naa[1,2]='after'\nm.id=2\nm.note='after'\n";

    internal static string Write(string form) => form switch
    {
        "array" => "INSERT INTO t FROM ARRAY aa",
        "memvar" => "INSERT INTO t FROM MEMVAR",
        "select" => "INSERT INTO t SELECT * FROM src",
        "update" => "UPDATE t SET id=2,note='after' WHERE id=1",
        _ => "DELETE FROM t WHERE id=1"
    };

    internal static string Finish(string finish) => finish == "innercommit" ? "END TRANSACTION\nROLLBACK" : finish == "commit" ? "END TRANSACTION" :
        "ROLLBACK" + (finish == "nested" ? "\nEND TRANSACTION" : "");

    internal const string State = "result=TRANSFORM(RECCOUNT())+'|'\nGO 1\nresult=result+TRANSFORM(id)+'|'+ALLTRIM(note)+'|'+TRANSFORM(DELETED())";

    internal static string Expected(string form, string finish) => finish != "commit" ? "1|1|before|.F." :
        form == "update" ? "1|2|after|.F." : form == "delete" ? "1|1|before|.T." : "2|1|before|.F.";

    [Theory]
    [MemberData(nameof(Cases))]
    public void SessionSqlWrites_SnapshotAllFilesBeforeMutation(string format, string form, string finish)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_j1");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup(format) + "\n" + Start(format, finish));
        string[] files = ["t.dbf", "t.fpt", "t.cdx", IndexFile(format)];
        var before = files.ToDictionary(f => f, f => File.ReadAllBytes(dir.File(f)));
        // Exercise the session seam directly, including UPDATE/DELETE which also have a PRG fast path.
        session.Execute(Write(form));
        vfp.Execute(Finish(finish) + "\nUSE\nUSE t INDEX " + IndexFile(format) + "\nSET ORDER TO 2\n" + State);
        Assert.Equal(Expected(form, finish), vfp.Memory.Get("result").AsString);
        Assert.Equal(finish == "commit" && form != "delete", vfp.EvalExpression("SEEK(2)").AsLogical);
        Assert.Equal(!(finish == "commit" && form == "update"), vfp.EvalExpression("SEEK(1)").AsLogical);
        vfp.Execute("USE");
        if (finish != "commit")
            foreach (string file in files) Assert.Equal(before[file], File.ReadAllBytes(dir.File(file)));
    }
}

