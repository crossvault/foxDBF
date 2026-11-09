using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlDdlCreateClausesTests
{
    [Theory]
    [InlineData("TABLE", "FREE CODEPAGE = 850", 2)]
    [InlineData("DBF", "FREE CODEPAGE = 850", 2)]
    [InlineData("TABLE", "FREE", 3)]
    [InlineData("TABLE", "CODEPAGE = 0", 0)]
    [InlineData("TABLE", "CODEPAGE = 1252.9", 3)]
    [InlineData("DBF", "CODEPAGE = (850)", 2)]
    [InlineData("DBF", "CODEPAGE = 800+50", 2)]
    [InlineData("DBF", "", 3)]
    public void Create_Clauses_Work_In_MicroVfp_And_Ado(string verb, string clauses, byte marker)
    {
        using var dir = new SqlTestSupport.TempDir();
        using (var session = new VfpSession())
        {
            session.OpenDirectory(dir.Path);
            using var interpreter = new VfpInterpreter(session);
            interpreter.Execute($"CREATE {verb} micro {clauses} (id I, note M)\nINSERT INTO micro VALUES (1, 'Grüße')\nUSE");
        }
        using (var connection = new FoxDbfConnection($"Data Source={dir.Path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE {verb} ado {clauses} (id I, note M)";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO ado VALUES (1, 'Grüße')";
            command.ExecuteNonQuery();
        }
        foreach (string name in new[] { "micro", "ado" })
        {
            Assert.Equal(marker, File.ReadAllBytes(dir.File(name + ".dbf"))[29]);
            using var table = DbfTable.Open(dir.File(name + ".dbf"));
            Assert.Equal("Grüße", table.GetRecord(0)!.Value["note"]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("NAME longname")]
    public void Create_OpenDatabase_Rejects_Unimplemented_Membership_Before_Writing(string clauses)
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDatabase(dir.File("catalog.dbc"));
        Assert.Throws<NotSupportedException>(() => session.Execute($"CREATE TABLE t {clauses} (id I)"));
        Assert.False(File.Exists(dir.File("t.dbf")));
    }

    [Fact]
    public void Create_Free_With_OpenDatabase_Writes_No_Backlink()
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDatabase(dir.File("catalog.dbc"));
        session.Execute("CREATE TABLE t FREE (id I)");
        using var table = DbfTable.Open(dir.File("t.dbf"));
        Assert.All(File.ReadAllBytes(dir.File("t.dbf")).Skip(65).Take(263), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("NAME longname (id I)", 1520)]
    [InlineData("NAME longname FREE (id I)", 10)]
    [InlineData("FREE NAME longname (id I)", 10)]
    [InlineData("CODEPAGE = 850 FREE (id I)", 10)]
    [InlineData("FREE FREE (id I)", 10)]
    [InlineData("CODEPAGE 850 (id I)", 10)]
    [InlineData("CODEPAGE = 65001 (id I)", 1914)]
    [InlineData("CODEPAGE = -1 (id I)", 1914)]
    [InlineData("CODEPAGE = 99999 (id I)", 1914)]
    [InlineData("(id I PRIMARY KEY)", 1580)]
    [InlineData("(id I REFERENCES parent)", 1580)]
    [InlineData("(id I DEFAULT 7)", 1580)]
    [InlineData("(id I, CHECK id > 0 ERROR 'positive')", 1580)]
    [InlineData("(id I, FOREIGN KEY id TAG fk REFERENCES parent)", 1580)]
    [InlineData("(id I) PRIMARY KEY id TAG pk", 36)]
    [InlineData("(id I) CHECK id > 0 ERROR 'positive'", 36)]
    [InlineData("(id I) FROM ARRAY laFields", 36)]
    [InlineData("(id I) FREE", 36)]
    public void Create_Vfp_Errors_Are_Preserved_Without_Files(string clauses, int error)
    {
        using var dir = new SqlTestSupport.TempDir();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        Assert.Equal(error, Assert.Throws<FoxDbfSqlException>(() => session.Execute("CREATE TABLE t " + clauses)).VfpErrorNumber);
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE DBF t " + clauses;
        Assert.Equal(error, Assert.Throws<FoxDbfSqlException>(() => command.ExecuteNonQuery()).VfpErrorNumber);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("nError=0\nTRY\nCREATE DBF t " + clauses + "\nCATCH TO ex\nnError=ex.ErrorNo\nENDTRY");
        Assert.Equal(error, interpreter.EvalExpression("nError").AsDouble);
        Assert.False(File.Exists(dir.File("t.dbf")));
    }

    [Theory]
    [InlineData("FROM ARRAY laFields", "FROM ARRAY")]
    [InlineData("FREE FROM ARRAY laFields", "FROM ARRAY")]
    [InlineData("(id I UNIQUE)", "UNIQUE")]
    [InlineData("(id I, UNIQUE id TAG uq)", "UNIQUE")]
    [InlineData("(id I AUTOINC)", "AUTOINC")]
    [InlineData("(txt C(10) NOCPTRANS)", "NOCPTRANS")]
    public void Create_Unimplemented_Clauses_Fail_Explicitly_Before_Writing(string clauses, string feature)
    {
        using var dir = new SqlTestSupport.TempDir();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var error = Assert.Throws<NotSupportedException>(() => session.Execute("CREATE TABLE t " + clauses));
        Assert.Contains(feature, error.Message);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }
}
