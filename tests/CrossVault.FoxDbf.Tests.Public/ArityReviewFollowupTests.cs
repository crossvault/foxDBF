using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Data;

namespace CrossVault.FoxDbf.Tests;

public sealed class ArityReviewFollowupTests
{
    [Theory]
    [InlineData("SELECT ALEN(arr) FROM probe")]
    [InlineData("SELECT ALEN(arr) n FROM probe")]
    [InlineData("SELECT ALEN(arr) AS n FROM probe")]
    [InlineData("SELECT id FROM probe ORDER BY ALEN(arr)")]
    [InlineData("SELECT id FROM probe GROUP BY ALEN(arr)")]
    [InlineData("SELECT id FROM probe WHERE ALEN(arr)")]
    [InlineData("SELECT id FROM probe GROUP BY id HAVING ALEN(arr)")]
    [InlineData("SELECT p.id FROM probe p JOIN probe q ON ALEN(arr)")]
    [InlineData("INSERT INTO probe VALUES (ALEN(arr))")]
    [InlineData("UPDATE probe SET id=ALEN(arr)")]
    [InlineData("UPDATE probe SET id=1 WHERE ALEN(arr)")]
    [InlineData("DELETE FROM probe WHERE ALEN(arr)")]
    [InlineData("CREATE TABLE probe CODEPAGE = ALEN(arr) (id I)")]
    [InlineData("SELECT SUM(ALEN(arr)) FROM probe")]
    [InlineData("SELECT id FROM probe WHERE id IN (SELECT ALEN(arr) FROM probe)")]
    [InlineData("INSERT INTO probe SELECT ALEN(arr) FROM probe")]
    public void SqlExpressionSites_UseNormalizationEntryPoint(string sql)
    {
        var fragments = new List<string>();
        SqlParser.Parse(sql, text =>
        {
            fragments.Add(text);
            return text.Replace("ALEN(arr)", "3", StringComparison.Ordinal);
        });
        Assert.Contains(fragments, text => text is "ALEN(arr)" or "SUM(ALEN(arr))");
    }

    public static IEnumerable<object[]> ProjectionCases()
    {
        yield return ["SELECT ALEN(arr) FROM probe", "3"];
        yield return ["SELECT ALEN(arr) n FROM probe", "3"];
        yield return ["SELECT ALEN(arr) AS n FROM probe", "3"];
        yield return ["SELECT arr[1] FROM probe", "7"];
        yield return ["SELECT arr[1] n FROM probe", "7"];
        yield return ["SELECT arr[1] AS n FROM probe", "7"];
        yield return ["SELECT SUM(ALEN(arr)) FROM probe", "3"];
        yield return ["SELECT id FROM probe WHERE id IN (SELECT ALEN(arr) FROM probe)", "3"];
        yield return ["SELECT ALEN(arr) n FROM probe ORDER BY ALEN(arr)", "err:1806"];
        yield return ["SELECT arr[1] n FROM probe ORDER BY arr[1]", "err:1806"];
        yield return ["SELECT ALEN(arr) n FROM probe GROUP BY ALEN(arr)", "err:1806"];
        yield return ["SELECT arr[1] n FROM probe GROUP BY arr[1]", "err:1806"];
    }

    internal const string ProjectionSetup = "CREATE TABLE probe FREE (id I)\nINSERT INTO probe VALUES (3)\nDIMENSION arr[3]\narr[1]=7\n";
    internal static string ProjectionCode(string sql) => sql + " INTO ARRAY result\nv=TRANSFORM(result[1])";
    internal static string Catch(string code) => "v='ok'\nTRY\n" + code + "\nCATCH TO failure\nv='err:'+TRANSFORM(failure.ErrorNo)\nENDTRY\n";

    public static IEnumerable<object[]> CollisionCases()
    {
        foreach (string kind in new[] { "CURSOR", "TABLE" })
        foreach (string name in new[] { "alen", "source", "other", "spare", "fresh" })
            yield return [kind, name, "", kind == "CURSOR" && name is "source" or "other" or "spare" ? "err:24" : kind == "TABLE" && name == "other" ? "err:102" : "ok"];
        yield return ["CURSOR", "alen", "USE IN source\n", "ok"];
        yield return ["CURSOR", "q", "SELECT id FROM spare INTO CURSOR q\n", "ok"];
    }
    internal const string CollisionSetup = "CREATE TABLE alen FREE (id I)\nINSERT INTO alen VALUES (1)\nUSE\nCREATE TABLE other FREE (id I)\nINSERT INTO other VALUES (2)\nUSE\nUSE alen IN 0 ALIAS source\nUSE other IN 0 ALIAS spare\n";
    internal static string CollisionCode(string kind, string name, string before) => before + $"SELECT id FROM spare INTO {kind} {name}";

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void ProjectionForms_MatchVfp9(string sql, string expected) => Run(ProjectionSetup, ProjectionCode(sql), expected);

    [Theory]
    [MemberData(nameof(CollisionCases))]
    public void IntoNames_MatchVfp9(string kind, string name, string before, string expected)
        => Run(CollisionSetup, CollisionCode(kind, name, before), expected);

    [Fact]
    public void IntoCursor_SourceFileStem_Raises24()
        => Run(CollisionSetup, "SELECT id FROM source INTO CURSOR alen", "err:24");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntoCursor_SourceFileStem_SqlAndAdoRaise24(bool ado)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_into_session");
        string[] setup = ["CREATE TABLE alen FREE (id I)", "INSERT INTO alen VALUES (1)", "USE alen ALIAS source"];
        const string sql = "SELECT id FROM source INTO CURSOR alen";
        if (ado)
        {
            using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            foreach (string statement in setup)
            {
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
            command.CommandText = sql;
            var error = Assert.IsAssignableFrom<IVfpErrorCode>(Record.Exception(() => command.ExecuteNonQuery()));
            Assert.Equal(24, error.VfpErrorNumber);
            command.CommandText = "SELECT id FROM source";
            Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
        }
        else
        {
            using var session = new VfpSession();
            session.OpenDirectory(dir.Path);
            foreach (string statement in setup) session.Execute(statement);
            Assert.Equal(24, Assert.Throws<FoxDbfSqlException>(() => session.Execute(sql)).VfpErrorNumber);
            Assert.Equal(1, Convert.ToInt32(session.Execute("SELECT id FROM source")!.Rows.Single()[0]));
        }
    }

    internal static void Run(string setup, string code, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("arity_followup");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute(setup + Catch(code));
        Assert.Equal(expected, interpreter.Memory.Get("v").AsString);
    }
}
