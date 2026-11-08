using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

// Synthetic counterparts of SeekExactVfp9OracleTests.M2_*; error numbers and positions come from VFP9.
public sealed class MicroVfpSeekParityTests
{
    [Theory]
    [InlineData("GENERAL", 1, true)]
    [InlineData("GENERAL", 2, false)]
    [InlineData("GENERAL", 3, false)]
    [InlineData("SPANISH", 1, true)]
    [InlineData("SPANISH", 2, false)]
    [InlineData("SPANISH", 3, false)]
    public void ScopedSeek_StepsOverAtMostOneKeyOutsideScope(string collation, int outside, bool found)
    {
        using var dir = new MicroVfpTestSupport.TempDir("seek_scope");
        using (var w = DbfWriter.Create(dir.File("p.dbf"), new[] { new DbfColumnDef("NAME", 'C', 10) },
                   new DbfCreateOptions { CodePage = 0x03 }))
        {
            foreach (string value in new[] { "Muller", "Müller", "Ab c", "Ab  c", "Abc" }
                         .Concat(new[] { "Ab", "áb", "ab" }.Take(outside)))
                w.AppendRecord(value);
            w.CreateTag(new CdxTagDefinition("kg", "name") { Collation = collation });
        }
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interp = new VfpInterpreter(session);
        interp.Execute("USE p ORDER kg\nSET EXACT OFF\nSET KEY TO 'Ab '\nSEEK 'Ab'");
        Assert.Equal(found, Eval(interp, "FOUND()").AsLogical);
        Assert.Equal(!found, Eval(interp, "EOF()").AsLogical);
        Assert.Equal(found ? 4 : 6 + outside, Eval(interp, "RECNO()").AsInteger);
        Assert.True(Eval(interp, "KEYMATCH('Ab')").AsLogical);
        Assert.Equal(found, Eval(interp, "SEEK('Ab ')").AsLogical);
        Assert.True(Eval(interp, "SEEK('Ab c')").AsLogical);
    }

    [Theory]
    [InlineData("MACHINE", 240, false)]
    [InlineData("MACHINE", 241, true)]
    [InlineData("GENERAL", 120, false)]
    [InlineData("GENERAL", 121, true)]
    [InlineData("GENERAL", 150, true)]
    [InlineData("SPANISH", 120, false)]
    [InlineData("SPANISH", 121, true)]
    [InlineData("SPANISH", 150, true)]
    public void IndexWidthLimit_Raises112_WithoutReplacingExistingTag(string collation, int width, bool reject)
    {
        using var dir = new MicroVfpTestSupport.TempDir("seek_width");
        using (var w = DbfWriter.Create(dir.File("p.dbf"), new[] { new DbfColumnDef("NAME", 'C', 10) },
                   new DbfCreateOptions { CodePage = 0x03 }))
        {
            w.AppendRecord("Müller");
            w.CreateTag(new CdxTagDefinition("probe", "name"));
        }
        byte[] before = File.ReadAllBytes(dir.File("p.cdx"));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interp = new VfpInterpreter(session);
        interp.Execute($"USE p\nSET COLLATE TO '{collation}'");
        string command = $"INDEX ON PADR(name, {width}) TAG probe";
        if (reject)
        {
            var error = Assert.Throws<DbfWriteException>(() => interp.Execute(command));
            Assert.Equal(112, error.VfpErrorNumber);
            Assert.Equal(before, File.ReadAllBytes(dir.File("p.cdx")));
            interp.Execute($"lnError = 0\nTRY\n{command}\nCATCH TO loError\nlnError = loError.ErrorNo\nENDTRY");
            Assert.Equal(112, Eval(interp, "lnError").AsInteger);
        }
        else
        {
            interp.Execute(command);
            Assert.True(Eval(interp, "SEEK('Müller')").AsLogical);
        }
    }

    private static CrossVault.FoxDbf.Expressions.VfpValue Eval(VfpInterpreter interp, string expression)
    {
        Assert.True(interp.TryEvalExpression(expression, out var value));
        return value;
    }
}
