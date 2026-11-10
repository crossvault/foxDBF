using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNpvRound5PrecisionTests
{
    internal sealed record Probe(string Digits, string ExpectedVfp);
    internal static readonly Probe[] Probes =
    [
        new("1000", "N|2.50|2.5000000000;N|2.500000000000000000|2.5000000000"), new("10^100", "N|2.50|2.5000000000;N|2|2.0000000000"), new("-2147483648", "N|2.50|2.5000000000;N|2|2.0000000000"),
        new("-1000", "N|2.50|2.5000000000;N|0|0.0000000000"), new("18", "N|2.50|2.5000000000;N|2.500000000000000000|2.5000000000"), new("19", "N|2.50|2.5000000000;N|2.500000000000000000|2.5000000000"),
    ];
    internal static string Script(Probe p) => MicroVfpNpvRound5Tests.AggregateScript($"IIF(.T.,1.25,ROUND(1.25,{p.Digits}))", "SUM")
        + "\nbranchResult=result\n" + MicroVfpNpvRound5Tests.AggregateScript($"ROUND(1.25,{p.Digits})", "SUM")
        + "\nresult=branchResult+';'+result";
    public static IEnumerable<object[]> Cases => Probes.Select(p => new object[] { p.Digits });

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundMetadata_UsesVfpPrecision(string digits)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round5precision");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        var p = Probes.Single(p => p.Digits == digits);
        vfp.Execute(MicroVfpNpvRound5Tests.Setup + "\n" + Script(p));
        Assert.Equal(p.ExpectedVfp, vfp.Memory.Get("result").AsString);
    }
}
