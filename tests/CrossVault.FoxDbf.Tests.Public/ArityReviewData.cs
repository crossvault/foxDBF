using System.Collections.Generic;

namespace CrossVault.FoxDbf.Tests;

internal static class ArityReviewData
{
    internal const string Setup = """
        CREATE TABLE probe FREE (id N(5), cc C(10), n N(5), flag L)
        USE probe
        INSERT INTO probe VALUES (1,'abc',0,.F.)
        APPEND BLANK
        BLANK
        REPLACE id WITH 2
        USE
        CREATE TABLE src FREE (sid N(5), sc C(10))
        USE src
        INSERT INTO src VALUES (1,'abc')
        INSERT INTO src VALUES (2,'')
        USE
        USE probe IN 0
        USE src IN 0
        SELECT probe
        GO TOP
        """;

    internal static IEnumerable<(string Code, int Error)> Errors()
    {
        foreach (string declaration in new[] { "DIMENSION", "LOCAL ARRAY", "PUBLIC ARRAY", "PRIVATE", "PRIVATE ARRAY" })
            foreach (string name in new[] { "max", "pi", "len", "date", "uppe", "evaluate" })
                yield return ($"{declaration} {name}[2,2]", declaration == "PRIVATE ARRAY" ? 0 : 1652);
        foreach (var (name, indices, error) in new[] { ("max", "1", 1229), ("pi", "1", 1230), ("len", "1,2", 1230), ("date", "2", 1229) })
        {
            yield return ($"v={name}[{indices}]", error);
            yield return ($"v={name}({indices})", error);
        }
        foreach (string code in new[] {
            "DELETE FOR LEFT(cc) = 'A'", "RECALL WHILE SUBSTR(cc) = ''",
            "REPLACE cc WITH LEFT(cc) NEXT 1", "LOCATE FOR LEFT(cc)='A'",
            "SUM LEFT(cc) TO y", "COUNT FOR VAL()=1",
            "UPDATE ON id FROM probe REPLACE cc WITH LEFT(cc)",
            "SELECT * FROM probe WHERE MAX(id)>0 INTO CURSOR result" })
        {
            yield return (code, 1229);
            yield return ("USE probe EXCLUSIVE\nZAP\n" + code, 1229);
        }
        foreach (var (code, error) in new[] {
            ("DELETE FOR LEFT(cc,1,2)=''", 1230),
            ("RECALL WHILE VAL('1','2')=1", 1230),
            ("REPLACE cc WITH IIF(.T.,'A') NEXT 1", 1306) })
            yield return (code, error);
    }

    internal static IEnumerable<(string Code, string Expected)> Arrays()
    {
        foreach (string declaration in new[] { "DIMENSION", "LOCAL ARRAY", "PUBLIC ARRAY" })
        {
            string name = declaration.Split(' ')[0] + "Items";
            yield return ($"{declaration} {name}[3]\n{name}[2]=42\nv=TRANSFORM({name}[2])+','+TRANSFORM({name}(2))", "42,42");
        }
        foreach (string name in new[] { "cocreateguid", "strzero" })
            yield return ($"DIMENSION {name}[3]\n{name}[2]=42\nv=TRANSFORM({name}[2])+','+TRANSFORM({name}(2))", "42,42");
    }

    internal static IEnumerable<(string Code, string Expected)> IsBlank()
    {
        foreach (string field in new[] { "cc", "p.cc", "p->cc" })
            yield return ($"SELECT ISBLANK({field}) AS b FROM probe p INTO CURSOR result\nv=''\nSCAN\nv=v+TRANSFORM(b)\nENDSCAN", ".F..T.");
        yield return ("SELECT probe\nGO BOTTOM\nv=TRANSFORM(ISBLANK(n))+TRANSFORM(ISBLANK(flag))", ".T..T.");
        yield return ("SELECT ISBLANK(n) AS b, ISBLANK(flag) AS c FROM probe INTO CURSOR result\nv=''\nSCAN\nv=v+TRANSFORM(b)+TRANSFORM(c)\nENDSCAN", ".F..F..F..F.");
        foreach (string condition in new[] { "ISBLANK(cc)", "ISBLANK(probe.cc)", "ISBLANK(cc) AND id IN (2)", "ISBLANK(n)" })
            yield return ($"SELECT * FROM probe WHERE {condition} INTO CURSOR result\nv=TRANSFORM(RECCOUNT())", condition == "ISBLANK(n)" ? "0" : "1");
        yield return ("SELECT ISBLANK(p.cc) AS b, ISBLANK(s.sc) AS c FROM probe p JOIN src s ON p.id=s.sid INTO CURSOR result\nv=''\nSCAN\nv=v+TRANSFORM(b)+TRANSFORM(c)\nENDSCAN", ".F..F..T..T.");
        foreach (string suffix in new[] { "", " AND src.sid IN (1,2)" })
            yield return ($"DELETE probe FROM src WHERE ISBLANK(src.sc){suffix}\nv=TRANSFORM(_TALLY)", "2");
        yield return ("DELETE FROM probe WHERE ISBLANK(n)\nv=TRANSFORM(_TALLY)", "0");
        yield return ("UPDATE probe SET cc='blank' WHERE ISBLANK(cc) AND id IN (2)\nv=TRANSFORM(_TALLY)", "1");
        yield return ("UPDATE probe SET flag=ISBLANK(n)\nGO BOTTOM\nv=TRANSFORM(flag)", ".F.");
        yield return ("SELECT p.id FROM probe p JOIN src s ON p.id=s.sid WHERE ISBLANK(p.cc) INTO CURSOR result\nv=TRANSFORM(RECCOUNT())", "1");
    }
}
