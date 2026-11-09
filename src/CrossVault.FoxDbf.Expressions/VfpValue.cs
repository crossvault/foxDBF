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

    // Display precision belongs to the numeric value, not its magnitude. Aggregates retain
    // it across assignment and later SET DECIMALS changes without rounding their payload.
    internal int DisplayDecimals => _displayDecimalsPlusOne - 1;
    internal VfpValue WithDisplayDecimals(int decimals) => new(_type, _num, _dec, _isExact,
        _str, _bool, _date, _isBinary, (byte)(Math.Clamp(decimals, 0, 19) + 1));

    internal VfpValue WithFieldScale(char type, int decimals) => IsNull ? this
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

    public static VfpValue Currency(decimal d) =>
        new(VfpType.Currency, (double)d, d, true, null, false, default);

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
    internal bool IsEmptyDate => _date == default && !_bool;

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

    /// <summary>Logical payload.</summary>
    public bool AsLogical => _bool;

    /// <summary>Date payload.</summary>
    public DateOnly AsDate => DateOnly.FromDateTime(_date);

    /// <summary>DateTime payload.</summary>
    public System.DateTime AsDateTime => _date;

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
