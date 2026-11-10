using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>microVFP builtin functions — the P2 scalar/string batch (TRANSFORM/STRCONV/TEXTMERGE/PROPER/SOUNDEX/...).</summary>
public sealed partial class VfpInterpreter
{
    // ─────────────────────────── P2 scalar/string batch ───────────────────────────
    // Authoritative semantics: MICROVFP_EXTENSIONS_BACKLOG.md §C.18 + verified empirically against the
    // VFP9 runtime (TRANSFORM pictures / SOUNDEX quirks / PROPER edge cases confirmed byte-for-byte).

    /// <summary>The default word separators for GETWORDCOUNT()/GETWORDNUM() (space, tab, LF, CR).</summary>
    private const string DefaultWordDelims = " \t\n\r";

    /// <summary>VARTYPE(eExpr [, lNullDataType]): the 1-letter type code of the ALREADY-evaluated value
    /// (unlike TYPE(), which macro-evaluates a string). NULL → "X" (the VFP quirk) unless lNullDataType is
    /// set — but a bare .NULL. carries no base type, so it stays "X".</summary>
    private VfpValue FnVartype(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character("U");
        var v = a[0];
        if (v.Type == VfpType.Null) return VfpValue.Character("X");
        if (TryGetObject(v, out _)) return VfpValue.Character("O");   // CATCH TO oErr — the Exception object.
        if (v.IsBinary) return VfpValue.Character("Q");
        string code = v.Type switch
        {
            VfpType.Character => "C",
            VfpType.Numeric or VfpType.Integer => "N",
            VfpType.Currency => "Y",
            VfpType.Date => "D",
            VfpType.DateTime => "T",
            VfpType.Logical => "L",
            _ => "U",
        };
        return VfpValue.Character(code);
    }

    /// <summary>TRANSFORM(eExpr [, cFormatCodes]): picture/@-function formatting of any value. Without a
    /// format the value's default stringification is used; with one, the @-function codes (@! upper, @0 hex)
    /// and numeric PICTURE templates (9/#/,/.) are honoured.</summary>
    private VfpValue FnTransform(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character(string.Empty);
        var v = a[0];
        string fmt = a.Length > 1 && a[1].Type == VfpType.Character ? a[1].AsString : string.Empty;
        // Round6: an explicit empty picture uses ten integer slots and SET DECIMALS.
        // Indeterminate overflow formats as zero with a picture; infinity fills its digit slots.
        if (v.Type == VfpType.Numeric && !double.IsFinite(v.AsDouble) && a.Length > 1)
        {
            if (fmt.Length == 0) fmt = new string('9', 10) + (_ctx.NumericDecimals > 0 ? "." + new string('9', _ctx.NumericDecimals) : "");
            if (double.IsInfinity(v.AsDouble) && fmt.All(c => c is '9' or '#' or '.'))
                return VfpValue.Character(new string(fmt.Select(c => c == '.' ? '.' : '*').ToArray()));
        }
        if (fmt.Length == 0) return VfpValue.Character(TransformNoFormat(v));

        // Split "@<funcs> <picture>" — the @-run ends at the first space; the rest is the PICTURE template.
        string funcs = string.Empty, picture = fmt;
        if (fmt[0] == '@')
        {
            int sp = fmt.IndexOf(' ');
            if (sp < 0) { funcs = fmt[1..]; picture = string.Empty; }
            else { funcs = fmt.Substring(1, sp - 1); picture = fmt[(sp + 1)..]; }
        }
        bool hex = funcs.Contains('0');
        bool upper = funcs.Contains('!');

        if (v.Type is VfpType.Numeric or VfpType.Integer or VfpType.Currency)
        {
            if (hex) return VfpValue.Character("0x" + ((uint)v.AsInteger).ToString("X8", CultureInfo.InvariantCulture));
            if (picture.Length > 0) return VfpValue.Character(FormatNumericPicture(v.AsNumber, picture));
            return VfpValue.Character(TransformNoFormat(v));
        }
        if (v.Type == VfpType.Character)
        {
            string s = v.AsString;
            if (upper) s = s.ToUpperInvariant();
            return VfpValue.Character(s);
        }
        if (v.Type == VfpType.Logical)
        {
            if (picture.Contains('Y') || picture.Contains('y')) return VfpValue.Character(v.AsLogical ? "Y" : "N");
            return VfpValue.Character(v.AsLogical ? "T" : "F");
        }
        return VfpValue.Character(TransformNoFormat(v));
    }

    /// <summary>The default (format-less) TRANSFORM/TEXTMERGE stringification of a value.</summary>
    private string TransformNoFormat(VfpValue v) => v.Type switch
    {
        VfpType.Character => v.IsBinary ? Convert.ToHexString(Encoding.Latin1.GetBytes(v.AsString)) : v.AsString,
        // VFP9 (runtime-verified): the format-less TRANSFORM of a logical is the ".T."/".F." literal, NOT "T"/"F".
        VfpType.Logical => v.AsLogical ? ".T." : ".F.",
        VfpType.Null => ".NULL.",
        VfpType.Date => v.IsEmptyDate ? TransformEmptyDate() : v.AsDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        VfpType.DateTime => v.IsEmptyDate ? TransformEmptyDate() + "   :  :   AM"
            : v.AsDateTime.ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture),
        // CALCULATE NPV's non-finite result keeps its calculation scale, including after SET DECIMALS.
        // VFP9 reserves at least two fractional places in this overflow display.
        VfpType.Numeric when v.OverflowDisplayWidth > 0 => new string('*', v.OverflowDisplayWidth),
        // Stored DBF/SQL overflow retains its own marker width.
        VfpType.Numeric when double.IsNaN(v.AsDouble) => new string('*', 11),
        VfpType.Numeric when double.IsInfinity(v.AsDouble) => new string('*', 10),
        VfpType.Currency => (v.AsNumber < 0 ? "$-" : "$")
            + Math.Abs(v.RoundForDisplay(_ctx.NumericDecimals)).ToString("N" + _ctx.NumericDecimals, CultureInfo.InvariantCulture),
        VfpType.Numeric or VfpType.Integer when _ctx.FixedNumeric => FixedNumber(v),
        VfpType.Numeric when v.DisplayDecimals >= 0 && v.AsNumber != decimal.Truncate(v.AsNumber)
            => Math.Round(v.AsDisplayNumber, v.DisplayDecimals, MidpointRounding.AwayFromZero).ToString("F" + v.DisplayDecimals, CultureInfo.InvariantCulture),
        VfpType.Numeric or VfpType.Integer => NumberToPlainString(v.AsNumber),
        _ => v.ToString(),
    };

    private string TransformEmptyDate()
    {
        // Empty VFP9 dates keep their separators; DateTime also keeps the blank time and AM suffix.
        string year = Session.ImportCentury ? "    " : "  ";
        return Session.ImportDateFormat switch
        {
            "GERMAN" => "  .  ." + year,
            "ANSI" => year + ".  .  ",
            _ => "  /  /" + year
        };
    }

    /// <summary>A numeric value as its shortest exact decimal string (no leading/trailing padding, trailing
    /// fractional zeros trimmed): 5→"5", 1234.5→"1234.5", 1234.50→"1234.5".</summary>
    private static string NumberToPlainString(decimal d)
    {
        string s = d.ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s;
    }

    /// <summary>Render <paramref name="value"/> onto a numeric PICTURE template (9/# digit slots, ',' grouping,
    /// '.' decimal point, '*' fill). Digits fill right-to-left; the sign takes the slot left of the top digit;
    /// unfilled slots become spaces and grouping is suppressed in the pad area.</summary>
    private static string FormatNumericPicture(decimal value, string picture)
    {
        int dot = picture.IndexOf('.');
        string intPat = dot < 0 ? picture : picture[..dot];
        string decPat = dot < 0 ? string.Empty : picture[(dot + 1)..];
        int decCount = decPat.Count(ch => ch is '9' or '#');

        // VFP9 accepts pictures wider than decimal's precision; extra slots are zero-filled.
        decimal absRounded = Math.Round(Math.Abs(value), Math.Min(decCount, 28), MidpointRounding.AwayFromZero);
        bool neg = value < 0;
        string fixedStr = absRounded.ToString("F" + decCount, CultureInfo.InvariantCulture);
        int fdot = fixedStr.IndexOf('.');
        string intDigits = fdot < 0 ? fixedStr : fixedStr[..fdot];
        string decDigits = fdot < 0 ? string.Empty : fixedStr[(fdot + 1)..];
        string body = (neg ? "-" : string.Empty) + intDigits;

        var outChars = new List<char>();
        int bi = body.Length - 1;
        for (int pi = intPat.Length - 1; pi >= 0; pi--)
        {
            char t = intPat[pi];
            switch (t)
            {
                case '9':
                case '#':
                    if (bi >= 0) outChars.Add(body[bi--]); else outChars.Add(' ');
                    break;
                case ',':
                    outChars.Add(bi >= 0 && char.IsDigit(body[bi]) ? ',' : ' ');
                    break;
                case '*':
                    if (bi >= 0) outChars.Add(body[bi--]); else outChars.Add('*');
                    break;
                default:
                    outChars.Add(t);
                    break;
            }
        }
        while (bi >= 0) outChars.Add(body[bi--]);   // picture too narrow → keep the overflow digits.
        outChars.Reverse();
        string intResult = new string(outChars.ToArray());
        return decCount > 0 ? intResult + "." + decDigits : intResult;
    }

    /// <summary>PROPER(cStr): upper-case the FIRST character of each space-delimited word, lower-case the
    /// rest. Deliberately as "simple-minded" as VFP — Mc/Mac and apostrophes are NOT special (the documented
    /// PROPER weakness): "mcDONALD"→"Mcdonald", "o'brien"→"O'brien".</summary>
    private VfpValue FnProper(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character(string.Empty);
        string s = a[0].AsString;
        var sb = new StringBuilder(s.Length);
        bool atWordStart = true;
        foreach (char c in s)
        {
            if (c == ' ') { sb.Append(c); atWordStart = true; }
            else { sb.Append(atWordStart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)); atWordStart = false; }
        }
        return VfpValue.Character(sb.ToString());
    }

    /// <summary>ISLOWER(cStr): the FIRST character is a lowercase letter (empty/non-alpha → .F.).</summary>
    private static bool FnIsLower(VfpValue[] a) => a.Length > 0 && a[0].AsString.Length > 0 && char.IsLower(a[0].AsString[0]);

    /// <summary>ISUPPER(cStr): the FIRST character is an uppercase letter (empty/non-alpha → .F.).</summary>
    private static bool FnIsUpper(VfpValue[] a) => a.Length > 0 && a[0].AsString.Length > 0 && char.IsUpper(a[0].AsString[0]);

    /// <summary>STREXTRACT(cSearched, cBegin [, cEnd [, nOcc [, nFlags]]]): the substring after the
    /// nOcc-th cBegin up to cEnd (or end-of-string when cEnd is empty). nFlags bit 1 = case-insensitive;
    /// bit 2 = return the remainder when cEnd is present but not found.</summary>
    private static string FnStrExtract(VfpValue[] a)
    {
        if (a.Length < 2) return string.Empty;
        string searched = a[0].AsString;
        string begin = a[1].AsString;
        string end = a.Length > 2 ? a[2].AsString : string.Empty;
        int occ = a.Length > 3 ? a[3].TruncatedInt32 : 1;
        int flags = a.Length > 4 ? a[4].TruncatedInt32 : 0;
        if (occ < 1) return string.Empty;
        var cmp = (flags & 1) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool remainderIfNoEnd = (flags & 2) != 0;

        if (begin.Length == 0) return string.Empty;
        int idx = -1, from = 0;
        for (int k = 0; k < occ; k++)
        {
            idx = searched.IndexOf(begin, from, cmp);
            if (idx < 0) return string.Empty;
            from = idx + begin.Length;
        }
        int start = idx + begin.Length;
        if (end.Length == 0) return searched[start..];
        int endIdx = searched.IndexOf(end, start, cmp);
        if (endIdx < 0) return remainderIfNoEnd ? searched[start..] : string.Empty;
        return searched.Substring(start, endIdx - start);
    }

    /// <summary>Split <paramref name="s"/> into "words" on any single character in <paramref name="delims"/>,
    /// collapsing runs (no empty words) — the shared engine for GETWORDCOUNT/GETWORDNUM.</summary>
    private static List<string> GetWords(string s, string delims)
    {
        var res = new List<string>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && delims.IndexOf(s[i]) >= 0) i++;
            if (i >= n) break;
            int start = i;
            while (i < n && delims.IndexOf(s[i]) < 0) i++;
            res.Add(s[start..i]);
        }
        return res;
    }

    /// <summary>GETWORDCOUNT(cStr [, cDelims]): word count. cDelims (a LIST of single-char separators)
    /// REPLACES the defaults (space/tab/LF/CR).</summary>
    private static int FnGetWordCount(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        string delims = a.Length > 1 && a[1].Type == VfpType.Character && a[1].AsString.Length > 0 ? a[1].AsString : DefaultWordDelims;
        return GetWords(a[0].AsString, delims).Count;
    }

    /// <summary>GETWORDNUM(cStr, nIndex [, cDelims]): the nIndex-th word (1-based). No bounds error — an
    /// out-of-range / non-positive index silently returns "" (the documented VFP quirk).</summary>
    private static string FnGetWordNum(VfpValue[] a)
    {
        if (a.Length < 2) return string.Empty;
        string delims = a.Length > 2 && a[2].Type == VfpType.Character && a[2].AsString.Length > 0 ? a[2].AsString : DefaultWordDelims;
        var words = GetWords(a[0].AsString, delims);
        int n = a[1].TruncatedInt32;
        return n >= 1 && n <= words.Count ? words[n - 1] : string.Empty;
    }

    /// <summary>The classic (simple) Soundex code (1 letter + 3 digits) VFP returns — verified against the
    /// VFP9 runtime incl. its quirks (first char kept verbatim; H/W transparent; adjacent same-code merge;
    /// empty → "0000").</summary>
    private static string SoundexCode(string s)
    {
        if (s.Length == 0) return "0000";
        char first = s[0];
        var sb = new StringBuilder(4);
        sb.Append(char.IsLetter(first) ? char.ToUpperInvariant(first) : first);
        int prev = SoundexDigit(first);
        for (int i = 1; i < s.Length && sb.Length < 4; i++)
        {
            int d = SoundexDigit(s[i]);
            if (d != 0 && d != prev) sb.Append((char)('0' + d));
            prev = d;
        }
        while (sb.Length < 4) sb.Append('0');
        return sb.ToString();
    }

    private static int SoundexDigit(char c) => char.ToUpperInvariant(c) switch
    {
        'B' or 'F' or 'P' or 'V' => 1,
        'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => 2,
        'D' or 'T' => 3,
        'L' => 4,
        'M' or 'N' => 5,
        'R' => 6,
        _ => 0,
    };

    /// <summary>SOUNDEX(cStr): phonetic code (see <see cref="SoundexCode"/>).</summary>
    private static string FnSoundex(VfpValue[] a) => SoundexCode(a.Length > 0 ? a[0].AsString : string.Empty);

    /// <summary>DIFFERENCE(cStr1, cStr2): count of position-wise matching Soundex characters (0..4).</summary>
    private static int FnDifference(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string c1 = SoundexCode(a[0].AsString), c2 = SoundexCode(a[1].AsString);
        int match = 0;
        for (int i = 0; i < 4; i++) if (c1[i] == c2[i]) match++;
        return match;
    }

    /// <summary>STRCONV(cExpr, nConversion [, ...]): the common, runtime-verified conversion codes. A VFP
    /// character value is decoded in the expression context's encoding; Q values retain raw Latin1 bytes.
    /// The codepage defaults to the connection's Windows-ANSI codepage (== CPCURRENT()). Verified byte-for-byte against
    /// the VFP9 runtime for input "Ab"+CHR(233):
    ///   5 = codepage → double-byte Unicode (UTF-16LE)   ["Ab é" → 41 00 62 00 E9 00]
    ///   6 = double-byte Unicode (UTF-16LE) → codepage   (inverse of 5)
    ///   7 = lower-case (locale)                          ["Ab é" → "ab é"]
    ///   8 = upper-case (locale)                          ["Ab é" → "AB É"]
    ///   9 = codepage → UTF-8                             ["Ab é" → 41 62 C3 A9]
    ///  10 = UTF-8 → codepage                             (inverse of 9)
    /// FLAGGED (not runtime-distinct on an SBCS/1252 connection — pass through unchanged, matching the runtime
    /// for such content): 1/2/3/4 (single↔double-byte DBCS locale conversions) and any other/exotic code.</summary>
    private string FnStrConv(VfpValue[] a)
    {
        if (a.Length < 2) return a.Length > 0 ? a[0].AsString : string.Empty;
        string s = a[0].AsString;
        int mode = a[1].TruncatedInt32;
        if (a[1].AsDouble > int.MaxValue || a[1].AsDouble < int.MinValue)
            throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
        var l1 = Encoding.Latin1;
        var cp = _ctx.Encoding ?? ConnectionEncoding();
        byte[] bytes = (a[0].IsBinary ? l1 : cp).GetBytes(s);
        try
        {
            switch (mode)
            {
                case 15: return Convert.ToHexString(bytes);
                case 5:   // codepage bytes → UTF-16LE bytes
                    return cp.GetString(Encoding.Unicode.GetBytes(cp.GetString(bytes)));
                case 6:   // UTF-16LE bytes → codepage bytes
                    return cp.GetString(cp.GetBytes(Encoding.Unicode.GetString(bytes)));
                case 7:   // lower-case (locale)
                    return cp.GetString(cp.GetBytes(cp.GetString(bytes).ToLower(CultureInfo.CurrentCulture)));
                case 8:   // upper-case (locale)
                    return cp.GetString(cp.GetBytes(cp.GetString(bytes).ToUpper(CultureInfo.CurrentCulture)));
                case 9:   // codepage → UTF-8
                    return cp.GetString(Encoding.UTF8.GetBytes(cp.GetString(bytes)));
                case 10:  // UTF-8 → codepage
                    return cp.GetString(cp.GetBytes(Encoding.UTF8.GetString(bytes)));
                default:  // FLAG: DBCS single↔double-byte locale modes / exotic codes — pass through.
                    return s;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException or EncoderFallbackException)
        {
            return s;   // malformed input for the requested conversion — degrade to the input (no error).
        }
    }

    /// <summary>The interpreter session code page as an <see cref="Encoding"/> (== CPCURRENT()); falls
    /// back to Latin1 if the codepage cannot be resolved on this platform.</summary>
    private Encoding ConnectionEncoding()
    {
        try
        {
            return SessionCodePage switch
            {
                620 => Encodings.ResolveEncoding(0x69),
                895 => Encodings.ResolveEncoding(0x68),
                _ => Encoding.GetEncoding(SessionCodePage),
            };
        }
        catch (ArgumentException) { return Encoding.Latin1; }
    }

    /// <summary>CPCONVERT(nCurrentCP, nNewCP, cExpr): transcode cExpr between code pages. Same CP ⇒ identity;
    /// otherwise use the measured VFP9 byte maps (including lossy best-fit substitutions).</summary>
    private string FnCpConvert(VfpValue[] a)
    {
        if (a.Length < 3) return a.Length > 2 ? a[2].AsString : string.Empty;
        int from = a[0].TruncatedInt32, to = a[1].TruncatedInt32;
        string s = a[2].AsString;
        if (from == to) return s;
        try
        {
            var valueEncoding = a[2].IsBinary ? Encoding.Latin1 : _ctx.Encoding ?? ConnectionEncoding();
            byte[] converted = VfpSessionText.ConvertBytes(valueEncoding.GetBytes(s), from, to);
            return valueEncoding.GetString(converted);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return s;
        }
    }

    /// <summary>CPCURRENT([1|2]): the current system code page — 1 (default) = Windows ANSI, 2 = the
    /// underlying OEM/DOS code page.</summary>
    private int FnCpCurrent(VfpValue[] a)
    {
        if (a.Length > 0 && (a[0].AsDouble > int.MaxValue || a[0].AsDouble < int.MinValue))
            throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
        bool oem = a.Length > 0 && IsNumeric(a[0]) && a[0].TruncatedInt32 == 2;
        var ti = CultureInfo.CurrentCulture.TextInfo;
        return oem ? ti.OEMCodePage : SessionCodePage;
    }

    /// <summary>CPDBF([cAlias|nArea]): the code page marked in the addressed (or current) table's header,
    /// as a VFP code-page id; 0 when the driver is unknown/unavailable or no table is open.</summary>
    private int FnCpDbf(VfpValue[] a)
    {
        var wa = AreaArg(a, 0);
        return wa?.Table.CodePage ?? 0;
    }

    /// <summary>TEXTMERGE(cExpr [, lParse [, cDelimBegin [, cDelimEnd]]]): replace every &lt;&lt;expr&gt;&gt;
    /// placeholder with the default stringification of the evaluated expression (a one-liner TEXT…ENDTEXT
    /// engine). The begin/end delimiters default to "&lt;&lt;"/"&gt;&gt;" but may be overridden per-call
    /// (VFP's SET TEXTMERGE DELIMITERS is a separate, unimplemented command). lParse is accepted and ignored —
    /// this function always evaluates.</summary>
    private string FnTextMerge(VfpValue[] a)
    {
        if (a.Length == 0) return string.Empty;
        string s = a[0].AsString;
        // Per-call delimiters win; otherwise the session default set by SET TEXTMERGE DELIMITERS TO (which
        // itself defaults to "<<"/">>").
        string beg = a.Length > 2 && a[2].Type == VfpType.Character && a[2].AsString.Length > 0 ? a[2].AsString : _tmDelimBegin;
        string end = a.Length > 3 && a[3].Type == VfpType.Character && a[3].AsString.Length > 0 ? a[3].AsString : _tmDelimEnd;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf(beg, i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, open - i);
            int close = s.IndexOf(end, open + beg.Length, StringComparison.Ordinal);
            if (close < 0) { sb.Append(s, open, s.Length - open); break; }
            string expr = s.Substring(open + beg.Length, close - open - beg.Length);
            sb.Append(TransformNoFormat(EvalText(expr)));
            i = close + end.Length;
        }
        return sb.ToString();
    }

    /// <summary>ALINES(ArrayName, cExpr [, nFlags | lTrim [, cParseString1 ...]]):
    /// split into array rows using whole-string delimiters. Explicit delimiters replace CRLF/CR/LF;
    /// empty explicit values are ignored and an all-empty explicit set performs no split. At each scan
    /// position the earliest delimiter wins and argument order breaks equal-offset ties. Flags: 1 trims
    /// spaces, 2 keeps a terminal empty row even with flag 4, 4 omits non-terminal empty rows,
    /// 8 compares OrdinalIgnoreCase, and
    /// 16 appends the matched delimiter to the preceding row.</summary>
    private int FnAlines(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string name = a[0].AsString;
        string text = a[1].AsString;

        bool trim = false, includeTrailingEmpty = false, skipEmpty = false;
        bool ignoreCase = false, keepDelimiter = false;
        int parseFrom = 2;                       // index of the first cParseChar arg (default: none).
        if (a.Length > 2)
        {
            var p = a[2];
            if (p.Type == VfpType.Logical) { trim = p.AsLogical; parseFrom = 3; }
            else if (p.Type is VfpType.Numeric or VfpType.Integer or VfpType.Currency)
            {
                int flags = p.TruncatedInt32;
                if (flags < 0) throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
                trim = (flags & 1) != 0;
                includeTrailingEmpty = (flags & 2) != 0;
                skipEmpty = (flags & 4) != 0;
                ignoreCase = (flags & 8) != 0;
                keepDelimiter = (flags & 16) != 0;
                parseFrom = 3;
            }
            // else (character) ⇒ p is already the first parse char; parseFrom stays 2.
        }

        bool explicitDelimiters = a.Length > parseFrom;
        var delimiters = new List<string>();
        for (int k = parseFrom; k < a.Length; k++)
            if (a[k].AsString.Length > 0)
                delimiters.Add(a[k].AsString);
        if (!explicitDelimiters)
            delimiters.AddRange(["\r\n", "\r", "\n"]);

        var outLines = SplitAlines(text, delimiters,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal,
            trim, includeTrailingEmpty, skipEmpty, keepDelimiter);

        int count = outLines.Count;
        var arr = Memory.RedimOrCreateArray(name, Math.Max(1, count), 0);
        for (int i = 0; i < count; i++) arr.Set(i + 1, null, VfpValue.Character(outLines[i]));
        return count;
    }

    private static List<string> SplitAlines(
        string text,
        IReadOnlyList<string> delimiters,
        StringComparison comparison,
        bool trim,
        bool includeTrailingEmpty,
        bool skipEmpty,
        bool keepDelimiter)
    {
        var output = new List<string>();
        if (delimiters.Count == 0)
        {
            AddAlinesPart(output, text, null, trim, skipEmpty, keepDelimiter);
            return output;
        }

        var matchers = new AlinesKmpMatcher[delimiters.Count];
        for (int i = 0; i < delimiters.Count; i++)
            matchers[i] = new AlinesKmpMatcher(delimiters[i], comparison);

        var matches = new AlinesMatch?[text.Length];
        for (int position = 0; position < text.Length; position++)
        {
            for (int i = 0; i < matchers.Length; i++)
            {
                var matcher = matchers[i];
                if (!matcher.Advance(text[position])) continue;

                int matchStart = position - matcher.Length + 1;
                var candidate = new AlinesMatch(i, matcher.Length);
                if (matches[matchStart] is not { } current || i < current.DelimiterIndex)
                    matches[matchStart] = candidate;
            }
        }

        int start = 0;
        for (int matchStart = 0; matchStart < matches.Length; matchStart++)
        {
            if (matches[matchStart] is not { } match || matchStart < start) continue;
            AddAlinesPart(output, text[start..matchStart],
                delimiters[match.DelimiterIndex], trim, skipEmpty, keepDelimiter);
            start = matchStart + match.Length;
        }

        string tail = text[start..];
        if (tail.Length > 0 || includeTrailingEmpty)
            AddAlinesPart(output, tail, null, trim,
                tail.Length == 0 && includeTrailingEmpty ? false : skipEmpty,
                keepDelimiter);
        return output;
    }

    private readonly struct AlinesMatch
    {
        public AlinesMatch(int delimiterIndex, int length)
        {
            DelimiterIndex = delimiterIndex;
            Length = length;
        }

        public int DelimiterIndex { get; }
        public int Length { get; }
    }

    private sealed class AlinesKmpMatcher
    {
        private readonly string _pattern;
        private readonly int[] _lps;
        private readonly StringComparison _comparison;
        private int _matched;

        public AlinesKmpMatcher(string pattern, StringComparison comparison)
        {
            _pattern = pattern;
            _comparison = comparison;
            _lps = BuildLps(pattern, comparison);
        }

        public int Length => _pattern.Length;

        public bool Advance(char value)
        {
            while (_matched > 0 && !CharsEqual(value, _pattern[_matched], _comparison))
                _matched = _lps[_matched - 1];
            if (CharsEqual(value, _pattern[_matched], _comparison)) _matched++;
            if (_matched != _pattern.Length) return false;
            _matched = _lps[_matched - 1];
            return true;
        }

        private static int[] BuildLps(string pattern, StringComparison comparison)
        {
            var lps = new int[pattern.Length];
            int prefixLength = 0;
            for (int i = 1; i < pattern.Length;)
            {
                if (CharsEqual(pattern[i], pattern[prefixLength], comparison))
                {
                    lps[i++] = ++prefixLength;
                }
                else if (prefixLength > 0)
                {
                    prefixLength = lps[prefixLength - 1];
                }
                else
                {
                    lps[i++] = 0;
                }
            }
            return lps;
        }

        private static bool CharsEqual(char left, char right, StringComparison comparison)
            => comparison == StringComparison.Ordinal
                ? left == right
                : char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }

    private static void AddAlinesPart(
        List<string> output, string value, string? delimiter,
        bool trim, bool skipEmpty, bool keepDelimiter)
    {
        if (trim) value = value.Trim(' ');
        if (skipEmpty && value.Length == 0) return;
        if (keepDelimiter && delimiter is not null) value += delimiter;
        output.Add(value);
    }

    /// <summary>SYS(10, nJulianDay): a Julian-day number → date string, honouring the interpreter's date
    /// rendering (inverse of SYS(11)/SYS(1)).</summary>
    private VfpValue FnSys10(VfpValue[] a)
    {
        if (a.Length < 2) return VfpValue.Character(string.Empty);
        int day = a[1].TruncatedInt32;
        if (day < 1721425 || day > 5373484) return VfpValue.Character(TransformEmptyDate());
        return VfpValue.Character(FromJulianDay(day));
    }

    /// <summary>The inverse of <see cref="JulianDay"/> (Fliegel–Van Flandern).</summary>
    private static string FromJulianDay(int jd)
    {
        int a = jd + 32044;
        int b = (4 * a + 3) / 146097;
        int c = a - 146097 * b / 4;
        int d = (4 * c + 3) / 1461;
        int e = c - 1461 * d / 4;
        int m = (5 * e + 2) / 153;
        int day = e - (153 * m + 2) / 5 + 1;
        int month = m + 3 - 12 * (m / 10);
        int year = 100 * b + d - 4800 + m / 10;
        // SYS(10) can format year zero, which DateOnly cannot represent (Round5 oracle).
        return FormattableString.Invariant($"{month:00}/{day:00}/{year:0000}");
    }

    private static int FnOccurs(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string needle = a[0].AsString, hay = a[1].AsString;
        if (needle.Length == 0) return 0;
        int count = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    private static int FnAtc(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string needle = a[0].AsString, hay = a[1].AsString;
        if (needle.Length == 0) return 0;
        int occ = a.Length > 2 ? a[2].TruncatedInt32 : 1;
        if (occ < 1 || a.Length > 2 && a[2].AsDouble > int.MaxValue)
            throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
        int i = -1;
        for (int k = 0; k < occ; k++)
        {
            i = hay.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
        }
        return i + 1;
    }

    // STRTRAN(cSearched, cSought [, cReplacement]) — replace every (case-sensitive, like VFP default)
    // occurrence of cSought with cReplacement. The RI infra (riopen/rireuse/riend) flips a cursor's
    // "?"/"*" reuse marker in pcRIcursors with this.
    private static string FnStrtran(VfpValue[] a)
    {
        if (a.Length > 3)
            throw new MicroVfpRuntimeException("STRTRAN(): only two or three arguments are supported.");

        if (a.Length < 2) return a.Length > 0 ? a[0].AsString : string.Empty;
        string src = a[0].AsString, sought = a[1].AsString;
        string repl = a.Length > 2 ? a[2].AsString : string.Empty;
        if (sought.Length == 0) return src;
        return src.Replace(sought, repl, StringComparison.Ordinal);
    }

}
