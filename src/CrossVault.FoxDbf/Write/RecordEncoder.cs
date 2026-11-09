namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Assembles a full physical record buffer (<see cref="DbfTable.RecordLength"/> bytes)
/// from a row of .NET values — the record-level inverse of the reader (plan §A5b/§D0/§D1).
/// Layout: leading delete flag (<c>0x20</c> active / <c>0x2A</c> deleted), each field at
/// <c>column.Offset + 1</c> via <see cref="FieldEncoder"/>, and the <c>_NullFlags</c> bitmap
/// (LSB-first physical order): a set NULL bit per null nullable field, plus a varlen bit and
/// trailing length byte per short Varchar/Varbinary value. Memo (<c>M</c>) fields write a
/// 0 pointer here (memo append is deferred to D1).
/// </summary>
public static class RecordEncoder
{
    /// <summary>
    /// Encode <paramref name="values"/> (one per non-system column, in physical order) into
    /// a fresh <c>byte[RecordLength]</c> for <paramref name="schema"/>.
    /// </summary>
    public static byte[] Encode(DbfTable schema, object?[] values, bool deleted = false)
        => Encode(schema, values, deleted, blankWhenNull: null);

    /// <summary>
    /// <see cref="Encode(DbfTable, object?[], bool)"/> with a per-column BLANK mask (positional, like
    /// <paramref name="values"/>): a null value whose mask entry is set encodes as the VFP blank of its
    /// type with the NULL bit left CLEAR — what VFP9 stores for a column an <c>INSERT</c> omits under
    /// <c>SET NULL OFF</c> — instead of <c>.NULL.</c>.
    /// </summary>
    internal static byte[] Encode(DbfTable schema, object?[] values, bool deleted, bool[]? blankWhenNull)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        var dest = new byte[schema.RecordLength];
        // Leading deletion flag: 0x20 active, 0x2A '*' deleted (§A3/§A4).
        dest[0] = deleted ? (byte)0x2A : (byte)0x20;

        var columns = schema.Columns;
        var encoding = schema.TextEncoding;
        var nf = schema.NullFlagsColumn;

        for (int ci = 0; ci < columns.Count; ci++)
        {
            var column = columns[ci];
            // The _NullFlags system column is written separately (its bits accumulate from
            // every other field), never as a positional value. Skip it here.
            if (column.IsSystem)
                continue;

            object? value = ci < values.Length ? values[ci] : null;
            if (value is DBNull)
                value = null;

            var field = dest.AsSpan(column.Offset + 1, column.Length);
            var (varlenBit, nullBit) = schema.GetVarlenNullBits(column);

            // NOCPTRANS strings retain their session bytes (Latin1 for the table-native API).
            // Q accepts raw byte[] as well as strings; neither crosses a table conversion map.
            var fieldEnc = ((column.IsBinary && column.Type is 'C' or 'V') || column.Type == 'Q')
                ? schema.BinaryTextEncoding
                : encoding;

            if (value is null)
            {
                bool blank = blankWhenNull is not null && ci < blankWhenNull.Length && blankWhenNull[ci];
                if ((blank || nullBit >= 0) && column.Type is ('V' or 'Q') && varlenBit >= 0 && nf is not null)
                {
                    // VFP9 writes a blank AND a .NULL. Varchar/Varbinary the same way: 0x20 padding, a 0
                    // length in the last byte and the varlen bit set (the null bit then tells them apart).
                    FieldEncoder.EncodeEmptyVarlen(field);
                    SetBit(dest, nf, varlenBit);
                }
                else
                {
                    // Type-appropriate empty bytes.
                    FieldEncoder.Encode(column, null, fieldEnc, field);
                }

                // The NULL bit for a nullable column — unless the caller asked for the blank.
                if (!blank && nullBit >= 0 && nf is not null)
                    SetBit(dest, nf, nullBit);
            }
            else
            {
                bool varlenUsed = FieldEncoder.Encode(column, value, fieldEnc, field);
                if (varlenUsed && varlenBit >= 0 && nf is not null)
                    SetBit(dest, nf, varlenBit);
            }
        }

        return dest;
    }

    /// <summary>
    /// Encode <paramref name="values"/> keyed by column name (case-insensitive) into a fresh
    /// <c>byte[RecordLength]</c> for <paramref name="schema"/>; missing keys encode as NULL.
    /// </summary>
    public static byte[] Encode(DbfTable schema, IReadOnlyDictionary<string, object?> values, bool deleted = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        // Re-key into a case-insensitive lookup so the column-name match is independent of
        // the caller's dictionary comparer, then project into positional order.
        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values)
            lookup[kv.Key] = kv.Value;

        var columns = schema.Columns;
        var positional = new object?[columns.Count];
        for (int ci = 0; ci < columns.Count; ci++)
        {
            // Missing key → null (encodes as the §A5b type-appropriate empty + NULL bit).
            positional[ci] = lookup.TryGetValue(columns[ci].Name, out var v) ? v : null;
        }

        return Encode(schema, positional, deleted);
    }

    /// <summary>Set bit <paramref name="bit"/> in the <c>_NullFlags</c> bitmap (LSB-first, §A5b).</summary>
    private static void SetBit(byte[] dest, DbfColumn nullFlags, int bit)
    {
        int index = nullFlags.Offset + 1 + (bit >> 3);
        if ((uint)index < (uint)dest.Length)
            dest[index] |= (byte)(1 << (bit & 7));
    }
}
