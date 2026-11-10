namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleLiteralTests
{
    internal const string Setup = "CREATE TABLE t (b B(3),f F(10,3))\nINSERT INTO t VALUES (1.5,1.5)";
    internal static readonly (string Name, string Expected)[] Cases =
    [
        ("d2:1e-2", "0.01|        0.0100000000"),
        ("d2:1.50e-2", "0.0150|        0.0150000000"),
        ("d2:1.50e2", "150|      150.0000000000"),
        ("d2:1e-10", "0.0000000001|        0.0000000001"),
        ("d2:1.50e0", "1.50|        1.5000000000"),
        ("d2:1.50e-1", "0.150|        0.1500000000"),
        ("d2:b", "1.500|        1.5000000000"),
        ("d2:f", "1.500|        1.5000000000"),
        ("d2:b+1", "2.500|        2.5000000000"),
        ("d2:f*1.5", "2.2500|        2.2500000000"),
        ("d2:b/2", "0.7500|        0.7500000000"),
        ("d2:-f", "-1.500|       -1.5000000000"),
        ("d2:ROUND(b,4)", "1.5000|        1.5000000000"),
        ("d2:MOD(f,1)", "0.500|        0.5000000000"),
        ("d4:1e-2", "0.01|        0.0100000000"),
        ("d4:1.50e-2", "0.0150|        0.0150000000"),
        ("d4:1.50e2", "150|      150.0000000000"),
        ("d4:1e-10", "0.0000000001|        0.0000000001"),
        ("d4:1.50e0", "1.50|        1.5000000000"),
        ("d4:1.50e-1", "0.150|        0.1500000000"),
        ("d4:b", "1.500|        1.5000000000"),
        ("d4:f", "1.500|        1.5000000000"),
        ("d4:b+1", "2.500|        2.5000000000"),
        ("d4:f*1.5", "2.2500|        2.2500000000"),
        ("d4:b/2", "0.750000|        0.7500000000"),
        ("d4:-f", "-1.500|       -1.5000000000"),
        ("d4:ROUND(b,4)", "1.5000|        1.5000000000"),
        ("d4:MOD(f,1)", "0.500|        0.5000000000"),
    ];
    internal static string Scenario(string name) => $"USE t\nSET DECIMALS TO {name[1]}\nx={name[3..]}\nresult=TRANSFORM(x)+'|'+STR(x,20,10)";
    [Fact]
    public void ExponentsAndDoubleFloatScale_MatchVfp9()
    {
        foreach (var test in Cases)
            Assert.Equal(test.Expected, MicroVfpXbaseUpdateTests.Run(Setup, Scenario(test.Name)));
    }
}
