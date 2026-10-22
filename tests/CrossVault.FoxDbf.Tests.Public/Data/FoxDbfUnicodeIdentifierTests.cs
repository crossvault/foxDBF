using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CrossVault.FoxDbf.Data;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// ADO.NET with a non-ASCII field name (VFP9 accepts <c>razónSoc</c>; fixture <c>vfp_test/uninames/unifree.dbf</c>,
/// copied to a temp dir because Update mutates). The command builder must recognise the column as a plain
/// field reference, and the advertised IdentifierPattern must accept it.
/// </summary>
public sealed class FoxDbfUnicodeIdentifierTests : IDisposable
{
    private readonly IndexMaintTestSupport.TempDir _dir = new();

    public FoxDbfUnicodeIdentifierTests()
        => File.Copy(Fixtures.VfpTest(Path.Combine("uninames", "unifree.dbf")), Path.Combine(_dir.Path, "unifree.dbf"));

    public void Dispose() => _dir.Dispose();

    private FoxDbfConnection Open()
    {
        var c = new FoxDbfConnection($"Data Source={_dir.Path}");
        c.Open();
        return c;
    }

    [Fact]
    public void CommandBuilder_UpdatesAUnicodeColumn()
    {
        using (var conn = Open())
        {
            var select = (FoxDbfCommand)conn.CreateCommand();
            select.CommandText = "SELECT RECNO() AS recno, id, razónSoc FROM unifree";
            var a = new FoxDbfDataAdapter(select);
            using var builder = new FoxDbfCommandBuilder(a);

            Assert.Contains("razónSoc", builder.GetUpdateCommand().CommandText, StringComparison.OrdinalIgnoreCase);

            var dt = new DataTable();
            a.Fill(dt);
            dt.Rows.Cast<DataRow>().First(r => Convert.ToInt32(r["id"]) == 1)["razónSoc"] = "Nuevo";
            Assert.Equal(1, a.Update(dt));
        }

        using var t = DbfTable.Open(Path.Combine(_dir.Path, "unifree.dbf"));
        Assert.Equal("Nuevo", t.GetRecord(0)?["RAZÓNSOC"]?.ToString()?.TrimEnd());
    }

    [Fact]
    public void DataSourceInformation_IdentifierPattern_AcceptsUnicodeLetters()
    {
        using var conn = Open();
        DataTable t = conn.GetSchema(DbMetaDataCollectionNames.DataSourceInformation);
        var pattern = new Regex((string)t.Rows[0]["IdentifierPattern"]);

        Assert.Matches(pattern, "razónSoc");
        Assert.Matches(pattern, "_id2");
        Assert.DoesNotMatch(pattern, "2id");
        Assert.DoesNotMatch(pattern, "raz ón");
    }
}
