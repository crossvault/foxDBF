using System.Globalization;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class MicroVfpNpvRound6Tests
{
    internal sealed record Probe(string Name, string Body)
    {
        public string ExpectedVfp => Pins[Name];
    }
    internal const string Setup = "CREATE TABLE t FREE (n N(10,2) NULL, nan N(10,2), infinity N(10,2))\nINSERT INTO t VALUES (1,3,4)\nINSERT INTO t VALUES (2,5,6)\nUSE";
    internal static string Catch(string body) => "TRY\n" + body + "\nCATCH TO failure\nresult='ERR|'+TRANSFORM(failure.ErrorNo)\nENDTRY";
    internal static string Scalar(string expression) => "v=" + expression + "\n" + MicroVfpNpvRound5Tests.Format;
    internal static readonly Probe[] Scalars = new[]
    {
        "NORMALIZE('nan')", "NORMALIZE('infinity')", "NORMALIZE('+infinity')", "NORMALIZE('-nan')",
        "NORMALIZE('1e99999999999999999999')", "NORMALIZE(' -1e999 ')",
        "1/0", "0/0", "-1/0", "1/0=1/0", "0/0=0/0", "0/0>1", "0/0<1",
        "ISNULL(1/0)", "VARTYPE(1/0)", "STR(1/0)", "TRANSFORM(1/0)",
        "IIF(1/0>5,'big','small')", "IIF(0/0>5,'big','small')",
        "-8^(1/3)", "(-8)^(1/3)", "-(8^(1/3))", "(-8)^2", "(-8)^0.5",
        "TRANSFORM(0/0)", "TRANSFORM(-1/0)", "TRANSFORM(5/0)", "TRANSFORM(1.00/0)",
        "TRANSFORM(EXP(1000))", "TRANSFORM(10^400)", "TRANSFORM(-(1/0))",
        "TRANSFORM(1/0+1)", "TRANSFORM(1/0,'')", "TRANSFORM((1/0))",
        "0/0<>0/0", "0/0<=0/0", "0/0>=0/0", "0/0=1", "0/0=-1/0", "0/0<-1/0",
        "-5/0", "1/-0", "-1/-0", "-(1/0)", "1.00/0", "0/-0", "0/0=1/0", "-1/0<0/0", "0/0> -1/0", "0/0==-1/0", "INLIST(0/0,-1/0)", "BETWEEN(0/0,-1/0,-1/0)"
    }.Select(e => new Probe(e, Scalar(e))).ToArray();

    internal static readonly Probe[] Arrays = ArrayCases().ToArray();
    private static IEnumerable<Probe> ArrayCases()
    {
        foreach (string dimensions in new[] { "6", "3,2" })
        foreach (int start in new[] { 6, 7, 8, 0, -1, -2 })
        foreach (string count in new[] { "", ",0", ",-1", ",-2", ",1", ",100", ",2147483647" })
        {
            string expression = $"ASCAN(aa,6,{start}{count})";
            yield return new Probe(dimensions + "/" + expression,
                $"DIMENSION aa[{dimensions}]\nFOR i=1 TO 6\naa[i]=i\nENDFOR\nresult=TRANSFORM({expression})");
        }
    }

    internal static readonly Probe[] Commands = new Probe[]
    {
        new("replace", "USE t\nGO TOP\nREPLACE n WITH n/0\n" + Scalar("n")),
        new("calculate", "USE t\nCALCULATE SUM(n/0) TO v\n" + MicroVfpNpvRound5Tests.Format),
        new("nan-key", "USE t\nINDEX ON nan TAG nk\nresult=KEY()"),
        new("infinity-key", "USE t\nINDEX ON infinity TAG ik\nresult=KEY()"),
        new("divide-index", "USE t\nINDEX ON n/0 TAG dz\nresult=KEY()\nSEEK 1/0\nresult=result+'|'+TRANSFORM(FOUND())+'|'+TRANSFORM(RECNO())\nSEEK -1/0\nresult=result+'|'+TRANSFORM(FOUND())\nSEEK 0/0\nresult=result+'|'+TRANSFORM(FOUND())"),
    }.Concat(AggregateCases()).Concat(TransformCases()).ToArray();

    private static IEnumerable<Probe> AggregateCases()
    {
        foreach (int decimals in new[] { 0, 2, 4, 10 })
        foreach (string command in new[] { "CALCULATE SUM", "CALCULATE AVG", "SUM", "AVERAGE" })
        foreach (string expression in new[] { "n/0", "0/0", "-n/0", "EXP(1000)", "1/0", "-1/0", "10^400", "INT(1/0)" })
        {
            string body = command.StartsWith("CALCULATE") ? $"{command}({expression})" : $"{command} {expression}";
            yield return new Probe($"{decimals}/{command}/{expression}", $"SET DECIMALS TO {decimals}\nUSE t\nv=0\n{body} TO v\n" + MicroVfpNpvRound5Tests.Format + "\nSET DECIMALS TO 2");
        }
    }

    private static IEnumerable<Probe> TransformCases()
    {
        foreach (int decimals in new[] { 0, 2, 4 })
        foreach (string expression in new[] { "TRANSFORM(1/0)", "TRANSFORM(-1/0)", "TRANSFORM(1/0,'')", "TRANSFORM(0/0,'')", "TRANSFORM(1/0+1)", "TRANSFORM((1/0)*2)", "TRANSFORM(100/0)", "TRANSFORM(1/0.00)", "TRANSFORM(10^400)", "TRANSFORM(-(10^400))", "TRANSFORM(1/0,'999.99')" })
            yield return new Probe($"{decimals}/{expression}", $"SET DECIMALS TO {decimals}\nUSE t\nGO TOP\n" + Scalar(expression) + "\nSET DECIMALS TO 2");
    }

    internal sealed record Query(string Sql)
    {
        public string ExpectedVfp => Pins[Sql];
    }
    internal static readonly Query[] Queries =
    [
        new("SELECT n/0 AS v FROM t"),
        new("SELECT 0/0 AS v FROM t"),
        new("SELECT -n/0 AS v FROM t"),
        new("SELECT n AS v FROM t WHERE n/0>1"),
        new("SELECT n/0=n/0 AS v FROM t"),
        new("SELECT ISNULL(n/0) AS v FROM t"),
        new("SELECT IIF(n/0>5,'big','small') AS v FROM t"),
        new("SELECT n AS v FROM t WHERE 0/0>1"),
    ];
    internal const string SqlFormat = "IF ISNULL(v)\npart='<NULL>'\nELSE\nIF VARTYPE(v)$'NIYB'\npart=ALLTRIM(STR(v,30,6))\nELSE\npart=TRANSFORM(v)\nENDIF\nENDIF\nresult=result+'['+part+']'";
    internal static string QueryScript(Query q) => q.Sql + " INTO CURSOR q\nresult=''\nSELECT q\nSCAN\n" + SqlFormat + "\nENDSCAN";
    internal const string Cleanup = "CLOSE TABLES ALL\nUSE t\nDELETE TAG ALL\nREPLACE ALL n WITH RECNO()\nUSE";
    internal static readonly Probe[] Evaluation =
    [
        new("negated-negative-quotient", Scalar("-(-1/0)")),
        new("direct-double-negation", Scalar("TRANSFORM(-(-1/0))")),
        new("negative-zero-divisor", Scalar("SIGN(1/-0)")),
        new("negative-zero-dividend", Scalar("SIGN(-1/-0)")),
        new("quotient-in-power", Scalar("-(10^(400/1))")),
        new("replace-retains-field", "USE t\nGO TOP\n" + Catch("REPLACE n WITH n/0") + "\nresult=result+'|'+TRANSFORM(n)"),
    ];
    internal static Probe[] Group(string group) => group switch
    {
        "scalars" => Scalars, "arrays" => Arrays, "commands" => Commands,
        "evaluation" => Evaluation,
        _ => Queries.Select(q => new Probe(q.Sql, QueryScript(q))).ToArray()
    };
    public static IEnumerable<object[]> Cases => new[] { "scalars", "arrays", "commands", "sql", "evaluation" }
        .SelectMany(g => Group(g).Select((_, i) => new object[] { g, i }));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Interpreter_MatchesVfp9(string group, int index)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round6");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup);
        var p = Group(group)[index];
        vfp.Execute(Catch(p.Body));
        Assert.Equal(p.ExpectedVfp, vfp.Memory.Get("result").AsString);
    }

    internal static string SqlSnapshot(IEnumerable<object?> values) => string.Concat(values.Select(v => "[" + (v switch
    {
        null or DBNull => "<NULL>",
        bool b => b ? ".T." : ".F.",
        string s => s,
        double d when !double.IsFinite(d) => new string('*', 30),
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("F6", CultureInfo.InvariantCulture)
    }) + "]"));

    internal static void CheckSqlPaths(string root)
    {
        using (var session = new VfpSession())
        {
            session.OpenDirectory(root);
            foreach (var q in Queries)
                Assert.Equal(q.ExpectedVfp, SqlSnapshot(session.Execute(q.Sql)!.Rows.Select(r => r[0])));
        }
        using var connection = new FoxDbfConnection($"Data Source={root}");
        connection.Open();
        using var command = connection.CreateCommand();
        foreach (var q in Queries)
        {
            command.CommandText = q.Sql;
            using var reader = command.ExecuteReader();
            var values = new List<object?>();
            while (reader.Read()) values.Add(reader.GetValue(0));
            Assert.Equal(q.ExpectedVfp, SqlSnapshot(values));
        }
    }

    [Fact]
    public void SqlSessionAndAdo_MatchVfp9()
    {
        using var dir = new MicroVfpTestSupport.TempDir("round6sql");
        using (var session = new VfpSession())
        {
            session.OpenDirectory(dir.Path);
            using var vfp = new VfpInterpreter(session);
            vfp.Execute(Setup);
        }
        CheckSqlPaths(dir.Path);
    }
}
