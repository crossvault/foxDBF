using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// The text VFP9 stores for an index KEY/FOR expression. VFP does not keep what was typed: it compiles the
/// expression and writes back the DECOMPILED form (into a standalone <c>.idx</c> header @16/@236 and a CDX
/// tag's expression pool alike — <c>NORMALIZE()</c> and <c>FILTER()</c> return the same text upper-cased).
/// Probed against the VFP9 runtime (IdxExpressionTextVfp9OracleTests):
/// <list type="bullet">
/// <item>blanks between tokens are dropped; parentheses the user wrote are kept, never added;</item>
/// <item><c>AND/OR/NOT/!</c> → <c>.AND./.OR./.NOT.</c>; <c>&lt;&gt;</c>, <c>!=</c> → <c>#</c>;
/// <c>&gt;=</c> → <c>=&gt;</c>; <c>=&lt;</c> → <c>&lt;=</c>; <c>^</c> → <c>**</c>; <c>a % b</c> →
/// <c>MOD(a,b)</c>; unary <c>+</c> disappears;</item>
/// <item>built-in function names upper-case and unabbreviated (<c>recn()</c> → <c>RECNO()</c>, even the
/// quirky <c>FIELD(1)</c> → <c>FIELDS(1)</c>); fields, variables and UDF names lower-case; an alias upper-case
/// (<c>t->name</c> → <c>T.name</c>, <c>m.x</c> → <c>M.x</c>);</item>
/// <item>a string keeps its content, delimited by <c>"</c>, else <c>'</c>, else <c>[ ]</c>;</item>
/// <item>numeric constants are FOLDED (<c>1+2</c> → <c>3</c>, <c>(3)</c> → <c>3</c>, <c>-(3)</c> → <c>-3</c>)
/// and re-printed with their decimals (<c>1.50</c>, <c>007</c> → <c>7</c>, <c>.5</c> → <c>0.5</c>,
/// <c>1e2</c> → <c>100</c>, <c>0x1F</c> → <c>31</c>; <c>$1</c> → <c>$1.0000</c>);</item>
/// <item>dates/datetimes print strict and 24-hour: <c>{^2000/01/02}</c>, <c>{^2000/01/02 15:04:05}</c>, <c>{}</c>.</item>
/// </list>
/// Anything outside the probed subset (more than 15 significant digits, non-strict date literals, a
/// constant fold whose result VFP would print rounded and then fold again, qualified calls, non-ASCII names …)
/// keeps the WHOLE text verbatim — the pre-canonicalisation behaviour — rather than guess.
/// </summary>
internal static class VfpExpressionText
{
    /// <summary>NORMALIZE()/FILTER()/KEY() use the decompiled text with upper-case identifiers, while string
    /// literals retain their contents (unlike FOR(), which uppercases the entire stored text).</summary>
    public static string Normalize(string text)
    {
        string canonical = Canonicalize(text);
        var result = new StringBuilder(canonical.Length);
        for (int i = 0; i < canonical.Length; i++)
        {
            char c = canonical[i];
            if (c is '\'' or '"' or '[')
            {
                char closing = c == '[' ? ']' : c;
                result.Append(c);
                while (++i < canonical.Length)
                {
                    result.Append(canonical[i]);
                    if (canonical[i] == closing) break;
                }
            }
            else
            {
                result.Append(char.ToUpperInvariant(c));
            }
        }
        return result.ToString();
    }

    /// <summary>The text VFP9 would store for <paramref name="text"/>, or <paramref name="text"/> itself when
    /// it is outside the reproduced subset (or does not parse).</summary>
    public static string Canonicalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try
        {
            string canonical = Print(Fold(new Parser(new ExpressionLexer(text).Tokenize()).ParseAll()));
            // Safety net: the stored text is re-parsed by every reader (and by REINDEX), so it must parse with
            // this engine and be a fixed point; otherwise keep what the caller wrote.
            ExpressionParser.Parse(canonical);
            string again = Print(Fold(new Parser(new ExpressionLexer(canonical).Tokenize()).ParseAll()));
            return again == canonical ? canonical : text;
        }
        catch (ExpressionException) { return text; }
        catch (NotCanonical) { return text; }
    }

    private sealed class NotCanonical : Exception { }

    // ---- syntax tree (keeps the user's parentheses and the literals' source spelling) -------------------

    private abstract class Node { }

    /// <summary>A numeric constant: value and the decimals VFP prints it with. <see cref="Inexact"/> marks a
    /// folded division/power result that the printed decimals round.</summary>
    private sealed class Num(decimal value, int decimals, bool inexact = false) : Node
    {
        public decimal Value { get; } = value;
        public int Decimals { get; } = decimals;
        public bool Inexact { get; } = inexact;
    }

    private sealed class Cur(decimal value) : Node { public decimal Value { get; } = value; }
    private sealed class Lit(string printed) : Node { public string Printed { get; } = printed; }
    private sealed class Name(string printed) : Node { public string Printed { get; } = printed; }
    private sealed class Call(string name, List<Node> args) : Node
    {
        public string Name { get; } = name;
        public List<Node> Args { get; } = args;
    }
    private sealed class Paren(Node inner) : Node { public Node Inner { get; } = inner; }
    private sealed class Unary(TokenType op, Node operand) : Node
    {
        public TokenType Op { get; } = op;
        public Node Operand { get; } = operand;
    }
    private sealed class Binary(TokenType op, Node left, Node right) : Node
    {
        public TokenType Op { get; } = op;
        public Node Left { get; } = left;
        public Node Right { get; } = right;
    }

    // ---- parser: the same binding powers as ExpressionParser ----------------------------------------------

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;
        private int _depth;

        private Token Peek => tokens[_pos];

        public Node ParseAll()
        {
            if (Peek.Type == TokenType.Eof) throw new NotCanonical();
            var node = ParseExpr(0);
            if (Peek.Type != TokenType.Eof) throw new NotCanonical();
            return node;
        }

        private Node ParseExpr(int minBp)
        {
            if (++_depth > 200) throw new NotCanonical();
            try
            {
                var left = ParsePrefix();
                while (true)
                {
                    var t = Peek;
                    int lbp = LeftBp(t.Type);
                    if (lbp == 0 || lbp < minBp) break;
                    _pos++;
                    var right = ParseExpr(t.Type == TokenType.Caret ? lbp : lbp + 1);
                    left = new Binary(t.Type, left, right);
                }
                return left;
            }
            finally { _depth--; }
        }

        private Node ParsePrefix()
        {
            var t = tokens[_pos++];
            switch (t.Type)
            {
                case TokenType.Number: return NumberLiteral(t.Text);
                case TokenType.String: return new Lit(StringLiteral(t.Value.AsString));
                case TokenType.DateLit: return new Lit(DateLiteral(t, withTime: false));
                case TokenType.DateTimeLit: return new Lit(DateLiteral(t, withTime: true));
                case TokenType.True: return new Lit(".T.");
                case TokenType.False: return new Lit(".F.");
                case TokenType.NullLit: return new Lit(".NULL.");
                case TokenType.Minus: return new Unary(TokenType.Minus, ParseExpr(71));
                case TokenType.Plus: return new Unary(TokenType.Plus, ParseExpr(71));
                case TokenType.Not:
                case TokenType.Bang: return new Unary(TokenType.Not, ParseExpr(30));
                case TokenType.LParen:
                {
                    var inner = ParseExpr(0);
                    if (tokens[_pos++].Type != TokenType.RParen) throw new NotCanonical();
                    return new Paren(inner);
                }
                case TokenType.Identifier:
                    return Peek.Type == TokenType.LParen ? ParseCall(t.Text) : NameReference(t.Text);
                default: throw new NotCanonical();
            }
        }

        private Node ParseCall(string name)
        {
            if (name.Contains('.')) throw new NotCanonical();         // obj.method(): not an index expression
            _pos++; // '('
            var args = new List<Node>();
            if (Peek.Type != TokenType.RParen)
            {
                args.Add(ParseExpr(0));
                while (Peek.Type == TokenType.Comma)
                {
                    _pos++;
                    args.Add(ParseExpr(0));
                }
            }
            if (tokens[_pos++].Type != TokenType.RParen) throw new NotCanonical();
            return new Call(VfpFunctionNames.Canonical(AsciiName(name)), args);
        }

        private static int LeftBp(TokenType t) => t switch
        {
            TokenType.Or => 10,
            TokenType.And => 20,
            TokenType.Eq or TokenType.ExactEq or TokenType.Ne or
            TokenType.Lt or TokenType.Le or TokenType.Gt or TokenType.Ge or
            TokenType.Dollar => 40,
            TokenType.Plus or TokenType.Minus => 50,
            TokenType.Star or TokenType.Slash or TokenType.Percent => 60,
            TokenType.Caret => 70,
            _ => 0,
        };
    }

    private static string AsciiName(string name)
    {
        foreach (char c in name)
            if (c > 0x7F) throw new NotCanonical();                    // VFP lower-cases per code page; not probed
        return name;
    }

    private static Node NameReference(string text)
    {
        string name = AsciiName(text);
        int dot = name.IndexOf('.');
        if (dot < 0)
            return name.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                ? new Lit(".NULL.")
                : new Name(name.ToLowerInvariant());
        if (name.IndexOf('.', dot + 1) >= 0) throw new NotCanonical(); // a.b.c: not probed
        return new Name(name[..dot].ToUpperInvariant() + "." + name[(dot + 1)..].ToLowerInvariant());
    }

    // ---- literals -------------------------------------------------------------------------------------------

    private static Node NumberLiteral(string source)
    {
        if (source.StartsWith('$'))
        {
            if (!decimal.TryParse(source.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal c))
                throw new NotCanonical();
            return new Cur(Math.Round(c, 4, MidpointRounding.AwayFromZero));
        }
        if (source.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return new Num(long.Parse(source.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture), 0);

        // Decimals = digits after the point, shifted by an exponent (1.25e1 → 12.5, 1.5e-10 → 0.00000000015).
        int e = source.IndexOfAny(['e', 'E']);
        string mantissa = e < 0 ? source : source[..e];
        int exponent = e < 0 ? 0 : int.Parse(source.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int point = mantissa.IndexOf('.');
        int fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        if (!decimal.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            throw new NotCanonical();
        return new Num(value, Math.Max(0, fraction - exponent));
    }

    private static string StringLiteral(string content)
    {
        if (!content.Contains('"')) return "\"" + content + "\"";
        if (!content.Contains('\'')) return "'" + content + "'";
        if (!content.Contains(']')) return "[" + content + "]";
        throw new NotCanonical();
    }

    private static string DateLiteral(Token t, bool withTime)
    {
        string inner = t.Text.Trim();
        if (!withTime && t.Value.IsEmptyDate) return "{}";
        if (!inner.StartsWith('^')) throw new NotCanonical();          // {01/02/2000}: depends on SET DATE
        return withTime
            ? "{^" + t.Value.AsDateTime.ToString("yyyy'/'MM'/'dd HH':'mm':'ss", CultureInfo.InvariantCulture) + "}"
            : "{^" + t.Value.AsDate.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture) + "}";
    }

    // ---- constant folding -------------------------------------------------------------------------------------

    private static Node Fold(Node node)
    {
        switch (node)
        {
            case Paren p:
            {
                var inner = Fold(p.Inner);
                return inner is Num ? inner : new Paren(inner);       // (1+2) → 3, but ((n45)) stays
            }
            case Unary u:
            {
                // VFP9 KEY(): -n^2 becomes -N**2, -(n^2) retains parentheses, -2^2 folds to 4.
                // Unary signs bind before powers (MicroVfpNpvRound3Vfp9OracleTests).
                var operand = Fold(u.Operand);
                if (u.Op == TokenType.Plus) return operand;            // +3 → 3, +n45 → n45
                if (u.Op == TokenType.Minus && operand is Num n)
                    return new Num(-n.Value, n.Decimals, n.Inexact);   // -(3) → -3, -(-3) → 3
                return new Unary(u.Op, operand);
            }
            case Binary b:
            {
                var left = Fold(b.Left);
                var right = Fold(b.Right);
                if (left is Num l && right is Num r && b.Op is TokenType.Plus or TokenType.Minus
                        or TokenType.Star or TokenType.Slash or TokenType.Caret)
                    return FoldArithmetic(b.Op, l, r);
                return new Binary(b.Op, left, right);
            }
            case Call c:
            {
                var args = new List<Node>(c.Args.Count);
                foreach (var a in c.Args) args.Add(Fold(a));
                return new Call(c.Name, args);
            }
            default:
                return node;
        }
    }

    /// <summary>VFP's compile-time fold: + and - keep the larger decimals, * their sum, / the larger, ** the
    /// base's (probed: 1.5*2.5 → 3.75, 1.5*2 → 3.0, 10/4 → 3, 1.0/3 → 0.3, 2**0.5 → 1, 2.0**2 → 4.0).</summary>
    private static Num FoldArithmetic(TokenType op, Num l, Num r)
    {
        // A rounded-on-print operand: whether VFP folds the printed or the exact value further is unprobed.
        if (l.Inexact || r.Inexact) throw new NotCanonical();
        try
        {
            switch (op)
            {
                case TokenType.Plus: return new Num(l.Value + r.Value, Math.Max(l.Decimals, r.Decimals));
                case TokenType.Minus: return new Num(l.Value - r.Value, Math.Max(l.Decimals, r.Decimals));
                case TokenType.Star: return new Num(l.Value * r.Value, l.Decimals + r.Decimals);
                case TokenType.Slash:
                {
                    if (r.Value == 0) throw new NotCanonical();
                    return Rounded(l.Value / r.Value, Math.Max(l.Decimals, r.Decimals));
                }
                default:
                {
                    // VFP9 folds a signed base before exponentiation: -2**2 is 4.
                    double p = Math.Pow((double)l.Value, (double)r.Value);
                    if (double.IsNaN(p) || double.IsInfinity(p) || Math.Abs(p) > 1e18) throw new NotCanonical();
                    return Rounded((decimal)p, l.Decimals);
                }
            }
        }
        catch (OverflowException) { throw new NotCanonical(); }
    }

    private static Num Rounded(decimal value, int decimals)
    {
        if (decimals > 18) throw new NotCanonical();
        decimal printed = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return new Num(value, decimals, inexact: printed != value);
    }

    // ---- printing ---------------------------------------------------------------------------------------------

    private static string Print(Node node)
    {
        var sb = new StringBuilder();
        Print(node, sb);
        return sb.ToString();
    }

    private static void Print(Node node, StringBuilder sb)
    {
        switch (node)
        {
            case Num n: sb.Append(FormatNumber(n.Value, n.Decimals)); break;
            case Cur c:
                sb.Append('$').Append(FormatNumber(c.Value, 4)); break;
            case Lit l: sb.Append(l.Printed); break;
            case Name nm: sb.Append(nm.Printed); break;
            case Paren p: sb.Append('('); Print(p.Inner, sb); sb.Append(')'); break;
            case Call c:
                sb.Append(c.Name).Append('(');
                for (int i = 0; i < c.Args.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Print(c.Args[i], sb);
                }
                sb.Append(')');
                break;
            case Unary u:
                sb.Append(u.Op == TokenType.Not ? ".NOT." : "-");
                Print(u.Operand, sb);
                break;
            case Binary b when b.Op == TokenType.Percent:
                sb.Append("MOD(");
                Print(b.Left, sb);
                sb.Append(',');
                Print(b.Right, sb);
                sb.Append(')');
                break;
            case Binary b:
                Print(b.Left, sb);
                sb.Append(b.Op switch
                {
                    TokenType.Plus => "+",
                    TokenType.Minus => "-",
                    TokenType.Star => "*",
                    TokenType.Slash => "/",
                    TokenType.Caret => "**",
                    TokenType.Eq => "=",
                    TokenType.ExactEq => "==",
                    TokenType.Ne => "#",
                    TokenType.Lt => "<",
                    TokenType.Le => "<=",
                    TokenType.Gt => ">",
                    TokenType.Ge => "=>", // VFP9 prints => even for FILTER()/SET('FILTER') given >=.
                    TokenType.Dollar => "$",
                    TokenType.And => ".AND.",
                    TokenType.Or => ".OR.",
                    _ => throw new NotCanonical(),
                });
                Print(b.Right, sb);
                break;
            default: throw new NotCanonical();
        }
    }

    /// <summary>A constant with <paramref name="decimals"/> decimals, as VFP prints it. Only values VFP prints
    /// exactly are reproduced: ≤ 15 significant digits and below 1E19 (beyond, VFP pads with zeros or switches
    /// to <c> 1.0000000000000E+19</c>).</summary>
    private static string FormatNumber(decimal value, int decimals)
    {
        if (decimals > 18) throw new NotCanonical();
        decimal rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0 && value < 0) throw new NotCanonical();      // a printed "-0": not probed
        if (Math.Abs(rounded) >= 1e19m) throw new NotCanonical();
        string s = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        string digits = s.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0').TrimEnd('0');
        if (digits.Length > 15) throw new NotCanonical();
        return s;
    }
}
