using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>Command semantics pinned by StandaloneIdxCommandsVfp9OracleTests in the internal project.</summary>
public sealed class StandaloneIdxCommandsTests
{
    [Theory]
    [InlineData("MACHINE", "")]
    [InlineData("MACHINE", " COMPACT")]
    [InlineData("GENERAL", "")]
    [InlineData("GENERAL", " COMPACT")]
    public void IndexOnToDescending_Raises1706_WithoutChangingFilesOrPosition(string collation, string compact)
    {
        using var b = new Bench();
        b.Interpreter.Execute($"USE t\nSET COLLATE TO '{collation}'\nINDEX ON k TAG oldtag\nGO 3");
        byte[] dbf = System.IO.File.ReadAllBytes(b.File("t.dbf"));
        byte[] cdx = System.IO.File.ReadAllBytes(b.File("t.cdx"));
        var error = Assert.Throws<MicroVfpRuntimeException>(() =>
            b.Interpreter.Execute($"INDEX ON k TO rejected DESCENDING{compact}"));
        Assert.Equal(1706, error.VfpErrorNumber);
        Assert.False(System.IO.File.Exists(b.File("rejected.idx")));
        Assert.Equal(dbf, System.IO.File.ReadAllBytes(b.File("t.dbf")));
        Assert.Equal(cdx, System.IO.File.ReadAllBytes(b.File("t.cdx")));
        Assert.Equal("OLDTAG", b.Interpreter.EvalExpression("ORDER()").AsString);
        Assert.Equal(3m, b.Interpreter.EvalExpression("RECNO()").AsNumber);
    }

    [Theory]
    [InlineData("GENERAL", "GENERAL", "")]
    [InlineData("GENERAL", "MACHINE", "")]
    [InlineData("MACHINE", "GENERAL", "")]
    [InlineData("MACHINE", "MACHINE", "")]
    [InlineData("GENERAL", "GENERAL", " DESCENDING")]
    public void CopyTag_WritesCompactIdx_WithSourceCollation_AndLeavesEof(string tagCollation, string sessionCollation, string direction)
    {
        using var b = new Bench();
        b.Interpreter.Execute($"""
            USE t
            SET COLLATE TO "{tagCollation}"
            INDEX ON k TAG source FOR n > 0{direction}
            SET COLLATE TO "{sessionCollation}"
            GO 3
            COPY TAG source TO copied
            """);
        Assert.Equal("SOURCE", b.Interpreter.EvalExpression("ORDER()").AsString);
        Assert.Equal(4m, b.Interpreter.EvalExpression("RECNO()").AsNumber);
        Assert.True(b.Interpreter.EvalExpression("EOF()").AsLogical);
        Assert.Equal(string.Empty, b.Interpreter.EvalExpression("NDX(1)").AsString);
        using var idx = IdxFile.Open(b.File("copied.idx"));
        Assert.True(idx.Header.IsCompact);
        Assert.Equal(tagCollation, idx.Header.Collation);
        Assert.Equal("k", idx.Header.KeyExpression);
        Assert.Equal("n>0", idx.Header.ForExpression);
        var entries = idx.EnumerateEntries().ToArray();
        Assert.Equal(new uint[] { 2, 3, 1 }, entries.Select(e => e.RecordNumber));
        string[] keys = tagCollation == "GENERAL"
            ? new[] { "6000000000000000", "6000000000000000", "6100000000000000" }
            : new[] { "41000000", "61000000", "62000000" };
        Assert.Equal(keys, entries.Select(e => Convert.ToHexString(e.Key)));
    }

    [Theory]
    [InlineData("SET INDEX TO x,y", "X", 2, 2, 3)]
    [InlineData("SET INDEX TO y,x", "Y", 3, 3, 2)]
    [InlineData("SET ORDER TO oldtag\nSET INDEX TO x,y", "X", 2, 2, 3)]
    [InlineData("SET ORDER TO oldtag\nSET INDEX TO x,y ADDITIVE", "X", 2, 2, 3)]
    [InlineData("SET INDEX TO x\nSET INDEX TO y ADDITIVE", "Y", 3, 3, 2)]
    [InlineData("SET INDEX TO x\nSET INDEX TO", "", 2, 1, 2)]
    [InlineData("SET ORDER TO oldtag\nSET INDEX TO", "", 3, 1, 2)]
    [InlineData("SET INDEX TO extra", "", 1, 1, 2)]
    [InlineData("SET INDEX TO extra,x", "", 1, 1, 2)]
    [InlineData("SET INDEX TO x,y ORDER 0", "", 1, 1, 2)]
    public void SetIndex_SelectsFirstListedIdx_AndRepositions(string command, string order, int position, int top, int next)
    {
        using var b = new Bench();
        b.Interpreter.Execute("""
            USE t
            INDEX ON k TO x COMPACT
            INDEX ON -n TO y COMPACT
            INDEX ON n TAG oldtag
            INDEX ON k TAG ztag OF extra
            SET INDEX TO
            SET ORDER TO 0
            GO 3
            """ + "\n" + command);
        Assert.Equal(order, b.Interpreter.EvalExpression("ORDER()").AsString);
        Assert.Equal(position, b.Interpreter.EvalExpression("RECNO()").AsNumber);
        b.Interpreter.Execute("GO TOP");
        Assert.Equal(top, b.Interpreter.EvalExpression("RECNO()").AsNumber);
        b.Interpreter.Execute("SKIP");
        Assert.Equal(next, b.Interpreter.EvalExpression("RECNO()").AsNumber);
    }

    private sealed class Bench : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = new("idx_commands");
        private readonly VfpSession _session = new();
        public VfpInterpreter Interpreter { get; }
        public string File(string name) => _dir.File(name);

        public Bench()
        {
            using (var writer = DbfWriter.Create(File("t.dbf"),
                new[] { new DbfColumnDef("k", 'C', 4), new DbfColumnDef("n", 'I') },
                new DbfCreateOptions { CodePage = 0x03 }))
            {
                writer.AppendRecord("b", 1);
                writer.AppendRecord("A", 2);
                writer.AppendRecord("a", 3);
            }
            _session.OpenDirectory(_dir.Path);
            Interpreter = new VfpInterpreter(_session);
        }

        public void Dispose()
        {
            _session.Dispose();
            _dir.Dispose();
        }
    }
}
