using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>A [Fact] that runs only where file names are case-sensitive (Linux CI); Windows and macOS skip it.</summary>
public sealed class CaseSensitiveFileSystemFactAttribute : FactAttribute
{
    public CaseSensitiveFileSystemFactAttribute()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Skip = "Needs a case-sensitive file system (Linux): NAME.CDX vs name.cdx are the same file here.";
    }
}

/// <summary>
/// VFP writes sidecars as <c>NAME.CDX</c> / <c>NAME.FPT</c>. On a case-sensitive file system the structural
/// index must still be found — for reading AND for maintenance — instead of being ignored (stale index) or
/// shadowed by a new lower-case <c>name.cdx</c>.
/// </summary>
public sealed class SidecarCaseTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public SidecarCaseTests()
    {
        foreach (var file in Directory.GetFiles(Fixtures.VfpTest("exprtags")))
            if (!file.EndsWith(".prg", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(_dir.Path, Path.GetFileName(file)));   // keeps exprtags.CDX
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ChangeExtension_WithoutAnExistingSidecar_ReturnsThePlainPath()
    {
        string dbf = Path.Combine(_dir.Path, "none.dbf");
        Assert.Equal(Path.ChangeExtension(dbf, ".cdx"), SidecarFile.ChangeExtension(dbf, ".cdx"));
        Assert.Null(SidecarFile.FindCaseInsensitive(Path.ChangeExtension(dbf, ".cdx")));
    }

    [CaseSensitiveFileSystemFact]
    public void UpperCaseCdx_IsFound_ForReading()
    {
        string dbf = Path.Combine(_dir.Path, "exprtags.dbf");
        Assert.Equal(Path.Combine(_dir.Path, "exprtags.CDX"), SidecarFile.ChangeExtension(dbf, ".cdx"));

        using var session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        session.Use("exprtags");
        Assert.NotNull(session.FindAreaByAlias("exprtags")!.Cdx);
    }

    [CaseSensitiveFileSystemFact]
    public void UpperCaseCdx_IsMaintained_OnAppend_AndNoLowerCaseTwinAppears()
    {
        string dbf = Path.Combine(_dir.Path, "exprtags.dbf");
        using (var w = DbfWriter.Open(dbf))
            w.AppendRecord(5, "zulu", 1m, new DateOnly(2022, 2, 2), 1m);

        Assert.False(File.Exists(Path.Combine(_dir.Path, "exprtags.cdx")));
        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(Path.Combine(_dir.Path, "exprtags.CDX"), table);
        Assert.Equal(5u, cdx.Tag("TID")!.Seek(5));
    }
}
