namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNumericScaleFinal5Tests
{
    internal static IEnumerable<(string Name, string Script)> Probes()
    {
        const string table = "CREATE TABLE t FREE (n N(3), tail I)\n";
        foreach (var (name, setup, command) in new[]
        {
            ("insert", "", "INSERT INTO t VALUES (12345,7)"),
            ("update", "INSERT INTO t VALUES (1,2)\n", "UPDATE t SET n=12345,tail=7"),
            ("replace", "APPEND BLANK\n", "REPLACE n WITH 12345,tail WITH 7"),
            ("gather", "APPEND BLANK\nm.n=12345\nm.tail=7\n", "GATHER MEMVAR"),
            ("array", "DIMENSION aa[2]\naa[1]=12345\naa[2]=7\n", "INSERT INTO t FROM ARRAY aa"),
            ("memvar", "m.n=12345\nm.tail=7\n", "INSERT INTO t FROM MEMVAR"),
            ("select", "CREATE TABLE src FREE (n I,tail I)\nINSERT INTO src VALUES (12345,7)\n", "INSERT INTO t SELECT * FROM src"),
            ("buffer", "SET MULTILOCKS ON\nINSERT INTO t VALUES (1,2)\n=CURSORSETPROP('Buffering',5,'t')\n", "REPLACE n WITH 12345,tail WITH 7\nresult=TRANSFORM(TABLEUPDATE(.T.))")
        })
            yield return (name, table + setup + "result='OK'\nTRY\n" + command + "\nCATCH TO failure\nresult='ERR:'+TRANSFORM(failure.ErrorNo)\nENDTRY\nSELECT t\nresult=result+'|'+TRANSFORM(RECCOUNT())+'|'+TRANSFORM(tail)\nUSE\nresult=result+'|'+SUBSTR(FILETOSTR('t.dbf'),362,3)");
        foreach (var (definition, number) in new[] { ("N(4,2)", "9.996"), ("N(4,1)", "99.96"), ("N(3,2)", "0.5"), ("N(4,2)", "-0.123"), ("N(4,2)", "999"), ("N(4,2)", "999.49"), ("N(4,2)", "999.5") })
            yield return ("store_" + definition + "_" + number, $"CREATE TABLE t FREE (n {definition})\nAPPEND BLANK\nREPLACE n WITH {number}\nUSE\nresult=SUBSTR(FILETOSTR('t.dbf'),330,4)");
        foreach (string into in new[] { "CURSOR", "TABLE" })
            foreach (string value in new[] { "123.499", "12345", "99.96" })
                yield return ("computed_" + into + value, $"CREATE TABLE t FREE (id I)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (2)\nSELECT IIF(id=1,1.00,{value}) AS x FROM t INTO {into} zz\n{(into == "TABLE" ? "USE zz" : "SELECT zz")}\nGO BOTTOM\nresult=STR(x,20,4)");
        foreach (string value in new[] { "0.00015", "1.00005", "2.00025", "-0.00015" })
            yield return ("currency_" + value, $"CREATE TABLE t FREE (y Y)\nAPPEND BLANK\nREPLACE y WITH {value}\nresult=STR(y,25,4)\nUSE\nresult=result+'|'+STRCONV(SUBSTR(FILETOSTR('t.dbf'),330,8),15)");
        yield return ("currency_display", "SET DECIMALS TO 1\nresult=STR($1234567890123.4499,25,1)+'|'+TRANSFORM($1234567890123.4499)");
        foreach (string fixedMode in new[] { "OFF", "ON" })
            foreach (string expr in new[] { "n", "t.n", "i", "b", "y", "f", "m.v", "n+0", "(n)", "'a'", "'a','b'", "'a',1,'b'", "1,'a',2", ".T.,'b'" })
                yield return ($"print_{fixedMode}_{expr}", $"CREATE TABLE t FREE (n N(6,2),i I,b B(2),y Y,f F(6,2))\nINSERT INTO t VALUES (1.5,1,1.5,$1.5,1.5)\nv=1.5\nSET FIXED {fixedMode}\nSET DECIMALS TO 4\nSET ALTERNATE TO p.txt\nSET ALTERNATE ON\n? {expr}\n?? {expr}\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('p.txt')");
        foreach (string command in new[] { "? 'a'", "? 'ab'", "? 'abc'", "? 'a','b'", "?? 'a'", "? 'a'\n?? 'b'", "? 1", "? 12", "? .T.", "? 'a',1", "? 1,'a'", "? 'a'\n? 'b'" })
            yield return ("single_" + command.Replace("\n", ";"), "SET ALTERNATE TO p.txt\nSET ALTERNATE ON\n" + command + "\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('p.txt')");
        yield return ("additive", "SET ALTERNATE TO p.txt\nSET ALTERNATE ON\n? 'first'\nSET ALTERNATE TO\nSET ALTERNATE TO p.txt ADDITIVE\nSET ALTERNATE ON\n? 'second'\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('p.txt')");
        yield return ("buffer_flush", "SET MULTILOCKS ON\nCREATE TABLE t FREE (n N(3))\nINSERT INTO t VALUES (1)\n=CURSORSETPROP('Buffering',5,'t')\nresult='OK'\nTRY\nREPLACE n WITH 12345\nCATCH TO failure\nresult='ERR:'+TRANSFORM(failure.ErrorNo)\nENDTRY\nresult=result+'|'+TRANSFORM(TABLEUPDATE(.T.))");
        yield return ("double_currency", "CREATE TABLE t FREE (b B,y Y)\nINSERT INTO t VALUES (0.00015,$0)\nREPLACE y WITH b\nresult=STR(y,25,4)\nREPLACE b WITH 1.00005\nREPLACE y WITH b\nresult=result+'|'+STR(y,25,4)\nREPLACE b WITH 2.00025\nREPLACE y WITH b\nresult=result+'|'+STR(y,25,4)");
        yield return ("having_count", "CREATE TABLE t FREE (n I)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (3)\nSELECT n,COUNT(*) AS c FROM t GROUP BY n HAVING CNT(*)>1 INTO CURSOR zz\nresult=TRANSFORM(RECCOUNT())+'|'+TRANSFORM(zz.c)");
        foreach (string having in new[] { "COUNT(*)>0", "COUNT(*)=0", "COUNT(*)=1", "COUNT(*)=3", "SUM(n)>1", "1=1", "c>1" })
            yield return ("bare_count_"+having, "CREATE TABLE t FREE (n I)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (3)\nSELECT COUNT(*) AS c FROM t HAVING " + having + " INTO CURSOR zz\nresult=TRANSFORM(RECCOUNT())+'|'+TRANSFORM(zz.c)");
        foreach (string query in new[] { "SELECT CNT(*) AS c FROM t HAVING CNT(*)>1", "SELECT n,COUNT(*) AS c FROM t GROUP BY n ORDER BY CNT(*)", "SELECT COUNT(DISTINCT n) AS c FROM t", "SELECT SUM(DISTINCT n) AS c FROM t", "SELECT AVG(DISTINCT n) AS c FROM t", "SELECT MAX(DISTINCT n) AS c FROM t", "SELECT MIN(DISTINCT n) AS c FROM t" })
            yield return (query, "CREATE TABLE t FREE (n I NULL)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (1)\nINSERT INTO t VALUES (3)\nINSERT INTO t VALUES (.NULL.)\n" + query + " INTO CURSOR zz\nresult=TRANSFORM(c)");
    }

    internal static readonly Dictionary<string, string> Expected = new()
    {
        ["bare_count_COUNT(*)>0"] = "0|0",
        ["bare_count_COUNT(*)=0"] = "1|3",
        ["bare_count_COUNT(*)=1"] = "0|0",
        ["bare_count_COUNT(*)=3"] = "0|0",
        ["bare_count_SUM(n)>1"] = "0|0",
        ["bare_count_1=1"] = "1|3",
        ["bare_count_c>1"] = "1|3",

        ["insert"] = "OK|1|7|***",
        ["update"] = "OK|1|7|***",
        ["replace"] = "ERR:39|1|7|***",
        ["gather"] = "ERR:39|1|7|***",
        ["array"] = "ERR:39|1|7|***",
        ["memvar"] = "OK|1|7|***",
        ["select"] = "OK|1|7|***",
        ["buffer"] = "ERR:39|1|7|  1",
        ["store_N(4,2)_9.996"] = "10.0",
        ["store_N(4,1)_99.96"] = " 100",
        ["store_N(3,2)_0.5"] = ".50\u001a",
        ["store_N(4,2)_-0.123"] = "-.12",
        ["store_N(4,2)_999"] = " 999",
        ["store_N(4,2)_999.49"] = " 999",
        ["store_N(4,2)_999.5"] = "1000",
        ["computed_CURSOR123.499"] = "            123.0000",
        ["computed_CURSOR12345"] = "********************",
        ["computed_CURSOR99.96"] = "            100.0000",
        ["computed_TABLE123.499"] = "            123.0000",
        ["computed_TABLE12345"] = "********************",
        ["computed_TABLE99.96"] = "            100.0000",
        ["currency_0.00015"] = "                   0.0002|0200000000000000",
        ["currency_1.00005"] = "                   1.0001|1127000000000000",
        ["currency_2.00025"] = "                   2.0003|234E000000000000",
        ["currency_-0.00015"] = "                  -0.0002|FEFFFFFFFFFFFFFF",
        ["currency_display"] = "          1234567890123.4|$1,234,567,890,123.4",
        ["print_OFF_n"] = "\r\n  1.50  1.50\u001a",
        ["print_OFF_t.n"] = "\r\n  1.50  1.50\u001a",
        ["print_OFF_i"] = "\r\n          1          1\u001a",
        ["print_OFF_b"] = "\r\n                 1.50                 1.50\u001a",
        ["print_OFF_y"] = "\r\n               1.5000               1.5000\u001a",
        ["print_OFF_f"] = "\r\n  1.50  1.50\u001a",
        ["print_OFF_m.v"] = "\r\n         1.5         1.5\u001a",
        ["print_OFF_n+0"] = "\r\n   1.50   1.50\u001a",
        ["print_OFF_(n)"] = "\r\n  1.50  1.50\u001a",
        ["print_OFF_'a'"] = "\r\naa\u001a",
        ["print_OFF_'a','b'"] = "\r\na ba b\u001a",
        ["print_OFF_'a',1,'b'"] = "\r\na 1 ba 1 b\u001a",
        ["print_OFF_1,'a',2"] = "\r\n1 a 21 a 2\u001a",
        ["print_OFF_.T.,'b'"] = "\r\n.T. b.T. b\u001a",
        ["print_ON_n"] = "\r\n  1.5000  1.5000\u001a",
        ["print_ON_t.n"] = "\r\n  1.5000  1.5000\u001a",
        ["print_ON_i"] = "\r\n          1.0000          1.0000\u001a",
        ["print_ON_b"] = "\r\n                 1.5000                 1.5000\u001a",
        ["print_ON_y"] = "\r\n               1.5000               1.5000\u001a",
        ["print_ON_f"] = "\r\n  1.5000  1.5000\u001a",
        ["print_ON_m.v"] = "\r\n         1.5000         1.5000\u001a",
        ["print_ON_n+0"] = "\r\n   1.5000   1.5000\u001a",
        ["print_ON_(n)"] = "\r\n  1.5000  1.5000\u001a",
        ["print_ON_'a'"] = "\r\naa\u001a",
        ["print_ON_'a','b'"] = "\r\na ba b\u001a",
        ["print_ON_'a',1,'b'"] = "\r\na 1.0000 ba 1.0000 b\u001a",
        ["print_ON_1,'a',2"] = "\r\n1.0000 a 2.00001.0000 a 2.0000\u001a",
        ["print_ON_.T.,'b'"] = "\r\n.T. b.T. b\u001a",
        ["single_? 'a'"] = "\r\na\u001a",
        ["single_? 'ab'"] = "\r\nab\u001a",
        ["single_? 'abc'"] = "\r\nabc\u001a",
        ["single_? 'a','b'"] = "\r\na b\u001a",
        ["single_?? 'a'"] = "a\u001a",
        ["single_? 'a';?? 'b'"] = "\r\nab\u001a",
        ["single_? 1"] = "\r\n1\u001a",
        ["single_? 12"] = "\r\n12\u001a",
        ["single_? .T."] = "\r\n.T.\u001a",
        ["single_? 'a',1"] = "\r\na 1\u001a",
        ["single_? 1,'a'"] = "\r\n1 a\u001a",
        ["single_? 'a';? 'b'"] = "\r\na\r\nb\u001a",
        ["additive"] = "\r\nfirst\r\nsecond\u001a",
        ["buffer_flush"] = "ERR:39|.T.",
        ["double_currency"] = "                   0.0002|                   1.0001|                   2.0003",
        ["having_count"] = "1|2",
        ["SELECT CNT(*) AS c FROM t HAVING CNT(*)>1"] = "0",
        ["SELECT n,COUNT(*) AS c FROM t GROUP BY n ORDER BY CNT(*)"] = "ERR:1300",
        ["SELECT COUNT(DISTINCT n) AS c FROM t"] = "2",
        ["SELECT SUM(DISTINCT n) AS c FROM t"] = "4",
        ["SELECT AVG(DISTINCT n) AS c FROM t"] = "2",
        ["SELECT MAX(DISTINCT n) AS c FROM t"] = "ERR:1819",
        ["SELECT MIN(DISTINCT n) AS c FROM t"] = "ERR:1819",
    };
    public static IEnumerable<object[]> Cases => Probes().Select(c => new object[] { c.Name, c.Script, Expected[c.Name] });

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesVfp9(string name, string script, string expected)
    {
        string actual = MicroVfpNumericScaleReviewSupport.Run(script);
        Assert.True(expected == actual, $"{name}: expected [{expected}], actual [{actual}]");
    }
}
