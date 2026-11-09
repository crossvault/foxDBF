using System.Runtime.CompilerServices;
using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// Maps the DBF code-page / language-driver byte (header offset 29) to a numeric
/// Windows/IBM code page (plan §A7), and resolves it to a concrete
/// <see cref="System.Text.Encoding"/>. Includes the VFP9 identifiers and legacy
/// dBase mappings, with a <see cref="RegisterCodePage"/> hook for custom drivers.
/// </summary>
public static class Encodings
{
    /// <summary>Default fallback code page when the header byte is unknown (§A7: CP1252).</summary>
    public const int DefaultCodePage = 1252;

    // VFP9 identifiers plus the legacy ruby-dbf/dBase aliases.
    private static readonly Dictionary<byte, int> Map = new()
    {
        [0x01] = 437,  // U.S. MS-DOS
        [0x02] = 850,  // International MS-DOS
        [0x03] = 1252, // Windows ANSI
        [0x04] = 10000, // Macintosh
        [0x08] = 865,  // Danish OEM
        [0x09] = 437,  // Dutch OEM
        [0x0a] = 850,  // Dutch OEM*
        [0x0b] = 437,  // Finnish OEM
        [0x0d] = 437,  // French OEM
        [0x0e] = 850,  // French OEM*
        [0x0f] = 437,  // German OEM
        [0x10] = 850,  // German OEM*
        [0x11] = 437,  // Italian OEM
        [0x12] = 850,  // Italian OEM*
        [0x13] = 932,  // Japanese Shift-JIS
        [0x14] = 850,  // Spanish OEM*
        [0x15] = 437,  // Swedish OEM
        [0x16] = 850,  // Swedish OEM*
        [0x17] = 865,  // Norwegian OEM
        [0x18] = 437,  // Spanish OEM
        [0x19] = 437,  // English OEM (Britain)
        [0x1a] = 850,  // English OEM (Britain)*
        [0x1b] = 437,  // English OEM (U.S.)
        [0x1c] = 863,  // French OEM (Canada)
        [0x1d] = 850,  // French OEM*
        [0x1f] = 852,  // Czech OEM
        [0x22] = 852,  // Hungarian OEM
        [0x23] = 852,  // Polish OEM
        [0x24] = 860,  // Portuguese OEM
        [0x25] = 850,  // Portuguese OEM*
        [0x26] = 866,  // Russian OEM
        [0x37] = 850,  // English OEM (U.S.)*
        [0x40] = 852,  // Romanian OEM
        [0x4d] = 936,  // Chinese GBK (PRC)
        [0x4e] = 949,  // Korean (ANSI/OEM)
        [0x4f] = 950,  // Chinese Big5 (Taiwan)
        [0x50] = 874,  // Thai (ANSI/OEM)
        [0x57] = 1252, // ANSI
        [0x58] = 1252, // Western European ANSI
        [0x59] = 1252, // Spanish ANSI
        [0x64] = 852,  // Eastern European MS-DOS
        [0x65] = 866,  // Russian MS-DOS
        [0x66] = 865,  // Nordic MS-DOS
        [0x67] = 861,  // Icelandic MS-DOS
        [0x68] = 895,  // Kamenicky Czech MS-DOS
        [0x69] = 620,  // Mazovia Polish MS-DOS
        [0x6a] = 737,  // Greek MS-DOS (437G)
        [0x6b] = 857,  // Turkish MS-DOS
        [0x6c] = 863,  // French-Canadian MS-DOS
        [0x78] = 950,  // Taiwan Big 5
        [0x79] = 949,  // Hangul (Wansung)
        [0x7a] = 936,  // PRC GBK
        [0x7b] = 932,  // Japanese Shift-JIS
        [0x7c] = 874,  // Thai Windows/MS-DOS
        [0x7d] = 1255, // Hebrew Windows
        [0x7e] = 1256, // Arabic Windows
        [0x86] = 737,  // Greek OEM
        [0x87] = 852,  // Slovenian OEM
        [0x88] = 857,  // Turkish OEM
        [0x96] = 10007, // Macintosh Cyrillic
        [0x97] = 10029, // Macintosh Central European
        [0x98] = 10006, // Macintosh Greek
        [0xc8] = 1250, // Eastern European Windows
        [0xc9] = 1251, // Russian Windows
        [0xca] = 1254, // Turkish Windows
        [0xcb] = 1253, // Greek Windows
        [0xcc] = 1257, // Baltic Windows
    };

    // Guards concurrent reads/writes of the (rarely mutated) custom mappings.
    private static readonly object Gate = new();

    /// <summary>
    /// Register the CodePages provider once at assembly load so legacy single-byte/DBCS
    /// code pages (CP1252, CP850, CP866, Shift-JIS …) are resolvable on every platform
    /// (§A7). A module initializer (rather than a static ctor) makes registration
    /// deterministic and independent of which member is touched first.
    /// </summary>
#pragma warning disable CA2255 // Module-init is the §A7-mandated one-time provider registration.
    [ModuleInitializer]
    internal static void Initialize()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#pragma warning restore CA2255

    /// <summary>
    /// Resolve a header code-page byte to a concrete <see cref="Encoding"/> (§A7):
    /// header code page → <paramref name="fallback"/> (default CP1252). The returned
    /// encoding is configured with replacement fallbacks (mirrors the ruby
    /// <c>undef/invalid: :replace</c> behaviour). Never throws.
    /// </summary>
    public static Encoding ResolveEncoding(byte codePageByte, Encoding? fallback = null)
    {
        if (ResolveCodePage(codePageByte) is int cp && TryGetEncoding(cp) is { } enc)
            return enc;

        if (fallback is not null)
            return fallback;

        return TryGetEncoding(DefaultCodePage) ?? Encoding.Latin1;
    }

    /// <summary>
    /// Register (or override) a custom mapping from a header code-page byte to a numeric
    /// .NET/IBM code page (plan §A7 <c>RegisterCodePage</c> hook). After registration
    /// <see cref="ResolveCodePage"/> / <see cref="ResolveEncoding"/> honour the custom entry.
    /// </summary>
    public static void RegisterCodePage(byte codePageByte, int codePage)
    {
        lock (Gate)
            Map[codePageByte] = codePage;
    }

    /// <summary>
    /// Resolve a code-page byte to its numeric code page (e.g. 0x03 → 1252),
    /// or null when the byte is unknown / unmapped (e.g. 0x00).
    /// </summary>
    public static int? ResolveCodePage(byte codePageByte)
    {
        lock (Gate)
            return Map.TryGetValue(codePageByte, out var cp) ? cp : null;
    }

    internal static byte MarkForCodePage(int codePage)
    {
        lock (Gate)
            foreach (var pair in Map)
                if (pair.Value == codePage) return pair.Key;
        throw new NotSupportedException($"No DBF language-driver mark for code page {codePage}.");
    }

    /// <summary>The usable header code page, or zero for an unknown/unavailable driver.
    /// Unlike the decoding fallback, this is the value reported by VFP9 CPDBF().</summary>
    public static int ResolveAvailableCodePage(byte codePageByte)
        => ResolveCodePage(codePageByte) is int cp && TryGetEncoding(cp) is not null ? cp : 0;

    // GetEncoding with replacement fallbacks (§A7: undef/invalid → :replace), null on
    // an unknown/unsupported code page so callers degrade to a fallback (never throw).
    private static Encoding? TryGetEncoding(int codePage)
    {
        if (codePage == 620) return LegacyDosEncoding.Mazovia;
        if (codePage == 895) return LegacyDosEncoding.Kamenicky;

        try
        {
            return Encoding.GetEncoding(
                codePage,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
