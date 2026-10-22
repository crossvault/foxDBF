using System.Text;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP reads a character field at its full declared width, as VFP9 does: <c>vfp_test/mvfpwidth/body.prg</c> runs
/// through the interpreter and must log exactly what VFP9 logged for it (<c>mvfpwidth.txt</c>, produced by
/// <c>mkmvfpwidth.prg</c>) — <c>LEN(name)</c> = 10 on every row, <c>name == 'abc'</c> false, field = field of different
/// widths, COUNT/LOCATE/SEEK/KEYMATCH under SET EXACT OFF and ON, TOTAL ON, SCATTER, REPLACE truncating a longer value
/// (<c>REPLACE name WITH name + 'q'</c> changes nothing), OLDVAL/CURVAL and buffered / unbuffered APPEND BLANK.
/// </summary>
public sealed class MicroVfpFullWidthGoldenTests
{
    [Fact]
    public void FieldReads_MatchVfp9()
    {
        string golden = Fixtures.VfpTest(Path.Combine("mvfpwidth", "mvfpwidth.txt"));
        string body = File.ReadAllText(Fixtures.VfpTest(Path.Combine("mvfpwidth", "body.prg")));
        using var dir = new MicroVfpTestSupport.TempDir("mvfpwidth");
        string outPath = dir.File("out.txt");
        File.Copy(Fixtures.VfpTest(Path.Combine("mvfpwidth", "fwseed.dbf")), dir.File("fw.dbf"));

        using (var session = new VfpSession())
        {
            session.OpenDirectory(dir.Path);
            var interp = new VfpInterpreter(session);
            // The VFP9 run's settings (mkmvfpwidth.prg): EXCLUSIVE ON, DELETED ON, ANSI OFF, EXACT at VFP's default.
            interp.Execute(
                "SET EXCLUSIVE ON\nSET DELETED ON\nSET ANSI OFF\n" +
                $"lnH = FCREATE('{outPath}')\n" +
                "ON ERROR =FPUTS(lnH, 'ERR=' + MESSAGE() + ' L' + TRANSFORM(LINENO()))\n" +
                body +
                "\nON ERROR\n=FCLOSE(lnH)\n");
        }

        var want = File.ReadAllLines(golden, Encoding.Latin1);
        var got = File.ReadAllLines(outPath, Encoding.Latin1);
        var diff = new List<string>();
        int n = Math.Max(want.Length, got.Length);
        for (int i = 0; i < n; i++)
        {
            string w = i < want.Length ? want[i] : "<missing>";
            string g = i < got.Length ? got[i] : "<missing>";
            if (w != g) diff.Add($"vfp  {w}\nours {g}");
        }
        Assert.True(diff.Count == 0, $"{diff.Count} lines differ from VFP9:\n" + string.Join("\n", diff));
    }
}
