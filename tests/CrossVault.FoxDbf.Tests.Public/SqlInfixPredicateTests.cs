using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlInfixPredicateTests
{
    internal static readonly string[] Predicates =
    [
        "cid IN (1,2)", "cid NOT IN (1,2)", "NOT cid IN (1,2)", "NOT (cid IN (1,2))",
        "cid IN (2)", "cid IN ()", "cid IN (1,.NULL.)", "cid NOT IN (1,.NULL.)",
        "NOT (cid NOT IN (1,.NULL.))", "cid IN ('1',2)", "cid IN (1,'x')",
        "cid IN (1+1,m.val)", "cname IN ('B')", "cname IN ('Bob')", "cname IN ('Bob       ')",
        "d IN ({^2024-01-01})", "flag IN (.T.)", "cid BETWEEN 1 AND 3", "cid BETWEEN 3 AND 1",
        "cid NOT BETWEEN 1 AND 3", "NOT cid BETWEEN 1 AND 3", "NOT (cid BETWEEN 1 AND 3)",
        "cid BETWEEN .NULL. AND 3", "cid BETWEEN 1 AND .NULL.", "cid NOT BETWEEN .NULL. AND 3",
        "cid BETWEEN 1 AND 2 AND rid=2", "cid BETWEEN 1 AND 2 OR rid=7",
        "cname BETWEEN 'A' AND 'B'", "cname BETWEEN 'Bob' AND 'Bob'", "cid BETWEEN '1' AND '3'",
        "cname LIKE 'A%'", "cname LIKE 'A_X%'", "cname LIKE 'Bob'", "cname LIKE 'Bob%'",
        "cname LIKE 'bob%'", "cname LIKE 'B_b%'", "cname NOT LIKE 'A%'", "NOT cname LIKE 'A%'",
        "NOT (cname LIKE 'A%')", "cname LIKE .NULL.", "cname LIKE m.pat", "cname LIKE 'A'+'%'",
        "cname LIKE 'A!%X%' ESCAPE '!'", "cname LIKE 'A!_X%' ESCAPE '!'",
        "cname LIKE 'A%' ESCAPE ''", "cname LIKE 'A%' ESCAPE '!!'", "cname LIKE 'A%' ESCAPE m.esc",
        "cid LIKE '1%'", "cname LIKE 1", "cname LIKE 'Bob       '", "cname LIKE ''",
        "cname LIKE '%'", "cname LIKE 'A!X%' ESCAPE '!'", "cname LIKE 'A!' ESCAPE '!'",
        "cname LIKE 'A!%X%' ESCAPE '!!'", "cname LIKE 'A!%X%' ESCAPE ''",
        "cname LIKE 'A%' ESCAPE .NULL.", "cname LIKE 'A%' ESCAPE 1",
        "'A!X' LIKE 'A!X' ESCAPE '!'", "'A!' LIKE 'A!' ESCAPE '!'",
        "'A!X' LIKE 'A!!X' ESCAPE '!'", "cname LIKE 'Bob_'", "cname LIKE 'Bob_______'",
        "cname LIKE 'B_________'", "'Bob' LIKE 'Bob '", "'Bob ' LIKE 'Bob'",
        "cname LIKE cname", "cname LIKE RTRIM(cname)", ".NULL. LIKE 'A%'", ".NULL. LIKE .NULL.",
        "cid NOT BETWEEN 3 AND .NULL.", "cid IN (SELECT cname FROM t)",
        "cid IN (1,2) AND cid IN (SELECT cid FROM t WHERE cid BETWEEN 2 AND 3)",
        "NOT (cid BETWEEN 1 AND 2) AND cid IN (SELECT cid FROM t)",
    ];

    internal static IEnumerable<(string Sql, bool Ansi, bool Exact)> Inputs()
    {
        foreach (string pred in Predicates)
            yield return ($"SELECT rid FROM t WHERE {pred} ORDER BY rid", false, false);
        foreach (bool ansi in new[] { false, true })
            foreach (bool exact in new[] { false, true })
                foreach (string pred in new[] { "cname IN ('B')", "cname IN ('Bob')", "cname BETWEEN 'A' AND 'B'", "cname BETWEEN 'Bob' AND 'Bob'", "cname LIKE 'Bob'", "cname LIKE 'Bob%'" })
                    yield return ($"SELECT rid FROM t WHERE {pred} ORDER BY rid", ansi, exact);
        foreach (string expression in new[] { "cid IN (1,2)", "cid BETWEEN 1 AND 2", "cname LIKE 'A%'" })
            yield return ($"SELECT {expression} AS f FROM t", false, false);
        foreach (string pred in new[] { "cid IN (1,2)", "cid BETWEEN 1 AND 2", "cname LIKE 'A%'" })
        {
            yield return ($"SELECT rid,cid,cname FROM t GROUP BY rid,cid,cname HAVING {pred} ORDER BY rid", false, false);
            yield return ($"SELECT t.rid FROM t JOIN t other ON t.rid=other.rid AND t.{pred} ORDER BY t.rid", false, false);
            yield return ($"UPDATE t SET cid=cid WHERE {pred}", false, false);
            yield return ($"DELETE FROM t WHERE {pred}", false, false);
        }
        foreach (var extra in ExtraInputs()) yield return extra;
    }

    internal static IEnumerable<(string Sql, bool Ansi, bool Exact)> ExtraInputs()
    {
        foreach (string pred in new[]
        {
            "(cid) BETWEEN (1) AND (2)", "cid BETWEEN (1) AND (2) AND EXISTS (SELECT * FROM t z WHERE z.rid=t.rid)",
            "BETWEEN(cid,1,2) AND EXISTS (SELECT * FROM t z WHERE z.rid=t.rid)",
            "NOT NOT cid IN (1,2)", ".NOT. cid IN (1,2)", "cid IN (1,2) .AND. rid=2",
            "cid IN (1,2) OR cid BETWEEN 3 AND 4 AND cname LIKE 'A%'",
            "cid IN (cid,'x')", "cid IN (.NULL.,cid)", "cid IN (.NULL.,'x')",
            "cid IN (1,)", "cid BETWEEN 1", "cname LIKE", "cname LIKE 'A%' ESCAPE",
            "cname LIKE IIF(rid=6,.NULL.,'B%')", "cname LIKE m.nul", "cname LIKE IIF(rid=2,.NULL.,'A%')",
            "cname LIKE '%b'", "cname LIKE 'B% '" , "cname LIKE 'B%' ESCAPE '%'",
            "'A_' LIKE 'A!_' ESCAPE '!'", "'A%' LIKE 'A!%' ESCAPE '!'",
            "'A_' LIKE 'A!!_' ESCAPE '!'", "cname LIKE 'A%' ESCAPE cname",
            "cid IN (SELECT cid FROM t z WHERE z.rid IN (t.rid))",
            "cid BETWEEN 1 AND 2 AND cid IN (SELECT cid FROM t z WHERE z.rid=t.rid)",
            "NOT cid BETWEEN 1 AND 2 AND EXISTS (SELECT * FROM t z WHERE z.rid=t.rid)",
        }) yield return ($"SELECT rid FROM t WHERE {pred} ORDER BY rid", false, false);
        foreach (bool ansi in new[] { false, true })
            foreach (bool exact in new[] { false, true })
                foreach (string pred in new[] { "cname IN ('B')", "cname BETWEEN 'A' AND 'B'", "cname LIKE 'Bob'" })
                    foreach (string command in new[] { "UPDATE t SET cid=cid", "DELETE FROM t" })
                        yield return ($"{command} WHERE {pred}", ansi, exact);
        foreach (string having in new[] { "COUNT(*) IN (1,2)", "SUM(rid) BETWEEN 1 AND 3", "UPPER(cname) LIKE 'B%'" })
            yield return ($"SELECT MIN(rid) AS firstid,cname FROM t GROUP BY cname HAVING {having} ORDER BY firstid", false, false);
        foreach (string predicate in new[]
        {
            "cid IN (SELECT cid FROM t z WHERE z.rid BETWEEN t.rid AND t.rid)",
            "cid IN (SELECT cid FROM t z WHERE z.cname LIKE t.cname)",
            "cid IN (SELECT cid FROM t z WHERE z.rid IN (t.rid+0))",
            "cid IN (SELECT cid FROM t z WHERE t.rid IN (z.rid))",
            "cid IN (SELECT cid FROM t z WHERE z.rid IN (1,2))",
            "cname LIKE IIF(.T.,.NULL.,'A%')", "m.nul LIKE 'A%'", "IIF(.T.,.NULL.,'A') LIKE 'A%'",
            "cname LIKE 'A%' ESCAPE cname AND rid<>6", "cname LIKE 'A%' ESCAPE IIF(rid=6,'!',cname)",
            "cid IN (cid,m.missing)",
        }) yield return ($"SELECT rid FROM t WHERE {predicate} ORDER BY rid", false, false);
        yield return ("SELECT rid FROM t WHERE cid NOT BETWEEN (1) AND (2) AND EXISTS (SELECT * FROM t z WHERE z.rid=t.rid) ORDER BY rid", false, false);
        yield return ("SELECT rid FROM t WHERE cid IN (SELECT cid FROM t z WHERE z.rid IN (1,2) AND z.rid=t.rid) ORDER BY rid", false, false);
        foreach (string predicate in new[]
        {
            "cid IN (1,'2')", "cid IN (1,'7')", "cid IN (1,'x',2)", "cid IN (1,.T.)",
            "cid IN (.NULL.,1,'x')", "cid NOT IN (1,'x')", "cid IN (1,'x') OR cid=7",
            "cname IN ('Bob',2)", "cname IN (2,'Bob')", "cid+0 IN (1,'x')",
            "UPPER(cname) LIKE 'A%' ESCAPE '!!'", "cname NOT LIKE 'A%' ESCAPE '!!'",
            "cname LIKE '%A%' ESCAPE '!!'", "cname LIKE 'A%' ESCAPE m.longesc",
        }) yield return ($"SELECT rid FROM t WHERE {predicate} ORDER BY rid", false, false);
        foreach (string predicate in new[] { "cid IN (1,'x')", "cname LIKE 'A%' ESCAPE '!!'" })
        {
            yield return ($"SELECT rid,cid,cname FROM t GROUP BY rid,cid,cname HAVING {predicate} ORDER BY rid", false, false);
            yield return ($"SELECT t.rid FROM t JOIN t other ON t.rid=other.rid AND t.{predicate} ORDER BY t.rid", false, false);
            yield return ($"UPDATE t SET cid=cid WHERE {predicate}", false, false);
            yield return ($"DELETE FROM t WHERE {predicate}", false, false);
        }
    }

    // Captured from SqlInfixVfp9OracleTests on native VFP9-created tables.
    internal static readonly string[] Expected =
    [
        "1,2,",
        "3,4,5,7,",
        "3,4,5,7,",
        "3,4,5,7,",
        "2,",
        "error:10",
        "1,",
        "",
        "1,",
        "error:107",
        "error:107",
        "2,3,",
        "2,",
        "2,",
        "2,",
        "1,3,5,7,",
        "1,3,5,",
        "1,2,3,",
        "",
        "4,5,7,",
        "4,5,7,",
        "4,5,7,",
        "",
        "",
        "4,5,7,",
        "2,",
        "1,2,7,",
        "1,2,4,5,",
        "2,",
        "error:107",
        "1,4,5,",
        "4,5,",
        "2,",
        "2,",
        "3,",
        "2,",
        "2,3,7,",
        "2,3,7,",
        "2,3,7,",
        "error:1833",
        "1,4,5,",
        "1,4,5,",
        "4,",
        "5,",
        "1,4,5,",
        "1,4,5,",
        "1,4,5,",
        "error:11",
        "error:11",
        "2,",
        "7,",
        "1,2,3,4,5,7,",
        "",
        "",
        "4,",
        "",
        "error:11",
        "error:11",
        "1,2,3,4,5,6,7,",
        "1,2,3,4,5,6,7,",
        "",
        "2,",
        "2,",
        "2,",
        "",
        "1,2,3,4,5,6,7,",
        "1,2,3,4,5,7,",
        "1,2,3,4,5,7,",
        "error:1833",
        "error:1833",
        "1,2,",
        "error:107",
        "2,",
        "3,4,5,7,",
        "2,",
        "2,",
        "1,2,4,5,",
        "2,",
        "2,",
        "2,",
        "2,",
        "2,",
        "1,2,4,5,",
        "2,",
        "2,",
        "2,",
        "",
        "2,",
        "1,4,5,",
        "2,",
        "2,",
        "2,",
        "",
        "2,",
        "1,4,5,",
        "2,",
        "2,",
        "2,",
        "error:36",
        "error:36",
        "error:36",
        "1,2,",
        "1,2,",
        "tally:2",
        "tally:2",
        "1,2,",
        "1,2,",
        "tally:2",
        "tally:2",
        "1,4,5,",
        "1,4,5,",
        "tally:3",
        "tally:3",
        "1,2,",
        "1,2,",
        "1,2,",
        "1,2,",
        "3,4,5,7,",
        "2,",
        "1,2,4,",
        "1,2,3,4,5,7,",
        "1,2,3,4,5,7,",
        "error:107",
        "error:10",
        "error:10",
        "error:36",
        "error:36",
        "2,",
        "error:11",
        "1,4,5,",
        "2,3,",
        "2,",
        "2,",
        "1,2,3,4,5,6,7,",
        "1,2,3,4,5,6,7,",
        "",
        "error:11",
        "error:1801",
        "1,2,",
        "3,4,5,7,",
        "tally:1",
        "tally:1",
        "tally:4",
        "tally:4",
        "tally:1",
        "tally:1",
        "tally:1",
        "tally:1",
        "tally:4",
        "tally:4",
        "tally:1",
        "tally:1",
        "tally:0",
        "tally:0",
        "tally:3",
        "tally:3",
        "tally:1",
        "tally:1",
        "tally:0",
        "tally:0",
        "tally:3",
        "tally:3",
        "tally:1",
        "tally:1",
        "1,2,3,4,5,6,7,",
        "1,2,3,",
        "2,3,",
        "error:1801",
        "error:1801",
        "error:1801",
        "error:1801",
        "1,2,",
        "error:1833",
        "error:11",
        "error:1833",
        "error:11",
        "",
        "error:1806",
        "3,4,5,7,",
        "1,2,",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "1,4,5,",
        "2,3,7,",
        "1,4,5,",
        "1,4,5,",
        "error:107",
        "error:107",
        "error:107",
        "error:107",
        "1,4,5,",
        "1,4,5,",
        "tally:3",
        "tally:3",
    ];

    // Native CDX/Rushmore changes error timing for these predicates.
    private static readonly Dictionary<int, string> IndexedOverrides = new()
    {
        [10] = "1,",
        [44] = "error:11",
        [45] = "error:11",
        [54] = "error:11",
        [55] = "error:11",
        [180] = "1,",
        [181] = "1,",
        [183] = "1,",
        [184] = "1,",
        [191] = "error:11",
        [192] = "error:11",
        [193] = "error:11",
        [195] = "1,",
        [199] = "error:11",
        [200] = "error:11",
        [201] = "error:11",
    };

    internal static string ExpectedResult(int index, bool indexed)
        => indexed && IndexedOverrides.TryGetValue(index, out string? result) ? result : Expected[index];

    public static IEnumerable<object[]> Cases() => CasesFor(indexed: false);
    internal static IEnumerable<object[]> CasesFor(bool indexed) => Inputs().Select((c, i) => (c, Expected: ExpectedResult(i, indexed)))
        .Distinct().Select(c => new object[] { c.c.Sql, c.c.Ansi, c.c.Exact, c.Expected });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Conditions_MatchVfp9(string sql, bool ansi, bool exact, string expected)
    {
        using var temp = new SqlTestSupport.TempDir();
        CreateFixture(temp.Path);
        Assert.Equal(expected, Execute(temp.Path, sql, ansi, exact));
    }

    internal static void CreateFixture(string dir)
    {
        using var w = DbfWriter.Create(Path.Combine(dir, "t.dbf"), new[]
        {
            new DbfColumnDef("rid", 'I', 4), new DbfColumnDef("cid", 'I', 4, nullable: true),
            new DbfColumnDef("cname", 'C', 10, nullable: true), new DbfColumnDef("d", 'D', 8, nullable: true),
            new DbfColumnDef("flag", 'L', 1, nullable: true),
        });
        string?[] names = ["Alpha", "Bob", "bob", "A%X", "A_X", null, ""];
        for (int i = 1; i <= 7; i++)
            w.AppendRecord(i, i == 6 ? null : i, names[i - 1], i == 6 ? null : new DateOnly(2024, 1, i == 2 ? 2 : i == 4 ? 3 : 1),
                i == 6 ? null : i is 1 or 3 or 5);
    }

    internal static string Execute(string dir, string sql, bool ansi, bool exact, bool optimize = true)
    {
        if (sql.Contains("m.", StringComparison.OrdinalIgnoreCase)) return ExecuteHosted(dir, sql, ansi, exact, optimize);
        using var session = new VfpSession(new EvaluationContext { Ansi = ansi, Exact = exact, Deleted = true, Optimize = optimize });
        session.OpenDirectory(dir);
        try
        {
            var result = session.Execute(sql)!;
            return result.AffectedRecords >= 0 ? "tally:" + result.AffectedRecords
                : string.Concat(result.Rows.Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture) + ","));
        }
        catch (FoxDbfSqlException ex) { return "error:" + ex.VfpErrorNumber; }
        catch (ExpressionException ex) { return "error:" + ex.VfpErrorNumber; }
    }

    internal static string ExecuteHosted(string dir, string sql, bool ansi, bool exact, bool optimize = true)
    {
        using var session = new VfpSession();
        session.OpenDirectory(dir);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"SET OPTIMIZE {(optimize ? "ON" : "OFF")}\nSET ANSI {(ansi ? "ON" : "OFF")}\nSET EXACT {(exact ? "ON" : "OFF")}\nSET DELETED ON\nval=3\npat='A%'\nesc='!'\nnul=.NULL.\nlongesc='!!'");
        try
        {
            interpreter.Execute(sql + (sql.StartsWith("SELECT") ? " INTO ARRAY result" : ""));
            int count = interpreter.EvalExpression("_TALLY").AsInteger;
            if (!sql.StartsWith("SELECT")) return "tally:" + count;
            return string.Concat(Enumerable.Range(1, count).Select(i =>
                interpreter.EvalExpression($"result[{i},1]").AsInteger + ","));
        }
        catch (Exception ex) { return "error:" + VfpInterpreter.ErrorNumberOf(ex); }
    }
}

public sealed class MicroVfpSqlInfixTests
{
    public static IEnumerable<object[]> IndexedCases()
    {
        foreach (object[] row in SqlInfixPredicateTests.CasesFor(indexed: true))
            foreach (bool optimize in new[] { false, true })
                yield return [row[0], row[1], row[2], row[3], optimize];
    }

    [Theory]
    [MemberData(nameof(IndexedCases))]
    public void IndexedEmbeddedConditions_MatchVfp9(string sql, bool ansi, bool exact, string expected, bool optimize)
    {
        using var temp = new SqlTestSupport.TempDir();
        SqlInfixPredicateTests.CreateFixture(temp.Path);
        using (var writer = DbfWriter.Open(temp.File("t.dbf")))
        {
            writer.CreateTag(new CdxTagDefinition("cid", "cid"));
            writer.CreateTag(new CdxTagDefinition("cname", "cname"));
        }
        Assert.Equal(expected, SqlInfixPredicateTests.ExecuteHosted(temp.Path, sql, ansi, exact, optimize));
    }

    [Theory]
    [MemberData(nameof(SqlInfixPredicateTests.Cases), MemberType = typeof(SqlInfixPredicateTests))]
    public void EmbeddedConditions_MatchVfp9(string sql, bool ansi, bool exact, string expected)
    {
        using var temp = new SqlTestSupport.TempDir();
        SqlInfixPredicateTests.CreateFixture(temp.Path);
        Assert.Equal(expected, SqlInfixPredicateTests.ExecuteHosted(temp.Path, sql, ansi, exact));
    }
}
