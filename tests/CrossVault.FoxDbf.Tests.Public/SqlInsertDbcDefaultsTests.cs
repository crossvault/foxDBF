using System;
using System.IO;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// A microVFP <c>INSERT</c> into a DBC member fills each column it omits from the field's DEFAULT
/// (<c>DBGETPROP(…, 'Field', 'DefaultValue')</c>), whatever <c>SET NULL</c> says, once per row and with the
/// target as the current work area — as VFP9 does (oracle: SqlInsertOmittedColumnsVfp9OracleTests).
/// TasTrade's <c>orders</c> carries constant, built-in and stored-procedure DEFAULTs: <c>order_id = newid()</c>
/// (the counter of <c>ALIAS()</c>, so the target must be current) and <c>order_number = newid("order_number")</c>
/// each advance a counter in <c>setup</c>; <c>order_date = DATE()</c>, <c>deliver_by = DATE()+7</c>,
/// <c>discount = 0</c>, <c>employee_id = defaultemployee()</c>. The raw SQL path
/// (<see cref="VfpSession.Execute(string)"/>, ADO.NET without <c>EnforceRules</c>) does not evaluate DEFAULTs.
/// Rows are read from the .dbf by their physical (10-character) field names. Every case runs on a temp copy.
/// </summary>
public sealed class SqlInsertDbcDefaultsTests
{
    private static string TastradeCopy(MicroVfpTestSupport.TempDir dir)
        => MicroVfpTestSupport.CopyDatabase(Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData"), dir);

    private static VfpInterpreter Interpreter(string copy, out VfpSession session)
    {
        session = new VfpSession();
        session.OpenDatabase(Path.Combine(copy, "tastrade.dbc"));
        var vfp = new VfpInterpreter(session);
        vfp.LoadFile(MicroVfpTestSupport.TastradeSp);
        return vfp;
    }

    /// <summary>The <c>setup</c> counter of <paramref name="key"/> (ORDERS / ORDER_NUMBER).</summary>
    private static int Counter(string copy, string key)
    {
        using var t = DbfTable.Open(Path.Combine(copy, "setup.dbf"), new DbfOptions { LockMode = LockMode.Shared });
        for (int i = 0; i < t.RecordCount; i++)
        {
            var r = t.GetRecord(i)!.Value;
            if (string.Equals(r["KEY_NAME"]?.ToString()?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                return int.Parse(r["VALUE"]!.ToString()!.Trim());
        }
        throw new InvalidOperationException("no setup key " + key);
    }

    private static (int Count, DbfRecord Last) LastOrder(string copy)
    {
        using var t = DbfTable.Open(Path.Combine(copy, "orders.dbf"), new DbfOptions { LockMode = LockMode.Shared });
        return (t.RecordCount, t.GetRecord(t.RecordCount - 1)!.Value);
    }

    private static string Text(DbfRecord r, string field) => r[field]?.ToString()?.Trim() ?? "";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // the target already open in a work area (and not the current one)
    public void MicroVfp_Insert_FillsOmittedColumnsFromDbcDefaults(bool targetOpen)
    {
        using var dir = new MicroVfpTestSupport.TempDir("ins_dbc_default");
        string copy = TastradeCopy(dir);
        int orders = Counter(copy, "ORDERS"), numbers = Counter(copy, "ORDER_NUMBER");
        int before = LastOrder(copy).Count;

        var vfp = Interpreter(copy, out var session);
        using (session)
        {
            if (targetOpen) vfp.Execute("USE tastrade!orders IN 0\nUSE tastrade!customer IN 0\nSELECT customer");
            vfp.Execute("INSERT INTO orders (shipper_id) VALUES ('ZZ')\nCLOSE TABLES ALL");
        }

        var (count, row) = LastOrder(copy);
        Assert.Equal(before + 1, count);
        Assert.Equal("ZZ", Text(row, "SHIPPER_ID"));
        Assert.Equal(orders.ToString(), Text(row, "ORDER_ID"));          // newid() saw ALIAS() = ORDERS
        Assert.Equal(numbers.ToString(), Text(row, "ORDER_NUMB"));
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), row["ORDER_DATE"]);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today).AddDays(7), row["DELIVER_BY"]);
        Assert.Equal(0m, Convert.ToDecimal(row["DISCOUNT"]));
        Assert.NotEqual("", Text(row, "EMPLOYEE_I"));                    // defaultemployee()

        // Each newid() DEFAULT ran exactly once: both counters advanced by one.
        Assert.Equal(orders + 1, Counter(copy, "ORDERS"));
        Assert.Equal(numbers + 1, Counter(copy, "ORDER_NUMBER"));
    }

    [Fact]
    public void MicroVfp_SetNullOn_DefaultsStopAtTheFirstOmittedNotNullField()
    {
        // orders: order_id (DEFAULT newid()) → customer_id (NOT NULL, no DEFAULT) → … → order_number
        // (DEFAULT newid("order_number")). VFP9 walks the fields in order: order_id's DEFAULT runs, customer_id
        // raises 1581, and order_number's DEFAULT never runs; no row lands.
        using var dir = new MicroVfpTestSupport.TempDir("ins_dbc_default_1581");
        string copy = TastradeCopy(dir);
        int orders = Counter(copy, "ORDERS"), numbers = Counter(copy, "ORDER_NUMBER");
        int before = LastOrder(copy).Count;

        var vfp = Interpreter(copy, out var session);
        using (session)
            vfp.Execute("SET NULL ON\nINSERT INTO orders (shipper_id) VALUES ('ZZ')\nCLOSE TABLES ALL");

        Assert.Equal(before, LastOrder(copy).Count);
        Assert.Equal(orders + 1, Counter(copy, "ORDERS"));
        Assert.Equal(numbers, Counter(copy, "ORDER_NUMBER"));
    }

    [Fact]
    public void MicroVfp_Insert_SuppliedColumn_DoesNotEvaluateItsDefault()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ins_dbc_default_given");
        string copy = TastradeCopy(dir);
        int orders = Counter(copy, "ORDERS"), numbers = Counter(copy, "ORDER_NUMBER");

        var vfp = Interpreter(copy, out var session);
        using (session)
            vfp.Execute("INSERT INTO orders (shipper_id, order_numb) VALUES ('ZZ', 'N1')\nCLOSE TABLES ALL");

        Assert.Equal("N1", Text(LastOrder(copy).Last, "ORDER_NUMB"));
        Assert.Equal(numbers, Counter(copy, "ORDER_NUMBER"));    // its DEFAULT never ran
        Assert.Equal(orders + 1, Counter(copy, "ORDERS"));       // order_id's did, once
    }

    [Fact]
    public void MicroVfp_InsertFromMemvar_FieldWithoutMemvar_TakesItsDefault()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ins_dbc_default_memvar");
        string copy = TastradeCopy(dir);
        int orders = Counter(copy, "ORDERS");

        var vfp = Interpreter(copy, out var session);
        using (session)
            vfp.Execute("m.shipper_id = 'ZZ'\nINSERT INTO orders FROM MEMVAR\nCLOSE TABLES ALL");

        var row = LastOrder(copy).Last;
        Assert.Equal("ZZ", Text(row, "SHIPPER_ID"));
        Assert.Equal(orders.ToString(), Text(row, "ORDER_ID"));
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), row["ORDER_DATE"]);
    }

    [Fact]
    public void MicroVfp_BufferedInsert_TakesItsDefaultsOnce_BeforeTableUpdate()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ins_dbc_default_buffered");
        string copy = TastradeCopy(dir);
        int orders = Counter(copy, "ORDERS");

        var vfp = Interpreter(copy, out var session);
        using (session)
        {
            vfp.Execute("SET MULTILOCKS ON\nUSE tastrade!orders IN 0\nSELECT orders\n=CURSORSETPROP('Buffering', 5, 'orders')\n" +
                        "INSERT INTO orders (shipper_id) VALUES ('ZZ')\nGO BOTTOM\nlcPre = orders.order_id");
            Assert.Equal(orders.ToString(), vfp.Memory.Get("lcPre").AsString.Trim());   // visible in the buffer
            vfp.Execute("=TABLEUPDATE(.T., .T., 'orders')\nCLOSE TABLES ALL");
        }

        Assert.Equal(orders.ToString(), Text(LastOrder(copy).Last, "ORDER_ID"));
        Assert.Equal(orders + 1, Counter(copy, "ORDERS"));   // TABLEUPDATE did not evaluate it again
    }

    [Fact]
    public void RawSessionExecute_Insert_DoesNotEvaluateDbcDefaults()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ins_dbc_default_raw");
        string copy = TastradeCopy(dir);
        int orders = Counter(copy, "ORDERS");

        using (var session = new VfpSession())
        {
            session.OpenDatabase(Path.Combine(copy, "tastrade.dbc"));
            session.Execute("INSERT INTO orders (shipper_id) VALUES ('ZZ')");
        }

        var row = LastOrder(copy).Last;
        Assert.Equal("ZZ", Text(row, "SHIPPER_ID"));
        Assert.Equal("", Text(row, "ORDER_ID"));   // the raw path writes the row as given
        Assert.Null(row["ORDER_DATE"]);
        Assert.Equal(orders, Counter(copy, "ORDERS"));
    }
}
