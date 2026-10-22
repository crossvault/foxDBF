using System;
using System.Collections.Generic;
using System.Text;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// Low-level, string- and paren-aware scanning helpers shared by the parser. All of these
/// respect VFP string literals (<c>'…'</c>, <c>"…"</c>) and nesting (<c>()</c>, <c>[]</c>,
/// <c>{}</c>) so that a keyword/comma/operator INSIDE an expression is never mistaken for a
/// statement-level token.
/// </summary>
internal static class PrgScan
{
    private static bool IsIdentStart(char c) => VfpIdentifier.IsStart(c);
    private static bool IsIdentChar(char c) => VfpIdentifier.IsPart(c);

    internal static bool IsBracketLiteralStart(string s, int index)
    {
        char prev = index > 0 ? s[index - 1] : '\0';
        return prev != ')' && prev != ']' && !IsIdentChar(prev);
    }

    /// <summary>Top-level (depth-0, outside strings) identifier words, with positions.</summary>
    public static List<(int Start, int Len, string Upper)> TopWords(string s)
    {
        var list = new List<(int, int, string)>();
        int depth = 0;
        bool inStr = false;
        char q = '\0';
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == q) inStr = false;
                i++;
                continue;
            }
            switch (c)
            {
                case '\'':
                case '"':
                    inStr = true; q = c; i++; continue;
                case '(':
                case '{':
                    depth++; i++; continue;
                case '[':
                    if (IsBracketLiteralStart(s, i))
                    {
                        int close = s.IndexOf(']', i + 1);
                        i = close < 0 ? s.Length : close + 1;
                        continue;
                    }
                    depth++; i++; continue;
                case ')':
                case ']':
                case '}':
                    if (depth > 0) depth--; i++; continue;
            }
            if (depth == 0 && IsIdentStart(c))
            {
                int start = i;
                i++;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                list.Add((start, i - start, s.Substring(start, i - start).ToUpperInvariant()));
                continue;
            }
            i++;
        }
        return list;
    }

    /// <summary>0-based position of the first top-level whole-word <paramref name="keyword"/>,
    /// or -1. Comparison is case-insensitive.</summary>
    public static int IndexOfKeyword(string s, string keyword)
    {
        foreach (var (start, _, upper) in TopWords(s))
            if (upper == keyword) return start;
        return -1;
    }

    /// <summary>Position + identity of the first top-level word matching any of
    /// <paramref name="keywords"/> (in source order). Returns -1 / null when none.</summary>
    public static int IndexOfAnyKeyword(string s, out string? which, params string[] keywords)
    {
        // TopWords is in source order, so the first match is the earliest.
        foreach (var (start, _, upper) in TopWords(s))
            foreach (var k in keywords)
                if (upper == k) { which = k; return start; }
        which = null;
        return -1;
    }

    /// <summary>Splits on top-level (depth-0, non-string) commas; trims each piece.</summary>
    public static List<string> SplitTopCommas(string s)
    {
        var parts = new List<string>();
        int depth = 0;
        bool inStr = false;
        char q = '\0';
        int last = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == q) inStr = false; continue; }
            switch (c)
            {
                case '\'':
                case '"': inStr = true; q = c; break;
                case '(':
                case '{': depth++; break;
                case '[':
                    if (IsBracketLiteralStart(s, i))
                    {
                        int close = s.IndexOf(']', i + 1);
                        i = close < 0 ? s.Length : close;
                    }
                    else depth++;
                    break;
                case ')':
                case ']':
                case '}': if (depth > 0) depth--; break;
                case ',':
                    if (depth == 0) { parts.Add(s.Substring(last, i - last).Trim()); last = i + 1; }
                    break;
            }
        }
        var tail = s.Substring(last).Trim();
        if (tail.Length > 0 || parts.Count > 0) parts.Add(tail);
        return parts;
    }

    /// <summary>Tokenises a command tail into clause tokens: identifier/operator runs,
    /// balanced <c>(…)</c> groups (kept whole, parens included), string literals, and a bare
    /// <c>,</c>. Whitespace separates. Used for keyword-driven option clauses (USE/SEEK/…).</summary>
    public static List<string> ClauseTokens(string s)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ',') { tokens.Add(","); i++; continue; }
            if (c == '\'' || c == '"')
            {
                int start = i; char q = c; i++;
                while (i < s.Length && s[i] != q) i++;
                if (i < s.Length) i++;
                tokens.Add(s.Substring(start, i - start));
                continue;
            }
            if (c == '[' && IsBracketLiteralStart(s, i))
            {
                int start = i;
                int close = s.IndexOf(']', i + 1);
                i = close < 0 ? s.Length : close + 1;
                tokens.Add(s.Substring(start, i - start));
                continue;
            }
            if (c == '(')
            {
                int start = i, depth = 0;
                bool inStr = false; char q = '\0';
                while (i < s.Length)
                {
                    char d = s[i];
                    if (!inStr && d == '[' && IsBracketLiteralStart(s, i))
                    {
                        int close = s.IndexOf(']', i + 1);
                        i = close < 0 ? s.Length : close + 1;
                        continue;
                    }
                    if (inStr) { if (d == q) inStr = false; i++; continue; }
                    if (d == '\'' || d == '"') { inStr = true; q = d; i++; continue; }
                    if (d == '(') depth++;
                    else if (d == ')') { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
                tokens.Add(s.Substring(start, i - start));
                continue;
            }
            // a run of non-space, non-comma, non-paren, non-quote characters
            int rs = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',' &&
                   s[i] != '(' && s[i] != ')' && s[i] != '\'' && s[i] != '"')
                i++;
            tokens.Add(s.Substring(rs, i - rs));
        }
        return tokens;
    }

    /// <summary>True when <paramref name="s"/> contains a top-level (depth-0, outside string) macro
    /// reference <c>&amp;ident</c> — a runtime textual substitution. A <c>&amp;&amp;</c> (already a stripped
    /// comment) never counts. Drives the generalised statement-level macro expansion.</summary>
    public static bool ContainsTopLevelMacro(string s)
    {
        long profileStart = VfpInsertProfile.Start();
        try
        {
            int depth = 0;
            bool inStr = false;
            char q = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                switch (c)
                {
                    case '\'':
                    case '"': inStr = true; q = c; continue;
                    case '(':
                    case '{': depth++; continue;
                    case '[':
                        if (IsBracketLiteralStart(s, i))
                        {
                            int close = s.IndexOf(']', i + 1);
                            i = close < 0 ? s.Length : close;
                        }
                        else depth++;
                        continue;
                    case ')':
                    case ']':
                    case '}': if (depth > 0) depth--; continue;
                    case '&':
                        if (depth != 0) continue;
                        char next = i + 1 < s.Length ? s[i + 1] : '\0';
                        if (next == '&') { i++; continue; }        // a comment marker, not a macro.
                        if (IsIdentStart(next)) return true;
                        continue;
                }
            }
            return false;
        }
        finally
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.MacroScan, profileStart);
        }
    }

    /// <summary>The first identifier word of a logical line, UPPER-cased. Lines that begin
    /// with <c>=</c>, <c>&amp;</c> or <c>#</c> report that symbol as their "word".</summary>
    public static string FirstWord(string s)
    {
        s = s.TrimStart();
        if (s.Length == 0) return string.Empty;
        char c = s[0];
        if (c == '=') return "=";
        if (c == '&') return "&";
        if (c == '#') return "#";
        if (!IsIdentStart(c)) return string.Empty;
        int j = 0;
        while (j < s.Length && IsIdentChar(s[j])) j++;
        return s.Substring(0, j).ToUpperInvariant();
    }

    /// <summary>The text after the first identifier word, trimmed.</summary>
    public static string AfterFirstWord(string s)
    {
        s = s.TrimStart();
        int j = 0;
        while (j < s.Length && IsIdentChar(s[j])) j++;
        return s.Substring(j).Trim();
    }

    /// <summary>Second top-level word, UPPER-cased, or empty.</summary>
    public static string SecondWord(string s)
    {
        var words = TopWords(s);
        return words.Count >= 2 ? words[1].Upper : string.Empty;
    }

    /// <summary>Position of the first top-level simple-assignment <c>=</c> (not <c>== &lt;=
    /// &gt;= != &lt;&gt;</c>), or -1.</summary>
    public static int IndexOfAssign(string s)
    {
        int depth = 0;
        bool inStr = false;
        char q = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == q) inStr = false; continue; }
            switch (c)
            {
                case '\'':
                case '"': inStr = true; q = c; continue;
                case '(':
                case '{': depth++; continue;
                case '[':
                    if (IsBracketLiteralStart(s, i))
                    {
                        int close = s.IndexOf(']', i + 1);
                        i = close < 0 ? s.Length : close;
                    }
                    else depth++;
                    continue;
                case ')':
                case ']':
                case '}': if (depth > 0) depth--; continue;
                case '=':
                    if (depth != 0) continue;
                    char prev = i > 0 ? s[i - 1] : '\0';
                    char next = i + 1 < s.Length ? s[i + 1] : '\0';
                    if (prev is '<' or '>' or '!' or '=') continue;
                    if (next == '=') { i++; continue; }
                    return i;
            }
        }
        return -1;
    }

    /// <summary>Strips the trailing inline <c>&amp;&amp;</c> comment (string-aware) and reports
    /// whether the resulting code ends with a <c>;</c> line-continuation (which is removed).</summary>
    public static (string Code, bool Continues) StripCommentAndContinuation(string line)
    {
        int cut = line.Length;
        bool inStr = false;
        char q = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr) { if (c == q) inStr = false; continue; }
            if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
            if (c == '[' && IsBracketLiteralStart(line, i))
            {
                int close = line.IndexOf(']', i + 1);
                i = close < 0 ? line.Length : close;
                continue;
            }
            if (c == '&' && i + 1 < line.Length && line[i + 1] == '&') { cut = i; break; }
        }
        string code = line.Substring(0, cut).TrimEnd();
        bool continues = code.EndsWith(';');
        if (continues) code = code.Substring(0, code.Length - 1);
        return (code, continues);
    }

    /// <summary>True when a (left-trimmed) line is a full-line comment: <c>*</c>, <c>&amp;&amp;</c>,
    /// or the <c>NOTE</c> command.</summary>
    public static bool IsCommentLine(string trimmedStart)
    {
        if (trimmedStart.Length == 0) return false;
        if (trimmedStart[0] == '*') return true;
        if (trimmedStart.StartsWith("&&", StringComparison.Ordinal)) return true;
        if (trimmedStart.Length >= 4 &&
            trimmedStart.Substring(0, 4).Equals("NOTE", StringComparison.OrdinalIgnoreCase) &&
            (trimmedStart.Length == 4 || !IsIdentChar(trimmedStart[4])))
            return true;
        return false;
    }
}
