using System.Text;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// An expression FRAGMENT located by the PRG parser (the RHS of an assignment, an
/// IF/DO&#160;WHILE/DO&#160;CASE condition, FOR bounds, a REPLACE&#160;WITH value, a SCAN/DELETE
/// FOR clause, a RETURN value, …). The PRG parser does NOT have its own expression grammar:
/// every fragment is handed verbatim to <see cref="VfpExpression.Parse"/> (the existing,
/// battle-tested xBase expression engine). <see cref="Parsed"/> holds the resulting AST.
/// </summary>
internal sealed class PrgExpr
{
    /// <summary>The exact source text of the fragment (trimmed).</summary>
    public string Text { get; }

    /// <summary>
    /// The parsed expression, or <c>null</c> for the rare fragment the expression engine
    /// cannot represent yet (e.g. a by-reference <c>@var</c> argument inside a DECLARE-DLL
    /// call). Parser phase keeps the raw text so the whole corpus still parses.
    /// </summary>
    public VfpExpression? Parsed { get; }

    /// <summary>True when <see cref="Parsed"/> is a real <see cref="VfpExpression"/>.</summary>
    public bool IsParsed => Parsed is not null;

    private PrgExpr(string text, VfpExpression? parsed)
    {
        Text = text;
        Parsed = parsed;
    }

    // VFP's by-reference marker '@var' is legal only in argument position; it is a no-op for
    // PARSE-structure, so we strip it as a fallback when the raw fragment won't parse. The
    // scanner must leave @ characters in all literal forms untouched.
    private static bool IsIdentifierStart(char c) => VfpIdentifier.IsStart(c);

    private static string StripByRefMarkers(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inString = false;
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == quote) inString = false;
                continue;
            }

            if (c is '\'' or '"')
            {
                inString = true;
                quote = c;
                sb.Append(c);
                continue;
            }

            if (c == '[' && PrgScan.IsBracketLiteralStart(text, i))
            {
                int close = text.IndexOf(']', i + 1);
                int end = close < 0 ? text.Length : close + 1;
                sb.Append(text, i, end - i);
                i = end - 1;
                continue;
            }

            if (c == '@' && i + 1 < text.Length && IsIdentifierStart(text[i + 1]))
                continue;

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses <paramref name="text"/> via <see cref="VfpExpression.Parse"/>. Never throws:
    /// an unrepresentable fragment yields a <see cref="PrgExpr"/> with <see cref="Parsed"/>
    /// = <c>null</c> (still carrying its <see cref="Text"/>).
    /// </summary>
    public static PrgExpr Parse(string text)
    {
        var t = (text ?? string.Empty).Trim();
        // Normalise VFP memory-array syntax (bracket subscripts → parens; ALEN/AERROR array-name argument
        // → quoted name) so the engine can parse + the interpreter resolve it. Text is kept verbatim.
        var norm = MicroVfpExprRewrite.Normalize(t);
        try
        {
            return new PrgExpr(t, VfpExpression.Parse(norm));
        }
        catch (ExpressionException)
        {
            var stripped = StripByRefMarkers(norm);
            if (!string.Equals(stripped, norm, System.StringComparison.Ordinal))
            {
                try { return new PrgExpr(t, VfpExpression.Parse(stripped)); }
                catch (ExpressionException) { }
            }
            return new PrgExpr(t, null);
        }
    }

    public override string ToString() => Text;
}
