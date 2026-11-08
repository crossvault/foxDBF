using System;
using System.IO;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P3 batch 2 — the remaining string/char functions (MICROVFP_EXTENSIONS_BACKLOG.md §C.18 P3,
/// §C.17 DBCS variants, §C.14 MEMLINES/MLINE):
/// LIKE/LIKEC, CHRTRANC, ISLEADBYTE, NORMALIZE, SYS(15), TXTWIDTH, the DBCS char variants
/// (AT_C/ATCC/RATC, LEFTC/RIGHTC/SUBSTRC, STUFFC, ATLINE/ATCLINE/RATLINE), STRTOFILE/FILETOSTR, and
/// MEMLINES/MLINE with SET MEMOWIDTH word-wrap + the _MLINE offset mechanics.
///
/// Written TESTS-FIRST: every expected value was PINNED against the local VFP9 runtime (probe runs) —
/// see MicroVfpP3StringsOracleTests in the Internal project for the authoritative golden that re-derives
/// the tricky ones (MEMOWIDTH wrap, _MLINE, SYS(15), NORMALIZE, TXTWIDTH fixed-pitch, STRTOFILE flags).
/// The suite is RED until the interpreter/runtime dispatch cases are implemented.
///
/// SAFETY: pure scalar expressions need no data; the STRTOFILE/FILETOSTR and memo-field cases build a
/// FRESH throwaway temp dir/table — no committed fixture, no customer data, no oracle here.
///
/// KEY VFP9 FACTS (probe-verified, CP1252 single-byte target):
///  • LIKE(pat,str): anchored glob, '*'=any run (incl. empty), '?'=exactly one char, case-SENSITIVE,
///    trailing blanks significant on BOTH sides; no bracket classes ('.' and '[' are literal).
///  • The DBCS char variants are byte-identical to their base functions on CP1252.
///  • SET MEMOWIDTH minimum is 8 (values &lt; 8 are silently ignored → width unchanged); default 50.
///    CR (CHR(13)) is the hard line break; LF (CHR(10)) is stripped; word-wrap breaks at the last space
///    in the width window, hard-splitting only when a single word has no space inside the window.
///  • SYS(15, cTransTable, cExpr): each char c of cExpr → the byte at 1-based position ASC(c) of
///    cTransTable (out-of-range keeps c). (NB: table is arg2, expression is arg3.)
/// </summary>
public sealed class MicroVfpP3StringsTests
{
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

    // ─────────────────────────── LIKE / LIKEC ───────────────────────────

    [Fact]
    public void Like_WildcardMatrix_CaseSensitive_TrailingBlanksSignificant()
    {
        using var h = new H();
        Assert.True(h.Bool("LIKE('A*','ABC')"));       // * = any run
        Assert.True(h.Bool("LIKE('A?C','ABC')"));      // ? = one char
        Assert.True(h.Bool("LIKE('A?C','AXC')"));
        Assert.False(h.Bool("LIKE('A?C','AC')"));      // ? needs EXACTLY one char
        Assert.True(h.Bool("LIKE('*C','ABC')"));
        Assert.True(h.Bool("LIKE('*','')"));           // * matches the empty string
        Assert.True(h.Bool("LIKE('','')"));            // empty pattern matches empty string
        Assert.False(h.Bool("LIKE('','x')"));          // empty pattern, non-empty string → .F.
        Assert.False(h.Bool("LIKE('abc','ABC')"));     // case-SENSITIVE
        Assert.False(h.Bool("LIKE('ABC','ABC ')"));    // trailing blank in string is significant
        Assert.False(h.Bool("LIKE('ABC ','ABC')"));    // trailing blank in pattern is significant
        Assert.True(h.Bool("LIKE('A*C','ABBBC')"));
        Assert.True(h.Bool("LIKE('?','A')"));
        Assert.False(h.Bool("LIKE('[A]','A')"));       // NO bracket classes — '[A]' is literal
        Assert.True(h.Bool("LIKE('A*','ABC ')"));      // * absorbs the trailing blank
        Assert.True(h.Bool("LIKE('??*','ABC')"));
        Assert.False(h.Bool("LIKE('a.c','abc')"));     // '.' is literal, not a wildcard
    }

    [Fact]
    public void LikeC_IsByteIdenticalToLike_OnCp1252()
    {
        using var h = new H();
        Assert.True(h.Bool("LIKEC('A*','ABC')"));
        Assert.True(h.Bool("LIKEC('A?C','AXC')"));
        Assert.False(h.Bool("LIKEC('abc','ABC')"));
        Assert.False(h.Bool("LIKEC('ABC','ABC ')"));
    }

    // ─────────────────────────── CHRTRANC ───────────────────────────

    [Fact]
    public void ChrtranC_MapsCharByChar_DeletingWhenReplacementShorter()
    {
        using var h = new H();
        Assert.Equal("xByDzF", h.Str("CHRTRANC('ABCDEF','ACE','xyz')"));
        // cToChars shorter than cFromChars ⇒ surplus matches are DELETED (same as CHRTRAN).
        Assert.Equal("xByDF", h.Str("CHRTRANC('ABCDEF','ACE','xy')"));
        // on CP1252 it coincides exactly with CHRTRAN.
        Assert.Equal(h.Str("CHRTRAN('ABCDEF','ACE','xy')"), h.Str("CHRTRANC('ABCDEF','ACE','xy')"));
    }

    // ─────────────────────────── ISLEADBYTE ───────────────────────────

    [Fact]
    public void IsLeadByte_IsAlwaysFalse_OnSingleByteCodePage()
    {
        using var h = new H();
        Assert.False(h.Bool("ISLEADBYTE('A')"));
        Assert.False(h.Bool("ISLEADBYTE(CHR(233))"));   // even a high byte is not a lead byte on CP1252
        Assert.False(h.Bool("ISLEADBYTE('hello')"));
    }

    // ─────────────────────────── DBCS char variants == byte variants ───────────────────────────

    [Fact]
    public void Atc_EmptyNeedle_ReturnsZero()
    {
        using var h = new H();
        Assert.Equal(0m, h.Num("ATC('', 'ABCB')"));
    }

    [Fact]
    public void DbcsCharVariants_CoincideWithByteVariants_OnCp1252()
    {
        using var h = new H();
        // AT_C / ATCC / RATC
        Assert.Equal(4m, h.Num("AT_C('B','ABCB',2)"));
        Assert.Equal(h.Num("AT('B','ABCB',2)"), h.Num("AT_C('B','ABCB',2)"));
        Assert.Equal(2m, h.Num("ATCC('b','ABCB')"));               // case-insensitive
        Assert.Equal(h.Num("ATC('b','ABCB')"), h.Num("ATCC('b','ABCB')"));
        Assert.Equal(4m, h.Num("RATC('B','ABCB')"));
        Assert.Equal(h.Num("RAT('B','ABCB')"), h.Num("RATC('B','ABCB')"));
        // LEFTC / RIGHTC / SUBSTRC / STUFFC
        Assert.Equal("Hel", h.Str("LEFTC('Hello',3)"));
        Assert.Equal("llo", h.Str("RIGHTC('Hello',3)"));
        Assert.Equal("ell", h.Str("SUBSTRC('Hello',2,3)"));
        Assert.Equal("HXYlo", h.Str("STUFFC('Hello',2,2,'XY')"));
        Assert.Equal(h.Str("LEFT('Hello',3)"), h.Str("LEFTC('Hello',3)"));
        Assert.Equal(h.Str("STUFF('Hello',2,2,'XY')"), h.Str("STUFFC('Hello',2,2,'XY')"));
    }

    // ─────────────────────────── NORMALIZE ───────────────────────────

    [Fact]
    public void Normalize_UppercasesOutsideLiterals_DropsWhitespace_ArrowToDot_RequotesLiterals()
    {
        using var h = new H();
        Assert.Equal("ABC", h.Str("NORMALIZE('abc')"));
        Assert.Equal("ABC", h.Str("NORMALIZE('Abc')"));
        Assert.Equal("A+B", h.Str("NORMALIZE('a + b')"));         // whitespace removed
        Assert.Equal("A+B", h.Str("NORMALIZE('a+b')"));
        Assert.Equal("A-B+C", h.Str("NORMALIZE('a - b + c')"));
        Assert.Equal("CUST.NAME", h.Str("NORMALIZE('cust->name')")); // -> becomes .
        // single-quoted literal is re-emitted double-quoted; content is preserved verbatim.
        Assert.Equal("\"hello\"", h.Str("NORMALIZE(\"'hello'\")"));
        Assert.Equal("\"a b\"", h.Str("NORMALIZE(\"'a b'\")"));   // spaces INSIDE a literal survive
        Assert.Equal("\"AbC\"", h.Str("NORMALIZE(\"'AbC'\")"));   // literal content case preserved
        Assert.Equal("\"hi\"", h.Str("NORMALIZE('\"hi\"')"));
    }

    // ─────────────────────────── SYS(15) ───────────────────────────

    [Fact]
    public void Sys15_TranslatesByAsciiPositionIntoTheTable()
    {
        using var h = new H();
        // identity table CHR(0)..CHR(255): each char maps to the byte at 1-based position ASC(c),
        // i.e. CHR(ASC(c)-1) → 'A'(65)→'@'(64), 'B'→'A', 'C'→'B'.
        h.Run("lcId = ''");
        h.Run("FOR ii = 0 TO 255" + "\n  lcId = lcId + CHR(ii)\nENDFOR");
        Assert.Equal("@AB", h.Str("SYS(15, lcId, 'ABC')"));
        // stuff position 66 (maps 'B') with 'Z' → 'B' now translates to 'Z'.
        Assert.Equal("@ZB", h.Str("SYS(15, STUFF(lcId,66,1,'Z'), 'ABC')"));
        // short table (66 chars): 'C'(67) is out of range → left unchanged.
        Assert.Equal("@AC", h.Str("SYS(15, LEFT(lcId,66), 'ABC')"));
        // table is arg2, expression is arg3: SYS(15,'ABC','AB') → 'A'(65)/'B'(66) both out of range → 'AB'.
        Assert.Equal("AB", h.Str("SYS(15, 'ABC', 'AB')"));
    }

    // ─────────────────────────── TXTWIDTH (fixed-pitch / char-count model) ───────────────────────────

    [Fact]
    public void TxtWidth_ReturnsCharacterCount_FixedPitchSemantics()
    {
        using var h = new H();
        // microVFP is headless — no GDI/font engine. TXTWIDTH returns the CHARACTER COUNT, which is EXACT
        // for a fixed-pitch font (VFP: TXTWIDTH('ABC','Courier New',10) = 3.00) and a documented
        // approximation for the proportional desktop font (that value is GUI-/machine-bound → FLAGGED).
        Assert.Equal(3m, h.Num("TXTWIDTH('ABC')"));
        Assert.Equal(3m, h.Num("TXTWIDTH('ABC','Courier New',10)"));
        Assert.Equal(0m, h.Num("TXTWIDTH('')"));
        Assert.Equal(5m, h.Num("TXTWIDTH('Hello')"));
    }

    // ─────────────────────────── STRTOFILE / FILETOSTR ───────────────────────────

    [Fact]
    public void StrToFile_And_FileToStr_OverwriteAdditiveAndBomFlags()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_p3str_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var h = new H();
            h.Run("lcF = '" + dir.Replace("\\", "\\\\") + "\\\\out.txt'");

            // return value = bytes written; default overwrites.
            Assert.Equal(5m, h.Num("STRTOFILE('hello', lcF)"));
            Assert.Equal("hello", h.Str("FILETOSTR(lcF)"));

            // logical .T. (3rd arg) = ADDITIVE (append); return = bytes appended.
            Assert.Equal(6m, h.Num("STRTOFILE(' world', lcF, .T.)"));
            Assert.Equal("hello world", h.Str("FILETOSTR(lcF)"));

            // default (no flag) overwrites again.
            Assert.Equal(4m, h.Num("STRTOFILE('over', lcF)"));
            Assert.Equal("over", h.Str("FILETOSTR(lcF)"));

            // numeric flag 1 = ADDITIVE (append).
            Assert.Equal(1m, h.Num("STRTOFILE('x', lcF, 1)"));
            Assert.Equal("overx", h.Str("FILETOSTR(lcF)"));

            // flag 2 = overwrite + UTF-16LE BOM (FF FE); string bytes written as-is; return counts the BOM.
            Assert.Equal(4m, h.Num("STRTOFILE('AB', lcF, 2)"));
            Assert.Equal(255m, h.Num("ASC(SUBSTR(FILETOSTR(lcF),1,1))"));
            Assert.Equal(254m, h.Num("ASC(SUBSTR(FILETOSTR(lcF),2,1))"));
            Assert.Equal(4m, h.Num("LEN(FILETOSTR(lcF))"));

            // flag 4 = overwrite + UTF-8 BOM (EF BB BF).
            Assert.Equal(5m, h.Num("STRTOFILE('AB', lcF, 4)"));
            Assert.Equal(239m, h.Num("ASC(SUBSTR(FILETOSTR(lcF),1,1))"));
            Assert.Equal(187m, h.Num("ASC(SUBSTR(FILETOSTR(lcF),2,1))"));
            Assert.Equal(191m, h.Num("ASC(SUBSTR(FILETOSTR(lcF),3,1))"));
            Assert.Equal(5m, h.Num("LEN(FILETOSTR(lcF))"));

            // FILETOSTR of a MISSING file raises a trappable VFP err 1 "File does not exist." (VFP9-verified —
            // it does NOT return ""). EvalExpression is fail-soft (swallows to .NULL.), so the throw is observed
            // through the =expr statement form (which goes through the throwing evaluator).
            h.Run("lcMissing = '" + dir.Replace("\\", "\\\\") + "\\\\nope.txt'");
            var exMissing = Assert.Throws<MicroVfpRuntimeException>(() => h.Run("=FILETOSTR(lcMissing)"));
            Assert.Equal(1, exMissing.VfpErrorNumber);

            // STRTOFILE to an unwritable target (here a NONEXISTENT sub-directory) returns 0 — VFP9's write-
            // failure return is 0, NOT a -1 sentinel.
            // Path.Combine, NOT a hard-coded backslash: on Unix '\' is a valid filename character, so a
            // backslash-built "missing sub-directory" silently becomes a writable file in `dir` and
            // STRTOFILE returns 2 instead of the pinned 0 (caught by the Linux CI leg).
            h.Run("lcBad = '" + Path.Combine(dir, "nodir", "out.txt").Replace("\\", "\\\\") + "'");
            Assert.Equal(0m, h.Num("STRTOFILE('hi', lcBad)"));

            // A numeric flag outside {0,1,2,4} is NOT a bitmask — VFP9 raises err 11 (it does not overwrite).
            var exFlag = Assert.Throws<MicroVfpRuntimeException>(() => h.Run("=STRTOFILE('hi', lcF, 3)"));
            Assert.Equal(11, exFlag.VfpErrorNumber);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── MEMLINES / MLINE + SET MEMOWIDTH ───────────────────────────

    [Fact]
    public void MemLines_And_MLine_WordWrapAtMemoWidth()
    {
        using var h = new H();
        h.Run("lcM = 'The quick brown fox jumps over the lazy dog'");

        h.Run("SET MEMOWIDTH TO 20");
        Assert.Equal(3m, h.Num("MEMLINES(lcM)"));
        Assert.Equal("The quick brown fox ", h.Str("MLINE(lcM,1)"));  // wrap keeps the break space
        Assert.Equal("jumps over the lazy ", h.Str("MLINE(lcM,2)"));
        Assert.Equal("dog", h.Str("MLINE(lcM,3)"));

        h.Run("SET MEMOWIDTH TO 10");
        Assert.Equal(5m, h.Num("MEMLINES(lcM)"));
        Assert.Equal("The quick ", h.Str("MLINE(lcM,1)"));
        Assert.Equal("brown fox ", h.Str("MLINE(lcM,2)"));
    }

    [Fact]
    public void MemoWidth_MinimumIsEight_ValuesBelowEightAreIgnored()
    {
        using var h = new H();
        Assert.Equal(50m, h.Num("SET('MEMOWIDTH')"));   // default 50
        h.Run("SET MEMOWIDTH TO 40");
        Assert.Equal(40m, h.Num("SET('MEMOWIDTH')"));
        h.Run("SET MEMOWIDTH TO 5");                    // below the minimum of 8 → silently ignored
        Assert.Equal(40m, h.Num("SET('MEMOWIDTH')"));
        h.Run("SET MEMOWIDTH TO 8");                    // 8 is accepted
        Assert.Equal(8m, h.Num("SET('MEMOWIDTH')"));
    }

    [Fact]
    public void MLine_HardSplitsAWordWithNoSpaceInTheWindow()
    {
        using var h = new H();
        h.Run("SET MEMOWIDTH TO 8");
        h.Run("lcT = 'abcdefghij'");
        Assert.Equal(2m, h.Num("MEMLINES(lcT)"));
        Assert.Equal("abcdefgh", h.Str("MLINE(lcT,1)"));  // hard split at width 8
        Assert.Equal("ij", h.Str("MLINE(lcT,2)"));
    }

    [Fact]
    public void MLine_CrIsHardBreak_LfIsStripped()
    {
        using var h = new H();
        h.Run("SET MEMOWIDTH TO 50");
        h.Run("lcC = 'alpha' + CHR(13) + 'beta' + CHR(13) + CHR(10) + 'gamma'");
        Assert.Equal(3m, h.Num("MEMLINES(lcC)"));
        Assert.Equal("alpha", h.Str("MLINE(lcC,1)"));   // CR terminates; the char is NOT in the line
        Assert.Equal("beta", h.Str("MLINE(lcC,2)"));
        Assert.Equal("gamma", h.Str("MLINE(lcC,3)"));   // the CRLF's LF is stripped, not a second break

        // a lone LF is stripped entirely (not a line break).
        h.Run("lcL = 'aaa' + CHR(10) + 'bbb'");
        Assert.Equal(1m, h.Num("MEMLINES(lcL)"));
        Assert.Equal("aaabbb", h.Str("MLINE(lcL,1)"));
    }

    [Fact]
    public void MLine_EmptyAndBeyondLastLine()
    {
        using var h = new H();
        h.Run("SET MEMOWIDTH TO 50");
        Assert.Equal(0m, h.Num("MEMLINES('')"));         // empty memo → 0 lines
        Assert.Equal("", h.Str("MLINE('',1)"));
        h.Run("lcD = 'one' + CHR(13) + 'two'");
        Assert.Equal(2m, h.Num("MEMLINES(lcD)"));
        Assert.Equal("", h.Str("MLINE(lcD,3)"));         // beyond the last line → ""
        Assert.Equal("", h.Str("MLINE(lcD,0)"));         // line 0 → ""
        // trailing CR does NOT create an extra empty line.
        h.Run("lcTr = 'aaa' + CHR(13) + 'bbb' + CHR(13)");
        Assert.Equal(2m, h.Num("MEMLINES(lcTr)"));
        // interior double CR DOES create an empty middle line.
        h.Run("lcDb = 'aaa' + CHR(13) + CHR(13) + 'bbb'");
        Assert.Equal(3m, h.Num("MEMLINES(lcDb)"));
        Assert.Equal("", h.Str("MLINE(lcDb,2)"));
        Assert.Equal("bbb", h.Str("MLINE(lcDb,3)"));
    }

    [Fact]
    public void MLine_OffsetMechanics_MLineSystemVariableAdvancesPastEachLine()
    {
        using var h = new H();
        h.Run("SET MEMOWIDTH TO 20");
        h.Run("lcM = 'The quick brown fox jumps over the lazy dog'");
        h.Run("_MLINE = 0");
        // MLINE(memo, 1, _MLINE) returns the line starting at offset _MLINE and advances _MLINE past it.
        Assert.Equal("The quick brown fox ", h.Str("MLINE(lcM,1,_MLINE)"));
        Assert.Equal(20m, h.Num("_MLINE"));
        Assert.Equal("jumps over the lazy ", h.Str("MLINE(lcM,1,_MLINE)"));
        Assert.Equal(40m, h.Num("_MLINE"));
        Assert.Equal("dog", h.Str("MLINE(lcM,1,_MLINE)"));
        Assert.Equal(43m, h.Num("_MLINE"));
    }

    [Fact]
    public void MLine_OffsetMechanics_CrAndCrlf_MLineLandsAfterTheCr()
    {
        using var h = new H();
        h.Run("SET MEMOWIDTH TO 50");

        // CR only: _MLINE lands just past the CR (start of the next line).
        h.Run("lcE = 'alpha' + CHR(13) + 'beta'");
        h.Run("_MLINE = 0");
        Assert.Equal("alpha", h.Str("MLINE(lcE,1,_MLINE)"));
        Assert.Equal(6m, h.Num("_MLINE"));
        Assert.Equal("beta", h.Str("MLINE(lcE,1,_MLINE)"));
        Assert.Equal(10m, h.Num("_MLINE"));

        // CRLF: _MLINE lands on the LF after line 1; the next scan skips that leading LF.
        h.Run("lcF = 'alpha' + CHR(13) + CHR(10) + 'beta'");
        h.Run("_MLINE = 0");
        Assert.Equal("alpha", h.Str("MLINE(lcF,1,_MLINE)"));
        Assert.Equal(6m, h.Num("_MLINE"));
        Assert.Equal("beta", h.Str("MLINE(lcF,1,_MLINE)"));
        Assert.Equal(11m, h.Num("_MLINE"));
    }

    // ─────────────────────────── ATLINE / ATCLINE / RATLINE ───────────────────────────

    [Fact]
    public void AtLine_AtcLine_RatLine_ReturnLineNumberUnderMemoWidth()
    {
        using var h = new H();
        h.Run("SET MEMOWIDTH TO 20");
        h.Run("lcM = 'The quick brown fox jumps over the lazy dog'");
        // lines: 1="The quick brown fox " 2="jumps over the lazy " 3="dog"
        Assert.Equal(1m, h.Num("ATLINE('fox', lcM)"));
        Assert.Equal(3m, h.Num("ATLINE('dog', lcM)"));
        Assert.Equal(0m, h.Num("ATLINE('zzz', lcM)"));    // not found → 0
        Assert.Equal(1m, h.Num("ATCLINE('FOX', lcM)"));   // case-insensitive
        // "the" (lowercase) only occurs in line 2 ("the lazy"); line 1 has "The".
        Assert.Equal(2m, h.Num("ATLINE('the', lcM)"));
        Assert.Equal(2m, h.Num("RATLINE('the', lcM)"));   // last matching line, from the end
    }

    // ─────────────────────────── memo FIELD integration ───────────────────────────

    [Fact]
    public void MemLines_And_MLine_WorkOnA_RealMemoField()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_p3mem_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dbf = Path.Combine(dir, "mt.dbf");
            using (var w = DbfWriter.Create(dbf, new[]
            {
                new DbfColumnDef("id", 'I', 4),
                new DbfColumnDef("notes", 'M', 4),
            }))
            {
                w.AppendRecord(1, "The quick brown fox jumps over the lazy dog");
                w.Flush();
            }
            using var s = new VfpSession();
            s.OpenDirectory(dir);
            var interp = new VfpInterpreter(s);
            interp.Execute("USE mt");
            interp.Execute("SET MEMOWIDTH TO 20");
            Assert.Equal(3m, interp.EvalExpression("MEMLINES(notes)").AsNumber);
            Assert.Equal("The quick brown fox ", interp.EvalExpression("MLINE(notes,1)").AsString);
            Assert.Equal("jumps over the lazy ", interp.EvalExpression("MLINE(notes,2)").AsString);
            Assert.Equal(2m, interp.EvalExpression("ATLINE('lazy', notes)").AsNumber);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
