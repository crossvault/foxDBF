using System.Buffers.Binary;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class FieldEncoderFinal6Tests
{
    [Theory]
    [InlineData(300000000000d, 3000000000000000L)]
    [InlineData(-300000000000d, -3000000000000000L)]
    [InlineData(250000000000.25d, 2500000000002500L)]
    public void LargeDoubleCurrencyBytes(double value, long scaled)
    {
        using var dir = new MicroVfpTestSupport.TempDir("final6_currency");
        using var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("y", 'Y')]);
        Span<byte> bytes = stackalloc byte[8];
        FieldEncoder.Encode(writer.Schema.Columns[0], value, writer.Schema.Encoding, bytes);
        Assert.Equal(scaled, BinaryPrimitives.ReadInt64LittleEndian(bytes));
        Assert.Equal(scaled / 10000m, VfpValue.Number(value).ToCurrencyNumber());
    }

    [Theory]
    [InlineData("INSERT INTO t (y) VALUES ($922337203685477+$1)")]
    [InlineData("UPDATE t SET y=y*1000000000000")]
    public void AdoExpressionOverflowIsDbException(string sql)
    {
        using var dir = new MicroVfpTestSupport.TempDir("final6_ado");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("y", 'Y')]))
            writer.AppendRecord(1000m);
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var error = Assert.Throws<FoxDbfException>(() => command.ExecuteNonQuery());
        Assert.Equal(1988, error.VfpErrorNumber);
        Assert.IsType<ExpressionException>(error.InnerException);
    }

    [Fact]
    public void AdoSqlErrorKeepsExistingContract()
    {
        using var dir = new MicroVfpTestSupport.TempDir("final6_sql_error");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("n", 'N', 8, 3)])) { }
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(DISTINCT n) FROM t";
        Assert.Equal(1819, Assert.Throws<FoxDbfSqlException>(() => command.ExecuteScalar()).VfpErrorNumber);
    }

    public static IEnumerable<object[]> ClrNumbers => new object[] { (sbyte)5, (ushort)5, 5u, 5ul, "5", "1e5" }
        .Select(value => new object[] { value, value is string text && text == "1e5" ? 0m : 5m });

    [Theory]
    [MemberData(nameof(ClrNumbers))]
    public void ClrNormalizationMatchesNumericFields(object value, decimal expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("final6_clr");
        using var writer = DbfWriter.Create(dir.File("t.dbf"),
            [new DbfColumnDef("y", 'Y'), new DbfColumnDef("b", 'B'), new DbfColumnDef("n", 'N', 3)]);
        Span<byte> bytes = stackalloc byte[8];
        FieldEncoder.Encode(writer.Schema.Columns[0], value, writer.Schema.Encoding, bytes);
        Assert.Equal(expected * 10000m, BinaryPrimitives.ReadInt64LittleEndian(bytes));
        FieldEncoder.Encode(writer.Schema.Columns[1], value, writer.Schema.Encoding, bytes);
        Assert.Equal((double)expected, BinaryPrimitives.ReadDoubleLittleEndian(bytes));
        Assert.False(FieldEncoder.NumericOverflow(writer.Schema.Columns[2], value, writer.Schema.Encoding));
    }

    [Fact]
    public void UnsignedNumericOverflowIsDetected()
    {
        using var dir = new MicroVfpTestSupport.TempDir("final6_uint");
        using var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("n", 'N', 3)]);
        Assert.True(FieldEncoder.NumericOverflow(writer.Schema.Columns[0], 12345u, writer.Schema.Encoding));
    }

    [Fact]
    public void ImportRowToleratesCaseInsensitiveDuplicateNames()
    {
        using var dir = new MicroVfpTestSupport.TempDir("final6_import");
        using var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("n", 'N', 8, 3)]);
        var type = typeof(VfpSession).Assembly.GetType("CrossVault.FoxDbf.Sql.DmlExecutor+ImportRow")!;
        var column = writer.Schema.Columns[0];
        var row = (IVfpValueRow)Activator.CreateInstance(type, new[] { column, column },
            new Dictionary<string, object?> { ["n"] = 1.234m }, 0)!;
        Assert.Equal(1.234m, row.GetValue("n").AsNumber);
        Assert.Equal(3, row.GetValue("N").DisplayDecimals);
    }
}
