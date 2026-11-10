namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNpvRound3Tests
{
    internal const string ExpectedStoredKey = "114|Index does not match the table. Delete the index file and re-create the index.|Index does not match the table. Delete the index file and re-create the index.|.F.";
    [Fact]
    public void TruncatedStoredKey_ClosesStructuralTable()
    {
        using var dir = new MicroVfpTestSupport.TempDir("round3_index");
        using var session = new CrossVault.FoxDbf.Sql.VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new CrossVault.FoxDbf.MicroVfp.VfpInterpreter(session);
        vfp.Execute(Setup+"\nUSE t\nINDEX ON n+1 TAG bad\nUSE");
        TruncateKey(dir.File("t.cdx"));
        vfp.Execute(StoredKeyScenario);
        Assert.Equal(ExpectedStoredKey, vfp.Memory.Get("result").AsString);
    }
    internal const string StoredKeyScenario = "result='OK'\nTRY\nUSE t\nCATCH TO failure\nresult=TRANSFORM(failure.ErrorNo)+'|'+failure.Message+'|'+MESSAGE()\nENDTRY\nresult=result+'|'+TRANSFORM(USED('t'))";
    internal static void TruncateKey(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes("n+1"));
        Assert.True(offset >= 0);
        bytes[offset+2] = (byte)' ';
        File.WriteAllBytes(path,bytes);
    }
    internal sealed record Probe(string Name, string Setup, string Script, string ExpectedVfp);
    internal const string Setup = "CREATE TABLE t FREE (n N(10,2))\nINSERT INTO t VALUES (100)\nINSERT INTO t VALUES (200)\nUSE";
    internal const string Procedures = "FUNCTION tick()\nPUBLIC calls,trace\ncalls=calls+1\ntrace=trace+TRANSFORM(RECNO())+','\nRETURN 1\nENDFUNC";
    private static readonly Dictionary<string,string> Pins = new()
    {
        ["unary_-+5^2"] = "ERR:1231|Missing operand.|Missing operand.",
        ["unary_+-5^2"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["unary_+5^2"] = "25",
        ["unary_++5"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["unary_+ +5"] = "ERR:36|Command contains unrecognized phrase/keyword.|Command contains unrecognized phrase/keyword.",
        ["unary_-+$5^2"] = "ERR:1231|Missing operand.|Missing operand.",
        ["unary_+$5^2"] = "25",
        ["syntax_SELECT n+ FROM t"] = "ERR:1221|Command is missing required clause.|Command is missing required clause.",
        ["syntax_UPDATE t SET n = n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["syntax_REPLACE n WITH n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["syntax_LOCATE FOR n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["syntax_UPDATE ON n FROM src REPLACE n WITH n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["key_-n^2"] = "-N**2|100",
        ["key_-(n^2)"] = "-(N**2)|200",
        ["key_-2^2"] = "4|100",
        ["key_-+n^2"] = "ERR:1231|Missing operand.|Missing operand.",
        ["rand_SUM RAND() TO x"] = "1.19|1.1923975635",
        ["rand_AVERAGE RAND() TO x"] = "0.60|0.5961987817",
        ["rand_CALCULATE SUM(RAND()) TO x"] = "1.19|1.1923975635",
        ["rand_SUM RAND()+n TO x"] = "300.50|300.4986446083",
        ["rand_AVERAGE RAND()+n TO x"] = "150.25|150.2493223042",
        ["rand_CALCULATE SUM(RAND()+n) TO x"] = "300.50|300.4986446083",
        ["rand_x=RAND()+RAND()"] = "0.96|0.9561890888",
        ["calls_SUM_empty"] = "1|1,|0",
        ["calls_CALCULATE AVG_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE AVG_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE AVG_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MIN_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MIN_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MIN_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MIN_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MIN_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MIN_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MIN_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MAX_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MAX_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MAX_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MAX_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MAX_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MAX_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE MAX_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE CNT_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE CNT_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE CNT_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE CNT_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE CNT_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE CNT_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE CNT_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_SUM_tick()*n"] = "4|1,1,1,2,|300",
        ["tree_SUM_ABS(tick())"] = "4|1,1,1,2,|2",
        ["tree_SUM_IIF(n>0,tick(),0)"] = "5|1,1,1,1,2,|2",
        ["tree_AVERAGE_tick()+n"] = "4|1,1,1,2,|151",
        ["tree_AVERAGE_n+tick()"] = "4|1,1,1,2,|151",
        ["tree_AVERAGE_tick()+tick()"] = "8|1,1,1,1,1,1,2,2,|2",
        ["tree_AVERAGE_tick()*n"] = "4|1,1,1,2,|150",
        ["tree_AVERAGE_ABS(tick())"] = "4|1,1,1,2,|1",
        ["tree_AVERAGE_IIF(n>0,tick(),0)"] = "5|1,1,1,1,2,|1",
        ["tree_CALCULATE SUM_tick()+n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE SUM_n+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE SUM_tick()+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE SUM_tick()*n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE SUM_ABS(tick())"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE SUM_IIF(n>0,tick(),0)"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE AVG_tick()+n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE AVG_n+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE AVG_tick()+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE AVG_tick()*n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE AVG_ABS(tick())"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE AVG_IIF(n>0,tick(),0)"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MIN_tick()+n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MIN_n+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MIN_tick()+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_SUM_one"] = "2|1,1,|1",
        ["calls_SUM_two"] = "3|1,1,2,|2",
        ["calls_SUM_FOR n>100"] = "2|2,2,|1",
        ["calls_SUM_FOR .F."] = "1|3,|0",
        ["calls_SUM_NEXT 1"] = "2|1,1,|1",
        ["calls_SUM_NEXT 0"] = "1|1,|0",
        ["calls_AVERAGE_empty"] = "1|1,|0",
        ["calls_AVERAGE_one"] = "2|1,1,|1",
        ["calls_AVERAGE_two"] = "3|1,1,2,|1",
        ["calls_AVERAGE_FOR n>100"] = "2|2,2,|1",
        ["calls_AVERAGE_FOR .F."] = "1|3,|0",
        ["calls_AVERAGE_NEXT 1"] = "2|1,1,|1",
        ["calls_AVERAGE_NEXT 0"] = "1|1,|0",
        ["calls_CALCULATE SUM_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE SUM_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE SUM_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE SUM_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE SUM_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE SUM_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE SUM_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE AVG_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE AVG_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE AVG_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE AVG_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE STD_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE STD_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE STD_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE STD_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE STD_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE STD_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE STD_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE VAR_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE VAR_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE VAR_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE VAR_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE VAR_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE VAR_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE VAR_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE NPV_empty"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE NPV_one"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE NPV_two"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE NPV_FOR n>100"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE NPV_FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE NPV_NEXT 1"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["calls_CALCULATE NPV_NEXT 0"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_SUM_tick()+n"] = "4|1,1,1,2,|302",
        ["tree_SUM_n+tick()"] = "4|1,1,1,2,|302",
        ["tree_SUM_tick()+tick()"] = "8|1,1,1,1,1,1,2,2,|4",
        ["tree_CALCULATE MIN_tick()*n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MIN_ABS(tick())"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MIN_IIF(n>0,tick(),0)"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MAX_tick()+n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MAX_n+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MAX_tick()+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MAX_tick()*n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MAX_ABS(tick())"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE MAX_IIF(n>0,tick(),0)"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE CNT_tick()+n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE CNT_n+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE CNT_tick()+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE CNT_tick()*n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE CNT_ABS(tick())"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE CNT_IIF(n>0,tick(),0)"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE STD_tick()+n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE STD_n+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE STD_tick()+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE STD_tick()*n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE STD_ABS(tick())"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE STD_IIF(n>0,tick(),0)"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE VAR_tick()+n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE VAR_n+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE VAR_tick()+tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["deep_IIF(.F.,tick(),tick())"] = "7|1,1,1,1,1,1,2,|2",
        ["deep_EVL(0,tick())"] = "5|1,1,1,1,2,|2",
        ["deep_NVL(.NULL.,tick())"] = "5|1,1,1,1,2,|2",
        ["scale_RAND()_0"] = "1|1.1923975635",
        ["scale_RAND()_4"] = "1.1924|1.1923975635",
        ["scale_RAND()+n_0"] = "300.50|300.4986446083",
        ["scale_RAND()+n_4"] = "300.4986|300.4986446083",
        ["scale_ABS(RAND())_0"] = "0|0.4986446083",
        ["scale_ABS(RAND())_4"] = "0.4986|0.4986446083",
        ["scale_ROUND(RAND(),4)_0"] = "0.4986|0.4986000000",
        ["scale_ROUND(RAND(),4)_4"] = "0.4986|0.4986000000",
        ["scale_SQRT(n)_0"] = "24.14|24.1421356237",
        ["scale_SQRT(n)_4"] = "24.1421|24.1421356237",
        ["scale_tick()_0"] = "2|2.0000000000",
        ["scale_tick()_4"] = "2|2.0000000000",
        ["scale_IIF(.T.,1.234567,0)_0"] = "2.469134|2.4691340000",
        ["scale_IIF(.T.,1.234567,0)_4"] = "2.469134|2.4691340000",
        ["scale_n*RAND()_0"] = "51.57|51.5670773806",
        ["scale_n*RAND()_4"] = "51.567077|51.5670773806",
        ["scale_RAND()*RAND()_0"] = "1|0.5477769320",
        ["scale_RAND()*RAND()_4"] = "0.54777693|0.5477769320",
        ["rng_STD_two"] = "49.7677038613|0.1561341470",
        ["rng_STD_FOR n>100"] = "0.0000000000|0.0170261655",
        ["rng_STD_FOR .F."] = "0.0000000000|0.4816184428",
        ["rng_STD_NEXT 1"] = "0.0000000000|0.0170261655",
        ["rng_STD_NEXT 0"] = "0.0000000000|0.4816184428",
        ["rng_VAR_empty"] = "0.0000000000|0.4816184428",
        ["rng_VAR_one"] = "0.0000000000|0.0170261655",
        ["rng_VAR_two"] = "2476.8243476308|0.1561341470",
        ["rng_VAR_FOR n>100"] = "0.0000000000|0.0170261655",
        ["rng_VAR_FOR .F."] = "0.0000000000|0.4816184428",
        ["rng_VAR_NEXT 1"] = "0.0000000000|0.0170261655",
        ["rng_VAR_NEXT 0"] = "0.0000000000|0.4816184428",
        ["rng_NPV_empty"] = "-500.5183815572|0.0170261655",
        ["rng_NPV_one"] = "-400.5324850524|0.9014136964",
        ["rng_NPV_two"] = "-231.6013518808|0.5361762783",
        ["rng_NPV_FOR n>100"] = "-300.7024573098|0.9014136964",
        ["rng_NPV_FOR .F."] = "-500.5183815572|0.0170261655",
        ["rng_NPV_NEXT 1"] = "-400.5324850524|0.9014136964",
        ["rng_NPV_NEXT 0"] = "-500.5183815572|0.0170261655",
        ["deep_(tick()+n)*2"] = "5|1,1,1,1,2,|604",
        ["deep_tick()+n+tick()"] = "9|1,1,1,1,1,1,1,2,2,|304",
        ["deep_ABS(ABS(tick()))"] = "5|1,1,1,1,2,|2",
        ["deep_-tick()"] = "4|1,1,1,2,|-2",
        ["deep_IIF(.T.,tick(),tick())"] = "7|1,1,1,1,1,1,2,|2",
        ["tree_CALCULATE VAR_tick()*n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE VAR_ABS(tick())"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["tree_CALCULATE VAR_IIF(n>0,tick(),0)"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_SUM"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_AVG"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_MIN"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_MAX"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_CNT"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_STD"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_VAR"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["udf_direct_NPV"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["rng_SUM_empty"] = "0.0000000000|0.4816184428",
        ["rng_SUM_one"] = "100.4816184428|0.0170261655",
        ["rng_SUM_two"] = "300.4986446083|0.1561341470",
        ["rng_SUM_FOR n>100"] = "200.4816184428|0.0170261655",
        ["rng_SUM_FOR .F."] = "0.0000000000|0.4816184428",
        ["rng_SUM_NEXT 1"] = "100.4816184428|0.0170261655",
        ["rng_SUM_NEXT 0"] = "0.0000000000|0.4816184428",
        ["rng_AVG_empty"] = "0.0000000000|0.4816184428",
        ["rng_AVG_one"] = "100.4816184428|0.0170261655",
        ["rng_AVG_two"] = "150.2493223042|0.1561341470",
        ["rng_AVG_FOR n>100"] = "200.4816184428|0.0170261655",
        ["rng_AVG_FOR .F."] = "0.0000000000|0.4816184428",
        ["rng_AVG_NEXT 1"] = "100.4816184428|0.0170261655",
        ["rng_AVG_NEXT 0"] = "0.0000000000|0.4816184428",
        ["rng_MIN_empty"] = "0.2454099681|0.7107791207",
        ["rng_MIN_one"] = "100.2454099681|0.4816184428",
        ["rng_MIN_two"] = "100.2454099681|0.0170261655",
        ["rng_MIN_FOR n>100"] = "200.2454099681|0.4816184428",
        ["rng_MIN_FOR .F."] = "0.2454099681|0.7107791207",
        ["rng_MIN_NEXT 1"] = "100.2454099681|0.4816184428",
        ["rng_MAX_empty"] = "0.2454099681|0.7107791207",
        ["rng_MAX_one"] = "100.7107791207|0.4816184428",
        ["rng_MAX_two"] = "200.4816184428|0.0170261655",
        ["rng_MAX_FOR n>100"] = "200.7107791207|0.4816184428",
        ["rng_MAX_FOR .F."] = "0.2454099681|0.7107791207",
        ["rng_MAX_NEXT 1"] = "100.7107791207|0.4816184428",
        ["rng_CNT_empty"] = "0.0000000000|0.4816184428",
        ["rng_CNT_one"] = "1.0000000000|0.0170261655",
        ["rng_CNT_two"] = "2.0000000000|0.1561341470",
        ["rng_CNT_FOR n>100"] = "1.0000000000|0.0170261655",
        ["rng_CNT_FOR .F."] = "0.0000000000|0.4816184428",
        ["rng_CNT_NEXT 1"] = "1.0000000000|0.0170261655",
        ["rng_CNT_NEXT 0"] = "0.0000000000|0.4816184428",
        ["rng_STD_empty"] = "0.0000000000|0.4816184428",
        ["rng_STD_one"] = "0.0000000000|0.0170261655",
        ["more_scale_PI()_0"] = "6|6.2831853072",
        ["more_scale_PI()_4"] = "6.2832|6.2831853072",
        ["more_scale_EXP(1)_0"] = "5|5.4365636569",
        ["more_scale_EXP(1)_4"] = "5.4366|5.4365636569",
        ["more_scale_LOG(n)_0"] = "9.90|9.9034875525",
        ["more_scale_LOG(n)_4"] = "9.9035|9.9034875525",
        ["more_scale_SIN(n)_0"] = "-1|-1.3796629383",
        ["more_scale_SIN(n)_4"] = "-1.3797|-1.3796629383",
        ["more_scale_VAL('1.23456')_0"] = "2|2.4691200000",
        ["more_scale_VAL('1.23456')_4"] = "2.4691|2.4691200000",
        ["more_scale_IIF(.T.,RAND(),0)_0"] = "0|0.1731603125",
        ["more_scale_IIF(.T.,RAND(),0)_4"] = "0.1732|0.1731603125",
        ["more_scale_NVL(.NULL.,RAND())_0"] = "0|0.1731603125",
        ["more_scale_NVL(.NULL.,RAND())_4"] = "0.1732|0.1731603125",
        ["more_scale_EVL(0,RAND())_0"] = "0|0.1731603125",
        ["more_scale_EVL(0,RAND())_4"] = "0.1732|0.1731603125",
        ["branch_IIF(.T.,tick(),missing)"] = "5|1,1,1,1,2,|2",
        ["branch_IIF(.T.,tick(),unknownfn())"] = "5|1,1,1,1,2,|2",
        ["branch_IIF(.F.,unknownfn(),tick())"] = "5|1,1,1,1,2,|2",
        ["npv_udf_tick()/10,n"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["npv_udf_tick()/10,n FOR .F."] = "0||0",
        ["npv_udf_0.1,n,tick()"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["npv_udf_0.1,n,tick() FOR .F."] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["npv_udf_0.1,IIF(.T.,n,tick())"] = "0||256.20",
        ["npv_udf_0.1,IIF(.T.,n,tick()) FOR .F."] = "0||0",
        ["onerror_SELECT n+ FROM t"] = "ERR:1221|Command is missing required clause.|Command is missing required clause.",
        ["onerror_UPDATE t SET n = n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["onerror_REPLACE n WITH n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["onerror_LOCATE FOR n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["onerror_UPDATE ON n FROM src REPLACE n WITH n +"] = "ERR:1231|Missing operand.|Missing operand.",
        ["rng_MIN_NEXT 0"] = "<null>|0.7107791207",
        ["rng_MAX_NEXT 0"] = "<null>|0.7107791207",
        ["unknown_SUM missingfn() TO x"] = "ERR:1|File 'missingfn.prg' does not exist.|File 'missingfn.prg' does not exist.",
        ["unknown_CALCULATE SUM(missingfn()) TO x"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["unknown_CALCULATE SUM(missingfn()) FOR .F. TO x"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["unknown_CALCULATE NPV(missingfn(),n) TO x"] = "ERR:31|Invalid subscript reference.|Invalid subscript reference.",
        ["unknown_CALCULATE NPV(missingfn(),n) FOR .F. TO x"] = "0",
    };
    internal static readonly Probe[] Probes = Build();
    private static Probe[] Build()
    {
        var probes = new List<Probe>();
        void Add(string name, string command, string setup = Setup, string before = "", string after = "result=TRANSFORM(x)")
            => probes.Add(new(name, setup, "USE t\nGO TOP\nSET DECIMALS TO 2\n" + before + "\nTRY\n" + command + "\n" + after
                + "\nCATCH TO failure\nresult='ERR:'+TRANSFORM(failure.ErrorNo)+'|'+failure.Message+'|'+MESSAGE()\nENDTRY", Pins[name]));
        foreach (string expr in new[] { "-+5^2", "+-5^2", "+5^2", "++5", "+ +5", "-+$5^2", "+$5^2" })
            Add("unary_" + expr, "x=" + expr);
        foreach (string command in new[] { "SELECT n+ FROM t", "UPDATE t SET n = n +", "REPLACE n WITH n +", "LOCATE FOR n +", "UPDATE ON n FROM src REPLACE n WITH n +" })
            Add("syntax_" + command, command, Setup + "\nCREATE TABLE src FREE (n N(10,2))\nINSERT INTO src VALUES (100)\nUSE", "USE src IN 2\nSELECT t", "result='OK'");
        foreach (string expr in new[] { "-n^2", "-(n^2)", "-2^2", "-+n^2" })
            Add("key_" + expr, "INDEX ON " + expr + " TAG a", after: "result=KEY()+'|'+TRANSFORM(n)");
        foreach (string command in new[] { "SUM RAND() TO x", "AVERAGE RAND() TO x", "CALCULATE SUM(RAND()) TO x", "SUM RAND()+n TO x", "AVERAGE RAND()+n TO x", "CALCULATE SUM(RAND()+n) TO x", "x=RAND()+RAND()" })
            Add("rand_" + command, command, before: "=RAND(1)", after: "result=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,10))");
        foreach (string fn in new[] { "SUM", "AVERAGE", "CALCULATE SUM", "CALCULATE AVG", "CALCULATE MIN", "CALCULATE MAX", "CALCULATE CNT", "CALCULATE STD", "CALCULATE VAR", "CALCULATE NPV" })
        foreach (string scope in new[] { "empty", "one", "two", "FOR n>100", "FOR .F.", "NEXT 1", "NEXT 0" })
        {
            string setup = scope == "empty" ? "CREATE TABLE t FREE (n N(10,2))\nUSE" : scope == "one" ? "CREATE TABLE t FREE (n N(10,2))\nINSERT INTO t VALUES (100)\nUSE" : Setup;
            string operand = fn == "CALCULATE NPV" ? "(tick()/10,tick()+n,tick()-501)" : fn.StartsWith("CALCULATE") ? "(tick())" : " tick()";
            Add("calls_" + fn + "_" + scope, fn + operand + (scope.Contains(' ') ? " " + scope : "") + " TO x", setup,
                "PUBLIC calls,trace\ncalls=0\ntrace=''", "result=TRANSFORM(calls)+'|'+trace+'|'+TRANSFORM(x)");
        }
        foreach (string fn in new[] { "SUM", "AVERAGE", "CALCULATE SUM", "CALCULATE AVG", "CALCULATE MIN", "CALCULATE MAX", "CALCULATE CNT", "CALCULATE STD", "CALCULATE VAR" })
        foreach (string expr in new[] { "tick()+n", "n+tick()", "tick()+tick()", "tick()*n", "ABS(tick())", "IIF(n>0,tick(),0)" })
            Add("tree_"+fn+"_"+expr, fn+(fn.StartsWith("CALCULATE") ? "("+expr+")" : " "+expr)+" TO x",
                before: "PUBLIC calls,trace\ncalls=0\ntrace=''", after: "result=TRANSFORM(calls)+'|'+trace+'|'+TRANSFORM(x)");
        foreach (string fn in new[] { "SUM", "AVG", "MIN", "MAX", "CNT", "STD", "VAR", "NPV" })
            Add("udf_direct_"+fn, "CALCULATE "+fn+(fn=="NPV" ? "(0.1,tick())" : "(tick())")+" TO x", before: "PUBLIC calls,trace\ncalls=0\ntrace=''");
        foreach (string fn in new[] { "SUM", "AVG", "MIN", "MAX", "CNT", "STD", "VAR", "NPV" })
        foreach (string scope in new[] { "empty", "one", "two", "FOR n>100", "FOR .F.", "NEXT 1", "NEXT 0" })
        {
            string setup = scope == "empty" ? "CREATE TABLE t FREE (n N(10,2))\nUSE" : scope == "one" ? "CREATE TABLE t FREE (n N(10,2))\nINSERT INTO t VALUES (100)\nUSE" : Setup;
            Add("rng_"+fn+"_"+scope,"CALCULATE "+fn+(fn=="NPV" ? "(RAND()/10,RAND()+n,RAND()-501)" : "(RAND()+n)")+(scope.Contains(' ') ? " "+scope : "")+" TO x", setup,
                "=RAND(1)", "result=IIF(ISNULL(x),'<null>',ALLTRIM(STR(x,30,10)))+'|'+ALLTRIM(STR(RAND(),30,10))");
        }
        foreach (string expr in new[] { "(tick()+n)*2", "tick()+n+tick()", "ABS(ABS(tick()))", "-tick()", "IIF(.T.,tick(),tick())", "IIF(.F.,tick(),tick())", "EVL(0,tick())", "NVL(.NULL.,tick())" })
            Add("deep_"+expr,"SUM "+expr+" TO x",before:"PUBLIC calls,trace\ncalls=0\ntrace=''",after:"result=TRANSFORM(calls)+'|'+trace+'|'+TRANSFORM(x)");
        foreach (string expr in new[] { "RAND()", "RAND()+n", "ABS(RAND())", "ROUND(RAND(),4)", "SQRT(n)", "tick()", "IIF(.T.,1.234567,0)", "n*RAND()", "RAND()*RAND()" })
        foreach (int decimals in new[] { 0, 4 })
            Add("scale_"+expr+"_"+decimals,"SUM "+expr+" TO x",before:"=RAND(1)\nSET DECIMALS TO "+decimals+"\nPUBLIC calls,trace\ncalls=0\ntrace=''",after:"result=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,10))");
        foreach (string command in new[] { "SELECT n+ FROM t", "UPDATE t SET n = n +", "REPLACE n WITH n +", "LOCATE FOR n +", "UPDATE ON n FROM src REPLACE n WITH n +" })
            Add("onerror_"+command,"ON ERROR result='ERR:'+TRANSFORM(ERROR())+'|'+MESSAGE()\n"+command+"\nON ERROR",Setup+"\nCREATE TABLE src FREE (n N(10,2))\nINSERT INTO src VALUES (100)\nUSE", "USE src IN 2\nSELECT t", "");
        foreach (string expr in new[] { "PI()", "EXP(1)", "LOG(n)", "SIN(n)", "VAL('1.23456')", "IIF(.T.,RAND(),0)", "NVL(.NULL.,RAND())", "EVL(0,RAND())" })
        foreach (int decimals in new[] { 0, 4 })
            Add("more_scale_"+expr+"_"+decimals,"SUM "+expr+" TO x",before:"=RAND(1)\nSET DECIMALS TO "+decimals,after:"result=TRANSFORM(x)+'|'+ALLTRIM(STR(x,30,10))");
        foreach (string expr in new[] { "IIF(.T.,tick(),missing)", "IIF(.T.,tick(),unknownfn())", "IIF(.F.,unknownfn(),tick())" })
            Add("branch_"+expr,"SUM "+expr+" TO x",before:"PUBLIC calls,trace\ncalls=0\ntrace=''",after:"result=TRANSFORM(calls)+'|'+trace+'|'+TRANSFORM(x)");
        foreach (string expr in new[] { "tick()/10,n", "0.1,n,tick()", "0.1,IIF(.T.,n,tick())" })
        foreach (string scope in new[] { "", " FOR .F." })
            Add("npv_udf_"+expr+scope,"CALCULATE NPV("+expr+")"+scope+" TO x",before:"PUBLIC calls,trace\ncalls=0\ntrace=''",after:"result=TRANSFORM(calls)+'|'+trace+'|'+TRANSFORM(x)");
        foreach (string command in new[] { "SUM missingfn() TO x", "CALCULATE SUM(missingfn()) TO x", "CALCULATE SUM(missingfn()) FOR .F. TO x", "CALCULATE NPV(missingfn(),n) TO x", "CALCULATE NPV(missingfn(),n) FOR .F. TO x" })
            Add("unknown_"+command,command);
        return probes.Select(p =>
        {
            bool indirect = p.Name.StartsWith("calls_CALCULATE") || p.Name.StartsWith("tree_CALCULATE");
            if (indirect) p = p with { Script = p.Script.Replace("tick()", "EVALUATE('tick()')") };
            // Even rejected CALCULATE operands must leave the UDF counter untouched.
            return indirect || p.Name.StartsWith("udf_direct_") || p.Name.StartsWith("npv_udf_")
                ? p with { Script = p.Script + "\nresult=result+'|calls='+TRANSFORM(calls)+'|trace='+trace",
                    ExpectedVfp = p.ExpectedVfp + "|calls=0|trace=" }
                : p;
        }).ToArray();
    }
    [Theory]
    [InlineData("SELECT n+ FROM t",1221,"Command is missing required clause.")]
    [InlineData("UPDATE t SET n = n +",1231,"Missing operand.")]
    public void TruncatedSql_SessionAndAdo(string sql, int number, string message)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round3_sql");
        using var session = new CrossVault.FoxDbf.Sql.VfpSession();
        session.OpenDirectory(dir.Path);
        using (var vfp = new CrossVault.FoxDbf.MicroVfp.VfpInterpreter(session)) vfp.Execute(Setup);
        AssertSqlPaths(dir.Path, sql, number, message);
    }

    internal static void AssertSqlPaths(string path, string sql, int number, string message)
    {
        using var session = new CrossVault.FoxDbf.Sql.VfpSession();
        session.OpenDirectory(path);
        var error = Assert.Throws<CrossVault.FoxDbf.Sql.FoxDbfSqlException>(() => session.Execute(sql));
        Assert.Equal(number,error.VfpErrorNumber);
        Assert.Equal(message,error.Message);
        using var connection = new CrossVault.FoxDbf.Data.FoxDbfConnection("Data Source="+path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var adoError = Assert.ThrowsAny<Exception>(() => command.ExecuteScalar());
        Assert.Equal(number,Assert.IsAssignableFrom<IVfpErrorCode>(adoError).VfpErrorNumber);
        Assert.Equal(message,adoError.Message);
    }
    public static IEnumerable<object[]> Cases => Probes.Select(p => new object[] { p.Name });
    [Theory]
    [MemberData(nameof(Cases))]
    public void Round3_MatchesVfp9(string name)
    {
        var p = Probes.Single(p => p.Name == name);
        Assert.Equal(p.ExpectedVfp, MicroVfpXbaseUpdateTests.Run(p.Setup, Procedures + "\n" + p.Script));
    }
}
