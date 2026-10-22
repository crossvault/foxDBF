using System;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The SEEK needle of <see cref="CdxTag.CreateCharacterSeekNeedle"/> against VFP9's answers (probes of 2026-10-22,
/// pinned live by the internal SeekExactVfp9OracleTests): the table <c>Ab, Ab c, Abc, Abd, ' x', x, 'Ab  c'</c> in
/// C(10), CP1252, with a MACHINE and a GENERAL tag.
/// </summary>
public sealed class CharacterSeekNeedleTests : IDisposable
{
    private readonly IndexMaintTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly string[] Values = { "Ab", "Ab c", "Abc", "Abd", " x", "x", "Ab  c" };

    private static readonly string[] Needles =
        { "Ab", "Ab ", "Ab  ", "Ab c", "Ab c ", " ", "   ", "x ", " x", " x ", "Ab" + new string(' ', 20), "Abc" + new string(' ', 8) };

    // VFP9 SEEK results (recno or N) per needle.
    private static readonly string[] GeneralOff = { "1", "1", "1", "2", "2", "5", "N", "6", "5", "5", "1", "3" };
    private static readonly string[] GeneralOn = { "1", "1", "1", "2", "2", "N", "N", "6", "5", "5", "1", "3" };
    private static readonly string[] MachineOff = { "1", "1", "1", "2", "2", "5", "N", "6", "5", "5", "N", "N" };
    private static readonly string[] MachineOn = { "1", "1", "1", "2", "2", "N", "N", "6", "5", "5", "N", "N" };

    private CdxFile OpenTable(out DbfTable table)
    {
        string dbf = System.IO.Path.Combine(_dir.Path, "q.dbf");
        using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("NAME", 'C', 10) },
                   new DbfCreateOptions { CodePage = 0x03, Overwrite = true }))
        {
            foreach (var v in Values) w.AppendRecord(v);
            w.CreateTag(new CdxTagDefinition("kg", "name") { Collation = "GENERAL" });
            w.CreateTag(new CdxTagDefinition("km", "name"));
        }
        table = DbfTable.Open(dbf);
        return CdxFile.Open(System.IO.Path.ChangeExtension(dbf, ".cdx"), table);
    }

    [Theory]
    [InlineData("kg", false)]
    [InlineData("kg", true)]
    [InlineData("km", false)]
    [InlineData("km", true)]
    public void Seek_FollowsVfp9_ForTrailingBlanksAndOverlongNeedles(string tagName, bool exact)
    {
        using var cdx = OpenTable(out var table);
        using var _ = table;
        var tag = cdx.Tag(tagName)!;
        var expected = (tagName, exact) switch
        {
            ("kg", false) => GeneralOff,
            ("kg", true) => GeneralOn,
            ("km", false) => MachineOff,
            _ => MachineOn,
        };
        var actual = Needles.Select(n => tag.Seek(tag.CreateCharacterSeekNeedle(n, exact)) is uint r ? r.ToString() : "N").ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GeneralPrefixNeedle_TrailingBlankMatchesBlankOrEndOfHeads_NotAnotherLetter()
    {
        using var cdx = OpenTable(out var table);
        using var _ = table;
        var tag = cdx.Tag("kg")!;
        var needle = tag.CreateCharacterSeekNeedle("Ab ", exact: false);
        var matching = tag.EnumerateEntries().Where(e => needle.Matches(e.Key)).Select(e => e.RecordNumber).OrderBy(r => r);
        Assert.Equal(new uint[] { 1, 2, 7 }, matching);   // Ab, Ab c, Ab  c — not Abc / Abd.
    }

    [Fact]
    public void GeneralNeedleAsWideAsTheKey_IsExact()
    {
        using var cdx = OpenTable(out var table);
        using var _ = table;
        var tag = cdx.Tag("kg")!;
        Assert.True(tag.CreateCharacterSeekNeedle("Abc       ", exact: false).Exact);
        Assert.False(tag.CreateCharacterSeekNeedle("Abc", exact: false).Exact);
        Assert.False(tag.CreateCharacterSeekNeedle("Abc        ", exact: false).Exact);   // wider than the key: heads.
    }

    /// <summary>The trailing-blank rule reads a key byte under 0x10 as "past the value's heads": every head of a
    /// character (the blank's too) is at least 0x10, every tail below it.</summary>
    [Fact]
    public void WeightTables_HeadsAtLeast0x10_TailsBelow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var collations = new[]
        {
            GeneralCollation.Instance, GeneralCollation.Cp850, GeneralCollation.Cp437, GeneralCollation.Cp1250,
            GeneralCollation.Spanish1252, GeneralCollation.Spanish850,
        };
        foreach (var c in collations)
        {
            Assert.True(c.BlankHead >= 0x10, $"{c.Name}/{c.CodePage}: blank head {c.BlankHead:X2}");
            var enc = Encoding.GetEncoding(c.CodePage);
            for (int b = 1; b < 256; b++)
            {
                string s = enc.GetString(new[] { (byte)b });
                if (s == " ") continue;
                var heads = c.GetHeadKey(s);
                var key = c.GetCollatedKey(s);
                Assert.All(heads, h => Assert.True(h >= 0x10, $"{c.Name}/{c.CodePage} byte {b:X2}: head {h:X2}"));
                Assert.All(key.Skip(heads.Length), t => Assert.True(t < 0x10, $"{c.Name}/{c.CodePage} byte {b:X2}: tail {t:X2}"));
            }
        }
    }
}
