namespace CrossVault.FoxDbf.Tests;

public sealed class Final3CompatibilityTests
{
    [Theory]
    [InlineData("ON", "AB", "AB  ", false)]
    [InlineData("OFF", "AB  ", "AB", false)]
    [InlineData("OFF", "ABC", "AB", true)]
    public void UpdateOn_VarcharUsesWholeBlankPaddedKeys(string exact, string target, string source, bool random)
        => Assert.Equal(target.TrimEnd() == source.TrimEnd() ? "9|1" : "1|0", MicroVfpXbaseUpdateTests.Run(
            $"CREATE TABLE t (id V(4), n I)\nINSERT INTO t VALUES ('{target}',1)\nINDEX ON id TAG id\nCREATE TABLE src (id V(4), n I)\nINSERT INTO src VALUES ('{source}',9)",
            $"SET EXACT {exact}\nUSE src IN 2\nUSE t IN 1 ORDER id\nUPDATE ON id FROM src REPLACE n WITH src.n{(random ? " RANDOM" : "")}\nGO TOP\nresult=TRANSFORM(n)+'|'+TRANSFORM(_TALLY)"));

    [Theory]
    [InlineData("C(4)", "ON")]
    [InlineData("C(4)", "OFF")]
    [InlineData("V(4)", "ON")]
    [InlineData("V(4)", "OFF")]
    public void UpdateOn_PrefixKeysDoNotConsumeLongerTarget(string type, string exact)
        => Assert.Equal("7|9|2", MicroVfpXbaseUpdateTests.Run(
            $"CREATE TABLE t (id {type}, n I)\nINSERT INTO t VALUES ('ABC',1)\nINSERT INTO t VALUES ('ZZ',2)\nINDEX ON id TAG id\nCREATE TABLE src (id {type}, n I)\nINSERT INTO src VALUES ('AB',5)\nINSERT INTO src VALUES ('ABC',7)\nINSERT INTO src VALUES ('ZZ',9)",
            $"SET EXACT {exact}\nUSE src IN 2\nUSE t IN 1 ORDER id\nUPDATE ON id FROM src REPLACE n WITH src.n\nGO 1\nresult=TRANSFORM(n)+'|'\nGO 2\nresult=result+TRANSFORM(n)+'|'+TRANSFORM(_TALLY)"));

    [Theory]
    [InlineData("MACHINE", "TAG kk")]
    [InlineData("GENERAL", "TAG kk")]
    [InlineData("MACHINE", "TO extra.idx")]
    [InlineData("MACHINE", "TO extra.idx COMPACT")]
    public void EmptySeek_NullableTagMatchesFirstNonNullWithExactOff(string collate, string format)
        => Assert.Equal(".T.|2|.F.", MicroVfpXbaseUpdateTests.Run(
            $"SET COLLATE TO '{collate}'\nCREATE TABLE t (c C(8) NULL)\nINSERT INTO t VALUES (.NULL.)\nINSERT INTO t VALUES ('alpha')\nINDEX ON c {format}",
            "USE t" + (format.StartsWith("TAG") ? " ORDER kk" : " INDEX extra.idx")
            + "\nSET ORDER TO 1\nSET EXACT OFF\nresult=TRANSFORM(SEEK(''))+'|'+TRANSFORM(RECNO())\nSET EXACT ON\nresult=result+'|'+TRANSFORM(SEEK(''))"));
}
