using System.Buffers.Binary;
using System.Data;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class FieldEncoderFinal5Tests
{
    [Fact]
    public void AdoArrayOverflowCarries39AndKeepsRow()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ado_overflow_final5");
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        connection.Interpreter.Execute("CREATE TABLE t FREE (n N(3),tail I)\nDIMENSION aa[2]\naa[1]=12345\naa[2]=7");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t FROM ARRAY aa";
        System.Data.Common.DbException error = Assert.Throws<FoxDbfException>(() => command.ExecuteNonQuery());
        Assert.Equal(39, ((FoxDbfException)error).VfpErrorNumber);
        command.CommandText = "SELECT tail FROM t";
        Assert.Equal(7, Convert.ToInt32(command.ExecuteScalar()));
        Assert.Equal("***", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(dir.File("t.dbf")), 361, 3));
    }

    [Fact]
    public void WriterAndAdoSqlKeepNumericStarsWithoutError()
    {
        using var dir = new MicroVfpTestSupport.TempDir("writer_overflow_final5");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("n", 'N', 3)]))
            writer.AppendRecord(new object?[] { 12345 });
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (12345)";
        Assert.Equal(1, command.ExecuteNonQuery());
        command.CommandText = "UPDATE t SET n=12345";
        Assert.Equal(2, command.ExecuteNonQuery());
        var bytes = File.ReadAllBytes(dir.File("t.dbf"));
        int header = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        Assert.Equal("***", System.Text.Encoding.ASCII.GetString(bytes, header + 1, 3));
        Assert.Equal("***", System.Text.Encoding.ASCII.GetString(bytes, header + 5, 3));
    }

    public static IEnumerable<object[]> Numbers => new object[] { (short)999, (byte)99, (sbyte)99, 999u, (ushort)999, 999ul, "999" }
        .Select(v => new object[] { v, Convert.ToDecimal(v) == 99 ? "99.0" : " 999" });

    [Theory]
    [MemberData(nameof(Numbers))]
    public void NumericClrTypesReduceScale(object value, string expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("encoder_final5");
        using var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("n", 'N', 4, 2)]);
        Span<byte> bytes = stackalloc byte[4];
        FieldEncoder.Encode(writer.Schema.Columns[0], value, writer.Schema.Encoding, bytes);
        Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Theory]
    [InlineData(0.00015, 2L)]
    [InlineData(1.00005, 10001L)]
    [InlineData(2.00025, 20003L)]
    [InlineData(-0.00015, -2L)]
    public void DoubleCurrencyStoredBytes(double value, long expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("currency_final5");
        using var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("y", 'Y')]);
        Span<byte> bytes = stackalloc byte[8];
        FieldEncoder.Encode(writer.Schema.Columns[0], value, writer.Schema.Encoding, bytes);
        Assert.Equal(expected, BinaryPrimitives.ReadInt64LittleEndian(bytes));
    }

    [Fact]
    public void AdoInt16ReducesScale()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ado_final5");
        using (var writer = DbfWriter.Create(dir.File("t.dbf"), [new DbfColumnDef("n", 'N', 4, 2)])) { }
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (@v)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "v";
        parameter.DbType = DbType.Int16;
        parameter.Value = (short)999;
        command.Parameters.Add(parameter);
        Assert.Equal(1, command.ExecuteNonQuery());
        command.CommandText = "SELECT n FROM t";
        Assert.Equal(999m, Convert.ToDecimal(command.ExecuteScalar()));
    }
}
