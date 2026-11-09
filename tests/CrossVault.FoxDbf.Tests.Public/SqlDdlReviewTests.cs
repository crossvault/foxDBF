using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlDdlReviewTests
{
    [Fact]
    public void Create_Does_Not_Overwrite_Or_Delete_A_Concurrent_Table()
    {
        using var dir = new SqlTestSupport.TempDir();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        byte[] other = [1, 2, 3, 4];
        DbfWriter.FaultBeforeDbfCommit = () => File.WriteAllBytes(dir.File("t.dbf"), other);
        try
        {
            Assert.Throws<IOException>(() => session.Execute("CREATE TABLE t (id I UNIQUE, note M)"));
        }
        finally { DbfWriter.FaultBeforeDbfCommit = null; }
        Assert.Equal(other, File.ReadAllBytes(dir.File("t.dbf")));
        Assert.False(File.Exists(dir.File("t.fpt")));
        Assert.False(File.Exists(dir.File("t.cdx")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidate_Memo_Expression_Validates_Pending_Content(bool batch)
    {
        using var dir = new SqlTestSupport.TempDir();
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("id", 'I'), new DbfColumnDef("note", 'M')]))
        {
            writer.CreateTag(new CdxTagDefinition("memo", "LEFT(note,3)") { Candidate = true });
            writer.AppendRecord(1, "one");
            if (batch) writer.AppendRecords(new object?[][] { [2, "two"], [3, "six"] });
            else { writer.AppendRecord(2, "two"); writer.AppendRecord(3, "six"); }
        }
        string[] files = ["t.dbf", "t.fpt", "t.cdx"];
        var before = files.Select(f => File.ReadAllBytes(dir.File(f))).ToArray();
        using (var writer = DbfWriter.Open(dir.File("t.dbf")))
        {
            Assert.Equal(1884, Assert.Throws<DbfWriteException>(() => writer.AppendRecord(4, "one more")).VfpErrorNumber);
            Assert.Equal(1884, Assert.Throws<DbfWriteException>(() => writer.UpdateRecord(1, new object?[] { 2, "one more" })).VfpErrorNumber);
        }
        for (int i = 0; i < files.Length; i++) Assert.Equal(before[i], File.ReadAllBytes(dir.File(files[i])));
    }

    [Theory]
    [InlineData("before_dcx")]
    [InlineData("published1")]
    [InlineData("published2")]
    [InlineData("published0")]
    public void Create_Injected_Container_Failure_Restores_All_Files(string phase)
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), []);
        string[] files = ["catalog.dbc", "catalog.dct", "catalog.dcx"];
        var before = files.Select(f => File.ReadAllBytes(dir.File(f))).ToArray();
        using var reader = new VfpSession();
        reader.OpenDatabase(dir.File("catalog.dbc"));
        using var session = new VfpSession();
        session.OpenDatabase(dir.File("catalog.dbc"));
        DbfDatabaseBuilder.AddTableFaultForTests = point =>
        {
            if (point != phase) return;
            using var contender = DbfWriter.Open(dir.File("catalog.dbc"), new DbfOptions { LockMode = LockMode.Shared });
            Assert.ThrowsAny<IOException>(() => contender.LockFile());
            throw new InvalidOperationException("Injected DBC fault");
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => session.Execute("CREATE TABLE t (id I PRIMARY KEY, note M)"));
        }
        finally { DbfDatabaseBuilder.AddTableFaultForTests = null; }
        for (int i = 0; i < files.Length; i++) Assert.Equal(before[i], File.ReadAllBytes(dir.File(files[i])));
        foreach (string ext in new[] { "dbf", "fpt", "cdx" }) Assert.False(File.Exists(dir.File("t." + ext)));
        Assert.DoesNotContain("t", session.Database!.TableNames);
        session.Execute("CREATE TABLE t (id I PRIMARY KEY)");
    }

    [Theory]
    [InlineData("append")]
    [InlineData("batch_existing")]
    [InlineData("batch_internal")]
    [InlineData("update")]
    public void Candidate_Refusal_Preserves_Dbf_Fpt_Cdx(string operation)
    {
        using var dir = new SqlTestSupport.TempDir();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE t (id I UNIQUE, note M)");
        using (var writer = DbfWriter.Open(dir.File("t.dbf")))
        {
            writer.AppendRecord(1, "original");
            writer.AppendRecord(2, "second");
        }
        string[] files = ["t.dbf", "t.fpt", "t.cdx"];
        var before = files.Select(f => File.ReadAllBytes(dir.File(f))).ToArray();
        using (var writer = DbfWriter.Open(dir.File("t.dbf")))
        {
            void Write()
            {
                switch (operation)
                {
                    case "append": writer.AppendRecord(1, "rejected"); break;
                    case "batch_existing": writer.AppendRecords(new object?[][] { [3, "new"], [1, "rejected"] }); break;
                    case "batch_internal": writer.AppendRecords(new object?[][] { [3, "new"], [3, "rejected"] }); break;
                    case "update": writer.UpdateRecord(1, new object?[] { 1, "rejected" }); break;
                }
            }
            Assert.Equal(1884, Assert.Throws<DbfWriteException>(Write).VfpErrorNumber);
            Assert.False(writer.ReindexNeeded);
        }
        for (int i = 0; i < files.Length; i++) Assert.True(before[i].SequenceEqual(File.ReadAllBytes(dir.File(files[i]))), files[i] + " changed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidate_Reccount_Uses_Each_Appended_Record_Count(bool batch)
    {
        using var dir = new SqlTestSupport.TempDir();
        using var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("id", 'I')]);
        writer.CreateTag(new CdxTagDefinition("count", "RECCOUNT()") { Candidate = true });
        if (batch) writer.AppendRecords(new object?[][] { [1], [2], [3] });
        else for (int i = 1; i <= 3; i++) writer.AppendRecord(i);
        Assert.Equal(3, writer.RecordCount);
    }

    [Fact]
    public void Create_Member_With_Another_Open_Session()
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), []);
        using var reader = new VfpSession();
        reader.OpenDatabase(dir.File("catalog.dbc"));
        using var writer = new VfpSession();
        writer.OpenDatabase(dir.File("catalog.dbc"));
        writer.Execute("CREATE TABLE t (id I PRIMARY KEY, note M)");
        Assert.Contains("t", writer.Database!.TableNames);
    }

    [Fact]
    public void Create_Dcx_Write_Failure_Preserves_Container_And_Removes_New_Files()
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), []);
        string[] files = ["catalog.dbc", "catalog.dct", "catalog.dcx"];
        var before = files.Select(f => File.ReadAllBytes(dir.File(f))).ToArray();
        using var session = new VfpSession();
        session.OpenDatabase(dir.File("catalog.dbc"));
        using (var deny = new FileStream(dir.File("catalog.dcx"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => session.Execute("CREATE TABLE t (id I PRIMARY KEY, note M)"));
        for (int i = 0; i < files.Length; i++) Assert.True(before[i].SequenceEqual(File.ReadAllBytes(dir.File(files[i]))), files[i] + " changed");
        foreach (string ext in new[] { "dbf", "fpt", "cdx" }) Assert.False(File.Exists(dir.File("t." + ext)));
        Assert.DoesNotContain("t", session.Database!.TableNames);
    }
}
