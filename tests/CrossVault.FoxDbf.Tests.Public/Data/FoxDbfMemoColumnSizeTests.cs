using System;
using System.Data;
using System.Linq;
using CrossVault.FoxDbf.Data;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// Schema metadata for long (memo/blob) columns — crossvault/foxDBF#3: <c>DataTable.Load</c> gave a Memo
/// column <c>MaxLength = 4</c>, the width of the memo-block pointer in the record, because
/// <c>GetSchemaTable</c> reported the field width as <c>ColumnSize</c>.
///
/// Expected values are those the Visual FoxPro OLE DB provider (VFPOLEDB.1) reports for the same table
/// (probed with <c>OleDbDataReader.GetSchemaTable</c>, <c>DataTable.Load</c> and
/// <c>OleDbConnection.GetSchema("Columns")</c>):
/// M/G/W → ColumnSize 2147483647, IsLong true, CHARACTER_MAXIMUM_LENGTH 2147483647;
/// V/Q/C → ColumnSize = field width, IsLong false; numeric types → no CHARACTER_MAXIMUM_LENGTH.
/// </summary>
public sealed class FoxDbfMemoColumnSizeTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();
    private static readonly string LongText = new('x', 5000);

    public void Dispose() => _dir.Dispose();

    private FoxDbfConnection OpenWithTable()
    {
        var c = new FoxDbfConnection($"Data Source={_dir.Path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (ID I, NOTES M, PIC G, BLB W, VC V(10), VB Q(10), CH C(8), NUM N(10,2))";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"INSERT INTO t (ID, NOTES, VC, CH, NUM) VALUES (1, '{LongText}', 'abc', 'abc', 1.5)";
        cmd.ExecuteNonQuery();
        return c;
    }

    private static DataRow SchemaRow(DataTable schema, string column)
        => schema.Rows.Cast<DataRow>().Single(r =>
            string.Equals((string)r["ColumnName"], column, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void DataTableLoad_MemoColumn_HasNoFourCharacterLimit()
    {
        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID, NOTES, VC, CH FROM t";
        using var reader = cmd.ExecuteReader();

        var table = new DataTable();
        table.Load(reader);

        Assert.Equal(int.MaxValue, table.Columns["NOTES"]!.MaxLength);
        Assert.Equal(10, table.Columns["VC"]!.MaxLength);
        Assert.Equal(8, table.Columns["CH"]!.MaxLength);
        Assert.Equal(LongText, ((string)table.Rows[0]["NOTES"]).TrimEnd());

        // Editing the loaded memo must not trip a MaxLength constraint either.
        table.Rows[0]["NOTES"] = LongText + "more";
    }

    [Fact]
    public void DataAdapterFill_WithKeyInfo_MemoColumnHasNoFourCharacterLimit()
    {
        using var conn = OpenWithTable();
        using var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = "SELECT ID, NOTES FROM t";
        var adapter = new FoxDbfDataAdapter(select) { MissingSchemaAction = MissingSchemaAction.AddWithKey };

        var table = new DataTable();
        adapter.Fill(table);

        Assert.Equal(int.MaxValue, table.Columns["NOTES"]!.MaxLength);
        Assert.Equal(LongText, ((string)table.Rows[0]["NOTES"]).TrimEnd());
    }

    [Theory]
    [InlineData("NOTES", int.MaxValue, true)]
    [InlineData("PIC", int.MaxValue, true)]
    [InlineData("BLB", int.MaxValue, true)]
    [InlineData("VC", 10, false)]
    [InlineData("VB", 10, false)]
    [InlineData("CH", 8, false)]
    public void GetSchemaTable_ReportsVfpOleDbColumnSizeAndIsLong(string column, int size, bool isLong)
    {
        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM t";
        using var reader = cmd.ExecuteReader();

        var row = SchemaRow(reader.GetSchemaTable()!, column);

        Assert.Equal(size, Convert.ToInt32(row["ColumnSize"]));
        Assert.Equal(isLong, (bool)row["IsLong"]);
    }

    [Theory]
    [InlineData("NOTES", int.MaxValue)]
    [InlineData("PIC", int.MaxValue)]
    [InlineData("BLB", int.MaxValue)]
    [InlineData("VC", 10)]
    [InlineData("VB", 10)]
    [InlineData("CH", 8)]
    [InlineData("ID", null)]
    [InlineData("NUM", null)]
    public void GetSchemaColumns_ReportsVfpOleDbCharacterMaximumLength(string column, int? expected)
    {
        using var conn = OpenWithTable();

        var cols = conn.GetSchema("Columns", new[] { null, null, "t", null });
        var row = cols.Rows.Cast<DataRow>().Single(r =>
            string.Equals((string)r["COLUMN_NAME"], column, StringComparison.OrdinalIgnoreCase));

        object max = row["CHARACTER_MAXIMUM_LENGTH"];
        object octet = row["CHARACTER_OCTET_LENGTH"];
        if (expected is null)
        {
            Assert.Equal(DBNull.Value, max);
            Assert.Equal(DBNull.Value, octet);
        }
        else
        {
            Assert.Equal(expected.Value, Convert.ToInt32(max));
            Assert.Equal(expected.Value, Convert.ToInt32(octet));
        }
    }
}
