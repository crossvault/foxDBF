using System;
using System.IO;
using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Which write paths store the decompiled KEY/FOR text (as VFP9's INDEX ON / REINDEX do) and which keep the
/// text already on disk (VFP9 leaves sibling tags untouched when one tag is added; CREATE DATABASE stores the
/// .dcx texts as typed). Verified against the VFP9 runtime: after REINDEX (and PACK) a tag written as
/// <c>!DELETED()</c> reads <c>.NOT.DELETED()</c>; after INDEX ON ... TAG new it still reads <c>!DELETED()</c>.
/// </summary>
public sealed class IndexExpressionTextWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foxdbf_exprtext_" + Guid.NewGuid().ToString("N"));

    public IndexExpressionTextWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string NewTable()
    {
        string dbf = Path.Combine(_dir, "t.dbf");
        using var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("NAME", 'C', 10), new DbfColumnDef("CAT", 'C', 2) });
        for (int i = 0; i < 40; i++)
            w.AppendRecord("N" + i.ToString("D2"), "C" + (i % 4));
        return dbf;
    }

    private static (string Key, string For) TagText(string dbf, string tag)
    {
        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table);
        var t = cdx.Tag(tag)!;
        return (t.KeyExpression, t.ForExpression);
    }

    [Fact]
    public void CreateTag_StoresTheDecompiledText()
    {
        string dbf = NewTable();
        using (var w = DbfWriter.Open(dbf))
            w.CreateTag(new CdxTagDefinition("TNAME", "UPPER( Name )", "NOT DELETED() AND cat <> 'C1'"));

        Assert.Equal(("UPPER(name)", ".NOT.DELETED().AND.cat#\"C1\""), TagText(dbf, "TNAME"));
    }

    [Fact]
    public void CreateTag_LeavesSiblingText_Reindex_DecompilesEveryTag()
    {
        string dbf = NewTable();
        using (var w = DbfWriter.Open(dbf))
        {
            w.CreateTag(new CdxTagDefinition("TOLD", "upper(name)", "!DELETED()") { StoreExpressionsVerbatim = true });
            w.CreateTag(new CdxTagDefinition("TNEW", "cat + name"));
        }
        Assert.Equal(("upper(name)", "!DELETED()"), TagText(dbf, "TOLD"));
        Assert.Equal(("cat+name", ""), TagText(dbf, "TNEW"));

        using (var w = DbfWriter.Open(dbf))
            w.Reindex();
        Assert.Equal(("UPPER(name)", ".NOT.DELETED()"), TagText(dbf, "TOLD"));
        Assert.Equal(("cat+name", ""), TagText(dbf, "TNEW"));
    }

    [Fact]
    public void CreateStandaloneIdx_StoresTheDecompiledText()
    {
        string dbf = NewTable();
        string idx = Path.Combine(_dir, "x.idx");
        using (var w = DbfWriter.Open(dbf))
            w.CreateStandaloneIdx(idx, "UPPER( name )", "MOD(RECNO(), 3) = 0");

        byte[] header = File.ReadAllBytes(idx).AsSpan(0, 512).ToArray();
        Assert.Equal("UPPER(name)", Encoding.ASCII.GetString(header, 16, 220).TrimEnd('\0'));
        Assert.Equal("MOD(RECNO(),3)=0", Encoding.ASCII.GetString(header, 236, 220).TrimEnd('\0'));
    }

    /// <summary>The optimizer matches a typed condition against a tag whose stored KEY is in VFP's spelling
    /// (<c>.AND.</c>, <c>"…"</c>, <c>#</c>, <c>=&gt;</c>) — as every VFP-built index stores it.</summary>
    [Fact]
    public void Optimizer_MatchesATypedConditionToTheDecompiledKey()
    {
        string dbf = NewTable();
        using (var w = DbfWriter.Open(dbf))
            w.CreateTag(new CdxTagDefinition("TCAT", "IIF(cat <> 'C1' AND cat >= 'C0', cat, '  ')"));
        Assert.Equal("IIF(cat#\"C1\".AND.cat=>\"C0\",cat,\"  \")", TagText(dbf, "TCAT").Key);

        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table);
        var plan = QueryOptimizer.Explain(table, cdx, "IIF(CAT != \"C1\" .AND. CAT => \"C0\", CAT, '  ') = 'C2'");
        Assert.Contains("TCAT", plan.UsedTags);
    }
}
