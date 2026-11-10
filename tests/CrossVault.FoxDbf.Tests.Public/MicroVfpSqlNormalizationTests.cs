using System;
using System.Collections.Generic;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlNormalizationTests
{
    internal const string Setup = """
        CREATE TABLE probe FREE (id I, n I, cc C(10))
        INSERT INTO probe VALUES (1,1,'')
        INSERT INTO probe VALUES (2,2,'Alpha')
        INSERT INTO probe VALUES (3,4,'Zulu')
        """;

    internal const string Arrays = "DIMENSION arr[3]\narr[1]=1\narr[2]=2\narr[3]=3\n";

    public static IEnumerable<object[]> Cases()
    {
        foreach (string feature in new[] { "ISBLANK(cc)", "n IN (1,2)", "n BETWEEN 1 AND 2", "cc LIKE 'A%'" })
        foreach (string array in new[] { "ALEN(arr)=3", "arr[1]=1" })
        foreach (string command in new[] { "DELETE", "UPDATE", "SELECT" })
        {
            bool blank = feature.StartsWith("ISBLANK", StringComparison.Ordinal);
            bool like = feature.Contains("LIKE", StringComparison.Ordinal);
            string predicate = feature + " AND " + array;
            string sql = command switch
            {
                "DELETE" => "DELETE FROM probe WHERE " + predicate,
                "UPDATE" => "UPDATE probe SET n=ALEN(arr) WHERE " + predicate,
                _ => "SELECT id FROM probe WHERE " + predicate + " ORDER BY id INTO CURSOR q"
            };
            string expected = command switch
            {
                "SELECT" => blank ? "1|1;" : like ? "1|2;" : "2|1;2;",
                "DELETE" => blank ? "1|1,1,.T.;2,2,.F.;3,4,.F.;"
                    : like ? "1|1,1,.F.;2,2,.T.;3,4,.F.;" : "2|1,1,.T.;2,2,.T.;3,4,.F.;",
                _ => blank ? "1|1,3,.F.;2,2,.F.;3,4,.F.;"
                    : like ? "1|1,1,.F.;2,3,.F.;3,4,.F.;" : "2|1,3,.F.;2,3,.F.;3,4,.F.;"
            };
            yield return new object[] { sql, expected };
        }

        yield return new object[] { "UPDATE probe SET n=IIF(ISBLANK(cc),ALEN(arr),0)", "3|1,3,.F.;2,0,.F.;3,0,.F.;" };
        yield return new object[] { "UPDATE probe SET n=IIF(ISBLANK(cc),arr[1],0)", "3|1,1,.F.;2,0,.F.;3,0,.F.;" };
        yield return new object[] { "UPDATE probe SET n=ALEN(arr) WHERE n BETWEEN 1 AND 2", "2|1,3,.F.;2,3,.F.;3,4,.F.;" };
        yield return new object[] { "UPDATE probe SET n=IIF(ISBLANK(cc),LOOKUP(n,2,id,'idtag'),0)", "err:11" };
        yield return new object[] { "DELETE FROM probe WHERE n IN (1,2) AND LOOKUP(n,2,id,'idtag')=2", "err:11" };
        yield return new object[] { "SELECT LOOKUP(n,2,id,'idtag') AS id FROM probe INTO CURSOR q", "err:11" };
        yield return new object[] { "SELECT IIF(.F.,LOOKUP(n,2,id,'idtag'),id) AS id FROM probe ORDER BY id INTO CURSOR q", "3|1;2;3;" };
        yield return new object[] { "SELECT LOOKUP(n,2,id,'idtag') AS id FROM probe WHERE id=99 INTO CURSOR q", "err:11" };
        yield return new object[] { "SELECT ALEN(arr)+arr[1] AS id FROM probe WHERE ISBLANK(cc) INTO CURSOR q", "1|4;" };
        yield return new object[] { "SELECT p.id FROM probe p JOIN probe s ON p.id=s.id AND s.n IN (1,2) AND ALEN(arr)=3 AND arr[1]=1 ORDER BY p.id INTO CURSOR q", "2|1;2;" };
        yield return new object[] { "SELECT id FROM probe GROUP BY id HAVING id IN (1,2) AND ALEN(arr)=3 AND arr[1]=1 ORDER BY id INTO CURSOR q", "2|1;2;" };
        yield return new object[] { "SELECT id FROM probe WHERE id IN (SELECT id FROM probe WHERE n BETWEEN 1 AND 2 AND ALEN(arr)=3 AND arr[1]=1) ORDER BY id INTO CURSOR q", "2|1;2;" };
        yield return new object[] { "SELECT id FROM probe WHERE n IN (1,2) AND ALEN(arr)=0 INTO CURSOR q", "0|" };
        yield return new object[] { "SELECT id FROM probe WHERE n IN (1,2) AND [ALEN(arr)]='ALEN(arr)' ORDER BY id INTO CURSOR q", "2|1;2;" };
        yield return new object[] { "SELECT id FROM probe WHERE n IN (1,2) AND [ACOPY(arr,dest)]='ACOPY(arr,dest)' ORDER BY id INTO CURSOR q", "2|1;2;" };
        yield return new object[] { "SELECT id FROM probe WHERE n IN (1,2) AND [LOOKUP(n,2,id)]='LOOKUP(n,2,id)' ORDER BY id INTO CURSOR q", "2|1;2;" };
    }

    internal static string Scenario(string sql)
    {
        bool select = sql.StartsWith("SELECT", StringComparison.Ordinal);
        return "SET DELETED OFF\n" + Arrays + "INDEX ON id TAG idtag\nSET ORDER TO 0\nTRY\n" + sql + "\nv=TRANSFORM(_TALLY)+'|'\n" + (select ? "" : "SELECT probe\n")
            + "GO TOP\nSCAN\nv=v+TRANSFORM(id)"
            + (select ? "" : "+','+TRANSFORM(n)+','+TRANSFORM(DELETED())") + "+';'\nENDSCAN\nCATCH TO failure\nv='err:'+TRANSFORM(failure.ErrorNo)\nENDTRY";
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SqlExpressions_NormalizeReferences_MatchVfp9(string sql, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_normalize");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute(Setup + "\n" + Scenario(sql));
        Assert.Equal(expected, interpreter.Memory.Get("v").AsString);
    }
}
