using System;
using CrossVault.FoxDbf.Expressions;
using static CrossVault.FoxDbf.Tests.Ev;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Edge/adversarial pin-down tests for the VFP/xBase expression engine
/// (<see cref="VfpExpression"/>). Expectations are grounded in the official VFP9
/// help (vfphelp.com) and the Hacker's Guide (hackfox.github.io) and HARD-CODED.
///
/// Key authoritative rulings used below:
///  * SET EXACT OFF: <c>=</c> compares only up to the RIGHT operand's length, so
///    <c>"ABC" = "AB"</c> is .T. but <c>"AB" = "ABC"</c> is .F.
///    (KB Q151635 / Hacker's Guide "=" operator).
///  * <c>==</c> is always exact regardless of EXACT, so <c>"AB" == "ABC"</c> is .F.
///  * SUBSTR/AT/STUFF are 1-based.
///  * MOD/% result takes the sign of the DIVISOR.
///  * INT truncates toward zero.
///  * The string minus operator moves the left operand's trailing blanks to the end.
///  * Three-valued logic: <c>.NULL. .OR. .T.</c> = .T.; <c>.NULL. .AND. .F.</c> = .F.
/// </summary>
public sealed class ExpressionEngineTests
{
    // ====================================================================
    //  ARITHMETIC OPERATORS + PRECEDENCE
    // ====================================================================

    [Theory]
    [InlineData("2+3*4", 14)]      // * binds tighter than +
    [InlineData("(2+3)*4", 20)]    // parens override
    [InlineData("2*3+4", 10)]
    [InlineData("10-2-3", 5)]      // - is left-associative
    [InlineData("10/4", 2.5)]
    [InlineData("-5+3", -2)]       // unary minus
    [InlineData("- -5", 5)]        // double unary minus
    [InlineData("2^3", 8)]         // power
    [InlineData("9^0.5", 3)]       // sqrt via power
    [InlineData("2*-3", -6)]       // unary minus after operator
    public void Arithmetic_PrecedenceAndAssociativity(string expr, double expected)
        => Assert.Equal(expected, Eval(expr).AsDouble, 9);

    [Theory]
    [InlineData("7%3", 1)]
    [InlineData("10%2", 0)]
    [InlineData("-1%3", 2)]     // result takes sign of divisor (VFP MOD)
    [InlineData("1%-3", -2)]    // negative divisor -> negative result
    public void Modulo_ResultTakesSignOfDivisor(string expr, double expected)
        => Assert.Equal(expected, Eval(expr).AsDouble, 9);

    [Fact]
    public void Arithmetic_ResultIsNumeric()
        => Assert.Equal(VfpType.Numeric, Eval("1+1").Type);

    // ====================================================================
    //  STRING CONCATENATION ( + and - )
    // ====================================================================

    [Fact]
    public void Concat_Plus_JoinsVerbatim()
        => Assert.Equal("ABCD", Eval("'AB'+'CD'").AsString);

    [Fact]
    public void Concat_Plus_KeepsInteriorAndTrailingSpaces()
        => Assert.Equal("AB CD", Eval("'AB '+'CD'").AsString);

    [Fact]
    public void Concat_Minus_MovesLeftTrailingBlanksToEnd()
        // "Hello " (one trailing blank) - "World" => "Hello" + "World" + " "
        => Assert.Equal("HelloWorld ", Eval("'Hello '-'World'").AsString);

    [Fact]
    public void Concat_Minus_MultipleTrailingBlanksAllMoved()
        // "AB  " has two trailing blanks -> "AB"+"CD"+"  "
        => Assert.Equal("ABCD  ", Eval("'AB  '-'CD'").AsString);

    // ====================================================================
    //  COMPARISON OPERATORS + SET EXACT
    // ====================================================================

    [Theory]
    [InlineData("1<2", true)]
    [InlineData("2<2", false)]
    [InlineData("2<=2", true)]
    [InlineData("3>2", true)]
    [InlineData("2>=3", false)]
    [InlineData("2=2", true)]
    [InlineData("2<>3", true)]
    [InlineData("2!=3", true)]
    [InlineData("2#3", true)]   // # is "not equal"
    [InlineData("2#2", false)]
    public void Comparison_NumericOperators(string expr, bool expected)
        => Assert.Equal(expected, Eval(expr).AsLogical);

    [Fact]
    public void Exact_Off_RightSideGovernsLength_PrefixMatches()
    {
        // EXACT OFF: compare up to RIGHT operand's length.
        Assert.True(Eval("'ABC' = 'AB'", ctx: ExactOff).AsLogical);   // right "AB" is prefix of left
        Assert.True(Eval("'Smithsonian' = 'Smith'", ctx: ExactOff).AsLogical);
    }

    [Fact]
    public void Exact_Off_LeftShorterThanRight_IsFalse()
        => Assert.False(Eval("'AB' = 'ABC'", ctx: ExactOff).AsLogical);

    [Fact]
    public void Exact_Off_EmptyRightAlwaysMatches()
        => Assert.True(Eval("'anything' = ''", ctx: ExactOff).AsLogical);

    [Fact]
    public void Exact_On_RequiresFullMatchModuloTrailingBlanks()
    {
        Assert.False(Eval("'ABC' = 'AB'", ctx: ExactOn).AsLogical);
        Assert.True(Eval("'AB' = 'AB'", ctx: ExactOn).AsLogical);
    }

    [Fact]
    public void DoubleEquals_AlwaysExact_IgnoresExactSetting()
    {
        Assert.False(Eval("'AB' == 'ABC'", ctx: ExactOff).AsLogical);
        Assert.False(Eval("'ABC' == 'AB'", ctx: ExactOff).AsLogical);
        Assert.True(Eval("'AB' == 'AB'", ctx: ExactOff).AsLogical);
    }

    // ====================================================================
    //  DOLLAR (contains) OPERATOR
    // ====================================================================

    [Fact]
    public void Dollar_LeftContainedInRight_IsTrue()
        => Assert.True(Eval("'CD' $ 'ABCDEF'").AsLogical);

    [Fact]
    public void Dollar_NotContained_IsFalse()
        => Assert.False(Eval("'x' $ 'ABC'").AsLogical);

    // ====================================================================
    //  LOGICAL OPERATORS + PRECEDENCE + SHORT CIRCUIT
    // ====================================================================

    [Theory]
    [InlineData(".T. .AND. .F.", false)]
    [InlineData(".T. .OR. .F.", true)]
    [InlineData(".NOT. .F.", true)]
    [InlineData("!.F.", true)]                 // bang as NOT
    [InlineData(".T. AND .T.", true)]          // keyword form without dots
    [InlineData(".F. OR .T.", true)]
    [InlineData("NOT .T.", false)]
    public void Logical_TruthTable(string expr, bool expected)
        => Assert.Equal(expected, Eval(expr).AsLogical);

    [Fact]
    public void Logical_Precedence_NotBeforeAndBeforeOr()
        // .T. .OR. .F. .AND. .F.  ==  .T. .OR. (.F. .AND. .F.)  ==  .T.
        => Assert.True(Eval(".T. .OR. .F. .AND. .F.").AsLogical);

    [Fact]
    public void Logical_Not_BindsTighterThanAnd()
        // .NOT. .F. .AND. .T.  ==  (.NOT. .F.) .AND. .T.  ==  .T.
        => Assert.True(Eval(".NOT. .F. .AND. .T.").AsLogical);

    // ====================================================================
    //  NULL PROPAGATION (three-valued logic)
    // ====================================================================

    [Fact]
    public void Null_ArithmeticPropagates()
        => Assert.True(Eval(".NULL. + 1").IsNull);

    [Fact]
    public void Null_ComparisonProducesNull()
        => Assert.True(Eval("1 = .NULL.").IsNull);

    [Fact]
    public void Null_Or_True_IsTrue()
    {
        var v = Eval(".NULL. .OR. .T.");
        Assert.False(v.IsNull);
        Assert.True(v.AsLogical);
    }

    [Fact]
    public void Null_And_False_IsFalse()
    {
        var v = Eval(".NULL. .AND. .F.");
        Assert.False(v.IsNull);
        Assert.False(v.AsLogical);
    }

    [Fact]
    public void Null_And_True_IsNull()
        => Assert.True(Eval(".NULL. .AND. .T.").IsNull);

    [Fact]
    public void Null_StringFunctionPropagates()
        => Assert.True(Eval("UPPER(.NULL.)").IsNull);

    // ====================================================================
    //  STRING FUNCTIONS  (vfphelp / hackfox)
    // ====================================================================

    [Theory]
    [InlineData("UPPER('abc')", "ABC")]
    [InlineData("LOWER('AbC')", "abc")]
    [InlineData("ALLTRIM('  ab  ')", "ab")]
    [InlineData("TRIM('ab  ')", "ab")]
    [InlineData("RTRIM('ab  ')", "ab")]
    [InlineData("LTRIM('  ab')", "ab")]
    [InlineData("LEFT('ABCDEF',3)", "ABC")]
    [InlineData("RIGHT('ABCDEF',2)", "EF")]
    [InlineData("SUBSTR('ABCDEF',2,3)", "BCD")]   // 1-based start
    [InlineData("SUBSTR('ABCDEF',4)", "DEF")]     // to end
    [InlineData("STUFF('ABCDEF',2,3,'xy')", "AxyEF")]
    [InlineData("CHRTRAN('ABCDEF','ACE','xyz')", "xByDzF")]
    [InlineData("CHRTRAN('ABC','AB','x')", "xC")] // shorter replacement deletes
    [InlineData("STRTRAN('before'+CHR(0)+'after'+CHR(0),CHR(0),'')", "beforeafter")]
    [InlineData("REPLICATE('ab',3)", "ababab")]
    [InlineData("SPACE(3)", "   ")]
    [InlineData("PADL('7',3,'0')", "007")]
    [InlineData("PADR('7',3)", "7  ")]
    [InlineData("PADC('7',3)", " 7 ")]
    [InlineData("CHR(65)", "A")]
    public void StringFunctions_ReturnExpected(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    [Theory]
    [InlineData("PADL('abcdef',3)")]
    [InlineData("PADR('abcdef',3)")]
    [InlineData("PADC('abcdef',3)")]
    public void Pad_OverlongSource_TruncatesFromRight(string expression)
    {
        var parsed = VfpExpression.Parse(expression);
        Assert.Equal("abc", parsed.Evaluate(TestRow.Empty).AsString);
        Assert.Equal("abc", parsed.Compile()(TestRow.Empty).AsString);
    }

    [Theory]
    [InlineData("AT('CD','ABCDEF')", 3)]      // 1-based
    [InlineData("AT('x','ABC')", 0)]          // not found -> 0
    [InlineData("AT('A','ABABA',2)", 3)]      // 2nd occurrence
    [InlineData("RAT('A','ABABA')", 5)]       // last occurrence
    [InlineData("LEN('abc')", 3)]
    [InlineData("ASC('A')", 65)]
    public void StringFunctions_ReturnNumeric(string expr, int expected)
        => Assert.Equal(expected, Eval(expr).AsInteger);

    [Theory]
    [InlineData("ATC('sm','Smith')", 1)]
    [InlineData("ATC('SM','smith')", 1)]
    [InlineData("ATC('x','Smith')", 0)]
    [InlineData("ATC('','Smith')", 0)]
    [InlineData("ATC('b','aBcb',2)", 4)]
    public void Atc_IsCaseInsensitive_InEvaluateAndCompile(string expression, int expected)
    {
        var parsed = VfpExpression.Parse(expression);
        Assert.Equal(expected, parsed.Evaluate(TestRow.Empty).AsInteger);
        Assert.Equal(expected, parsed.Compile()(TestRow.Empty).AsInteger);
    }

    // STR: width + decimals, right-justified, padded with leading spaces.
    [Theory]
    [InlineData("STR(123,5)", "  123")]
    [InlineData("STR(3.14159,5,2)", " 3.14")]
    [InlineData("STR(3.6,2)", " 4")]            // rounds when no decimals
    public void Str_FormatsWithWidthAndDecimals(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    [Theory]
    [InlineData("STRZERO(45,5)", "00045")]
    [InlineData("STRZERO(7,3)", "007")]
    public void Strzero_PadsWithLeadingZeros(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    [Theory]
    [InlineData("VAL('12.5')", 12.5)]
    [InlineData("VAL('12abc')", 12)]   // parses leading numeric
    [InlineData("VAL('abc')", 0)]      // non-numeric -> 0
    public void Val_ParsesLeadingNumber(string expr, double expected)
        => Assert.Equal(expected, Eval(expr).AsDouble, 9);

    // ====================================================================
    //  DATE / CONVERSION FUNCTIONS
    // ====================================================================

    [Fact]
    public void Dtos_FormatsToEightDigits()
        => Assert.Equal("20200303", Eval("DTOS({^2020-03-03})").AsString);

    [Theory]
    [InlineData("DAY({^2020-03-03})", 3)]
    [InlineData("MONTH({^2020-03-03})", 3)]
    [InlineData("YEAR({^2020-03-03})", 2020)]
    [InlineData("DOW({^2020-03-03})", 3)]   // Tuesday, Sunday=1
    public void DateParts_AreExtracted(string expr, int expected)
        => Assert.Equal(expected, Eval(expr).AsInteger);

    [Fact]
    public void Cdow_ReturnsWeekdayName()
        => Assert.Equal("Tuesday", Eval("CDOW({^2020-03-03})").AsString);

    [Fact]
    public void Cmonth_ReturnsMonthName()
        => Assert.Equal("March", Eval("CMONTH({^2020-03-03})").AsString);

    [Fact]
    public void Gomonth_AddsMonths_ClampingEndOfMonth()
    {
        Assert.Equal(new DateOnly(2020, 2, 15), Eval("GOMONTH({^2020-01-15},1)").AsDate);
        // Jan 31 + 1 month -> Feb 29 (2020 is a leap year)
        Assert.Equal(new DateOnly(2020, 2, 29), Eval("GOMONTH({^2020-01-31},1)").AsDate);
    }

    [Theory]
    [InlineData("{} + 1")]
    [InlineData("1 + {}")]
    [InlineData("{} + -1")]
    [InlineData("-1 + {}")]
    [InlineData("{} - 1")]
    [InlineData("{} - -1")]
    public void EmptyDate_Arithmetic_PreservesTypedEmpty(string expression)
        => AssertTypedEmptyBoth(expression, TestRow.Empty, VfpType.Date);

    [Theory]
    [InlineData("EMPTYDT + 1")]
    [InlineData("1 + EMPTYDT")]
    [InlineData("EMPTYDT + -1")]
    [InlineData("-1 + EMPTYDT")]
    [InlineData("EMPTYDT - 1")]
    [InlineData("EMPTYDT - -1")]
    public void EmptyDateTime_Arithmetic_PreservesTypedEmpty(string expression)
        => AssertTypedEmptyBoth(
            expression, new TestRow().Set("EMPTYDT", DateTime.MinValue), VfpType.DateTime);

    [Theory]
    [InlineData("GOMONTH({}, 1)", VfpType.Date)]
    [InlineData("GOMONTH({}, -1)", VfpType.Date)]
    [InlineData("GOMONTH(EMPTYDT, 1)", VfpType.Date)]
    [InlineData("GOMONTH(EMPTYDT, -1)", VfpType.Date)]
    public void Gomonth_EmptyDateAndDateTime_PreserveTypedEmpty(string expression, VfpType type)
        => AssertTypedEmptyBoth(
            expression, new TestRow().Set("EMPTYDT", DateTime.MinValue), type);

    [Fact]
    public void DateAndDateTime_BoundaryOverflow_ReturnsTypedEmpty()
    {
        var row = new TestRow()
            .Set("DLOW", DateOnly.MinValue.AddDays(1))
            .Set("DMAX", DateOnly.MaxValue)
            .Set("TLOW", DateTime.MinValue.AddSeconds(1))
            .Set("TMAX", DateTime.MaxValue);

        AssertTypedEmptyBoth("DMAX + 1", row, VfpType.Date);
        AssertTypedEmptyBoth("1 + DMAX", row, VfpType.Date);
        AssertTypedEmptyBoth("DLOW - 2", row, VfpType.Date);
        AssertTypedEmptyBoth("GOMONTH(DMAX, 1)", row, VfpType.Date);
        AssertTypedEmptyBoth("GOMONTH(DLOW, -1)", row, VfpType.Date);
        AssertTypedEmptyBoth("TMAX + 1", row, VfpType.DateTime);
        AssertTypedEmptyBoth("1 + TMAX", row, VfpType.DateTime);
        AssertTypedEmptyBoth("TLOW - 2", row, VfpType.DateTime);
        AssertTypedEmptyBoth("GOMONTH(TMAX, 1)", row, VfpType.DateTime);
        AssertTypedEmptyBoth("GOMONTH(TLOW, -1)", row, VfpType.DateTime);
    }

    [Fact]
    public void DateDifferencesAndNumericMinusDate_RemainNumeric()
    {
        var row = new TestRow()
            .Set("T1", new DateTime(2020, 1, 1, 0, 0, 0))
            .Set("T2", new DateTime(2020, 1, 1, 0, 1, 0));

        AssertNumericBoth("{^2020-01-02} - {^2020-01-01}", TestRow.Empty, 1);
        AssertNumericBoth("T2 - T1", row, 60);
        AssertNumericBoth("1 - {}", TestRow.Empty, 1);
    }

    [Fact]
    public void Ctod_ParsesDate()
        => Assert.Equal(new DateOnly(2020, 3, 3), Eval("CTOD('03/03/2020')").AsDate);

    // ====================================================================
    //  NUMERIC FUNCTIONS
    // ====================================================================

    [Theory]
    [InlineData("INT(3.9)", 3)]
    [InlineData("INT(-3.9)", -3)]   // truncates toward zero
    [InlineData("ABS(-5)", 5)]
    [InlineData("MOD(10,3)", 1)]
    [InlineData("MOD(-1,3)", 2)]    // sign of divisor
    [InlineData("MOD(1,-3)", -2)]
    [InlineData("MAX(1,2,3)", 3)]
    [InlineData("MIN(3,1,2)", 1)]
    public void NumericFunctions_Integers(string expr, double expected)
        => Assert.Equal(expected, Eval(expr).AsDouble, 9);

    [Theory]
    [InlineData("ROUND(3.14159,2)", 3.14)]
    [InlineData("ROUND(3.146,2)", 3.15)]
    [InlineData("ROUND(12345,-2)", 12300)]   // negative places round integer part
    public void Round_RoundsToDecimalPlaces(string expr, double expected)
        => Assert.Equal(expected, Eval(expr).AsDouble, 9);

    // ====================================================================
    //  SYSTEM / LOGICAL FUNCTIONS
    // ====================================================================

    [Theory]
    [InlineData("EMPTY('')", true)]
    [InlineData("EMPTY('  ')", true)]     // blanks are empty
    [InlineData("EMPTY('x')", false)]
    [InlineData("EMPTY(0)", true)]
    [InlineData("EMPTY(5)", false)]
    public void Empty_DetectsEmptyValues(string expr, bool expected)
        => Assert.Equal(expected, Eval(expr).AsLogical);

    [Theory]
    [InlineData("ISNULL(.NULL.)", true)]
    [InlineData("ISNULL('x')", false)]
    public void IsNull_DetectsNull(string expr, bool expected)
        => Assert.Equal(expected, Eval(expr).AsLogical);

    [Theory]
    [InlineData("IIF(.T.,'a','b')", "a")]
    [InlineData("IIF(.F.,'a','b')", "b")]
    [InlineData("IIF(1<2,'a','b')", "a")]
    public void Iif_SelectsBranch(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    [Fact]
    public void Iif_Evaluate_EvaluatesOnlySelectedBranch()
    {
        var expression = VfpExpression.Parse("IIF(.T., 1, BUMP())");
        var row = new BumpRow();

        Assert.Equal(1, expression.Evaluate(row).AsInteger);
        Assert.Equal(0, row.BumpCount);

        expression = VfpExpression.Parse("IIF(.NULL., 1, BUMP())");
        Assert.Equal(99, expression.Evaluate(row).AsInteger);
        Assert.Equal(1, row.BumpCount);
    }

    [Fact]
    public void Iif_Compile_EvaluatesOnlySelectedBranch()
    {
        var row = new BumpRow();

        Assert.Equal(1, VfpExpression.Parse("IIF(.T., 1, BUMP())").Compile()(row).AsInteger);
        Assert.Equal(0, row.BumpCount);

        Assert.Equal(99, VfpExpression.Parse("IIF(.F., 1, BUMP())").Compile()(row).AsInteger);
        Assert.Equal(1, row.BumpCount);
    }

    [Fact]
    public void Iif_EvaluateWithFunctionResolver_OffersIifThenEvaluatesOnlySelectedBranch()
    {
        int iifRefusals = 0;
        int bumps = 0;
        ExpressionFunctionResolver resolver =
            (string name, IReadOnlyList<ExpressionArgument> _, out VfpValue result) =>
            {
                if (name == "IIF") iifRefusals++;
                if (name == "BUMP")
                {
                    bumps++;
                    result = VfpValue.Integer(99);
                    return true;
                }

                result = VfpValue.Null;
                return false;
            };

        var result = VfpExpression.Parse("IIF(.T., 1, BUMP())").EvaluateWithFunctionResolver(
            TestRow.Empty, EvaluationContext.Default, resolver);

        Assert.Equal(1, result.AsInteger);
        Assert.Equal(1, iifRefusals);
        Assert.Equal(0, bumps);
    }

    [Fact]
    public void Iif_InvalidArgumentCount_ReportsVfp9SyntaxError()
    {
        // FunctionArityVfp9OracleTests pins error 1300 before any arguments can run.
        Assert.Equal(1300, Assert.Throws<ExpressionException>(() =>
            VfpExpression.Parse("IIF(.T., BUMP(), BUMP(), 4)")).VfpErrorNumber);
    }

    [Theory]
    [InlineData("BETWEEN(5,1,10)", true)]
    [InlineData("BETWEEN(1,1,10)", true)]   // inclusive
    [InlineData("BETWEEN(0,1,10)", false)]
    public void Between_IsInclusive(string expr, bool expected)
        => Assert.Equal(expected, Eval(expr).AsLogical);

    [Theory]
    [InlineData("INLIST(2,1,2,3)", true)]
    [InlineData("INLIST(9,1,2,3)", false)]
    [InlineData("INLIST('b','a','b','c')", true)]
    public void Inlist_ChecksMembership(string expr, bool expected)
        => Assert.Equal(expected, Eval(expr).AsLogical);

    [Fact]
    public void Recno_Deleted_ReadFromRow()
    {
        var row = new TestRow(recNo: 42, deleted: true);
        Assert.Equal(42, Eval("RECNO()", row).AsInteger);
        Assert.True(Eval("DELETED()", row).AsLogical);
    }

    // ====================================================================
    //  FIELDS + FUNCTIONS over a row
    // ====================================================================

    [Fact]
    public void Field_Character_UpperAndConcat()
    {
        var row = new TestRow().Set("NAME", "alice").Set("CITY", "NY");
        Assert.Equal("ALICE", Eval("UPPER(NAME)", row).AsString);
        Assert.Equal("aliceNY", Eval("NAME+CITY", row).AsString);
    }

    [Fact]
    public void Field_Numeric_Arithmetic()
    {
        var row = new TestRow().Set("QTY", 5m).Set("PRICE", 2m);
        Assert.Equal(10, Eval("QTY*PRICE", row).AsDouble, 9);
    }

    [Fact]
    public void Field_NullValuePropagates()
    {
        var row = new TestRow().Set("X", null);
        Assert.True(Eval("X+1", row).IsNull);
    }

    // ====================================================================
    //  CASE-INSENSITIVITY + LITERALS
    // ====================================================================

    [Fact]
    public void Keywords_And_Functions_AreCaseInsensitive()
    {
        Assert.Equal("ABC", Eval("upper('abc')").AsString);
        Assert.Equal("ABC", Eval("UpPeR('abc')").AsString);
        Assert.True(Eval(".t. .and. .T.").AsLogical);
    }

    [Fact]
    public void Literal_SingleAndDoubleQuotedStrings()
    {
        Assert.Equal("hi", Eval("'hi'").AsString);
        Assert.Equal("hi", Eval("\"hi\"").AsString);
    }

    [Fact]
    public void Literal_NumericTypes()
    {
        Assert.Equal(VfpType.Numeric, Eval("3.14").Type);
        Assert.Equal(3.14, Eval("3.14").AsDouble, 9);
        Assert.Equal(42, Eval("42").AsInteger);
    }

    [Fact]
    public void Literal_DateAndDateTime()
    {
        var d = Eval("{^2020-03-03}");
        Assert.Equal(VfpType.Date, d.Type);
        Assert.Equal(new DateOnly(2020, 3, 3), d.AsDate);

        var dt = Eval("{^2020-03-03 10:30:00}");
        Assert.Equal(VfpType.DateTime, dt.Type);
        Assert.Equal(new DateTime(2020, 3, 3, 10, 30, 0), dt.AsDateTime);
    }

    [Fact]
    public void Literal_LogicalAndNull()
    {
        Assert.True(Eval(".T.").AsLogical);
        Assert.False(Eval(".F.").AsLogical);
        Assert.True(Eval(".NULL.").IsNull);
    }

    // ====================================================================
    //  PARSE ERRORS  -> ExpressionException
    // ====================================================================

    [Theory]
    [InlineData("1 +")]          // dangling operator
    [InlineData("(1 + 2")]       // unbalanced paren
    [InlineData("UPPER(")]       // unterminated call
    [InlineData("'unterminated")]// unterminated string
    [InlineData("@#$")]          // garbage
    [InlineData("")]             // empty
    public void Parse_Malformed_ThrowsExpressionException(string expr)
        => Assert.Throws<ExpressionException>(() => VfpExpression.Parse(expr));

    [Fact]
    public void Evaluate_NeverThrows_OnNullField()
    {
        // A well-formed expression over a .NULL. field must not throw.
        var row = new TestRow().Set("X", null);
        var ex = Record.Exception(() => Eval("X + 1 > 0 .AND. UPPER(X) = 'A'", row));
        Assert.Null(ex);
    }

    // ====================================================================
    //  TYPE INFERENCE
    // ====================================================================

    [Fact]
    public void InferType_UpperOfCharField_IsCharacterSameLength()
    {
        var schema = new TestSchema().Add("NAME", 'C', 10);
        var t = VfpExpression.Parse("UPPER(NAME)").InferType(schema);
        Assert.Equal(VfpType.Character, t.Type);
        Assert.Equal(10, t.Length);
    }

    [Fact]
    public void InferType_DtosOfDate_IsCharacter8()
    {
        var schema = new TestSchema().Add("D", 'D', 8);
        var t = VfpExpression.Parse("DTOS(D)").InferType(schema);
        Assert.Equal(VfpType.Character, t.Type);
        Assert.Equal(8, t.Length);
    }

    [Fact]
    public void InferType_NumericPlusOne_IsNumeric()
    {
        var schema = new TestSchema().Add("N", 'N', 10, 2);
        var t = VfpExpression.Parse("N + 1").InferType(schema);
        Assert.Equal(VfpType.Numeric, t.Type);
    }

    [Fact]
    public void InferType_CharConcatStr_AddsLengths()
    {
        // NAME (C10) + STR(N,4) -> Character length 14
        var schema = new TestSchema().Add("NAME", 'C', 10).Add("N", 'N', 8, 0);
        var t = VfpExpression.Parse("NAME + STR(N,4)").InferType(schema);
        Assert.Equal(VfpType.Character, t.Type);
        Assert.Equal(14, t.Length);
    }

    // ====================================================================
    //  EXECUTION EQUIVALENCE  (compiled delegate == interpreter)
    // ====================================================================

    [Theory]
    [InlineData("2+3*4")]
    [InlineData("(2+3)*4")]
    [InlineData("2^3 + 1")]
    [InlineData("10 % 3")]
    [InlineData("'AB'+'CD'")]
    [InlineData("'Hello '-'World'")]
    [InlineData("UPPER('abc') + STR(123,5)")]
    [InlineData("SUBSTR('ABCDEF',2,3)")]
    [InlineData("'CD' $ 'ABCDEF'")]
    [InlineData("1 < 2 .AND. 3 >= 3")]
    [InlineData(".NULL. .OR. .T.")]
    [InlineData("IIF(1<2,'a','b')")]
    [InlineData("BETWEEN(5,1,10)")]
    [InlineData("DTOS({^2020-03-03})")]
    [InlineData("ROUND(3.14159,2)")]
    public void CompiledDelegate_MatchesInterpreter(string expr)
    {
        var parsed = VfpExpression.Parse(expr);
        var ctx = EvaluationContext.Default;
        var interpreted = parsed.Evaluate(TestRow.Empty, ctx);
        var compiled = parsed.Compile(ctx)(TestRow.Empty);
        Assert.Equal(interpreted, compiled);
    }

    // ====================================================================
    //  PERFORMANCE: parse once, compile once, evaluate a big loop
    // ====================================================================

    [Fact]
    public void Compiled_Reused_OverManyRecords_NoReparse()
    {
        var parsed = VfpExpression.Parse("UPPER(NAME) + STR(N,4)");
        var fn = parsed.Compile(EvaluationContext.Default);
        var row = new TestRow().Set("NAME", "ab").Set("N", 7m);

        VfpValue last = default;
        for (int i = 0; i < 100_000; i++)
            last = fn(row);

        Assert.Equal("AB   7", last.AsString); // STR(7,4) => "   7"
    }

    private sealed class BumpRow : IRowContext, IVfpFunctionHost
    {
        public int BumpCount { get; private set; }
        public object? GetField(string name) => null;
        public int RecNo => 1;
        public bool Deleted => false;
        public int RecCount => 0;

        public bool TryInvoke(
            string upperName, VfpValue[] args, EvaluationContext ctx, out VfpValue result)
        {
            if (upperName == "BUMP")
            {
                BumpCount++;
                result = VfpValue.Integer(99);
                return true;
            }

            result = VfpValue.Null;
            return false;
        }
    }

    private static void AssertTypedEmptyBoth(string expression, IRowContext row, VfpType expectedType)
    {
        var parsed = VfpExpression.Parse(expression);
        AssertTypedEmpty(parsed.Evaluate(row), expectedType);
        AssertTypedEmpty(parsed.Compile()(row), expectedType);
    }

    private static void AssertTypedEmpty(VfpValue value, VfpType expectedType)
    {
        Assert.Equal(expectedType, value.Type);
        if (expectedType == VfpType.Date)
            Assert.Equal(default, value.AsDate);
        else
            Assert.Equal(default, value.AsDateTime);
    }

    private static void AssertNumericBoth(string expression, IRowContext row, double expected)
    {
        var parsed = VfpExpression.Parse(expression);
        Assert.Equal(expected, parsed.Evaluate(row).AsDouble);
        Assert.Equal(expected, parsed.Compile()(row).AsDouble);
    }
}
