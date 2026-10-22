using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The fields a microVFP append does not supply are written as VFP9 writes them:
/// <list type="bullet">
/// <item><description><c>APPEND FROM</c>: a target field the source lacks is blank under <c>SET NULL OFF</c>
/// and <c>.NULL.</c> under <c>SET NULL ON</c> (error 1581 when NOT NULL); a blank source field stays blank,
/// a <c>.NULL.</c> one stays <c>.NULL.</c> and raises 1581 in a NOT NULL target field, keeping the rows
/// before it.</description></item>
/// <item><description>A buffered <c>INSERT</c> committed by <c>TABLEUPDATE</c> writes its omitted columns
/// like the write-through INSERT, following SET NULL as it was at INSERT time — blank bytes (spaces, a 0
/// memo pointer), never a typed 0 / "" / .F.</description></item>
/// <item><description>An empty memo allocates no memo block, and a Varbinary is padded with 0x20.</description></item>
/// </list>
/// The engine reads a blank N/M field back as null, so these tests check the bytes and null bits on disk.
/// </summary>
public sealed class MicroVfpAppendBlankFieldsTests : IDisposable
{
    private readonly MicroVfpTestSupport.TempDir _dir = new("append_blank");

    public void Dispose() => _dir.Dispose();

    /// <summary>The append target: K plus one column per type that encodes differently when blank.</summary>
    private string NewTarget(string name, bool nullable)
    {
        string path = _dir.File(name + ".dbf");
        using var w = DbfWriter.Create(path, new[]
        {
            new DbfColumnDef("K", 'I'),
            new DbfColumnDef("C", 'C', 5, nullable: nullable),
            new DbfColumnDef("N", 'N', 6, 2, nullable: nullable),
            new DbfColumnDef("L", 'L', nullable: nullable),
            new DbfColumnDef("M", 'M', nullable: nullable),
            new DbfColumnDef("Q", 'Q', 5, nullable: nullable),
        }, new DbfCreateOptions { Overwrite = true });
        return path;
    }

    /// <summary>The APPEND FROM source: K/C/N/L only, NULL-able — a filled, a .NULL. and a blank row.</summary>
    private void NewSource()
    {
        using (var w = DbfWriter.Create(_dir.File("src.dbf"), new[]
        {
            new DbfColumnDef("K", 'I'),
            new DbfColumnDef("C", 'C', 5, nullable: true),
            new DbfColumnDef("N", 'N', 6, 2, nullable: true),
            new DbfColumnDef("L", 'L', nullable: true),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            w.AppendRecord(1, "ab", 1.5m, true);
            w.AppendRecord(2, null, null, null);
        }
        using var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        s.Execute("INSERT INTO src (K) VALUES (3)");   // SET NULL OFF: C/N/L blank, null bits clear.
    }

    private VfpInterpreter Interpreter()
    {
        var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        return new VfpInterpreter(s);
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

    private static long FptLength(string dbf) => new FileInfo(Path.ChangeExtension(dbf, ".fpt")).Length;

    private static readonly byte[] EmptyVarlen = { 0x20, 0x20, 0x20, 0x20, 0x00 };

    /// <summary>Record <paramref name="rec0"/> holds VFP blank bytes in <paramref name="columns"/>, null bits clear.</summary>
    private static void AssertBlank(string path, int rec0, params string[] columns)
    {
        foreach (string c in columns)
        {
            var (bytes, isNull, varlen) = Field(path, rec0, c);
            Assert.False(isNull, $"{c}: must be blank, not .NULL.");
            byte[] want = c switch
            {
                "C" => "     "u8.ToArray(),
                "N" => "      "u8.ToArray(),
                "L" => " "u8.ToArray(),
                "M" => new byte[4],
                "Q" => EmptyVarlen,
                _ => throw new ArgumentOutOfRangeException(nameof(columns), c, null),
            };
            Assert.Equal(want, bytes);
            if (c == "Q") Assert.True(varlen, "a blank Varbinary carries the varlen bit.");
        }
    }

    private static void AssertNull(string path, int rec0, params string[] columns)
    {
        foreach (string c in columns)
            Assert.True(Field(path, rec0, c).Null, $"{c}: must be .NULL.");
    }

    // ---- APPEND FROM ----------------------------------------------------------------------

    [Fact]
    public void AppendFrom_SetNullOff_UnsuppliedFieldsBlank_SourceBlankAndNullKept()
    {
        NewSource();
        string path = NewTarget("t", nullable: true);
        long fptBefore = FptLength(path);

        Interpreter().Execute("USE t\nAPPEND FROM src");

        Assert.Equal(3, RecordCount(path));
        Assert.Equal("ab   "u8.ToArray(), Field(path, 0, "C").Bytes);
        Assert.Equal("  1.50"u8.ToArray(), Field(path, 0, "N").Bytes);
        Assert.Equal("T"u8.ToArray(), Field(path, 0, "L").Bytes);
        AssertBlank(path, 0, "M", "Q");
        AssertNull(path, 1, "C", "N", "L");                 // a .NULL. source field stays .NULL.
        AssertBlank(path, 1, "M", "Q");
        AssertBlank(path, 2, "C", "N", "L", "M", "Q");      // a blank source field stays blank (L not 'F').
        Assert.Equal(fptBefore, FptLength(path));
    }

    [Fact]
    public void AppendFrom_SetNullOn_UnsuppliedNullableFieldsAreNull()
    {
        NewSource();
        string path = NewTarget("t", nullable: true);

        Interpreter().Execute("USE t\nSET NULL ON\nAPPEND FROM src FIELDS K, C");

        Assert.Equal(3, RecordCount(path));
        AssertNull(path, 0, "N", "L", "M", "Q");
        Assert.Equal("ab   "u8.ToArray(), Field(path, 0, "C").Bytes);
        AssertNull(path, 1, "C");
        AssertBlank(path, 2, "C");
    }

    [Fact]
    public void AppendFrom_NullIntoNotNullField_Raises1581_KeepsEarlierRows()
    {
        NewSource();
        string path = NewTarget("t", nullable: false);
        var interp = Interpreter();
        interp.Execute("USE t");

        var ex = Assert.Throws<MicroVfpRuntimeException>(() => interp.Execute("APPEND FROM src"));
        Assert.Equal(1581, ex.VfpErrorNumber);

        Assert.Equal(1, RecordCount(path));                 // row 1 landed; the .NULL. row 2 stopped the APPEND.
        AssertBlank(path, 0, "M", "Q");
    }

    [Fact]
    public void AppendFrom_SetNullOn_UnsuppliedNotNullField_Raises1581_NoRows()
    {
        NewSource();
        string path = NewTarget("t", nullable: false);
        var interp = Interpreter();
        interp.Execute("USE t\nSET NULL ON");

        Assert.ThrowsAny<Exception>(() => interp.Execute("APPEND FROM src FIELDS K"));

        Assert.Equal(0, RecordCount(path));
    }

    // ---- buffered INSERT ------------------------------------------------------------------

    [Fact]
    public void BufferedInsert_SetNullOff_OmittedColumnsAreWrittenBlank()
    {
        string path = NewTarget("t", nullable: true);
        long fptBefore = FptLength(path);

        Interpreter().Execute(
            "USE t\n=CURSORSETPROP('Buffering', 5)\nINSERT INTO t (K) VALUES (1)\n=TABLEUPDATE(.T.)\nUSE");

        Assert.Equal(1, RecordCount(path));
        AssertBlank(path, 0, "C", "N", "L", "M", "Q");      // not "  0.00", 'F' or a memo block.
        Assert.Equal(fptBefore, FptLength(path));
    }

    [Fact]
    public void BufferedInsert_SetNullOn_OmittedColumnsAreNull_AsAtInsertTime()
    {
        string path = NewTarget("t", nullable: true);

        Interpreter().Execute(
            "USE t\n=CURSORSETPROP('Buffering', 5)\nSET NULL ON\nINSERT INTO t (K) VALUES (1)\n" +
            "SET NULL OFF\n=TABLEUPDATE(.T.)\nUSE");

        Assert.Equal(1, RecordCount(path));
        AssertNull(path, 0, "C", "N", "L", "M", "Q");
    }

    [Fact]
    public void BufferedInsert_SetNullOn_OmittedNotNullColumn_BuffersNoRow()
    {
        string path = NewTarget("t", nullable: false);

        Interpreter().Execute(
            "USE t\n=CURSORSETPROP('Buffering', 5)\nSET NULL ON\nINSERT INTO t (K) VALUES (1)\n" +
            "SET NULL OFF\nINSERT INTO t (K) VALUES (2)\n=TABLEUPDATE(.T.)\nUSE");

        Assert.Equal(1, RecordCount(path));
        Assert.Equal(new byte[] { 2, 0, 0, 0 }, Field(path, 0, "K").Bytes);
    }

    // ---- empty memo / Varbinary padding ---------------------------------------------------

    [Fact]
    public void Insert_EmptyMemo_AllocatesNoBlock_AndVarbinaryIsPaddedWithSpaces()
    {
        string path = NewTarget("t", nullable: false);
        long fptBefore = FptLength(path);

        using (var s = new VfpSession())
        {
            s.OpenDirectory(_dir.Path);
            s.Execute("INSERT INTO t (K, M, Q) VALUES (1, '', 'ab')");
        }

        Assert.Equal(new byte[4], Field(path, 0, "M").Bytes);
        Assert.Equal(fptBefore, FptLength(path));
        var (q, _, varlen) = Field(path, 0, "Q");
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', 0x20, 0x20, 0x02 }, q);
        Assert.True(varlen);
    }

    [Fact]
    public void ReplaceMemoWithEmpty_StoresZeroPointer_AndReadsAsEmptyNotNull()
    {
        string path = NewTarget("t", nullable: true);
        var interp = Interpreter();
        interp.Execute("USE t\nINSERT INTO t (K, M) VALUES (1, 'abc')\nGO 1\nREPLACE M WITH ''");

        Assert.Equal(new byte[4], Field(path, 0, "M").Bytes);
        interp.Execute("lcM = M + 'x'\nllNull = ISNULL(M)");
        Assert.Equal("x", interp.Memory.Get("lcM").ToClr());
        Assert.Equal(false, interp.Memory.Get("llNull").ToClr());
    }
}
