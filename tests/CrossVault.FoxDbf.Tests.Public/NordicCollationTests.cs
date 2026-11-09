using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class NordicCollationTests
{
    [Fact]
    public void Swefin_ExpansionMissingZeroTailMatchesAe()
    {
        var context = new EvaluationContext { Collation = VfpCollations.ByName("SWEFIN"), Exact = false };
        // VFP9's SCAN oracle matches both ligature cases as well as literal ae.
        foreach (string value in new[] { "æ           ", "Æ           ", "ae          " })
        {
            var row = new TestRow().Set("name", value);
            Assert.True(VfpExpression.Parse("name = 'ae'").Evaluate(row, context).AsLogical);
            Assert.False(VfpExpression.Parse("name < 'ae'").Evaluate(row, context).AsLogical);
            Assert.True(VfpExpression.Parse("name >= 'ae'").Evaluate(row, context).AsLogical);
        }
    }

    [Theory]
    [InlineData(1252, 3)]
    [InlineData(437, 1)]
    [InlineData(850, 2)]
    [InlineData(865, 102)]
    [InlineData(10000, 4)]
    public void Nordan_RushmoreIncludesContractionsAtPrefixBoundary(int codePage, byte mark)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "boundary.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 12)], new DbfCreateOptions { CodePage = mark }))
        {
            foreach (string value in new[] { "a", "aa", "Aa", "aA", "AA", "aaa", "aab", "å", "ae" })
                writer.AppendRecord(value);
            writer.CreateTag(new CdxTagDefinition("k", "name", collation: "NORDAN"));
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        var context = new EvaluationContext { Collation = VfpCollations.ByName("NORDAN", codePage), Exact = false };
        var scan = QueryOptimizer.FindRecords(table, null, "name = 'a'", context);
        var indexed = QueryOptimizer.FindRecords(table, cdx, "name = 'a'", context);
        // Pinned by NordicCollationVfp9OracleTests: literal prefixes match before collation.
        Assert.Equal(new[] { 1, 2, 4, 6, 7, 9 }, scan.RecordNumbers);
        Assert.Equal(scan.RecordNumbers, indexed.RecordNumbers);
        Assert.True(indexed.Optimized);
    }

    [Theory]
    [InlineData("NORDAN", 1252, 3)]
    [InlineData("NORDAN", 437, 1)]
    [InlineData("NORDAN", 850, 2)]
    [InlineData("NORDAN", 865, 102)]
    [InlineData("NORDAN", 10000, 4)]
    [InlineData("SWEFIN", 1252, 3)]
    [InlineData("SWEFIN", 437, 1)]
    [InlineData("SWEFIN", 850, 2)]
    [InlineData("SWEFIN", 865, 102)]
    [InlineData("SWEFIN", 10000, 4)]
    [InlineData("ICELAND", 1252, 3)]
    [InlineData("ICELAND", 437, 1)]
    [InlineData("ICELAND", 850, 2)]
    [InlineData("ICELAND", 861, 103)]
    public void Nordic_IsExact_AndWriterMaintainsTag(string name, int codePage, byte mark)
    {
        Assert.Equal(name, VfpCollations.ByName(name, codePage).Name);
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "nordic.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 12)], new DbfCreateOptions { CodePage = mark }))
        {
            writer.AppendRecord("å");
            writer.CreateTag(new CdxTagDefinition("k", "name", collation: name));
            writer.AppendRecord("aa");
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        Assert.True(cdx.Tag("k")!.IsKeyCollationExact);
        Assert.NotNull(cdx.Tag("k")!.Seek("aa"));
    }
    // Keys pinned from VFP9 CDX records, independently of the generated weight tables.
    [Theory]
    [InlineData("NORDAN", 1252, "aa", "7E07")]
    [InlineData("NORDAN", 1252, "aA", "7E07")]
    [InlineData("NORDAN", 1252, "Aa", "7E07")]
    [InlineData("NORDAN", 1252, "AA", "7E07")]
    [InlineData("NORDAN", 1252, "aaa", "7E6007")]
    [InlineData("NORDAN", 1252, "åa", "7E6006")]
    [InlineData("NORDAN", 1252, "æa", "7C60")]
    [InlineData("NORDAN", 1252, "øa", "7D60")]
    [InlineData("NORDAN", 1252, "äá", "7C600401")]
    [InlineData("NORDAN", 1252, "öá", "7D600401")]
    [InlineData("NORDAN", 1252, "üá", "75600401")]
    [InlineData("NORDAN", 1252, "þa", "7B60")]
    [InlineData("NORDAN", 1252, "ða", "6460")]
    [InlineData("NORDAN", 1252, "ßá", "737360000001")]
    [InlineData("NORDAN", 1252, "aaä", "7E7C0704")]
    [InlineData("NORDAN", 1252, "aaaaaa", "7E7E7E070707")]
    [InlineData("NORDAN", 437, "aa", "7E")]
    [InlineData("NORDAN", 437, "aA", "7E")]
    [InlineData("NORDAN", 437, "Aa", "7E")]
    [InlineData("NORDAN", 437, "AA", "7E")]
    [InlineData("NORDAN", 437, "aaa", "7E60")]
    [InlineData("NORDAN", 437, "åa", "7E6006")]
    [InlineData("NORDAN", 437, "æa", "7C60")]
    [InlineData("NORDAN", 437, "øa", "2660")]
    [InlineData("NORDAN", 437, "äá", "7C600402")]
    [InlineData("NORDAN", 437, "öá", "7D600402")]
    [InlineData("NORDAN", 437, "üá", "79600402")]
    [InlineData("NORDAN", 437, "þa", "2660")]
    [InlineData("NORDAN", 437, "ða", "2660")]
    [InlineData("NORDAN", 437, "ßá", "737360000002")]
    [InlineData("NORDAN", 437, "aaä", "7E7C04")]
    [InlineData("NORDAN", 437, "aaaaaa", "7E7E7E")]
    [InlineData("NORDAN", 850, "aa", "7E07")]
    [InlineData("NORDAN", 850, "aA", "7E07")]
    [InlineData("NORDAN", 850, "Aa", "7E07")]
    [InlineData("NORDAN", 850, "AA", "7E07")]
    [InlineData("NORDAN", 850, "aaa", "7E6007")]
    [InlineData("NORDAN", 850, "åa", "7E6006")]
    [InlineData("NORDAN", 850, "æa", "7C60")]
    [InlineData("NORDAN", 850, "øa", "7D60")]
    [InlineData("NORDAN", 850, "äá", "7C600402")]
    [InlineData("NORDAN", 850, "öá", "7D600402")]
    [InlineData("NORDAN", 850, "üá", "79600402")]
    [InlineData("NORDAN", 850, "þa", "7B60")]
    [InlineData("NORDAN", 850, "ða", "6460")]
    [InlineData("NORDAN", 850, "ßá", "737360000002")]
    [InlineData("NORDAN", 850, "aaä", "7E7C0704")]
    [InlineData("NORDAN", 850, "aaaaaa", "7E7E7E070707")]
    [InlineData("NORDAN", 865, "aa", "7F")]
    [InlineData("NORDAN", 865, "aA", "7F")]
    [InlineData("NORDAN", 865, "Aa", "7F")]
    [InlineData("NORDAN", 865, "AA", "7F")]
    [InlineData("NORDAN", 865, "aaa", "7F60")]
    [InlineData("NORDAN", 865, "åa", "7F6006")]
    [InlineData("NORDAN", 865, "æa", "7C60")]
    [InlineData("NORDAN", 865, "øa", "7D60")]
    [InlineData("NORDAN", 865, "äá", "7C600402")]
    [InlineData("NORDAN", 865, "öá", "7E600402")]
    [InlineData("NORDAN", 865, "üá", "79600402")]
    [InlineData("NORDAN", 865, "þa", "2660")]
    [InlineData("NORDAN", 865, "ða", "2660")]
    [InlineData("NORDAN", 865, "ßá", "737360000002")]
    [InlineData("NORDAN", 865, "aaä", "7F7C04")]
    [InlineData("NORDAN", 865, "aaaaaa", "7F7F7F")]
    [InlineData("NORDAN", 10000, "aa", "9401")]
    [InlineData("NORDAN", 10000, "aA", "9401")]
    [InlineData("NORDAN", 10000, "Aa", "9401")]
    [InlineData("NORDAN", 10000, "AA", "9401")]
    [InlineData("NORDAN", 10000, "aaa", "947701")]
    [InlineData("NORDAN", 10000, "åa", "9477")]
    [InlineData("NORDAN", 10000, "æa", "9277")]
    [InlineData("NORDAN", 10000, "øa", "9377")]
    [InlineData("NORDAN", 10000, "äá", "92770102")]
    [InlineData("NORDAN", 10000, "öá", "93770102")]
    [InlineData("NORDAN", 10000, "üá", "90770202")]
    [InlineData("NORDAN", 10000, "þa", "2877")]
    [InlineData("NORDAN", 10000, "ða", "2877")]
    [InlineData("NORDAN", 10000, "ßá", "89897702")]
    [InlineData("NORDAN", 10000, "aaä", "94920101")]
    [InlineData("NORDAN", 10000, "aaaaaa", "949494010101")]
    [InlineData("SWEFIN", 1252, "aa", "6060")]
    [InlineData("SWEFIN", 1252, "aA", "6060")]
    [InlineData("SWEFIN", 1252, "Aa", "6060")]
    [InlineData("SWEFIN", 1252, "AA", "6060")]
    [InlineData("SWEFIN", 1252, "aaa", "606060")]
    [InlineData("SWEFIN", 1252, "åa", "7C60")]
    [InlineData("SWEFIN", 1252, "æa", "606560")]
    [InlineData("SWEFIN", 1252, "øa", "7E6001")]
    [InlineData("SWEFIN", 1252, "äá", "7D6001")]
    [InlineData("SWEFIN", 1252, "öá", "7E600001")]
    [InlineData("SWEFIN", 1252, "üá", "79600001")]
    [InlineData("SWEFIN", 1252, "þa", "746860")]
    [InlineData("SWEFIN", 1252, "ða", "6460")]
    [InlineData("SWEFIN", 1252, "ßá", "737360000001")]
    [InlineData("SWEFIN", 1252, "aaä", "60607D")]
    [InlineData("SWEFIN", 1252, "aaaaaa", "606060606060")]
    [InlineData("SWEFIN", 437, "aa", "6060")]
    [InlineData("SWEFIN", 437, "aA", "6060")]
    [InlineData("SWEFIN", 437, "Aa", "6060")]
    [InlineData("SWEFIN", 437, "AA", "6060")]
    [InlineData("SWEFIN", 437, "aaa", "606060")]
    [InlineData("SWEFIN", 437, "åa", "7C60")]
    [InlineData("SWEFIN", 437, "æa", "606560")]
    [InlineData("SWEFIN", 437, "øa", "2660")]
    [InlineData("SWEFIN", 437, "äá", "7D6002")]
    [InlineData("SWEFIN", 437, "öá", "7E600002")]
    [InlineData("SWEFIN", 437, "üá", "79600002")]
    [InlineData("SWEFIN", 437, "þa", "2660")]
    [InlineData("SWEFIN", 437, "ða", "2660")]
    [InlineData("SWEFIN", 437, "ßá", "737360000002")]
    [InlineData("SWEFIN", 437, "aaä", "60607D")]
    [InlineData("SWEFIN", 437, "aaaaaa", "606060606060")]
    [InlineData("SWEFIN", 850, "aa", "6060")]
    [InlineData("SWEFIN", 850, "aA", "6060")]
    [InlineData("SWEFIN", 850, "Aa", "6060")]
    [InlineData("SWEFIN", 850, "AA", "6060")]
    [InlineData("SWEFIN", 850, "aaa", "606060")]
    [InlineData("SWEFIN", 850, "åa", "7C60")]
    [InlineData("SWEFIN", 850, "æa", "606560")]
    [InlineData("SWEFIN", 850, "øa", "7E6002")]
    [InlineData("SWEFIN", 850, "äá", "7D6002")]
    [InlineData("SWEFIN", 850, "öá", "7E600002")]
    [InlineData("SWEFIN", 850, "üá", "79600002")]
    [InlineData("SWEFIN", 850, "þa", "7B60")]
    [InlineData("SWEFIN", 850, "ða", "6460")]
    [InlineData("SWEFIN", 850, "ßá", "737360000002")]
    [InlineData("SWEFIN", 850, "aaä", "60607D")]
    [InlineData("SWEFIN", 850, "aaaaaa", "606060606060")]
    [InlineData("SWEFIN", 865, "aa", "6060")]
    [InlineData("SWEFIN", 865, "aA", "6060")]
    [InlineData("SWEFIN", 865, "Aa", "6060")]
    [InlineData("SWEFIN", 865, "AA", "6060")]
    [InlineData("SWEFIN", 865, "aaa", "606060")]
    [InlineData("SWEFIN", 865, "åa", "7C60")]
    [InlineData("SWEFIN", 865, "æa", "606560")]
    [InlineData("SWEFIN", 865, "øa", "7F60")]
    [InlineData("SWEFIN", 865, "äá", "7D6002")]
    [InlineData("SWEFIN", 865, "öá", "7E600002")]
    [InlineData("SWEFIN", 865, "üá", "79600002")]
    [InlineData("SWEFIN", 865, "þa", "2660")]
    [InlineData("SWEFIN", 865, "ða", "2660")]
    [InlineData("SWEFIN", 865, "ßá", "737360000002")]
    [InlineData("SWEFIN", 865, "aaä", "60607D")]
    [InlineData("SWEFIN", 865, "aaaaaa", "606060606060")]
    [InlineData("SWEFIN", 10000, "aa", "7777")]
    [InlineData("SWEFIN", 10000, "aA", "7777")]
    [InlineData("SWEFIN", 10000, "Aa", "7777")]
    [InlineData("SWEFIN", 10000, "AA", "7777")]
    [InlineData("SWEFIN", 10000, "aaa", "777777")]
    [InlineData("SWEFIN", 10000, "åa", "9177")]
    [InlineData("SWEFIN", 10000, "æa", "777B77")]
    [InlineData("SWEFIN", 10000, "øa", "937701")]
    [InlineData("SWEFIN", 10000, "äá", "927702")]
    [InlineData("SWEFIN", 10000, "öá", "93770002")]
    [InlineData("SWEFIN", 10000, "üá", "8F770202")]
    [InlineData("SWEFIN", 10000, "þa", "2877")]
    [InlineData("SWEFIN", 10000, "ða", "2877")]
    [InlineData("SWEFIN", 10000, "ßá", "89897702")]
    [InlineData("SWEFIN", 10000, "aaä", "777792")]
    [InlineData("SWEFIN", 10000, "aaaaaa", "777777777777")]
    [InlineData("ICELAND", 1252, "aa", "6060")]
    [InlineData("ICELAND", 1252, "aA", "6060")]
    [InlineData("ICELAND", 1252, "Aa", "6060")]
    [InlineData("ICELAND", 1252, "AA", "6060")]
    [InlineData("ICELAND", 1252, "aaa", "606060")]
    [InlineData("ICELAND", 1252, "åa", "606006")]
    [InlineData("ICELAND", 1252, "æa", "8660")]
    [InlineData("ICELAND", 1252, "øa", "756006")]
    [InlineData("ICELAND", 1252, "äá", "60610401")]
    [InlineData("ICELAND", 1252, "öá", "876101")]
    [InlineData("ICELAND", 1252, "üá", "7C610401")]
    [InlineData("ICELAND", 1252, "þa", "8560")]
    [InlineData("ICELAND", 1252, "ða", "6660")]
    [InlineData("ICELAND", 1252, "ßá", "7A7A61000001")]
    [InlineData("ICELAND", 1252, "aaä", "606060000004")]
    [InlineData("ICELAND", 1252, "aaaaaa", "606060606060")]
    [InlineData("ICELAND", 437, "aa", "6060")]
    [InlineData("ICELAND", 437, "aA", "6060")]
    [InlineData("ICELAND", 437, "Aa", "6060")]
    [InlineData("ICELAND", 437, "AA", "6060")]
    [InlineData("ICELAND", 437, "aaa", "606060")]
    [InlineData("ICELAND", 437, "åa", "606006")]
    [InlineData("ICELAND", 437, "æa", "8660")]
    [InlineData("ICELAND", 437, "øa", "2660")]
    [InlineData("ICELAND", 437, "äá", "60610401")]
    [InlineData("ICELAND", 437, "öá", "876101")]
    [InlineData("ICELAND", 437, "üá", "7C610401")]
    [InlineData("ICELAND", 437, "þa", "2660")]
    [InlineData("ICELAND", 437, "ða", "2660")]
    [InlineData("ICELAND", 437, "ßá", "7A7A61000001")]
    [InlineData("ICELAND", 437, "aaä", "606060000004")]
    [InlineData("ICELAND", 437, "aaaaaa", "606060606060")]
    [InlineData("ICELAND", 850, "aa", "6060")]
    [InlineData("ICELAND", 850, "aA", "6060")]
    [InlineData("ICELAND", 850, "Aa", "6060")]
    [InlineData("ICELAND", 850, "AA", "6060")]
    [InlineData("ICELAND", 850, "aaa", "606060")]
    [InlineData("ICELAND", 850, "åa", "606006")]
    [InlineData("ICELAND", 850, "æa", "8660")]
    [InlineData("ICELAND", 850, "øa", "876006")]
    [InlineData("ICELAND", 850, "äá", "60610001")]
    [InlineData("ICELAND", 850, "öá", "876101")]
    [InlineData("ICELAND", 850, "üá", "7C610401")]
    [InlineData("ICELAND", 850, "þa", "8560")]
    [InlineData("ICELAND", 850, "ða", "6660")]
    [InlineData("ICELAND", 850, "ßá", "7A7A61000001")]
    [InlineData("ICELAND", 850, "aaä", "606060")]
    [InlineData("ICELAND", 850, "aaaaaa", "606060606060")]
    [InlineData("ICELAND", 861, "aa", "8A8A")]
    [InlineData("ICELAND", 861, "aA", "8A8A")]
    [InlineData("ICELAND", 861, "Aa", "8A8A")]
    [InlineData("ICELAND", 861, "AA", "8A8A")]
    [InlineData("ICELAND", 861, "aaa", "8A8A8A")]
    [InlineData("ICELAND", 861, "åa", "8A8A04")]
    [InlineData("ICELAND", 861, "æa", "AC8A")]
    [InlineData("ICELAND", 861, "øa", "AD8A01")]
    [InlineData("ICELAND", 861, "äá", "8A8B03")]
    [InlineData("ICELAND", 861, "öá", "AD8B")]
    [InlineData("ICELAND", 861, "üá", "A38B02")]
    [InlineData("ICELAND", 861, "þa", "AB8A")]
    [InlineData("ICELAND", 861, "ða", "8F8A")]
    [InlineData("ICELAND", 861, "ßá", "A1A18B")]
    [InlineData("ICELAND", 861, "aaä", "8A8A8A000003")]
    [InlineData("ICELAND", 861, "aaaaaa", "8A8A8A8A8A8A")]
    public void Nordic_KeysMatchVfp9(string name, int codePage, string value, string expected)
        => Assert.Equal(expected, Convert.ToHexString(VfpCollations.ByName(name, codePage).GetCollatedKey(value)));
}
