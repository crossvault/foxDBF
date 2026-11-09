using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpXbaseUpdateReviewTests
{
    internal static readonly Dictionary<string, string> Expected = new()
    {
        ["date_key"] = "7|keep|2",
        ["datetime_key"] = "7|keep|2",
        ["memo_key"] = "ERR:1145|0|keep|77",
        ["varchar_key"] = "7|keep|2",
        ["varbinary_key"] = "7|keep|2",
        ["blank_varbinary_key"] = "7|keep|2",
        ["blank_varchar_key"] = "7|keep|2",
        ["blob_key"] = "ERR:1145|0|keep|77",
        ["general_key"] = "ERR:1145|0|keep|77",
        ["key_source_alias"] = "ERR:12|0|keep|77",
        ["field_source_alias"] = "ERR:12|0|keep|77",
        ["random_expr"] = "7|keep|2",
        ["fieldstate_changed"] = "2|2|2",
        ["buffering3"] = "0|3|2",
        ["buffering5"] = "0|5|2",
        ["precision3"] = "2|keep|2",
        ["precision5"] = "2|keep|2",
        ["precision"] = "2|keep|2",
        ["type5"] = "ERR:9|0|0|keep|0",
        ["type"] = "ERR:9|0|keep|0",
        ["type_varbinary"] = "ERR:9|0",
        ["type_blob"] = "ERR:9|0",
        ["type_general"] = "ERR:9|0",
        ["type_varbinary_text"] = "1",
        ["type_varbinary_binary"] = "1",
        ["type_blob_text"] = "1",
        ["type_blob_binary"] = "1",
        ["type_general_text"] = "ERR:9|0",
        ["type_general_binary"] = "ERR:9|0",
        ["varbinary_normalization"] = "4|Q|4",
        ["key_alias"] = "ERR:13|0|keep|77",
        ["field_alias"] = "ERR:13|0|keep|77",
        ["arrow"] = "7|keep|2",
        ["memvar_random"] = "7|keep|2",
        ["field_random"] = "7|keep|2",
        ["width"] = "ERR:9|0|keep|77",
        ["prefix_random"] = "ERR:9|0|keep|77",
        ["logical"] = "ERR:1145|0|keep|77",
        ["found"] = ".F.|7|keep|2",
        ["oldval"] = "5|5|2",
        ["general_scan"] = "0|keep|0",
        ["general_random"] = "7|keep|2",
        ["syntax"] = "ERR:36|0|keep|77",
        ["buffering"] = "0|1|2",
        ["fieldstate"] = "0|1|2",
        ["oldval_sequence"] = "2|0|2",
    };
    public static IEnumerable<object[]> Cases => Expected.Select(p => new object[] { p.Key, p.Value });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Review_UpdateOnMatchesVfp9(string kind, string expected)
    {
        var (setup, scenario) = Scenario(kind);
        Assert.Equal(expected, MicroVfpXbaseUpdateTests.Run(setup, scenario));
    }

    internal static (string Setup, string Scenario) Scenario(string kind)
    {
        if (kind == "varbinary_normalization")
            return ("CREATE TABLE t (id N(6), k Q(4), n N(2), typ C(1))\nINSERT INTO t (id) VALUES (1)\n"
                + "CREATE TABLE src (id N(6))\nINSERT INTO src VALUES (1)",
                "SET TABLEPROMPT OFF\nUSE src IN 2\nUSE t IN 1\nSELECT t\n"
                + "UPDATE ON id FROM src REPLACE k WITH 'abcde', n WITH LEN(k), typ WITH VARTYPE(k)\n"
                + "result=TRANSFORM(n)+'|'+typ+'|'+TRANSFORM(LEN(k))");
        if (kind.StartsWith("type_varbinary") || kind.StartsWith("type_blob") || kind.StartsWith("type_general"))
        {
            string type = kind.StartsWith("type_varbinary") ? "Q(4)" : kind.StartsWith("type_blob") ? "W" : "G";
            string value = kind.EndsWith("_text") ? "'xy'" : kind.EndsWith("_binary") ? "CREATEBINARY('xy')" : "123";
            return ($"CREATE TABLE t (id N(6), k {type})\nINSERT INTO t (id) VALUES (1)\n"
                + "CREATE TABLE src (id N(6))\nINSERT INTO src VALUES (1)",
                "SET TABLEPROMPT OFF\nUSE src IN 2\nUSE t IN 1\nSELECT t\n_TALLY=77\n"
                + MicroVfpSqlDmlTests.Catch("UPDATE ON id FROM src REPLACE k WITH " + value)
                + "\nresult=result+TRANSFORM(_TALLY)");
        }
        string setup = "CREATE TABLE t (id N(6), n N(6), k C(30))\nINSERT INTO t VALUES (1,0,'keep')\n"
            + "CREATE TABLE src (id N(6), n N(6))\nINSERT INTO src VALUES (1,7)\nINSERT INTO src VALUES (1,7)";
        string prefix = "SET TABLEPROMPT OFF\nSET MULTILOCKS ON\nUSE src IN 2\nUSE t IN 1\nSELECT t\nINDEX ON id TAG id\nGO 1\n";
        string command = "UPDATE ON id FROM src REPLACE n WITH 7";
        string after = "";
        switch (kind)
        {
            case "varbinary_key":
                setup = setup.Replace("id N(6)", "id Q(4)").Replace("(1,", "(CREATEBINARY('a'),");
                prefix = prefix.Replace("INDEX ON id TAG id\n", "");
                break;
            case "blank_varbinary_key":
            case "blank_varchar_key":
                setup = setup.Replace("id N(6)", kind == "blank_varbinary_key" ? "id Q(4)" : "id V(4)")
                    .Replace("INSERT INTO t VALUES (1,0,'keep')", "APPEND BLANK")
                    .Replace("INSERT INTO src VALUES (1,7)", "APPEND BLANK");
                prefix = prefix.Replace("INDEX ON id TAG id\n", "") + "REPLACE k WITH 'keep'\n";
                break;
            case "blob_key":
            case "general_key":
                setup = setup.Replace("id N(6)", "id " + (kind == "blob_key" ? "W" : "G"))
                    .Replace("INSERT INTO t VALUES (1,0,'keep')", "APPEND BLANK\nREPLACE k WITH 'keep'")
                    .Replace("INSERT INTO src VALUES (1,7)", "APPEND BLANK");
                prefix = prefix.Replace("INDEX ON id TAG id\n", "");
                break;
            case "date_key":
            case "datetime_key":
            case "memo_key":
            case "varchar_key":
                string type = kind == "date_key" ? "D" : kind == "datetime_key" ? "T" : kind == "memo_key" ? "M" : "V(4)";
                string key = kind == "date_key" ? "{^2024-01-01}" : kind == "datetime_key" ? "{^2024-01-01 12:00:00}" : "'abc'";
                setup = setup.Replace("id N(6)", "id " + type).Replace("(1,", "(" + key + ",");
                prefix = prefix.Replace("INDEX ON id TAG id\n", "");
                break;
            case "precision3":
            case "precision5": prefix += "=CURSORSETPROP('Buffering'," + kind[^1] + ")\n"; goto case "precision";
            case "precision": command = command.Replace("WITH 7", "WITH n+0.6"); break;
            case "type5":
                prefix += "=CURSORSETPROP('Buffering',5)\n";
                after = "result=result+TRANSFORM(GETNEXTMODIFIED(0))+'|'\n";
                goto case "type";
            case "type": command = command.Replace("n WITH 7", "k WITH 123"); break;
            case "key_source_alias": command = command.Replace("ON id", "ON src.id"); break;
            case "field_source_alias": command = command.Replace("REPLACE n", "REPLACE src.n"); break;
            case "random_expr": prefix += "random=6\n"; command = command.Replace("WITH 7", "WITH 1+random"); break;
            case "key_alias": command = command.Replace("ON id", "ON nosuch.id"); break;
            case "field_alias": command = command.Replace("REPLACE n", "REPLACE nosuch.n"); break;
            case "arrow": command = command.Replace("ON id", "ON t->id").Replace("REPLACE n", "REPLACE t->n"); break;
            case "memvar_random": prefix += "random=7\n"; command = command.Replace("WITH 7", "WITH random"); break;
            case "field_random":
                setup = setup.Replace("n N(6)", "random N(6)");
                command = command.Replace("n WITH 7", "random WITH src.random RANDOM"); break;
            case "width":
            case "prefix_random":
                setup = setup.Replace("t (id N(6)", "t (id C(" + (kind == "width" ? "4" : "6") + ")")
                    .Replace("src (id N(6)", "src (id C(" + (kind == "width" ? "8" : "3") + ")")
                    .Replace("(1,", "('abc',");
                if (kind == "prefix_random") { prefix += "SET EXACT OFF\n"; command += " RANDOM"; }
                break;
            case "logical": setup = setup.Replace("id N(6)", "id L").Replace("(1,", "(.T.,"); break;
            case "found": prefix += "SEEK 999\nGO 1\n"; command += " RANDOM"; after = "result=result+TRANSFORM(FOUND())+'|'\n"; break;
            case "oldval": prefix += "REPLACE n WITH 5\n"; command = command.Replace("n WITH 7", "k WITH TRANSFORM(OLDVAL('n')) RANDOM"); break;
            case "general_scan":
            case "general_random":
                setup = setup.Replace("id N(6)", "id C(4)").Replace("t VALUES (1,", "t VALUES ('a',").Replace("src VALUES (1,", "src VALUES ('A',");
                prefix = prefix.Replace("INDEX ON", "SET COLLATE TO 'GENERAL'\nINDEX ON");
                if (kind == "general_random") command += " RANDOM";
                break;
            case "syntax": command += " FOR .T."; break;
            case "buffering3":
            case "buffering5": prefix += "=CURSORSETPROP('Buffering'," + kind[^1] + ")\n"; goto case "buffering";
            case "fieldstate_changed": command = command.Replace("n WITH 7", "n WITH n+1, k WITH TRANSFORM(GETFLDSTATE('n'))"); break;
            case "buffering": command = command.Replace("n WITH 7", "k WITH TRANSFORM(CURSORGETPROP('Buffering'))"); break;
            case "fieldstate": command = command.Replace("n WITH 7", "k WITH TRANSFORM(GETFLDSTATE('n'))"); break;
            case "oldval_sequence": command = command.Replace("n WITH 7", "n WITH n+1, k WITH TRANSFORM(OLDVAL('n'))"); break;
        }
        string scenario = prefix + "_TALLY=77\n" + MicroVfpSqlDmlTests.Catch(command) + "\n" + after
            + "result=result+TRANSFORM(" + (kind == "field_random" ? "random" : "n") + ")+ '|' +ALLTRIM(k)+'|'+TRANSFORM(_TALLY)";
        return (setup, scenario);
    }
}
