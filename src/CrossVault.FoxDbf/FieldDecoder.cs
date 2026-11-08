using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// Decodes a single field's raw bytes into a .NET value per plan §A5. One
/// <c>switch</c> dispatch on the column's type char replaces the Ruby gem's
/// one-class-per-type hierarchy (ponytail): <c>C→string</c>, <c>N→long|decimal</c>,
/// <c>F→double</c>, <c>I→int</c>, <c>Y→decimal</c>, <c>B→double</c>, <c>D→DateOnly</c>,
/// <c>T/@→DateTime</c>, <c>L→bool?</c>, <c>+→int</c>, and raw/placeholder for the rest.
/// </summary>
/// <remarks>
/// Never throws on bad <em>data</em> (§A10): malformed numerics/dates degrade to
/// <see langword="null"/>, never an exception. <c>Length == 0</c> ⇒ always
/// <see langword="null"/>; an unmapped type char falls back to a decoded string.
/// </remarks>
public static class FieldDecoder
{
    // Numeric / date / logical fields are stored as ASCII text; Latin1 is an
    // ASCII-superset single-byte decode that never fails, keeping §A10's never-throw
    // contract independent of the (character) table encoding.
    private static readonly Encoding AsciiText = Encoding.Latin1;

    // Ruby String#strip's whitespace set: NUL, tab, LF, VT, FF, CR, space. .NET's
    // string.Trim() omits NUL (U+0000), so 'C' fields are trimmed against this exact
    // set to match the ruby strip oracle and spec §A5 (leading+trailing, incl. NUL pad).
    private static readonly char[] StripChars = ['\0', '\t', '\n', '\v', '\f', '\r', ' '];

    // The Julian Day Number of 0001-01-01 (proleptic Gregorian), i.e. the offset
    // between FoxPro 'T' day numbers and .NET DayNumber (0 == 0001-01-01).
    private const int JdnEpoch = 1_721_426;

    /// <summary>
    /// Decode <paramref name="raw"/> (the field's bytes, deletion flag already
    /// stripped) for <paramref name="column"/> using <paramref name="encoding"/>
    /// for character data (§A5).
    /// </summary>
    public static object? Decode(DbfColumn column, ReadOnlySpan<byte> raw, Encoding encoding, bool suppressUtf8Detection = true)
    {
        // §A5 dispatch rule: a zero-length field is always null.
        if (raw.IsEmpty)
            return null;

        switch (column.Type)
        {
            case 'C':
                // Character: strip leading/trailing whitespace AND NUL padding to
                // match spec §A5 (Trim()) and the ruby String#strip oracle.
                return DecodeCharacter(column, raw, encoding, suppressUtf8Detection).Trim(StripChars);

            case 'N':
                return DecodeNumeric(raw, column.Decimal);

            case 'F':
                return DecodeFloat(raw);

            case 'I':
                return raw.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(raw) : null;

            case 'Y':
                // Currency: signed int64 LE scaled by 10000 → exact decimal.
                return raw.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(raw) / 10000m : null;

            case 'B':
                return raw.Length >= 8 ? BinaryPrimitives.ReadDoubleLittleEndian(raw) : (object?)null;

            case 'D':
                return DecodeDate(raw);

            case 'T':
            case '@':
                return DecodeDateTime(raw);

            case 'L':
                return DecodeLogical(raw[0]);

            case '+':
                return DecodeAutoIncrement(raw);

            // Memo / varchar / varbinary / blob / general: raw bytes placeholder for
            // later phases (A6/A5b). Binary, never transcoded.
            case 'M':
            case 'V':
            case 'Q':
            case 'W':
            case 'G':
                return raw.ToArray();

            // Type '0' (_NullFlags) and any unmapped type → decoded string fallback.
            // Strip whitespace/NUL here (NOT at the CSV layer) so an all-NUL _NullFlags
            // bitmap collapses to "" exactly as the Ruby golden master expects, while text
            // memo/Varchar values (decoded elsewhere) keep their leading/trailing layout.
            default:
                return encoding.GetString(raw).Trim(StripChars);
        }
    }

    /// <summary>
    /// A character ('C') field at its full declared width, as VFP's expressions see it: leading and trailing blanks
    /// kept, a trailing NUL padding (written by non-VFP tools) read as blanks. Same code-page / UTF-8 detection as
    /// <see cref="Decode"/>.
    /// </summary>
    internal static string DecodeCharacterPadded(DbfColumn column, ReadOnlySpan<byte> raw, Encoding encoding, bool suppressUtf8Detection)
    {
        string s = DecodeCharacter(column, raw, encoding, suppressUtf8Detection);
        int end = s.Length;
        while (end > 0 && s[end - 1] == '\0') end--;
        return end == s.Length ? s : string.Concat(s.AsSpan(0, end), new string(' ', s.Length - end));
    }

    /// <summary>
    /// Decode a character field in its selected code page, as VFP9 does. UTF-8 detection
    /// is available only when explicitly enabled for non-VFP data; a forced encoding
    /// and NOCPTRANS fields always bypass detection.
    /// </summary>
    private static string DecodeCharacter(DbfColumn column, ReadOnlySpan<byte> raw, Encoding encoding, bool suppressUtf8Detection)
    {
        if (!suppressUtf8Detection && !column.IsBinary && System.Text.Unicode.Utf8.IsValid(raw))
            return Encoding.UTF8.GetString(raw);
        return encoding.GetString(raw);
    }

    private static object? DecodeNumeric(ReadOnlySpan<byte> raw, int decimals)
    {
        var s = AsciiText.GetString(raw).Trim();
        if (s.Length == 0)
            return null;

        var inv = CultureInfo.InvariantCulture;

        if (decimals <= 0)
        {
            if (long.TryParse(s, NumberStyles.Number, inv, out var l))
                return l;
            // Lenient: tolerate a stray decimal point / junk by truncating.
            if (decimal.TryParse(s, NumberStyles.Number, inv, out var d) &&
                d >= long.MinValue && d <= long.MaxValue)
                return (long)d;
            return null; // never throw (§A10)
        }

        if (decimal.TryParse(s, NumberStyles.Number, inv, out var dec))
            return dec;
        // Overflow guard: > 28 significant digits → decimal can't hold it; fall back
        // to double rather than throw (§A5 money decision / §A10).
        if (double.TryParse(s, NumberStyles.Float, inv, out var dbl))
            return dbl;
        return null;
    }

    private static object? DecodeFloat(ReadOnlySpan<byte> raw)
    {
        var s = AsciiText.GetString(raw).Trim();
        if (s.Length == 0)
            return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static object? DecodeDate(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8)
            return null;

        var s = AsciiText.GetString(raw[..8]);
        if (s.Trim().Length == 0)
            return null; // all-spaces sentinel → null

        // Must be exactly 8 ASCII digits.
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
                return null;
        }

        int year = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
        int month = int.Parse(s.AsSpan(4, 2), CultureInfo.InvariantCulture);
        int day = int.Parse(s.AsSpan(6, 2), CultureInfo.InvariantCulture);

        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // 00000000 / 99999999 / impossible dates → null
        }
    }

    private static object? DecodeDateTime(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8)
            return null;

        int days = BinaryPrimitives.ReadInt32LittleEndian(raw);
        int millis = BinaryPrimitives.ReadInt32LittleEndian(raw[4..]);
        if (days == 0)
            return null; // empty datetime sentinel

        int dayNumber = days - JdnEpoch;
        if ((uint)dayNumber > DateOnly.MaxValue.DayNumber)
            return null;

        var date = DateOnly.FromDayNumber(dayNumber);
        var result = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        // FoxPro stores ms-since-midnight, but the time component is whole-second
        // precise. The stored ms frequently carries a sub-second artefact (e.g. an
        // intended 10:00:00 is stored as 35_999_999 ms), so ROUND to the nearest
        // second to recover the intended value — this matches how the CDX index
        // double decodes the same datetime (see IndexKey.DecodeDateTime).
        long seconds = (long)Math.Round((uint)millis / 1000.0, MidpointRounding.AwayFromZero);
        // VFP9 has a one-millisecond asymmetry at day rollover: exactly 23:59:59.500
        // stays at 23:59:59, while .501 through the valid 24:00:00.000 sentinel roll
        // to next-day midnight. Guard DateTime's max date; corrupt millis retain the
        // established never-throw same-date-midnight degradation.
        if (seconds >= 86_400)
        {
            if (millis == 86_399_500)
                seconds = 86_399;
            else if (millis is >= 86_399_501 and <= 86_400_000)
                return date.DayNumber < DateOnly.MaxValue.DayNumber ? result.AddDays(1) : result;
            else
                return result;
        }
        return result.AddSeconds(seconds);
    }

    private static object? DecodeLogical(byte b) => b switch
    {
        (byte)'Y' or (byte)'y' or (byte)'T' or (byte)'t' => true,
        // §A5 line 206: L is true iff the byte is Y/y/T/t; every other byte — N/F/0,
        // 0x20 space (the common uninitialized value), '?', NUL, junk — degrades to
        // false, matching the ruby gem (Boolean#type_cast, blank_value=false). VFP NULL
        // semantics are preserved separately via _NullFlags in DbfRecord.DecodeColumn.
        _ => false,
    };

    private static object? DecodeAutoIncrement(ReadOnlySpan<byte> raw)
    {
        // §A5b correction (verified): VFP stores '+' (AutoIncrement) per-record as a
        // 4-byte LITTLE-endian int32 — physically identical to 'I'. ruby-dbf's
        // big-endian MSB-as-sign bitfield is a bug; go-dbase models it as int32 LE.
        return raw.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(raw) : null;
    }
}
