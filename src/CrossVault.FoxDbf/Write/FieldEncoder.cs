using CrossVault.FoxDbf.Expressions;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Writes a single field's .NET value into its on-disk byte layout — the exact inverse
/// of <see cref="FieldDecoder"/> (plan §A5/§A5b/§D0). One <c>switch</c> on the column's
/// type char mirrors the decoder: <c>C</c> space-pad/truncate to the table code page,
/// <c>N</c>/<c>F</c> right-justified ASCII with <see cref="DbfColumn.Decimal"/> places,
/// <c>I</c>/<c>+</c> 4-byte LE int32, <c>Y</c> <c>round(v*10000)</c> int64 LE, <c>B</c>
/// IEEE-754 double LE, <c>D</c> <c>YYYYMMDD</c>, <c>T</c>/<c>@</c> two int32 LE (Julian
/// day + ms since midnight), <c>L</c> <c>T</c>/<c>F</c>, <c>V</c>/<c>Q</c> value + trailing
/// length byte, binary/<c>G</c> raw bytes, <c>M</c> a 0 pointer (memo write deferred to D1).
/// </summary>
/// <remarks>
/// N/F overflow writes <c>'*'</c> fill, as VFP9 SQL INSERT/UPDATE do. Integer and currency
/// overflow throw <see cref="DbfWriteException"/> with VFP error 39; invalid integer types report 9.
/// </remarks>
public static class FieldEncoder
{
    // The Julian Day Number of 0001-01-01 (proleptic Gregorian) — the offset between
    // FoxPro 'T' day numbers and .NET DayNumber (0 == 0001-01-01). Mirrors FieldDecoder.
    private const int JdnEpoch = 1_721_426;
    private const int MaxStackEncodeBytes = 256;
    private const int MaxStackNumberChars = 128;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Encode <paramref name="value"/> for <paramref name="column"/> into the first
    /// <see cref="DbfColumn.Length"/> bytes of <paramref name="dest"/>, using
    /// <paramref name="encoding"/> for character data. Returns <see langword="true"/>
    /// when a Varchar/Varbinary value shorter than the field width was written (so the
    /// caller must set the column's varlen bit in <c>_NullFlags</c>); otherwise
    /// <see langword="false"/>.
    /// </summary>
    public static bool Encode(DbfColumn column, object? value, Encoding encoding, Span<byte> dest)
    {
        // DBNull is treated identically to null (§A5b NULL → type-appropriate empty).
        if (value is DBNull)
            value = null;

        char t = column.Type;

        if (value is null)
            return EncodeNull(t, dest);

        switch (t)
        {
            case 'C':
                WriteCharacter(CoerceString(value), encoding, dest);
                return false;

            case 'N':
            case 'F':
                if (!TryWriteNumberRightJustified(value, column.Decimal, dest))
                    WriteAsciiRightJustified(FormatNumber(value, column.Decimal), dest);
                return false;

            case 'I':
            case '+':
                WriteInt32(dest, ToInt32(value));
                return false;

            case 'Y':
                WriteInt64(dest, ToCurrencyScaled(value));
                return false;

            case 'B':
                WriteDouble(dest, ToDouble(value));
                return false;

            case 'D':
                WriteDate(dest, ToDateOnly(value));
                return false;

            case 'T':
            case '@':
                WriteDateTime(dest, ToDateTime(value));
                return false;

            case 'L':
                if (dest.Length > 0)
                    dest[0] = ToBoolean(value) ? (byte)'T' : (byte)'F';
                return false;

            case 'M':
            case 'W':
                // 'M' (Memo) and 'W' (VFP9 Blob) are both FPT-backed 4-byte block pointers
                // decoded identically (FieldDecoder groups M/V/Q/W/G; DbfRecord dereferences
                // M/W as an FPT pointer). Memo/blob append is deferred to D1: D0 always writes
                // a 0 pointer. Writing the blob's raw bytes here would forge a garbage pointer.
                dest.Clear();
                return false;

            case 'V':
            case 'Q':
                return WriteVarlen(t, value, encoding, dest);

            // 'G' (General) and any other binary/unmapped type → raw bytes.
            default:
                WriteRawBytes(value, dest);
                return false;
        }
    }

    /// <summary>Write the §A5b type-appropriate empty for a null value.</summary>
    private static bool EncodeNull(char t, Span<byte> dest)
    {
        switch (t)
        {
            // Text/date/logical fields decode "all spaces" back to null.
            case 'C':
            case 'N':
            case 'F':
            case 'D':
            case 'L':
                dest.Fill((byte)' ');
                break;

            // Numeric/binary fields (I/Y/B/T/M/V/Q/G/binary) decode all-zero back to null.
            default:
                dest.Clear();
                break;
        }
        return false;
    }

    /// <summary>
    /// Write the empty Varchar/Varbinary form VFP9 stores for a blank or <c>.NULL.</c> <c>V</c>/<c>Q</c>
    /// field: <c>0x20</c> padding with a 0 effective length in the last byte (the caller sets the varlen
    /// bit). VFP pads a blank Varbinary with spaces too, not zero bytes.
    /// </summary>
    internal static void EncodeEmptyVarlen(Span<byte> dest)
    {
        if (dest.Length == 0)
            return;
        dest[..^1].Fill((byte)' ');
        dest[^1] = 0;
    }

    // ---- character -------------------------------------------------------------

    private static void WriteCharacter(string s, Encoding encoding, Span<byte> dest)
    {
        int byteCount = WriteEncodedPrefix(s, encoding, dest);
        int n = Math.Min(byteCount, dest.Length);
        // Space-pad the remainder (the inverse of the decoder's trailing-space trim).
        dest[n..].Fill((byte)' ');
    }

    private static int WriteEncodedPrefix(string s, Encoding encoding, Span<byte> dest)
    {
        if (dest.IsEmpty || s.Length == 0)
            return 0;

        int charsUsed = 0;
        for (int i = 0; i < s.Length;)
        {
            int next = i + 1;
            if (char.IsHighSurrogate(s[i]) && next < s.Length && char.IsLowSurrogate(s[next]))
                next++;

            if (encoding.GetByteCount(s.AsSpan(0, next)) > dest.Length)
                break;

            charsUsed = next;
            i = next;
        }

        return charsUsed == 0 ? 0 : encoding.GetBytes(s.AsSpan(0, charsUsed), dest);
    }

    // ---- numeric (N / F): right-justified ASCII, Decimal places ----------------

    private static void WriteAsciiRightJustified(ReadOnlySpan<char> s, Span<byte> dest)
    {
        int w = dest.Length;
        if (s.Length > w)
        {
            // Overflow (§A10 best-effort): VFP fills an over-wide numeric with '*'.
            dest.Fill((byte)'*');
            return;
        }
        int pad = w - s.Length;
        for (int i = 0; i < pad; i++)
            dest[i] = (byte)' ';
        // s is pure ASCII (digits, '.', '-'); a 1:1 byte cast is exact.
        for (int i = 0; i < s.Length; i++)
            dest[pad + i] = (byte)s[i];
    }

    internal static bool NumericOverflow(DbfColumn column, object? value, Encoding encoding)
    {
        if (column.Type is not ('N' or 'F') || value is null) return false;
        double number = ToDouble(value);
        // Most writes cannot overflow even after an integer rounding carry.
        if (double.IsFinite(number) && Math.Abs(number) < Math.Pow(10, column.Length - (number < 0 ? 1 : 0)) - 0.5)
            return false;
        Span<byte> bytes = stackalloc byte[column.Length];
        Encode(column, value, encoding, bytes);
        return bytes.Length > 0 && bytes[0] == (byte)'*';
    }

    private static bool TryWriteNumberRightJustified(object value, int decimals, Span<byte> dest)
    {
        if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f))
        {
            dest.Fill((byte)'*');
            return true;
        }
        // Normalize the CLR numeric types before both the full-scale and reduced-scale attempts.
        value = value switch
        {
            short n => (long)n, byte n => (long)n, sbyte n => (long)n,
            ushort n => (long)n, uint n => (long)n, ulong n => (decimal)n,
            string text when decimal.TryParse(text, NumberStyles.Number, Inv, out var n) => n,
            _ => value,
        };
        decimals = value switch
        {
            decimal n => VfpRuntime.NumericStorageDecimals(n, dest.Length, decimals),
            double n => VfpRuntime.NumericStorageDecimals(n, dest.Length, decimals),
            float n => VfpRuntime.NumericStorageDecimals((double)n, dest.Length, decimals),
            long n => VfpRuntime.NumericStorageDecimals((decimal)n, dest.Length, decimals),
            int n => VfpRuntime.NumericStorageDecimals((decimal)n, dest.Length, decimals),
            _ => decimals,
        };
        char[]? rented = null;
        Span<char> chars = dest.Length <= MaxStackNumberChars
            ? stackalloc char[MaxStackNumberChars]
            : (rented = ArrayPool<char>.Shared.Rent(dest.Length + 1)).AsSpan();
        // One extra position lets VFP omit a leading zero (.50 / -.12).

        try
        {
            for (int scale = decimals; scale >= 0; scale--)
            {
                if (TryFormatNumber(value, scale, chars, out int charsWritten, out bool handled))
                {
                    var text = chars[..charsWritten];
                    // .NET retains the IEEE sign of a zero rounded from a negative double.
                    if (text.StartsWith("-") && text[1..].IndexOfAnyExcept('0', '.') < 0)
                        text = text[1..];
                    if (text.Length > dest.Length && text.StartsWith("0.")) text = text[1..];
                    else if (text.Length > dest.Length && text.StartsWith("-0."))
                    {
                        chars[1] = '-';
                        text = chars[1..charsWritten];
                    }
                    if (text.Length <= dest.Length)
                    {
                        WriteAsciiRightJustified(text, dest);
                        return true;
                    }
                }
                else if (!handled) return false;
                // Retry from the original value: a rounding carry can require another lost digit.
            }
            dest.Fill((byte)'*');
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static bool TryFormatNumber(
        object value,
        int decimals,
        Span<char> dest,
        out int charsWritten,
        out bool handled)
    {
        handled = true;
        if (decimals <= 0)
        {
            ReadOnlySpan<char> defaultFormat = default;
            Span<char> f0 = stackalloc char[2];
            f0[0] = 'F';
            f0[1] = '0';

            switch (value)
            {
                case long l:
                    return l.TryFormat(dest, out charsWritten, defaultFormat, Inv);
                case int i:
                    return i.TryFormat(dest, out charsWritten, defaultFormat, Inv);
                case short sh:
                    return ((long)sh).TryFormat(dest, out charsWritten, defaultFormat, Inv);
                case byte b:
                    return ((long)b).TryFormat(dest, out charsWritten, defaultFormat, Inv);
                case decimal m:
                    return decimal.Round(m, 0, MidpointRounding.AwayFromZero)
                        .TryFormat(dest, out charsWritten, f0, Inv);
                case double d:
                    return Math.Round(d, MidpointRounding.AwayFromZero)
                        .TryFormat(dest, out charsWritten, f0, Inv);
                case float f:
                    return Math.Round((double)f, MidpointRounding.AwayFromZero)
                        .TryFormat(dest, out charsWritten, f0, Inv);
                default:
                    handled = false;
                    charsWritten = 0;
                    return false;
            }
        }

        Span<char> format = stackalloc char[11];
        format[0] = 'F';
        _ = decimals.TryFormat(format[1..], out int precisionChars, provider: Inv);
        ReadOnlySpan<char> fixedFormat = format[..(precisionChars + 1)];

        switch (value)
        {
            case decimal m:
                return m.TryFormat(dest, out charsWritten, fixedFormat, Inv);
            case double d:
                return d.TryFormat(dest, out charsWritten, fixedFormat, Inv);
            case float f:
                return ((double)f).TryFormat(dest, out charsWritten, fixedFormat, Inv);
            case long l:
                return l.TryFormat(dest, out charsWritten, fixedFormat, Inv);
            case int i:
                return i.TryFormat(dest, out charsWritten, fixedFormat, Inv);
            default:
                handled = false;
                charsWritten = 0;
                return false;
        }
    }

    private static string FormatNumber(object value, int decimals)
    {
        if (decimals <= 0)
        {
            // Integer form, no decimal point.
            return value switch
            {
                long l => l.ToString(Inv),
                int i => i.ToString(Inv),
                short sh => ((long)sh).ToString(Inv),
                byte b => ((long)b).ToString(Inv),
                decimal m => decimal.Round(m, 0, MidpointRounding.AwayFromZero).ToString("F0", Inv),
                double d => Math.Round(d, MidpointRounding.AwayFromZero).ToString("F0", Inv),
                float f => Math.Round((double)f, MidpointRounding.AwayFromZero).ToString("F0", Inv),
                _ => SafeDecimal(value).ToString("F0", Inv),
            };
        }

        string fmt = "F" + decimals.ToString(Inv);
        // Format from the value's native type to avoid lossy double→decimal conversion.
        return value switch
        {
            decimal m => m.ToString(fmt, Inv),
            double d => d.ToString(fmt, Inv),
            float f => ((double)f).ToString(fmt, Inv),
            long l => l.ToString(fmt, Inv),
            int i => i.ToString(fmt, Inv),
            _ => SafeDecimal(value).ToString(fmt, Inv),
        };
    }

    // ---- integer (I / +) -------------------------------------------------------

    private static void WriteInt32(Span<byte> dest, int v)
    {
        if (dest.Length >= 4)
            BinaryPrimitives.WriteInt32LittleEndian(dest, v);
    }

    // ---- currency (Y) ----------------------------------------------------------

    private static void WriteInt64(Span<byte> dest, long v)
    {
        if (dest.Length >= 8)
            BinaryPrimitives.WriteInt64LittleEndian(dest, v);
    }

    private static long ToCurrencyScaled(object value)
    {
        // Use the same 64-bit conversion for expressions and field writes, with the field error code.
        var number = value switch
        {
            double d => VfpValue.Number(d),
            float f => VfpValue.Number((double)f),
            _ => VfpValue.Number(SafeDecimal(value)),
        };
        try { return (long)(number.ToCurrencyNumber() * 10000m); }
        catch (ExpressionException ex) when (ex.VfpErrorNumber == 1988)
        {
            throw new DbfWriteException("Numeric overflow. Data was lost.", 39);
        }
    }

    // ---- double (B) ------------------------------------------------------------

    private static void WriteDouble(Span<byte> dest, double v)
    {
        if (dest.Length >= 8)
            BinaryPrimitives.WriteDoubleLittleEndian(dest, v);
    }

    // ---- date (D) --------------------------------------------------------------

    private static void WriteDate(Span<byte> dest, DateOnly d)
    {
        // The EMPTY date ({}) is stored as all-spaces (the blank-date bytes VFP writes), NOT "00010101" —
        // so REPLACE dfield WITH {} blanks the field (and ISBLANK()/EMPTY() read it back as blank/null).
        if (d == default)
        {
            dest.Fill((byte)' ');
            return;
        }
        // YYYYMMDD, zero-padded; the inverse of FieldDecoder.DecodeDate.
        int n = Math.Min(8, dest.Length);
        int y = d.Year;
        Span<byte> ascii = stackalloc byte[8];
        ascii[0] = (byte)('0' + (y / 1000) % 10);
        ascii[1] = (byte)('0' + (y / 100) % 10);
        ascii[2] = (byte)('0' + (y / 10) % 10);
        ascii[3] = (byte)('0' + y % 10);
        ascii[4] = (byte)('0' + d.Month / 10);
        ascii[5] = (byte)('0' + d.Month % 10);
        ascii[6] = (byte)('0' + d.Day / 10);
        ascii[7] = (byte)('0' + d.Day % 10);
        ascii[..n].CopyTo(dest);
        dest[n..].Fill((byte)' ');
    }

    // ---- datetime (T / @) ------------------------------------------------------

    private static void WriteDateTime(Span<byte> dest, DateTime dt)
    {
        if (dest.Length < 8)
            return;
        // The EMPTY datetime ({}) is stored as all-zero bytes — exactly the days==0 sentinel
        // FieldDecoder.DecodeDateTime maps back to null — mirroring WriteDate's blank handling. Without
        // this, default(DateTime) would encode days=JdnEpoch (a REAL {^0001-01-01}), so REPLACE tfield
        // WITH {} would round-trip to a non-empty value and EMPTY()/ISBLANK() would flip to .F.
        if (dt == default)
        {
            dest[..8].Clear();
            return;
        }
        int days = DateOnly.FromDateTime(dt).DayNumber + JdnEpoch;
        int ms = (int)Math.Round(dt.TimeOfDay.TotalMilliseconds, MidpointRounding.AwayFromZero);
        if (ms >= 86_400_000)
            ms = 86_399_999;
        BinaryPrimitives.WriteInt32LittleEndian(dest, days);
        BinaryPrimitives.WriteInt32LittleEndian(dest[4..], ms);
    }

    // ---- varchar / varbinary (V / Q) -------------------------------------------

    private static bool WriteVarlen(char t, object value, Encoding encoding, Span<byte> dest)
    {
        int w = dest.Length;
        if (w == 0)
            return false;

        if (t == 'V')
            return WriteVarchar(CoerceString(value), encoding, dest);

        ReadOnlySpan<byte> bytes = value is byte[] b
            ? b
            : encoding.GetBytes(CoerceString(value));

        if (bytes.Length >= w)
        {
            // Exactly (or over-) fills the field: no trailing length byte, no varlen bit.
            bytes[..w].CopyTo(dest);
            return false;
        }

        bytes.CopyTo(dest);
        // Pad the gap with spaces — VFP9 pads a Varbinary with 0x20 too, not zero bytes — then write
        // the effective length into the LAST byte and signal the caller to set the varlen bit.
        dest[bytes.Length..(w - 1)].Fill((byte)' ');
        dest[w - 1] = (byte)bytes.Length;
        return true;
    }

    private static bool WriteVarchar(string s, Encoding encoding, Span<byte> dest)
    {
        int w = dest.Length;
        int byteCount = WriteEncodedPrefix(s, encoding, dest);
        if (byteCount >= w)
            return false;

        // Pad the gap with spaces, then write the effective length into the last byte.
        dest[byteCount..(w - 1)].Fill((byte)' ');
        dest[w - 1] = (byte)byteCount;
        return true;
    }

    // ---- raw bytes (G / binary) ------------------------------------------------

    private static void WriteRawBytes(object value, Span<byte> dest)
    {
        if (value is byte[] b)
        {
            int n = Math.Min(b.Length, dest.Length);
            b.AsSpan(0, n).CopyTo(dest);
            dest[n..].Clear();
        }
        else
        {
            dest.Clear();
        }
    }

    // ---- value coercions (never throw, §A10) -----------------------------------

    private static string CoerceString(object value)
        => value as string ?? Convert.ToString(value, Inv) ?? "";

    private static long ToInt64(object value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint u => u,
        ushort us => us,
        ulong ul => unchecked((long)ul),
        bool bo => bo ? 1 : 0,
        // Narrowing to int64 throws on out-of-range (decimal->long always; double->long is
        // "unspecified"); saturate instead so 'I'/'+'/'Y' never throw (never-throw §A10, MUST-FIX).
        decimal m => SaturateToInt64(decimal.Round(m, 0, MidpointRounding.AwayFromZero)),
        double d => SaturateToInt64(Math.Round(d, MidpointRounding.AwayFromZero)),
        float f => SaturateToInt64(Math.Round((double)f, MidpointRounding.AwayFromZero)),
        string s => long.TryParse(s, NumberStyles.Number, Inv, out var r) ? r : 0L,
        _ => 0L,
    };

    /// <summary>Clamp a rounded <see cref="decimal"/> to the int64 range (no overflow throw).</summary>
    private static long SaturateToInt64(decimal rounded)
    {
        if (rounded >= long.MaxValue) return long.MaxValue;
        if (rounded <= long.MinValue) return long.MinValue;
        return (long)rounded;
    }

    /// <summary>Clamp a rounded <see cref="double"/> to the int64 range (NaN→0, no overflow throw).</summary>
    private static long SaturateToInt64(double rounded)
    {
        if (double.IsNaN(rounded)) return 0L;
        // (double)long.MaxValue rounds up to 2^63, so '>=' here saturates anything out of range.
        if (rounded >= 9223372036854775808.0) return long.MaxValue;
        if (rounded <= -9223372036854775808.0) return long.MinValue;
        return (long)rounded;
    }

    private static int ToInt32(object value)
    {
        decimal truncated = value switch
        {
            decimal m => decimal.Truncate(m),
            double d => TruncateFiniteDouble(d),
            float f => TruncateFiniteDouble(f),
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            sbyte sb => sb,
            uint u => u,
            ushort us => us,
            ulong ul when ul <= int.MaxValue => ul,
            ulong => throw IntegerOverflow(),
            string or bool => throw IntegerTypeMismatch(),
            _ => throw IntegerTypeMismatch(),
        };

        if (truncated < -2_147_483_647m || truncated > 2_147_483_647m)
            throw IntegerOverflow();
        return (int)truncated;
    }

    private static decimal TruncateFiniteDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw IntegerOverflow();
        double truncated = Math.Truncate(value);
        if (truncated < -2_147_483_647d || truncated > 2_147_483_647d)
            throw IntegerOverflow();
        return (decimal)truncated;
    }

    private static DbfWriteException IntegerOverflow()
        => new("Numeric overflow. Data was lost.", 39);

    private static DbfWriteException IntegerTypeMismatch()
        => new("Data type mismatch.", 9);

    private static decimal SafeDecimal(object value) => value switch
    {
        decimal m => m,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte b => b,
        ushort u => u,
        uint u => u,
        ulong u => u,
        double d => SafeFromDouble(d),
        float f => SafeFromDouble(f),
        string s => decimal.TryParse(s, NumberStyles.Number, Inv, out var r) ? r : 0m,
        _ => 0m,
    };

    private static decimal SafeFromDouble(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            return 0m;
        try { return (decimal)d; }
        catch (OverflowException) { return 0m; }
    }

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte b => b,
        ushort u => u,
        uint u => u,
        ulong u => u,
        string s => decimal.TryParse(s, NumberStyles.Number, Inv, out var r) ? (double)r : 0d,
        _ => 0d,
    };

    private static bool ToBoolean(object value) => value switch
    {
        bool b => b,
        string s => s.Length > 0 && (s[0] is 'T' or 't' or 'Y' or 'y' or '1'),
        _ => ToInt64(value) != 0,
    };

    private static DateOnly ToDateOnly(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => default,
    };

    private static DateTime ToDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => default,
    };
}
