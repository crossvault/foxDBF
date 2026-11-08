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
/// The EXCLUSIVE rewriters (INDEX ON / REINDEX / DELETE TAG / INDEX ON … TO) must present them too, or the tag
/// they build or rebuild cannot evaluate its KEY. Pinned against VFP9 by the internal
/// MicroVfpDbcLongNamesVfp9OracleTests and MicroVfpDbcLongNamesExclusiveVfp9OracleTests.
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

    /// <summary>The table opened EXCLUSIVE, as INDEX ON / REINDEX / DELETE TAG need it in VFP9.</summary>
    private (VfpSession Session, VfpInterpreter Vfp) OpenExclusive()
    {
        var s = new VfpSession(new EvaluationContext { Deleted = false });
        s.OpenDirectory(_dir.Path);
        s.OpenDatabase(_dbc);
        var vfp = new VfpInterpreter(s);
        vfp.Execute("SET EXCLUSIVE ON\nUSE customer_accounts IN 0 EXCLUSIVE\nSELECT customer_accounts");
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
            // MicroVfpInsertFailureVfp9OracleTests: the physical name raises 12 and appends no row.
            vfp.Execute("""
                DIMENSION errors[1]
                TRY
                    INSERT INTO customer_accounts (account_number, customer_n) VALUES (9, 'Short')
                CATCH TO problem
                    savedError = ERROR()
                    errorCount = AERROR(errors)
                ENDTRY
                """);
            Assert.Equal("12", Eval(vfp, "problem.ErrorNo"));
            Assert.Equal("0", Eval(vfp, "savedError"));
            Assert.Equal("1", Eval(vfp, "errorCount"));
            Assert.Equal("12", Eval(vfp, "errors[1]"));
            Assert.Equal("3", Eval(vfp, "RECCOUNT('customer_accounts')"));
        }
    }

    // ---- EXCLUSIVE rewrites (INDEX ON / REINDEX / DELETE TAG): pinned by MicroVfpDbcLongNamesExclusiveVfp9OracleTests ----

    [Fact]
    public void IndexOn_LongField_BuildsASeekableTag_AndKeepsTheOthersMaintained()
    {
        var (s, vfp) = OpenExclusive();
        using (s)
        {
            vfp.Execute("INDEX ON customer_name TAG probe");
            Assert.Equal("CUSTOMER_NAME", Eval(vfp, "KEY()").ToUpperInvariant());
            Assert.Equal(".T.", Eval(vfp, "SEEK('Gamma', 'customer_accounts', 'probe')"));
            Assert.Equal("3", Eval(vfp, "RECNO()"));

            vfp.Execute("GO 1\nREPLACE customer_name WITH 'Xavier'");
            Assert.Equal(".T.", Eval(vfp, "SEEK('Xavier', 'customer_accounts', 'custname')"));
            Assert.Equal(".T.", Eval(vfp, "SEEK('Xavier', 'customer_accounts', 'probe')"));
            Assert.Equal("1", Eval(vfp, "RECNO()"));
            Assert.Equal(".T.", Eval(vfp, "SEEK(2, 'customer_accounts', 'acct')"));
        }
        Assert.True(File.Exists(Cdx));
    }

    [Fact]
    public void IndexOn_ForClauseOnALongField_FiltersByIt()
    {
        var (s, vfp) = OpenExclusive();
        using (s)
        {
            vfp.Execute("INDEX ON UPPER(customer_name) TAG pfor FOR balance_amount > 15");
            Assert.Equal(".T.", Eval(vfp, "SEEK('BETA', 'customer_accounts', 'pfor')"));
            Assert.Equal("2", Eval(vfp, "RECNO()"));
            Assert.Equal(".F.", Eval(vfp, "SEEK('ALPHA', 'customer_accounts', 'pfor')"));
        }
    }

    [Fact]
    public void Reindex_And_DeleteTag_KeepTheLongNameTags()
    {
        var (s, vfp) = OpenExclusive();
        using (s)
        {
            vfp.Execute("REINDEX");
            Assert.Equal(".T.", Eval(vfp, "SEEK('Gamma', 'customer_accounts', 'custname')"));
            Assert.Equal("3", Eval(vfp, "RECNO()"));
            Assert.Equal(".T.", Eval(vfp, "SEEK(2, 'customer_accounts', 'acct')"));

            vfp.Execute("DELETE TAG acct");
            Assert.Equal("1", Eval(vfp, "TAGCOUNT()"));
            Assert.Equal(".T.", Eval(vfp, "SEEK('Beta', 'customer_accounts', 'custname')"));
            Assert.Equal("2", Eval(vfp, "RECNO()"));
        }
        Assert.True(File.Exists(Cdx));
    }

    [Fact]
    public void IndexOnTo_LongField_BuildsASeekableIdx()
    {
        var (s, vfp) = OpenExclusive();
        using (s)
        {
            vfp.Execute("INDEX ON customer_name TO longidx");
            Assert.Equal(".T.", Eval(vfp, "SEEK('Gamma')"));
            Assert.Equal("3", Eval(vfp, "RECNO()"));
        }
        Assert.True(File.Exists(_dir.File("longidx.idx")));
    }
}
