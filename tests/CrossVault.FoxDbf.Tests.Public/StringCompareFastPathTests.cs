using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The hot-path shortcuts behind the string operators give the same answers as the plain rules: MACHINE's inline
/// ASCII compare against a char-by-char reference of the three length rules, and the per-record memo of a C field's
/// full-width expression value against the trimmed indexer value it shares a slot with.
/// </summary>
public sealed class StringCompareFastPathTests
{
    [Fact]
    public void MachineAsciiCompare_MatchesReference()
    {
        var rng = new Random(20261022);
        const string alphabet = "  \t\0\u001fAaBbZz09~\u007f";
        var ctx = new EvaluationContext { Collation = VfpCollations.Machine };
        for (int n = 0; n < 20000; n++)
        {
            string l = RandomString(rng, alphabet), r = RandomString(rng, alphabet);
            foreach (var mode in new[] { StringCompareMode.TruncateLeft, StringCompareMode.Pad, StringCompareMode.Shorter })
                Assert.True(Reference(l, r, mode) == VfpRuntime.CompareStrings(l, r, mode, ctx),
                    $"{mode} [{Escape(l)}] vs [{Escape(r)}]");
        }
    }

    // Below 0x80 a char is its CP1252 byte: cut or blank-pad, then ordinal order.
    private static int Reference(string l, string r, StringCompareMode mode)
    {
        switch (mode)
        {
            case StringCompareMode.TruncateLeft:
                if (l.Length > r.Length) l = l[..r.Length];
                break;
            case StringCompareMode.Shorter:
                int m = Math.Min(l.Length, r.Length);
                l = l[..m]; r = r[..m];
                break;
            default:
                int w = Math.Max(l.Length, r.Length);
                l = l.PadRight(w); r = r.PadRight(w);
                break;
        }
        return Math.Sign(string.CompareOrdinal(l, r));
    }

    private static string RandomString(Random rng, string alphabet)
    {
        var c = new char[rng.Next(0, 9)];
        for (int i = 0; i < c.Length; i++) c[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(c);
    }

    private static string Escape(string s) => string.Concat(s.Select(ch => ch < ' ' ? $"<{(int)ch:X2}>" : ch.ToString()));

    [Fact]
    public void ExpressionValueMemo_KeepsIndexerValueApart()
    {
        using var dir = new MicroVfpTestSupport.TempDir("expr_value_memo");
        string path = Path.Combine(dir.Path, "memo.dbf");
        using (var w = DbfWriter.Create(path, new[] { new DbfColumnDef("NAME", 'C', 10), new DbfColumnDef("ID", 'I', 4) },
                   new DbfCreateOptions { Overwrite = true }))
        {
            w.AppendRecord("ab", 7);
            w.Flush();
        }

        using var table = DbfTable.Open(path);
        var rec = table.EnumerateAll(includeDeleted: true).Single();
        var padded = rec.GetExpressionValue("NAME");
        Assert.Equal("ab        ", padded);
        Assert.Same(padded, rec.GetExpressionValue("NAME"));     // memoized
        Assert.Equal("ab", rec["NAME"]);                          // the indexer stays trimmed
        Assert.Equal("ab", rec["NAME"]);
        Assert.Equal("ab        ", rec.GetExpressionValue("NAME"));
        Assert.Equal(7, rec.GetExpressionValue("ID"));
        Assert.Equal(7, rec["ID"]);
    }
}
