using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P2 — the scalar/string function batch (MICROVFP_EXTENSIONS_BACKLOG.md §C.18):
/// TRANSFORM, VARTYPE, ISLOWER/ISUPPER, PROPER, STREXTRACT, GETWORDCOUNT/GETWORDNUM, SOUNDEX/DIFFERENCE,
/// STRCONV, CPCONVERT, CPCURRENT/CPDBF, TEXTMERGE, SYS(10) and ALINES.
///
/// Written TESTS-FIRST: every expected value is PINNED to VFP9 semantics per the backlog citations, and
/// the suite is RED until the interpreter's HostInvoke stubs are implemented (they currently return
/// placeholders). SAFETY: pure functions need no data; the one table-touching case (CPDBF) builds a
/// FRESH synthetic free table in a throwaway temp dir — no committed fixture, no customer data, no
/// oracle. The VFP9-runtime golden for the semantically tricky cases lives in the Internal project.
/// </summary>
public sealed class MicroVfpScalarStringTests
{
    // ─────────────────────────── bare-interpreter harness (no data session needed) ───────────────────────────

    private sealed class H : IDisposable
    {
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }
        public H()
        {
            Session = new VfpSession();
            Interp = new VfpInterpreter(Session);
        }
        public void Run(string prg) => Interp.Execute(prg);
        public string Str(string e) => Interp.EvalExpression(e).AsString;
        public decimal Num(string e) => Interp.EvalExpression(e).AsNumber;
        public bool Bool(string e) => Interp.EvalExpression(e).AsLogical;
        public void Dispose() { try { Session.Dispose(); } catch { } }
    }

    // ─────────────────────────── TRANSFORM ───────────────────────────

    [Fact]
    public void Transform_NumericPictures_And_AtFunctions_And_Logical()
    {
        using var h = new H();
        // "999": three digit positions, right-justified, space-padded.
        Assert.Equal("  5", h.Str("TRANSFORM(5, '999')"));
        // grouping picture inserts the thousands separators between the 9s.
        Assert.Equal("1,234,567", h.Str("TRANSFORM(1234567, '9,999,999')"));
        // rounds to the picture's fractional width.
        Assert.Equal("1234.57", h.Str("TRANSFORM(1234.567, '9999.99')"));
        // @! upper-cases the character result.
        Assert.Equal("ABC", h.Str("TRANSFORM('abc', '@!')"));
        // @0 hex format (backlog: TRANSFORM(12345,'@0') → '0x00003039').
        Assert.Equal("0x00003039", h.Str("TRANSFORM(12345, '@0')"));
        // logical without a picture ⇒ the ".T."/".F." literal (runtime-verified — NOT 'T'/'F').
        Assert.Equal(".T.", h.Str("TRANSFORM(.T.)"));
        Assert.Equal(".F.", h.Str("TRANSFORM(.F.)"));
    }

    // ─────────────────────────── VARTYPE ───────────────────────────

    [Fact]
    public void Vartype_ReturnsOneLetterTypeCode_PerValue_IncludingNull()
    {
        using var h = new H();
        Assert.Equal("C", h.Str("VARTYPE('abc')"));
        Assert.Equal("N", h.Str("VARTYPE(123)"));
        Assert.Equal("N", h.Str("VARTYPE(1.5)"));
        Assert.Equal("L", h.Str("VARTYPE(.T.)"));
        Assert.Equal("D", h.Str("VARTYPE(DATE())"));
        Assert.Equal("T", h.Str("VARTYPE(DATETIME())"));
        // a bare .NULL. is type 'X'.
        Assert.Equal("X", h.Str("VARTYPE(.NULL.)"));
    }

    // ─────────────────────────── ISLOWER / ISUPPER ───────────────────────────

    [Fact]
    public void IsLower_IsUpper_TestFirstCharacterOnly()
    {
        using var h = new H();
        Assert.True(h.Bool("ISLOWER('abc')"));
        Assert.False(h.Bool("ISLOWER('Abc')"));
        Assert.False(h.Bool("ISLOWER('123')"));
        Assert.False(h.Bool("ISLOWER('')"));

        Assert.True(h.Bool("ISUPPER('Abc')"));
        Assert.False(h.Bool("ISUPPER('abc')"));
        Assert.False(h.Bool("ISUPPER('9x')"));
    }

    // ─────────────────────────── PROPER ───────────────────────────

    [Fact]
    public void Proper_TitleCasesEachSpaceDelimitedWord()
    {
        using var h = new H();
        Assert.Equal("Hello World", h.Str("PROPER('hello world')"));
        Assert.Equal("John Smith", h.Str("PROPER('JOHN SMITH')"));
        Assert.Equal("Mcdonald", h.Str("PROPER('mcDONALD')"));
    }

    // ─────────────────────────── STREXTRACT ───────────────────────────

    [Fact]
    public void StrExtract_Delimiters_Occurrence_And_Flags()
    {
        using var h = new H();
        // between begin and end delimiter.
        Assert.Equal("hello", h.Str("STREXTRACT('<a>hello</a>', '<a>', '</a>')"));
        // no end delimiter ⇒ to end of string.
        Assert.Equal("abc", h.Str("STREXTRACT('[x]abc', '[x]')"));
        // nOccurrence selects the n-th begin delimiter.
        Assert.Equal("b", h.Str("STREXTRACT('(a)(b)(c)', '(', ')', 2)"));
        // nFlags bit 1 = case-insensitive search.
        Assert.Equal("hello", h.Str("STREXTRACT('ABChelloXYZ', 'abc', 'xyz', 1, 1)"));
        // nFlags bit 2 = return the remainder when the end delimiter is absent.
        Assert.Equal("-tail", h.Str("STREXTRACT('start-tail', 'start', 'END', 1, 2)"));
        // not found (no flag 2) ⇒ empty string.
        Assert.Equal("", h.Str("STREXTRACT('nothing here', '<z>', '</z>')"));
    }

    // ─────────────────────────── GETWORDCOUNT / GETWORDNUM ───────────────────────────

    [Fact]
    public void GetWordCount_And_GetWordNum_DefaultAndCustomDelimiters()
    {
        using var h = new H();
        Assert.Equal(4m, h.Num("GETWORDCOUNT('the quick brown fox')"));
        // runs of default delimiters collapse (no empty words).
        Assert.Equal(2m, h.Num("GETWORDCOUNT('a   b')"));
        // custom delimiter list REPLACES the defaults.
        Assert.Equal(3m, h.Num("GETWORDCOUNT('a,b,c', ',')"));

        Assert.Equal("quick", h.Str("GETWORDNUM('the quick brown fox', 2)"));
        Assert.Equal("c", h.Str("GETWORDNUM('a,b,c', 3, ',')"));
        // bounds quirk: out-of-range / 0 ⇒ empty string, no error.
        Assert.Equal("", h.Str("GETWORDNUM('the quick brown fox', 0)"));
        Assert.Equal("", h.Str("GETWORDNUM('the quick brown fox', 9)"));
    }

    // ─────────────────────────── SOUNDEX / DIFFERENCE ───────────────────────────

    [Fact]
    public void Soundex_And_Difference_ClassicPairs()
    {
        using var h = new H();
        Assert.Equal("S530", h.Str("SOUNDEX('Smith')"));
        Assert.Equal("R163", h.Str("SOUNDEX('Robert')"));
        Assert.Equal("R163", h.Str("SOUNDEX('Rupert')"));

        // identical strings ⇒ 4.
        Assert.Equal(4m, h.Num("DIFFERENCE('Smith', 'Smith')"));
        // same soundex (R163) ⇒ 4.
        Assert.Equal(4m, h.Num("DIFFERENCE('Robert', 'Rupert')"));
    }

    // ─────────────────────────── STRCONV ───────────────────────────

    [Fact]
    public void StrConv_CaseUtf8AndUnicodeModes()
    {
        using var h = new H();
        // 8 = upper-case (locale), 7 = lower-case (locale) — runtime-verified codes.
        Assert.Equal("ABCXYZ", h.Str("STRCONV('abcXYZ', 8)"));
        Assert.Equal("abcxyz", h.Str("STRCONV('abcXYZ', 7)"));
        // 9 = codepage → UTF-8: ASCII is unchanged; é (CHR(233)) becomes the two UTF-8 bytes C3 A9,
        // which the connection surfaces as the Latin1 chars CHR(195)+CHR(169).
        Assert.Equal("Hi", h.Str("STRCONV('Hi', 9)"));
        Assert.Equal("caf" + (char)0xC3 + (char)0xA9, h.Str("STRCONV('caf' + CHR(233), 9)"));
        // 5 = codepage → UTF-16LE: each byte gains a trailing NUL.
        Assert.Equal("A" + (char)0 + "B" + (char)0, h.Str("STRCONV('AB', 5)"));
        // 5→6 and 9→10 round-trip back to the original.
        Assert.Equal("AB", h.Str("STRCONV(STRCONV('AB', 5), 6)"));
        Assert.Equal("caf" + (char)233, h.Str("STRCONV(STRCONV('caf' + CHR(233), 9), 10)"));
    }

    // ─────────────────────────── CPCONVERT ───────────────────────────

    [Fact]
    public void CpConvert_SameCodePage_IsIdentity()
    {
        using var h = new H();
        Assert.Equal("Hello", h.Str("CPCONVERT(1252, 1252, 'Hello')"));
    }

    // ─────────────────────────── TEXTMERGE ───────────────────────────

    [Fact]
    public void TextMerge_EvaluatesDoubleAngleExpressions()
    {
        using var h = new H();
        Assert.Equal("Hello World", h.Str("TEXTMERGE('Hello <<\"World\">>')"));
        Assert.Equal("Sum=5", h.Str("TEXTMERGE('Sum=<<2+3>>')"));
        h.Run("lcName = 'Fox'");
        Assert.Equal("Hi Fox!", h.Str("TEXTMERGE('Hi <<lcName>>!')"));
    }

    // ─────────────────────────── SYS(10) ───────────────────────────

    [Fact]
    public void Sys10_JulianDayToDateString_IsInverseOfSys1()
    {
        using var h = new H();
        // SYS(1) is today's Julian day number; SYS(10) of it must re-form today's date string
        // in whatever the current SET DATE/CENTURY produce (format-independent round-trip).
        Assert.Equal(h.Str("DTOC(DATE())"), h.Str("SYS(10, VAL(SYS(1)))"));
    }

    // ─────────────────────────── CPCURRENT ───────────────────────────

    [Fact]
    public void CpCurrent_ReturnsWindowsAnsiCodePage()
    {
        using var h = new H();
        Assert.Equal(1252m, h.Num("CPCURRENT()"));
        Assert.Equal(1252m, h.Num("CPCURRENT(1)"));
    }

    // ─────────────────────────── CPDBF ───────────────────────────

    [Fact]
    public void CpDbf_ReturnsTheOpenTablesMarkedCodePage()
    {
        // Synthetic free table; DbfWriter defaults to code-page byte 0x03 = Windows ANSI (1252).
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_cpdbf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dbf = Path.Combine(dir, "people.dbf");
            using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("name", 'C', 10) }))
            {
                w.AppendRecord("alice");
                w.Flush();
            }
            using var s = new VfpSession();
            s.OpenDirectory(dir);
            var interp = new VfpInterpreter(s);
            interp.Execute("USE people");
            Assert.Equal(1252m, interp.EvalExpression("CPDBF()").AsNumber);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── ALINES ───────────────────────────

    private static string[] Alines(H h, string expression, params string[] arrayReads)
    {
        decimal count = h.Num(expression);
        Assert.Equal(arrayReads.Length, (int)count);
        return arrayReads.Select(h.Str).ToArray();
    }

    [Fact]
    public void Alines_SplitsIntoArrayRows_ReturnsLineCount()
    {
        using var h = new H();
        h.Run("lcText = 'line1' + CHR(13) + CHR(10) + 'line2' + CHR(13) + CHR(10) + 'line3'");
        // return value is the line count.
        Assert.Equal(3m, h.Num("ALINES(laLines, lcText)"));
        // the array is (auto-)dimensioned to the line count and filled row-by-row.
        Assert.Equal(3m, h.Num("ALEN(laLines)"));
        Assert.Equal("line1", h.Str("laLines[1]"));
        Assert.Equal("line2", h.Str("laLines[2]"));
        Assert.Equal("line3", h.Str("laLines[3]"));
    }

    [Fact]
    public void Alines_Overloads_Trim_Flags_And_ParseChars()
    {
        using var h = new H();
        // 3rd arg logical ⇒ lTrim: each line is blank-trimmed (runtime-verified).
        Assert.Equal(2m, h.Num("ALINES(la, 'x  ' + CHR(13) + '  y', .T.)"));
        Assert.Equal("x", h.Str("la[1]"));
        Assert.Equal("y", h.Str("la[2]"));

        // 3rd arg character ⇒ it is a cParseChar (REPLACES line-break parsing).
        Assert.Equal(3m, h.Num("ALINES(lb, 'a,b,c', ',')"));
        Assert.Equal("b", h.Str("lb[2]"));

        // nFlags bit 4 = skip empty lines: "a<CR><CR>b" ⇒ 2 rows.
        Assert.Equal(2m, h.Num("ALINES(lc, 'a' + CHR(13) + CHR(13) + 'b', 4)"));
        Assert.Equal("b", h.Str("lc[2]"));

        // nFlags bit 1 = trim each line.
        Assert.Equal(2m, h.Num("ALINES(ld, ' x ' + CHR(13) + ' y ', 1)"));
        Assert.Equal("x", h.Str("ld[1]"));

        // parse chars REPLACE the default CR/LF parsing: a lone CR is kept inside a field.
        Assert.Equal(2m, h.Num("ALINES(le, 'a,b' + CHR(13) + 'c', ',')"));
        Assert.Equal("b" + (char)13 + "c", h.Str("le[2]"));
    }

    [Fact]
    public void Alines_WholeStringDelimiters_UseEarliestMatchAndArgumentOrder()
    {
        using var h = new H();
        Assert.Equal(new[] { "a", "", "b" },
            Alines(h, "ALINES(la, 'a:::b', 0, '::', ':')", "la[1]", "la[2]", "la[3]"));
        Assert.Equal(new[] { "a", "", "", "b" },
            Alines(h, "ALINES(lb, 'a:::b', 0, ':', '::')", "lb[1]", "lb[2]", "lb[3]", "lb[4]"));
        Assert.Equal(new[] { "<p>one", "<p>two" },
            Alines(h, "ALINES(lc, '<p>one</p><p>two</p>', 0, '</p>')", "lc[1]", "lc[2]"));
    }

    [Fact]
    public void Alines_Flags_ControlTrailingEmptySkipCaseTrimAndDelimiterRetention()
    {
        using var h = new H();
        Assert.Equal(new[] { "a", "", "b" },
            Alines(h, "ALINES(la, 'a||||b||', 0, '||')", "la[1]", "la[2]", "la[3]"));
        Assert.Equal(new[] { "a", "", "b", "" },
            Alines(h, "ALINES(lb, 'a||||b||', 2, '||')", "lb[1]", "lb[2]", "lb[3]", "lb[4]"));
        Assert.Equal(new[] { "a", "b" },
            Alines(h, "ALINES(lc, 'a||||b||', 4, '||')", "lc[1]", "lc[2]"));
        Assert.Equal(new[] { "a", "b", "c" },
            Alines(h, "ALINES(ld, 'aXXbxxc', 8, 'xx')", "ld[1]", "ld[2]", "ld[3]"));
        Assert.Equal(new[] { "a::", "b::", "" },
            Alines(h, "ALINES(le, 'a::b::', 18, '::')", "le[1]", "le[2]", "le[3]"));
        Assert.Equal(new[] { "a", "\t" + "b" + "\t" },
            Alines(h, "ALINES(lf, '  a  ||' + CHR(9) + 'b' + CHR(9), 1, '||')", "lf[1]", "lf[2]"));
    }

    [Fact]
    public void Alines_DefaultAndExplicitEmptyDelimiterContracts_AreDistinct()
    {
        using var h = new H();
        Assert.Equal(new[] { "a", "", "b" },
            Alines(h, "ALINES(la, 'a' + CHR(13) + CHR(10) + CHR(13) + 'b')", "la[1]", "la[2]", "la[3]"));
        Assert.Equal(new[] { "a" + (char)13 + (char)10 + "b" },
            Alines(h, "ALINES(lb, 'a' + CHR(13) + CHR(10) + 'b', 0, '')", "lb[1]"));
        Assert.Equal(new[] { "a", "b" },
            Alines(h, "ALINES(lc, 'a||b', 0, '', '||', '')", "lc[1]", "lc[2]"));
        Assert.Equal(new[] { "", "a", "" },
            Alines(h, "ALINES(ld, '||a||||', 0, '||')", "ld[1]", "ld[2]", "ld[3]"));
    }

    // Complexity guards: only the ALINES call is timed, so thread-pool or CPU contention from a parallel
    // test run cannot fail them; a quadratic scan over these 500 000-character inputs takes minutes.
    private static readonly TimeSpan AlinesWorkBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public void Alines_LargeCrOnlyInput_Flag4_CompletesWithinBoundedTime()
    {
        using var h = new H();
        h.Run("lcText = REPLICATE(CHR(13), 500000)");
        Assert.Equal(500000m, h.Num("LEN(lcText)"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        decimal count = h.Num("ALINES(la, lcText, 4)");
        watch.Stop();
        Assert.True(watch.Elapsed < AlinesWorkBudget,
            $"ALINES took {watch.Elapsed} for the large-input case.");
        Assert.Equal(0m, count);
    }

    [Fact]
    public void Alines_LongNearMissDelimiter_Flag4_CompletesWithinBoundedTime()
    {
        using var h = new H();
        h.Run("lcText = REPLICATE('a', 500000)");
        Assert.Equal(500000m, h.Num("LEN(lcText)"));
        Assert.Equal(250001m, h.Num("LEN(REPLICATE('a', 250000) + 'b')"));
        h.Run("lcDelim = REPLICATE('a', 250000) + 'b'");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        decimal count = h.Num("ALINES(la, lcText, 4, lcDelim)");
        watch.Stop();
        Assert.True(watch.Elapsed < AlinesWorkBudget,
            $"ALINES took {watch.Elapsed} for the near-miss delimiter case.");
        Assert.Equal(1m, count);
    }

    [Fact]
    public void Alines_EmptyExpressionAndTerminalFlagCombinations_PinCountArrayAndContent()
    {
        using var h = new H();
        Assert.Equal(0m, h.Num("ALINES(la, '', 4)"));
        Assert.Equal(1m, h.Num("ALEN(la)"));
        Assert.Equal("", h.Str("la[1]"));
        Assert.Equal(0m, h.Num("ALINES(le, '', 4, '||')"));
        Assert.Equal(1m, h.Num("ALEN(le)"));
        Assert.Equal("", h.Str("le[1]"));

        Assert.Equal(new[] { "a", "b", "" },
            Alines(h, "ALINES(lb, 'a||b||', 2 + 4, '||')", "lb[1]", "lb[2]", "lb[3]"));
        Assert.Equal(3m, h.Num("ALEN(lb)"));

        Assert.Equal(new[] { "a||", "b||" },
            Alines(h, "ALINES(lc, 'a||b||', 4 + 16, '||')", "lc[1]", "lc[2]"));
        Assert.Equal(2m, h.Num("ALEN(lc)"));

        Assert.Equal(new[] { "a||", "b||", "" },
            Alines(h, "ALINES(ld, 'a||b||', 2 + 4 + 16, '||')", "ld[1]", "ld[2]", "ld[3]"));
        Assert.Equal(3m, h.Num("ALEN(ld)"));
    }
}
