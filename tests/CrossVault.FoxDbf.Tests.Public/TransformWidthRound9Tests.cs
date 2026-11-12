using System.Text;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class TransformWidthRound9Tests
{
    internal sealed record Probe(string Key, string Body);

    internal static Probe[] Probes(int decimals)
    {
        var probes = new List<Probe>();
        foreach (string type in new[] { "B(4)", "B(2)", "N(20,4)", "F(20,4)" })
        foreach (string number in new[] { "1e14", "1e15", "1e16", "1e17", "1.5e15",
            "999999999999999.5", "123456789012345.6789" })
        foreach (string sign in new[] { "", "-" })
        {
            string setup = $"CREATE TABLE t FREE (g I, val {type})\n"
                + $"INSERT INTO t VALUES (1,{sign}{number})\nINSERT INTO t VALUES (1,{sign}{number})\n";
            probes.Add(new($"{decimals}/{type}/{sign}{number}", Body(setup, "val", decimals)));
        }

        // The adjudication's binary payloads are already pinned. These probes add their display paths.
        string aggregate = "CREATE TABLE t FREE (g I, val B(4))\n"
            + string.Concat(Enumerable.Repeat("INSERT INTO t VALUES (1,999999999999999.5)\n", 3));
        probes.Add(new($"{decimals}/adjudication/SUM(b)", Body(aggregate, "val", decimals)));
        string narrow = "CREATE TABLE t FREE (g I, a N(18,4))\n"
            + "INSERT INTO t VALUES (1,1234567890123.5678)\n"
            + "INSERT INTO t VALUES (1,9999999999999.9999)\n"
            + "INSERT INTO t VALUES (1,10000000000000)\n"
            + "INSERT INTO t VALUES (1,0.0001)\nINSERT INTO t VALUES (1,0.1001)\n";
        probes.Add(new($"{decimals}/adjudication/SUM(a18)", Body(narrow, "a", decimals)));
        string mixed = "CREATE TABLE t FREE (g I, a N(20,4), f B(2))\n"
            + "INSERT INTO t VALUES (1,123456789012345.6789,0.1)\n"
            + "INSERT INTO t VALUES (1,-999999999999997.5,123456789012.345)\n"
            + "INSERT INTO t VALUES (1,99999999999.9999,1e17)\n"
            + "INSERT INTO t VALUES (1,12345678901.2345,-2.5)\n"
            + "INSERT INTO t VALUES (1,1,7)\n";
        probes.Add(new($"{decimals}/adjudication/SUM(f)", Body(mixed, "f", decimals)));
        probes.Add(new($"{decimals}/adjudication/SUM(a)", Body(mixed, "a", decimals)));
        return probes.ToArray();
    }

    private static string Body(string setup, string field, int decimals)
    {
        var body = new StringBuilder($"CLOSE TABLES ALL\nSET FIXED OFF\nSET DECIMALS TO {decimals}\n{setup}GO TOP\nresult=''\n");
        void Observe(string expression) => body.AppendLine(
            $"v={expression}\nresult=result+TRANSFORM({expression})+'|'+TRANSFORM(v)+';'\n");
        Observe(field);
        foreach (string projection in new[] { field, $"SUM({field})" })
        {
            foreach (string group in projection == field ? new[] { "" } : new[] { "", " GROUP BY g" })
            {
                body.AppendLine($"SELECT {projection} AS outval FROM t{group} INTO CURSOR rr\nGO TOP");
                Observe("outval");
                body.AppendLine("USE IN rr\nSELECT t");
            }
            body.AppendLine($"SELECT {projection} AS outval FROM t INTO ARRAY aa");
            Observe("aa[1,1]");
        }
        return body.ToString();
    }

    internal static string Observe(Probe probe)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthround9");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        return TransformWidthRound3Tests.Observe(vfp, probe.Body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void LargeFiniteTransform_MatchesExpectedVfp(int decimals)
    {
        var failures = new List<string>();
        foreach (var probe in Probes(decimals))
        {
            string actual = Observe(probe);
            if (!ExpectedVfp.TryGetValue(probe.Key, out var expected) || actual != expected)
                failures.Add(probe.Key + ": VFP9=" + expected + "; actual=" + actual);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
