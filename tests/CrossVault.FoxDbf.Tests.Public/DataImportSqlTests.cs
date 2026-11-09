using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class DataImportSqlTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var c in DataImportTests.Cases())
            if (((string)c[2]).StartsWith("INSERT INTO"))
                foreach (string route in new[] { "session", "ado", "enforced" })
                    yield return new object[] { route }.Concat(c).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertSelect_TransactionMaintainsIndexAndAutoInc(bool commit)
    {
        using var dir = new MicroVfpTestSupport.TempDir("importtransaction");
        using (var writer = CrossVault.FoxDbf.Write.DbfWriter.Create(dir.File("t.dbf"), new[]
        {
            new CrossVault.FoxDbf.Write.DbfColumnDef("id", 'I', autoIncrement: new CrossVault.FoxDbf.Write.DbfAutoIncrement(5, 2)),
            new CrossVault.FoxDbf.Write.DbfColumnDef("n", 'I'),
        })) writer.CreateTag(new CrossVault.FoxDbf.Write.CdxTagDefinition("k", "n"));
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        connection.Interpreter.Execute("CREATE TABLE src (n I)\nINSERT INTO src VALUES (1)\nINSERT INTO src VALUES (2)\nUSE");
        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO t(n) SELECT n FROM src";
            Assert.Equal(2, command.ExecuteNonQuery());
            using (var untouched = DbfTable.Open(dir.File("t.dbf")))
                Assert.Equal(0, untouched.RecordCount);
            if (commit) transaction.Commit(); else transaction.Rollback();
        }
        using var table = DbfTable.Open(dir.File("t.dbf"));
        Assert.Equal(commit ? 2 : 0, table.RecordCount);
        using var index = CrossVault.FoxDbf.Index.CdxFile.Open(dir.File("t.cdx"), table);
        Assert.Equal(commit ? 2 : 0, index.Tag("k")!.EnumerateEntries().Count());
        if (commit)
        {
            Assert.Equal(5, table.GetRecord(0)!.Value[0]);
            Assert.Equal(7, table.GetRecord(1)!.Value[0]);
            Assert.Equal((uint)2, index.Tag("k")!.Seek(2));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void InsertSelect_AllEntryPoints(string route, string name, string setup, string command, string expression, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sqlimport");
        using var connection = new FoxDbfConnection($"Data Source={dir.Path};EnforceRules={(route == "enforced" ? "on" : "off")}");
        connection.Open();
        var session = connection.Session;
        var interpreter = connection.Interpreter;
        setup = DataImportTests.Arrange(name, setup, dir.Path, session);
        interpreter.Execute("SET CENTURY ON\n" + setup + "\nSELECT t");
        string result;
        try
        {
            int affected;
            if (route == "session") affected = session.Execute(command)!.AffectedRecords;
            else
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = command;
                affected = cmd.ExecuteNonQuery();
            }
            result = "OK:" + affected;
        }
        catch (FoxDbfSqlException e) { result = "E" + e.VfpErrorNumber; }
        catch (FoxDbfException e) { result = "E" + e.VfpErrorNumber; }
        catch (MicroVfpRuntimeException e) { result = "E" + e.VfpErrorNumber; }
        interpreter.Execute("result='" + result + "'\nSELECT t\nresult=result+'|'+TRANSFORM(RECCOUNT())+'|'\nSCAN\nresult=result+" + expression + "+';'\nENDSCAN");
        Assert.True(expected == interpreter.EvalExpression("result").AsString,
            route + " " + name + ": expected " + expected + "; actual " + interpreter.EvalExpression("result").AsString);
    }
}
