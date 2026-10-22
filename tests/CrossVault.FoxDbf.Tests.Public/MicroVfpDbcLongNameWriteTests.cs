using System;
using System.IO;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP REPLACE and the INSERT … VALUES direct-append fast path on a DBC member whose field names are
/// longer than the 10-char physical names, with a structural .cdx whose tag KEY names a long field
/// (<c>CUSTOMER_NAME</c>, as VFP9 stores it). The cached writer must present the long names: otherwise the
/// tag is un-derivable and the write deletes the .cdx, and a long INSERT column is unknown and the row is lost.
/// Pinned against VFP9 by the internal MicroVfpDbcLongNamesVfp9OracleTests.
/// </summary>
public sealed class MicroVfpDbcLongNameWriteTests : IDisposable
{
    private static readonly string[] LongNames = { "account_number", "customer_name", "balance_amount", "id", "remark_text_long" };

    private readonly SqlTestSupport.TempDir _dir = new();
    private readonly string _dbc;

    public MicroVfpDbcLongNameWriteTests()
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
                new DbfColumnDef("REMARK_TEX", 'M', 4) { Nullable = true },
            }, LongNames),
        });
        using var w = DbfWriter.Open(_dir.File("customer_accounts.dbf"), new DbfOptions(),
            cols => DbfDatabase.ApplyLongNames(cols, LongNames));
        w.AppendRecord(1, "Alpha", 10m, 101, "first");
        w.AppendRecord(2, "Beta", 20m, 102, "second");
        w.AppendRecord(3, "Gamma", 30m, 103, "third");
        w.CreateTag(new CdxTagDefinition("custname", "CUSTOMER_NAME"));
        w.CreateTag(new CdxTagDefinition("acct", "ACCOUNT_NUMBER"));
        w.Flush();
    }

    public void Dispose() => _dir.Dispose();

    private string Cdx => _dir.File("customer_accounts.cdx");

    private (VfpSession Session, VfpInterpreter Vfp) Open()
    {
        var s = new VfpSession(new EvaluationContext { Deleted = false });
        s.OpenDirectory(_dir.Path);
        s.OpenDatabase(_dbc);
        var vfp = new VfpInterpreter(s);
        vfp.Execute("USE customer_accounts IN 0 SHARED\nSELECT customer_accounts");
        return (s, vfp);
    }

    private static string Eval(VfpInterpreter vfp, string expr) => vfp.EvalExpression("TRANSFORM(" + expr + ")").AsString;

    [Fact]
    public void Replace_LongField_MaintainsTheStructuralCdx()
    {
        var (s, vfp) = Open();
        using (s)
        {
            vfp.Execute("GO 1\nREPLACE customer_name WITH 'Xavier'\nGO 2\nREPLACE account_number WITH 22");

            Assert.True(File.Exists(Cdx));
            Assert.Equal(".T.", Eval(vfp, "SEEK('Xavier', 'customer_accounts', 'custname')"));
            Assert.Equal("1", Eval(vfp, "RECNO()"));
            Assert.Equal(".F.", Eval(vfp, "SEEK('Alpha', 'customer_accounts', 'custname')"));
            Assert.Equal(".T.", Eval(vfp, "SEEK(22, 'customer_accounts', 'acct')"));
            Assert.Equal("2", Eval(vfp, "RECNO()"));
        }
        Assert.True(File.Exists(Cdx));
    }

    [Fact]
    public void Insert_LongColumnList_AppendsTheRowAndItsKeys()
    {
        var (s, vfp) = Open();
        using (s)
        {
            vfp.Execute("INSERT INTO customer_accounts (account_number, customer_name, balance_amount, id, remark_text_long) " +
                        "VALUES (4, 'Delta', 40, 104, 'fourth')");
            vfp.Execute("INSERT INTO customer_accounts VALUES (5, 'Echo', 50, 105, 'fifth')");

            Assert.Equal("5", Eval(vfp, "RECCOUNT('customer_accounts')"));
            Assert.Equal(".T.", Eval(vfp, "SEEK('Delta', 'customer_accounts', 'custname')"));
            Assert.Equal("4", Eval(vfp, "RECNO()"));
            Assert.Equal("fourth", Eval(vfp, "remark_text_long"));
            Assert.Equal(".T.", Eval(vfp, "SEEK(5, 'customer_accounts', 'acct')"));
            Assert.Equal("5", Eval(vfp, "RECNO()"));
        }
        Assert.True(File.Exists(Cdx));
    }

    [Fact]
    public void Insert_PhysicalNameOfALongField_LandsNoRow()
    {
        var (s, vfp) = Open();
        using (s)
        {
            // VFP9: error 12. microVFP's INSERT swallows its errors; the row must not land either way.
            vfp.Execute("INSERT INTO customer_accounts (account_number, customer_n) VALUES (9, 'Short')");
            Assert.Equal("3", Eval(vfp, "RECCOUNT('customer_accounts')"));
        }
    }
}
