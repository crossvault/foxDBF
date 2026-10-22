using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The row order VFP9 gives GROUP BY / DISTINCT / UNION results, and HAVING on select aliases. Every
/// expected value was observed in VFP9 (SET ENGINEBEHAVIOR 90) over the same rows: person(id I,
/// name C(10), amount I) with ids 1..6, name 'N' + (7 - id), amount id * 10; tags(w C(6), n I NULL);
/// cust / opened a small join pair.
/// <list type="bullet">
/// <item>GROUP BY returns the groups sorted by the key tuple in GROUP BY list order (.NULL. first,
/// .F. before .T.), even without ORDER BY and when the key is not selected.</item>
/// <item>DISTINCT and each non-ALL UNION step sort by the full row in column order; UNION ALL appends.</item>
/// <item>ORDER BY re-sorts that order stably: rows tied on the ORDER BY key keep it.</item>
/// <item>HAVING sees select aliases (aggregate aliases too) and compound AND/OR; without GROUP BY it
/// filters the projected rows.</item>
/// </list>
/// </summary>
public sealed class SqlSortedResultsHavingAliasTests : IDisposable
{
    private readonly string _dir;
    private readonly VfpSession _s;

    public SqlSortedResultsHavingAliasTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "foxdbf_sorted_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _s = new VfpSession();
        _s.OpenDirectory(_dir);
        _s.Execute("CREATE TABLE person (id I, name C(10), amount I)");
        for (int i = 1; i <= 6; i++)
            _s.Execute(string.Format(CultureInfo.InvariantCulture,
                "INSERT INTO person VALUES ({0}, 'N{1}', {2})", i, 7 - i, i * 10));
        _s.Execute("CREATE TABLE tags (w C(6), n I NULL)");
        foreach (var (w, n) in new[] { ("B", "1"), ("_x", "2"), ("1", "3"), ("a", "4"), ("Zz", "5"), ("b", "6"), ("c", ".NULL.") })
            _s.Execute($"INSERT INTO tags VALUES ('{w}', {n})");
        _s.Execute("CREATE TABLE cust (cname C(20), acctno I, region C(5))");
        _s.Execute("INSERT INTO cust VALUES ('Bob', 20, 'S')");
        _s.Execute("INSERT INTO cust VALUES ('Alice', 10, 'N')");
        _s.Execute("INSERT INTO cust VALUES ('Carl', 30, 'N')");
        _s.Execute("CREATE TABLE opened (acctno I, balance I, region C(5))");
        _s.Execute("INSERT INTO opened VALUES (30, 50, 'Y')");
        _s.Execute("INSERT INTO opened VALUES (10, 100, 'X')");
        _s.Execute("INSERT INTO opened VALUES (20, 200, 'Y')");
        _s.Execute("INSERT INTO opened VALUES (10, 300, 'X')");
    }

    public void Dispose()
    {
        _s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Join = " FROM cust ca JOIN opened ao ON ca.acctno = ao.acctno";

    [Theory]
    [InlineData("SELECT id % 2 AS k, COUNT(*) FROM person GROUP BY 1", "0:3 1:3")]
    [InlineData("SELECT id % 3 AS b, id % 2 AS a, COUNT(*) FROM person GROUP BY 1, 2", "0:0:1 0:1:1 1:0:1 1:1:1 2:0:1 2:1:1")]
    [InlineData("SELECT id % 3 AS b, id % 2 AS a, COUNT(*) FROM person GROUP BY 2, 1", "0:0:1 1:0:1 2:0:1 0:1:1 1:1:1 2:1:1")]
    [InlineData("SELECT MAX(name) FROM person GROUP BY amount", "N6 N5 N4 N3 N2 N1")]
    [InlineData("SELECT -id AS k, COUNT(*) FROM person GROUP BY 1", "-6:1 -5:1 -4:1 -3:1 -2:1 -1:1")]
    [InlineData("SELECT n, COUNT(*) FROM tags GROUP BY n", ".NULL.:1 1:1 2:1 3:1 4:1 5:1 6:1")]
    [InlineData("SELECT amount > 30 AS b, COUNT(*) FROM person GROUP BY 1", "False:3 True:3")]
    [InlineData("SELECT w, COUNT(*) FROM tags GROUP BY w", "1:1 B:1 Zz:1 _x:1 a:1 b:1 c:1")]
    [InlineData("SELECT ca.region, ao.region, COUNT(*)" + Join + " GROUP BY 2, 1", "N:X:2 N:Y:1 S:Y:1")]
    [InlineData("SELECT ao.acctno, SUM(ao.balance) FROM opened ao GROUP BY 1", "10:400 20:200 30:50")]
    [InlineData("SELECT id % 3 AS b, id % 2 AS a, COUNT(*) FROM person GROUP BY 1, 2 ORDER BY a DESC", "0:1:1 1:1:1 2:1:1 0:0:1 1:0:1 2:0:1")]
    [InlineData("SELECT DISTINCT id % 3 AS k FROM person", "0 1 2")]
    [InlineData("SELECT DISTINCT id % 3 AS b, id % 2 AS a FROM person", "0:0 0:1 1:0 1:1 2:0 2:1")]
    [InlineData("SELECT DISTINCT n FROM tags", ".NULL. 1 2 3 4 5 6")]
    [InlineData("SELECT DISTINCT name FROM person WHERE id > 2", "N1 N2 N3 N4")]
    [InlineData("SELECT DISTINCT id % 3 AS k, id % 2 AS a FROM person ORDER BY a", "0:0 1:0 2:0 0:1 1:1 2:1")]
    [InlineData("SELECT DISTINCT p.id % 3 AS k FROM person p JOIN person q ON p.id = q.id", "0 1 2")]
    [InlineData("SELECT id % 3 AS k FROM person", "1 2 0 1 2 0")]
    [InlineData("SELECT name, id FROM person UNION SELECT name, id * 2 FROM person",
        "N1:6 N1:12 N2:5 N2:10 N3:4 N3:8 N4:3 N4:6 N5:2 N5:4 N6:1 N6:2")]
    [InlineData("SELECT id * 2, name FROM person UNION ALL SELECT id, name FROM person",
        "2:N6 4:N5 6:N4 8:N3 10:N2 12:N1 1:N6 2:N5 3:N4 4:N3 5:N2 6:N1")]
    [InlineData("SELECT 7 - id FROM person UNION SELECT 7 - id FROM person UNION ALL SELECT 0 FROM person WHERE id = 1", "1 2 3 4 5 6 0")]
    [InlineData("SELECT 7 - id AS a FROM person UNION ALL SELECT 7 - id FROM person UNION SELECT 0 FROM person WHERE id = 1", "0 1 2 3 4 5 6")]
    [InlineData("SELECT 7 - id AS a, 1 AS b FROM person UNION SELECT id, 0 FROM person ORDER BY 2",
        "1:0 2:0 3:0 4:0 5:0 6:0 1:1 2:1 3:1 4:1 5:1 6:1")]
    [InlineData("SELECT id % 2 AS k, COUNT(*) FROM person GROUP BY 1 UNION ALL SELECT id + 5, 0 FROM person WHERE id < 3", "0:3 1:3 6:0 7:0")]
    public void ResultRows_ComeBackInVfp9Order(string sql, string expected)
        => Assert.Equal(expected, Format(_s.Execute(sql)!));

    [Theory]
    [InlineData("SELECT id AS k, COUNT(*) FROM person GROUP BY k HAVING k > 3", "4:1 5:1 6:1")]
    [InlineData("SELECT id % 3 AS k, COUNT(*) AS n FROM person GROUP BY k HAVING n > 1", "0:2 1:2 2:2")]
    [InlineData("SELECT id % 3 AS k, SUM(amount) AS s FROM person GROUP BY k HAVING s > 70", "0:90")]
    [InlineData("SELECT id % 3 AS k, COUNT(*) AS n FROM person GROUP BY k HAVING n + k > 2", "1:2 2:2")]
    [InlineData("SELECT COUNT(*) AS n FROM person HAVING n > 1", "6")]
    [InlineData("SELECT COUNT(*) AS n FROM person HAVING n > 10", "")]
    [InlineData("SELECT id % 3 AS k, COUNT(*) AS n FROM person GROUP BY 1 HAVING k = 1 OR n > 5", "1:2")]
    [InlineData("SELECT id % 3 AS k, COUNT(*) FROM person GROUP BY 1 HAVING COUNT(*) > 1 AND k > 0", "1:2 2:2")]
    [InlineData("SELECT id % 3 AS k, COUNT(*) AS n FROM person GROUP BY 1 HAVING K = 1", "1:2")]
    [InlineData("SELECT ca.region AS r, SUM(ao.balance) AS s" + Join + " GROUP BY 1 HAVING s > 100", "N:450 S:200")]
    [InlineData("SELECT name AS nm, COUNT(*) AS n FROM person GROUP BY name HAVING nm > 'N3'", "N4:1 N5:1 N6:1")]
    [InlineData("SELECT id % 3 AS k, COUNT(*) AS n FROM person GROUP BY 1 HAVING BETWEEN(k, 1, 2)", "1:2 2:2")]
    [InlineData("SELECT COUNT(*) AS n FROM person WHERE id > 99 HAVING n = 0", "0")]
    [InlineData("SELECT MAX(id) AS m FROM person WHERE id > 99 HAVING ISNULL(m)", ".NULL.")]
    [InlineData("SELECT id AS k FROM person HAVING k > 4", "5 6")]
    [InlineData("SELECT id % 3 AS k, id FROM person HAVING k > 0 ORDER BY id DESC", "2:5 1:4 2:2 1:1")]
    [InlineData("SELECT id AS k FROM person WHERE id > 1 HAVING k < 4", "2 3")]
    public void Having_SeesSelectAliases_AsVfp9(string sql, string expected)
        => Assert.Equal(expected, Format(_s.Execute(sql)!));

    [Theory]
    [InlineData("SELECT COUNT(*), MAX(n) FROM tags GROUP BY w", "1:2 1:3 1:4 2:6 1:.NULL. 1:5")]
    [InlineData("SELECT DISTINCT w FROM tags", "_x 1 a B c Zz")]
    [InlineData("SELECT w FROM tags UNION SELECT w FROM tags", "_x 1 a B c Zz")]
    [InlineData("SELECT w, COUNT(*) FROM tags WHERE n < 6 GROUP BY 1", "_x:1 1:1 a:1 B:1 Zz:1")]
    public void UnderGeneral_RowsSortByTheCollation(string sql, string expected)
    {
        using var general = new VfpSession(new EvaluationContext { Collation = VfpCollations.General });
        general.OpenDirectory(_dir);
        Assert.Equal(expected, Format(general.Execute(sql)!));
    }

    private static string Format(SqlResult r)
        => string.Join(" ", r.Rows.Select(row => string.Join(":", row.Select(Cell))));

    private static string Cell(object? v) => v switch
    {
        null => ".NULL.",
        string str => str.TrimEnd(),
        int or long or short or decimal or double or float => Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
