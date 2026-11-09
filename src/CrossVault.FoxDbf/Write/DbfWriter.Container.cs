using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Write;

public sealed partial class DbfWriter
{
    // DBC properties grow in-place until their allocated block is full. CREATE TABLE's
    // PrimaryKey assignment is observable in DCT allocation order, even for empty tables.
    internal void ReplaceContainerProperty(int recordIndex, byte[] value)
    {
        var column = _schema.Columns.Single(c => c.Name.Equals("PROPERTY", StringComparison.OrdinalIgnoreCase));
        Span<byte> pointerBytes = stackalloc byte[4];
        RandomAccess.Read(_handle, pointerBytes, _headerLength + (long)recordIndex * _recordLength + 1 + column.Offset);
        int pointer = BinaryPrimitives.ReadInt32LittleEndian(pointerBytes);
        Span<byte> header = stackalloc byte[8];
        if (_fpt is not null && pointer > 0)
        {
            long offset = (long)pointer * _fptBlockSize;
            RandomAccess.Read(_fpt.SafeFileHandle, header, offset);
            int length = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
            int capacity = ((length + 8 + _fptBlockSize - 1) / _fptBlockSize) * _fptBlockSize - 8;
            if (value.Length <= capacity)
            {
                BinaryPrimitives.WriteInt32BigEndian(header[4..], value.Length);
                _fpt.Position = offset;
                _fpt.Write(header);
                _fpt.Write(value);
                _fpt.Flush();
                return;
            }
        }
        var values = _schema.Columns.Select(c => c == column ? (object)value : KeepValue).ToArray();
        UpdateRecord(recordIndex, values);
    }
}
