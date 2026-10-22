using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// The COMPACT standalone <c>.idx</c> (<c>INDEX ON … TO x.idx COMPACT</c>): <see cref="DbfWriter.CreateCompactIdx"/>,
/// its read side (<see cref="IdxFile"/> over a compact file), microVFP's COMPACT flag and the implicit compact
/// <c>.idx</c> under SET COLLATE GENERAL, the page layout and VFP's header statistics. The byte-for-byte VFP9
/// comparison lives in the Internal project (CompactIdxVfp9OracleTests); the values pinned here are taken from VFP9
/// files.
/// </summary>
public sealed class CompactIdxTests
{
    private sealed class Bench : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "foxdbf_cidx_" + Guid.NewGuid().ToString("N"));
        public string Dbf => Path.Combine(Dir, "t.dbf");
        public string File(string name) => Path.Combine(Dir, name);

        /// <summary>A table with one C(1) key per record, in record order.</summary>
        public Bench(string keys)
        {
            Directory.CreateDirectory(Dir);
            using var w = DbfWriter.Create(Dbf, new[] { new DbfColumnDef("k", 'C', 1), new DbfColumnDef("n", 'I') });
            for (int i = 0; i < keys.Length; i++)
                w.AppendRecord(keys[i].ToString(), i + 1);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>The five statistics VFP9 stores at @16..35 of the header: entries, keys unlike the one before,
    /// the sum of record-number distances, the trend of those distances, and the steps down.</summary>
    private static (uint, uint, uint, int, uint) Statistics(byte[] file) => (
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)),
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20)),
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)),
        BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(28)),
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32)));

    // Each row: the C(1) key of record 1, 2, … and the statistics VFP9 wrote for INDEX ON k TO x COMPACT.
    [Theory]
    [InlineData("A", 1u, 1u, 0u, 0, 0u)]
    [InlineData("BA", 2u, 2u, 1u, 0, 1u)]
    [InlineData("AA", 2u, 1u, 1u, 0, 0u)]
    [InlineData("ACB", 3u, 3u, 3u, -1, 1u)]
    [InlineData("BAC", 3u, 3u, 3u, 1, 1u)]
    [InlineData("CBA", 3u, 3u, 2u, 0, 2u)]
    [InlineData("ABBA", 4u, 2u, 6u, -2, 1u)]
    [InlineData("BAAB", 4u, 2u, 6u, 2, 1u)]
    [InlineData("CABAB", 5u, 3u, 9u, 1, 2u)]
    [InlineData("CFHBAHEDD", 9u, 7u, 23u, 3, 4u)]
    [InlineData("HHGCDCGABCAEAEHGG", 17u, 7u, 89u, 3, 5u)]
    [InlineData("ECAFFFCGGHGBEGD", 15u, 8u, 81u, 2, 4u)]
    public void HeaderStatistics_AreVfp9s(string keys, uint entries, uint distinct, uint distances, int trend, uint descents)
    {
        using var b = new Bench(keys);
        using (var w = DbfWriter.Open(b.Dbf))
            w.CreateCompactIdx(b.File("x.idx"), "k");
        Assert.Equal((entries, distinct, distances, trend, descents), Statistics(System.IO.File.ReadAllBytes(b.File("x.idx"))));
    }

    [Fact]
    public void HeaderStatistics_ABlankFirstKeyIsNotCounted()
    {
        using var b = new Bench("  AB");   // VFP9 counts the keys unlike the one before from an all-blank key
        using (var w = DbfWriter.Open(b.Dbf))
            w.CreateCompactIdx(b.File("x.idx"), "k");
        Assert.Equal(2u, Statistics(System.IO.File.ReadAllBytes(b.File("x.idx"))).Item2);
    }

    [Fact]
    public void CompactIdx_HeaderAndEntries_ReadBack()
    {
        using var b = new Bench("DBCAEBDA");
        using (var w = DbfWriter.Open(b.Dbf))
        {
            w.CreateCompactIdx(b.File("u.idx"), "k", forExpr: "n>1", unique: true);
            w.CreateCompactIdx(b.File("g.idx"), "k", collation: "GENERAL");
        }

        byte[] raw = System.IO.File.ReadAllBytes(b.File("u.idx"));
        Assert.Equal(0x29, raw[14]);                                                   // compact | FOR | UNIQUE
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32LittleEndian(raw));             // the root is on page 2

        using var table = DbfTable.Open(b.Dbf);
        using (var idx = IdxFile.Open(b.File("u.idx"), table))
        {
            var h = idx.Header;
            Assert.True(h.IsCompact);
            Assert.True(h.IsUnique);
            Assert.True(h.HasFor);
            Assert.Equal("k", h.KeyExpression);
            Assert.Equal("n>1", h.ForExpression);
            Assert.Equal("MACHINE", h.Collation);
            // n>1 drops record 1 ('D'); UNIQUE keeps the lowest recno per key.
            Assert.Equal(new uint[] { 4, 2, 3, 7, 5 }, idx.EnumerateEntries().Select(e => e.RecordNumber).ToArray());
        }
        using (var idx = IdxFile.Open(b.File("g.idx"), table))
        {
            Assert.Equal("GENERAL", idx.Header.Collation);
            Assert.Equal(2, idx.KeyLength);                                            // GENERAL doubles the width
            Assert.Equal(new uint[] { 4, 8, 2, 6, 3, 1, 7, 5 }, idx.EnumerateEntries().Select(e => e.RecordNumber).ToArray());
        }
    }

    [Fact]
    public void CompactIdx_RefusesDescending()
    {
        using var b = new Bench("AB");
        using var table = DbfTable.Open(b.Dbf);
        // CreateCompactIdx has no DESCENDING; the builder refuses it as VFP9 does ("Cannot sort .IDX files in descending order.").
        Assert.Throws<DbfWriteException>(() => CdxIndexBuilder.BuildCompactIdx(b.File("d.idx"), table,
            Array.Empty<CdxIndexBuilder.BuildRow>(), new CdxTagDefinition("d", "k", descending: true)));
    }

    [Fact]
    public void MicroVfp_IndexOnToCompact_WritesACompactIdx_AndOrdersBy_It()
    {
        using var b = new Bench("DBCAEBDA");
        using var s = new VfpSession();
        s.OpenDirectory(b.Dir);
        var interp = new VfpInterpreter(s);

        interp.Execute("USE t\nINDEX ON k TO plain\nINDEX ON k TO small COMPACT");
        Assert.Equal(0x00, System.IO.File.ReadAllBytes(b.File("plain.idx"))[14] & 0x20);   // VFP9's default .idx is uncompressed
        Assert.Equal(0x20, System.IO.File.ReadAllBytes(b.File("small.idx"))[14] & 0x20);

        interp.Execute("GO TOP");
        Assert.Equal(4m, interp.EvalExpression("RECNO()").AsNumber);
        interp.Execute("SKIP 2");
        Assert.Equal(2m, interp.EvalExpression("RECNO()").AsNumber);
        Assert.True(interp.EvalExpression("SEEK('E')").AsLogical);
        Assert.Equal(5m, interp.EvalExpression("RECNO()").AsNumber);
    }

    [Fact]
    public void MicroVfp_IndexOnTo_UnderSetCollateGeneral_IsCompact()
    {
        using var b = new Bench("baBA");
        using var s = new VfpSession();
        s.OpenDirectory(b.Dir);
        var interp = new VfpInterpreter(s);

        interp.Execute("USE t\nSET COLLATE TO 'GENERAL'\nINDEX ON k TO gen");
        byte[] raw = System.IO.File.ReadAllBytes(b.File("gen.idx"));
        Assert.Equal(0x20, raw[14] & 0x20);
        Assert.Equal("GENERAL", System.Text.Encoding.ASCII.GetString(raw, 494, 7));

        // GENERAL ignores case: a/A sort together, ahead of b/B.
        interp.Execute("GO TOP");
        Assert.Equal("a", interp.EvalExpression("LOWER(k)").AsString);
        interp.Execute("SKIP 2");
        Assert.Equal("b", interp.EvalExpression("LOWER(k)").AsString);
        Assert.True(interp.EvalExpression("SEEK('B')").AsLogical);
    }

    // VFP9 compact .idx trees (INDEX ON … TO x.idx COMPACT, random unique keys): every node's page, the branch
    // nodes' child counts and the file's page count. Format: K<key length> n<rows>: eof <pages> root <page> ||
    // branch levels from the root || L <leaf pages>.
    [Theory]
    [InlineData("K41 n140: eof 18 root 2 || B 2:2 || B 15:10 14:3 || L 4 3 5 6 7 8 9 10 11 12 13 16 17")]
    [InlineData("K10 n1350: eof 38 root 2 || B 2:2 || B 32:27 31:6 || L 4 3 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 33 34 35 36 37")]
    [InlineData("K45 n875: eof 103 root 2 || B 2:2 || B 96:9 95:1 || B 14:9 13:9 24:9 34:9 44:9 54:9 64:9 74:9 84:9 94:7 || L 4 3 5 6 7 8 9 10 11 12 15 16 17 18 19 20 21 22 23 25 26 27 28 29 30 31 32 33 35 36 37 38 39 40 41 42 43 45 46 47 48 49 50 51 52 53 55 56 57 58 59 60 61 62 63 65 66 67 68 69 70 71 72 73 75 76 77 78 79 80 81 82 83 85 86 87 88 89 90 91 92 93 97 98 99 100 101 102")]
    [InlineData("K60 n427: eof 75 root 2 || B 2:2 || B 62:7 61:2 || B 12:7 11:7 20:7 28:7 36:7 44:7 52:7 60:7 70:5 || L 4 3 5 6 7 8 9 10 13 14 15 16 17 18 19 21 22 23 24 25 26 27 29 30 31 32 33 34 35 37 38 39 40 41 42 43 45 46 47 48 49 50 51 53 54 55 56 57 58 59 63 64 65 66 67 68 69 71 72 73 74")]
    [InlineData("K96 n284: eof 99 root 2 || B 2:2 || B 91:4 90:1 || B 26:4 25:4 47:4 68:4 89:2 || B 9:4 8:4 14:4 19:4 24:4 31:4 36:4 41:4 46:4 52:4 57:4 62:4 67:4 73:4 78:4 83:4 88:4 96:3 || L 4 3 5 6 7 10 11 12 13 15 16 17 18 20 21 22 23 27 28 29 30 32 33 34 35 37 38 39 40 42 43 44 45 48 49 50 51 53 54 55 56 58 59 60 61 63 64 65 66 69 70 71 72 74 75 76 77 79 80 81 82 84 85 86 87 92 93 94 95 97 98")]
    public void TreeLayout_PlacesEveryNodeAsVfp9(string tree)
    {
        var parts = tree.Split("||").Select(p => p.Trim()).ToArray();
        int keyLen = int.Parse(Regex.Match(parts[0], @"K(\d+)").Groups[1].Value);
        int eof = int.Parse(Regex.Match(parts[0], @"eof (\d+)").Groups[1].Value);
        var branches = parts[1..^1].Reverse()
            .Select(p => p[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Split(':')).Select(n => new CdxIndexBuilder.TreeNode(int.Parse(n[0]), int.Parse(n[1]))).ToList())
            .ToList();
        var leaves = parts[^1][1..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

        var (ourLeaves, ourBranches, next) = CdxIndexBuilder.TreeLayout(leaves.Count, keyLen, rootPage: 2);

        Assert.Equal(leaves, ourLeaves);
        Assert.Equal(branches.Count, ourBranches.Count);
        for (int h = 0; h < branches.Count; h++)
            Assert.Equal(branches[h], ourBranches[h]);
        Assert.Equal(eof, next);
    }
}
