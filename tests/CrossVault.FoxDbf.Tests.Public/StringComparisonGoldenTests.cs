using System.Text;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The string operators against VFP9's own answers (<c>vfp_test/strcmp/</c>, produced by <c>mkstrcmp.prg</c>):
/// 98 operand pairs (different lengths, trailing and leading blanks, case, diacritics, ligatures, SPANISH ch/ll) under
/// MACHINE, GENERAL and SPANISH, SET EXACT OFF / ON and the operators <c>= == &lt; &lt;= &gt; &gt;= &lt;&gt;</c>, each as an
/// Xbase expression and as SQL under SET ANSI OFF / ON. What VFP9 shows:
/// <list type="bullet">
///   <item>Xbase SET EXACT OFF cuts the LEFT operand to the right one's length (MACHINE by characters, GENERAL by head
///   weights: <c>'AEtest' = 'Æ'</c>), trailing blanks count (<c>'Ab' = 'A '</c> is false), and the relational
///   operators follow the same rule (<c>'Ab' &lt;= 'A'</c> is true). SET EXACT ON pads the shorter with blanks.</item>
///   <item>SQL SET ANSI OFF cuts the LONGER operand to the shorter one; SET ANSI ON pads. SET EXACT does not matter.</item>
///   <item>Xbase <c>==</c> is character for character with equal length, whatever the collation (<c>'a' == 'A'</c> is
///   false under GENERAL); SQL <c>==</c> is the padded collation-aware comparison.</item>
/// </list>
/// </summary>
public sealed class StringComparisonGoldenTests
{
    private static readonly Encoding Cp1252 = CodePages();

    private static Encoding CodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    [Fact]
    public void Operators_MatchVfp9()
    {
        var lines = File.ReadAllLines(Fixtures.VfpTest(Path.Combine("strcmp", "strcmp.txt")), Cp1252);
        Assert.Equal(4116, lines.Length);
        var diff = new List<string>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            string coll = f[0], op = f[4], want = f[5];
            bool exact = f[1] == "ON";
            var row = new TestRow().Set("pl", f[2]).Set("pr", f[3]);
            var expr = VfpExpression.Parse("pl " + op + " pr");
            var collation = VfpCollations.ByName(coll, 1252);
            string got = string.Concat(
                Eval(expr, row, new EvaluationContext { Collation = collation, Exact = exact }),
                Eval(expr, row, new EvaluationContext { Collation = collation, Exact = exact, SqlSemantics = true, Ansi = false }),
                Eval(expr, row, new EvaluationContext { Collation = collation, Exact = exact, SqlSemantics = true, Ansi = true }));
            if (got == want) continue;
            string key = $"{coll} {f[1]} [{f[2]}] {op} [{f[3]}]";
            diff.Add($"{key}: vfp {want} ours {got}");
        }
        Assert.True(diff.Count == 0, $"{diff.Count} comparisons differ from VFP9:\n" + string.Join("\n", diff));
    }

    /// <summary>INLIST follows <c>=</c>, BETWEEN the relational operators; both honour SET EXACT on a C(10) value.</summary>
    [Fact]
    public void Functions_OnFullWidthField_MatchVfp9()
    {
        var lines = File.ReadAllLines(Fixtures.VfpTest(Path.Combine("strcmp", "fn.txt")), Cp1252);
        string[] values = { "A         ", "Ab        ", "          " };
        string[] exprs =
        {
            "INLIST(name, 'A')", "INLIST(name, 'A         ')", "INLIST('A', name)", "BETWEEN(name, 'A', 'A')",
            "BETWEEN(name, 'Ab', 'Ab')", "EMPTY(name)", null!, null!, "name $ 'Ab        '", "'A' $ name",
            "MAX(name, 'Ab') == name", null!, "name = ''",
        };
        var diff = new List<string>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            var ctx = new EvaluationContext { Exact = f[0] == "ON" };
            var row = new TestRow().Set("name", values[int.Parse(f[1]) - 1]);
            for (int i = 0; i < exprs.Length; i++)
            {
                if (exprs[i] is null) continue;   // ASCAN() and ISBLANK() are microVFP functions.
                string got = Eval(VfpExpression.Parse(exprs[i]), row, ctx);
                if (got != f[2][i].ToString()) diff.Add($"EXACT {f[0]} [{values[int.Parse(f[1]) - 1]}] {exprs[i]}: vfp {f[2][i]} ours {got}");
            }
            Assert.Equal(int.Parse(f[3]), VfpExpression.Parse("LEN(name + 'x')").Evaluate(row, ctx).AsDouble);
        }
        Assert.True(diff.Count == 0, string.Join("\n", diff));
    }

    private static string Eval(VfpExpression expr, IRowContext row, EvaluationContext ctx)
        => expr.Evaluate(row, ctx).AsLogical ? "T" : "F";
}
