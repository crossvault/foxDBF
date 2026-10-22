using System.Buffers.Binary;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// The logical type of a CDX/IDX tag KEY, resolved from the tag KEY expression
/// (plan §C5). Drives how <see cref="IndexKey.Decode"/> turns the stored
/// order-preserving key bytes back into a .NET value.
/// </summary>
public enum IndexKeyType
{
    /// <summary>Type could not be resolved (no schema / composite expression / unknown column).</summary>
    Unknown = 0,

    /// <summary>Character key (raw space-padded bytes; pad <c>0x20</c>).</summary>
    Character,

    /// <summary>Integer (DBF type <c>I</c>): 4-byte big-endian with the sign bit flipped.</summary>
    Integer,

    /// <summary>Numeric / Float / Currency / Double (<c>N</c>/<c>F</c>/<c>Y</c>/<c>B</c>): 8-byte transformed IEEE-754 double.</summary>
    Numeric,

    /// <summary>Date (DBF type <c>D</c>): 8-byte transformed double whose integer part is a Julian day number.</summary>
    Date,

    /// <summary>DateTime (DBF type <c>T</c>): 8-byte transformed double = Julian day + fractional day.</summary>
    DateTime,
}

/// <summary>
/// A decoded CDX/IDX tag key (plan §C5): reverses the order-preserving on-disk
/// transform back into a .NET value given the tag key <see cref="Type"/>, while
/// always exposing the original <see cref="RawBytes"/>.
///
/// <para>Transforms reversed:</para>
/// <list type="bullet">
///   <item><b>Numeric/Date/DateTime</b> — take the 8 stored (big-endian) bytes; if
///   the top bit is set the original was non-negative → clear bit 63; else the
///   original was negative → invert ALL 64 bits; then read as a big-endian double.
///   Date: the double is a Julian day number → <see cref="DateOnly"/>. DateTime:
///   Julian day + fractional day → <see cref="DateTime"/> (rounded to the millisecond).</item>
///   <item><b>Integer</b> — 4-byte big-endian with the sign bit flipped back → <see cref="int"/>.</item>
///   <item><b>Character</b> — the raw key bytes (trailing spaces significant); the
///   trimmed string is exposed via <see cref="AsString"/>.</item>
/// </list>
/// Never throws on bad/short/malformed key bytes — yields an undecodable key
/// (<see cref="Value"/> null) instead.
/// </summary>
public readonly struct IndexKey
{
    /// <summary>The original stored key bytes (the tag key length, incl. any trailing pad).</summary>
    public byte[] RawBytes { get; }

    /// <summary>The resolved key type used to decode <see cref="Value"/>.</summary>
    public IndexKeyType Type { get; }

    /// <summary>
    /// The decoded value: <see cref="string"/> (Character, trailing-trimmed),
    /// <see cref="int"/> (Integer), <see cref="double"/> (Numeric),
    /// <see cref="DateOnly"/> (Date), <see cref="DateTime"/> (DateTime), or
    /// <see langword="null"/> when undecodable / <see cref="IndexKeyType.Unknown"/>.
    /// </summary>
    public object? Value { get; }

    /// <summary>Constructs a decoded key. Public for testing; normally produced by <see cref="Decode"/>.</summary>
    public IndexKey(IndexKeyType type, byte[] rawBytes, object? value)
    {
        Type = type;
        RawBytes = rawBytes;
        Value = value;
    }

    /// <summary>The original stored key bytes as a span.</summary>
    public ReadOnlySpan<byte> Raw => RawBytes;

    /// <summary>The decoded value as a <see cref="string"/> (Character keys), else null.</summary>
    public string? AsString => Value as string;

    /// <summary>The decoded value as an <see cref="int"/> (Integer keys), else null.</summary>
    public int? AsInt32 => Value as int?;

    /// <summary>The decoded value as a <see cref="double"/> (Numeric keys), else null.</summary>
    public double? AsDouble => Value as double?;

    /// <summary>The decoded value as a <see cref="DateOnly"/> (Date keys), else null.</summary>
    public DateOnly? AsDate => Value as DateOnly?;

    /// <summary>The decoded value as a <see cref="DateTime"/> (DateTime keys), else null.</summary>
    public DateTime? AsDateTime => Value as DateTime?;

    /// <summary>
    /// Decodes <paramref name="keyBytes"/> as a key of the given <paramref name="type"/>
    /// (plan §C5). Never throws — returns an undecodable key on short/garbage input.
    /// </summary>
    public static IndexKey Decode(ReadOnlySpan<byte> keyBytes, IndexKeyType type)
    {
        var raw = keyBytes.ToArray();
        object? value;
        try
        {
            value = type switch
            {
                IndexKeyType.Character => DecodeCharacter(raw),
                IndexKeyType.Integer => DecodeInteger(keyBytes),
                IndexKeyType.Numeric => DecodeDouble(keyBytes),
                IndexKeyType.Date => DecodeDate(keyBytes),
                IndexKeyType.DateTime => DecodeDateTime(keyBytes),
                _ => null,
            };
        }
        catch
        {
            // Never throw on bad / short / malformed key bytes.
            value = null;
        }

        return new IndexKey(type, raw, value);
    }

    /// <summary>
    /// Decodes a MACHINE character key stored in <paramref name="encoding"/> (the table code page): the
    /// value is the trailing-pad-trimmed string. Never throws.
    /// </summary>
    internal static IndexKey DecodeCharacter(ReadOnlySpan<byte> keyBytes, Encoding encoding)
    {
        var raw = keyBytes.ToArray();
        object? value;
        try { value = encoding.GetString(raw).TrimEnd(' '); }
        catch { value = null; }
        return new IndexKey(IndexKeyType.Character, raw, value);
    }

    /// <summary>Character key: trailing spaces are significant; the value is the trailing-trimmed string.</summary>
    private static object DecodeCharacter(byte[] raw)
        // Latin1 maps every byte 1:1 (ASCII-compatible); trim only trailing pad spaces.
        => Encoding.Latin1.GetString(raw).TrimEnd(' ');

    /// <summary>Integer (I): 4-byte big-endian with the sign bit flipped back.</summary>
    private static object? DecodeInteger(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
            return null;
        uint u = BinaryPrimitives.ReadUInt32BigEndian(b);
        u ^= 0x8000_0000u; // flip the sign bit back
        return unchecked((int)u);
    }

    /// <summary>
    /// Reverses the order-preserving 8-byte transform back to an IEEE-754 double:
    /// if bit 63 is set the original was non-negative → clear bit 63; else the
    /// original was negative → invert ALL 64 bits; then read the bit pattern.
    /// </summary>
    private static double? DecodeTransformedDouble(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8)
            return null;
        ulong u = BinaryPrimitives.ReadUInt64BigEndian(b);
        if ((u & 0x8000_0000_0000_0000ul) != 0)
            u &= ~0x8000_0000_0000_0000ul; // non-negative original: clear bit 63
        else
            u = ~u; // negative original: invert all 64 bits
        return BitConverter.Int64BitsToDouble(unchecked((long)u));
    }

    private static object? DecodeDouble(ReadOnlySpan<byte> b)
        => DecodeTransformedDouble(b);

    private static object? DecodeDate(ReadOnlySpan<byte> b)
    {
        var d = DecodeTransformedDouble(b);
        if (d is null)
            return null;
        var (y, m, day) = JulianDayToYmd((long)Math.Round(d.Value));
        try
        {
            return new DateOnly(y, m, day);
        }
        catch
        {
            return null;
        }
    }

    private static object? DecodeDateTime(ReadOnlySpan<byte> b)
    {
        var d = DecodeTransformedDouble(b);
        if (d is null)
            return null;

        double dayNumber = Math.Floor(d.Value);
        double frac = d.Value - dayNumber;
        var (y, m, day) = JulianDayToYmd((long)dayNumber);

        // Fractional day → milliseconds (the 8-byte double carries ~ms precision).
        long ms = (long)Math.Round(frac * 86_400_000.0);
        try
        {
            return new DateTime(y, m, day, 0, 0, 0, DateTimeKind.Unspecified)
                .AddMilliseconds(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts an astronomical Julian Day Number to a proleptic-Gregorian
    /// year/month/day (Fliegel–Van Flandern). VFP stores date/datetime index keys
    /// as this day number (midnight = integer part).
    /// </summary>
    internal static (int Year, int Month, int Day) JulianDayToYmd(long jdn)
    {
        long a = jdn + 32044;
        long b = (4 * a + 3) / 146097;
        long c = a - (146097 * b) / 4;
        long dd = (4 * c + 3) / 1461;
        long e = c - (1461 * dd) / 4;
        long mm = (5 * e + 2) / 153;

        int day = (int)(e - (153 * mm + 2) / 5 + 1);
        int month = (int)(mm + 3 - 12 * (mm / 10));
        int year = (int)(100 * b + dd - 4800 + mm / 10);
        return (year, month, day);
    }

    /// <summary>Forward Gregorian → Julian Day Number (inverse of <see cref="JulianDayToYmd"/>).</summary>
    internal static long YmdToJulianDay(int year, int month, int day)
    {
        long a = (14 - month) / 12;
        long y = year + 4800 - a;
        long m = month + 12 * a - 3;
        return day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
    }

    /// <summary>
    /// Encodes a .NET <paramref name="value"/> to the order-preserving key bytes for
    /// the given <paramref name="type"/> (the inverse of <see cref="Decode"/>). Returns
    /// <see langword="null"/> when the value is absent / not encodable. Never throws.
    /// </summary>
    internal static byte[]? Encode(object? value, IndexKeyType type)
    {
        if (value is null)
            return null;
        try
        {
            return type switch
            {
                IndexKeyType.Character => EncodeCharacter(value),
                IndexKeyType.Integer => EncodeInteger(value),
                IndexKeyType.Numeric => EncodeDouble(Convert.ToDouble(value)),
                IndexKeyType.Date => EncodeDate(value),
                IndexKeyType.DateTime => EncodeDateTime(value),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? EncodeCharacter(object value)
        => value is string s ? Encoding.Latin1.GetBytes(s) : null;

    /// <summary>
    /// The byte encoding a CHARACTER index key over <paramref name="keyExpr"/> stores and compares in
    /// (VFP9: the TABLE code page — 'ñ' is 0xA4 in a CP850 table, 0xF1 in a CP1252 one). A bare
    /// binary/NOCPTRANS field is byte-identity Latin-1 (its value is decoded 1:1); every other field and
    /// every expression uses the table encoding. No table → the library default code page (CP1252).
    /// </summary>
    internal static Encoding ResolveCharacterEncoding(DbfTable? table, string? keyExpr)
    {
        if (table is null)
            return Encodings.ResolveEncoding(0);
        string field = (keyExpr ?? string.Empty).Trim();
        foreach (var col in table.Columns)
            if (string.Equals(col.Name, field, StringComparison.OrdinalIgnoreCase))
                return col.IsBinary ? Encoding.Latin1 : table.Encoding;
        return table.Encoding;
    }

    /// <summary>
    /// The NATURAL (unpadded) stored bytes of a CHARACTER key value: under MACHINE the raw code-page
    /// bytes in <paramref name="encoding"/>; under any other collation the collation weight key. The
    /// SINGLE producer shared by the CDX builder, incremental maintenance, SEEK and Rushmore, so the
    /// written keys and every needle compared against them stay in lock-step.
    /// </summary>
    internal static byte[] EncodeCharacterKey(ReadOnlySpan<char> s, IVfpCollation collation, Encoding encoding)
    {
        if (!IsMachineCollation(collation))
            return collation.GetCollatedKey(s);
        var bytes = new byte[encoding.GetByteCount(s)];
        encoding.GetBytes(s, bytes);
        return bytes;
    }

    /// <summary>True for the identity (MACHINE) collation, whose keys are raw code-page bytes.</summary>
    internal static bool IsMachineCollation(IVfpCollation collation)
        => string.Equals(collation.Name, VfpCollations.Machine.Name, StringComparison.OrdinalIgnoreCase);

    private static byte[] EncodeInteger(object value)
    {
        uint u = unchecked((uint)Convert.ToInt32(value)) ^ 0x8000_0000u;
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, u);
        return buf;
    }

    private static byte[] EncodeTransformedDouble(double d)
    {
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(d));
        if ((bits & 0x8000_0000_0000_0000ul) != 0)
            bits = ~bits;                          // negative: invert all 64 bits
        else
            bits |= 0x8000_0000_0000_0000ul;       // non-negative: set bit 63
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, bits);
        return buf;
    }

    private static byte[] EncodeDouble(double d) => EncodeTransformedDouble(d);

    /// <summary>
    /// Encodes an ORDERED-key value bound (a decoded <see cref="double"/> in the SAME scale
    /// the Rushmore optimizer compares against — raw value for Numeric, Julian-day(+frac)
    /// for Date/DateTime, the integer for Integer) into order-preserving key bytes for a Rushmore
    /// SEEK lower bound. The bytes compare (unsigned) identically to the stored keys, so a B-tree
    /// descent on them lands at-or-before the first matching key (the forward walk + predicate then
    /// confirm). Returns <see langword="null"/> to mean "no lower bound / start at the left-most leaf"
    /// (an Integer bound below <see cref="int.MinValue"/>); an Integer bound above
    /// <see cref="int.MaxValue"/> saturates to the maximum key. Never throws.
    /// </summary>
    internal static byte[]? EncodeOrderedBound(IndexKeyType type, double value)
    {
        try
        {
            switch (type)
            {
                case IndexKeyType.Integer:
                    // Floor so the bound never overshoots the first matching integer key.
                    double f = Math.Floor(value);
                    if (f > int.MaxValue) return EncodeInteger(int.MaxValue);
                    if (f < int.MinValue) return null; // -inf → left-most leaf
                    return EncodeInteger((int)f);
                case IndexKeyType.Numeric:
                case IndexKeyType.Date:
                case IndexKeyType.DateTime:
                    return EncodeTransformedDouble(value);
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? EncodeDate(object value)
    {
        DateOnly date = value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => default,
        };
        if (value is not DateOnly and not DateTime)
            return null;
        return EncodeTransformedDouble(YmdToJulianDay(date.Year, date.Month, date.Day));
    }

    private static byte[]? EncodeDateTime(object value)
    {
        if (value is not DateTime dt)
            return null;
        long jdn = YmdToJulianDay(dt.Year, dt.Month, dt.Day);
        double frac = dt.TimeOfDay.TotalDays;
        return EncodeTransformedDouble(jdn + frac);
    }

    /// <summary>
    /// Resolves the key TYPE from a tag KEY <paramref name="keyExpression"/> (plan §C5):
    /// a bare field name maps to its <see cref="DbfColumn.Type"/> via <paramref name="table"/>;
    /// otherwise <see cref="IndexKeyType.Unknown"/>.
    /// </summary>
    public static IndexKeyType ResolveType(string keyExpression, DbfTable? table)
    {
        if (table is null || keyExpression is null)
            return IndexKeyType.Unknown;

        string field = keyExpression.Trim();
        if (field.Length == 0)
            return IndexKeyType.Unknown;

        foreach (var col in table.Columns)
        {
            if (string.Equals(col.Name, field, StringComparison.OrdinalIgnoreCase))
                return FromColumnType(col.Type);
        }

        // Not a bare field name (composite expression, function, …) → unresolved.
        return IndexKeyType.Unknown;
    }

    /// <summary>Maps a DBF column type to the logical index key type (plan §C5).</summary>
    internal static IndexKeyType FromColumnType(char dbfType) => dbfType switch
    {
        'C' or 'V' => IndexKeyType.Character,
        'I' => IndexKeyType.Integer,
        'N' or 'F' or 'Y' or 'B' => IndexKeyType.Numeric,
        'D' => IndexKeyType.Date,
        'T' => IndexKeyType.DateTime,
        _ => IndexKeyType.Unknown,
    };
}
