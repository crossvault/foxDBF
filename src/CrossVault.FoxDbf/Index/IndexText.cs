using System.Text;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// Internal byte-decoding helpers shared by the CDX/IDX header parsers
/// (plan §C2). Index expression text is stored uncompiled in the TABLE's code page.
/// </summary>
internal static class IndexText
{
    /// <summary>
    /// Reads a NUL-terminated string from <paramref name="span"/> starting at <paramref name="start"/>,
    /// scanning at most <paramref name="maxLength"/> bytes, decoded with <paramref name="encoding"/> (the
    /// table code page; null = the library default). Returns the empty string when out of range or empty.
    /// </summary>
    public static string ReadZ(ReadOnlySpan<byte> span, int start, int maxLength, Encoding? encoding)
    {
        if (start < 0 || start >= span.Length || maxLength <= 0)
            return string.Empty;

        int available = Math.Min(maxLength, span.Length - start);
        var region = span.Slice(start, available);

        int nul = region.IndexOf((byte)0);
        if (nul >= 0)
            region = region[..nul];

        return CodePageText.Decode(region, encoding);
    }

    /// <summary>
    /// Reads the 8-byte collation name at @494. An all-zero (or empty) field
    /// means the MACHINE collation.
    /// </summary>
    public static string ReadCollation(ReadOnlySpan<byte> eightBytes)
    {
        int nul = eightBytes.IndexOf((byte)0);
        var region = nul >= 0 ? eightBytes[..nul] : eightBytes;
        region = region.TrimEnd((byte)0x20);
        return region.IsEmpty ? "MACHINE" : Encoding.ASCII.GetString(region);
    }
}
