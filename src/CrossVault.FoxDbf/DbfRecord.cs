namespace CrossVault.FoxDbf;

/// <summary>
/// A lightweight handle over a single physical record's byte buffer (plan §A3/§A4).
/// The buffer length equals <see cref="DbfTable"/>'s record length and includes the
/// leading deletion flag at byte 0 (<c>0x2A</c> <c>'*'</c> = deleted, <c>0x20</c>
/// space = active). Field data starts at record byte <c>1</c>, so a column's data is
/// sliced at <c>column.Offset + 1 .. column.Offset + 1 + column.Length</c>.
/// </summary>
/// <remarks>
/// Records yielded by <see cref="DbfTable.Records"/> / <see cref="DbfTable.EnumerateAll"/>
/// are backed by a <em>copy</em> of the streaming buffer, so a yielded record stays
/// valid after the enumerator advances. Value decoding (§A5) is a later phase; for now
/// this exposes raw field bytes plus a placeholder decoded accessor.
/// </remarks>
public readonly struct DbfRecord
{
    private readonly byte[] _buffer;
    private readonly DbfTable _table;

    // Per-record decode memo. Both arrays are reference fields on a readonly struct:
    // struct copies share them, and mutating their *elements* (not the field) is legal.
    // _decoded says what the slot holds: Decoded (the indexer's value) or ExpressionDecoded (a C field at full
    // width, GetExpressionValue); a C slot holds one of the two at a time.
    private readonly object?[] _cache;
    private readonly byte[] _decoded;
    private const byte Decoded = 1, ExpressionDecoded = 2;

    /// <summary>
    /// Wrap a per-record buffer. The buffer is owned by this record (callers must hand
    /// over a copy when the source buffer is reused across iterations).
    /// </summary>
    internal DbfRecord(DbfTable table, byte[] buffer)
    {
        _table = table;
        _buffer = buffer;
        int n = table.Columns.Count;
        _cache = new object?[n];
        _decoded = new byte[n];
    }

    /// <summary>The owning table (column offsets / encoding source).</summary>
    public DbfTable Table => _table;

    /// <summary>The full raw record bytes, including the leading deletion flag.</summary>
    public ReadOnlySpan<byte> Raw => _buffer;

    /// <summary>True when the record's leading flag byte is <c>0x2A</c> (<c>'*'</c>, deleted).</summary>
    public bool IsDeleted => _buffer.Length > 0 && _buffer[0] == 0x2A;

    /// <summary>
    /// Raw bytes of the field at <paramref name="columnIndex"/>, sliced at
    /// <c>column.Offset + 1 .. + column.Length</c> (the +1 skips the deletion flag).
    /// </summary>
    public ReadOnlySpan<byte> GetRawField(int columnIndex) => GetRawField(_table.Columns[columnIndex]);

    /// <summary>Raw bytes of the given <paramref name="column"/> (see <see cref="GetRawField(int)"/>).</summary>
    public ReadOnlySpan<byte> GetRawField(DbfColumn column)
        => _buffer.AsSpan(column.Offset + 1, column.Length);

    /// <summary>The decoded value of the column at <paramref name="columnIndex"/> (§A5).</summary>
    public object? GetValue(int columnIndex) => this[columnIndex];

    /// <summary>
    /// The decoded value of the column named <paramref name="name"/> (§A5), or
    /// <see langword="null"/> for blank/NULL/unknown-column. Decoded values are
    /// memoized per record instance.
    /// </summary>
    public object? this[string name]
    {
        get
        {
            int index = IndexOfColumn(name);
            return index < 0 ? null : this[index];
        }
    }

    /// <summary>The decoded value of the column at <paramref name="index"/> (§A5).</summary>
    public object? this[int index]
    {
        get
        {
            var columns = _table.Columns;
            if ((uint)index >= (uint)columns.Count)
                return null;

            if (_decoded[index] == Decoded)
                return _cache[index];

            var column = columns[index];
            var value = DecodeColumn(column);
            _cache[index] = value;
            _decoded[index] = Decoded;
            return value;
        }
    }

    /// <summary>
    /// The value VFP's expressions see for the column at <paramref name="index"/>: a character ('C') field at its full
    /// declared width (leading and trailing blanks kept, see <see cref="FieldDecoder.DecodeCharacterPadded"/>), every
    /// other column as <see cref="this[int]"/>. Feeds the expression engine and SQL results; the public indexer keeps
    /// its trimmed value.
    /// </summary>
    internal object? GetExpressionValue(int index)
    {
        var columns = _table.Columns;
        if ((uint)index >= (uint)columns.Count)
            return null;
        var column = columns[index];
        if (column.Type != 'C')
            return this[index];
        // Memoized like the indexer: a filter naming the field twice (a OR chain, a range) decodes it once.
        if (_decoded[index] == ExpressionDecoded)
            return _cache[index];

        object? value = null;
        var (_, nullBit) = _table.GetVarlenNullBits(column);
        if (!(_table.ApplyNullFlags && nullBit >= 0 && IsNullFlagBitSet(nullBit)))
        {
            var raw = GetRawField(column);
            if (!raw.IsEmpty)
            {
                var encoding = column.IsBinary ? System.Text.Encoding.Latin1 : _table.Encoding;
                // VFP reads the declared code page, even when the bytes happen to be valid UTF-8; UTF-8 detection
                // only applies when the table was opened with DbfOptions.DetectUtf8 (non-VFP data).
                value = FieldDecoder.DecodeCharacterPadded(column, raw, encoding, _table.SuppressUtf8Detection);
            }
        }
        _cache[index] = value;
        _decoded[index] = ExpressionDecoded;
        return value;
    }

    /// <summary>The expression value (<see cref="GetExpressionValue(int)"/>) of the column named <paramref name="name"/>.</summary>
    internal object? GetExpressionValue(string name)
    {
        int index = IndexOfColumn(name);
        return index < 0 ? null : GetExpressionValue(index);
    }

    /// <summary>
    /// The value the VFP9 runtime reads for the column at <paramref name="index"/>: <see langword="null"/>
    /// ONLY when the column's <c>_NullFlags</c> bit is set (honoured per <see cref="DbfTable.ApplyNullFlags"/>);
    /// a blank field without a null bit reads as its type's EMPTY value instead of the public indexer's
    /// <see langword="null"/> — <c>0</c> for N/F, the empty date/datetime (<c>default</c>) for D/T,
    /// <c>""</c> for a memo and an empty blob for W. VFP9-pinned (BlankFieldNullBitVfp9OracleTests): a blank
    /// N/F/D/T/M field has ISNULL() .F. and EMPTY() .T. whether or not the column is NULL-able. The public
    /// <see cref="this[int]"/> keeps mapping blank → <see langword="null"/> (the ADO.NET IsDBNull contract);
    /// this accessor is the read the microVFP runtime and its SQL engine use.
    /// </summary>
    internal object? GetVfpValue(int index)
    {
        var value = GetExpressionValue(index);   // a C field at its full width, as VFP reads it.
        if (value is not null) return value;
        var columns = _table.Columns;
        if ((uint)index >= (uint)columns.Count) return null;

        var column = columns[index];
        var (_, nullBit) = _table.GetVarlenNullBits(column);
        if (_table.ApplyNullFlags && nullBit >= 0 && IsNullFlagBitSet(nullBit))
            return null;

        return column.Type switch
        {
            'N' => column.Decimal > 0 ? 0m : 0L,   // the CLR types DecodeNumeric yields for a value.
            'F' => 0d,
            'D' => default(DateOnly),
            'T' or '@' => default(DateTime),
            'M' => string.Empty,
            'W' => Array.Empty<byte>(),
            _ => null,
        };
    }

    /// <summary><see cref="GetVfpValue(int)"/> by column name; <see langword="null"/> for an unknown column.</summary>
    internal object? GetVfpValue(string name)
    {
        int index = IndexOfColumn(name);
        return index < 0 ? null : GetVfpValue(index);
    }

    /// <summary>
    /// Decode one column applying the §A5b VFP rules: a set null bit (when
    /// <see cref="DbfTable.ApplyNullFlags"/>) overrides the value to
    /// <see langword="null"/>; <c>V</c>/<c>Q</c> use the varlen bit for their effective
    /// length; binary (NOCPTRANS) <c>C</c>/<c>M</c> are not code-page transcoded.
    /// </summary>
    private object? DecodeColumn(DbfColumn column)
    {
        var (varlenBit, nullBit) = _table.GetVarlenNullBits(column);

        // NULL override: a set null bit wins over whatever the raw bytes decode to.
        if (_table.ApplyNullFlags && nullBit >= 0 && IsNullFlagBitSet(nullBit))
            return null;

        var raw = GetRawField(column);

        // Memo / Blob: the field holds a POINTER (VFP 4-byte LE block number, or
        // right-justified ASCII digits otherwise), resolved against the table's sidecar
        // memo file (§A6). A 0/blank pointer or a missing memo file → null; a text memo is
        // transcoded via the table Encoding, a binary/NOCPTRANS memo returns raw bytes.
        // 'W' (VFP9 0x32 Blob) is a 4-byte FPT pointer like memo, but always binary (§A5b
        // line 257) — ReadMemoValue returns its raw FPT block bytes, never a transcoded string.
        if (column.Type is 'M' or 'W')
            return _table.ReadMemoValue(column, raw);

        // Varchar / Varbinary: effective length is the full field width unless the
        // varlen bit is set, in which case it lives in the field's LAST byte (§A5b).
        if (column.Type is 'V' or 'Q')
        {
            int effLen = column.Length;
            if (varlenBit >= 0 && IsNullFlagBitSet(varlenBit))
                effLen = raw.Length > 0 ? raw[^1] : 0;
            if (effLen > raw.Length) effLen = raw.Length;
            if (effLen < 0) effLen = 0;

            var slice = raw[..effLen];
            // A binary (NOCPTRANS, flag 0x04) Varchar must NOT be code-page transcoded —
            // the §A7 line 312 bypass is generic to ALL flag-0x04 columns, not just C/M.
            // Decode 1:1 via Latin1 so raw bytes (e.g. 0x80) are preserved verbatim.
            var venc = column.IsBinary ? System.Text.Encoding.Latin1 : _table.Encoding;
            return column.Type == 'V'
                ? venc.GetString(slice) // V → string via encoding (Latin1 when binary)
                : slice.ToArray();      // Q → raw bytes (binary, not transcoded)
        }

        // Binary / NOCPTRANS C/M: decode 1:1 (Latin1) instead of the table code page.
        var encoding = column.IsBinary && column.Type is 'C' or 'M'
            ? System.Text.Encoding.Latin1
            : _table.Encoding;

        // Suppress the UTF-8 auto-detect short-circuit when the table encoding is an
        // explicit override (§A7 highest precedence) so the forced encoding wins even
        // when raw bytes are coincidentally valid UTF-8. A binary C/M column already
        // bypasses transcoding via Latin1, so the flag is moot there.
        return FieldDecoder.Decode(column, raw, encoding, _table.SuppressUtf8Detection);
    }

    /// <summary>
    /// True when <paramref name="column"/>'s NULL bit is set in <c>_NullFlags</c> — a genuine <c>.NULL.</c>,
    /// as opposed to a blank field, which the indexer also decodes to <see langword="null"/>.
    /// </summary>
    internal bool IsNullFlagSet(DbfColumn column)
    {
        var (_, nullBit) = _table.GetVarlenNullBits(column);
        return nullBit >= 0 && IsNullFlagBitSet(nullBit);
    }

    /// <summary>Test bit <paramref name="bit"/> in the <c>_NullFlags</c> bitmap (LSB-first, §A5b).</summary>
    private bool IsNullFlagBitSet(int bit)
    {
        var nf = _table.NullFlagsColumn;
        if (nf is null)
            return false;

        var bytes = GetRawField(nf);
        int byteIndex = bit >> 3;
        if ((uint)byteIndex >= (uint)bytes.Length)
            return false;

        return (bytes[byteIndex] & (1 << (bit & 7))) != 0;
    }

    private int IndexOfColumn(string name)
    {
        var columns = _table.Columns;
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Decoded value as a <see cref="string"/>, or null on mismatch/blank (null-safe coercion).</summary>
    public string? GetString(string name) => this[name] as string;

    /// <summary>Decoded value as an <see cref="int"/>, or null on type mismatch/blank.</summary>
    public int? GetInt32(string name) => this[name] switch
    {
        int i => i,
        long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
        _ => null,
    };

    /// <summary>Decoded value as a <see cref="long"/>, or null on type mismatch/blank.</summary>
    public long? GetInt64(string name) => this[name] switch
    {
        long l => l,
        int i => i,
        _ => null,
    };

    /// <summary>Decoded value as a <see cref="decimal"/>, or null on type mismatch/blank.</summary>
    public decimal? GetDecimal(string name) => this[name] switch
    {
        decimal m => m,
        long l => l,
        int i => i,
        _ => null,
    };

    /// <summary>Decoded value as a <see cref="double"/>, or null on type mismatch/blank.</summary>
    public double? GetDouble(string name) => this[name] switch
    {
        double d => d,
        decimal m => (double)m,
        long l => l,
        int i => i,
        _ => null,
    };

    /// <summary>Decoded value as a <see cref="bool"/>, or null on type mismatch/blank.</summary>
    public bool? GetBoolean(string name) => this[name] as bool?;

    /// <summary>Decoded value as a <see cref="DateOnly"/>, or null on type mismatch/blank.</summary>
    public DateOnly? GetDateOnly(string name) => this[name] as DateOnly?;

    /// <summary>Decoded value as a <see cref="DateTime"/>, or null on type mismatch/blank.</summary>
    public DateTime? GetDateTime(string name) => this[name] as DateTime?;

    /// <summary>All decoded values in physical column order; length equals <c>Table.Columns.Count</c>.</summary>
    public object?[] ToArray()
    {
        var columns = _table.Columns;
        var result = new object?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
            result[i] = this[i];
        return result;
    }

    /// <summary>Decoded values keyed by column name (§A5).</summary>
    public IReadOnlyDictionary<string, object?> ToDictionary()
    {
        var columns = _table.Columns;
        // Case-insensitive to mirror the string indexer / IndexOfColumn (OrdinalIgnoreCase),
        // so a caller can key the dictionary with any case the indexer accepts.
        var dict = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count; i++)
            dict[columns[i].Name] = this[i];
        return dict;
    }
}
