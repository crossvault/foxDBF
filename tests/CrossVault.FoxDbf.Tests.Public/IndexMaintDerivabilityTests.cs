using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Project-review finding 5.1 — review item 2: KEY-derivation must be RESOLVE-aware, not merely
/// length-aware. A tag whose KEY expression names a field ABSENT from the physical <c>.dbf</c> schema (the
/// canonical DBC long-name case) can only be derived to WRONG (all-blank) key bytes — and, fatally, when the
/// derived fallback length (C, 10) coincides with the real stored key length, the old length-only guard let
/// it through and silently inserted blank keys on the next write. The write path must instead REFUSE to
/// change the table and must preserve the sidecar, as for an unavailable VFP index expression (Q2).
///
/// SAFETY: throwaway temp dir only; no committed fixture is touched.
/// </summary>
public sealed class IndexMaintDerivabilityTests
{
    [Fact]
    public void AppendWithSameLengthUnresolvableKey_RefusesBeforeChangingFiles()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        // A C(10) column so a same-length coincidence is possible: an unresolvable KEY infers the fallback
        // (Character, 10) — byte-for-byte the SAME stored key length as this real C(10) column, so a
        // length-only guard could not tell them apart.
        var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { "ALPHA" }, new object?[] { "BRAVO" } },
            new CdxTagDefinition("NAMETAG", "NAME"));

        string cdx = Path.ChangeExtension(dbf, ".cdx");

        // Patch ONLY the tag's KEY-expression text in the .cdx from the real column "NAME" to a same-byte-length
        // name "ZZZZ" that does NOT exist in the schema — leaving the stored key LENGTH (10) untouched. This
        // reproduces the reviewer's coincidence: an unresolvable KEY whose derived length matches on disk.
        PatchKeyExpression(cdx, "name", "zzzz");   // stored decompiled: field names lower-case

        byte[] dbfBefore = File.ReadAllBytes(dbf);
        byte[] cdxBefore = File.ReadAllBytes(cdx);
        using (var w = DbfWriter.Open(dbf))
        {
            Assert.Throws<DbfWriteException>(() => w.AppendRecord("CHARLIE"));
            Assert.False(w.ReindexNeeded);
            Assert.Equal(2, w.RecordCount);
        }

        Assert.Equal(dbfBefore, File.ReadAllBytes(dbf));
        Assert.Equal(cdxBefore, File.ReadAllBytes(cdx));
    }

    /// <summary>Replace the first NUL-terminated occurrence of <paramref name="from"/> (a tag KEY expression)
    /// in a compound <c>.cdx</c> with <paramref name="to"/> (which MUST be the same byte length, so no header
    /// length field needs updating).</summary>
    private static void PatchKeyExpression(string cdx, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);
        byte[] bytes = File.ReadAllBytes(cdx);
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(from + "\0");
        byte[] repl = System.Text.Encoding.ASCII.GetBytes(to);

        int at = IndexOf(bytes, needle);
        Assert.True(at >= 0, $"key expression '{from}' not found in {cdx}");
        for (int i = 0; i < repl.Length; i++)
            bytes[at + i] = repl[i];
        File.WriteAllBytes(cdx, bytes);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
