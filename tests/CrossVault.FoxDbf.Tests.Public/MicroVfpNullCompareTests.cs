namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNullCompareTests
{
    [Theory]
    [InlineData("source", false, "1|10|200")]
    [InlineData("source", true, "1|10|200")]
    [InlineData("target", false, "0|10|20")]
    [InlineData("target", true, "1|10|200")]
    [InlineData("both", false, "0|10|20")]
    [InlineData("both", true, "2|100|200")]
    public void UpdateOn_NullKeys_MatchVfp9(string location, bool random, string expected)
        => Assert.Equal(expected, MicroVfpXbaseUpdateTests.Run(Setup(location), Scenario(random)));

    internal static string Setup(string location) => $"""
        CREATE TABLE t (id N(6) NULL, n I)
        INSERT INTO t VALUES ({(location is "target" or "both" ? ".NULL." : "1")},10)
        INSERT INTO t VALUES (2,20)
        INDEX ON id TAG id
        CREATE TABLE src (id N(6) NULL, n I)
        INSERT INTO src VALUES ({(location is "source" or "both" ? ".NULL." : "1")},100)
        INSERT INTO src VALUES (2,200)
        """;

    internal static string Scenario(bool random) => "USE src IN 2\nUSE t IN 1\nSELECT t\nSET ORDER TO id\n"
        + MicroVfpSqlDmlTests.Catch("UPDATE ON id FROM src REPLACE n WITH src.n" + (random ? " RANDOM" : ""))
        + "\nresult=result+TRANSFORM(_TALLY)+'|'\nGO 1\nresult=result+TRANSFORM(n)+'|'\nGO 2\nresult=result+TRANSFORM(n)";

    [Fact]
    public void SeekNull_FindsStoredNullKey()
        => Assert.Equal(".T.|1", MicroVfpXbaseUpdateTests.Run(Setup("target"),
            "USE t\nSET ORDER TO id\nresult=TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(RECNO())"));

    public static IEnumerable<object[]> IndexCases =>
        from format in new[] { "structural", "legacy", "compact", "cdx" }
        from type in new[] { "N(6)", "I", "B", "Y", "C(8)", "D", "T", "L" }
        select new object[] { format, type };

    internal static string IndexSetup(string format, string type)
    {
        string blank = type == "C(8)" ? "''" : type == "D" ? "{}" : type == "T" ? "CTOT('')" : type == "L" ? ".F." : "0";
        return $"CREATE TABLE t (id {type} NULL, n I)\nINSERT INTO t VALUES (.NULL.,1)\nINSERT INTO t VALUES ({blank},2)\nINSERT INTO t VALUES (.NULL.,3)\n"
            + "INDEX ON id " + (format == "structural" ? "TAG id" : format == "cdx" ? "TAG id OF extra.cdx" : "TO extra.idx" + (format == "compact" ? " COMPACT" : "")) + "\nUSE";
    }

    internal static string IndexScenario(string format, string type)
    {
        string open = "USE t" + (format == "structural" ? " ORDER id" : " INDEX extra." + (format == "cdx" ? "cdx" : "idx")) + "\nSET ORDER TO 1\n";
        if (type == "Y") return open + string.Join("\n", new[] { "x=SEEK(.NULL.)", "SEEK .NULL.", "x=KEYMATCH(.NULL.)", "x=INDEXSEEK(.NULL.)" }
            .Select((command, i) => "result=result+'" + i + ":'\n" + MicroVfpSqlDmlTests.Catch(command + "\nresult=result+TRANSFORM(x)+'|'")));
        return open + "result=TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(RECNO())\nSET FILTER TO n=3\nSEEK .NULL.\nresult=result+'|'+TRANSFORM(FOUND())+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(KEYMATCH(.NULL.))+'|'+TRANSFORM(INDEXSEEK(.NULL.))";
    }

    [Theory]
    [MemberData(nameof(IndexCases))]
    public void SeekNull_ReopensAllIndexFormatsAndHonorsFilter(string format, string type)
        => Assert.Equal(type == "Y" ? "0:ERR:9|1:ERR:9|2:.T.|3:ERR:9|" : ".T.|1|.T.|3|.T.|.T.",
            MicroVfpXbaseUpdateTests.Run(IndexSetup(format, type), IndexScenario(format, type)));

    public static IEnumerable<object[]> CharacterCases => from format in new[] { "structural", "legacy", "compact", "cdx" }
        from collation in new[] { "MACHINE", "GENERAL" } select new object[] { format, collation };

    internal static string CharacterSetup(string format, string collation) => IndexSetup(format, "C(8)")
        .Replace("(.NULL.,3)", "('beta',3)").Replace("INDEX ON", "SET COLLATE TO '" + collation + "'\nINDEX ON");

    internal static string CharacterScenario(string format)
    {
        string open = "USE t" + (format == "structural" ? " ORDER id" : " INDEX extra." + (format == "cdx" ? "cdx" : "idx")) + "\nSET ORDER TO 1\n";
        return open + "SET EXACT ON\nresult=TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(RECNO())+'|'+TRANSFORM(SEEK(''))+'|'+TRANSFORM(RECNO())\n"
            + "SET EXACT OFF\nresult=result+'|'+TRANSFORM(SEEK('be'))+'|'+TRANSFORM(RECNO())\nREPLACE id WITH .NULL.\nUSE\n" + open
            + "result=result+'|'+TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(RECNO())\nSET FILTER TO n=3\nresult=result+'|'+TRANSFORM(SEEK(.NULL.))+'|'+TRANSFORM(RECNO())\n"
            + "SET FILTER TO\nGO 1\nREPLACE id WITH 'alpha'\nUSE\n" + open + "result=result+'|'+TRANSFORM(SEEK('alpha'))+'|'+TRANSFORM(RECNO())";
    }

    [Theory]
    [MemberData(nameof(CharacterCases))]
    public void NullableCharacterIndex_DistinguishesNullBlankAndTextAfterWrites(string format, string collation)
        => Assert.Equal(".T.|1|.T.|2|.T.|3|.T.|1|.T.|3|.T.|1", MicroVfpXbaseUpdateTests.Run(CharacterSetup(format, collation), CharacterScenario(format)));
}
