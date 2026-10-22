using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// A BLANK field (null bit clear, or a non-NULL-able column) is NOT .NULL. in VFP9: it reads as its typed
/// empty (0, {}, "", .F.) with ISNULL() .F. and EMPTY() .T. — only a set <c>_NullFlags</c> bit is .NULL.
/// (VFP9-pinned by BlankFieldNullBitVfp9OracleTests in the internal suite). microVFP field reads and the
/// SQL it runs follow that; the public <see cref="DbfRecord"/> indexer and <see cref="VfpSession.Execute"/>
/// (the ADO.NET path) keep mapping a blank N/F/D/T/M field to <see langword="null"/>.
/// <para>
/// Fixture: every column type twice — non-nullable (<c>?1</c>) and NULL-able (<c>?2</c>). Record 1 is the
/// VFP <c>APPEND BLANK</c> image (blank bytes, every null bit clear); record 2 holds .NULL. in each
/// NULL-able column (blank bytes + null bit) and blanks in the rest. Synthetic data only.
/// </para>
/// </summary>
public sealed class MicroVfpBlankFieldNullBitTests : IDisposable
{
    private static readonly string[] Types = { "c", "n", "f", "d", "t", "m", "i", "y", "l", "b" };

    private readonly string _dir;
    private readonly VfpSession _session;
    private readonly VfpInterpreter _interp;

    public MicroVfpBlankFieldNullBitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "foxdbf_blanknull_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "blk.dbf");

        var cols = new[] { false, true }.SelectMany(nullable => new[]
        {
            new DbfColumnDef(Name("c", nullable), 'C', 5, nullable: nullable),
            new DbfColumnDef(Name("n", nullable), 'N', 6, 2, nullable: nullable),
            new DbfColumnDef(Name("f", nullable), 'F', 8, 2, nullable: nullable),
            new DbfColumnDef(Name("d", nullable), 'D', nullable: nullable),
            new DbfColumnDef(Name("t", nullable), 'T', nullable: nullable),
            new DbfColumnDef(Name("m", nullable), 'M', nullable: nullable),
            new DbfColumnDef(Name("i", nullable), 'I', nullable: nullable),
            new DbfColumnDef(Name("y", nullable), 'Y', nullable: nullable),
            new DbfColumnDef(Name("l", nullable), 'L', nullable: nullable),
            new DbfColumnDef(Name("b", nullable), 'B', 8, 2, nullable: nullable),
        }).ToArray();
        using (var w = DbfWriter.Create(path, cols))
        {
            var nulls = new object?[cols.Length];
            w.AppendRecord(nulls);
            w.AppendRecord(nulls);
            w.Flush();
        }

        // Record 1 → the APPEND BLANK image: clear its _NullFlags bitmap (the field bytes are already blank).
        int nfOffset, nfLength, headerLength, recordLength;
        using (var t = DbfTable.Open(path))
        {
            var nf = t.NullFlagsColumn!;
            nfOffset = nf.Offset; nfLength = nf.Length;
            headerLength = t.HeaderLength; recordLength = t.RecordLength;
        }
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = headerLength + 1 + nfOffset;   // record 1, +1 skips the deletion flag.
            fs.Write(new byte[nfLength]);
        }

        _session = new VfpSession();
        _session.OpenDirectory(_dir);
        _interp = new VfpInterpreter(_session);
        _interp.Execute("USE blk");
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Name(string type, bool nullable) => type + (nullable ? "2" : "1");

    private bool Bool(string expr) => _interp.EvalExpression(expr).AsLogical;

    public static TheoryData<string> AllFields()
    {
        var data = new TheoryData<string>();
        foreach (var t in Types) { data.Add(t + "1"); data.Add(t + "2"); }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFields))]
    public void Blank_field_reads_as_typed_empty_not_null(string field)
    {
        _interp.Execute("GO 1");
        Assert.False(Bool($"ISNULL({field})"), $"{field}: a blank field (null bit clear) must not be .NULL.");
        Assert.True(Bool($"EMPTY({field})"), $"{field}: a blank field must be EMPTY()");
        Assert.NotEqual("X", _interp.EvalExpression($"VARTYPE({field})").AsString);
    }

    [Theory]
    [MemberData(nameof(AllFields))]
    public void Only_a_set_null_bit_reads_as_null(string field)
    {
        _interp.Execute("GO 2");
        bool nullable = field.EndsWith('2');
        Assert.Equal(nullable, Bool($"ISNULL({field})"));
        Assert.Equal(!nullable, Bool($"EMPTY({field})"));
    }

    [Fact]
    public void Blank_values_are_the_vfp_empties()
    {
        _interp.Execute("GO 1");
        // Check the TYPE too: a .NULL. would also report AsNumber 0 / AsString "".
        Assert.Equal(VfpType.Numeric, _interp.EvalExpression("n1").Type);
        Assert.Equal(0m, _interp.EvalExpression("n1").AsNumber);
        Assert.Equal(VfpType.Numeric, _interp.EvalExpression("f2").Type);
        Assert.Equal(0m, _interp.EvalExpression("f2").AsNumber);
        Assert.True(Bool("d1 = {}"));
        Assert.True(Bool("t2 = {}"));
        Assert.Equal(VfpType.Character, _interp.EvalExpression("m2").Type);
        Assert.Equal("", _interp.EvalExpression("m2").AsString);
        Assert.Equal(VfpType.Numeric, _interp.EvalExpression("n1 + 1").Type);   // .NULL. would propagate.
        Assert.Equal(1m, _interp.EvalExpression("n1 + 1").AsNumber);
    }

    [Theory]
    [MemberData(nameof(AllFields))]
    public void Sql_select_reads_blank_as_empty_and_null_bit_as_null(string field)
    {
        _interp.Execute($"SELECT ISNULL({field}), EMPTY({field}), {field} FROM blk INTO ARRAY la");
        bool nullable = field.EndsWith('2');
        Assert.False(Bool("la[1,1]"));
        Assert.True(Bool("la[1,2]"));
        Assert.False(Bool("ISNULL(la[1,3])"));
        Assert.Equal(nullable, Bool("la[2,1]"));
        Assert.Equal(!nullable, Bool("la[2,2]"));
        Assert.Equal(nullable, Bool("ISNULL(la[2,3])"));

        _interp.Execute($"SELECT COUNT({field}), COUNT(*) FROM blk WHERE ISNULL({field}) OR .T. INTO ARRAY lc");
        Assert.Equal(nullable ? 1m : 2m, _interp.EvalExpression("lc[1]").AsNumber);
        _interp.Execute($"SELECT RECNO() FROM blk WHERE ISNULL({field}) INTO ARRAY lw");
        Assert.Equal(nullable ? 1m : 0m, _interp.EvalExpression("_TALLY").AsNumber);
        _interp.Execute($"SELECT RECNO() FROM blk WHERE EMPTY({field}) INTO ARRAY lw");
        Assert.Equal(nullable ? 1m : 2m, _interp.EvalExpression("_TALLY").AsNumber);
    }

    [Fact]
    public void Sql_numeric_aggregates_over_blanks_are_zero()
    {
        _interp.Execute("SELECT SUM(n1), MAX(n1), SUM(n2), n1 + 1 FROM blk INTO ARRAY la");
        Assert.Equal(0m, _interp.EvalExpression("la[1,1]").AsNumber);
        Assert.Equal(0m, _interp.EvalExpression("la[1,2]").AsNumber);
        Assert.Equal(0m, _interp.EvalExpression("la[1,3]").AsNumber);
    }

    [Fact]
    public void Select_star_into_cursor_keeps_blank_empty_and_null_null()
    {
        _interp.Execute("SELECT * FROM blk INTO CURSOR cur");
        foreach (var t in Types)
        {
            _interp.Execute("GO 1 IN cur");
            Assert.False(Bool($"ISNULL(cur.{t}1)"), $"cur row 1 {t}1");
            Assert.False(Bool($"ISNULL(cur.{t}2)"), $"cur row 1 {t}2");
            Assert.True(Bool($"EMPTY(cur.{t}2)"), $"cur row 1 {t}2 EMPTY");
            _interp.Execute("GO 2 IN cur");
            Assert.False(Bool($"ISNULL(cur.{t}1)"), $"cur row 2 {t}1");
            Assert.True(Bool($"ISNULL(cur.{t}2)"), $"cur row 2 {t}2 must stay .NULL. in the cursor");
        }
    }

    [Fact]
    public void Public_record_indexer_and_session_execute_keep_blank_as_null()
    {
        // The public DbfRecord / VfpSession.Execute (ADO.NET) contract is unchanged: blank N/D → null.
        using (var t = DbfTable.Open(Path.Combine(_dir, "blk.dbf"), new DbfOptions { LockMode = LockMode.Shared }))
        {
            var rec = t.GetRecord(0)!.Value;
            Assert.Null(rec["n1"]);
            Assert.Null(rec["d2"]);
            Assert.Equal(0m, rec.GetVfpValue("n1"));                             // N(6,2) → decimal 0
            Assert.Equal(0m, rec.GetVfpValue("n2"));
            Assert.Equal(default(DateOnly), rec.GetVfpValue("d2"));
            Assert.Equal(default(DateTime), rec.GetVfpValue("t1"));
            Assert.Equal("", rec.GetVfpValue("m2"));
            var rec2 = t.GetRecord(1)!.Value;
            Assert.Null(rec2.GetVfpValue("n2"));                                 // null bit set → null
            Assert.Equal(0m, rec2.GetVfpValue("n1"));
        }

        var r = _session.Execute("SELECT n1, d2 FROM blk")!;
        var first = r.Rows.First();
        Assert.Null(first[0]);
        Assert.Null(first[1]);

        // … and there EMPTY() keeps matching such a (null-reading) blank field.
        var empties = _session.Execute("SELECT COUNT(*) FROM blk WHERE EMPTY(n1) AND EMPTY(d1)")!;
        Assert.Equal(2m, Convert.ToDecimal(empties.Rows.First()[0]));
    }
}
