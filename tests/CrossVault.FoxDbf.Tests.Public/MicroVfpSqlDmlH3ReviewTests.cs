namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpSqlDmlH3ReviewTests
{
    internal const string Procedures = "FUNCTION badrule\nUPDATE aux SET k=123\nRETURN .F.\nENDFUNC\nFUNCTION recgate\ncalls=calls+1\nRETURN n=0\nENDFUNC\nFUNCTION later\nlatercalls=latercalls+1\nRETURN 9\nENDFUNC";
    internal static string Setup(string kind) => "PUBLIC calls,latercalls\ncalls=0\nlatercalls=0\nCREATE TABLE aux (k C(4))\nINSERT INTO aux VALUES ('ok')\nCREATE TABLE src (id N(4))\nINSERT INTO src VALUES (1)\n" +
        "CREATE TABLE t (id N(4), n I, k " + (kind is "field" or "error" ? "I CHECK " + (kind == "field" ? "IIF(k=1,.T.,badrule())" : "k=1 ERROR badrule()") : "C(4)") +
        ", tail I" + (kind == "record" ? ", CHECK recgate()" : "") + ")\nINSERT INTO t VALUES (1,0," + (kind is "field" or "error" ? "1" : "'keep'") + ",0)";
    internal static string Scenario(string kind, bool xbase) => "USE src IN 0\nUSE t IN 0\nSELECT t\ncalls=0\n" +
        MicroVfpSqlDmlTests.Catch((xbase ? "UPDATE ON id FROM src " : "") + "REPLACE " + (kind == "type" ? "k WITH 'ok', n WITH 'x'" : "n WITH 1, k WITH " + (kind is "field" or "error" ? "2" : "123")) + (xbase ? "" : ", tail WITH later()")) +
        "\nresult=result+TRANSFORM(n)+'|'+ALLTRIM(TRANSFORM(k))+'|'+TRANSFORM(calls)+'|'+TRANSFORM(latercalls)";
    [Theory]
    [InlineData("field", false)]
    [InlineData("field", true)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    [InlineData("type", false)]
    [InlineData("type", true)]
    [InlineData("record", false)]
    [InlineData("record", true)]
    public void H3_PartialAssignments(string kind, bool xbase) => Assert.Equal("ERR:9|" + (kind == "type" ? "0|ok" : kind == "field" ? "1|2" : kind == "error" ? "0|1" : "0|keep") + (kind == "record" ? "|1|0" : "|0|0"),
        MicroVfpSqlDmlFinalReviewTests.Run(Setup(kind), Scenario(kind, xbase), Procedures));
    public static IEnumerable<object[]> Types()
    {
        foreach (string type in new[] { "I", "N(5)", "F(5)", "B", "Y", "L", "D", "T", "C(4)", "V(4)", "Q(4)", "M", "W", "G" })
        foreach (string value in new[] { "'x'", "1", ".T.", "{^2026-01-01}", "{^2026-01-01 12:00:00}", ".NULL.", "CREATEBINARY('AB')" })
        foreach (int buffering in new[] { 1, 5 })
        {
            bool accepted = value == ".NULL." || type[0] switch
            {
                'I' or 'N' or 'F' or 'B' or 'Y' => value == "1",
                'L' => value == ".T.",
                'D' or 'T' => value.StartsWith('{'),
                'C' or 'V' or 'Q' or 'M' or 'W' => value is "'x'" or "CREATEBINARY('AB')",
                _ => false
            };
            yield return new object[] { type, value, buffering, accepted };
        }
    }
    [Theory]
    [MemberData(nameof(Types))]
    public void H3_RejectTypeBeforeLaterClause(string type, string value, int buffering, bool accepted) =>
        Assert.Equal(accepted ? "1|1" : "ERR:9|1|0", MicroVfpSqlDmlFinalReviewTests.Run(
            "PUBLIC latercalls\nlatercalls=0\nCREATE TABLE t (prefix I, n " + type + " NULL, tail I)\nAPPEND BLANK",
            $"USE t\nSET MULTILOCKS ON\n=CURSORSETPROP('Buffering',{buffering})\n" + MicroVfpSqlDmlTests.Catch("REPLACE prefix WITH 1, n WITH " + value + ", tail WITH later()") +
            "\nresult=result+TRANSFORM(prefix)+'|'+TRANSFORM(latercalls)" + (buffering > 1 ? "\n=TABLEREVERT(.T.)" : ""), Procedures));

}
