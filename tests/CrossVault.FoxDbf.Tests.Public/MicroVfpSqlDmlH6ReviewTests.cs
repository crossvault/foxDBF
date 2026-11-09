namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpSqlDmlH6ReviewTests
{
    internal const string Procedure = "FUNCTION __RI_DELETE_t\nseen=ALIAS()\nRETURN .T.\nENDFUNC";
    internal const string Setup = "PUBLIC seen\nseen=''\nCREATE TABLE t (n I)\nINSERT INTO t VALUES (1)\nCREATE TRIGGER ON t FOR DELETE AS __RI_DELETE_t()";
    internal const string Open = "CLOSE TABLES ALL\nUSE t AGAIN ALIAS x IN 0\nSELECT x";
    [Fact]
    public void H6_DeleteUsesExistingAlias() => Assert.Equal("X", DeleteThroughProvider());
    internal static string DeleteThroughProvider()
    {
        using var dir = new MicroVfpTestSupport.TempDir("h6");
        using var connection = MicroVfpSqlDmlFinalReviewTests.OpenAdo(dir, Procedure + "\n" + Setup[..Setup.IndexOf("CREATE TRIGGER", StringComparison.Ordinal)]);
        connection.Interpreter.Execute(Open);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM t";
        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Equal("X", connection.Interpreter.Memory.Get("seen").AsString);
        Assert.Single(connection.Session.OpenAreas);
        return connection.Interpreter.Memory.Get("seen").AsString;
    }
}
