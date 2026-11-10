namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpCalculateNpvTests
{
    internal sealed record Probe(string Name, string Setup, string Script, string Expected);

    internal const string Setup = "CREATE TABLE t (n N(10,2) NULL, r N(10,2) NULL, initial N(10,2) NULL)\n"
        + "INSERT INTO t VALUES (100,0.1,-500)\nINSERT INTO t VALUES (200,0.2,-600)\nINSERT INTO t VALUES (300,0.3,-700)\nUSE";

    internal const string Value = "result=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,10))+'|'+VARTYPE(x)";

    internal static string Scenario(string command, string before = "", string after = Value)
        => "USE t\nGO TOP\nSET DECIMALS TO 4\n" + before + "\nTRY\n" + command + "\n" + after
            + "\nCATCH TO failure\nresult='ERR:'+TRANSFORM(failure.ErrorNo)+'|'+failure.Message+'|'+MESSAGE()\nENDTRY";

    private static readonly Dictionary<string, string> Pins = new()
    {
        ["basic"] = "481.5928|481.5927873779|N",
        ["initial"] = "-18.4072|-18.4072126221|N",
        ["next"] = "256.1983|256.1983471074|N",
        ["for"] = "429.7521|429.7520661157|N",
        ["while"] = "256.1983|256.1983471074|N",
        ["rest"] = "429.7521|429.7520661157|N",
        ["null_middle"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["null_first"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["all_null"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["null_rate"] = "0|0.0000000000|N",
        ["null_initial"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["rate_0"] = "600|600.0000000000|N",
        ["rate_-0.1"] = "769.5473|769.5473251029|N",
        ["rate_-1"] = "***************|******************************|N",
        ["rate_-2"] = "-200|-200.0000000000|N",
        ["field_rate"] = "366.3478|366.3478204898|N",
        ["field_rate_go2"] = "366.3478|366.3478204898|N",
        ["field_rate_for"] = "344.1815|344.1814595661|N",
        ["field_initial"] = "-18.4072|-18.4072126221|N",
        ["expression_rate"] = "366.3478|366.3478204898|N",
        ["empty"] = "0|0.0000000000|N",
        ["empty_initial"] = "-500|-500.0000000000|N",
        ["no_match"] = "-500|-500.0000000000|N",
        ["type_'bad',n"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["type_0.1,'bad'"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["type_0.1,n,'bad'"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["type_.T.,n"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["type_0.1,.T."] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["type_0.1,n,.T."] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["invalid_NPV(n)"] = "ERR:10|Syntax error.|Syntax error.",
        ["unexecuted_NPV(n)"] = "OK",
        ["invalid_NPV()"] = "ERR:152|Missing expression.|Missing expression.",
        ["unexecuted_NPV()"] = "OK",
        ["invalid_NPV(0.1,n,1,2)"] = "ERR:10|Syntax error.|Syntax error.",
        ["unexecuted_NPV(0.1,n,1,2)"] = "OK",
        ["invalid_FOO(n)"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["unexecuted_FOO(n)"] = "OK",
        ["invalid_n"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["unexecuted_n"] = "OK",
        ["invalid_SUM(n"] = "ERR:10|Syntax error.|Syntax error.",
        ["unexecuted_SUM(n"] = "OK",
        ["scalar"] = "ERR:1|File 'npv.prg' does not exist.|File 'npv.prg' does not exist.",
        ["flow_I"] = "481.5928|481.5927873779|N",
        ["flow_Y"] = "481.5928|481.5927873779|N",
        ["flow_B"] = "481.5928|481.5927873779|N",
        ["flow_N(12,4)"] = "481.5928|481.5927873779|N",
        ["decimals_2"] = "481.59|481.5927873779|N",
        ["decimals_changed"] = "481.59|481.5927873779|N",
        ["mixed"] = "481.5928|481.5927873779|N|600|200|100|300|3|81.6497|6666.6667|-104.1667",
        ["initial_for"] = "-170.2479|-170.2479338843|N",
        ["initial_next"] = "-418.1818|-418.1818181818|N",
        ["initial_no_match"] = "0|0.0000000000|N",
        ["initial_expression"] = "482.5928|482.5927873779|N",
        ["null_rate_middle"] = "268.4239|268.4238838085|N",
        ["null_rate_first"] = "344.1815|344.1814595661|N",
        ["null_rate_and_flow"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["null_rate_and_initial"] = "-500|-500.0000000000|N",
        ["null_flow_filtered"] = "429.7521|429.7520661157|N",
        ["bad_rate_no_match"] = "0|0.0000000000|N",
        ["bad_flow_no_match"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["bad_initial_no_match"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["null_flow_no_match"] = "ERR:27|Not a numeric expression.|Not a numeric expression.",
        ["initial_null_first_filtered"] = "-170.2479|-170.2479338843|N",
        ["currency_rate_initial"] = "-18.4072|-18.4072126221|N",
        ["syntax_NPV(,n)"] = "ERR:152|Missing expression.|Missing expression.",
        ["syntax_NPV(0.1,)"] = "ERR:152|Missing expression.|Missing expression.",
        ["syntax_NPV(0.1,n,)"] = "ERR:152|Missing expression.|Missing expression.",
        ["syntax_NPV(0.1,n)+1"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["syntax_SUM()"] = "ERR:152|Missing expression.|Missing expression.",
        ["syntax_SUM(n,1)"] = "ERR:10|Syntax error.|Syntax error.",
        ["overflow_0"] = "*************|******************************|N",
        ["overflow_2"] = "*************|******************************|N",
        ["overflow_6"] = "*****************|******************************|N",
        ["overflow_changed"] = "*************|******************************|N",
        ["zero_over_zero"] = "***************|******************************|N",
        ["initial_null_rate_first"] = "-155.8185|-155.8185404339|N",
        ["dynamic_flow_for"] = "429.7521|429.7520661157|N",
        ["dynamic_flow_no_match"] = "0|0.0000000000|N",
        ["currency_literal_$-500"] = "$-500.0000|-500.0000000000|Y",
        ["currency_literal_$+500"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["currency_literal_$ -500"] = "$-500.0000|-500.0000000000|Y",
        ["currency_literal_$-.12345"] = "$-0.1235|-0.1235000000|Y",
        ["currency_literal_$(-500)"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["fractional_flow_I"] = "7.1450|7.1450037566|N",
        ["fractional_flow_Y"] = "8.2457|8.2456912096|N",
        ["fractional_flow_B"] = "8.2457|8.2456912096|N",
        ["fractional_flow_N(12,4)"] = "8.2457|8.2456912096|N",
        ["nested_arguments"] = "403.4091|403.4090909091|N",
        ["bracket_literal"] = "481.5928|481.5927873779|N",
        ["scoped_empty_argument"] = "ERR:152|Missing expression.|Missing expression.",
        ["trailing_comma"] = "ERR:10|Syntax error.|Syntax error.",
        ["leading_comma"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
    };

    // Pins are obtained from MicroVfpCalculateNpvVfp9OracleTests using VFP9-created tables.
    internal static readonly Probe[] Probes = BuildProbes();

    private static Probe[] BuildProbes()
    {
        var probes = new List<Probe>();
        void Add(string name, string command, string before = "", string after = Value, string setup = Setup)
            => probes.Add(new(name, setup, Scenario(command, before, after), Pins[name]));
        Add("basic", "CALCULATE NPV(0.1,n) TO x");
        Add("initial", "CALCULATE NPV(0.1,n,-500) TO x");
        Add("next", "CALCULATE NEXT 2 NPV(0.1,n) TO x");
        Add("for", "CALCULATE NPV(0.1,n) FOR n>100 TO x");
        Add("while", "CALCULATE NPV(0.1,n) WHILE n<300 TO x");
        Add("rest", "CALCULATE REST NPV(0.1,n) TO x", before: "GO 2");
        Add("null_middle", "CALCULATE NPV(0.1,n) TO x", before: "GO 2\nREPLACE n WITH .NULL.\nGO TOP");
        Add("null_first", "CALCULATE NPV(0.1,n) TO x", before: "REPLACE n WITH .NULL.");
        Add("all_null", "CALCULATE NPV(0.1,n,-500) TO x", before: "REPLACE ALL n WITH .NULL.\nGO TOP");
        Add("null_rate", "CALCULATE NPV(.NULL.,n) TO x");
        Add("null_initial", "CALCULATE NPV(0.1,n,.NULL.) TO x");
        foreach (string rate in new[] { "0", "-0.1", "-1", "-2" })
            Add("rate_" + rate, $"CALCULATE NPV({rate},n) TO x");
        Add("field_rate", "CALCULATE NPV(r,n) TO x");
        Add("field_rate_go2", "CALCULATE NPV(r,n) TO x", before: "GO 2");
        Add("field_rate_for", "CALCULATE NPV(r,n) FOR n>100 TO x");
        Add("field_initial", "CALCULATE NPV(0.1,n,initial) TO x", before: "GO 2");
        Add("expression_rate", "CALCULATE NPV(RECNO()/10,n) TO x", before: "GO 2");
        Add("empty", "CALCULATE NPV(0.1,n) TO x", setup: "CREATE TABLE t (n N(10,2) NULL)\nUSE");
        Add("empty_initial", "CALCULATE NPV(0.1,n,-500) TO x", setup: "CREATE TABLE t (n N(10,2) NULL)\nUSE");
        Add("no_match", "CALCULATE NPV(0.1,n,-500) FOR .F. TO x");
        foreach (string arguments in new[] { "'bad',n", "0.1,'bad'", "0.1,n,'bad'", ".T.,n", "0.1,.T.", "0.1,n,.T." })
            Add("type_" + arguments, $"CALCULATE NPV({arguments}) TO x");
        foreach (string item in new[] { "NPV(n)", "NPV()", "NPV(0.1,n,1,2)", "FOO(n)", "n", "SUM(n" })
        {
            Add("invalid_" + item, $"CALCULATE {item} TO x");
            Add("unexecuted_" + item, $"IF .F.\nCALCULATE {item} TO x\nENDIF", after: "result='OK'");
        }
        Add("scalar", "x=NPV(0.1,n)");
        foreach (string type in new[] { "I", "Y", "B", "N(12,4)" })
            Add("flow_" + type, "CALCULATE NPV(0.1,n) TO x", setup: Setup.Replace("n N(10,2)", "n " + type));
        Add("decimals_2", "CALCULATE NPV(0.1,n) TO x", before: "SET DECIMALS TO 2");
        Add("decimals_changed", "CALCULATE NPV(0.1,n) TO x", before: "SET DECIMALS TO 2", after: "SET DECIMALS TO 6\n" + Value);
        Add("mixed", "CALCULATE NPV(0.1,n),SUM(n),AVG(n),MIN(n),MAX(n),CNT(n),STD(n),VAR(n),NPV(0.2,n,-500) TO x,a,b,c,d,e,f,g,y",
            after: Value + "\nresult=result+'|'+TRANSFORM(a)+'|'+TRANSFORM(b)+'|'+TRANSFORM(c)+'|'+TRANSFORM(d)+'|'+TRANSFORM(e)+'|'+TRANSFORM(f)+'|'+TRANSFORM(g)+'|'+TRANSFORM(y)");
        Add("initial_for", "CALCULATE NPV(0.1,n,initial) FOR n>100 TO x");
        Add("initial_next", "CALCULATE NEXT 1 NPV(0.1,n,initial) TO x", before: "GO 2");
        Add("initial_no_match", "CALCULATE NPV(0.1,n,initial) FOR .F. TO x", before: "GO 2");
        Add("initial_expression", "CALCULATE NPV(0.1,n,RECNO()) TO x", before: "GO 2");
        Add("null_rate_middle", "CALCULATE NPV(r,n) TO x", before: "GO 2\nREPLACE r WITH .NULL.\nGO TOP");
        Add("null_rate_first", "CALCULATE NPV(r,n) TO x", before: "REPLACE r WITH .NULL.");
        Add("null_rate_and_flow", "CALCULATE NPV(.NULL.,.NULL.) TO x");
        Add("null_rate_and_initial", "CALCULATE NPV(.NULL.,n,-500) TO x");
        Add("null_flow_filtered", "CALCULATE NPV(0.1,n) FOR n>100 TO x", before: "REPLACE n WITH .NULL.");
        Add("bad_rate_no_match", "CALCULATE NPV('bad',n) FOR .F. TO x");
        Add("bad_flow_no_match", "CALCULATE NPV(0.1,'bad') FOR .F. TO x");
        Add("bad_initial_no_match", "CALCULATE NPV(0.1,n,'bad') FOR .F. TO x");
        Add("null_flow_no_match", "CALCULATE NPV(0.1,.NULL.) FOR .F. TO x");
        Add("initial_null_first_filtered", "CALCULATE NPV(0.1,n,initial) FOR n>100 TO x", before: "REPLACE initial WITH .NULL.");
        Add("currency_rate_initial", "CALCULATE NPV($0.1,n,$-500) TO x");
        foreach (string item in new[] { "NPV(,n)", "NPV(0.1,)", "NPV(0.1,n,)", "NPV(0.1,n)+1", "SUM()", "SUM(n,1)" })
            Add("syntax_" + item, $"CALCULATE {item} TO x");
        foreach (int decimals in new[] { 0, 2, 6 })
            Add("overflow_" + decimals, "CALCULATE NPV(-1,n) TO x", before: $"SET DECIMALS TO {decimals}");
        Add("overflow_changed", "CALCULATE NPV(-1,n) TO x", before: "SET DECIMALS TO 2", after: "SET DECIMALS TO 6\n" + Value);
        Add("zero_over_zero", "CALCULATE NPV(-1,0) TO x");
        Add("initial_null_rate_first", "CALCULATE NPV(r,n,initial) TO x", before: "REPLACE r WITH .NULL.");
        Add("dynamic_flow_for", "CALCULATE NPV(0.1,IIF(n=100,'bad',n)) FOR n>100 TO x");
        Add("dynamic_flow_no_match", "CALCULATE NPV(0.1,IIF(EOF(),1,'bad')) FOR .F. TO x");
        foreach (string literal in new[] { "$-500", "$+500", "$ -500", "$-.12345", "$(-500)" })
            Add("currency_literal_" + literal, "x=" + literal);
        foreach (string type in new[] { "I", "Y", "B", "N(12,4)" })
            Add("fractional_flow_" + type, "CALCULATE NPV(0.1,n) TO x", setup: Setup.Replace("n N(10,2)", "n " + type)
                .Replace("(100,", "(1.2345,").Replace("(200,", "(3.4567,").Replace("(300,", "(5.6789,"));
        Add("nested_arguments", "CALCULATE NPV(IIF(n>100,0.2,0.1),n) TO x");
        Add("bracket_literal", "CALCULATE NPV(IIF([)]=[)],0.1,0.2),n) TO x");
        Add("scoped_empty_argument", "CALCULATE NEXT 1 NPV() TO x");
        Add("trailing_comma", "CALCULATE NPV(0.1,n), TO x");
        Add("leading_comma", "CALCULATE ,NPV(0.1,n) TO x");
        return probes.ToArray();
    }

    public static IEnumerable<object[]> Cases => Probes.Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void CalculateNpvAndDeferredErrors_MatchVfp9(string name)
    {
        var probe = Probes.Single(p => p.Name == name);
        Assert.Equal(probe.Expected, MicroVfpXbaseUpdateTests.Run(probe.Setup, probe.Script));
    }
}
