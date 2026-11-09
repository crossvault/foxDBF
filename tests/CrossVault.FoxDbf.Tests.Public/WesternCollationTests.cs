using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class WesternCollationTests
{
    [Theory]
    [InlineData("SPANISH", 437, 1)]
    [InlineData("SPANISH", 10000, 4)]
    [InlineData("UNIQWT", 1252, 3)]
    [InlineData("UNIQWT", 437, 1)]
    [InlineData("UNIQWT", 850, 2)]
    [InlineData("UNIQWT", 10000, 4)]
    [InlineData("DUTCH", 1252, 3)]
    [InlineData("DUTCH", 437, 1)]
    [InlineData("DUTCH", 850, 2)]
    [InlineData("DUTCH", 10000, 4)]
    public void Western_IsExact_AndWriterMaintainsTag(string sequence, int codePage, byte mark)
    {
        Assert.Equal(sequence, VfpCollations.ByName(sequence, codePage).Name);
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "western.dbf");
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 20)],
            new DbfCreateOptions { CodePage = mark }))
        {
            writer.AppendRecord("Öl");
            writer.CreateTag(new CdxTagDefinition("k", "name") { Collation = sequence });
            writer.AppendRecord("Straße");
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        Assert.True(cdx.Tag("k")!.IsKeyCollationExact);
        Assert.Equal(2u, cdx.Tag("k")!.Seek("Straße"));
    }

    // Raw VFP9 probe keys, pinned independently of the generated tables.
    [Theory]
    [InlineData("SPANISH", 437, "aá", "60600001")]
    [InlineData("SPANISH", 437, "Aa", "6060")]
    [InlineData("SPANISH", 437, "áa", "606001")]
    [InlineData("SPANISH", 437, "Æá", "60666001")]
    [InlineData("SPANISH", 437, "ßá", "76766001")]
    [InlineData("SPANISH", 437, "ch", "63")]
    [InlineData("SPANISH", 437, "CH", "63")]
    [InlineData("SPANISH", 437, "ll", "6E")]
    [InlineData("SPANISH", 437, "ij", "6A6B")]
    [InlineData("SPANISH", 437, "IJ", "6A6B")]
    [InlineData("SPANISH", 437, "ñá", "716001")]
    [InlineData("SPANISH", 10000, "aá", "77770002")]
    [InlineData("SPANISH", 10000, "Aa", "7777")]
    [InlineData("SPANISH", 10000, "áa", "777702")]
    [InlineData("SPANISH", 10000, "Æá", "777C77000002")]
    [InlineData("SPANISH", 10000, "ßá", "8C8C7702")]
    [InlineData("SPANISH", 10000, "ch", "7A")]
    [InlineData("SPANISH", 10000, "CH", "7A")]
    [InlineData("SPANISH", 10000, "ll", "84")]
    [InlineData("SPANISH", 10000, "ij", "8081")]
    [InlineData("SPANISH", 10000, "IJ", "8081")]
    [InlineData("SPANISH", 10000, "ñá", "877702")]
    [InlineData("UNIQWT", 1252, "aá", "7477")]
    [InlineData("UNIQWT", 1252, "Aa", "7574")]
    [InlineData("UNIQWT", 1252, "áa", "7774")]
    [InlineData("UNIQWT", 1252, "Æá", "758E77")]
    [InlineData("UNIQWT", 1252, "ßá", "C8C877")]
    [InlineData("UNIQWT", 1252, "ch", "859B")]
    [InlineData("UNIQWT", 1252, "CH", "869C")]
    [InlineData("UNIQWT", 1252, "ll", "ABAB")]
    [InlineData("UNIQWT", 1252, "ij", "9DA7")]
    [InlineData("UNIQWT", 1252, "IJ", "9EA8")]
    [InlineData("UNIQWT", 1252, "ñá", "B177")]
    [InlineData("UNIQWT", 437, "aá", "9194")]
    [InlineData("UNIQWT", 437, "Aa", "9291")]
    [InlineData("UNIQWT", 437, "áa", "9491")]
    [InlineData("UNIQWT", 437, "Æá", "92A494")]
    [InlineData("UNIQWT", 437, "ßá", "D0D094")]
    [InlineData("UNIQWT", 437, "ch", "9DAE")]
    [InlineData("UNIQWT", 437, "CH", "9EAF")]
    [InlineData("UNIQWT", 437, "ll", "BABA")]
    [InlineData("UNIQWT", 437, "ij", "B0B6")]
    [InlineData("UNIQWT", 437, "IJ", "B1B7")]
    [InlineData("UNIQWT", 437, "ñá", "C094")]
    [InlineData("UNIQWT", 850, "aá", "8184")]
    [InlineData("UNIQWT", 850, "Aa", "8281")]
    [InlineData("UNIQWT", 850, "áa", "8481")]
    [InlineData("UNIQWT", 850, "Æá", "829B84")]
    [InlineData("UNIQWT", 850, "ßá", "DADA84")]
    [InlineData("UNIQWT", 850, "ch", "92A8")]
    [InlineData("UNIQWT", 850, "CH", "93A9")]
    [InlineData("UNIQWT", 850, "ll", "B9B9")]
    [InlineData("UNIQWT", 850, "ij", "AAB5")]
    [InlineData("UNIQWT", 850, "IJ", "ABB6")]
    [InlineData("UNIQWT", 850, "ñá", "BF84")]
    [InlineData("UNIQWT", 10000, "aá", "7477")]
    [InlineData("UNIQWT", 10000, "Aa", "7574")]
    [InlineData("UNIQWT", 10000, "áa", "7774")]
    [InlineData("UNIQWT", 10000, "Æá", "758E77")]
    [InlineData("UNIQWT", 10000, "ßá", "C8C877")]
    [InlineData("UNIQWT", 10000, "ch", "859B")]
    [InlineData("UNIQWT", 10000, "CH", "869C")]
    [InlineData("UNIQWT", 10000, "ll", "ABAB")]
    [InlineData("UNIQWT", 10000, "ij", "9DA7")]
    [InlineData("UNIQWT", 10000, "IJ", "9EA8")]
    [InlineData("UNIQWT", 10000, "ñá", "B177")]
    [InlineData("DUTCH", 1252, "aá", "60600001")]
    [InlineData("DUTCH", 1252, "Aa", "6060")]
    [InlineData("DUTCH", 1252, "áa", "606001")]
    [InlineData("DUTCH", 1252, "Æá", "606660000001")]
    [InlineData("DUTCH", 1252, "ßá", "767660000001")]
    [InlineData("DUTCH", 1252, "ch", "6269")]
    [InlineData("DUTCH", 1252, "CH", "6269")]
    [InlineData("DUTCH", 1252, "ll", "6D6D")]
    [InlineData("DUTCH", 1252, "ij", "6A6B")]
    [InlineData("DUTCH", 1252, "IJ", "6A6B")]
    [InlineData("DUTCH", 1252, "ñá", "70600501")]
    [InlineData("DUTCH", 437, "aá", "60600001")]
    [InlineData("DUTCH", 437, "Aa", "6060")]
    [InlineData("DUTCH", 437, "áa", "606001")]
    [InlineData("DUTCH", 437, "Æá", "606660000001")]
    [InlineData("DUTCH", 437, "ßá", "767660000001")]
    [InlineData("DUTCH", 437, "ch", "6269")]
    [InlineData("DUTCH", 437, "CH", "6269")]
    [InlineData("DUTCH", 437, "ll", "6D6D")]
    [InlineData("DUTCH", 437, "ij", "6A6B")]
    [InlineData("DUTCH", 437, "IJ", "6A6B")]
    [InlineData("DUTCH", 437, "ñá", "70600501")]
    [InlineData("DUTCH", 850, "aá", "60600001")]
    [InlineData("DUTCH", 850, "Aa", "6060")]
    [InlineData("DUTCH", 850, "áa", "606001")]
    [InlineData("DUTCH", 850, "Æá", "606660000001")]
    [InlineData("DUTCH", 850, "ßá", "767660000001")]
    [InlineData("DUTCH", 850, "ch", "6269")]
    [InlineData("DUTCH", 850, "CH", "6269")]
    [InlineData("DUTCH", 850, "ll", "6D6D")]
    [InlineData("DUTCH", 850, "ij", "6A6B")]
    [InlineData("DUTCH", 850, "IJ", "6A6B")]
    [InlineData("DUTCH", 850, "ñá", "70600501")]
    [InlineData("DUTCH", 10000, "aá", "77770002")]
    [InlineData("DUTCH", 10000, "Aa", "7777")]
    [InlineData("DUTCH", 10000, "áa", "777702")]
    [InlineData("DUTCH", 10000, "Æá", "777B77000002")]
    [InlineData("DUTCH", 10000, "ßá", "89897702")]
    [InlineData("DUTCH", 10000, "ch", "797E")]
    [InlineData("DUTCH", 10000, "CH", "797E")]
    [InlineData("DUTCH", 10000, "ll", "8282")]
    [InlineData("DUTCH", 10000, "ij", "7F80")]
    [InlineData("DUTCH", 10000, "IJ", "7F80")]
    [InlineData("DUTCH", 10000, "ñá", "84770102")]
    public void Western_KeysMatchVfp9(string sequence, int codePage, string value, string expected)
        => Assert.Equal(expected, Convert.ToHexString(VfpCollations.ByName(sequence, codePage).GetCollatedKey(value)));
}
