using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpXbaseUpdateTests
{
    // Snapshots from the real VFP9 batch, including pointers and partial failures.
    internal static readonly Dictionary<string, string> Expected = new()
    {
        ["plain"] = "T|3|4|.F.|5|.T.\n1,10,A;2,421,421;2,21,C;4,440,440;",
        ["indexed"] = "T|3|4|.F.|5|.T.\n1,10,A;2,421,421;2,21,C;4,440,440;",
        ["random"] = "T|3|4|.F.|5|.T.\n1,10,A;2,421,421;2,21,C;4,440,440;",
        ["random_noindex"] = "ERR:26|T|77|3|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["unsorted"] = "T|1|4|.F.|5|.T.\n1,10,A;2,20,B;2,21,C;4,240,240;",
        ["random_unsorted"] = "T|3|2|.F.|5|.T.\n1,10,A;2,621,621;2,21,C;4,240,240;",
        ["target_unsorted"] = "T|1|1|.F.|5|.T.\n4,410,410;2,20,B;2,21,C;1,40,D;",
        ["source_index"] = "T|1|4|.F.|5|.T.\n1,10,A;2,20,B;2,21,C;4,440,440;",
        ["wrongorder"] = "T|1|4|.F.|5|.T.\n1,10,A;2,20,B;2,21,C;4,440,440;",
        ["missing"] = "ERR:13|T|77|3|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["badfield"] = "ERR:12|T|77|3|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["badkey"] = "ERR:12|T|77|3|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["deleted"] = "T|3|4|.F.|5|.T.\n1,10,A;2,20,B;2,422,422;4,440,440;",
        ["filter"] = "T|1|4|.F.|5|.T.\n1,10,A;2,20,B;2,21,C;4,440,440;",
        ["buffer3"] = "T|3|4|.F.|5|.T.\n0\n1,10,A;2,421,421;2,21,C;4,440,440;",
        ["buffer5"] = "T|3|4|.F.|5|.T.\n2\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["empty_source"] = "T|0|1|.F.|1|.T.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["empty_target"] = "T|0|1|.T.|5|.T.\n",
        ["unmatched_last"] = "T|2|5|.T.|5|.T.\n1,10,A;2,421,421;2,21,C;4,40,D;",
        ["indexes"] = "T|3|4|.F.|5|.T.\n.T.|.F.|.T.\n1,10,A;2,421,421;2,21,C;4,440,440;",
        ["integer"] = "ERR:1145|T|77|3|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["key_expression"] = "ERR:36|T|77|3|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["field1"] = "ERR:1582|T|1|2|.F.|2|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_field1"] = "ERR:1582|T|1|2|.F.|2|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["field3"] = "ERR:1582|T|1|2|.F.|2|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_field3"] = "ERR:1582|T|1|2|.F.|2|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["field5"] = "ERR:1582|T|1|2|.F.|2|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_field5"] = "ERR:1582|T|1|2|.F.|2|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["record1"] = "ERR:1583|T|2|2|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_record1"] = "ERR:1583|T|2|2|.F.|4|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["record3"] = "ERR:1583|T|2|2|.F.|3|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_record3"] = "ERR:1583|T|2|2|.F.|4|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["record5"] = "ERR:1583|T|2|2|.F.|3|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_record5"] = "ERR:1583|T|2|2|.F.|4|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["trigger1"] = "ERR:1539|T|2|2|.F.|3|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_trigger1"] = "ERR:1539|T|2|2|.F.|4|.F.\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["trigger3"] = "ERR:1539|T|2|2|.F.|3|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_trigger3"] = "ERR:1539|T|2|2|.F.|4|.F.\n1\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["trigger5"] = "T|3|4|.F.|5|.T.\n2\n1,10,A;2,20,B;2,21,C;4,40,D;",
        ["random_trigger5"] = "T|3|4|.F.|5|.T.\n2\n1,10,A;2,20,B;2,21,C;4,40,D;",
    };

    public static IEnumerable<object[]> Cases => Expected.Select(pair => new object[] { pair.Key, pair.Value });

    internal static readonly Dictionary<string, string> ExtraExpected = new()
    {
        ["F(6)"] = Expected["plain"],
        ["B"] = Expected["integer"],
        ["Y"] = Expected["integer"],
        ["rhs"] = Expected["badfield"],
        ["qualified_key"] = Expected["plain"],
        ["source_integer"] = Expected["badfield"].Replace("ERR:12", "ERR:9"),
        ["candidate"] = "ERR:1884|T|3|4|.F.|5|.T.\n1,10,A;2,20,SAME;2,21,C;4,40,D;",
        ["random_unmatched"] = "T|2|2|.F.|5|.T.\n1,10,A;2,421,421;2,21,C;4,40,D;",
        ["random_empty_source"] = Expected["empty_source"].Replace("T|0|1|", "T|0|3|")
    };

    public static IEnumerable<object[]> ExtraCases => ExtraExpected.Select(pair => new object[] { pair.Key, pair.Value });

    [Theory]
    [MemberData(nameof(ExtraCases))]
    public void UpdateOn_ValidatesBeforeMovingAndKeepsRecordAtomicity(string kind, string expected)
    {
        var (setup, scenario) = ExtraScenario(kind);
        Assert.Equal(expected, Run(setup, scenario));
    }

    internal static (string Setup, string Scenario) ExtraScenario(string kind)
    {
        string setup = Setup;
        string scenario = Scenario("indexed");
        if (kind is "F(6)" or "B" or "Y")
            return (setup.Replace("id N(6)", "id " + kind), scenario.Replace("TRANSFORM(id)", "ALLTRIM(STR(id))"));
        if (kind == "rhs") scenario = scenario.Replace("n+src.n", "src.absent");
        if (kind == "qualified_key") scenario = scenario.Replace("UPDATE ON id ", "UPDATE ON t.id ");
        if (kind == "source_integer") setup = setup.Replace("CREATE TABLE src (id N(6)", "CREATE TABLE src (id I");
        if (kind == "candidate") scenario = scenario.Replace("INDEX ON id TAG id", "INDEX ON k TAG k CANDIDATE\nINDEX ON id TAG id")
            .Replace("n WITH n+src.n, k WITH TRANSFORM(n)", "k WITH 'SAME'");
        if (kind.StartsWith("random_")) scenario = Scenario(kind == "random_unmatched" ? "unmatched_last" : "empty_source")
            .Replace("WITH TRANSFORM(n)", "WITH TRANSFORM(n) RANDOM");
        return (setup, scenario);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void UpdateOn_MatchesVfp9Snapshot(string kind, string expected)
    {
        bool random = kind.StartsWith("random_") && (kind.EndsWith('1') || kind.EndsWith('3') || kind.EndsWith('5'));
        if (random) kind = kind[7..];
        string setup = kind.StartsWith("field") ? Setup.Replace("n I,", "n I CHECK n<250,")
            : kind.StartsWith("record") ? Setup.Replace("k C(10))", "k C(10), CHECK n<250)") : Setup;
        if (kind == "integer") setup = setup.Replace("id N(6)", "id I");
        string scenario = Scenario(kind == "integer" ? "indexed" : kind == "key_expression" ? "plain" : kind);
        if (kind == "key_expression") scenario = scenario.Replace("UPDATE ON id ", "UPDATE ON id+0 ");
        if (random) scenario = scenario.Replace("WITH TRANSFORM(n)", "WITH TRANSFORM(n) RANDOM");
        string procedure = kind.StartsWith("trigger") ? "PROCEDURE __RI_UPDATE_t\nRETURN n<250\nENDPROC\n" : "";
        Assert.Equal(expected, Run(setup, procedure + scenario, kind.StartsWith("field") || kind.StartsWith("record") || kind.StartsWith("trigger")));
    }

    internal const string Setup = """
        CREATE TABLE t (id N(6), n I, k C(10))
        INSERT INTO t VALUES (1,10,'A')
        INSERT INTO t VALUES (2,20,'B')
        INSERT INTO t VALUES (2,21,'C')
        INSERT INTO t VALUES (4,40,'D')
        CREATE TABLE src (id N(6), n I)
        INSERT INTO src VALUES (2,200)
        INSERT INTO src VALUES (2,201)
        INSERT INTO src VALUES (3,300)
        INSERT INTO src VALUES (4,400)
        """;

    internal static string Scenario(string kind)
    {
        string prefix = "SET TABLEPROMPT OFF\nSET DELETED OFF\nSET MULTILOCKS ON\nUSE src EXCLUSIVE IN 2\nSELECT src\n";
        if (kind == "source_index") prefix += "INDEX ON id TAG id DESCENDING\n";
        if (kind is "unsorted" or "random_unsorted") prefix += "GO 1\nREPLACE id WITH 4\nGO 4\nREPLACE id WITH 2\n";
        prefix += "GO 3\nUSE t EXCLUSIVE IN 1\nSELECT t\n";
        if (kind is not "plain" and not "unsorted" and not "random_noindex") prefix += "INDEX ON id TAG id\n";
        if (kind == "wrongorder") prefix += "INDEX ON k TAG k DESCENDING\n";
        if (kind == "target_unsorted") prefix += "SET ORDER TO 0\nGO 1\nREPLACE id WITH 4\nGO 4\nREPLACE id WITH 1\n";
        if (kind == "indexes") prefix += "INDEX ON k TAG k\nINDEX ON k TO extra COMPACT\nSET ORDER TO TAG id\n";
        if (kind == "empty_source") prefix += "SELECT src\nZAP\nSELECT t\n";
        if (kind == "empty_target") prefix += "ZAP\n";
        if (kind == "unmatched_last") prefix += "SELECT src\nGO 4\nREPLACE id WITH 9\nSELECT t\n";
        if (kind == "deleted") prefix += "GO 2\nDELETE\nSET DELETED ON\n";
        if (kind == "filter") prefix += "SET FILTER TO id<>2\n";
        int buffering = kind.EndsWith('3') ? 3 : kind.EndsWith('5') ? 5 : 1;
        if (buffering > 1) prefix += $"=CURSORSETPROP('Buffering',{buffering})\n";
        prefix += (kind == "empty_target" ? "" : "GO 3\n") + "_TALLY=77\n";
        string command = "UPDATE ON " + (kind == "badkey" ? "absent" : "id") + " FROM " + (kind == "missing" ? "absent" : "src") + " REPLACE " +
            (kind == "badfield" ? "absent WITH 1" : "n WITH n+src.n, k WITH TRANSFORM(n)") + (kind.StartsWith("random") ? " RANDOM" : "");
        return prefix + MicroVfpSqlDmlTests.Catch(command) + "\n" + """
            result=result+ALIAS()+'|'+TRANSFORM(_TALLY)+'|'+TRANSFORM(RECNO('t'))+'|'+TRANSFORM(EOF('t'))+'|'+TRANSFORM(RECNO('src'))+'|'+TRANSFORM(EOF('src'))+CHR(10)
            """ + "\n" + (buffering > 1 ? "result=result+TRANSFORM(TABLEREVERT(.T.))+CHR(10)\n" : "") +
            (kind == "indexes" ? "USE\nUSE t INDEX extra.idx\nSET ORDER TO TAG k\nresult=result+TRANSFORM(SEEK('421'))+'|'+TRANSFORM(SEEK('B'))+'|'\nSET ORDER TO 3\nresult=result+TRANSFORM(SEEK('440'))+CHR(10)\n" : "") + """
            FOR row=1 TO RECCOUNT()
                GO row
                result=result+TRANSFORM(id)+','+TRANSFORM(n)+','+ALLTRIM(k)+';'
            ENDFOR
            """;
    }

    internal static string Run(string setup, string scenario, bool enforce = false)
    {
        using var dir = new MicroVfpTestSupport.TempDir("xbase_update");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(setup + "\nCLOSE TABLES ALL");
        vfp.EnforceReferentialIntegrity = enforce;
        vfp.Execute("PUBLIC result\nresult=''\n" + scenario);
        return vfp.Memory.Get("result").AsString.Replace("\r", "");
    }
}
