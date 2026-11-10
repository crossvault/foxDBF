namespace CrossVault.FoxDbf.Tests;

public sealed partial class MicroVfpNpvRound4Tests
{
    // ExpectedVfp records display, full numeric value, UDF count/record trace, and the next seeded RAND.
    internal sealed record Probe(string Name, int Rows, string Script, string ExpectedVfp);
    internal const string Procedures = "FUNCTION bump()\nPUBLIC calls,trace\ncalls=calls+1\ntrace=trace+TRANSFORM(RECNO())+','\nRETURN 1\nENDFUNC";
    internal static string Setup(int rows) => "CREATE TABLE t FREE (n N(10,2), a N(10,1), b N(10,3))\n"
        + string.Join("\n", Enumerable.Range(1, rows).Select(i => $"INSERT INTO t VALUES ({i * 100},1.2,2.345)")) + "\nUSE";
    private static readonly Dictionary<string,string> Pins = CreatePins();
    internal static readonly Probe[] Probes = Build();
    private static Probe[] Build()
    {
        var result = new List<Probe>();
        void Add(string name, string expr, int rows, string scope = "", int decimals = 2, string fn = "SUM")
        {
            string command = fn.StartsWith("CALCULATE") ? $"{fn}({expr})" : $"{fn} {expr}";
            string script = $"USE t\nGO TOP\nSET DECIMALS TO {decimals}\nPUBLIC calls,trace,mv,mw\ncalls=0\ntrace=''\nmv=1.2\nmw=2.345\n=RAND(1)\nTRY\n{command} {scope} TO x\nresult=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,10))\nCATCH TO failure\nresult='ERR:'+TRANSFORM(failure.ErrorNo)\nENDTRY\nresult=result+'|'+TRANSFORM(calls)+'|'+trace+'|'+ALLTRIM(STR(RAND(),20,10))\nUSE";
            result.Add(new(name, rows, script, Pins[name]));
        }
        string[] shapes = ["n", "1.2", "mv", "bump()", "bump()+n", "ABS(bump())", "ABS(ABS(bump()))", "ABS(ABS(ABS(bump())))",
            "IIF(.T.,bump(),0)", "IIF(.F.,bump(),n)", "IIF(bump()>0,1,0)", "IIF(.T.,IIF(.T.,bump(),0),0)",
            "IIF(.T.,IIF(.T.,IIF(.T.,bump(),0),0),0)", "ICASE(.T.,bump(),.F.,bump(),0)", "ICASE(.F.,bump(),.T.,n)",
            "ICASE(.F.,bump(),n)", "ICASE(.T.,n,.F.,missing,0)", "ICASE(.T.,n,.F.,unknownfn())",
            "IIF(.T.,n,missing)", "IIF(.T.,n,unknownfn())", "NVL(bump(),0)", "NVL(.NULL.,bump())", "EVL(0,bump())", "EVL(bump(),0)",
            "RAND()", "RAND()+n", "ABS(RAND())", "IIF(RAND()>0.8,1,0)", "IIF(.T.,RAND(),0)", "ICASE(RAND()>0.8,1,0)",
            "NVL(.NULL.,RAND())", "EVL(0,RAND())"];
        foreach (string expr in shapes)
        foreach (int rows in new[] {0,1,2,3})
            Add($"eval/{expr}/{rows}/ALL", expr, rows);
        foreach (string expr in shapes)
        foreach (string scope in new[] {"FOR n>100", "FOR .F.", "NEXT 0", "NEXT 1"})
            Add($"eval/{expr}/3/{scope}", expr, 3, scope);
        string[] scales = ["IIF(n=100,1,2.12)", "IIF(n=100,2.12,1)", "ICASE(n=100,1,2.12)", "ICASE(n=100,2.12,1)",
            "ICASE(n=100,1,n>100,2.12)", "ICASE(n=100,2.12,n>100,1)", "IIF(n=100,a,b)", "IIF(n=100,b,a)",
            "IIF(n=100,mv,mw)", "IIF(n=100,mw,mv)", "NVL(IIF(n=100,.NULL.,1),2.12)", "NVL(IIF(n=100,1,.NULL.),2.12)",
            "EVL(IIF(n=100,0,1),2.12)", "EVL(IIF(n=100,1,0),2.12)", "NVL(IIF(n=100,.NULL.,a),b)", "EVL(IIF(n=100,0,mv),mw)",
            "a*b", "a/b", "mv*mw", "mv/mw", "1.2*2.345", "1.2/2.345", "IIF(n=100,a,b)*mv", "IIF(n=100,mv,mw)/a"];
        foreach (string expr in scales)
        foreach (int decimals in new[] {2,4})
        foreach (string fn in new[] {"SUM", "AVERAGE", "CALCULATE SUM", "CALCULATE AVG"})
            Add($"scale/{fn}/{expr}/{decimals}", expr, 2, decimals: decimals, fn: fn);
        foreach (string expr in new[] {"a", "b", "mv", "1.2", "ABS(a)", "a+mv", "a/2", "1.2/2", "IIF(n=100,1,2.12345)", "IIF(n=100,2.12345,1)", "ICASE(n=100,a,b)", "ICASE(n=100,b,a)", "NVL(IIF(n=100,a,.NULL.),b)", "EVL(IIF(n=100,mv,0),mw)"})
        foreach (int decimals in new[] {2,4})
            Add($"extra-scale/{expr}/{decimals}", expr, 2, decimals: decimals);
        foreach (string expr in new[] {"IIF((bump()+n)>0,1,0)", "IIF(bump()>0 AND bump()>0,1,0)", "IIF(.T. OR bump()>0,1,0)", "IIF(NOT bump()<0,1,0)", "IIF(RAND()>0.5,RAND(),RAND())", "ICASE(RAND()>0.5,RAND(),RAND()>0.5,RAND(),RAND())", "IIF(.T.,IIF(.T.,IIF(.T.,IIF(.T.,IIF(.T.,bump(),0),0),0),0),0)", "ICASE(.T.,n,missing,bump(),0)", "NVL(n,unknownfn())", "EVL(n,unknownfn())", "ABS(IIF(.T.,n,missing))", "IIF(.T.,n,ABS(missing))"})
        foreach (int rows in new[] {0,2})
            Add($"extra-eval/{expr}/{rows}", expr, rows);
        foreach (var (first, second) in new[] {("a", "b"), ("mv", "mw")})
        foreach (string expr in new[] {$"ICASE(n=100,{first},{second})", $"ICASE(n=100,{second},{first})",
            $"NVL(IIF(n=100,.NULL.,{first}),{second})", $"NVL(IIF(n=100,{first},.NULL.),{second})",
            $"EVL(IIF(n=100,0,{first}),{second})", $"EVL(IIF(n=100,{first},0),{second})"})
        foreach (int decimals in new[] {2,4})
        foreach (string fn in new[] {"SUM", "AVERAGE", "CALCULATE SUM", "CALCULATE AVG"})
            Add($"branch-scale/{fn}/{expr}/{decimals}", expr, 2, decimals: decimals, fn: fn);
        return result.ToArray();
    }
    public static IEnumerable<object[]> Cases => Probes.Select(p => new object[] { p.Name });
    [Theory]
    [MemberData(nameof(Cases))]
    public void AggregateMatrix(string name)
    {
        var p = Probes.Single(p => p.Name == name);
        using var dir = new MicroVfpTestSupport.TempDir("round4");
        using var session = new CrossVault.FoxDbf.Sql.VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new CrossVault.FoxDbf.MicroVfp.VfpInterpreter(session);
        vfp.Execute(Procedures + "\n" + Setup(p.Rows) + "\n" + p.Script);
        Assert.Equal(p.ExpectedVfp, vfp.Memory.Get("result").AsString);
    }

}
