using System;
using System.Buffers;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// VFP's table-driven collations: <c>GENERAL</c> (the VFP9 Western and Central European code pages), <c>SPANISH</c> (1252
/// and 850) and <c>GERMAN</c> (1252, 437, 10000). The two-pass key is built from a per-code-page weight table (<see cref="GeneralWeights1252"/> and the
/// CP850 / CP437 tables, ported from CodeBase <c>COLL4ARR.C</c>, the CP1250 table derived from VFP9's keys, plus
/// their VFP9-verified variants):
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>a <b>head</b> (primary) byte per character — the case- AND accent-folded
///   sort weight, with ligatures expanded to two letters (<c>Œ→OE</c>, <c>Æ→AE</c>,
///   <c>Þ→TH</c>, <c>ß→SS</c>; CP850 GENERAL also <c>Ä→AE</c>, <c>Ö→OE</c>, <c>Ü→UE</c>); then</item>
///   <item>a <b>tail</b> region: the diacritic weight of every character that has one.</item>
/// </list>
/// <para>
/// Each character is weighted by its byte in the collation's <see cref="CodePage"/> — the TABLE code page of
/// the index — so the same text collates differently in a CP850 and a CP1252 table, exactly as in VFP9.
/// <see cref="Instance"/> is CP1252 GENERAL; <see cref="VfpCollations.ByName(string?, int)"/> picks the
/// variant for a table code page.
/// </para>
/// <para>
/// <see cref="GetCollatedKey"/> returns the NATURAL key (heads then tails, trailing 0x00
/// tail bytes trimmed); VFP9 pads it with 0x00 to the tag key length, so the trimmed tails
/// and the pad are the same bytes. The stored GENERAL CDX key IS these weight bytes.
/// Examples: <c>"ADMIN"</c> → <c>60 64 6F 6A 70</c>; <c>"JMÖLCK"</c> →
/// <c>6B 6F 72 6D 62 6C 04</c>; <c>"ARÖTTGER"</c> → <c>60 75 72 77 77 68 66 75 00 04</c>.
/// </para>
/// </remarks>
public sealed class GeneralCollation : IVfpCollation
{
    /// <summary>The shared CP1252 GENERAL singleton (the weight table is immutable).</summary>
    public static readonly GeneralCollation Instance = new("GENERAL", CollationTable.General1252);

    /// <summary>GENERAL over CP850 table bytes.</summary>
    internal static readonly GeneralCollation Cp850 = new("GENERAL", CollationTable.General850);

    /// <summary>GENERAL over CP437 table bytes.</summary>
    internal static readonly GeneralCollation Cp437 = new("GENERAL", CollationTable.General437);

    /// <summary>GENERAL over CP1250 table bytes.</summary>
    internal static readonly GeneralCollation Cp1250 = new("GENERAL", CollationTable.General1250);

    /// <summary>GENERAL over CP852 table bytes.</summary>
    internal static readonly GeneralCollation Cp852 = new("GENERAL", CollationTable.General852);

    /// <summary>GENERAL over CP857 table bytes.</summary>
    internal static readonly GeneralCollation Cp857 = new("GENERAL", CollationTable.General857);

    /// <summary>GENERAL over CP861 table bytes.</summary>
    internal static readonly GeneralCollation Cp861 = new("GENERAL", CollationTable.General861);

    /// <summary>GENERAL over CP863 table bytes.</summary>
    internal static readonly GeneralCollation Cp863 = new("GENERAL", CollationTable.General863);

    /// <summary>GENERAL over CP865 table bytes.</summary>
    internal static readonly GeneralCollation Cp865 = new("GENERAL", CollationTable.General865);

    /// <summary>GENERAL over CP1254 table bytes.</summary>
    internal static readonly GeneralCollation Cp1254 = new("GENERAL", CollationTable.General1254);

    /// <summary>GENERAL over CP10000 table bytes.</summary>
    internal static readonly GeneralCollation Cp10000 = new("GENERAL", CollationTable.General10000);

    /// <summary>GENERAL over CP10029 table bytes.</summary>
    internal static readonly GeneralCollation Cp10029 = new("GENERAL", CollationTable.General10029);

    /// <summary>GENERAL over CP620 table bytes.</summary>
    internal static readonly GeneralCollation Cp620 = new("GENERAL", CollationTable.General620);

    /// <summary>GENERAL over CP895 table bytes.</summary>
    internal static readonly GeneralCollation Cp895 = new("GENERAL", CollationTable.General895);

    /// <summary>SPANISH over CP1252 table bytes.</summary>
    internal static readonly GeneralCollation Spanish1252 = new("SPANISH", CollationTable.Spanish1252);

    /// <summary>SPANISH over CP850 table bytes.</summary>
    internal static readonly GeneralCollation Spanish850 = new("SPANISH", CollationTable.Spanish850);

    internal static readonly GeneralCollation German1252 = new("GERMAN", CollationTable.German1252);
    internal static readonly GeneralCollation German437 = new("GERMAN", CollationTable.German437);
    internal static readonly GeneralCollation German10000 = new("GERMAN", CollationTable.German10000);

    private readonly string _name;
    private readonly CollationTable _table;

    internal GeneralCollation(string name, CollationTable table)
    {
        _name = name;
        _table = table;
    }

    /// <inheritdoc/>
    public string Name => _name;

    /// <summary>The code page whose bytes this collation's weight table is indexed by (the table code page).</summary>
    public int CodePage => _table.CodePage;

    /// <summary>
    /// Builds the natural GENERAL key for <paramref name="s"/>: the head pass then the
    /// diacritic-tail pass, with trailing 0x00 tail bytes trimmed and trailing source
    /// spaces ignored (VFP keys on the trimmed value). The host pads the result with
    /// 0x00 to the stored tag key length for the byte-exact CDX key.
    /// </summary>
    public byte[] GetCollatedKey(ReadOnlySpan<char> s)
    {
        // Capacity comes from the longest expansion in this table.
        int cap = s.Length * _table.MaxWeights;
        Span<byte> heads = cap <= StackCap ? stackalloc byte[StackCap] : new byte[cap];
        Span<byte> tails = cap <= StackCap ? stackalloc byte[StackCap] : new byte[cap];

        BuildKey(s, heads, tails, out int hc, out int tc);

        var key = new byte[hc + tc];
        heads[..hc].CopyTo(key);
        tails[..tc].CopyTo(key.AsSpan(hc));
        return key;
    }

    /// <summary>Suffixes that can complete a contraction across a prefix boundary.</summary>
    internal string[] ContractionSuffixes => _table.ContractionSuffixes;

    /// <summary>
    /// The head (primary) weights of <paramref name="s"/> alone — the leading part of <see cref="GetCollatedKey"/>
    /// before the diacritic tails. VFP9 SEEKs with this needle under SET EXACT OFF.
    /// </summary>
    internal byte[] GetHeadKey(ReadOnlySpan<char> s)
    {
        int cap = s.Length * _table.MaxWeights;
        Span<byte> heads = cap <= StackCap ? stackalloc byte[StackCap] : new byte[cap];
        Span<byte> tails = cap <= StackCap ? stackalloc byte[StackCap] : new byte[cap];

        BuildKey(s, heads, tails, out int hc, out _);
        return heads[..hc].ToArray();
    }

    /// <summary>
    /// The head weight of a blank (0x11 under CP1252). Every head is at least 0x10, while
    /// every tail and the 0x00 pad are below 0x10, so a key byte under 0x10 lies past the value's heads.
    /// </summary>
    internal byte BlankHead => _table.Head[_table.CharToByte[' ']];

    /// <inheritdoc/>
    public int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        // Zero-allocation: build both keys into stack (or pooled) head/tail buffers and
        // compare the logical heads-then-tails sequences byte-by-byte. No byte[] per call,
        // which matters on the per-record hot path ($/=/<=/>= for Character operands).
        int capL = left.Length * _table.MaxWeights;
        int capR = right.Length * _table.MaxWeights;

        byte[]? rhL = null, rtL = null, rhR = null, rtR = null;
        Span<byte> hL = capL <= StackCap ? stackalloc byte[capL] : (rhL = ArrayPool<byte>.Shared.Rent(capL));
        Span<byte> tL = capL <= StackCap ? stackalloc byte[capL] : (rtL = ArrayPool<byte>.Shared.Rent(capL));
        Span<byte> hR = capR <= StackCap ? stackalloc byte[capR] : (rhR = ArrayPool<byte>.Shared.Rent(capR));
        Span<byte> tR = capR <= StackCap ? stackalloc byte[capR] : (rtR = ArrayPool<byte>.Shared.Rent(capR));
        try
        {
            BuildKey(left, hL, tL, out int hcL, out int tcL);
            BuildKey(right, hR, tR, out int hcR, out int tcR);
            return CompareKey(hL[..hcL], tL[..tcL], hR[..hcR], tR[..tcR]);
        }
        finally
        {
            if (rhL is not null) ArrayPool<byte>.Shared.Return(rhL);
            if (rtL is not null) ArrayPool<byte>.Shared.Return(rtL);
            if (rhR is not null) ArrayPool<byte>.Shared.Return(rhR);
            if (rtR is not null) ArrayPool<byte>.Shared.Return(rtR);
        }
    }

    /// <summary>
    /// Compares two strings with the length rule of an operator: <see cref="StringCompareMode.Pad"/> is
    /// <see cref="Compare(ReadOnlySpan{char}, ReadOnlySpan{char})"/>; the cutting modes keep trailing blanks and the 0x00
    /// tails, cut the head run (of the left operand, or of the longer one) to the other's head count and compare the
    /// heads, then the tails, where tails that merely extend the other operand's tails count as equal. VFP9-verified
    /// for GENERAL; SPANISH ligatures under SET EXACT OFF keep the deviations listed in <c>StringComparisonGoldenTests</c>.
    /// </summary>
    internal int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right, StringCompareMode mode)
    {
        if (mode == StringCompareMode.Pad) return Compare(left, right);
        // A character-for-character prefix is equal before any weighting: under SPANISH 'chico' = 'c' holds although
        // 'ch' is one head, while 'ch' = 'C' does not (VFP9). For GENERAL the weighted path gives the same answer.
        if (left.StartsWith(right) || (mode == StringCompareMode.Shorter && right.StartsWith(left))) return 0;

        int capL = left.Length * _table.MaxWeights;
        int capR = right.Length * _table.MaxWeights;
        byte[]? rhL = null, rtL = null, rhR = null, rtR = null;
        Span<byte> hL = capL <= StackCap ? stackalloc byte[capL] : (rhL = ArrayPool<byte>.Shared.Rent(capL));
        Span<byte> tL = capL <= StackCap ? stackalloc byte[capL] : (rtL = ArrayPool<byte>.Shared.Rent(capL));
        Span<byte> hR = capR <= StackCap ? stackalloc byte[capR] : (rhR = ArrayPool<byte>.Shared.Rent(capR));
        Span<byte> tR = capR <= StackCap ? stackalloc byte[capR] : (rtR = ArrayPool<byte>.Shared.Rent(capR));
        try
        {
            BuildKey(left, hL, tL, out int hcL, out int tcL, trim: false);
            BuildKey(right, hR, tR, out int hcR, out int tcR, trim: false);
            if (mode == StringCompareMode.TruncateLeft) hcL = Math.Min(hcL, hcR);
            else hcL = hcR = Math.Min(hcL, hcR);

            int d = hL[..hcL].SequenceCompareTo(hR[..hcR]);
            if (d != 0) return d < 0 ? -1 : 1;

            var a = tL[..tcL];
            var b = tR[..tcR];
            if (a.StartsWith(b)) return 0;
            if (mode == StringCompareMode.Shorter && b.StartsWith(a)) return 0;
            d = a.SequenceCompareTo(b);
            return d < 0 ? -1 : 1;
        }
        finally
        {
            if (rhL is not null) ArrayPool<byte>.Shared.Return(rhL);
            if (rtL is not null) ArrayPool<byte>.Shared.Return(rtL);
            if (rhR is not null) ArrayPool<byte>.Shared.Return(rhR);
            if (rtR is not null) ArrayPool<byte>.Shared.Return(rtR);
        }
    }

    /// <summary>Stack-buffer threshold (bytes). Inputs whose worst-case head/tail buffer fits are
    /// keyed with no heap traffic at all; longer ones fall back to an <see cref="ArrayPool{T}"/>
    /// rent (Compare) or a plain array (GetCollatedKey, which must allocate the result anyway).</summary>
    private const int StackCap = 256;

    /// <summary>
    /// Shared two-pass key builder: writes the head (primary) weights into
    /// <paramref name="heads"/> and the diacritic (tail) weights into <paramref name="tails"/>,
    /// expanding ligatures (Œ→OE, Æ→AE, Þ→TH, ß→SS), folding SPANISH contractions (ch, ll) into one
    /// head, trimming (unless <paramref name="trim"/> is off) trailing source spaces and trailing 0x00 tail bytes. Buffers must each hold at
    /// least <c>s.Length * _table.MaxWeights</c> bytes. The logical key is <c>heads[..hc]</c> followed by <c>tails[..tc]</c>.
    /// </summary>
    private void BuildKey(ReadOnlySpan<char> s, Span<byte> heads, Span<byte> tails, out int hc, out int tc, bool trim = true)
    {
        int len = s.Length;
        while (trim && len > 0 && s[len - 1] == ' ') len--;

        var table = _table;
        var head = table.Head;
        var tail = table.Tail;
        var toByte = table.CharToByte;
        var contractions = table.Contractions;
        hc = 0; tc = 0;

        for (int i = 0; i < len; i++)
        {
            byte idx = toByte[s[i]];
            if (contractions.Length != 0 && table.ContractionStarts[idx] && i + 1 < len
                && TryContract(contractions, s.Slice(i, len - i), toByte, out var contracted))
            {
                var weights = contracted!.Weights;
                weights.Heads.CopyTo(heads[hc..]);
                weights.Tails.CopyTo(tails[tc..]);
                hc += weights.Heads.Length;
                tc += weights.Tails.Length;
                i += contracted.Input.Length - 1;
                continue;
            }

            byte h = head[idx];
            if (h != GeneralWeights1252.Expand)
            {
                heads[hc++] = h;
                byte t = tail[idx];
                if (t != GeneralWeights1252.NoTail) tails[tc++] = t;
            }
            else
            {
                var weights = table.Expansions[tail[idx]];
                weights.Heads.CopyTo(heads[hc..]);
                weights.Tails.CopyTo(tails[tc..]);
                hc += weights.Heads.Length;
                tc += weights.Tails.Length;
            }
        }

        while (trim && tc > 0 && tails[tc - 1] == 0) tc--;
    }

    private static bool TryContract(CollationContraction[] contractions, ReadOnlySpan<char> input,
        byte[] toByte, out CollationContraction? matched)
    {
        foreach (var rule in contractions)
        {
            if (input.Length < rule.Input.Length) continue;
            int i = 0;
            for (; i < rule.Input.Length; i++)
            {
                byte b = toByte[input[i]];
                if (rule.FoldAsciiCase && b is >= (byte)'A' and <= (byte)'Z') b |= 0x20;
                if (b != rule.Input[i]) break;
            }
            if (i == rule.Input.Length)
            {
                matched = rule;
                return true;
            }
        }
        matched = null;
        return false;
    }

    /// <summary>Unsigned byte-order compare of two logical keys, each given as a head run
    /// followed by a tail run, without materialising either concatenation.</summary>
    private static int CompareKey(ReadOnlySpan<byte> hA, ReadOnlySpan<byte> tA,
                                  ReadOnlySpan<byte> hB, ReadOnlySpan<byte> tB)
    {
        int lenA = hA.Length + tA.Length;
        int lenB = hB.Length + tB.Length;
        int n = Math.Min(lenA, lenB);
        for (int i = 0; i < n; i++)
        {
            byte x = i < hA.Length ? hA[i] : tA[i - hA.Length];
            byte y = i < hB.Length ? hB[i] : tB[i - hB.Length];
            int d = x - y;
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return lenA.CompareTo(lenB);
    }
}
