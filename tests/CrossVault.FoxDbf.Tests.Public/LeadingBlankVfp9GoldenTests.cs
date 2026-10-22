using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// A C(10) field with and without leading blanks (<c>'  x'</c>, <c>' Á'</c>, <c>' A'</c>, an all-blank row …) against
/// VFP9's own answers (<c>vfp_test/leadblank/</c>, produced by <c>mkleadblank.prg</c>): for MACHINE and GENERAL, SET
/// EXACT OFF / ON, ten needles and <c>= == &lt; &lt;= &gt; &gt;=</c>, the plain scan, Rushmore over a tag on the field and
/// SQL WHERE under SET ANSI OFF / ON return exactly VFP's rows. VFP evaluates the field at its full width, so
/// <c>name = 'A'</c> never matches <c>' Á'</c> and <c>name == 'A'</c> matches nothing (the field is <c>'A         '</c>).
/// Rushmore is held to VFP's scan, not to VFP's own Rushmore, which loses rows for GENERAL <c>name &gt; ' '</c>; VFP's
/// SQL under SET ANSI OFF (the cutting comparison, like SET EXACT OFF) inherits that loss, so such a needle and operator
/// have no ANSI OFF check.
/// </summary>
public sealed class LeadingBlankVfp9GoldenTests
{
    [Theory]
    [InlineData("MACHINE")]
    [InlineData("GENERAL")]
    public void ScanRushmoreAndSql_MatchVfp9(string collation)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1252 = Encoding.GetEncoding(1252);
        using var dir = new MicroVfpTestSupport.TempDir("leadblank");
        string dbf = dir.File("lb.dbf");
        File.Copy(Fixtures.VfpTest(Path.Combine("leadblank", "lb.dbf")), dbf);
        using (var w = DbfWriter.Open(dbf))
            w.CreateTag(new CdxTagDefinition("k", "name") { Collation = collation });

        var lines = File.ReadAllLines(Fixtures.VfpTest(Path.Combine("leadblank", "lb.txt")), cp1252)
            .Where(l => l.StartsWith(collation + " ", StringComparison.Ordinal)).ToList();
        Assert.Equal(120, lines.Count);

        var coll = VfpCollations.ByName(collation, 1252);
        var diff = new List<string>();
        using (var table = DbfTable.Open(dbf))
        using (var cdx = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table))
        {
            foreach (var line in lines)
            {
                var (exact, needle, op, scan, _, _, _) = Parse(line);
                var ctx = new EvaluationContext { Collation = coll, Exact = exact };
                string filter = $"name {op} '{needle}'";
                string viaScan = Join(QueryOptimizer.FindRecords(table, null, filter, ctx).RecordNumbers);
                string viaIndex = Join(QueryOptimizer.FindRecords(table, cdx, filter, ctx).RecordNumbers);
                if (viaScan != scan) diff.Add($"scan  {line[..line.IndexOf('|')]}: vfp [{scan}] ours [{viaScan}]");
                if (viaIndex != scan) diff.Add($"index {line[..line.IndexOf('|')]}: vfp [{scan}] ours [{viaIndex}]");
            }
        }

        // Needle + operator where VFP's own Rushmore loses rows of its scan under the cutting comparison.
        var vfpRushmoreLoss = lines.Select(Parse).Where(p => !p.Exact && p.Rush != p.Scan).Select(p => (p.Needle, p.Op)).ToHashSet();
        if (collation == "GENERAL") Assert.Equal(new HashSet<(string, string)> { (" ", ">") }, vfpRushmoreLoss);
        else Assert.Empty(vfpRushmoreLoss);

        foreach (bool ansi in new[] { false, true })
        foreach (bool exact in new[] { false, true })
        {
            using var session = new VfpSession(new EvaluationContext { Collation = coll, Exact = exact, Ansi = ansi });
            session.OpenDirectory(dir.Path);
            foreach (var line in lines.Where(l => l.Contains(exact ? " EXACT ON " : " EXACT OFF ", StringComparison.Ordinal)))
            {
                var (_, needle, op, _, _, sqlAnsiOff, sqlAnsiOn) = Parse(line);
                if (!ansi && vfpRushmoreLoss.Contains((needle, op))) continue;
                var rows = session.Execute($"SELECT RECNO() AS r FROM lb WHERE name {op} '{needle}'")!.Rows;
                string got = Join(rows.Select(r => Convert.ToInt32(r[0])));
                string want = ansi ? sqlAnsiOn : sqlAnsiOff;
                if (got != want) diff.Add($"SQL ANSI {(ansi ? "ON" : "OFF")} {line[..line.IndexOf('|')]}: vfp [{want}] ours [{got}]");
            }
        }
        Assert.True(diff.Count == 0, $"{diff.Count} differ from VFP9:\n" + string.Join("\n", diff));
    }

    // "GENERAL EXACT OFF [ A] <= | scan: 1 7 9 | rush: … | sqlAnsiOff: … | sqlAnsiOn: … "
    private static (bool Exact, string Needle, string Op, string Scan, string Rush, string SqlAnsiOff, string SqlAnsiOn) Parse(string line)
    {
        var parts = line.Split('|');
        string head = parts[0];
        int lb = head.IndexOf('['), rb = head.LastIndexOf(']');
        string Col(int i) => parts[i][(parts[i].IndexOf(':') + 1)..].Trim();
        return (head.Contains("EXACT ON", StringComparison.Ordinal), head[(lb + 1)..rb], head[(rb + 1)..].Trim(), Col(1), Col(2), Col(3), Col(4));
    }

    private static string Join(IEnumerable<int> recnos) => string.Join(" ", recnos.OrderBy(r => r));
}
