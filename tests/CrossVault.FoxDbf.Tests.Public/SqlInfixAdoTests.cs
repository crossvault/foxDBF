using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlInfixAdoTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (object[] c in SqlInfixPredicateTests.CasesFor(indexed: true))
        {
            if (((string)c[0]).Contains("m.", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (bool highlike in new[] { false, true })
                yield return [c[0], c[1], c[2], c[3], highlike];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IndexedCommandText_MatchesVfp9(string sql, bool ansi, bool exact, string expected, bool highlike)
    {
        using var temp = new SqlTestSupport.TempDir();
        SqlInfixPredicateTests.CreateFixture(temp.Path);
        using (var writer = DbfWriter.Open(temp.File("t.dbf")))
        {
            writer.CreateTag(new CdxTagDefinition("cid", "cid"));
            writer.CreateTag(new CdxTagDefinition("cname", "cname"));
        }
        using var connection = new FoxDbfConnection($"Data Source={temp.Path};Ansi={(ansi ? "on" : "off")};Deleted=on" +
            (highlike ? ";Accelerator=Highlike" : ""));
        connection.Open();
        connection.Session.Context.Exact = exact;
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        string actual;
        try
        {
            if (!sql.StartsWith("SELECT")) actual = "tally:" + command.ExecuteNonQuery();
            else
            {
                using var reader = command.ExecuteReader();
                var ids = new List<int>();
                while (reader.Read()) ids.Add(Convert.ToInt32(reader.GetValue(0)));
                actual = string.Concat(ids.Select(i => i + ","));
            }
        }
        catch (ExpressionException ex) { actual = "error:" + ex.VfpErrorNumber; }
        catch (Exception ex) when (ex is IVfpErrorCode { VfpErrorNumber: not null } error)
        { actual = "error:" + error.VfpErrorNumber; }
        Assert.Equal(expected, actual);
    }
}
