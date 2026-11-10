using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpNpvRound5Tests
{
    internal sealed record Probe(string Expression, string ExpectedVfp);
    internal static readonly Probe[] Probes =
    [
        new("SQRT(-1)", "ERR|61|SQRT() argument cannot be negative."), new("SIGN(SQRT(-1))", "ERR|61|SQRT() argument cannot be negative."),
        new("LOG(0)", "ERR|58|LOG(): Zero or negative used as argument."), new("LOG(-1)", "ERR|58|LOG(): Zero or negative used as argument."), new("LOG10(-1)", "ERR|292|Cannot use 0 or negative as the argument for LOG10()."),
        new("ACOS(2)", "ERR|293|Expression used with ACOS() is out of range."), new("ASIN(2)", "ERR|291|Expression used with ASIN() is out of range."), new("EXP(1000)", "N|*************|******************************"),
        new("SIGN(EXP(1000))", "N|1|1.0000000000"), new("INT(SQRT(-1))", "ERR|61|SQRT() argument cannot be negative."), new("ROUND(SQRT(-1),2)", "ERR|61|SQRT() argument cannot be negative."),
        new("10^400", "N|*************|******************************"), new("-(10^400)", "N|***********|******************************"), new("MOD(5,0)", "ERR|1307|Cannot divide by 0."), new("5%0", "ERR|1307|Cannot divide by 0."),
        new("SIGN(-EXP(1000))", "N|-1|-1.0000000000"), new("ABS(SQRT(-1))", "ERR|61|SQRT() argument cannot be negative."), new("CEILING(SQRT(-1))", "ERR|61|SQRT() argument cannot be negative."),
        new("FLOOR(SQRT(-1))", "ERR|61|SQRT() argument cannot be negative."), new("ROUND(1.25,1000)", "N|1.250000000000000000|1.2500000000"), new("ROUND(1.25,-1000)", "N|0|0.0000000000"),
        new("ROUND(1.25,-2147483648)", "N|1|1.0000000000"), new("ROUND($1.25,-2147483648)", "Y|$1.00|1.0000000000"),
        new("NTOM(EXP(1000))", "ERR|1988|Currency value is out of range."), new("NTOM(SQRT(-1))", "ERR|61|SQRT() argument cannot be negative."), new("$1*10^100", "ERR|1988|Currency value is out of range."),
        new("$1%(-10^100)", "Y|$1.00|1.0000000000"), new("SUBSTR('abc',2,2147483647)", "C|bc"),
        new("STUFF('abc',2,2147483647,'x')", "C|ax"), new("REPLICATE('abc',2147483647)", "ERR|1903|String is too long to fit."),
        new("GOMONTH(1,2147483647)", "ERR|11|Function argument value, type, or count is invalid."), new("GOMONTH({^2024-01-01},2147483647)", "D|  /  /    "),
        new("{^9999-12-31}+1", "D|  /  /    "), new("{^9999-12-31 23:59:59}+1", "T|  /  /       :  :   AM"),
        new("{^2024-01-01 00:00:00}+SQRT(-1)", "ERR|61|SQRT() argument cannot be negative."),
        new("WEEK({^2024-01-01},1,8)", "ERR|11|Function argument value, type, or count is invalid."), new("WEEK({^2024-01-01},1,0)", "N|1|1.0000000000"),
        new("CTOBIN('a','N')", "ERR|11|Function argument value, type, or count is invalid."), new("CTOBIN('ab','Y')", "ERR|11|Function argument value, type, or count is invalid."), new("CTOBIN('abcd','Y')", "ERR|11|Function argument value, type, or count is invalid."),
        new("TRANSFORM(1.25,'9.'+REPLICATE('9',40))", "C|1.2500000000000000000000000000000000000000"),

        new("SIGN((-1)^0.5)", "ERR|78|** or ^ domain error."),
        new("(-1)^0.5", "ERR|78|** or ^ domain error."),
        new("EXP(1000)-EXP(1000)", "N|**************|******************************"),
        new("SIGN(EXP(1000)-EXP(1000))", "N|-1|-1.0000000000"),
        new("INT(EXP(1000))", "N|*************|******************************"),
        new("ROUND(EXP(1000),2)", "N|**************|******************************"),
        new("ROUND(1.25,-400)", "N|0|0.0000000000"),
        new("ROUND(1.25,256)", "N|1.250000000000000000|1.2500000000"),
        new("ROUND(1.25,65536)", "N|1.250000000000000000|1.2500000000"),
        new("ROUND(1.25,-65536)", "N|0|0.0000000000"),
        new("ATC('a','abc',10^100)", "ERR|11|Function argument value, type, or count is invalid."),
        new("MLINE('abc',10^100)", "C|"),
        new("MLINE('abc',1,10^100)", "C|abc"),
        new("GETWORDNUM('abc',10^100)", "C|"),
        new("STREXTRACT('abc','a','c',10^100)", "C|"),
        new("STREXTRACT('abc','a','c',1,10^100)", "C|b"),
        new("STRCONV('abc',10^100)", "ERR|11|Function argument value, type, or count is invalid."),
        new("CPCONVERT(10^100,1252,'abc')", "C|abc"),
        new("CPCURRENT(10^100)", "ERR|11|Function argument value, type, or count is invalid."),
        new("SYS(10,10^100)", "C|  /  /    "),
        new("SYS(10,0)", "C|  /  /    "),
        new("SYS(10,5373485)", "C|  /  /    "),
        new("SELECT(10^100)", "N|1|1.0000000000"),
        new("ALIAS(10^100)", "ERR|17|Table number is invalid."),
        new("SET('TEXTMERGE',10^100)", "ERR|11|Function argument value, type, or count is invalid."),
        new("WEEK({^2024-01-01},4,1)", "ERR|11|Function argument value, type, or count is invalid."),
        new("CTOBIN('','N')", "ERR|11|Function argument value, type, or count is invalid."),
        new("CTOBIN('ab','N')", "ERR|11|Function argument value, type, or count is invalid."),
        new("TRANSFORM(1.25,'9.'+REPLICATE('9',29))", "C|1.25000000000000000000000000000"),
        new("5/0", "N|***************|******************************"),
        new("$5/0", "ERR|1988|Currency value is out of range."),
        new("$5%0", "ERR|1307|Cannot divide by 0."),
        new("MOD($5,0)", "ERR|1307|Cannot divide by 0."),
        new("SIGN(0/0)", "N|-1|-1.0000000000"),
        new("SQRT(0/0)", "N|***************|******************************"),
        new("LOG(0/0)", "N|********************|******************************"),
        new("ACOS(0/0)", "N|********************|******************************"),
        new("ASIN(0/0)", "N|********************|******************************"),
        new("INT(0/0)", "N|**********|******************************"),
        new("ROUND(0/0,2)", "N|*************|******************************"),
        new("ROUND(1.25,-2147483647)", "N|0|0.0000000000"),
        new("ROUND(1.25,-2147483646)", "N|0|0.0000000000"),
        new("ROUND(1.25,2147483648)", "N|1|1.0000000000"),
        new("ROUND(1.25,10^100)", "N|1|1.0000000000"),
        new("ROUND($1.25,10^100)", "Y|$1.00|1.0000000000"),
        new("$1%(-10^28)", "Y|$1.00|1.0000000000"),
        new("$1%(-10^20)", "Y|$1.00|1.0000000000"),
        new("$1%(-1000000000000000)", "ERR|1988|Currency value is out of range."),
        new("$1%EXP(1000)", "Y|$0.00|0.0000000000"),
        new("MOD(EXP(1000),2)", "N|0|0.0000000000"),
        new("SPACE(2147483647)", "ERR|1903|String is too long to fit."),
        new("PADL('a',2147483647)", "ERR|11|Function argument value, type, or count is invalid."),
        new("STR(1,2147483647)", "ERR|1908|Width or decimal place argument is invalid."),
        new("LEN(REPLICATE('a',16777184))", "N|16777184|16777184.0000000000"),
        new("LEN(REPLICATE('a',16777185))", "ERR|1903|String is too long to fit."),
        new("SYS(10,1721426)", "C|01/01/0001"),
        new("SYS(10,1721425)", "C|12/31/0000"),
        new("GETWORDNUM('abc',2147483648)", "C|"),
        new("MLINE('abc',1,2147483648)", "C|abc"),
        new("ATC('a','abc',0)", "ERR|11|Function argument value, type, or count is invalid."),
        new("WEEK({^2024-01-01},0,1)", "N|1|1.0000000000"),
        new("ALEN(aa,10^100)", "N|2|2.0000000000"),
        new("ACOPY(aa,bb,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("ADEL(aa,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("AINS(aa,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("AELEMENT(aa,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("ASUBSCRIPT(aa,10^100,1)", "ERR|1234|Subscript is outside defined range."),
        new("ASCAN(aa,1,1,2147483647)", "ERR|1234|Subscript is outside defined range."),
        new("ASORT(aa,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("ALINES(bb,'abc',10^100)", "N|1|1.0000000000"),
        new("aa(10^100)", "ERR|31|Invalid subscript reference."),
        new("MESSAGE(10^100)", "C|"),
        new("LOG10(0/0)", "N|********************|******************************"),
        new("EXP(0/0)", "N|***************|******************************"),
        new("ABS(0/0)", "N|***************|******************************"),
        new("CEILING(0/0)", "N|********************|******************************"),
        new("FLOOR(0/0)", "N|********************|******************************"),
        new("SQRT(EXP(1000))", "N|*************|******************************"),
        new("LOG(EXP(1000))", "N|********************|******************************"),
        new("ATAN(0/0)", "N|********************|******************************"),
        new("COS(EXP(1000))", "N|********************|******************************"),
        new("SIN(EXP(1000))", "N|********************|******************************"),
        new("TAN(EXP(1000))", "N|********************|******************************"),
        new("EXP(1000)+1", "N|**************|******************************"),
        new("EXP(1000)*2", "N|***************|******************************"),
        new("0/0+1", "N|***************|******************************"),
        new("0/0*2", "N|***************|******************************"),
        new("-(0/0)", "N|***************|******************************"),
        new("ROUND(EXP(1000),0)", "N|**************|******************************"),
        new("ROUND(0/0,0)", "N|**********|******************************"),
        new("ROUND(0/0,4)", "N|***************|******************************"),
        new("SYS(10,1)", "C|  /  /    "),
        new("SYS(10,1721059)", "C|  /  /    "),
        new("SYS(10,1721060)", "C|  /  /    "),
        new("ATC('a','abc',2147483648)", "ERR|11|Function argument value, type, or count is invalid."),
        new("ALEN(aa,-2147483648)", "ERR|11|Function argument value, type, or count is invalid."),
        new("ACOPY(aa,bb,1,10^100)", "N|0|0.0000000000"),
        new("ACOPY(aa,bb,1,1,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("ADEL(aa,1,10^100)", "ERR|1230|Too many arguments."),
        new("ASUBSCRIPT(aa,1,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("ASORT(aa,1,10^100)", "N|1|1.0000000000"),
        new("ASORT(aa,1,2147483647)", "N|1|1.0000000000"),
        new("ASCAN(aa,1,10^100)", "ERR|1234|Subscript is outside defined range."),
        new("ALINES(bb,'abc',2147483648)", "ERR|11|Function argument value, type, or count is invalid."),
        new("MESSAGE(2147483648)", "C|"),
        new("SET('FIELDS',10^100)", "C|"),
        new("STREXTRACT('abc','a','c',-2147483648)", "C|"),
        new("NORMALIZE('1e99999999999999999999')", "C|********************"),
        new("ADEL(aa,1,1)", "N|1|1.0000000000"),
        new("AINS(aa,1,1)", "N|1|1.0000000000"),
        new("GETWORDNUM('abc',4294967297)", "C|abc"),
        new("MLINE('abc',1,4294967296)", "C|abc"),
        new("LEN(STR(1,255))", "ERR|1908|Width or decimal place argument is invalid."),
        new("LEN(STR(1,256))", "ERR|1908|Width or decimal place argument is invalid."),
        new("LEN(STR(1,237))", "N|237|237.0000000000"),
        new("LEN(STR(1,238))", "ERR|1908|Width or decimal place argument is invalid."),
        new("VARTYPE(PROGRAM(10^100))", "C|C"),
        new("AUSED(bb,10^100)", "ERR|11|Function argument value, type, or count is invalid."),
        new("AFIELDS(bb,10^100)", "ERR|17|Table number is invalid."),
        new("VARTYPE(SYS(10^100))", "C|C"),
        new("STRTOFILE('x','probe_unused.txt',10^100)", "N|1|1.0000000000"),
        new("ASCAN(aa,1,1,3)", "ERR|1234|Subscript is outside defined range."),
        new("ASORT(aa,1,0)", "N|1|1.0000000000"),
        new("SYS(10,1721061)", "C|  /  /    "),
        new("MOD(0/0,2)", "N|0|0.0000000000"),
        new("MOD(2,0/0)", "N|0|0.0000000000"),
        new("MOD(2,EXP(1000))", "N|0|0.0000000000"),
        new("ASORT(mixed)", "ERR|9|Data type mismatch."),
        new("ASORT(mixedrows)", "ERR|9|Data type mismatch."),
        new("AINS(aa,1,10^100)", "ERR|1230|Too many arguments."),
        new("AELEMENT(aa,0)", "ERR|1234|Subscript is outside defined range."),
        new("ASORT(aa,-(10^100))", "ERR|1234|Subscript is outside defined range."),
        new("ASORT(aa,0)", "ERR|1234|Subscript is outside defined range."),
        new("ASORT(aa,3)", "ERR|1234|Subscript is outside defined range."),
    ];

    internal const string Arrays = "DIMENSION aa[2],bb[2],mixed[2],mixedrows[2,2]\naa[1]=1\naa[2]=2\nmixed[1]=1\nmixed[2]='text'\nmixedrows[1,1]=1\nmixedrows[2,1]='text'";
    internal const string Setup = "CREATE TABLE t FREE (n N(10,2))\nINSERT INTO t VALUES (-1)\nINSERT INTO t VALUES (4)\nUSE";
    internal const string Format = "result=VARTYPE(v)+'|'+TRANSFORM(v)\nIF VARTYPE(v)$'NIYB'\nresult=result+'|'+ALLTRIM(STR(v,30,10))\nENDIF";
    internal static string Script(string expression) => $"{Arrays}\nTRY\nv={expression}\n{Format}\nCATCH TO failure\nresult='ERR|'+TRANSFORM(failure.ErrorNo)+'|'+failure.Message\nENDTRY";
    internal static string AggregateScript(string expression, string command) => Arrays + "\nUSE t\nGO TOP\nTRY\n"
        + (command.StartsWith("CALCULATE") ? $"{command}({expression})" : $"{command} {expression}")
        + " TO v\n" + Format + "\nCATCH TO failure\nresult='ERR|'+TRANSFORM(failure.ErrorNo)+'|'+failure.Message\nENDTRY\nUSE";
    internal static string AggregateExpected(string command) => command is "AVERAGE" or "CALCULATE AVG"
        ? "N|1|1.0000000000" : "N|2|2.0000000000";

    public static IEnumerable<object[]> Cases => Probes.Select(p => new object[] { p.Expression });

    // These are engine built-ins, so verify the shared evaluator and compiled delegate too.
    // Error numbers/messages come from the same live VFP9 observations as the interpreter cases.
    public static IEnumerable<object[]> EngineErrors => Probes.Take(41)
        .Where(p => p.ExpectedVfp.StartsWith("ERR|"))
        .Select(p => new object[] { p.Expression, int.Parse(p.ExpectedVfp.Split('|')[1]), p.ExpectedVfp.Split('|')[2] });

    [Theory]
    [MemberData(nameof(EngineErrors))]
    public void EngineAndCompiledBuiltinErrors_AreVfpErrors(string source, int number, string message)
    {
        var expression = CrossVault.FoxDbf.Expressions.VfpExpression.Parse(source);
        var interpreted = Assert.Throws<CrossVault.FoxDbf.Expressions.ExpressionException>(() => expression.Evaluate(TestRow.Empty));
        var compiled = Assert.Throws<CrossVault.FoxDbf.Expressions.ExpressionException>(() => expression.Compile()(TestRow.Empty));
        Assert.Equal(number, interpreted.VfpErrorNumber);
        Assert.Equal(message, interpreted.Message);
        Assert.Equal(number, compiled.VfpErrorNumber);
        Assert.Equal(message, compiled.Message);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ScalarAndUnchosenAggregateBranches_MatchVfp9(string expression)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round5");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("SET CENTURY ON\n" + Setup + "\n" + Arrays + "\n" + Script(expression));
        Assert.Equal(Probes.Single(p => p.Expression == expression).ExpectedVfp, vfp.Memory.Get("result").AsString);
        foreach (string command in new[] { "SUM", "AVERAGE", "CALCULATE SUM", "CALCULATE AVG" })
        {
            vfp.Execute(AggregateScript($"IIF(.T.,1,{expression})", command));
            Assert.Equal(AggregateExpected(command), vfp.Memory.Get("result").AsString);
        }
    }

    [Theory]
    [InlineData("SUM", "N|1|1.0000000000")]
    [InlineData("AVERAGE", "N|0.50|0.5000000000")]
    [InlineData("CALCULATE SUM", "N|1|1.0000000000")]
    public void SignOfNegativeSqrtInUnchosenBranch(string command, string expectedVfp)
    {
        using var dir = new MicroVfpTestSupport.TempDir("round5sign");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup + "\n" + AggregateScript("IIF(n>=0,SIGN(SQRT(n)),0)", command));
        Assert.Equal(expectedVfp, vfp.Memory.Get("result").AsString);
    }
}
