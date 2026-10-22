using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// SQL UPDATE / DELETE / INSERT on a DBC member whose field names are longer than the 10-char physical
/// descriptor names. As in VFP9 (pinned by the internal SqlDmlDbcLongNamesVfp9OracleTests), the statement
/// names the fields by their DBC long names — in SET, WHERE, the INSERT column list and as
/// <c>table.long_name</c> — while the truncated physical name of a longer field is unknown (1806).
/// </summary>
public sealed class SqlDmlDbcLongNameTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();
    private readonly string _dbc;

    public SqlDmlDbcLongNameTests()
    {
        _dbc = _dir.File("longdml.dbc");
        DbfDatabaseBuilder.Create(_dbc, new[]
        {
            new DbcTableSpec("customer_accounts", "customer_accounts.dbf", new[]
            {
                new DbfColumnDef("ACCOUNT_NU", 'I', 4),
                new DbfColumnDef("CUSTOMER_N", 'C', 20),
                new DbfColumnDef("BALANCE_AM", 'N', 10, 2),
                new DbfColumnDef("ID", 'I', 4),
                // nullable: the member carries the hidden _NullFlags column, which has no long name
                new DbfColumnDef("REMARK_TEX", 'M', 4) { Nullable = true },
            }, new[] { "account_number", "customer_name", "balance_amount", "id", "remark_text_long" }),
        });
        using var w = DbfWriter.Open(_dir.File("customer_accounts.dbf"));
        w.AppendRecord(1, "Alpha", 10m, 101, "first");
        w.AppendRecord(2, "Beta", 20m, 102, "second");
        w.AppendRecord(3, "Gamma", 30m, 103, "third");
        w.Flush();
    }

    public void Dispose() => _dir.Dispose();

    private VfpSession Open()
    {
        var s = new VfpSession(new EvaluationContext { Deleted = false });
        s.OpenDirectory(_dir.Path);
        s.OpenDatabase(_dbc);
        return s;
    }

    private static string Rows(VfpSession s, string sql)
        => string.Join("|", SqlTestSupport.Materialize(s.Execute(sql)!)
            .Select(r => string.Join(":", r.Select(Cell))));

    private static string Cell(object? v) => v switch
    {
        string str => str.Trim(),
        decimal d => d.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    [Fact]
    public void Update_LongSetAndWhere_WritesTheRow()
    {
        using var s = Open();
        Assert.Equal(1, s.Execute("UPDATE customer_accounts SET customer_name = 'Xavier' WHERE account_number = 1")!.AffectedRecords);
        Assert.Equal(1, s.Execute("UPDATE customer_accounts SET balance_amount = balance_amount + 5 WHERE customer_name = 'Beta'")!.AffectedRecords);
        Assert.Equal(1, s.Execute(
            "UPDATE customer_accounts SET customer_accounts.customer_name = 'Qu' WHERE customer_accounts.account_number = 3")!.AffectedRecords);

        Assert.Equal("1:Xavier:10:first|2:Beta:25:second|3:Qu:30:third",
            Rows(s, "SELECT account_number, customer_name, balance_amount, remark_text_long FROM customer_accounts ORDER BY 1"));
    }

    [Fact]
    public void Update_LongMemoField_WritesTheMemo()
    {
        using var s = Open();
        Assert.Equal(1, s.Execute("UPDATE customer_accounts SET remark_text_long = 'memo!' WHERE id = 102")!.AffectedRecords);
        Assert.Equal("first|memo!|third", Rows(s, "SELECT remark_text_long FROM customer_accounts ORDER BY id"));
    }

    [Fact]
    public void Delete_LongWhere_DeletesTheRow()
    {
        using var s = Open();
        Assert.Equal(1, s.Execute("DELETE FROM customer_accounts WHERE customer_name = 'Gamma'")!.AffectedRecords);
        Assert.Equal("3", Rows(s, "SELECT account_number FROM customer_accounts WHERE DELETED()"));
    }

    [Fact]
    public void Insert_LongColumnList_AppendsTheRow()
    {
        using var s = Open();
        Assert.Equal(1, s.Execute("INSERT INTO customer_accounts (account_number, customer_name) VALUES (4, 'Delta')")!.AffectedRecords);
        Assert.Equal("4:Delta", Rows(s, "SELECT account_number, customer_name FROM customer_accounts WHERE account_number = 4"));
    }

    [Theory]
    [InlineData("UPDATE customer_accounts SET customer_n = 'Z' WHERE account_number = 2")]
    [InlineData("UPDATE customer_accounts SET id = 5 WHERE customer_n = 'Beta'")]
    [InlineData("DELETE FROM customer_accounts WHERE balance_am > 0")]
    public void PhysicalNameOfALongField_IsUnknown(string sql)
    {
        using var s = Open();
        var ex = Assert.Throws<FoxDbfSqlException>(() => s.Execute(sql));
        Assert.Equal(1806, ex.VfpErrorNumber);
        Assert.Equal("Alpha|Beta|Gamma", Rows(s, "SELECT customer_name FROM customer_accounts ORDER BY 1"));
    }

    [Fact]
    public void InsertColumnList_PhysicalNameOfALongField_IsError12()
    {
        // VFP9 treats an unknown INSERT column as an undefined variable (error 12), not 1806
        // (pinned by the internal MicroVfpDbcLongNamesVfp9OracleTests).
        using var s = Open();
        var ex = Assert.Throws<FoxDbfSqlException>(() =>
            s.Execute("INSERT INTO customer_accounts (account_number, customer_n) VALUES (9, 'Short')"));
        Assert.Equal(12, ex.VfpErrorNumber);
        Assert.Equal("Alpha|Beta|Gamma", Rows(s, "SELECT customer_name FROM customer_accounts ORDER BY 1"));
    }

    [Fact]
    public void OpenWorkAreaUnderAlias_KeepsItsLongNamesAcrossTheWrite()
    {
        using var s = Open();
        s.Use("customer_accounts", inArea: 0, alias: "ca");
        Assert.Equal(1, s.Execute("UPDATE ca SET customer_name = 'ViaAlias' WHERE account_number = 2")!.AffectedRecords);
        Assert.Equal(1, s.Execute("DELETE FROM ca WHERE customer_name = 'Alpha'")!.AffectedRecords);

        // The work area is re-opened after the write: it still reads under the long names.
        Assert.Equal("2:ViaAlias", Rows(s, "SELECT account_number, customer_name FROM ca WHERE customer_name = 'ViaAlias'"));
        Assert.Equal("1", Rows(s, "SELECT account_number FROM ca WHERE DELETED()"));
    }

    [Fact]
    public void Transaction_WritesThePrivateCopyUnderTheLongNames()
    {
        using var conn = new FoxDbfConnection($"Data Source={_dbc}");
        conn.Open();
        using (var tx = conn.BeginTransaction())
        {
            Assert.Equal(1, Exec(conn, "UPDATE customer_accounts SET customer_name = 'InTx' WHERE account_number = 1"));
            Assert.Equal(1, Exec(conn, "UPDATE customer_accounts SET balance_amount = 99 WHERE customer_name = 'InTx'"));
            Assert.Equal(99m, Convert.ToDecimal(Scalar(conn, "SELECT balance_amount FROM customer_accounts WHERE account_number = 1")));
            tx.Commit();
        }
        Assert.Equal("InTx", ((string)Scalar(conn, "SELECT customer_name FROM customer_accounts WHERE balance_amount = 99")!).Trim());
    }

    [Fact]
    public void LongNameWriter_RefusesToAlter_AndLeavesThePhysicalNames()
    {
        var longNames = new[] { "account_number", "customer_name", "balance_amount", "id", "remark_text_long" };
        string path = _dir.File("customer_accounts.dbf");
        using (var w = DbfWriter.Open(path, new DbfOptions(), cols => DbfDatabase.ApplyLongNames(cols, longNames)))
        {
            Assert.Equal(longNames, w.Schema.Columns.Select(c => c.Name));
            Assert.Throws<InvalidOperationException>(() => w.AddColumn(new DbfColumnDef("EXTRA", 'I', 4)));
        }
        using var t = DbfTable.Open(path);
        Assert.Equal(new[] { "ACCOUNT_NU", "CUSTOMER_N", "BALANCE_AM", "ID", "REMARK_TEX" }, t.Columns.Select(c => c.Name));
    }

    private static int Exec(FoxDbfConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(FoxDbfConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
