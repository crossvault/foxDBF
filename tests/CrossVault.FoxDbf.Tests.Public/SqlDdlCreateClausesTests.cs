using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlDdlCreateClausesTests
{
    [Fact]
    public void Create_Candidate_Rejects_Batch_Duplicates_Without_Changing_Files()
    {
        using var dir = new SqlTestSupport.TempDir();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE t (id I UNIQUE)");
        using (var writer = DbfWriter.Open(dir.File("t.dbf"))) writer.AppendRecord(1);
        var before = File.ReadAllBytes(dir.File("t.dbf"));
        using (var writer = DbfWriter.Open(dir.File("t.dbf")))
        {
            Assert.Equal(1884, Assert.Throws<DbfWriteException>(() => writer.AppendRecords(new object?[][] { [2], [2] })).VfpErrorNumber);
            Assert.False(writer.ReindexNeeded);
        }
        Assert.Equal(before, File.ReadAllBytes(dir.File("t.dbf")));
        using (var writer = DbfWriter.Open(dir.File("t.dbf")))
        {
            writer.AppendRecord(3);
            Assert.Equal(2, writer.RecordCount);
        }
    }

    [Fact]
    public void Create_Database_Membership_Rollback_Restores_All_Container_Files()
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), Array.Empty<DbcTableSpec>());
        string[] files = ["catalog.dbc", "catalog.dct", "catalog.dcx"];
        var before = files.Select(f => File.ReadAllBytes(dir.File(f))).ToArray();
        using var connection = new FoxDbfConnection($"Data Source={dir.File("catalog.dbc")}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE t (id I PRIMARY KEY, note M)";
        command.ExecuteNonQuery();
        transaction.Rollback();
        for (int i = 0; i < files.Length; i++) Assert.Equal(before[i], File.ReadAllBytes(dir.File(files[i])));
        Assert.False(File.Exists(dir.File("t.dbf")));
        Assert.False(File.Exists(dir.File("t.fpt")));
        Assert.False(File.Exists(dir.File("t.cdx")));
        command.Transaction = null;
        command.CommandText = "CREATE TABLE t (id I PRIMARY KEY)";
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Create_Database_Preserves_Long_Names_Defaults_Rules_And_Keys()
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDatabase(dir.File("catalog.dbc"));
        session.Execute("CREATE TABLE t NAME long_table_name (long_field_name I, long_field_next M NULL, qty I DEFAULT 7 CHECK qty > 0 ERROR 'positive', PRIMARY KEY long_field_name TAG pk, CHECK long_field_name > 0 ERROR 'id required')");
        var rules = session.Database!.GetTableRules("long_table_name");
        Assert.Equal("pk", rules.PrimaryKey);
        Assert.Equal("long_field_name>0", rules.RuleExpression);
        Assert.Equal("\"id required\"", rules.RuleText);
        Assert.Equal("7", rules.Fields[2].DefaultValue);
        Assert.Equal("qty>0", rules.Fields[2].RuleExpression);
        Assert.Equal("\"positive\"", rules.Fields[2].RuleText);
        session.Use("long_table_name");
        Assert.Equal("long_field_name", session.OpenAreas.Single().Table.Columns[0].Name);
        Assert.Equal("long_field_next", session.OpenAreas.Single().Table.Columns[1].Name);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(18)]
    public void Create_From_MicroVfp_Array_Writes_Structure(int width)
    {
        using var dir = new SqlTestSupport.TempDir();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        string source = $"DIMENSION laFields[1,{width}]\nlaFields[1,1]='id'\nlaFields[1,2]='I'\nlaFields[1,3]=4\nlaFields[1,4]=0\n";
        if (width == 18)
        {
            for (int col = 7; col <= 16; col++) source += $"laFields[1,{col}]=''\n";
            source += "laFields[1,17]=12\nlaFields[1,18]=3\n";
        }
        interpreter.Execute(source + "CREATE TABLE t FREE FROM ARRAY laFields\nUSE");
        using var table = DbfTable.Open(dir.File("t.dbf"));
        Assert.Single(table.Columns);
        Assert.Equal('I', table.Columns[0].Type);
        if (width == 18) Assert.Equal(0x31, File.ReadAllBytes(dir.File("t.dbf"))[0]);
    }

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
    public void Create_OpenDatabase_Registers_Membership(string clauses)
    {
        using var dir = new SqlTestSupport.TempDir();
        DbfDatabaseBuilder.Create(dir.File("catalog.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDatabase(dir.File("catalog.dbc"));
        session.Execute($"CREATE TABLE t {clauses} (id I)");
        Assert.True(File.Exists(dir.File("t.dbf")));
        Assert.Contains(clauses.Length == 0 ? "t" : "longname", session.Database!.TableNames);
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
    [InlineData("(id I UNIQUE)")]
    [InlineData("(id I, UNIQUE id TAG uq)")]
    [InlineData("(id I AUTOINC)")]
    [InlineData("(txt C(10) NOCPTRANS)")]
    public void Create_Free_Modifiers_Are_Written(string clauses)
    {
        using var dir = new SqlTestSupport.TempDir();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE t " + clauses);
        using var table = DbfTable.Open(dir.File("t.dbf"));
        Assert.Single(table.Columns);
        if (clauses.Contains("UNIQUE")) Assert.True(File.Exists(dir.File("t.cdx")));
        if (clauses.Contains("AUTOINC")) Assert.Equal(0x31, File.ReadAllBytes(dir.File("t.dbf"))[0]);
        if (clauses.Contains("NOCPTRANS")) Assert.True(table.Columns[0].IsBinary);
    }
}
