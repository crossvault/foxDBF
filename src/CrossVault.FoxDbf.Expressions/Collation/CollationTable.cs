using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// One VFP weight-table collation for ONE code page: the per-byte head/tail weights, the ligature expansions,
/// the optional two-letter contractions and the char → code-page byte map that feeds them. Consumed by
/// <see cref="GeneralCollation"/>, which runs the shared two-pass (heads, then tails) key algorithm.
/// </summary>
/// <remarks>
/// Every table below reproduces VFP9's stored CDX keys byte-for-byte. Probe 2026-10-22: VFP9 indexed all 255
/// non-zero bytes (as <c>X+'a'</c> and <c>X+'á'</c>) and 600 random strings per table; GENERAL 1252/850 and
/// SPANISH 1252/850 matched with 0 mismatches over 1142 keys each, GENERAL 437/1250 additionally over all 65 025
/// byte pairs and 18 472 letter triples (pinned by <c>CollationCodePageVfp9OracleTests</c>).
/// </remarks>
internal sealed class CollationTable
{
    /// <summary>Head (primary) weight per code-page byte; <see cref="GeneralWeights1252.Expand"/> = expansion.</summary>
    public byte[] Head { get; }

    /// <summary>Tail (diacritic) weight per byte; <see cref="GeneralWeights1252.NoTail"/> = none. For an
    /// expansion slot the index into <see cref="Expansions"/>.</summary>
    public byte[] Tail { get; }

    /// <summary>Ligature expansions: byte pairs whose own head/tail entries are emitted in place of the ligature.</summary>
    public byte[][] Expansions { get; }

    /// <summary>When <see langword="false"/> an expansion emits only the two heads (VFP9 SPANISH: <c>Æ</c> is
    /// <c>A E</c> with no tail bytes); GENERAL also emits the letters' tails.</summary>
    public bool ExpansionTails { get; }

    /// <summary>Two-letter contractions (lower-case ASCII pair → one head, no tail), matched case-insensitively —
    /// SPANISH <c>ch</c> and <c>ll</c>. Empty for GENERAL.</summary>
    public (byte First, byte Second, byte Head)[] Contractions { get; }

    /// <summary>The code page whose bytes index <see cref="Head"/>/<see cref="Tail"/>.</summary>
    public int CodePage { get; }

    /// <summary>char → code-page byte for every UTF-16 code unit; unrepresentable chars map to '?' (0x3F).</summary>
    public byte[] CharToByte { get; }

    private CollationTable(int codePage, byte[] head, byte[] tail, byte[][] expansions, bool expansionTails,
        (byte, byte, byte)[] contractions, byte[] charToByte)
    {
        CodePage = codePage;
        Head = head;
        Tail = tail;
        Expansions = expansions;
        ExpansionTails = expansionTails;
        Contractions = contractions;
        CharToByte = charToByte;
    }

    private static readonly Lazy<byte[]> Map1252 = new(() =>
    {
        var map = new byte[0x10000];
        for (int c = 0; c < map.Length; c++) map[c] = Cp1252.ToByte((char)c);
        // The five bytes CP1252 leaves undefined decode (.NET code-page encoding) to the C1 controls of the same
        // value; weight them as those bytes (VFP9: 0x10), not as the '?' Cp1252.ToByte substitutes.
        foreach (int b in new[] { 0x81, 0x8D, 0x8F, 0x90, 0x9D }) map[b] = (byte)b;
        return map;
    });

    private static readonly Lazy<byte[]> Map850 = new(() => HighCharMap(GeneralWeights850.HighChars));
    private static readonly Lazy<byte[]> Map437 = new(() => HighCharMap(GeneralWeights437.HighChars));
    private static readonly Lazy<byte[]> Map1250 = new(() => HighCharMap(GeneralWeights1250.HighChars));

    /// <summary>char → byte for an ASCII-based code page given its 128 high characters; anything else → '?'.</summary>
    private static byte[] HighCharMap(char[] high)
    {
        var map = new byte[0x10000];
        map.AsSpan().Fill((byte)'?');
        for (int c = 0; c < 0x80; c++) map[c] = (byte)c;
        for (int i = 0; i < high.Length; i++) map[high[i]] = (byte)(0x80 + i);
        return map;
    }

    private static readonly (byte, byte, byte)[] NoContractions = Array.Empty<(byte, byte, byte)>();

    /// <summary>SPANISH treats <c>ch</c> and <c>ll</c> as letters between c/d (head 99) and l/m (head 110).</summary>
    private static readonly (byte, byte, byte)[] SpanishContractions = { ((byte)'c', (byte)'h', 99), ((byte)'l', (byte)'l', 110) };

    /// <summary>GENERAL, CP1252 — the verbatim <see cref="GeneralWeights1252"/> table.</summary>
    public static CollationTable General1252 { get; } = new(1252, GeneralWeights1252.Head, GeneralWeights1252.Tail,
        GeneralWeights1252.Expansions, expansionTails: true, NoContractions, Map1252.Value);

    /// <summary>GENERAL, CP850 — <see cref="GeneralWeights850"/> plus VFP9's <c>þ</c>/<c>Þ</c> → <c>TH</c>.</summary>
    public static CollationTable General850 { get; } = BuildGeneral850();

    /// <summary>GENERAL, CP437 — the verbatim <see cref="GeneralWeights437"/> table (COLL4ARR, 0 deviations).</summary>
    public static CollationTable General437 { get; } = new(437, GeneralWeights437.Head, GeneralWeights437.Tail,
        GeneralWeights1252.Expansions, expansionTails: true, NoContractions, Map437.Value);

    /// <summary>GENERAL, CP1250 — <see cref="GeneralWeights1250"/>, derived from VFP9's own keys.</summary>
    public static CollationTable General1250 { get; } = new(1250, GeneralWeights1250.Head, GeneralWeights1250.Tail,
        GeneralWeights1250.Expansions, expansionTails: true, NoContractions, Map1250.Value);

    /// <summary>
    /// SPANISH, CP1252: VFP9 uses the CP1252 GENERAL weights with <c>ñ</c>/<c>Ñ</c> as their own letter (head 113,
    /// no tail), <c>ch</c>/<c>ll</c> contractions, tail-less expansions and NO-BREAK SPACE (0xA0) weighted as a
    /// space (17). CodeBase's <c>spanishCp1252CollationArray</c> is a different, one-byte sequence that VFP9 does
    /// NOT produce, so it is not used.
    /// </summary>
    public static CollationTable Spanish1252 { get; } = BuildSpanish1252();

    /// <summary>
    /// SPANISH, CP850: the raw CP850 GENERAL weights (no <c>þ</c> patch) with <c>ñ</c>/<c>Ñ</c> head 113, the
    /// umlauts as accented vowels (<c>ä</c> = A + tail 4, not AE), <c>þ</c>/<c>Þ</c> head 127, <c>¸</c>/<c>°</c>
    /// (0xF7/0xF8) expanding to <c>TH</c>, the contractions and tail-less expansions — exactly as VFP9 stores it.
    /// </summary>
    public static CollationTable Spanish850 { get; } = BuildSpanish850();

    private static CollationTable BuildGeneral850()
    {
        var head = (byte[])GeneralWeights850.Head.Clone();
        var tail = (byte[])GeneralWeights850.Tail.Clone();
        var exp = AppendTh(GeneralWeights850.Expansions, out byte th);
        foreach (int b in new[] { 0xE7, 0xE8 }) { head[b] = GeneralWeights1252.Expand; tail[b] = th; }
        return new CollationTable(850, head, tail, exp, expansionTails: true, NoContractions, Map850.Value);
    }

    private static CollationTable BuildSpanish1252()
    {
        var head = (byte[])GeneralWeights1252.Head.Clone();
        var tail = (byte[])GeneralWeights1252.Tail.Clone();
        head[0xA0] = 17;
        foreach (int b in new[] { 0xD1, 0xF1 }) { head[b] = 113; tail[b] = GeneralWeights1252.NoTail; }
        return new CollationTable(1252, head, tail, GeneralWeights1252.Expansions, expansionTails: false,
            SpanishContractions, Map1252.Value);
    }

    private static CollationTable BuildSpanish850()
    {
        var head = (byte[])GeneralWeights850.Head.Clone();
        var tail = (byte[])GeneralWeights850.Tail.Clone();
        void Set(int b, byte h, byte t) { head[b] = h; tail[b] = t; }
        Set(0x84, 96, 4); Set(0x8E, 96, 4);     // ä Ä
        Set(0x94, 114, 4); Set(0x99, 114, 4);   // ö Ö
        Set(0x81, 120, 4); Set(0x9A, 120, 4);   // ü Ü
        Set(0xA4, 113, GeneralWeights1252.NoTail); Set(0xA5, 113, GeneralWeights1252.NoTail);  // ñ Ñ
        Set(0xE7, 127, GeneralWeights1252.NoTail); Set(0xE8, 127, GeneralWeights1252.NoTail);  // þ Þ
        var exp = AppendTh(GeneralWeights850.Expansions, out byte th);
        Set(0xF7, GeneralWeights1252.Expand, th); Set(0xF8, GeneralWeights1252.Expand, th);    // ¸ °
        return new CollationTable(850, head, tail, exp, expansionTails: false, SpanishContractions, Map850.Value);
    }

    private static byte[][] AppendTh(byte[][] expansions, out byte index)
    {
        var exp = new byte[expansions.Length + 1][];
        expansions.CopyTo(exp, 0);
        index = (byte)expansions.Length;
        exp[index] = new byte[] { (byte)'T', (byte)'H' };
        return exp;
    }
}
