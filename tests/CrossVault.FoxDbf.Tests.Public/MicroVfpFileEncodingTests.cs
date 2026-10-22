using System;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP's file commands write the bytes VFP writes — the table / container code page or the interpreter's
/// byte-per-char model — never UTF-8, and a Latin-1-read .prg can use the CP1252 letters 0x80–0x9F (Š Œ š œ Ÿ)
/// in identifiers while its string literals keep their raw bytes.
/// </summary>
public sealed class MicroVfpFileEncodingTests : IDisposable
{
    private static readonly Encoding Cp1252 = Encoding.GetEncoding(1252);
    private readonly MicroVfpTestSupport.TempDir _dir = new("fileenc");

    static MicroVfpFileEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose() => _dir.Dispose();

    // ---- .prg identifiers: C1 controls → CP1252 letters, outside literals only ---------------------------

    [Theory]
    [InlineData("ln\u008Auma = 5", "lnŠuma = 5")]
    [InlineData("c\u009Cur = 1", "cœur = 1")]
    [InlineData("lc = '\u008A' + ln\u008A", "lc = '\u008A' + lnŠ")]
    [InlineData("lc = \"\u008A\"", "lc = \"\u008A\"")]
    [InlineData("lc = [\u008A]", "lc = [\u008A]")]
    [InlineData("la[n\u008A]", "la[nŠ]")]                    // a subscript, not a bracket string
    [InlineData("x = 1 && \u008A", "x = 1 && Š")]            // comments may change; they are discarded
    [InlineData("lc = ln\u0085", "lc = ln\u0085")]           // 0x85 is '…' in CP1252: not a letter
    public void NormalizeC1Letters_MapsLettersOutsideLiterals(string source, string expected)
        => Assert.Equal(expected, PrgParser.NormalizeC1Letters(source));

    [Fact]
    public void NormalizeC1Letters_LeavesTextBlocksAlone()
    {
        string src = "TEXT TO lc NOSHOW\r\n  ln\u008A\r\nENDTEXT\r\nln\u008A = 1";
        Assert.Equal("TEXT TO lc NOSHOW\r\n  ln\u008A\r\nENDTEXT\r\nlnŠ = 1", PrgParser.NormalizeC1Letters(src));
    }

    [Fact]
    public void NormalizeC1Letters_WithoutC1Characters_ReturnsTheSameString()
    {
        const string src = "lnX = 'abc'";
        Assert.Same(src, PrgParser.NormalizeC1Letters(src));
    }

    [Fact]
    public void Prg_IdentifierWithCp1252Letter_RunsAndLiteralKeepsItsByte()
    {
        string prg = Latin1(Cp1252.GetBytes("lnŠuma = 5\r\nlcLit = 'Š'\r\nllByte = (lcLit == CHR(138))\r\nlnOut = lnŠuma\r\n"));
        string path = _dir.File("c1.prg");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(prg));

        using var session = new VfpSession();
        var vfp = new VfpInterpreter(session);
        vfp.Execute(Encoding.Latin1.GetString(File.ReadAllBytes(path)));

        Assert.Equal(5m, vfp.EvalExpression("lnOut").AsNumber);
        Assert.True(vfp.EvalExpression("llByte").AsLogical);
    }

    [Fact]
    public void Prg_IdentifierWithCp1252Letter_AddressesTheTableField()
    {
        using (var w = DbfWriter.Create(_dir.File("t.dbf"), new[] { new DbfColumnDef("ŠUMA", 'N', 3) }))
            w.AppendRecord(1m);

        using var session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        var vfp = new VfpInterpreter(session);
        vfp.Execute(Latin1(Cp1252.GetBytes("USE t\r\nREPLACE Šuma WITH 7\r\nlnV = Šuma\r\n")));

        Assert.Equal(7m, vfp.EvalExpression("lnV").AsNumber);
    }

    // ---- COPY MEMO / DISPLAY STRUCTURE: table code page, not UTF-8 -------------------------------------------

    [Fact]
    public void CopyMemo_WritesTheMemoInTheTableCodePage()
    {
        const string text = "razón € Š";
        using (var w = DbfWriter.Create(_dir.File("m.dbf"), new[] { new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("NOTE", 'M', 4) }))
            w.AppendRecord(1, text);

        using var session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        new VfpInterpreter(session).Execute("USE m\r\nGO 1\r\nCOPY MEMO note TO out.txt");

        Assert.Equal(Cp1252.GetBytes(text), File.ReadAllBytes(_dir.File("out.txt")));
    }

    [Fact]
    public void DisplayStructure_ToFile_WritesFieldNamesInTheTableCodePage()
    {
        using (var w = DbfWriter.Create(_dir.File("s.dbf"), new[] { new DbfColumnDef("ŠUMA", 'N', 3) }))
            w.AppendRecord(1m);

        using var session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        new VfpInterpreter(session).Execute("USE s\r\nDISPLAY STRUCTURE TO FILE st.txt");

        byte[] bytes = File.ReadAllBytes(_dir.File("st.txt"));
        Assert.Contains((byte)0x8A, bytes);                                 // 'Š' in CP1252
        Assert.False(ContainsSequence(bytes, new byte[] { 0xC5, 0xA0 }));  // not UTF-8 'Š'
    }

    // ---- COPY / APPEND PROCEDURES: byte-identical round trip ------------------------------------------------

    [Fact]
    public void AppendThenCopyProcedures_RoundTripsTheSourceBytes()
    {
        using var tas = new MicroVfpTestSupport.TempDir("procs");
        var vfp = MicroVfpTestSupport.NewTastrade(tas, out var session);
        using var _ = session;
        string dataDir = session.DataDirectory!;

        byte[] appended = Cp1252.GetBytes("\r\nPROCEDURE zzEnc\r\n  RETURN 'razón € Š œ'\r\nENDPROC\r\n");
        File.WriteAllBytes(Path.Combine(dataDir, "add.prg"), appended);

        vfp.Execute("APPEND PROCEDURES FROM add.prg\r\nCOPY PROCEDURES TO out.prg");

        byte[] copied = File.ReadAllBytes(Path.Combine(dataDir, "out.prg"));
        Assert.True(ContainsSequence(copied, appended), "the appended bytes must come back unchanged");
        Assert.False(ContainsSequence(copied, new byte[] { 0xC3, 0xB3 }), "no UTF-8 'ó'");
        Assert.Equal(Latin1(Cp1252.GetBytes("razón € Š œ")), vfp.Call("zzEnc").AsString);
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }
}
