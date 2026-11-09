using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Tests;

public sealed class GeneralAllCodePageTests
{
    [Theory]
    [InlineData(852)]
    [InlineData(857)]
    [InlineData(861)]
    [InlineData(863)]
    [InlineData(865)]
    [InlineData(1254)]
    [InlineData(10000)]
    [InlineData(10029)]
    [InlineData(620)]
    [InlineData(895)]
    public void General_UsesExactCodePageWeights(int codePage)
    {
        Assert.Equal(CollationSupport.Exact, VfpCollations.Resolve("GENERAL", codePage, out var collation));
        Assert.Equal(codePage, Assert.IsType<GeneralCollation>(collation).CodePage);
    }

    // Literal keys read from VFP9, including expansions and undefined/symbol slots.
    [Theory]
    [InlineData(0x64, "6161", "7272")]
    [InlineData(0x64, "8061", "757202")]
    [InlineData(0x64, "A061", "727201")]
    [InlineData(0x64, "DF61", "5772")]
    [InlineData(0x64, "E161", "959572")]
    [InlineData(0x64, "FF61", "6572")]
    [InlineData(0x6B, "6161", "7D7D")]
    [InlineData(0x6B, "8061", "7F7D01")]
    [InlineData(0x6B, "A061", "7D7D02")]
    [InlineData(0x6B, "DF61", "587D")]
    [InlineData(0x6B, "E161", "8F8F7D")]
    [InlineData(0x6B, "FF61", "157D")]
    [InlineData(0x67, "6161", "8A8A")]
    [InlineData(0x67, "8061", "8C8A01")]
    [InlineData(0x67, "A061", "8A8A01")]
    [InlineData(0x67, "DF61", "6D8A")]
    [InlineData(0x67, "E161", "9D9D8A")]
    [InlineData(0x67, "FF61", "148A")]
    [InlineData(0x6C, "6161", "9898")]
    [InlineData(0x6C, "8061", "9A9801")]
    [InlineData(0x6C, "A061", "3598")]
    [InlineData(0x6C, "DF61", "7698")]
    [InlineData(0x6C, "E161", "1198")]
    [InlineData(0x6C, "FF61", "1598")]
    [InlineData(0x66, "6161", "6060")]
    [InlineData(0x66, "8061", "626007")]
    [InlineData(0x66, "A061", "606001")]
    [InlineData(0x66, "DF61", "1060")]
    [InlineData(0x66, "E161", "767660")]
    [InlineData(0x66, "FF61", "1060")]
    [InlineData(0xCA, "6161", "6E6E")]
    [InlineData(0xCA, "8061", "106E")]
    [InlineData(0xCA, "A061", "186E")]
    [InlineData(0xCA, "DF61", "80806E")]
    [InlineData(0xCA, "E161", "6E6E02")]
    [InlineData(0xCA, "FF61", "876E01")]
    [InlineData(0x04, "6161", "7777")]
    [InlineData(0x04, "8061", "777705")]
    [InlineData(0x04, "A061", "6677")]
    [InlineData(0x04, "DF61", "1077")]
    [InlineData(0x04, "E161", "6577")]
    [InlineData(0x04, "FF61", "1277")]
    [InlineData(0x97, "6161", "5F5F")]
    [InlineData(0x97, "8061", "5F5F02")]
    [InlineData(0x97, "A061", "515F")]
    [InlineData(0x97, "DF61", "705F03")]
    [InlineData(0x97, "E161", "715F02")]
    [InlineData(0x97, "FF61", "325F")]
    [InlineData(0x69, "6161", "6060")]
    [InlineData(0x69, "8061", "626008")]
    [InlineData(0x69, "A061", "7E6001")]
    [InlineData(0x69, "DF61", "1060")]
    [InlineData(0x69, "E161", "767660")]
    [InlineData(0x69, "FF61", "1060")]
    [InlineData(0x68, "6161", "8181")]
    [InlineData(0x68, "8061", "838101")]
    [InlineData(0x68, "A061", "818101")]
    [InlineData(0x68, "DF61", "6881")]
    [InlineData(0x68, "E161", "939381")]
    [InlineData(0x68, "FF61", "1481")]
    public void General_KeysMatchVfp9Probe(byte mark, string inputHex, string expectedHex)
    {
        var encoding = Encodings.ResolveEncoding(mark);
        var collation = VfpCollations.ByName("GENERAL", encoding.CodePage);
        string value = encoding.GetString(Convert.FromHexString(inputHex));
        Assert.Equal(expectedHex, Convert.ToHexString(collation.GetCollatedKey(value.AsSpan())));
    }
}
