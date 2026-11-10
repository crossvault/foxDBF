using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class ArityReviewSqlBoundaryTests
{
    internal const string LookupSetup = "CREATE TABLE probe FREE (id I, n I)\nINSERT INTO probe VALUES (1,10)\nINDEX ON id TAG idtag\nSET ORDER TO 0\n";

    public static IEnumerable<object[]> LookupCases()
    {
        yield return ["SELECT list", "SELECT LOOKUP(n,1,id,'idtag') AS val FROM probe INTO CURSOR q", "err:11"];
        yield return ["SELECT WHERE", "SELECT id FROM probe WHERE LOOKUP(n,1,id,'idtag')=10 INTO CURSOR q", "err:11"];
        yield return ["HAVING", "SELECT id FROM probe GROUP BY id,n HAVING LOOKUP(n,1,id,'idtag')=10 INTO CURSOR q", "err:11"];
        yield return ["JOIN ON", "SELECT p.id FROM probe p JOIN probe s ON p.id=s.id AND LOOKUP(p.n,1,p.id,'idtag')=10 INTO CURSOR q", "err:11"];
        yield return ["UPDATE SET", "UPDATE probe SET n=LOOKUP(n,1,id,'idtag')", "err:11"];
        yield return ["UPDATE WHERE", "UPDATE probe SET n=20 WHERE LOOKUP(n,1,id,'idtag')=10", "err:11"];
        yield return ["DELETE WHERE", "DELETE FROM probe WHERE LOOKUP(n,1,id,'idtag')=10", "err:11"];
        yield return ["INSERT VALUES", "INSERT INTO probe VALUES (2,LOOKUP(n,1,id,'idtag'))\nGO BOTTOM\nv=TRANSFORM(n)", "10"];
        yield return ["INSERT SELECT", "INSERT INTO probe SELECT 2,LOOKUP(n,1,id,'idtag') FROM probe", "err:11"];
        yield return ["CREATE CODEPAGE", "REPLACE n WITH 1252\nCREATE TABLE target CODEPAGE = LOOKUP(n,1,id,'idtag') (id I)\nv=TRANSFORM(CPDBF())", "1252"];
    }

    public static IEnumerable<object[]> NameCases()
    {
        foreach (string name in new[] { "alen", "acopy", "aerror", "isblank", "lookup", "ataginfo", "aerr", "look" })
        {
            yield return [name, "INSERT", $"INSERT INTO {name} (id,n) VALUES (2,ALEN(arr)+arr[1])\nGO BOTTOM\nv=TRANSFORM(id)+','+TRANSFORM(n)", "2,4"];
            yield return [name, "UPDATE", $"UPDATE {name} SET n=ALEN(arr)+arr[1] WHERE id=1\nGO TOP\nv=TRANSFORM(n)", "4"];
            yield return [name, "DELETE", $"DELETE FROM {name} WHERE id=arr[1]\nGO TOP\nv=TRANSFORM(DELETED())", ".T."];
            yield return [name, "SELECT FROM/JOIN", $"SELECT {name}.id FROM {name} {name} JOIN {name} peer ON {name}.id=peer.id AND ALEN(arr)=3 INTO CURSOR q\nGO TOP\nv=TRANSFORM(id)", "1"];
            yield return [name, "INTO CURSOR", $"COPY TO input\nUSE input\nSELECT id FROM input WHERE id=arr[1] INTO CURSOR {name}\nGO TOP\nv=TRANSFORM(id)", "1"];
        }
    }

    internal static string NameSetup(string name) => $"CREATE TABLE {name} (id I, n I)\nINSERT INTO {name} VALUES (1,10)\n";

    internal static string Scenario(string code) => "SET DELETED OFF\nDIMENSION arr[3]\narr[1]=1\nv='ok'\nTRY\n" + code
        + "\nCATCH TO failure\nv='err:'+TRANSFORM(failure.ErrorNo)\nENDTRY";

    [Theory]
    [MemberData(nameof(LookupCases))]
    public void Lookup_ClauseBoundary_MatchesVfp9(string clause, string code, string expected)
        => Run(LookupSetup, code, expected, clause);

    [Theory]
    [MemberData(nameof(NameCases))]
    public void ReferenceFunctionNames_InSqlStructure_MatchVfp9(string name, string clause, string code, string expected)
        => Run(NameSetup(name), code, expected, name + "/" + clause);

    private static void Run(string setup, string code, string expected, string label)
    {
        using var dir = new MicroVfpTestSupport.TempDir("sql_boundary");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute(setup + Scenario(code));
        string actual = interpreter.Memory.Get("v").AsString;
        Assert.True(expected == actual, label + ": expected=" + expected + "; ours=" + actual);
    }
}
