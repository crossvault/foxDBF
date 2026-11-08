using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// A forward-only reader over a SELECT <c>SqlResult</c> row stream. The CLR type of each column is
/// deterministic from the DBF field type (unlike SQLite): C→string, N/F→decimal or double, I→int,
/// Y→decimal, B→double, D→DateTime (DateOnly via <c>GetFieldValue&lt;DateOnly&gt;</c>), T→DateTime,
/// L→bool, M→string, G/P/Q/W→byte[], V→string.
/// </summary>
public sealed class FoxDbfDataReader : DbDataReader
{
    private SqlResult _result;
    private readonly IReadOnlyList<SqlResult>? _results;   // multi-result (batch) backing; null = single result
    private int _resultIndex;                               // current index into _results
    private readonly FoxDbfConnection _connection;
    private readonly CommandBehavior _behavior;
    private IEnumerator<object?[]>? _enumerator;
    private object?[]? _currentRow;
    private object?[]? _firstRow;
    private bool _firstRowPending;
    private bool _hasRows;
    private int _rowsReturned;
    private bool _disposed;
    private bool _closed;
    private int _recordsAffected = -1;

    internal FoxDbfDataReader(SqlResult result, FoxDbfConnection connection, CommandBehavior behavior)
    {
        _result = result;
        _connection = connection;
        _behavior = behavior;
        InitResult();
    }

    /// <summary>Multi-result-set constructor used by <see cref="FoxDbfBatch"/>: the reader exposes the
    /// FIRST result set and <see cref="NextResult"/> walks forward through the rest (one per batch command).
    /// The list must hold at least one result.</summary>
    internal FoxDbfDataReader(IReadOnlyList<SqlResult> results, FoxDbfConnection connection, CommandBehavior behavior)
    {
        _results = results;
        _resultIndex = 0;
        _result = results[0];
        _connection = connection;
        _behavior = behavior;
        InitResult();
    }

    /// <summary>Prime the row-enumeration state for <see cref="_result"/>. Eagerly peeks the first row so
    /// <see cref="HasRows"/> is accurate BEFORE the first <see cref="Read"/> (and reports false for an empty
    /// set); the peeked row is cached and returned by the first <see cref="Read"/>. SchemaOnly never
    /// enumerates rows, so HasRows is always false there. Re-runnable so <see cref="NextResult"/> can reset
    /// onto the next result set.</summary>
    private void InitResult()
    {
        _enumerator = null;
        _currentRow = null;
        _firstRow = null;
        _firstRowPending = false;
        _hasRows = false;
        _rowsReturned = 0;

        if ((_behavior & CommandBehavior.SchemaOnly) == 0)
        {
            _enumerator = _result.Rows.GetEnumerator();
            _hasRows = _enumerator.MoveNext();
            if (_hasRows) _firstRow = _enumerator.Current;
            _firstRowPending = true;
        }
    }

    public override int Depth => 0;

    public override int FieldCount => _result.Columns.Count;

    public override bool HasRows
    {
        get
        {
            if (_closed || _disposed) return false;
            // SchemaOnly never yields rows; otherwise return the flag cached at construction.
            if ((_behavior & CommandBehavior.SchemaOnly) != 0) return false;
            return _hasRows;
        }
    }

    public override bool IsClosed => _closed || _disposed;

    public override int RecordsAffected => _recordsAffected;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => this[GetOrdinal(name)];

    public override bool Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closed) return false;

        // SchemaOnly behavior: don't read any rows
        if ((_behavior & CommandBehavior.SchemaOnly) != 0)
        {
            _closed = true;
            return false;
        }

        // SingleRow: stop after exactly one row has been returned.
        if ((_behavior & CommandBehavior.SingleRow) != 0 && _rowsReturned >= 1)
        {
            _closed = true;
            return false;
        }

        bool advanced;
        if (_firstRowPending)
        {
            // Return the row peeked at construction for HasRows.
            _firstRowPending = false;
            advanced = _hasRows;
            if (advanced) _currentRow = _firstRow;
        }
        else
        {
            advanced = MoveNext();
        }

        if (advanced)
        {
            _rowsReturned++;
            return true;
        }

        _closed = true;
        return false;
    }

    private bool MoveNext()
    {
        if (_enumerator == null) return false;
        if (_enumerator.MoveNext())
        {
            _currentRow = _enumerator.Current;
            return true;
        }
        return false;
    }

    public override string GetName(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        return _result.Columns[ordinal].Name;
    }

    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < _result.Columns.Count; i++)
        {
            if (string.Equals(_result.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new IndexOutOfRangeException($"Column '{name}' not found.");
    }

    public override string GetDataTypeName(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        return _result.Columns[ordinal].VfpType.ToString();
    }

    public override Type GetFieldType(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _result.Columns.Count)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        var clrType = _result.Columns[ordinal].ClrType;
        // For ADO.NET compatibility, report D (Date) columns as DateTime (not DateOnly),
        // even though the executor returns DateOnly. This allows ADO.NET code to see a
        // consistent DateTime type for date fields (like other databases do), while still
        // supporting DateOnly via GetFieldValue<DateOnly>conversion.
        return clrType == typeof(DateOnly) ? typeof(DateTime) : clrType;
    }

    public override bool IsDBNull(int ordinal)
    {
        if (_currentRow == null) throw new InvalidOperationException("No current row.");
        if (ordinal < 0 || ordinal >= _currentRow.Length)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        return _currentRow[ordinal] == null;
    }

    public override object GetValue(int ordinal)
    {
        if (_currentRow == null) throw new InvalidOperationException("No current row.");
        if (ordinal < 0 || ordinal >= _currentRow.Length)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");
        return Normalize(_currentRow[ordinal]);
    }

    public override int GetValues(object[] values)
    {
        if (_currentRow == null) throw new InvalidOperationException("No current row.");
        int count = Math.Min(values.Length, _currentRow.Length);
        for (int i = 0; i < count; i++)
            values[i] = Normalize(_currentRow[i]);
        return count;
    }

    // ADO.NET requires GetValue/GetValues/this[] to return an instance of GetFieldType. The executor
    // hands back DateOnly for D columns, but GetFieldType (and GetSchemaTable) report DateTime, so we
    // box DateOnly as DateTime here. GetDateTime / GetFieldValue<DateOnly> handle the raw value directly.
    private static object Normalize(object? raw)
        => raw switch
        {
            null => DBNull.Value,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            _ => raw,
        };

    private static InvalidCastException NullCast(int ordinal)
        => new($"Column {ordinal} is NULL. Use IsDBNull or GetValue to read a NULL value.");

    public override bool GetBoolean(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is bool b) return b;
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to bool.");
    }

    public override byte GetByte(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is byte b) return b;
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to byte.");
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) return 0;
        if (val is byte[] ba)
        {
            if (buffer == null) return ba.LongLength;
            if (dataOffset < 0) throw new ArgumentOutOfRangeException(nameof(dataOffset));
            long available = ba.LongLength - dataOffset;
            if (available <= 0) return 0;
            long toCopy = Math.Min((long)length, available);
            if (toCopy <= 0) return 0;
            Array.Copy(ba, dataOffset, buffer, bufferOffset, toCopy);
            return toCopy;
        }
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to byte[].");
    }

    public override char GetChar(int ordinal)
    {
        var s = GetString(ordinal);
        return s.Length > 0 ? s[0] : '\0';
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = GetString(ordinal);
        if (buffer == null) return s.Length;
        if (dataOffset < 0) throw new ArgumentOutOfRangeException(nameof(dataOffset));
        long available = (long)s.Length - dataOffset;
        if (available <= 0) return 0;
        long toCopy = Math.Min((long)length, available);
        if (toCopy <= 0) return 0;
        s.CopyTo((int)dataOffset, buffer, bufferOffset, (int)toCopy);
        return toCopy;
    }

    public override DateTime GetDateTime(int ordinal)
    {
        if (_currentRow == null) throw new InvalidOperationException("No current row.");
        if (ordinal < 0 || ordinal >= _currentRow.Length)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is out of range.");

        var val = _currentRow[ordinal];
        if (val == null) throw NullCast(ordinal);
        if (val is DateTime dt) return dt;
        if (val is DateOnly d) return d.ToDateTime(TimeOnly.MinValue);
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to DateTime.");
    }

    public override decimal GetDecimal(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is decimal d) return d;
        if (val is double db) return Convert.ToDecimal(db);
        if (val is int i) return i;
        if (val is long l) return l;
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to decimal.");
    }

    public override double GetDouble(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is double d) return d;
        if (val is decimal de) return Convert.ToDouble(de);
        if (val is int i) return i;
        if (val is long l) return l;
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to double.");
    }

    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);

    public override Guid GetGuid(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is Guid g) return g;
        if (val is string s) return Guid.Parse(s);
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to Guid.");
    }

    public override short GetInt16(int ordinal) => (short)GetInt32(ordinal);

    public override int GetInt32(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (val is decimal d) return (int)d;
        if (val is double db) return (int)db;
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to int.");
    }

    public override long GetInt64(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is long l) return l;
        if (val is int i) return i;
        if (val is decimal d) return (long)d;
        if (val is double db) return (long)db;
        throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to long.");
    }

    public override string GetString(int ordinal)
    {
        var val = GetValue(ordinal);
        if (val is DBNull) throw NullCast(ordinal);
        if (val is string s) return s;
        return val?.ToString() ?? "";
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var val = GetValue(ordinal);
        // ADO.NET contract: a NULL value must throw for a typed getter, even for non-nullable T.
        if (val is DBNull) throw NullCast(ordinal);
        if (val is T t) return t;

        // Special handling for DateOnly (D columns surface as DateTime via GetValue/Normalize).
        if (typeof(T) == typeof(DateOnly) && val is DateTime dt)
            return (T)(object)DateOnly.FromDateTime(dt);

        try { return (T?)Convert.ChangeType(val, typeof(T)) ?? default!; }
        catch { throw new InvalidCastException($"Cannot cast {val?.GetType()?.Name ?? "null"} to {typeof(T).Name}."); }
    }

    public override bool NextResult()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Single-result reader (the common command path): there is never a second set.
        if (_results is null) return false;
        // Walked off the last batch command's result set.
        if (_resultIndex + 1 >= _results.Count)
        {
            _closed = true;
            return false;
        }
        _resultIndex++;
        _result = _results[_resultIndex];
        _closed = false;          // a fresh set re-opens the reader after the previous set was drained.
        InitResult();
        return true;
    }

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this);
    }

    public override DataTable? GetSchemaTable()
    {
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add("ColumnName", typeof(string));
        schema.Columns.Add("ColumnOrdinal", typeof(int));
        schema.Columns.Add("ColumnSize", typeof(int));
        schema.Columns.Add("NumericPrecision", typeof(int));
        schema.Columns.Add("NumericScale", typeof(int));
        schema.Columns.Add("DataType", typeof(Type));
        schema.Columns.Add("DataTypeName", typeof(string));
        schema.Columns.Add("IsLong", typeof(bool));
        schema.Columns.Add("AllowDBNull", typeof(bool));

        for (int i = 0; i < _result.Columns.Count; i++)
        {
            var col = _result.Columns[i];
            var row = schema.NewRow();
            row["ColumnName"] = col.Name;
            row["ColumnOrdinal"] = i;
            row["ColumnSize"] = FoxDbfColumnMetadata.ColumnSize(col.VfpType, col.Length);
            row["NumericPrecision"] = col.Decimals > 0 ? col.Length - col.Decimals : col.Length;
            row["NumericScale"] = col.Decimals;
            // Match GetFieldType: D columns surface as DateTime (not the executor's DateOnly).
            row["DataType"] = col.ClrType == typeof(DateOnly) ? typeof(DateTime) : col.ClrType;
            row["DataTypeName"] = col.VfpType.ToString();
            row["IsLong"] = FoxDbfColumnMetadata.IsLong(col.VfpType);
            row["AllowDBNull"] = true;
            schema.Rows.Add(row);
        }

        return schema;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _enumerator?.Dispose();
            _connection.SetActiveReader(null);

            if ((_behavior & CommandBehavior.CloseConnection) != 0)
            {
                try { _connection.Close(); }
                catch { }
            }
        }

        base.Dispose(disposing);
    }

}
