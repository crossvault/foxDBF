using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The columns an SQL <c>INSERT</c> does not supply follow <c>SET NULL</c>, as in VFP9:
/// <list type="bullet">
/// <item><description><c>SET NULL OFF</c> (the default): every omitted column is written BLANK, a
/// NULL-able one too (null bit clear). A blank V/Q is 0x20 padding + length byte 0 + varlen bit.</description></item>
/// <item><description><c>SET NULL ON</c>: an omitted NULL-able column is <c>.NULL.</c>; an omitted NOT NULL
/// column rejects the row with error 1581 (an AUTOINC column excepted).</description></item>
/// <item><description><c>FROM ARRAY</c> with a short row is blank whatever SET NULL says; <c>FROM MEMVAR</c>
/// treats a field with no memvar like an omitted column.</description></item>
/// </list>
/// The engine reads a blank C/N/M field back as null, so these tests check the null bits and the field
/// bytes on disk, which tell blank and <c>.NULL.</c> apart. Synthetic temp tables only.
/// </summary>
public sealed class SqlInsertOmittedColumnsTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>K plus one column per type that encodes differently when blank, all NULL-able or all NOT NULL.</summary>
    private string NewTable(string name, bool nullable)
    {
        string path = _dir.File(name + ".dbf");
        using var w = DbfWriter.Create(path, new[]
        {
            new DbfColumnDef("K", 'I'),
            new DbfColumnDef("C", 'C', 5, nullable: nullable),
            new DbfColumnDef("N", 'N', 6, 2, nullable: nullable),
            new DbfColumnDef("M", 'M', nullable: nullable),
            new DbfColumnDef("I", 'I', nullable: nullable),
            new DbfColumnDef("V", 'V', 5, nullable: nullable),
            new DbfColumnDef("Q", 'Q', 5, nullable: nullable),
        }, new DbfCreateOptions { Overwrite = true });
        return path;
    }

    private VfpSession OpenSession(bool nullOn)
    {
        var s = new VfpSession(new EvaluationContext { NullSetting = nullOn });
        s.OpenDirectory(_dir.Path);
        return s;
    }

    /// <summary>Raw bytes of <paramref name="column"/> in record <paramref name="rec0"/>, and whether its
    /// null / varlen bits are set.</summary>
    private static (byte[] Bytes, bool Null, bool Varlen) Field(string path, int rec0, string column)
    {
        using var t = DbfTable.Open(path, new DbfOptions { LockMode = LockMode.Shared });
        var col = t.Columns.First(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
        var rec = t.GetRecord(rec0)!.Value;
        byte[] bytes = rec.GetRawField(col).ToArray();
        var (varlenBit, nullBit) = t.GetVarlenNullBits(col);
        var flags = t.NullFlagsColumn is { } nf ? rec.GetRawField(nf).ToArray() : Array.Empty<byte>();
        bool Bit(int b) => b >= 0 && (flags[b >> 3] & (1 << (b & 7))) != 0;
        return (bytes, Bit(nullBit), Bit(varlenBit));
    }

    private static int RecordCount(string path)
    {
        using var t = DbfTable.Open(path, new DbfOptions { LockMode = LockMode.Shared });
        return t.RecordCount;
    }

    private static readonly byte[] EmptyVarlen = { 0x20, 0x20, 0x20, 0x20, 0x00 };

    private static void AssertBlank(string path, int rec0)
    {
        foreach (string c in new[] { "C", "N", "M", "I", "V", "Q" })
            Assert.False(Field(path, rec0, c).Null, $"{c}: an omitted column must be blank, not .NULL.");
        Assert.Equal("     "u8.ToArray(), Field(path, rec0, "C").Bytes);
        Assert.Equal("      "u8.ToArray(), Field(path, rec0, "N").Bytes);
        Assert.Equal(new byte[4], Field(path, rec0, "M").Bytes);
        Assert.Equal(new byte[4], Field(path, rec0, "I").Bytes);
        foreach (string c in new[] { "V", "Q" })
        {
            var (bytes, _, varlen) = Field(path, rec0, c);
            Assert.Equal(EmptyVarlen, bytes);
            Assert.True(varlen, $"{c}: a blank Varchar/Varbinary carries the varlen bit.");
        }
    }

    [Fact]
    public void SetNullOff_OmittedNullableColumns_AreWrittenBlank()
    {
        string path = NewTable("tn", nullable: true);
        long fptBefore = new FileInfo(Path.ChangeExtension(path, ".fpt")).Length;

        using (var s = OpenSession(nullOn: false))
            s.Execute("INSERT INTO tn (K) VALUES (1)");

        AssertBlank(path, 0);
        Assert.Equal(fptBefore, new FileInfo(Path.ChangeExtension(path, ".fpt")).Length);
    }

    [Fact]
    public void SetNullOff_OmittedNotNullColumns_AreWrittenBlank()
    {
        string path = NewTable("tx", nullable: false);

        using (var s = OpenSession(nullOn: false))
            s.Execute("INSERT INTO tx (K) VALUES (1)");

        AssertBlank(path, 0);
    }

    [Fact]
    public void SetNullOn_OmittedNullableColumns_AreNull()
    {
        string path = NewTable("tn", nullable: true);

        using (var s = OpenSession(nullOn: true))
            s.Execute("INSERT INTO tn (K, C) VALUES (1, 'ab')");

        Assert.False(Field(path, 0, "C").Null);
        foreach (string c in new[] { "N", "M", "I", "V", "Q" })
            Assert.True(Field(path, 0, c).Null, $"{c}: under SET NULL ON an omitted NULL-able column is .NULL.");
        // VFP9 stores a .NULL. Varchar/Varbinary as the empty varlen form, varlen bit set.
        foreach (string c in new[] { "V", "Q" })
        {
            var (bytes, _, varlen) = Field(path, 0, c);
            Assert.Equal(EmptyVarlen, bytes);
            Assert.True(varlen);
        }
    }

    [Fact]
    public void SetNullOn_OmittedNotNullColumn_RaisesError1581_AndAppendsNothing()
    {
        string path = NewTable("tx", nullable: false);

        using (var s = OpenSession(nullOn: true))
        {
            var ex = Assert.Throws<FoxDbfSqlException>(() => s.Execute("INSERT INTO tx (K, C) VALUES (1, 'ab')"));
            Assert.Equal(1581, ex.VfpErrorNumber);
            Assert.Contains("does not accept null values", ex.Message);
        }

        Assert.Equal(0, RecordCount(path));
    }

    [Fact]
    public void SetNullOn_OmittedAutoIncrementColumn_IsAssigned_NotRejected()
    {
        string path = _dir.File("ai.dbf");
        using (DbfWriter.Create(path, new[]
               {
                   new DbfColumnDef("K", 'I'),
                   new DbfColumnDef("A", 'I', autoIncrement: new DbfAutoIncrement(1, 1)),
                   new DbfColumnDef("C", 'C', 3, nullable: true),
               }, new DbfCreateOptions { Overwrite = true })) { }

        using (var s = OpenSession(nullOn: true))
            s.Execute("INSERT INTO ai (K) VALUES (1)");

        using var t = DbfTable.Open(path, new DbfOptions { LockMode = LockMode.Shared });
        Assert.Equal(1, t.RecordCount);
        Assert.Equal(1, t.GetRecord(0)!.Value["A"]);
        Assert.True(Field(path, 0, "C").Null);
    }

    // ---- microVFP: SET NULL, FROM ARRAY, FROM MEMVAR -----------------------------------------

    private VfpInterpreter Interpreter(VfpSession session) => new(session);

    [Fact]
    public void MicroVfp_DefaultSetNullOff_OmittedNullableColumn_IsNotNull()
    {
        string path = NewTable("tn", nullable: true);
        using (var s = new VfpSession())
        {
            s.OpenDirectory(_dir.Path);
            var vfp = Interpreter(s);
            vfp.Execute("INSERT INTO tn (K) VALUES (1)\nSELECT 0\nUSE tn\nGO TOP");
            // C is read null-bit-aware (a blank N/M still reads back as null on the engine's read side).
            Assert.False(vfp.EvalExpression("ISNULL(tn.C)").AsLogical);
            Assert.True(vfp.EvalExpression("EMPTY(tn.C)").AsLogical);
            vfp.Execute("CLOSE TABLES ALL");
        }
        AssertBlank(path, 0);
    }

    [Fact]
    public void MicroVfp_SetNullOn_OmittedNotNullColumn_AppendsNothing()
    {
        string path = NewTable("tx", nullable: false);
        using (var s = new VfpSession())
        {
            s.OpenDirectory(_dir.Path);
            var vfp = Interpreter(s);
            var error = Assert.Throws<FoxDbfSqlException>(() =>
                vfp.Execute("SET NULL ON\nINSERT INTO tx (K) VALUES (1)"));
            Assert.Equal(1581, error.VfpErrorNumber);
            vfp.Execute("CLOSE TABLES ALL");
        }
        Assert.Equal(0, RecordCount(path));
    }

    [Fact]
    public void MicroVfp_FromArray_ShortRow_IsBlank_EvenUnderSetNullOn()
    {
        string path = NewTable("tn", nullable: true);
        using (var s = new VfpSession())
        {
            s.OpenDirectory(_dir.Path);
            var vfp = Interpreter(s);
            vfp.Execute("SET NULL ON\nDIMENSION laRow[1]\nlaRow[1] = 1\nINSERT INTO tn FROM ARRAY laRow\nCLOSE TABLES ALL");
        }
        Assert.Equal(1, RecordCount(path));
        AssertBlank(path, 0);
    }

    [Fact]
    public void MicroVfp_FromMemvar_MissingMemvar_FollowsSetNull()
    {
        string path = NewTable("tn", nullable: true);
        using (var s = new VfpSession())
        {
            s.OpenDirectory(_dir.Path);
            var vfp = Interpreter(s);
            vfp.Execute("m.K = 1\nINSERT INTO tn FROM MEMVAR\nSET NULL ON\nm.K = 2\nINSERT INTO tn FROM MEMVAR\nCLOSE TABLES ALL");
        }
        Assert.Equal(2, RecordCount(path));
        AssertBlank(path, 0);
        foreach (string c in new[] { "C", "N", "M", "I", "V", "Q" })
            Assert.True(Field(path, 1, c).Null, $"{c}: FROM MEMVAR under SET NULL ON leaves a missing field .NULL.");
    }
}
