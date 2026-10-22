using System;
using System.Collections.Generic;
using System.Text;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP — expression-source NORMALISATION for memory ARRAYS.
//
//  The shared expression engine (CrossVault.FoxDbf.Expressions) has no concept of
//  memory arrays: it treats `[ … ]` purely as VFP's third STRING delimiter, and it
//  evaluates every function argument by VALUE. Two VFP array idioms therefore need a
//  small, semantics-preserving source rewrite BEFORE the text reaches the engine:
//
//   1. ARRAY SUBSCRIPTS — `a[i]` / `a[i,j]` (the bracket form) become `a(i)` / `a(i,j)`
//      so the engine parses them as a call the interpreter resolves to an element. A `[`
//      is a subscript ONLY when it immediately follows (no space) an identifier char,
//      `)` or `]` — exactly VFP's rule that distinguishes `a[1]` (subscript) from a
//      value-position `[string]` literal (e.g. `x = [abc]`, `… AND [abc]`).
//
//   2. ARRAY-NAME ARGUMENTS — `ALEN(a …)` / `AERROR(a)` take an array REFERENCE, not a
//      value; their first bare-identifier argument is wrapped in quotes (`ALEN('a' …)`)
//      so the interpreter's function host receives the NAME and can read the dimensions
//      / (re)dimension + fill the array. (VFP array functions always take a name here.)
//
//  Both passes are STRING-LITERAL aware (`'…'`, `"…"`, and an already-recognised `[…]`
//  literal are copied verbatim). The rewrite is confined to microVFP .prg expression
//  fragments — it never touches the SQL parser.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Source-text normalisation that lets the shared expression engine evaluate microVFP memory
/// arrays (bracket subscripts → parens; ALEN/AERROR array-name argument → quoted name).</summary>
internal static class MicroVfpExprRewrite
{
    private static bool IsIdentChar(char c) => VfpIdentifier.IsPart(c);

    /// <summary>Apply both array rewrites to <paramref name="s"/>; a no-op for text with neither a
    /// subscript bracket nor an ALEN/AERROR call.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        s = ConvertSubscripts(s);
        s = QuoteArrayFnArg(s, "ALEN");
        s = QuoteArrayFnArg(s, "AERROR");
        s = QuoteArrayFnArg(s, "ATAGINFO");   // ATAGINFO(ArrayName [, cTagFile [, area]]) — name by reference.
        s = QuoteArrayFnArg(s, "ALINES");     // ALINES(ArrayName, cExpr [, …]) — array name by reference.
        // P2 array batch — every one takes the (destination) array NAME as its FIRST argument.
        s = QuoteArrayFnArg(s, "ADEL");       // ADEL(ArrayName, nElement [, 2])
        s = QuoteArrayFnArg(s, "AINS");       // AINS(ArrayName, nElement [, 2])
        s = QuoteArrayFnArg(s, "AELEMENT");   // AELEMENT(ArrayName, nRow [, nCol])
        s = QuoteArrayFnArg(s, "ASUBSCRIPT"); // ASUBSCRIPT(ArrayName, nElement, nSubscript)
        s = QuoteArrayFnArg(s, "AFIELDS");    // AFIELDS(ArrayName [, cAlias | nArea])
        s = QuoteArrayFnArg(s, "ASORT");      // ASORT(ArrayName [, nStart [, nCount [, nOrder [, nFlags]]]])
        s = QuoteArrayFnArg(s, "ADATABASES"); // ADATABASES(ArrayName)
        s = QuoteArrayFnArg(s, "AUSED");      // AUSED(ArrayName [, nDataSessionId])
        s = QuoteArrayFnArg(s, "ASESSIONS");  // ASESSIONS(ArrayName)
        s = QuoteArrayFnArg(s, "ADIR");       // ADIR(ArrayName [, cSkeleton [, cAttributes]])
        s = QuoteArrayFnArg(s, "AFONT");      // AFONT(ArrayName [, cFontName [, nFontSize]]) — headless stub
        // ACOPY takes TWO array names by reference (source AND destination).
        s = QuoteArrayFn2Args(s, "ACOPY");    // ACOPY(aSource, aDest [, nStart [, nCount [, nDestStart]]])
        s = QuoteLookupArgs(s);               // LOOKUP(rReturn, eSearch, rSearched [, cTag]) — field names by reference.
        s = QuoteIsBlankFieldArg(s);          // ISBLANK(field) — a bare field ref needs RAW-byte inspection.
        return s;
    }

    /// <summary>ISBLANK's single argument is USUALLY a field whose BLANK state is a RAW-byte property (a
    /// blanked numeric reads as 0 but is byte-distinct from a written 0). When that argument is a bare field
    /// reference (an identifier, optionally <c>alias.field</c>) rewrite <c>ISBLANK(f)</c> to
    /// <c>ISBLANK('f', .T.)</c> so the host receives the NAME (+ a field-ref marker) and can inspect the
    /// record bytes; a literal / computed / quoted argument is left untouched (evaluated by VALUE). The host
    /// falls back to value-based blankness when the name is not actually a field (e.g. a memvar).</summary>
    private static string QuoteIsBlankFieldArg(string s)
    {
        if (s.IndexOf("ISBLANK", StringComparison.OrdinalIgnoreCase) < 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\'' || c == '"')                        // copy quoted string verbatim.
            {
                char q = c; sb.Append(c); i++;
                while (i < s.Length) { sb.Append(s[i]); if (s[i] == q) break; i++; }
                i++;
                continue;
            }
            if (VfpIdentifier.IsStart(c))
            {
                int start = i; i++;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                string ident = s.Substring(start, i - start);
                int p = i;
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (ident.Equals("ISBLANK", StringComparison.OrdinalIgnoreCase) && p < s.Length && s[p] == '(')
                {
                    int close = MatchParen(s, p);
                    if (close > p)
                    {
                        string inner = s.Substring(p + 1, close - p - 1);
                        var parts = new List<string>(SplitTopLevelCommas(inner));
                        // Only the single-argument bare-field form is a field reference.
                        if (parts.Count == 1 && QuoteIfBareField(parts[0]) is { } q && q != parts[0])
                        {
                            sb.Append(ident).Append('(').Append(q).Append(", .T.)");
                            i = close + 1;
                            continue;
                        }
                    }
                }
                sb.Append(ident);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Convert array-subscript brackets <c>name[…]</c> to <c>name(…)</c>, leaving value-position
    /// <c>[ … ]</c> string literals and quoted strings untouched.</summary>
    private static string ConvertSubscripts(string s)
    {
        if (s.IndexOf('[') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' || c == '"')                       // quoted string: copy verbatim.
            {
                char q = c; sb.Append(c); i++;
                while (i < s.Length) { sb.Append(s[i]); if (s[i] == q) break; i++; }
                continue;
            }
            if (c == '[')
            {
                if (!PrgScan.IsBracketLiteralStart(s, i)) { sb.Append('('); continue; }
                // value-position bracket string literal: copy through the matching ']'.
                sb.Append(c); i++;
                while (i < s.Length && s[i] != ']') { sb.Append(s[i]); i++; }
                if (i < s.Length) sb.Append(s[i]);
                continue;
            }
            if (c == ']') { sb.Append(')'); continue; }       // close of a converted subscript.
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Wrap the first bare-identifier argument of every <paramref name="fn"/><c>( … )</c> call in
    /// quotes, so the host receives the array NAME. String-literal aware.</summary>
    private static string QuoteArrayFnArg(string s, string fn)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\'' || c == '"')                        // copy quoted string verbatim.
            {
                char q = c; sb.Append(c); i++;
                while (i < s.Length) { sb.Append(s[i]); if (s[i] == q) break; i++; }
                i++;
                continue;
            }
            if (VfpIdentifier.IsStart(c))            // an identifier run.
            {
                int start = i;
                i++;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                string ident = s.Substring(start, i - start);
                sb.Append(ident);
                if (ident.Equals(fn, StringComparison.OrdinalIgnoreCase))
                    i = EmitQuotedFirstArg(s, i, sb);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Like <see cref="QuoteArrayFnArg"/> but wraps the first TWO bare-identifier arguments of
    /// every <paramref name="fn"/><c>( … )</c> call in quotes — for ACOPY, whose SOURCE and DESTINATION are
    /// both array names by reference. String-literal aware.</summary>
    private static string QuoteArrayFn2Args(string s, string fn)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\'' || c == '"')                        // copy quoted string verbatim.
            {
                char q = c; sb.Append(c); i++;
                while (i < s.Length) { sb.Append(s[i]); if (s[i] == q) break; i++; }
                i++;
                continue;
            }
            if (VfpIdentifier.IsStart(c))            // an identifier run.
            {
                int start = i;
                i++;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                string ident = s.Substring(start, i - start);
                sb.Append(ident);
                if (ident.Equals(fn, StringComparison.OrdinalIgnoreCase))
                    i = EmitQuotedLeadingArgs(s, i, sb, 2);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Having just emitted the function name (cursor at <paramref name="i"/>), quote up to
    /// <paramref name="maxArgs"/> LEADING bare-identifier arguments of the following <c>( … )</c> call and
    /// return the cursor past the last one it emitted; a non-call or a non-bare first argument leaves the
    /// builder untouched and returns <paramref name="i"/>. Trailing arguments are left for the caller's
    /// verbatim copy.</summary>
    private static int EmitQuotedLeadingArgs(string s, int i, StringBuilder sb, int maxArgs)
    {
        int p = i;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || s[p] != '(') return i;          // not a call.
        int open = p;

        var local = new StringBuilder();
        local.Append(s, i, open - i + 1);                    // any spaces + '('
        int q = open + 1;
        int quoted = 0;
        for (int arg = 0; arg < maxArgs; arg++)
        {
            int save = q;
            while (q < s.Length && char.IsWhiteSpace(s[q])) q++;
            if (q >= s.Length || !(VfpIdentifier.IsStart(s[q]))) break;  // arg not bare.
            int idStart = q; q++;
            while (q < s.Length && IsIdentChar(s[q])) q++;
            int idEnd = q;
            int after = idEnd;
            while (after < s.Length && char.IsWhiteSpace(s[after])) after++;
            if (after >= s.Length || (s[after] != ',' && s[after] != ')')) break;    // not a lone identifier.

            local.Append(s, save, idStart - save);           // leading ws after '(' or ','
            local.Append('\'').Append(s, idStart, idEnd - idStart).Append('\'');
            quoted++;
            if (s[after] == ')') { q = idEnd; break; }        // last arg — caller copies " )".
            local.Append(s, idEnd, after - idEnd).Append(','); // ws before comma + comma
            q = after + 1;
        }
        if (quoted == 0) return i;
        sb.Append(local);
        return q;
    }

    /// <summary>Quote the field-NAME arguments of every <c>LOOKUP( … )</c> call — the 1st (rReturn) and
    /// 3rd (rSearched) positional args — when they are bare field references, so the host receives the
    /// NAMES (VFP takes field names there, not values). The 2nd (search value) and 4th (tag) are left as
    /// evaluated expressions. String-literal aware; nested parens/strings respected in the arg split.</summary>
    private static string QuoteLookupArgs(string s)
    {
        int upper = s.IndexOf("LOOKUP", StringComparison.OrdinalIgnoreCase);
        if (upper < 0) return s;

        var sb = new StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\'' || c == '"')
            {
                char q = c; sb.Append(c); i++;
                while (i < s.Length) { sb.Append(s[i]); if (s[i] == q) break; i++; }
                i++;
                continue;
            }
            if (VfpIdentifier.IsStart(c))
            {
                int start = i; i++;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                string ident = s.Substring(start, i - start);
                int p = i;
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (ident.Equals("LOOKUP", StringComparison.OrdinalIgnoreCase) && p < s.Length && s[p] == '(')
                {
                    int close = MatchParen(s, p);
                    if (close > p)
                    {
                        string inner = s.Substring(p + 1, close - p - 1);
                        var parts = new List<string>(SplitTopLevelCommas(inner));
                        if (parts.Count >= 1) parts[0] = QuoteIfBareField(parts[0]);
                        if (parts.Count >= 3) parts[2] = QuoteIfBareField(parts[2]);
                        sb.Append(ident).Append('(').Append(string.Join(",", parts)).Append(')');
                        i = close + 1;
                        continue;
                    }
                }
                sb.Append(ident);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Wrap a bare field reference (an identifier, optionally <c>alias.field</c>) in single quotes;
    /// leave anything else (a literal, a number, an expression) untouched.</summary>
    private static string QuoteIfBareField(string part)
    {
        string t = part.Trim();
        if (t.Length == 0 || !(VfpIdentifier.IsStart(t[0]))) return part;
        foreach (char ch in t)
            if (!(IsIdentChar(ch) || ch == '.')) return part;
        return "'" + t + "'";
    }

    /// <summary>Index of the <c>)</c> matching the <c>(</c> at <paramref name="open"/> (string-aware), or −1.</summary>
    private static int MatchParen(string s, int open)
    {
        int depth = 0; bool inStr = false; char q = '\0';
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == q) inStr = false; continue; }
            if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>Split at TOP-LEVEL commas (ignoring commas inside nested parens/brackets or strings).</summary>
    private static IEnumerable<string> SplitTopLevelCommas(string s)
    {
        var result = new List<string>();
        int depth = 0; bool inStr = false; char q = '\0'; int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr) { if (c == q) inStr = false; continue; }
            if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0) { result.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        result.Add(s.Substring(start));
        return result;
    }

    /// <summary>Having just emitted the function name (cursor at <paramref name="i"/>), if a <c>(</c> +
    /// bare-identifier-first-argument follows, emit <c>('ident'</c> and return the cursor past the
    /// identifier; otherwise emit nothing and return <paramref name="i"/> unchanged.</summary>
    private static int EmitQuotedFirstArg(string s, int i, StringBuilder sb)
    {
        int p = i;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || s[p] != '(') return i;          // not a call.
        int open = p;
        p++;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || !(VfpIdentifier.IsStart(s[p]))) return i;  // first arg not bare.
        int idStart = p;
        p++;
        while (p < s.Length && IsIdentChar(s[p])) p++;
        int idEnd = p;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || (s[p] != ',' && s[p] != ')')) return i;   // not a lone identifier arg.
        sb.Append(s, i, open - i + 1);                        // any spaces + '('
        sb.Append('\'').Append(s, idStart, idEnd - idStart).Append('\'');
        return idEnd;                                         // resume right after the identifier.
    }
}
