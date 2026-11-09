using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class DeletionSensitiveIndexTests
{
    public static IEnumerable<object[]> Cases =>
        from function in new[] { "DELETED", "EVALUATE", "EVAL", "RAND", "DATE", "DATETIME", "TIME", "SECONDS", "SYS" }
        from key in new[] { false, true }
        select new object[] { function, key };

    [Theory]
    [MemberData(nameof(Cases))]
    public void DeleteRecall_OnlyDirectDeletedRekeysOnDisk(string function, bool key)
    {
        using var dir = new MicroVfpTestSupport.TempDir("deletion_functions");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Functions + Scenario(function, key));
        string expected = function == "DELETED" ? (key ? ".F.|.T.|.T.|.F." : ".F.|.F.|.T.|.F.") : ".T.|.F.|.T.|.F.";
        Assert.Equal(expected, vfp.Memory.Get("result").AsString);
    }

    internal const string RandomSetup = "SET TABLEPROMPT OFF\nCREATE TABLE t FREE (id I)\nINSERT INTO t VALUES (1)\n"
        + "INDEX ON id TAG a FOR NOT DELETED()\nINDEX ON RAND() TAG b\nUSE\n";

    internal static string[] RandomEntries(string path)
    {
        using var cdx = CrossVault.FoxDbf.Index.CdxFile.Open(path);
        return cdx.Tag("B")!.EnumerateEntries().Select(e => Convert.ToHexString(e.Key) + ":" + e.RecordNumber).ToArray();
    }

    [Fact]
    public void MixedRandomTag_DeletePreservesStoredKey()
    {
        using var dir = new MicroVfpTestSupport.TempDir("delete_random");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(RandomSetup);
        string[] before = RandomEntries(dir.File("t.cdx"));
        vfp.Execute("USE t\nGO 1\nDELETE\nUSE");
        Assert.Equal(before, RandomEntries(dir.File("t.cdx")));
    }

    public static IEnumerable<object[]> MixedCases =>
        from format in new[] { "structural", "legacy", "compact", "cdx" }
        from key in new[] { false, true }
        from direct in new[] { false, true }
        select new object[] { format, key, direct };

    [Theory]
    [MemberData(nameof(MixedCases))]
    public void MixedIndexes_DeleteRecallPreservesIndependentKeys(string format, bool key, bool direct)
        => Assert.Equal(direct ? (key ? ".F.|.T.|.T.|.F." : ".F.|.F.|.T.|.F.") : ".T.|.F.|.T.|.F.",
            MicroVfpXbaseUpdateTests.Run("", Functions + MixedScenario(format, key, direct)));

    internal static string MixedScenario(string format, bool key, bool direct)
    {
        string definition = key ? "IIF(" + (direct ? "NOT DELETED()" : "livefn()") + ",1,2)"
            : "id";
        string suffix = format switch
        {
            "structural" => "TAG b",
            "cdx" => "TAG b OF extra.cdx",
            _ => "TO extra.idx" + (format == "compact" ? " COMPACT" : "")
        };
        string open = format switch
        {
            "structural" => "USE t ORDER b",
            "cdx" => "USE t INDEX extra.cdx ORDER b",
            _ => "USE t INDEX extra.idx"
        };
        return "SET TABLEPROMPT OFF\nSET DELETED OFF\nCREATE TABLE t FREE (id I)\nINSERT INTO t VALUES (1)\n"
            + "INDEX ON id TAG a FOR NOT DELETED()\n"
            + (format == "cdx" ? "INDEX ON id TAG a OF extra.cdx FOR NOT DELETED()\n" : "")
            + "INDEX ON " + definition + " " + suffix
            + (key ? "" : " FOR " + (direct ? "NOT DELETED()" : "livefn()"))
            + "\nGO 1\nDELETE\nUSE\n" + open
            + "\nresult=TRANSFORM(SEEK(1))+'|'+TRANSFORM(SEEK(2))\nGO 1\nRECALL\nUSE\n" + open
            + "\nresult=result+'|'+TRANSFORM(SEEK(1))+'|'+TRANSFORM(SEEK(2))\nUSE";
    }

    internal const string Functions = "FUNCTION livefn\nRETURN NOT DELETED()\nENDFUNC\n";
    internal static string Scenario(string function, bool key)
    {
        string condition = function switch
        {
            "DELETED" => "NOT DELETED()",
            "EVALUATE" => "EVALUATE('NOT DELETED()')",
            "EVAL" => "EVAL('NOT DELETED()')",
            "RAND" => "RAND()>=0 AND livefn()",
            "DATE" => "NOT EMPTY(DATE()) AND livefn()",
            "DATETIME" => "NOT EMPTY(DATETIME()) AND livefn()",
            "TIME" => "LEN(TIME())>0 AND livefn()",
            "SECONDS" => "SECONDS()>=0 AND livefn()",
            "SYS" => "LEN(SYS(2015))>0 AND livefn()",
            _ => throw new ArgumentOutOfRangeException(nameof(function))
        };
        return $"""
            SET TABLEPROMPT OFF
            SET DELETED OFF
            CREATE TABLE t FREE (id I)
            INSERT INTO t VALUES (1)
            INDEX ON {(key ? "IIF(" + condition + ",1,2) TAG kk" : "id TAG kk FOR " + condition)}
            GO 1
            DELETE
            USE
            USE t ORDER kk
            result=TRANSFORM(SEEK(1))+'|'+TRANSFORM(SEEK(2))
            GO 1
            RECALL
            USE
            USE t ORDER kk
            result=result+'|'+TRANSFORM(SEEK(1))+'|'+TRANSFORM(SEEK(2))
            USE
            """;
    }
}
