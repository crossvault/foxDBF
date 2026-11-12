namespace CrossVault.FoxDbf;

/// <summary>
/// An immutable DBF field descriptor (plan §A4): the cleaned field
/// <see cref="Name"/>, the single-character <see cref="Type"/> code, the
/// on-disk <see cref="Length"/> in bytes, the <see cref="Decimal"/> count, and
/// the computed <see cref="Offset"/> — the prefix-sum of all prior column
/// lengths. The offset is measured from the first field byte (the record's
/// leading byte 0 is the deletion flag, so the physical byte position of this
/// field's data within a record buffer is <c>Offset + 1</c>).
/// </summary>
/// <remarks>
/// The validating constructor enforces the §A10 invariants: a negative
/// <paramref name="length"/> raises <see cref="DbfColumnLengthException"/>, and
/// an empty (post-clean) <paramref name="name"/> raises
/// <see cref="DbfColumnNameException"/>.
/// </remarks>
public sealed record DbfColumn
{
    /// <summary>The cleaned field name (trailing NUL/space bytes trimmed).</summary>
    public string Name { get; } = "";

    /// <summary>The single-character field type code (e.g. 'C', 'N', 'M', '0').</summary>
    public char Type { get; }

    /// <summary>The on-disk field length in bytes.</summary>
    public int Length { get; }

    /// <summary>The decimal-places count (byte 15 of the v0x02 16-byte descriptor, byte 17 of the 32-byte one).</summary>
    public int Decimal { get; }

    /// <summary>Prefix-sum of prior column lengths; field data lives at record byte <c>Offset + 1</c>.</summary>
    public int Offset { get; }

    // Values below this bound cannot overflow the declared finite TRANSFORM picture.
    // Cache the negative-sign boundary so ordinary row reads need only one comparison.
    internal double NumericPictureLimit { get; }

    /// <summary>
    /// The VFP field-flags byte from the 32-byte descriptor (offset 18). Only the
    /// 32-byte layout carries it; the 16/48-byte layouts have no such byte and
    /// resolve to 0 (plan §A5b). Bit meanings are exposed via <see cref="IsSystem"/>,
    /// <see cref="IsNullable"/>, <see cref="IsBinary"/> and <see cref="IsAutoIncrement"/>.
    /// </summary>
    public byte Flags { get; }

    /// <summary>True when flag bit <c>0x01</c> is set: a hidden system column (e.g. <c>_NullFlags</c>).</summary>
    public bool IsSystem => (Flags & 0x01) != 0;

    /// <summary>True when flag bit <c>0x02</c> is set: the field is NULL-able (consumes a null bit in <c>_NullFlags</c>).</summary>
    public bool IsNullable => (Flags & 0x02) != 0;

    /// <summary>True when flag bit <c>0x04</c> is set: binary / NOCPTRANS (C/M not transcoded; §A5b/§A7).</summary>
    public bool IsBinary => (Flags & 0x04) != 0;

    /// <summary>True when flag bit <c>0x08</c> is set: AutoIncrement (verified test is <c>(Flags &amp; 0x08) != 0</c>, §A5b).</summary>
    public bool IsAutoIncrement => (Flags & 0x08) != 0;

    /// <summary>
    /// Create a validated column descriptor. Throws <see cref="DbfColumnLengthException"/>
    /// when <paramref name="length"/> is negative and <see cref="DbfColumnNameException"/>
    /// when <paramref name="name"/> is empty. The optional <paramref name="flags"/> byte
    /// carries the §A5b VFP field flags (0 for the 16/48-byte descriptor layouts).
    /// </summary>
    public DbfColumn(string name, char type, int length, int decimalCount, int offset, byte flags = 0)
    {
        if (length < 0)
            throw new DbfColumnLengthException($"Column '{name}' has a negative length ({length}).");
        if (string.IsNullOrEmpty(name))
            throw new DbfColumnNameException("Column name is empty after cleaning (NUL/space trim).");

        Name = name;
        Type = type;
        Length = length;
        Decimal = decimalCount;
        Offset = offset;
        Flags = flags;
        NumericPictureLimit = type is 'N' or 'F' or 'B'
            ? Math.Pow(10, Math.Min(12, (type == 'B' ? 21 : length) - (decimalCount > 0 ? decimalCount + 2 : 1)))
            : double.PositiveInfinity;
    }
}
