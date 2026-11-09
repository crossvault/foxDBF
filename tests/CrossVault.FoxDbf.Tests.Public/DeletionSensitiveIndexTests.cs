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
