namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// The VFP <c>GENERAL</c> collation weight table for code page <b>850</b> (DOS Latin-1), mechanically
/// translated from the CodeBase reference <c>cp850generalCollationArray</c> /
/// <c>cp850generalCompressArray</c> in <c>ref/CodeBase-for-DBF/source/COLL4ARR.C</c>. Same head/tail format
/// as <see cref="GeneralWeights1252"/>, indexed by the CP850 byte.
/// </summary>
/// <remarks>
/// <para>
/// VFP9 differs from CP1252 GENERAL here: the umlauts expand to vowel+E (<c>Ä→AE</c>, <c>Ö→OE</c>,
/// <c>Ü→UE</c>) and every symbol, box-drawing and currency character collapses to the single weight
/// <c>0x10</c>.
/// </para>
/// <para>
/// One VFP9-verified deviation from COLL4ARR.C is applied by <see cref="CollationTable"/>, not here: COLL4ARR
/// gives <c>þ</c>/<c>Þ</c> (0xE7/0xE8) head 119 + tail 105, but VFP9 stores them as the two heads <c>T H</c>.
/// </para>
/// </remarks>
internal static class GeneralWeights850
{
    /// <summary>Primary (head) sort weight indexed by CP850 byte. <see cref="GeneralWeights1252.Expand"/> = ligature.</summary>
    public static readonly byte[] Head =
    {
         16,  16,  16,  16,  16,  16,  16,  16,  16,  17,  16,  16,  16,  16,  16,  16,
         16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,
         17,  18,  19,  20,  21,  22,  23,  24,  25,  26,  27,  28,  29,  30,  31,  32,
         86,  87,  88,  89,  90,  91,  92,  93,  94,  95,  33,  34,  35,  36,  37,  38,
         39,  96,  97,  98, 100, 102, 103, 104, 105, 106, 107, 108, 109, 111, 112, 114,
        115, 116, 117, 118, 119, 120, 122, 123, 124, 125, 126,  40,  41,  42,  43,  44,
         45,  96,  97,  98, 100, 102, 103, 104, 105, 106, 107, 108, 109, 111, 112, 114,
        115, 116, 117, 118, 119, 120, 122, 123, 124, 125, 126,  46,  47,  48,  49,  16,
         98, 255, 102,  96, 255,  96,  96,  98, 102, 102, 102, 106, 106, 106, 255,  96,
        102, 255, 255, 114, 255, 114, 120, 120, 125, 255, 255, 129,  16, 129,  16,  16,
         96, 106, 114, 120, 112, 112,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,
         16,  16,  16,  16,  16,  96,  96,  96,  67,  16,  16,  16,  16,  16,  16,  16,
         16,  16,  16,  16,  16,  16,  96,  96,  16,  16,  16,  16,  16,  16,  16,  16,
        101, 101, 102, 102, 102,  16, 106, 106, 106,  16,  16,  16,  16,  16, 106,  16,
        114, 255, 114, 114, 114, 114,  16, 119, 119, 120, 120, 120, 125, 125,  16,  16,
         16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,
    };

    /// <summary>Diacritic (tail) weight indexed by CP850 byte. <see cref="GeneralWeights1252.NoTail"/> = none; for an
    /// expansion slot this holds the index into <see cref="Expansions"/>.</summary>
    public static readonly byte[] Tail =
    {
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255,   0, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255,   0,   0,
        255, 255, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255, 255, 255,
        255,   0, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255,   0,   0,
        255, 255, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255, 255, 255,
          7,   3,   1,   3,   1,   2,   6,   7,   3,   4,   2,   4,   3,   2,   1,   6,
          1,   1,   1,   3,   0,   2,   3,   2,   4,   0,   3, 255, 255, 255, 255, 255,
          1,   1,   1,   1,   5,   5, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255,   1,   3,   2, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255,   5,   5, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255,   3,   4,   2, 255,   1,   3,   4, 255, 255, 255, 255, 255,   2, 255,
          1,   2,   3,   2,   5,   5, 255, 105, 105,   1,   3,   2,   1,   1, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
    };

    /// <summary>Ligature expansions (CP850 byte pairs) indexed by the <see cref="Tail"/> value of an expansion
    /// slot: 0=OE, 1=AE, 2=SS, 3=UE (cp850generalCompressArray; its "th" comment is wrong, the bytes are U E).</summary>
    public static readonly byte[][] Expansions =
    {
        new byte[] { 79, 69 }, // OE
        new byte[] { 65, 69 }, // AE
        new byte[] { 83, 83 }, // SS
        new byte[] { 85, 69 }, // UE
    };

    /// <summary>The Unicode characters of CP850 bytes 0x80-0xFF (bytes 0x00-0x7F are ASCII).</summary>
    public static readonly char[] HighChars =
    {
        '\u00C7', '\u00FC', '\u00E9', '\u00E2', '\u00E4', '\u00E0', '\u00E5', '\u00E7',
        '\u00EA', '\u00EB', '\u00E8', '\u00EF', '\u00EE', '\u00EC', '\u00C4', '\u00C5',
        '\u00C9', '\u00E6', '\u00C6', '\u00F4', '\u00F6', '\u00F2', '\u00FB', '\u00F9',
        '\u00FF', '\u00D6', '\u00DC', '\u00F8', '\u00A3', '\u00D8', '\u00D7', '\u0192',
        '\u00E1', '\u00ED', '\u00F3', '\u00FA', '\u00F1', '\u00D1', '\u00AA', '\u00BA',
        '\u00BF', '\u00AE', '\u00AC', '\u00BD', '\u00BC', '\u00A1', '\u00AB', '\u00BB',
        '\u2591', '\u2592', '\u2593', '\u2502', '\u2524', '\u00C1', '\u00C2', '\u00C0',
        '\u00A9', '\u2563', '\u2551', '\u2557', '\u255D', '\u00A2', '\u00A5', '\u2510',
        '\u2514', '\u2534', '\u252C', '\u251C', '\u2500', '\u253C', '\u00E3', '\u00C3',
        '\u255A', '\u2554', '\u2569', '\u2566', '\u2560', '\u2550', '\u256C', '\u00A4',
        '\u00F0', '\u00D0', '\u00CA', '\u00CB', '\u00C8', '\u0131', '\u00CD', '\u00CE',
        '\u00CF', '\u2518', '\u250C', '\u2588', '\u2584', '\u00A6', '\u00CC', '\u2580',
        '\u00D3', '\u00DF', '\u00D4', '\u00D2', '\u00F5', '\u00D5', '\u00B5', '\u00FE',
        '\u00DE', '\u00DA', '\u00DB', '\u00D9', '\u00FD', '\u00DD', '\u00AF', '\u00B4',
        '\u00AD', '\u00B1', '\u2017', '\u00BE', '\u00B6', '\u00A7', '\u00F7', '\u00B8',
        '\u00B0', '\u00A8', '\u00B7', '\u00B9', '\u00B3', '\u00B2', '\u25A0', '\u00A0',
    };
}
