namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// The VFP <c>GENERAL</c> collation weight table for code page <b>1250</b> (Windows Central European). CodeBase has
/// no GENERAL table for 1250 (<c>cp1250croatianCollationArray</c> is a different, one-byte sequence), so this
/// table is DERIVED from VFP9's own CDX keys: probe 2026-10-22 indexed all 255 non-zero bytes as <c>X+'a'</c> and
/// <c>X+'á'</c> (head, tail and expansion per byte read off the keys) and was then checked with 0 deviations
/// against keys it was not derived from: 600 random strings, all 65 025 byte pairs (no contractions) and
/// 18 472 letter triples.
/// </summary>
/// <remarks>
/// Its own letter order (<c>a</c> = 0x6C): <c>ą ć ę ł ń ó ś ź ż</c> are letters of their own, the other
/// accented letters (<c>á č ď ě ň ř š ť ů ž ...</c>) are base letter + tail. The only expansion is <c>ß→SS</c>;
/// the five undefined bytes (0x81 0x83 0x88 0x90 0x98) and the controls weigh 0x10.
/// </remarks>
internal static class GeneralWeights1250
{
    /// <summary>Primary (head) sort weight indexed by CP1250 byte. <see cref="GeneralWeights1252.Expand"/> = ligature.</summary>
    public static readonly byte[] Head =
    {
         16,  16,  16,  16,  16,  16,  16,  16,  16,  17,  18,  19,  20,  21,  16,  16,
         16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,
         22,  23,  24,  25,  26,  27,  28,  29,  30,  31,  32,  33,  34,  35,  36,  37,
         96,  97,  98,  99, 100, 101, 102, 103, 104, 105,  38,  39,  40,  41,  42,  43,
         44, 108, 110, 111, 115, 119, 121, 122, 124, 126, 127, 128, 129, 133, 134, 138,
        140, 141, 142, 143, 146, 148, 149, 150, 151, 152, 153,  45,  46,  47,  48,  49,
         50, 108, 110, 111, 115, 119, 121, 122, 124, 126, 127, 128, 129, 133, 134, 138,
        140, 141, 142, 143, 146, 148, 149, 150, 151, 152, 153,  51,  52,  53,  54,  16,
         16,  16,  55,  16,  56,  57,  58,  59,  16,  60, 143,  61, 144, 146, 153, 154,
         16,  62,  63,  64,  65,  66,  67,  68,  16,  69, 143,  70, 144, 146, 153, 154,
         71,  72,  73, 132,  74, 109,  75,  76,  77,  78, 143,  79,  80,  81,  82, 155,
         83,  84,  85, 132,  86,  87,  88,  89,  90, 109, 143,  91, 129,  92, 129, 155,
        142, 108, 108, 108, 108, 129, 112, 111, 111, 119, 120, 119, 119, 126, 126, 115,
        116, 135, 134, 139, 138, 138, 138,  93, 142, 148, 148, 148, 148, 152, 146, 255,
        142, 108, 108, 108, 108, 129, 112, 111, 111, 119, 120, 119, 119, 126, 126, 115,
        116, 135, 134, 139, 138, 138, 138,  94, 142, 148, 148, 148, 148, 152, 146,  95,
    };

    /// <summary>Diacritic (tail) weight indexed by CP1250 byte. <see cref="GeneralWeights1252.NoTail"/> = none; for an
    /// expansion slot this holds the index into <see cref="Expansions"/>.</summary>
    public static readonly byte[] Tail =
    {
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255,   0, 255,   0,   0,   0, 255, 255, 255,   0, 255, 255,   0, 255,   0,   0,
        255, 255,   0,   0,   0,   0, 255, 255, 255,   0,   0, 255, 255, 255, 255, 255,
        255,   0, 255,   0,   0,   0, 255, 255, 255,   0, 255, 255,   0, 255,   0,   0,
        255, 255,   0,   0,   0,   0, 255, 255, 255,   0,   0, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255,   1, 255, 255,   1,   1, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255,   1, 255, 255,   1,   1, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255,   2, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255,   2, 255,   2, 255,   2, 255,
          1,   1,   2,   4,   3,   1, 255,   2,   1,   1, 255,   2,   3,   1,   2,   1,
        255, 255,   1, 255,   1,   3,   2, 255,   2,   3,   1,   4,   2,   1,   2,   0,
          1,   1,   2,   4,   3,   1, 255,   2,   1,   1, 255,   2,   3,   1,   2,   1,
        255, 255,   1, 255,   1,   3,   2, 255,   2,   3,   1,   4,   2,   1,   2, 255,
    };

    /// <summary>Ligature expansions indexed by the <see cref="Tail"/> value of an expansion slot: 0=SS.</summary>
    public static readonly byte[][] Expansions =
    {
        new byte[] { 83, 83 }, // SS
    };

    /// <summary>The Unicode characters of CP1250 bytes 0x80-0xFF (bytes 0x00-0x7F are ASCII), as .NET's code-page
    /// encoding decodes them.</summary>
    public static readonly char[] HighChars =
    {
        '\u20AC', '\u0081', '\u201A', '\u0083', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u0088', '\u2030', '\u0160', '\u2039', '\u015A', '\u0164', '\u017D', '\u0179',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u0098', '\u2122', '\u0161', '\u203A', '\u015B', '\u0165', '\u017E', '\u017A',
        '\u00A0', '\u02C7', '\u02D8', '\u0141', '\u00A4', '\u0104', '\u00A6', '\u00A7',
        '\u00A8', '\u00A9', '\u015E', '\u00AB', '\u00AC', '\u00AD', '\u00AE', '\u017B',
        '\u00B0', '\u00B1', '\u02DB', '\u0142', '\u00B4', '\u00B5', '\u00B6', '\u00B7',
        '\u00B8', '\u0105', '\u015F', '\u00BB', '\u013D', '\u02DD', '\u013E', '\u017C',
        '\u0154', '\u00C1', '\u00C2', '\u0102', '\u00C4', '\u0139', '\u0106', '\u00C7',
        '\u010C', '\u00C9', '\u0118', '\u00CB', '\u011A', '\u00CD', '\u00CE', '\u010E',
        '\u0110', '\u0143', '\u0147', '\u00D3', '\u00D4', '\u0150', '\u00D6', '\u00D7',
        '\u0158', '\u016E', '\u00DA', '\u0170', '\u00DC', '\u00DD', '\u0162', '\u00DF',
        '\u0155', '\u00E1', '\u00E2', '\u0103', '\u00E4', '\u013A', '\u0107', '\u00E7',
        '\u010D', '\u00E9', '\u0119', '\u00EB', '\u011B', '\u00ED', '\u00EE', '\u010F',
        '\u0111', '\u0144', '\u0148', '\u00F3', '\u00F4', '\u0151', '\u00F6', '\u00F7',
        '\u0159', '\u016F', '\u00FA', '\u0171', '\u00FC', '\u00FD', '\u0163', '\u02D9',
    };
}
