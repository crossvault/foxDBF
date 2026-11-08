namespace CrossVault.FoxDbf.Data;

/// <summary>
/// Column-size metadata shared by <see cref="FoxDbfDataReader.GetSchemaTable"/> and
/// <c>GetSchema("Columns")</c>, matching what the Visual FoxPro OLE DB provider (VFPOLEDB) reports.
/// A memo-style field (M, G, P, W) stores only a block pointer in the record, so its field width
/// (4, or 10 in dBase III) says nothing about the value: such columns are "long" and report
/// <see cref="int.MaxValue"/>. Varchar/Varbinary (V, Q) are bounded by their declared width.
/// </summary>
internal static class FoxDbfColumnMetadata
{
    /// <summary>True for the memo-backed types VFPOLEDB flags as <c>IsLong</c>.</summary>
    public static bool IsLong(char vfpType) => vfpType is 'M' or 'G' or 'P' or 'W';

    /// <summary>True for types that carry a character/octet length (C, V, Q and the long types).</summary>
    public static bool HasCharacterLength(char vfpType) => vfpType is 'C' or 'V' or 'Q' || IsLong(vfpType);

    /// <summary>The ADO.NET <c>ColumnSize</c>: <see cref="int.MaxValue"/> for long types, else the field width.</summary>
    public static int ColumnSize(char vfpType, int fieldLength) => IsLong(vfpType) ? int.MaxValue : fieldLength;
}
