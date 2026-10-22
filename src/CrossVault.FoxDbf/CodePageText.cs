using System.Collections.Concurrent;
using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// Name and expression text that VFP stores as raw bytes in the TABLE's code page: field names in the
/// <c>.dbf</c> descriptors, tag names and KEY/FOR expressions in <c>.cdx</c>/<c>.idx</c> headers. VFP9
/// writes <c>RAZÓNSOC</c> with 'Ó' as 0xD3 in a CP1252 table and as 0xE0 in a CP850 table, and the tag
/// key <c>razónsoc</c> with 'ó' as 0xF3 / 0xA2 — the table code page, not the session's CPCURRENT()
/// (VFP9 probe 2026-10-22, fixtures in vfp_test/uninames). Encoding here never substitutes: a
/// character the code page cannot hold is reported instead of silently becoming '?'.
/// </summary>
internal static class CodePageText
{
    private static readonly ConcurrentDictionary<int, Encoding> StrictByCodePage = new();

    /// <summary>The encoding index text is decoded with when no table is known (the §A7 default code page).</summary>
    public static Encoding DefaultEncoding => Encodings.ResolveEncoding(0);

    /// <summary>Decode <paramref name="bytes"/> with <paramref name="encoding"/>, or the default code page when null.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, Encoding? encoding)
        => bytes.IsEmpty ? string.Empty : (encoding ?? DefaultEncoding).GetString(bytes);

    /// <summary>
    /// Encode <paramref name="text"/> in <paramref name="encoding"/> without any replacement or best-fit
    /// substitution. Returns false (with the first offending character in <paramref name="unmappable"/>)
    /// when the code page cannot represent the text exactly.
    /// </summary>
    public static bool TryEncode(string text, Encoding encoding, out byte[] bytes, out string? unmappable)
    {
        var strict = Strict(encoding);
        try
        {
            bytes = strict.GetBytes(text);
            if (strict.GetString(bytes) == text)
            {
                unmappable = null;
                return true;
            }
        }
        catch (EncoderFallbackException) { }
        catch (DecoderFallbackException) { }

        bytes = Array.Empty<byte>();
        unmappable = FirstUnmappable(text, strict);
        return false;
    }

    /// <summary>
    /// <see cref="TryEncode"/> or throw the exception built by <paramref name="error"/> from a message naming
    /// <paramref name="what"/>, the offending character and the code page.
    /// </summary>
    public static byte[] Encode(string text, Encoding encoding, string what, Func<string, Exception> error)
    {
        if (TryEncode(text, encoding, out var bytes, out var bad))
            return bytes;
        throw error($"{what} '{text}' contains {Describe(bad)}, which code page {encoding.CodePage} cannot store; " +
                    "refusing to write it as a substitute character.");
    }

    /// <summary>
    /// Upper-case <paramref name="text"/> the way VFP does for a name stored in <paramref name="encoding"/>: each
    /// letter whose upper case the code page can hold is mapped, any other is left as is. VFP9 in CP1252:
    /// UPPER('ó') = 'Ó', UPPER('ÿ') = 'Ÿ', but UPPER('ƒ') = 'ƒ' and UPPER('µ') = 'µ' — .NET's 'Ƒ' / 'Μ' do not exist
    /// in CP1252 (probe 2026-10-22).
    /// </summary>
    public static string ToUpperInCodePage(string text, Encoding encoding)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            char u = char.ToUpperInvariant(c);
            if (u != c && (c < 0x80 || TryEncode(u.ToString(), encoding, out _, out _)))
                chars[i] = u;
        }
        return new string(chars);
    }

    private static Encoding Strict(Encoding encoding)
        => StrictByCodePage.GetOrAdd(encoding.CodePage, static (_, enc) =>
        {
            var e = (Encoding)enc.Clone();
            e.EncoderFallback = EncoderFallback.ExceptionFallback;
            e.DecoderFallback = DecoderFallback.ExceptionFallback;
            return e;
        }, encoding);

    private static string? FirstUnmappable(string text, Encoding strict)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            string s = rune.ToString();
            try
            {
                if (strict.GetString(strict.GetBytes(s)) != s)
                    return s;
            }
            catch (EncoderFallbackException) { return s; }
            catch (DecoderFallbackException) { return s; }
        }
        return null;
    }

    private static string Describe(string? s)
        => s is null ? "a character" : $"'{s}' (U+{char.ConvertToUtf32(s, 0):X4})";
}
