using System.Text;

namespace CrossVault.FoxDbf;

// VFP9 STRCONV has no Unicode table for 620/895 (error 1914). By explicit policy,
// use common Mazovia / Kamenicky Unicode tables, except where CPCONVERT to both
// 1250 and 852 agrees AND both conversions round-trip to the original byte.
// This overrides 620:A8 -> U+00A7; 895:B6 -> U+00C2, DD -> U+0162.
// Disagreeing targets are best-fit evidence, not Unicode definitions. See
// vfp_test/codepages/README.md and the per-byte decision CSVs for sources/evidence.
internal sealed class LegacyDosEncoding : Encoding
{
    internal static readonly Encoding Mazovia = new LegacyDosEncoding(620, "Mazovia",
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000a\u000b\u000c\u000d\u000e\u000f" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f" +
        "\u0020\u0021\u0022\u0023\u0024\u0025\u0026\u0027\u0028\u0029\u002a\u002b\u002c\u002d\u002e\u002f" +
        "\u0030\u0031\u0032\u0033\u0034\u0035\u0036\u0037\u0038\u0039\u003a\u003b\u003c\u003d\u003e\u003f" +
        "\u0040\u0041\u0042\u0043\u0044\u0045\u0046\u0047\u0048\u0049\u004a\u004b\u004c\u004d\u004e\u004f" +
        "\u0050\u0051\u0052\u0053\u0054\u0055\u0056\u0057\u0058\u0059\u005a\u005b\u005c\u005d\u005e\u005f" +
        "\u0060\u0061\u0062\u0063\u0064\u0065\u0066\u0067\u0068\u0069\u006a\u006b\u006c\u006d\u006e\u006f" +
        "\u0070\u0071\u0072\u0073\u0074\u0075\u0076\u0077\u0078\u0079\u007a\u007b\u007c\u007d\u007e\u007f" +
        "\u00c7\u00fc\u00e9\u00e2\u00e4\u00e0\u0105\u00e7\u00ea\u00eb\u00e8\u00ef\u00ee\u0107\u00c4\u0104" +
        "\u0118\u0119\u0142\u00f4\u00f6\u0106\u00fb\u00f9\u015a\u00d6\u00dc\u00a2\u0141\u00a5\u015b\u0192" +
        "\u0179\u017b\u00f3\u00d3\u0144\u0143\u017a\u017c\u00a7\u2310\u00ac\u00bd\u00bc\u00a1\u00ab\u00bb" +
        "\u2591\u2592\u2593\u2502\u2524\u2561\u2562\u2556\u2555\u2563\u2551\u2557\u255d\u255c\u255b\u2510" +
        "\u2514\u2534\u252c\u251c\u2500\u253c\u255e\u255f\u255a\u2554\u2569\u2566\u2560\u2550\u256c\u2567" +
        "\u2568\u2564\u2565\u2559\u2558\u2552\u2553\u256b\u256a\u2518\u250c\u2588\u2584\u258c\u2590\u2580" +
        "\u03b1\u00df\u0393\u03c0\u03a3\u03c3\u00b5\u03c4\u03a6\u0398\u03a9\u03b4\u221e\u03c6\u03b5\u2229" +
        "\u2261\u00b1\u2265\u2264\u2320\u2321\u00f7\u2248\u00b0\u2219\u00b7\u221a\u207f\u00b2\u25a0\u00a0");

    internal static readonly Encoding Kamenicky = new LegacyDosEncoding(895, "Kamenicky",
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000a\u000b\u000c\u000d\u000e\u000f" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f" +
        "\u0020\u0021\u0022\u0023\u0024\u0025\u0026\u0027\u0028\u0029\u002a\u002b\u002c\u002d\u002e\u002f" +
        "\u0030\u0031\u0032\u0033\u0034\u0035\u0036\u0037\u0038\u0039\u003a\u003b\u003c\u003d\u003e\u003f" +
        "\u0040\u0041\u0042\u0043\u0044\u0045\u0046\u0047\u0048\u0049\u004a\u004b\u004c\u004d\u004e\u004f" +
        "\u0050\u0051\u0052\u0053\u0054\u0055\u0056\u0057\u0058\u0059\u005a\u005b\u005c\u005d\u005e\u005f" +
        "\u0060\u0061\u0062\u0063\u0064\u0065\u0066\u0067\u0068\u0069\u006a\u006b\u006c\u006d\u006e\u006f" +
        "\u0070\u0071\u0072\u0073\u0074\u0075\u0076\u0077\u0078\u0079\u007a\u007b\u007c\u007d\u007e\u007f" +
        "\u010c\u00fc\u00e9\u010f\u00e4\u010e\u0164\u010d\u011b\u011a\u0139\u00cd\u013e\u013a\u00c4\u00c1" +
        "\u00c9\u017e\u017d\u00f4\u00f6\u00d3\u016f\u00da\u00fd\u00d6\u00dc\u0160\u013d\u00dd\u0158\u0165" +
        "\u00e1\u00ed\u00f3\u00fa\u0148\u0147\u016e\u00d4\u0161\u0159\u0155\u0154\u00bc\u00a7\u00ab\u00bb" +
        "\u2591\u2592\u2593\u2502\u2524\u2561\u00c2\u2556\u2555\u2563\u2551\u2557\u255d\u255c\u255b\u2510" +
        "\u2514\u2534\u252c\u251c\u2500\u253c\u255e\u255f\u255a\u2554\u2569\u2566\u2560\u2550\u256c\u2567" +
        "\u2568\u2564\u2565\u2559\u2558\u2552\u2553\u256b\u256a\u2518\u250c\u2588\u2584\u0162\u2590\u2580" +
        "\u03b1\u00df\u0393\u03c0\u03a3\u03c3\u00b5\u03c4\u03a6\u0398\u03a9\u03b4\u221e\u03c6\u03b5\u2229" +
        "\u2261\u00b1\u2265\u2264\u2320\u2321\u00f7\u2248\u00b0\u2219\u00b7\u221a\u207f\u00b2\u25a0\u00a0");

    private readonly string _characters;
    private readonly Dictionary<char, byte> _bytes;
    private readonly string _name;

    private LegacyDosEncoding(int codePage, string name, string characters)
        : base(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback)
    {
        _name = name;
        _characters = characters;
        _bytes = new Dictionary<char, byte>(256);
        // Every byte has a distinct character, including controls: never lose an
        // undefined position to '?'. These two tables have no undefined positions.
        for (int i = 0; i < 256; i++) _bytes.Add(characters[i], (byte)i);
    }

    public override string EncodingName => _name;
    public override string WebName => $"cp{CodePage}";
    public override bool IsSingleByte => true;

    public override int GetByteCount(char[] chars, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(chars);
        return GetByteCount(chars.AsSpan(index, count));
    }

    public override int GetByteCount(ReadOnlySpan<char> chars)
        => Encode(chars, Span<byte>.Empty, countOnly: true);

    public override int GetBytes(char[] chars, int charIndex, int charCount,
        byte[] bytes, int byteIndex)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(bytes);
        return GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));
    }

    public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        if (bytes.Length < GetByteCount(chars))
            throw new ArgumentException("The output buffer is too small.", nameof(bytes));
        return Encode(chars, bytes, countOnly: false);
    }

    private int Encode(ReadOnlySpan<char> chars, Span<byte> bytes, bool countOnly)
    {
        int written = 0;
        EncoderFallbackBuffer? fallback = null;
        for (int i = 0; i < chars.Length; i++)
        {
            if (_bytes.TryGetValue(chars[i], out byte value))
            {
                if (!countOnly) bytes[written] = value;
                written = checked(written + 1);
                continue;
            }

            fallback ??= EncoderFallback.CreateFallbackBuffer();
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                fallback.Fallback(chars[i], chars[i + 1], i);
                i++;
            }
            else
            {
                fallback.Fallback(chars[i], i);
            }
            while (fallback.Remaining > 0)
            {
                if (!_bytes.TryGetValue(fallback.GetNextChar(), out value))
                    throw new EncoderFallbackException("The fallback character is not representable in this code page.");
                if (!countOnly) bytes[written] = value;
                written = checked(written + 1);
            }
        }
        return written;
    }

    public override int GetCharCount(byte[] bytes, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return GetCharCount(bytes.AsSpan(index, count));
    }

    public override int GetCharCount(ReadOnlySpan<byte> bytes) => bytes.Length;

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount,
        char[] chars, int charIndex)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(chars);
        return GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex));
    }

    public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        if (chars.Length < bytes.Length)
            throw new ArgumentException("The output buffer is too small.", nameof(chars));
        for (int i = 0; i < bytes.Length; i++) chars[i] = _characters[bytes[i]];
        return bytes.Length;
    }

    public override int GetMaxByteCount(int charCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charCount);
        long maximum = ((long)charCount + 1) * Math.Max(1, EncoderFallback.MaxCharCount);
        if (maximum > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(charCount));
        return (int)maximum;
    }

    public override int GetMaxCharCount(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        return byteCount;
    }
}
