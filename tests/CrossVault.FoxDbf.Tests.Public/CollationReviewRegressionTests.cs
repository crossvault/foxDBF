using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Tests;

public sealed class CollationReviewRegressionTests
{
    // Live GrokCollationVfp9OracleTests, matching startup code page. Each result string is
    // expression / SQL ANSI OFF / SQL ANSI ON; all seven operators were measured in both directions.
    [Theory]
    [InlineData("SPANISH", 1252, "ß", "s", false, "TTF", "FFF", "FFT")]
    [InlineData("SPANISH", 1252, "s", "ß", false, "FFF", "TTT", "FFF")]
    [InlineData("SPANISH", 1252, "ß", "s", true, "FTF", "FFF", "TFT")]
    [InlineData("SPANISH", 1252, "s", "ß", true, "FFF", "TTT", "FFF")]
    [InlineData("SWEFIN", 1252, "Æ", "AE", false, "FTT", "TFF", "FFF")]
    [InlineData("SWEFIN", 1252, "AE", "Æ", false, "TTT", "FFF", "FFF")]
    [InlineData("SWEFIN", 1252, "Æ", "AE", true, "TTT", "FFF", "FFF")]
    [InlineData("SWEFIN", 1252, "Æé", "AEé", false, "FFF", "FFF", "TTT")]
    [InlineData("SWEFIN", 1252, "AEé", "Æé", false, "FFF", "TTT", "FFF")]
    [InlineData("SWEFIN", 1252, "Æé", "AEé", true, "FFF", "FFF", "TTT")]
    [InlineData("HUNGARY", 1250, "CH", "ch", false, "FTT", "TFF", "FFF")]
    [InlineData("HUNGARY", 1250, "ch", "CH", false, "TTT", "FFF", "FFF")]
    [InlineData("HUNGARY", 1250, "CH", "ch", true, "TTT", "FFF", "FFF")]
    [InlineData("HUNGARY", 1250, "CHá", "chá", false, "FFF", "FFF", "TTT")]
    [InlineData("HUNGARY", 1250, "chá", "CHá", false, "FFF", "TTT", "FFF")]
    [InlineData("HUNGARY", 1250, "CHá", "chá", true, "FFF", "FFF", "TTT")]
    [InlineData("HUNGARY", 1250, "cHé", "Ché         ", false, "FFF", "FFF", "TTT")]
    [InlineData("TURKISH", 1254, "İ", "i", false, "FTT", "TFF", "FFF")]
    [InlineData("TURKISH", 1254, "i", "İ", false, "TTT", "FFF", "FFF")]
    [InlineData("TURKISH", 1254, "İ", "i", true, "TTT", "FFF", "FFF")]
    [InlineData("TURKISH", 1254, "İé", "ié", false, "FFF", "FFF", "TTT")]
    [InlineData("TURKISH", 1254, "ié", "İé", false, "FFF", "TTT", "FFF")]
    [InlineData("TURKISH", 1254, "İé", "ié", true, "FFF", "FFF", "TTT")]
    [InlineData("TURKISH", 1254, "ıé", "Ié          ", false, "FFF", "FFF", "TTT")]
    [InlineData("GENERAL", 1252, "á", "ab", false, "FFF", "FFT", "TTF")]
    [InlineData("GENERAL", 1252, "á", "a ", false, "FFF", "FFF", "TTT")]
    public void Comparisons_PreserveVfp9DirectionAndSecondaryPositions(string sequence, int cp,
        string left, string right, bool exact, string equal, string less, string greater)
    {
        var row = new TestRow().Set("pl", left).Set("pr", right);
        for (int mode = 0; mode < 3; mode++)
        {
            var context = new EvaluationContext
            {
                Collation = VfpCollations.ByName(sequence, cp), Exact = exact,
                SqlSemantics = mode != 0, Ansi = mode == 2,
            };
            foreach (var (op, expected) in new[] { ("=", equal), ("<", less), (">", greater) })
                Assert.Equal(expected[mode] == 'T', VfpExpression.Parse("pl " + op + " pr").Evaluate(row, context).AsLogical);
        }
    }

    // VFP9 CP1254 uses these historical byte maps even under SET COLLATE TO TURKISH.
    [Theory]
    [InlineData("UPPER", 0x69, 0x49)]
    [InlineData("LOWER", 0x49, 0x69)]
    [InlineData("UPPER", 0xfd, 0xfd)]
    [InlineData("UPPER", 0xe2, 0xe2)]
    [InlineData("LOWER", 0xc2, 0xa2)]
    [InlineData("LOWER", 0xd8, 0xf9)]
    public void Cp1254_CasingPreservesMeasuredVfp9Bytes(string function, int input, int expected)
    {
        var context = new EvaluationContext
        {
            Encoding = Encodings.ResolveEncoding(0xca), SessionCodePage = 1254,
            Collation = VfpCollations.ByName("TURKISH", 1254),
        };
        Assert.Equal((decimal)expected,
            VfpExpression.Parse($"ASC({function}(CHR({input})))").Evaluate(null!, context).AsNumber);
    }
}
