using System;
using System.Collections.Generic;
using System.Text;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP P3 — the MEMO-LINE family (MICROVFP_EXTENSIONS_BACKLOG §C.14 + §C.17):
//  MEMLINES() / MLINE() (incl. the _MLINE offset mechanics) and ATLINE()/ATCLINE()/RATLINE().
//
//  All of them segment a character/memo value into "lines" the SAME way, driven by SET MEMOWIDTH — the
//  algorithm was re-derived byte-for-byte against the local VFP9 runtime (see MicroVfpP3StringsOracleTests):
//    • CHR(13) (CR) is the ONLY hard line break; it is NOT part of the returned line.
//    • CHR(10) (LF) is STRIPPED entirely — never a break, never kept (so a lone LF joins the two sides).
//    • Within a CR-delimited paragraph the text is word-wrapped at the current MEMOWIDTH: the line takes as
//      many whole words as fit; the break happens at the LAST space inside the width window and that space
//      STAYS on the line (so a wrapped line can be exactly MEMOWIDTH chars, trailing space included). When
//      the width window contains NO space, the single word is HARD-split at exactly MEMOWIDTH. When the char
//      just past the window is itself a space, the line is exactly MEMOWIDTH chars and the space starts the
//      next line.
//    • _MLINE is the absolute character offset where the NEXT line begins (one less than the 1-based position
//      of that line's first character — the documented hackfox s4g083 off-by-one), updated after every MLINE().
//    • An empty memo is 0 lines; a trailing CR (or trailing CRLF) does NOT add an empty line; an interior
//      double CR DOES yield an empty middle line.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class VfpInterpreter
{
    // Chained from TryInvokeFileIo's default arm (kept OUT of the giant HostInvoke switch so its native stack
    // frame stays small — the MaxCallDepth recursion headroom depends on it, same rationale as the file I/O).
    private bool TryInvokeMemoLines(string name, VfpValue[] a, out VfpValue r)
    {
        switch (name)
        {
            case "MEMLINES": r = VfpValue.Integer(FnMemLines(a)); return true;
            case "MLINE": r = VfpValue.Character(FnMLine(a)); return true;
            case "ATLINE": r = VfpValue.Integer(FnAtLine(a, StringComparison.Ordinal, fromEnd: false)); return true;
            case "ATCLINE": r = VfpValue.Integer(FnAtLine(a, StringComparison.OrdinalIgnoreCase, fromEnd: false)); return true;
            case "RATLINE": r = VfpValue.Integer(FnAtLine(a, StringComparison.Ordinal, fromEnd: true)); return true;
            default: r = VfpValue.Null; return false;
        }
    }

    /// <summary>Compute the memo line that begins at absolute offset <paramref name="start"/>, returning its
    /// text and the offset where the NEXT line begins. See the file header for the exact CR/LF/word-wrap rules.
    /// A leading LF (left over from a prior CRLF break) is skipped; the returned text never contains the
    /// terminating CR.</summary>
    private static (string line, int next) ComputeLine(string memo, int start, int width)
    {
        int n = memo.Length;
        int i = start;
        while (i < n && memo[i] == '\n') i++;          // skip a leading LF (from a prior CRLF break)
        if (i >= n) return (string.Empty, n);

        var sb = new StringBuilder();
        int placed = 0;                                 // chars placed on the line (LF excluded)
        int lastSpaceSb = -1;                           // sb length up to & INCLUDING the last placed space
        int lastSpaceMemo = -1;                         // memo index of that space
        while (i < n)
        {
            char c = memo[i];
            if (c == '\r') return (sb.ToString(), i + 1);          // CR: hard break, excluded, next past it
            if (c == '\n') { i++; continue; }                       // LF: stripped (zero width)
            if (placed == width)
            {
                if (c == ' ') return (sb.ToString(), i);            // window ends AT a space → space starts next line
                if (lastSpaceMemo >= 0) return (sb.ToString(0, lastSpaceSb), lastSpaceMemo + 1); // break after last space
                return (sb.ToString(), i);                          // no space in window → hard split at width
            }
            sb.Append(c);
            placed++;
            if (c == ' ') { lastSpaceSb = sb.Length; lastSpaceMemo = i; }
            i++;
        }
        return (sb.ToString(), n);                       // reached end of memo
    }

    /// <summary>True when everything from <paramref name="off"/> to the end is bare LF (or nothing) — such a
    /// trailing remainder is NOT a line (LF contributes nothing), so a trailing CRLF/LF adds no empty line.</summary>
    private static bool OnlyLfRemains(string memo, int off)
    {
        for (int i = off; i < memo.Length; i++)
            if (memo[i] != '\n') return false;
        return true;
    }

    /// <summary>MEMLINES(memo): the number of wrapped/CR-delimited lines under the current SET MEMOWIDTH.</summary>
    private int FnMemLines(VfpValue[] a)
    {
        if (a.Length < 1) return 0;
        string memo = a[0].AsString;
        int n = memo.Length;
        if (n == 0) return 0;
        int count = 0, off = 0;
        while (off < n)
        {
            if (OnlyLfRemains(memo, off)) break;        // trailing LF-only remainder is not a line
            (_, int next) = ComputeLine(memo, off, _memoWidth);
            count++;
            if (next <= off) break;                     // progress guard
            off = next;
        }
        return count;
    }

    /// <summary>MLINE(memo, nLine [, nOffset]): the text of line <paramref name="nLine"/> (counted from the
    /// optional absolute character offset — the fast sequential-access form), and updates <c>_MLINE</c> to the
    /// offset where the following line begins. nLine &lt; 1 or beyond the last line yields "".</summary>
    private string FnMLine(VfpValue[] a)
    {
        if (a.Length < 2) return string.Empty;
        string memo = a[0].AsString;
        int nLine = a[1].TruncatedInt32;
        int off = a.Length > 2 ? a[2].TruncatedInt32 : 0;
        int n = memo.Length;
        if (off < 0) off = 0;
        if (nLine < 1) return string.Empty;

        string line = string.Empty;
        for (int k = 0; k < nLine; k++)
        {
            if (off >= n || OnlyLfRemains(memo, off)) { SetMLine(n); return string.Empty; }
            (line, int next) = ComputeLine(memo, off, _memoWidth);
            if (next <= off) { SetMLine(n); return line; }
            off = next;
        }
        SetMLine(off);
        return line;
    }

    // _MLINE is a writable system memory variable (like _TALLY) holding the offset of the NEXT line's start.
    private void SetMLine(int offset) => Memory.Set("_MLINE", VfpValue.Integer(offset));

    /// <summary>ATLINE/ATCLINE/RATLINE(cSearch, cExpr): the 1-based number of the first (or, for RATLINE, the
    /// last) memo line that CONTAINS <paramref name="a"/>[0], under the current SET MEMOWIDTH; 0 when no line
    /// contains it. There is no nOccurrence parameter (hackfox s4g029). Case-sensitivity per
    /// <paramref name="cmp"/> (ATCLINE is the only case-insensitive one; RATLINE has no CI twin).</summary>
    private int FnAtLine(VfpValue[] a, StringComparison cmp, bool fromEnd)
    {
        if (a.Length < 2) return 0;
        string needle = a[0].AsString;
        string memo = a[1].AsString;
        int n = memo.Length;
        if (n == 0) return 0;

        int lineNo = 0, found = 0, off = 0;
        while (off < n)
        {
            if (OnlyLfRemains(memo, off)) break;
            (string line, int next) = ComputeLine(memo, off, _memoWidth);
            lineNo++;
            if (line.IndexOf(needle, cmp) >= 0)
            {
                found = lineNo;
                if (!fromEnd) return found;             // ATLINE/ATCLINE: first match
            }
            if (next <= off) break;
            off = next;
        }
        return found;                                    // RATLINE: last match (0 if none)
    }
}
