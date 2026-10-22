using System;
using CrossVault.FoxDbf.Expressions;
using static CrossVault.FoxDbf.Tests.Ev;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Pin-down tests for the GENERAL collation (head/tail key bytes, ligature expansion,
/// collation-aware equality / <c>$</c> / INLIST) and for the function edge cases fixed
/// in this batch: STR() overflow shrink-decimals, STRZERO() sign handling, DTOS/DTOC on
/// an empty date, CHR()/ASC() code-page semantics, RECCOUNT(), LEFT/RIGHT length
/// inference, and the lexer's qualified-name + bracket-string support.
/// </summary>
public sealed class ExpressionCollationAndEdgeTests
{
    private static EvaluationContext General => new() { Collation = VfpCollations.General };

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    // ====================================================================
    //  GENERAL collation: byte-exact head/tail keys (validated vs real CDX)
    // ====================================================================

    [Theory]
    [InlineData("ADMIN", "60646F6A70")]                 // A/I tails (0) trimmed away
    [InlineData("JMÖLCK", "6B6F726D626C04")]            // Ö tail 0x04 kept, C tail 0 trimmed
    [InlineData("ARÖTTGER", "60757277776866750004")]    // interior 0x00 tail (A) kept before Ö
    [InlineData("MGÖZEL", "6F68727E666D04")]
    public void GeneralKey_HeadThenTail_MatchesReference(string s, string expectedHex)
        => Assert.Equal(expectedHex, Hex(VfpCollations.General.GetCollatedKey(s.AsSpan())));

    [Fact]
    public void GeneralKey_TrimsTrailingSpaces_BeforeKeying()
        => Assert.Equal(Hex(VfpCollations.General.GetCollatedKey("ADMIN".AsSpan())),
                        Hex(VfpCollations.General.GetCollatedKey("ADMIN   ".AsSpan())));

    [Theory]
    [InlineData("ß", "7676")]    // ß -> "SS" (head 118,118; tails 0,0 trimmed)
    [InlineData("Œ", "7266")]    // Œ -> "OE"
    [InlineData("Æ", "6066")]    // Æ -> "AE"
    [InlineData("Þ", "7769")]    // Þ -> "TH"
    public void GeneralKey_LigaturesExpand_InHeadPass(string s, string expectedHex)
        => Assert.Equal(expectedHex, Hex(VfpCollations.General.GetCollatedKey(s.AsSpan())));

    [Fact]
    public void GeneralKey_SingleAccent_EmitsTailByte()
        // Ö -> head 0x72 (O) + tail 0x04.
        => Assert.Equal("7204", Hex(VfpCollations.General.GetCollatedKey("Ö".AsSpan())));

    [Fact]
    public void MachineKey_IsRawCp1252Bytes()
        // A = 0x41, Ä = CP1252 0xC4 (no folding, no tail).
        => Assert.Equal("41C4", Hex(VfpCollations.Machine.GetCollatedKey("AÄ".AsSpan())));

    [Fact]
    public void Collation_ByName_ResolvesGeneralAndFallsBackToMachine()
    {
        Assert.Equal("GENERAL", VfpCollations.ByName("general").Name);
        Assert.Equal("MACHINE", VfpCollations.ByName("nonsense").Name);
        Assert.Equal("MACHINE", VfpCollations.ByName(null).Name);
    }

    // ====================================================================
    //  GENERAL collation: ordering consistency with the keys
    // ====================================================================

    [Fact]
    public void General_CaseFolds_InOrdering()
    {
        Assert.Equal(0, VfpCollations.General.Compare("ABC".AsSpan(), "abc".AsSpan()));
        // primary order a < b regardless of case
        Assert.True(VfpCollations.General.Compare("a".AsSpan(), "B".AsSpan()) < 0);
    }

    [Fact]
    public void General_AccentsSortViaTail_AfterUnaccented()
        // "cote" and "côte" share primary weights; the tail (accent) breaks the tie.
        => Assert.True(VfpCollations.General.Compare("cote".AsSpan(), "côte".AsSpan()) < 0);

    [Fact]
    public void Machine_IsCaseSensitiveOrdinal()
        => Assert.True(VfpCollations.Machine.Compare("a".AsSpan(), "B".AsSpan()) > 0); // 0x61 > 0x42

    // ====================================================================
    //  GENERAL collation routed through =, ==, $, INLIST, <
    // ====================================================================

    [Fact]
    public void General_Equality_IsCaseAndAccentInsensitive()
    {
        Assert.True(Eval("'A' = 'a'", ctx: General).AsLogical);
        Assert.True(Eval("'A' == 'a'", ctx: General).AsLogical);   // == also collation-aware
        // Case folds, accents preserved (the diacritic tail must still match).
        Assert.True(Eval("'CAFÉ' == 'café'", ctx: General).AsLogical);
        // Differing accents are NOT equal (é carries a tail byte, e does not).
        Assert.False(Eval("'café' == 'cafe'", ctx: General).AsLogical);
    }

    [Fact]
    public void Machine_Equality_StaysCaseSensitive()
    {
        Assert.False(Eval("'A' = 'a'", ctx: ExactOff).AsLogical);
        Assert.False(Eval("'A' == 'a'", ctx: ExactOff).AsLogical);
    }

    [Fact]
    public void General_Dollar_IsCaseInsensitive()
    {
        Assert.True(Eval("'A' $ 'banana'", ctx: General).AsLogical);   // 'a' inside
        Assert.False(Eval("'A' $ 'banana'", ctx: ExactOff).AsLogical); // ordinal: no 'A'
    }

    [Fact]
    public void General_Inlist_IsCaseInsensitive()
    {
        Assert.True(Eval("INLIST('a','A','B','C')", ctx: General).AsLogical);
        Assert.False(Eval("INLIST('a','A','B','C')", ctx: ExactOff).AsLogical);
    }

    [Fact]
    public void General_LessThan_UsesPrimaryWeights()
        => Assert.True(Eval("'apple' < 'Banana'", ctx: General).AsLogical);

    // ====================================================================
    //  Expansion chars (Æ/ß/Œ/Þ emit TWO weights): = (EXACT OFF) and $ must
    //  compare on collated-KEY length, not char length.
    // ====================================================================

    private static EvaluationContext GeneralExactOff => new() { Collation = VfpCollations.General, Exact = false };

    [Fact]
    public void StrEqInexact_General_ExpansionChar_MatchesByWeightNotCharCount()
    {
        // "Æ" is ONE char but TWO weights (A,E). Slicing the left by the right's CHAR
        // count would compare "A" vs "Æ" and fail; comparing by collated-key length,
        // the weights of "Æ" are exactly the prefix weights of "AEtest" -> equal.
        Assert.True(VfpRuntime.StrEqInexact("AEtest", "Æ", GeneralExactOff));
        Assert.True(VfpRuntime.StrEqInexact("STRAßENAME", "STRASSE", GeneralExactOff)); // ß -> SS
        // And via the public '=' operator (EXACT OFF is the VFP default).
        Assert.True(Eval("'AEtest' = 'Æ'", ctx: GeneralExactOff).AsLogical);
        Assert.True(Eval("'Ætest' = 'AE'", ctx: GeneralExactOff).AsLogical);
        // The right operand still has to be a (weight) PREFIX of the left.
        Assert.False(VfpRuntime.StrEqInexact("Æ", "AEtest", GeneralExactOff));
    }

    [Fact]
    public void Contains_General_ExpansionChar_MatchesByWeightRun()
    {
        // "AE" (2 weights) appears as a contiguous weight run inside "Ætest" (Æ->A,E,...).
        Assert.True(VfpRuntime.Contains("AE", "Ætest", GeneralExactOff));
        Assert.True(VfpRuntime.Contains("Æ", "an AE example", GeneralExactOff)); // symmetric direction
        Assert.True(VfpRuntime.Contains("SS", "STRAßE", GeneralExactOff));       // ß -> SS run
        // Via the public '$' operator.
        Assert.True(Eval("'AE' $ 'Ætest'", ctx: General).AsLogical);
        Assert.True(Eval("'Æ' $ 'an AE example'", ctx: General).AsLogical);
        // A needle whose weights do NOT occur contiguously stays false.
        Assert.False(VfpRuntime.Contains("EA", "Ætest", GeneralExactOff));
    }

    [Fact]
    public void Contains_General_CollatesNeedleConsistently_AcrossWindow()
    {
        // Needle longer than haystack -> false fast; case-folding still applies per weight.
        Assert.False(VfpRuntime.Contains("Æx", "Æ", GeneralExactOff));
        Assert.True(VfpRuntime.Contains("ae", "xxÆyy", GeneralExactOff)); // case-insensitive run
    }

    // ====================================================================
    //  STR() overflow: shrink decimals, asterisks only when integer won't fit
    // ====================================================================

    [Theory]
    [InlineData("STR(1234.5,6,2)", "1234.5")]  // shrinks dec 2->1 to fit, NOT "******"
    [InlineData("STR(1234.5,7,2)", "1234.50")] // fits with 2 decimals
    [InlineData("STR(1234.56,6,2)", "1234.6")] // shrink to 1 dec, rounded
    [InlineData("STR(12.5,4,2)", "12.5")]      // shrink dec to fit width 4
    public void Str_ReducesDecimalsToFit(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    [Theory]
    [InlineData("STR(123.4,3,2)", "***")]      // dec>0 and width <= int digits -> asterisks
    [InlineData("STR(1234.5,4,2)", "****")]    // can't fit int part + a decimal -> asterisks
    [InlineData("STR(12345,4)", "****")]       // integer overflow -> asterisks
    public void Str_AsterisksOnlyWhenIntegerDoesNotFit(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    // ====================================================================
    //  STRZERO(): "STR then leading spaces -> zeros", sign kept in place
    // ====================================================================

    [Theory]
    [InlineData("STRZERO(-1234,8)", "000-1234")] // sign stays, leading blanks -> zeros
    [InlineData("STRZERO(-1234,4)", "****")]     // overflow incl. sign -> asterisks
    [InlineData("STRZERO(1234,8)", "00001234")]
    [InlineData("STRZERO(45,5)", "00045")]
    [InlineData("STRZERO(-7,4)", "00-7")]
    public void Strzero_KeepsSignAndOverflows(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    // ====================================================================
    //  DTOS()/DTOC() on the empty (default) date -> blanks
    // ====================================================================

    [Fact]
    public void Dtos_EmptyDate_IsEightSpaces()
    {
        var row = new TestRow().Set("D", default(DateOnly));
        Assert.Equal(new string(' ', 8), Eval("DTOS(D)", row).AsString);
    }

    [Fact]
    public void Dtoc_EmptyDate_IsTenSpaces()
    {
        var row = new TestRow().Set("D", default(DateOnly));
        Assert.Equal(new string(' ', 10), Eval("DTOC(D)", row).AsString);
    }

    [Fact]
    public void Dtos_RealDate_StillFormats()
        => Assert.Equal("20200303", Eval("DTOS({^2020-03-03})").AsString);

    // ====================================================================
    //  CHR()/ASC(): code-page (CP1252) based, not Unicode
    // ====================================================================

    [Fact]
    public void Chr_HighByte_UsesCp1252_NotUnicode()
        // CHR(150) is CP1252 0x96 = U+2013 EN DASH, not the C1 control U+0096.
        => Assert.Equal("–", Eval("CHR(150)").AsString);

    [Fact]
    public void Asc_HighChar_ReturnsCp1252Byte()
        => Assert.Equal(150, Eval("ASC('–')").AsInteger);

    [Theory]
    [InlineData("CHR(65)", "A")]
    [InlineData("CHR(233)", "é")]   // 0xE9 Latin-1 identity
    public void Chr_RoundTripsAscIIAndLatin1(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    [Fact]
    public void ChrAsc_RoundTrip_AcrossHighRange()
    {
        for (int n = 128; n <= 255; n++)
        {
            var s = Eval($"CHR({n})").AsString;
            // Skip the 5 undefined CP1252 slots (decode to U+FFFF, not round-trippable).
            if (s == "￿") continue;
            int back = Eval($"ASC('{s}')").AsInteger;
            Assert.Equal(n, back);
        }
    }

    // ====================================================================
    //  RECCOUNT() reads the live row count
    // ====================================================================

    [Fact]
    public void Reccount_ReadsRowCount()
    {
        var row = new TestRow(recNo: 3) { RecCount = 4242 };
        Assert.Equal(4242, Eval("RECCOUNT()", row).AsInteger);
    }

    // ====================================================================
    //  LEFT/RIGHT length inference (arg index 1, not 2)
    // ====================================================================

    [Fact]
    public void InferType_Left_UsesLengthArgument()
    {
        var schema = new TestSchema().Add("NAME", 'C', 10);
        Assert.Equal(3, VfpExpression.Parse("LEFT(NAME,3)").InferType(schema).Length);
    }

    [Fact]
    public void InferType_Right_UsesLengthArgument()
    {
        var schema = new TestSchema().Add("NAME", 'C', 10);
        Assert.Equal(2, VfpExpression.Parse("RIGHT(NAME,2)").InferType(schema).Length);
    }

    [Fact]
    public void InferType_Substr_UsesThirdArgument()
    {
        var schema = new TestSchema().Add("NAME", 'C', 10);
        Assert.Equal(4, VfpExpression.Parse("SUBSTR(NAME,2,4)").InferType(schema).Length);
    }

    // ====================================================================
    //  Lexer: qualified field references + bracket string literals
    // ====================================================================

    [Fact]
    public void QualifiedName_ParsesAndResolves()
    {
        var row = new TestRow().Set("customer.cust_id", "ALFKI");
        Assert.Equal("ALFKI", Eval("customer.cust_id", row).AsString);
    }

    [Fact]
    public void QualifiedName_DoesNotSwallowDottedOperators()
    {
        // flag.AND.other must still parse as (flag) .AND. (other), not one identifier.
        var row = new TestRow().Set("flag", true).Set("other", true);
        Assert.True(Eval("flag .AND. other", row).AsLogical);
    }

    [Fact]
    public void QualifiedName_UnicodeIdentifier_ParsesAndResolves()
    {
        // FoxPro field names may contain Unicode letters: 'razónSocial' must lex as one
        // identifier in both the first and the continuation position of the name part.
        var row = new TestRow().Set("clientes.razónSocial", "ACME S.A.");
        Assert.Equal("ACME S.A.", Eval("clientes.razónSocial", row).AsString);
    }

    [Fact]
    public void MemoryVarStylePrefix_Parses()
    {
        var row = new TestRow().Set("m.lname", "Smith");
        Assert.Equal("Smith", Eval("m.lname", row).AsString);
    }

    [Theory]
    [InlineData("[hello]", "hello")]
    [InlineData("[a]+[b]", "ab")]
    [InlineData("[it's a \"test\"]", "it's a \"test\"")] // brackets nest quotes freely
    public void BracketStringLiteral_IsSupported(string expr, string expected)
        => Assert.Equal(expected, Eval(expr).AsString);

    // ====================================================================
    //  Compiled delegate still matches the interpreter for new paths
    // ====================================================================

    [Theory]
    [InlineData("STR(1234.5,6,2)")]
    [InlineData("STRZERO(-1234,8)")]
    [InlineData("CHR(150)")]
    [InlineData("[bracket] + 'x'")]
    public void CompiledDelegate_MatchesInterpreter_NewPaths(string expr)
    {
        var parsed = VfpExpression.Parse(expr);
        var ctx = EvaluationContext.Default;
        Assert.Equal(parsed.Evaluate(TestRow.Empty, ctx), parsed.Compile(ctx)(TestRow.Empty));
    }
}
