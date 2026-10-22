using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// VFP9 result-column names, GROUP BY ordinals/aliases and ORDER BY on a column alias. Every expected
/// value was observed in VFP9 (SET ENGINEBEHAVIOR 90) over the same rows: person(id I, name C(10),
/// amount I) with ids 1..6, name 'N' + (7 - id), amount id * 10; cust/opened are a two-row join pair.
/// <list type="bullet">
/// <item>Names are upper case; an unaliased computed column is <c>EXP_n</c> with n its POSITION;
/// aggregates are <c>CNT</c>, <c>CNT_FIELD</c>, <c>SUM_FIELD</c>, <c>SUM_EXP_n</c>, …; a name occurring
/// more than once (user aliases included) becomes <c>NAME_A</c>, <c>NAME_B</c>, ….</item>
/// <item><c>GROUP BY 1</c> / <c>GROUP BY k</c> group by the select item's expression; ordinals out of
/// range are 1807, an ordinal naming an aggregate 1846.</item>
/// <item><c>ORDER BY k</c> on a select alias sorts by that column, but a source field of the same
/// name wins over the alias.</item>
/// </list>
/// </summary>
public sealed class SqlVfpNamingGroupOrderTests : IDisposable
{
    private readonly string _dir;
    private readonly VfpSession _s;

    public SqlVfpNamingGroupOrderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "foxdbf_naming_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _s = new VfpSession();
        _s.OpenDirectory(_dir);
        _s.Execute("CREATE TABLE person (id I, name C(10), amount I)");
        for (int i = 1; i <= 6; i++)
            _s.Execute(string.Format(CultureInfo.InvariantCulture,
                "INSERT INTO person VALUES ({0}, 'N{1}', {2})", i, 7 - i, i * 10));
        _s.Execute("CREATE TABLE cust (cname C(20), acctno I, region C(5))");
        _s.Execute("INSERT INTO cust VALUES ('Alice', 10, 'N')");
        _s.Execute("INSERT INTO cust VALUES ('Bob', 20, 'S')");
        _s.Execute("CREATE TABLE opened (acctno I, balance I, region C(5))");
        _s.Execute("INSERT INTO opened VALUES (10, 100, 'X')");
        _s.Execute("INSERT INTO opened VALUES (20, 200, 'Y')");
    }

    public void Dispose()
    {
        _s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Join = " FROM cust ca JOIN opened ao ON ca.acctno = ao.acctno";

    [Theory]
    [InlineData("SELECT ca.cname, ca.acctno, ca.acctno * 2 FROM cust ca", "CNAME,ACCTNO,EXP_3")]
    [InlineData("SELECT ca.acctno, ao.acctno" + Join, "ACCTNO_A,ACCTNO_B")]
    [InlineData("SELECT ca.acctno, ao.acctno, ca.acctno" + Join, "ACCTNO_A,ACCTNO_B,ACCTNO_C")]
    [InlineData("SELECT *" + Join, "CNAME,ACCTNO_A,REGION_A,ACCTNO_B,BALANCE,REGION_B")]
    [InlineData("SELECT ca.cname, ca.acctno * 2, ao.acctno, ca.acctno" + Join, "CNAME,EXP_2,ACCTNO_A,ACCTNO_B")]
    [InlineData("SELECT ca.acctno AS acctno, ao.acctno" + Join, "ACCTNO_A,ACCTNO_B")]
    [InlineData("SELECT ca.region, SUM(ao.balance)" + Join + " GROUP BY 1", "REGION,SUM_BALANCE")]
    [InlineData("SELECT id, COUNT(*), SUM(amount), MAX(amount), id * 2, AVG(amount), MIN(name), COUNT(id) FROM person GROUP BY id",
        "ID,CNT,SUM_AMOUNT,MAX_AMOUNT,EXP_5,AVG_AMOUNT,MIN_NAME,CNT_ID")]
    [InlineData("SELECT id, SUM(amount * 2), AVG(amount + 1), MIN(id * 2) FROM person GROUP BY id",
        "ID,SUM_EXP_2,AVG_EXP_3,MIN_EXP_4")]
    [InlineData("SELECT SUM(p.amount), MAX(p.name) FROM person p", "SUM_AMOUNT,MAX_NAME")]
    [InlineData("SELECT SUM(amount), SUM(amount) FROM person", "SUM_AMOUNT_A,SUM_AMOUNT_B")]
    [InlineData("SELECT COUNT(1) FROM person", "CNT_EXP_1")]
    [InlineData("SELECT MAX(amount) + 1 FROM person", "EXP_1")]
    [InlineData("SELECT MAX(amount) - MIN(amount), SUM(amount) + COUNT(amount) FROM person", "EXP_1,EXP_2")]
    [InlineData("SELECT id AS k, id * 2 AS mixedCase FROM person", "K,MIXEDCASE")]
    [InlineData("SELECT id, id FROM person", "ID_A,ID_B")]
    [InlineData("SELECT id, name AS id FROM person", "ID_A,ID_B")]
    [InlineData("SELECT 'a' AS name, name FROM person", "NAME_A,NAME_B")]
    [InlineData("SELECT id * 2, id * 3 FROM person", "EXP_1,EXP_2")]
    [InlineData("SELECT id * 2 AS exp_2, id * 3 FROM person", "EXP_2_A,EXP_2_B")]
    [InlineData("SELECT 1, 'x', id FROM person", "EXP_1,EXP_2,ID")]
    [InlineData("SELECT *, id * 2 FROM person", "ID,NAME,AMOUNT,EXP_4")]
    [InlineData("SELECT p.*, q.id FROM person p, person q WHERE p.id = q.id", "ID_A,NAME,AMOUNT,ID_B")]
    [InlineData("SELECT p.name, q.name, r.name FROM person p, person q, person r WHERE p.id = q.id AND q.id = r.id",
        "NAME_A,NAME_B,NAME_C")]
    [InlineData("SELECT COUNT(*), COUNT(*) FROM person", "CNT_A,CNT_B")]
    [InlineData("SELECT id * 2, name FROM person UNION SELECT id, name FROM person", "EXP_1,NAME")]
    public void ResultColumnNames_MatchVfp9(string sql, string expected)
        => Assert.Equal(expected, string.Join(",", _s.Execute(sql)!.Columns.Select(c => c.Name)));

    [Theory]
    [InlineData("SELECT p.id % 2 AS k, COUNT(*) FROM person p GROUP BY 1 ORDER BY 1", "0:3 1:3")]
    [InlineData("SELECT id % 2 AS k, COUNT(*) FROM person GROUP BY k ORDER BY k", "0:3 1:3")]
    [InlineData("SELECT name, COUNT(*) FROM person GROUP BY 1 ORDER BY 1", "N1:1 N2:1 N3:1 N4:1 N5:1 N6:1")]
    [InlineData("SELECT COUNT(*), id % 3 AS k FROM person GROUP BY 2 ORDER BY 2 DESC", "2:2 2:1 2:0")]
    [InlineData("SELECT id % 2, SUM(amount) FROM person GROUP BY 1 ORDER BY 1", "0:120 1:90")]
    [InlineData("SELECT id % 2 AS k, COUNT(*) FROM person GROUP BY 1, k ORDER BY 1", "0:3 1:3")]
    [InlineData("SELECT id % 2 AS k, MAX(id) AS m FROM person GROUP BY 1 ORDER BY m DESC", "0:6 1:5")]
    [InlineData("SELECT ao.balance - ca.acctno AS k, COUNT(*)" + Join + " GROUP BY 1 ORDER BY 1 DESC", "180:1 90:1")]
    [InlineData("SELECT id AS k FROM person ORDER BY k DESC", "6 5 4 3 2 1")]
    [InlineData("SELECT id AS k FROM person ORDER BY K DESC", "6 5 4 3 2 1")]
    [InlineData("SELECT id AS k FROM person ORDER BY k", "1 2 3 4 5 6")]
    [InlineData("SELECT id * 10 AS k FROM person ORDER BY k DESC", "60 50 40 30 20 10")]
    [InlineData("SELECT p.id AS k FROM person p ORDER BY k DESC", "6 5 4 3 2 1")]
    [InlineData("SELECT id AS k FROM person WHERE id > 2 ORDER BY k DESC", "6 5 4 3")]
    [InlineData("SELECT TOP 2 id AS k FROM person ORDER BY k DESC", "6 5")]
    [InlineData("SELECT DISTINCT id % 3 AS k FROM person ORDER BY k DESC", "2 1 0")]
    [InlineData("SELECT UPPER(name) AS k FROM person ORDER BY k DESC", "N6 N5 N4 N3 N2 N1")]
    [InlineData("SELECT id % 3 AS k, id FROM person ORDER BY k DESC, id", "2:2 2:5 1:1 1:4 0:3 0:6")]
    [InlineData("SELECT ca.acctno AS k" + Join + " ORDER BY k DESC", "20 10")]
    // A source field of the same name wins over the select alias (VFP9: sorted by the field id).
    [InlineData("SELECT name AS id, id AS name FROM person ORDER BY id DESC", "N1:6 N2:5 N3:4 N4:3 N5:2 N6:1")]
    [InlineData("SELECT name AS id, id AS name FROM person ORDER BY 1 DESC", "N6:1 N5:2 N4:3 N3:4 N2:5 N1:6")]
    [InlineData("SELECT id FROM person ORDER BY amount DESC", "6 5 4 3 2 1")]
    public void GroupByOrdinalAlias_And_OrderByAlias_MatchVfp9(string sql, string expected)
        => Assert.Equal(expected, Format(_s.Execute(sql)!));

    [Theory]
    [InlineData("SELECT id % 2 AS k, COUNT(*) FROM person GROUP BY 3", 1807)]
    [InlineData("SELECT id AS k FROM person GROUP BY 0", 1807)]
    [InlineData("SELECT id % 2 AS k, COUNT(*) FROM person GROUP BY 2", 1846)]
    [InlineData("SELECT id AS k FROM person ORDER BY 2", 1808)]
    public void InvalidOrdinals_RaiseVfp9Errors(string sql, int error)
    {
        var ex = Assert.Throws<FoxDbfSqlException>(() => _s.Execute(sql));
        Assert.Equal(error, ex.VfpErrorNumber);
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
