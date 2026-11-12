using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>VFP value tag (data type) for <see cref="VfpValue"/> and type inference.</summary>
public enum VfpType
{
    /// <summary>Unknown / undetermined type.</summary>
    Unknown = 0,
    /// <summary>VFP <c>.NULL.</c>.</summary>
    Null,
    /// <summary>Character string (VFP type C / M).</summary>
    Character,
    /// <summary>Numeric (VFP type N / F; decimal or double backed).</summary>
    Numeric,
    /// <summary>Currency (VFP type Y).</summary>
    Currency,
    /// <summary>Integer (VFP type I).</summary>
    Integer,
    /// <summary>Date (VFP type D).</summary>
    Date,
    /// <summary>DateTime (VFP type T).</summary>
    DateTime,
    /// <summary>Logical (VFP type L).</summary>
    Logical,
}

/// <summary>
/// Immutable VFP value: a small tag plus an unboxed payload. Supports VFP
/// <c>.NULL.</c> propagation. Designed to avoid hot-path boxing.
/// </summary>
public readonly struct VfpValue : IEquatable<VfpValue>
{
    private readonly VfpType _type;
    private readonly double _num;     // numeric, double-backed
    private readonly decimal _dec;    // numeric/currency/integer, exact
    private readonly bool _isExact;   // numeric uses _dec when true
    private readonly string? _str;
    private readonly bool _isBinary;
    private readonly byte _displayDecimalsPlusOne;
    private readonly bool _bool;
    private readonly System.DateTime _date; // date + datetime payload

    private VfpValue(VfpType type, double num, decimal dec, bool isExact,
                     string? str, bool b, System.DateTime date, bool isBinary = false, byte displayDecimalsPlusOne = 0)
    {
        _type = type; _num = num; _dec = dec; _isExact = isExact;
        _str = str; _bool = b; _date = date;
        _isBinary = isBinary;
        _displayDecimalsPlusOne = displayDecimalsPlusOne;
    }

    /// <summary>The value's VFP type tag.</summary>
    public VfpType Type => _type;

    /// <summary>True for VFP <c>.NULL.</c>.</summary>
    public bool IsNull => _type == VfpType.Null;

    /// <summary>A byte-preserving VFP varbinary value (TYPE/VARTYPE Q).</summary>
    public bool IsBinary => _isBinary;

    // Display precision belongs to the numeric value, not its magnitude. Expressions retain
    // it across assignment and later SET DECIMALS changes without rounding their payload.
    internal int DisplayDecimals => _displayDecimalsPlusOne - 1;
    internal VfpValue WithDisplayDecimals(int decimals) => new(_type, _num, _dec, _isExact,
        _str, _bool, _date, _isBinary, (byte)(Math.Clamp(decimals, -1, 19) + 1));

    // Round9's live oracle: large finite B fields use a 21-column picture, N/F fields
    // retain their declared width, including after assignment and INTO ARRAY.
    // Numeric values do not use the date payload; keep the row value's size unchanged.
    internal int NumericDisplayWidth => _type == VfpType.Numeric && _date.Ticks > 0 ? (int)_date.Ticks
        : Math.Abs(_num) >= 1000000000000000d && double.IsFinite(_num)
            ? Math.Max(20, (int)Math.Floor(Math.Log10(Math.Abs(_num))) + 1 + (DisplayDecimals > 0 ? DisplayDecimals + 1 : 0) + (_num < 0 ? 1 : 0))
            : Math.Max(20, VfpRuntime.NumericOverflowWidth(DisplayDecimals));
    internal VfpValue WithNumericDisplayWidth(int width) => _type == VfpType.Numeric && double.IsFinite(_num)
        ? new(_type, _num, _dec, _isExact, _str, _bool, new System.DateTime(width), _isBinary, _displayDecimalsPlusOne)
        : this;
    // Field conversion already populated the double payload; do not convert decimal again per row.
    internal bool IsFinite => double.IsFinite(_num);
    internal bool NeedsNumericPicture(double limit) => Math.Abs(_num) >= limit && double.IsFinite(_num);

    // VFP9 calculated overflow (NPV) has a different TRANSFORM width from stored DBF overflow.
    // Retain the observed width on the value so assignments and later SET DECIMALS preserve it.
    // Non-finite double-backed numbers do not use the exact-decimal payload. Reuse it
    // for display metadata to keep this hot-path value at 48 bytes, without boxing.
    internal int OverflowDisplayWidth => _type == VfpType.Numeric && !_isExact && !double.IsFinite(_num)
        ? (int)_dec : 0;
    internal VfpValue WithOverflowDisplayWidth(int width) => _type == VfpType.Numeric && !_isExact && !double.IsFinite(_num)
        ? new(_type, _num, width, _isExact, _str, _bool, _date, _isBinary, _displayDecimalsPlusOne)
        : this;

    // Only non-finite values use this spare payload. The integer part is the stored
    // width; the fractional part holds six digits of immediate width and two
    // digits of scale+1. Re-division needs the original shape scale, which can
    // differ from DisplayDecimals for a constant quotient (review oracle).
    internal int ImmediateOverflowWidth => OverflowDisplayWidth == 0 ? 0
        : (int)((_dec - decimal.Truncate(_dec)) * 1000000m);
    internal int OverflowShapeScale => OverflowDisplayWidth == 0 ? -1
        : (int)((_dec - decimal.Truncate(_dec)) * 100000000m) % 100 - 1;
    internal VfpValue WithOverflowWidths(int stored, int immediate, int scale = -1)
        => new(_type, _num, stored + Math.Clamp(immediate, 0, 999999) / 1000000m
            + Math.Clamp(scale + 1, 0, 99) / 100000000m,
            _isExact, _str, _bool, _date, _isBinary, _displayDecimalsPlusOne);

    internal static VfpValue FromField(object? value, char type, int decimals)
    {
        // N fields with decimals are the common row-level case. Keep their direct
        // return ahead of overflow dispatch and reuse the required double conversion.
        if (value is decimal number)
        {
            if (type == 'Y') return Currency(number);
            double numeric = (double)number;
            if (Math.Abs(numeric) >= 1000000000000d)
                return LargeNumericField(number, decimals);
            return new(VfpType.Numeric, numeric, number, true, null, false, default,
                displayDecimalsPlusOne: (byte)(Math.Clamp(decimals, 0, 19) + 1));
        }
        if (value is double floating && type != 'Y')
            return type == 'F' && Math.Abs(floating) >= 1000000000000d && Math.Abs(floating) < (double)decimal.MaxValue
                ? LargeNumericField(decimal.Parse(floating.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture), decimals)
                : type == 'B' && Math.Abs(floating) >= 1000000000000d && double.IsFinite(floating)
                ? Number(floating, decimals).WithNumericDisplayWidth(21) : Number(floating, decimals);
        if (value is int integer && type != 'Y') return Integer(integer);
        return OtherField(value, type, decimals);
    }

    private static VfpValue OtherField(object? value, char type, int decimals)
        => value switch
        {
            null => Null,
            VfpValue typed => typed.WithFieldScale(type, decimals),
            _ when type == 'Y' => Currency(FromClr(value).AsNumber),
            long integer when Math.Abs((double)integer) >= 10000000000000000d => LargeNumericField(integer, decimals),
            long integer => new(VfpType.Numeric, integer, integer, true, null, false, default,
                displayDecimalsPlusOne: (byte)(Math.Clamp(decimals, 0, 19) + 1)),
            _ => FromClr(value),
        };

    // Stored overflow and large finite TRANSFORM pictures need the field width.
    // Keep it out of the ordinary decimal conversion's argument list.
    internal static VfpValue FromFieldWithWidth(object? value, char type, int decimals, int width)
    {
        var result = value switch
        {
            null => Null,
            VfpValue typed when typed.Type == VfpType.Numeric && !double.IsFinite(typed.AsDouble)
                => FieldOverflow(typed.WithFieldScale(type, decimals), type, decimals, width),
            VfpValue typed => typed.WithFieldScale(type, decimals),
            _ when type == 'Y' => Currency(FromClr(value).AsNumber),
            double floating when !double.IsFinite(floating)
                => FieldOverflow(Number(floating, decimals), type, decimals, width),
            double floating => FromField(floating, type, decimals),
            int integer => Integer(integer),
            float floating when !float.IsFinite(floating)
                => FieldOverflow(FromClr(value), type, decimals, width),
            _ => FromField(value, type, decimals),
        };
        return result.Type == VfpType.Numeric && double.IsFinite(result.AsDouble)
            && Math.Abs(result.AsDouble) >= Math.Pow(10, Math.Min(12, (type == 'B' ? 21 : width) - (decimals > 0 ? decimals + 2 : 1)))
            ? result.WithNumericDisplayWidth(type == 'B' ? 21 : Math.Max(1, width))
            : result;
    }

    private static VfpValue FieldOverflow(VfpValue value, char type, int decimals, int width)
        // Round4: assigning stored overflow retains the column width/scale. Decimal
        // fields use their direct conversion above, without a second value copy.
        => width > 0 && value.Type == VfpType.Numeric && !double.IsFinite(value.AsDouble)
            ? value.WithOverflowDisplayWidth(Math.Max(type == 'I' ? 11 : width,
                VfpRuntime.NumericOverflowWidth(decimals))) : value;

    private static VfpValue LargeNumericField(decimal number, int decimals)
    {
        // Round10's raw N(20) fixture: field decoding consumes at most sixteen
        // significant digits (123456789012345678 reads as 123456789012345600).
        int digits = decimal.Truncate(Math.Abs(number)).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        decimal unit = (decimal)Math.Pow(10, digits - 16);
        number = decimal.Truncate(number / unit) * unit;
        // The live largeval fixture distinguishes coefficient conversion from a direct
        // decimal cast: -999999999999997.5 reads as -999999999999997.6 in VFP9.
        string text = number.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
        int dot = text.IndexOf('.');
        int scale = dot < 0 ? 0 : text.Length - dot - 1;
        double coefficient = double.Parse(dot < 0 ? text : text.Remove(dot, 1), System.Globalization.CultureInfo.InvariantCulture);
        return Number(coefficient / Math.Pow(10, scale), decimals).WithNumericDisplayWidth(20);
    }

    internal VfpValue WithFieldScale(char type, int decimals) => IsNull ? this
        : type == 'Y' ? Currency(AsNumber)
        : type is 'N' or 'F' or 'B' or 'I' ? WithDisplayDecimals(decimals) : this;

    public static VfpValue Varbinary(byte[] bytes) =>
        new(VfpType.Character, 0d, 0m, false, System.Text.Encoding.Latin1.GetString(bytes), false, default, isBinary: true);

    /// <summary>The VFP <c>.NULL.</c> value.</summary>
    public static readonly VfpValue Null =
        new(VfpType.Null, 0d, 0m, false, null, false, default);

    public static VfpValue Character(string? s) =>
        new(VfpType.Character, 0d, 0m, false, s ?? string.Empty, false, default);

    public static VfpValue Number(decimal d) =>
        new(VfpType.Numeric, (double)d, d, true, null, false, default);

    public static VfpValue Number(double d) =>
        new(VfpType.Numeric, d, 0m, false, null, false, default);

    internal static VfpValue Number(double value, int decimals) =>
        new(VfpType.Numeric, value, 0m, false, null, false, default,
            displayDecimalsPlusOne: (byte)(Math.Clamp(decimals, -1, 19) + 1));

    public static VfpValue Currency(decimal d)
    {
        // VFP9 currency is a signed 64-bit integer scaled by 10,000.
        if (d < -922337203685477.5808m || d > 922337203685477.5807m)
            throw new ExpressionException("Currency value is out of range.") { VfpErrorNumber = 1988 };
        return new(VfpType.Currency, (double)d, d, true, null, false, default, displayDecimalsPlusOne: 5);
    }

    internal decimal ToCurrencyNumber()
    {
        if (_isExact) return Currency(Math.Round(_dec, 4, MidpointRounding.AwayFromZero)).AsNumber;
        // Cast the scaled integer, not the double itself: decimal(double) discards digits
        // after the fifteenth significant digit, unlike VFP9's fixed-point conversion.
        double scaled = _num * 10000d;
        double magnitude = Math.Abs(scaled);
        // VFP9 retains a half when binary multiplication falls one ULP short of the conversion boundary.
        // Quarter/half-unit ULPs are not conversion noise: bumping those corrupts large currencies.
        if (Math.BitIncrement(magnitude) - magnitude < 0.25
            && magnitude - Math.Floor(magnitude) < 0.5
            && Math.BitIncrement(magnitude) - Math.Floor(magnitude) == 0.5)
            scaled = Math.CopySign(Math.BitIncrement(magnitude), scaled);
        scaled = Math.Round(scaled, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(scaled) || scaled < long.MinValue || scaled >= 9223372036854775808d)
            throw new ExpressionException("Currency value is out of range.") { VfpErrorNumber = 1988 };
        return (long)scaled / 10000m;
    }

    public static VfpValue Integer(int i) =>
        new(VfpType.Integer, i, i, true, null, false, default);

    public static VfpValue Logical(bool b) =>
        new(VfpType.Logical, 0d, 0m, false, null, b, default);

    public static VfpValue Date(DateOnly d) =>
        new(VfpType.Date, 0d, 0m, false, null, false, d.ToDateTime(TimeOnly.MinValue));

    public static VfpValue DateTime(System.DateTime dt) =>
        new(VfpType.DateTime, 0d, 0m, false, null, false, dt);

    // CLR MinValue historically denotes an empty value. Index rows carry the separate on-disk
    // emptiness bit so the valid year-one value survives conversions and intermediate UDF values.
    internal static VfpValue Date(DateOnly date, bool empty) =>
        new(VfpType.Date, 0d, 0m, false, null, !empty, date.ToDateTime(TimeOnly.MinValue));
    internal static VfpValue DateTime(System.DateTime date, bool empty) =>
        new(VfpType.DateTime, 0d, 0m, false, null, !empty, date);
    internal bool IsEmptyDate => _type is VfpType.Date or VfpType.DateTime && _date == default && !_bool;

    /// <summary>
    /// Maps a raw CLR field value (as returned by <see cref="IRowContext.GetField"/>)
    /// to a <see cref="VfpValue"/>. <c>null</c> becomes VFP <c>.NULL.</c>.
    /// </summary>
    public static VfpValue FromClr(object? value) => value switch
    {
        null => Null,
        VfpValue v => v,
        byte[] bytes => Varbinary(bytes),
        string s => Character(s),
        bool b => Logical(b),
        char c => Character(c.ToString()),
        DateOnly d => Date(d),
        System.DateTime dt => DateTime(dt),
        decimal m => Number(m),
        double db => Number(db),
        float f => Number((double)f),
        int i => Integer(i),
        long l => Number((decimal)l),
        short sh => Integer(sh),
        sbyte sb => Integer(sb),
        byte by => Integer(by),
        ushort us => Integer(us),
        uint ui => Number((decimal)ui),
        ulong ul => Number((decimal)ul),
        _ => Character(value.ToString() ?? string.Empty),
    };

    /// <summary>Character payload (empty string for non-character).</summary>
    public string AsString => _str ?? string.Empty;

    /// <summary>Numeric payload as <see cref="decimal"/>.</summary>
    public decimal AsNumber
    {
        get
        {
            if (_isExact) return _dec;
            if (double.IsNaN(_num)) return 0m;
            if (_num >= (double)decimal.MaxValue) return decimal.MaxValue;
            if (_num <= (double)decimal.MinValue) return decimal.MinValue;
            return (decimal)_num;
        }
    }

    // VFP9 formats doubles with at most 16 significant digits and 15 fractional digits.
    // This is a display conversion only: arithmetic keeps the original double payload.
    internal decimal AsDisplayNumber
    {
        get
        {
            if (_type == VfpType.Currency)
            {
                decimal magnitude = Math.Abs(_dec);
                int digits = magnitude < 1 ? 1 : (int)Math.Floor(Math.Log10((double)magnitude)) + 1;
                return Math.Round(_dec, Math.Clamp(16 - digits, 0, 4), MidpointRounding.AwayFromZero);
            }
            if (_isExact || !double.IsFinite(_num) || Math.Abs(_num) >= (double)decimal.MaxValue) return AsNumber;
            Span<char> text = stackalloc char[32];
            // VFP9 rounds the sixteenth significant digit away at a midpoint.
            bool large = Math.Abs(_num) >= 100000000000000d;
            _num.TryFormat(text, out int length, large ? "G17" : "G16", System.Globalization.CultureInfo.InvariantCulture);
            if (!decimal.TryParse(text[..length], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out decimal value)) return AsNumber;
            // Log10 rounds up just below a power of ten (999999999999999.5),
            // which would incorrectly discard its last fractional digit.
            int places = large ? 16 - decimal.Truncate(Math.Abs(value))
                .ToString(System.Globalization.CultureInfo.InvariantCulture).Length : 15;
            // Sixteen significant digits also limits the integer part: the live B(2)
            // SUM probe renders 100000123456789016 as 100000123456789000.
            if (places < 0)
            {
                decimal unit = (decimal)Math.Pow(10, -places);
                return Math.Round(value / unit, 0, MidpointRounding.AwayFromZero) * unit;
            }
            return Math.Round(value, places, MidpointRounding.AwayFromZero);
        }
    }

    // The exact value of the double nearest 0.0001, to decimal's 28 places.
    private const decimal BinaryCurrencyScale = 0.0001000000000000000047921736m;

    internal decimal RoundForDisplay(int decimals)
    {
        if (_type != VfpType.Currency)
            return Math.Round(AsDisplayNumber, decimals, MidpointRounding.AwayFromZero);
        // Round10: currency pictures multiply the scaled integer by the double 0.0001 and round
        // once (S9: raw -922337203685475800 shows .58, S2: raw ...840 shows .59). Casting the
        // integer to double first would merge both.
        if (Math.Abs(_dec) >= 10000000000000m)
        {
            string product = (decimal.Truncate(_dec * 10000m) * BinaryCurrencyScale).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Math.Round(Number(double.Parse(product, System.Globalization.CultureInfo.InvariantCulture)).AsDisplayNumber, decimals, MidpointRounding.AwayFromZero);
        }
        decimal magnitude = Math.Abs(_dec);
        int digits = magnitude < 1 ? 1 : (int)Math.Floor(Math.Log10((double)magnitude)) + 1;
        // Apply the display's significant-digit limit and requested scale in one rounding.
        return Math.Round(_dec, Math.Min(decimals, Math.Clamp(16 - digits, 0, 4)), MidpointRounding.AwayFromZero);
    }

    /// <summary>Numeric payload as <see cref="double"/>.</summary>
    public double AsDouble => _isExact ? (double)_dec : _num;

    /// <summary>Numeric payload truncated to <see cref="int"/>.</summary>
    public int AsInteger
    {
        get
        {
            double value = AsDouble;
            if (double.IsNaN(value)) return 0;
            if (value >= int.MaxValue) return int.MaxValue;
            if (value <= int.MinValue) return int.MinValue;
            return (int)value;
        }
    }

    // VFP9 argument conversion keeps the low 32 bits of a truncated 64-bit integer;
    // values outside Int64 become zero. Round5 pins ROUND, MLINE, STREXTRACT and ALEN.
    // Keep the public saturating AsInteger API separate; built-ins validate their own ranges.
    internal int TruncatedInt32
    {
        get
        {
            double value = Math.Truncate(AsDouble);
            return !double.IsFinite(value) || value < long.MinValue || value >= 9223372036854775808d
                ? 0 : unchecked((int)(long)value);
        }
    }

    /// <summary>Logical payload.</summary>
    public bool AsLogical => _bool;

    /// <summary>Date payload.</summary>
    public DateOnly AsDate => _type is VfpType.Date or VfpType.DateTime ? DateOnly.FromDateTime(_date) : default;

    /// <summary>DateTime payload.</summary>
    public System.DateTime AsDateTime => _type is VfpType.Date or VfpType.DateTime ? _date : default;

    /// <summary>
    /// Maps this value back to a raw CLR object (the inverse of <see cref="FromClr"/>), preserving
    /// numeric backing exactly: an exact (decimal-backed) numeric yields a <see cref="decimal"/>, a
    /// double-backed numeric a <see cref="double"/>, so a memvar round-tripped through
    /// <see cref="IRowContext.GetField"/> keeps its value bit-for-bit. <c>.NULL.</c> → <c>null</c>.
    /// </summary>
    public object? ToClr() => _type switch
    {
        VfpType.Null => null,
        VfpType.Character => _isBinary ? System.Text.Encoding.Latin1.GetBytes(_str ?? string.Empty) : _str ?? string.Empty,
        VfpType.Logical => _bool,
        VfpType.Date => _date == default && _bool ? this : AsDate,
        VfpType.DateTime => _date == default && _bool ? this : _date,
        VfpType.Currency => _dec,
        VfpType.Integer => (int)_dec,
        _ => _isExact ? _dec : _num,   // Numeric: decimal when exact, else double.
    };

    public bool Equals(VfpValue other)
    {
        if (_type != other._type) return false;
        return _type switch
        {
            VfpType.Null => true,
            VfpType.Character => string.Equals(_str, other._str, StringComparison.Ordinal),
            VfpType.Logical => _bool == other._bool,
            VfpType.Date or VfpType.DateTime => _date == other._date && IsEmptyDate == other.IsEmptyDate,
            VfpType.Numeric or VfpType.Currency or VfpType.Integer => AsDouble == other.AsDouble,
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is VfpValue v && Equals(v);

    public override int GetHashCode() => _type switch
    {
        VfpType.Character => _str?.GetHashCode(StringComparison.Ordinal) ?? 0,
        VfpType.Logical => _bool.GetHashCode(),
        VfpType.Date or VfpType.DateTime => HashCode.Combine(_date, IsEmptyDate),
        VfpType.Numeric or VfpType.Currency or VfpType.Integer => AsDouble.GetHashCode(),
        _ => (int)_type,
    };

    public override string ToString() => _type switch
    {
        VfpType.Null => ".NULL.",
        VfpType.Character => AsString,
        VfpType.Logical => _bool ? ".T." : ".F.",
        VfpType.Date => AsDate.ToString("yyyy-MM-dd"),
        VfpType.DateTime => _date.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => (_isExact ? _dec.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       : _num.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    };
}
