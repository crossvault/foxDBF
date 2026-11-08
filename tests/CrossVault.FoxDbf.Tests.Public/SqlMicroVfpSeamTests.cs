using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlMicroVfpSeamTests
{
    internal const string Setup = """
        CREATE TABLE customer (id I, city C(10))
        INSERT INTO customer VALUES (1, 'Berlin')
        INSERT INTO customer VALUES (2, 'Paris')
        CREATE TABLE orders (custid I)
        INSERT INTO orders VALUES (1)
        INSERT INTO orders VALUES (3)
        CREATE TABLE emptyorders (custid I)
        CLOSE TABLES ALL
        USE customer
        """;

    public static TheoryData<string, string, string> Queries => new()
    {
        { "", "SELECT * FROM orders WHERE EXISTS (SELECT * FROM orders o WHERE city = 'Berlin')", "E12" },
        { "", "SELECT * FROM orders WHERE .F. AND city.nope", "E13" },
        { "city = 'Berlin'", "SELECT * FROM orders WHERE .F. AND city.nope", "E1924" },
        { "city = .NULL.", "SELECT * FROM orders WHERE .F. AND city.nope", "E1924" },
        { "city = 'Berlin'", "SELECT * FROM orders WHERE EXISTS (SELECT * FROM orders o WHERE city = 'Berlin')", "N2" },
        { "city = .NULL.", "SELECT * FROM orders WHERE EXISTS (SELECT * FROM orders o WHERE city = 'Berlin')", "N0" },
        { "", "SELECT * FROM orders WHERE EXISTS (SELECT * FROM orders o WHERE .F. AND city = 'Berlin')", "N0" },
        { "", "SELECT * FROM orders WHERE city = 'Berlin'", "E12" },
        { "", "SELECT * FROM emptyorders WHERE .F. AND city.nope", "E13" },
        { "city = .NULL.", "SELECT * FROM emptyorders WHERE .F. AND city.nope", "E1924" },
        { "city = .NULL.", "SELECT * FROM orders WHERE ISNULL(m.city)", "N2" },
        { "m = 7\ncity = .NULL.", "SELECT * FROM orders WHERE ISNULL(m.city)", "N2" },
        { "", "SELECT c.id FROM customer c WHERE EXISTS (SELECT * FROM orders o WHERE o.custid = c.id)", "N1" },
        { "", "SELECT * FROM orders WHERE EXISTS (SELECT * FROM orders o WHERE customer.city = 'Berlin')", "N2" },
        { "USE\ncity = .NULL.", "SELECT * FROM orders WHERE .F. AND city.nope", "E1924" },
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void HostedQuery_MatchesVfp9(string before, string sql, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_seams");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute(Setup);
        interpreter.Execute(before);
        Assert.Equal(expected, Query(interpreter, sql));
    }

    internal static string Query(VfpInterpreter interpreter, string sql)
    {
        try
        {
            interpreter.Execute(sql + " INTO ARRAY result");
            return "N" + interpreter.EvalExpression("_TALLY").AsNumber;
        }
        catch (Exception ex) { return "E" + VfpInterpreter.ErrorNumberOf(ex); }
    }

    [Fact]
    public void HavingSubquery_IsRejectedLikeVfp9()
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_having_seam");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using (var interpreter = new VfpInterpreter(session)) interpreter.Execute(Setup);
        const string sql = "SELECT c.id FROM customer c HAVING EXISTS (SELECT * FROM orders o WHERE o.custid = c.id)";
        Assert.Contains("Invalid use of subquery", Assert.Throws<FoxDbfSqlException>(() => session.Execute(sql)).Message);
        using var hosted = new VfpInterpreter(session);
        Assert.Contains("Invalid use of subquery", Assert.Throws<MicroVfpRuntimeException>(() => hosted.Execute(sql + " INTO ARRAY result")).Message);
    }
}
