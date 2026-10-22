using System;
using System.Collections.Generic;
using System.Globalization;

namespace CrossVault.FoxDbf.Expressions;

internal enum TokenType
{
    Number, String, DateLit, DateTimeLit,
    True, False, NullLit,
    Identifier,
    And, Or, Not,
    Plus, Minus, Star, Slash, Percent, Caret,
    LParen, RParen, Comma,
    Eq, ExactEq, Ne, Lt, Le, Gt, Ge, Dollar, Bang,
    Eof,
}

internal readonly struct Token
{
    public TokenType Type { get; }
    public int Pos { get; }
    public string Text { get; }       // identifier text (original case); number/date literal source text
    public VfpValue Value { get; }     // literal payload

    public Token(TokenType type, int pos, string text = "", VfpValue value = default)
    {
        Type = type; Pos = pos; Text = text; Value = value;
    }
}

/// <summary>
/// Span-based, alloc-light tokenizer for VFP/xBase expressions. Throws
/// <see cref="ExpressionException"/> on malformed lexical input.
/// </summary>
internal sealed class ExpressionLexer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _src;

    public ExpressionLexer(string src) => _src = src;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        ReadOnlySpan<char> s = _src;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }

            int start = i;
            switch (c)
            {
                case '(': tokens.Add(new(TokenType.LParen, start)); i++; continue;
                case ')': tokens.Add(new(TokenType.RParen, start)); i++; continue;
                case ',': tokens.Add(new(TokenType.Comma, start)); i++; continue;
                case '+': tokens.Add(new(TokenType.Plus, start)); i++; continue;
                case '-': tokens.Add(new(TokenType.Minus, start)); i++; continue;
                case '/': tokens.Add(new(TokenType.Slash, start)); i++; continue;
                case '%': tokens.Add(new(TokenType.Percent, start)); i++; continue;
                case '^': tokens.Add(new(TokenType.Caret, start)); i++; continue;
                case '$':
                    // In operand position `$12.5` is a CURRENCY literal (VFP stores it so in a CDX/IDX
                    // KEY/FOR text, e.g. `n45=$12.5000`); after an operand it is the substring operator.
                    if (!EndsOperand(tokens) && StartsNumber(s, i + 1))
                    {
                        i++;
                        Token number = ReadNumber(s, ref i);
                        tokens.Add(new(TokenType.Number, start, "$" + number.Text,
                            VfpValue.Currency(Math.Round(number.Value.AsNumber, 4, MidpointRounding.AwayFromZero))));
                        continue;
                    }
                    tokens.Add(new(TokenType.Dollar, start)); i++; continue;
                case '#': tokens.Add(new(TokenType.Ne, start)); i++; continue;
                case '*':
                    if (i + 1 < s.Length && s[i + 1] == '*') { tokens.Add(new(TokenType.Caret, start)); i += 2; }
                    else { tokens.Add(new(TokenType.Star, start)); i++; }
                    continue;
                case '=':
                    if (i + 1 < s.Length && s[i + 1] == '=') { tokens.Add(new(TokenType.ExactEq, start)); i += 2; }
                    // `=>` / `=<` are VFP spellings of >= / <=; VFP itself stores >= as `=>` in index headers.
                    else if (i + 1 < s.Length && s[i + 1] == '>') { tokens.Add(new(TokenType.Ge, start)); i += 2; }
                    else if (i + 1 < s.Length && s[i + 1] == '<') { tokens.Add(new(TokenType.Le, start)); i += 2; }
                    else { tokens.Add(new(TokenType.Eq, start)); i++; }
                    continue;
                case '!':
                    if (i + 1 < s.Length && s[i + 1] == '=') { tokens.Add(new(TokenType.Ne, start)); i += 2; }
                    else { tokens.Add(new(TokenType.Bang, start)); i++; }
                    continue;
                case '<':
                    if (i + 1 < s.Length && s[i + 1] == '=') { tokens.Add(new(TokenType.Le, start)); i += 2; }
                    else if (i + 1 < s.Length && s[i + 1] == '>') { tokens.Add(new(TokenType.Ne, start)); i += 2; }
                    else { tokens.Add(new(TokenType.Lt, start)); i++; }
                    continue;
                case '>':
                    if (i + 1 < s.Length && s[i + 1] == '=') { tokens.Add(new(TokenType.Ge, start)); i += 2; }
                    else { tokens.Add(new(TokenType.Gt, start)); i++; }
                    continue;
                case '\'':
                case '"':
                    tokens.Add(ReadString(s, ref i, c));
                    continue;
                case '[':
                    // VFP's third string delimiter: [ ... ].
                    tokens.Add(ReadBracketString(s, ref i));
                    continue;
                case '{':
                    tokens.Add(ReadDate(s, ref i));
                    continue;
                case '.':
                    if (i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
                    {
                        tokens.Add(ReadNumber(s, ref i));
                        continue;
                    }
                    tokens.Add(ReadDotted(s, ref i));
                    continue;
            }

            if (char.IsAsciiDigit(c))
            {
                tokens.Add(ReadNumber(s, ref i));
                continue;
            }
            if (VfpIdentifier.IsStart(c))
            {
                tokens.Add(ReadIdentifier(s, ref i));
                continue;
            }

            throw new ExpressionException($"Unexpected character '{c}'.", start);
        }
        tokens.Add(new(TokenType.Eof, i));
        return tokens;
    }

    private static Token ReadString(ReadOnlySpan<char> s, ref int i, char quote)
    {
        int start = i;
        i++; // opening quote
        int contentStart = i;
        while (i < s.Length && s[i] != quote) i++;
        if (i >= s.Length)
            throw new ExpressionException("Unterminated string literal.", start);
        string content = s.Slice(contentStart, i - contentStart).ToString();
        i++; // closing quote
        return new(TokenType.String, start, "", VfpValue.Character(content));
    }

    private static Token ReadBracketString(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        i++; // opening '['
        int contentStart = i;
        while (i < s.Length && s[i] != ']') i++;
        if (i >= s.Length)
            throw new ExpressionException("Unterminated string literal.", start);
        string content = s.Slice(contentStart, i - contentStart).ToString();
        i++; // closing ']'
        return new(TokenType.String, start, "", VfpValue.Character(content));
    }

    /// <summary>True when the last token ends an operand (so a following <c>$</c> is the operator).</summary>
    private static bool EndsOperand(List<Token> tokens)
    {
        if (tokens.Count == 0) return false;
        return tokens[^1].Type is TokenType.Number or TokenType.String or TokenType.DateLit
            or TokenType.DateTimeLit or TokenType.True or TokenType.False or TokenType.NullLit
            or TokenType.Identifier or TokenType.RParen;
    }

    private static bool StartsNumber(ReadOnlySpan<char> s, int i)
        => i < s.Length && (char.IsAsciiDigit(s[i])
            || (s[i] == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])));

    private static Token ReadNumber(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        // 0x1F: hexadecimal integer (VFP stores it as its decimal value, `31`).
        if (s[i] == '0' && i + 2 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X') && char.IsAsciiHexDigit(s[i + 2]))
        {
            i += 2;
            while (i < s.Length && char.IsAsciiHexDigit(s[i])) i++;
            var hex = s.Slice(start + 2, i - start - 2);
            if (hex.Length > 15 || !long.TryParse(hex, NumberStyles.AllowHexSpecifier, Inv, out long h))
                throw new ExpressionException($"Invalid numeric literal '{s.Slice(start, i - start).ToString()}'.", start);
            return new(TokenType.Number, start, s.Slice(start, i - start).ToString(), VfpValue.Number((decimal)h));
        }
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        // `3.AND.x`: the '.' opens a dotted operator, it is not this number's decimal point.
        if (i < s.Length && s[i] == '.' && !IsDottedOperatorAhead(s, i))
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int save = i;
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            if (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            }
            else i = save; // not an exponent after all
        }
        var span = s.Slice(start, i - start);
        VfpValue val;
        if (decimal.TryParse(span, NumberStyles.Float, Inv, out var dec))
            val = VfpValue.Number(dec);
        else if (double.TryParse(span, NumberStyles.Float, Inv, out var db))
            val = VfpValue.Number(db);
        else
            throw new ExpressionException($"Invalid numeric literal '{span.ToString()}'.", start);
        return new(TokenType.Number, start, span.ToString(), val);
    }

    private static Token ReadIdentifier(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        while (i < s.Length && (VfpIdentifier.IsPart(s[i]))) i++;

        // Qualified field references: alias.field (or m.var, customer.cust_id, a.b.c).
        // A '.' followed by a letter/underscore that is NOT a dotted operator/literal
        // (.AND. .OR. .NOT. .T. .F. .Y. .N. .NULL., each closed by a trailing '.')
        // is treated as a name qualifier and folded into a single identifier token.
        // The xBase `alias->field` arrow is the same qualifier; it is folded to '.' (as VFP stores it).
        bool arrow = false;
        while (i + 1 < s.Length)
        {
            if (s[i] == '.' && VfpIdentifier.IsStart(s[i + 1]) && !IsDottedOperatorAhead(s, i))
                i++; // the '.'
            else if (s[i] == '-' && s[i + 1] == '>' && i + 2 < s.Length && VfpIdentifier.IsStart(s[i + 2]))
            {
                arrow = true;
                i += 2; // the '->'
            }
            else
                break;
            while (i < s.Length && (VfpIdentifier.IsPart(s[i]))) i++;
        }

        // The classic xBase qualifier alias->field (also m->var) is the same reference as alias.field and
        // is folded into the identical "alias.field" token. VFP9 accepts blanks on either side of the arrow
        // but not inside it ("a- >b" is error 1231), and only one arrow per reference ("a->b->c" is an
        // error); "a - b > c" never contains the adjacent pair, so arithmetic/comparison is unaffected.
        if (TryArrowQualifier(s, i, out int fieldStart))
        {
            string alias = s.Slice(start, i - start).ToString();
            if (alias.Contains('.'))
                throw new ExpressionException($"'{alias}' is not an alias for a '->' qualifier.", start);
            i = fieldStart;
            while (i < s.Length && VfpIdentifier.IsPart(s[i])) i++;
            if (TryArrowQualifier(s, i, out _))
                throw new ExpressionException("Only one '->' qualifier is allowed in a field reference.", i);
            return new(TokenType.Identifier, start, alias + "." + s.Slice(fieldStart, i - fieldStart).ToString());
        }

        string text = s.Slice(start, i - start).ToString();
        if (arrow) text = text.Replace("->", ".", StringComparison.Ordinal);
        return text.ToUpperInvariant() switch
        {
            "AND" => new(TokenType.And, start),
            "OR" => new(TokenType.Or, start),
            "NOT" => new(TokenType.Not, start),
            _ => new(TokenType.Identifier, start, text),
        };
    }

    /// <summary>
    /// True when <paramref name="i"/> (just past an identifier) starts an xBase <c>-&gt;</c> qualifier:
    /// optional blanks, the adjacent pair <c>-&gt;</c>, optional blanks, then an identifier start, whose
    /// index is returned in <paramref name="fieldStart"/>.
    /// </summary>
    internal static bool TryArrowQualifier(ReadOnlySpan<char> s, int i, out int fieldStart)
    {
        fieldStart = -1;
        int j = i;
        while (j < s.Length && s[j] is ' ' or '\t') j++;
        if (j + 1 >= s.Length || s[j] != '-' || s[j + 1] != '>') return false;
        j += 2;
        while (j < s.Length && s[j] is ' ' or '\t') j++;
        if (j >= s.Length || !VfpIdentifier.IsStart(s[j])) return false;
        fieldStart = j;
        return true;
    }

    /// <summary>
    /// True when the '.' at <paramref name="dot"/> begins a dotted OPERATOR/LITERAL
    /// (.AND. .OR. .NOT. .T. .F. .Y. .N. .NULL.) rather than a field qualifier. Such
    /// tokens are a recognised word closed by a trailing '.'.
    /// </summary>
    private static bool IsDottedOperatorAhead(ReadOnlySpan<char> s, int dot)
    {
        int j = dot + 1;
        int wordStart = j;
        while (j < s.Length && char.IsAsciiLetter(s[j])) j++;
        // Must be closed by a trailing '.' to be an operator/literal form.
        if (j >= s.Length || s[j] != '.') return false;
        var word = s.Slice(wordStart, j - wordStart);
        return word.Equals("AND", StringComparison.OrdinalIgnoreCase)
            || word.Equals("OR", StringComparison.OrdinalIgnoreCase)
            || word.Equals("NOT", StringComparison.OrdinalIgnoreCase)
            || word.Equals("T", StringComparison.OrdinalIgnoreCase)
            || word.Equals("F", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || word.Equals("N", StringComparison.OrdinalIgnoreCase)
            || word.Equals("NULL", StringComparison.OrdinalIgnoreCase);
    }

    // Handles dotted operators/literals: .AND. .OR. .NOT. .T. .F. .Y. .N. .NULL.
    private static Token ReadDotted(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        i++; // leading '.'
        int wordStart = i;
        while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
        if (wordStart == i)
            throw new ExpressionException("Unexpected '.'.", start);
        string word = s.Slice(wordStart, i - wordStart).ToString().ToUpperInvariant();
        // Consume trailing '.' if present.
        if (i < s.Length && s[i] == '.') i++;
        return word switch
        {
            "AND" => new(TokenType.And, start),
            "OR" => new(TokenType.Or, start),
            "NOT" => new(TokenType.Not, start),
            "T" or "Y" => new(TokenType.True, start),
            "F" or "N" => new(TokenType.False, start),
            "NULL" => new(TokenType.NullLit, start),
            _ => throw new ExpressionException($"Unknown operator '.{word}.'.", start),
        };
    }

    private static Token ReadDate(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        i++; // '{'
        int contentStart = i;
        while (i < s.Length && s[i] != '}') i++;
        if (i >= s.Length)
            throw new ExpressionException("Unterminated date literal.", start);
        string inner = s.Slice(contentStart, i - contentStart).ToString();
        i++; // '}'
        return ParseDateLiteral(inner, start);
    }

    private static Token ParseDateLiteral(string inner, int pos)
    {
        string str = inner.Trim();
        if (str.StartsWith('^')) str = str.Substring(1).Trim();
        if (str.Length == 0 || str is "/" or "//" or "..")
            return new(TokenType.DateLit, pos, inner, VfpValue.Date(default));

        string datePart = str;
        string? timePart = null;
        int sp = str.IndexOf(' ');
        if (sp >= 0)
        {
            datePart = str.Substring(0, sp);
            timePart = str.Substring(sp + 1).Trim();
        }

        string[] dateFormats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy/M/d", "MM/dd/yyyy", "M/d/yyyy" };
        if (!DateTime.TryParseExact(datePart, dateFormats, Inv, DateTimeStyles.None, out var d))
            throw new ExpressionException($"Invalid date literal '{{{inner}}}'.", pos);

        if (timePart is null || timePart.Length == 0)
            return new(TokenType.DateLit, pos, inner, VfpValue.Date(DateOnly.FromDateTime(d)));

        string[] timeFormats = { "HH:mm:ss", "H:mm:ss", "HH:mm", "h:mm:ss tt", "hh:mm:ss tt" };
        if (!DateTime.TryParseExact(timePart, timeFormats, Inv, DateTimeStyles.None, out var t))
            throw new ExpressionException($"Invalid datetime literal '{{{inner}}}'.", pos);

        var dt = new DateTime(d.Year, d.Month, d.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Unspecified);
        return new(TokenType.DateTimeLit, pos, inner, VfpValue.DateTime(dt));
    }
}
