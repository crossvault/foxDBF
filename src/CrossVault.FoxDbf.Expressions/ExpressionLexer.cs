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
    public string Text { get; }       // identifier text (original case)
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
                case '$': tokens.Add(new(TokenType.Dollar, start)); i++; continue;
                case '#': tokens.Add(new(TokenType.Ne, start)); i++; continue;
                case '*':
                    if (i + 1 < s.Length && s[i + 1] == '*') { tokens.Add(new(TokenType.Caret, start)); i += 2; }
                    else { tokens.Add(new(TokenType.Star, start)); i++; }
                    continue;
                case '=':
                    if (i + 1 < s.Length && s[i + 1] == '=') { tokens.Add(new(TokenType.ExactEq, start)); i += 2; }
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
            if (char.IsLetter(c) || c == '_')
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

    private static Token ReadNumber(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        if (i < s.Length && s[i] == '.')
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
        return new(TokenType.Number, start, "", val);
    }

    private static Token ReadIdentifier(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;

        // Qualified field references: alias.field (or m.var, customer.cust_id, a.b.c).
        // A '.' followed by a letter/underscore that is NOT a dotted operator/literal
        // (.AND. .OR. .NOT. .T. .F. .Y. .N. .NULL., each closed by a trailing '.')
        // is treated as a name qualifier and folded into a single identifier token.
        while (i < s.Length && s[i] == '.' && i + 1 < s.Length &&
               (char.IsLetter(s[i + 1]) || s[i + 1] == '_') &&
               !IsDottedOperatorAhead(s, i))
        {
            i++; // the '.'
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        }

        string text = s.Slice(start, i - start).ToString();
        return text.ToUpperInvariant() switch
        {
            "AND" => new(TokenType.And, start),
            "OR" => new(TokenType.Or, start),
            "NOT" => new(TokenType.Not, start),
            _ => new(TokenType.Identifier, start, text),
        };
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
            return new(TokenType.DateLit, pos, "", VfpValue.Date(default));

        string datePart = str;
        string? timePart = null;
        int sp = str.IndexOf(' ');
        if (sp >= 0)
        {
            datePart = str.Substring(0, sp);
            timePart = str.Substring(sp + 1).Trim();
        }

        string[] dateFormats = { "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy" };
        if (!DateTime.TryParseExact(datePart, dateFormats, Inv, DateTimeStyles.None, out var d))
            throw new ExpressionException($"Invalid date literal '{{{inner}}}'.", pos);

        if (timePart is null || timePart.Length == 0)
            return new(TokenType.DateLit, pos, "", VfpValue.Date(DateOnly.FromDateTime(d)));

        string[] timeFormats = { "HH:mm:ss", "H:mm:ss", "HH:mm", "h:mm:ss tt", "hh:mm:ss tt" };
        if (!DateTime.TryParseExact(timePart, timeFormats, Inv, DateTimeStyles.None, out var t))
            throw new ExpressionException($"Invalid datetime literal '{{{inner}}}'.", pos);

        var dt = new DateTime(d.Year, d.Month, d.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Unspecified);
        return new(TokenType.DateTimeLit, pos, "", VfpValue.DateTime(dt));
    }
}
