using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP identifiers with non-ASCII letters, scanned by the same rule as the expression lexer
/// (<c>VfpIdentifier</c>): field names (VFP9 accepts <c>razónSoc</c>), memory variables, arrays, procedures,
/// macros and tag names. Runs over temp copies of the VFP9-generated fixtures in <c>vfp_test/uninames/</c>.
/// </summary>
public sealed class MicroVfpUnicodeIdentifierTests : IDisposable
{
    private readonly MicroVfpTestSupport.TempDir _dir = new("uninames");
    private readonly VfpSession _session = new();
    private readonly VfpInterpreter _interp;

    public MicroVfpUnicodeIdentifierTests()
    {
        foreach (var file in Directory.GetFiles(Fixtures.VfpTest("uninames")))
            if (!file.EndsWith(".prg", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, _dir.File(Path.GetFileName(file)));
        _session.OpenDirectory(_dir.Path);
        _interp = new VfpInterpreter(_session);
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { /* best-effort */ }
        _dir.Dispose();
    }

    private string Str(string expr) => _interp.EvalExpression(expr).AsString;

    [Fact]
    public void Replace_UnicodeField_WritesTheRow()
    {
        _interp.Execute("USE unifree\nGO 1\nREPLACE razónSoc WITH 'Nuevo'\nUSE");

        using var t = DbfTable.Open(_dir.File("unifree.dbf"));
        Assert.Equal("Nuevo", t.GetRecord(0)?["RAZÓNSOC"]?.ToString()?.TrimEnd());
        Assert.Equal("Peñarol", t.GetRecord(1)?["RAZÓNSOC"]?.ToString()?.TrimEnd());
    }

    [Fact]
    public void UnicodeMemvar_InSqlWhere_SelectsTheRow()
    {
        _interp.Execute("lcRazón = 'Peñarol'\nSELECT id FROM unifree WHERE razónSoc = lcRazón INTO CURSOR qq");
        Assert.Equal(1m, _interp.EvalExpression("RECCOUNT('qq')").AsNumber);
        Assert.Equal(2m, _interp.EvalExpression("qq.id").AsNumber);
    }

    [Fact]
    public void UnicodeArray_BracketSubscript_IsAnArray_NotAStringLiteral()
    {
        _interp.Execute("DIMENSION arró[2]\narró[1] = 'uno'\narró[2] = 'dos'");
        Assert.Equal("dos", Str("arró[2]"));
        Assert.Equal(2m, _interp.EvalExpression("ALEN(arró)").AsNumber);
    }

    [Fact]
    public void UnicodeMacroName_IsExpanded()
    {
        _interp.Execute("lcCampo = 'razónSoc'\nUSE unifree\nGO 2\nlcValor = &lcCampo.");
        Assert.Equal("Peñarol", Str("lcValor").TrimEnd());
    }

    [Fact]
    public void UnicodeProcedureName_IsCallable()
    {
        var interp = MicroVfpTestSupport.NewFromSource("FUNCTION sumaÑ(tnA)\n  RETURN tnA + 1\nENDFUNC\n", out var s);
        using (s)
        {
            Assert.True(interp.Has("sumaÑ"));
            Assert.Equal(42m, interp.EvalExpression("sumaÑ(41)").AsNumber);
        }
    }

    [Fact]
    public void LoadFile_DecodesCp1252_SoA0x8ALetterStillScans()
    {
        // 'Š' is 0x8A in CP1252 — a letter to VFP9, but a C1 control character if the file were read as Latin-1.
        string prg = _dir.File("cp1252.prg");
        File.WriteAllBytes(prg, Encodings.ResolveEncoding(0x03).GetBytes("FUNCTION dobleŠ(tn)\r\n  lnŠ = tn * 2\r\n  RETURN lnŠ\r\nENDFUNC\r\n"));
        _interp.LoadFile(prg);
        Assert.Equal(8m, _interp.EvalExpression("dobleŠ(4)").AsNumber);
    }

    [Fact]
    public void IndexOn_UnicodeKeyAndTag_SeeksAndUppercasesLikeVfp9()
    {
        _interp.Execute("USE unifree\nINDEX ON razónSoc TAG razón\nINDEX ON id TAG ƒid");
        // VFP9: TAG() of "razón" is RAZÓN; UPPER leaves ƒ alone in CP1252 (there is no 'Ƒ').
        Assert.Equal("RAZÓN", Str("TAG(1)"));
        Assert.Equal("ƒID", Str("TAG(2)"));
        _interp.Execute("SET ORDER TO razón");
        Assert.True(_interp.EvalExpression("SEEK('Peñarol')").AsLogical);
        Assert.Equal(2m, _interp.EvalExpression("id").AsNumber);
        _interp.Execute("USE");

        using var table = DbfTable.Open(_dir.File("unifree.dbf"));
        using var cdx = CdxFile.Open(_dir.File("unifree.cdx"), table);
        Assert.Equal("razónSoc", cdx.Tag("RAZÓN")!.KeyExpression);
    }

    [Fact]
    public void IndexOnTo_Cp850Table_IdxKeyReadsBackInTheTableCodePage()
    {
        // 'ó' is 0xA2 in CP850; read back with the CP1252 default it would turn into '¢'.
        _interp.Execute("USE uni850\nINDEX ON razónsoc TO r850");
        Assert.Equal("razónsoc", Str("LOWER(KEY())"));
        byte[] raw = File.ReadAllBytes(_dir.File("r850.idx"));
        Assert.Equal(0xA2, raw[16 + 3]);
    }

    [Fact]
    public void VfpBuiltCdx_TagNamesAndOrder_ReadThroughMicroVfp()
    {
        _interp.Execute("USE uniidx ORDER ŠUP");
        Assert.Equal("ŠUP", Str("ORDER()"));
        Assert.True(_interp.EvalExpression("SEEK('PEÑAROL')").AsLogical);
        Assert.Equal("razónsoc", Str("LOWER(KEY(1))"));
    }
}
