using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Under SET EXACT OFF a weighted comparison cuts the field to the literal before it weighs it, so matches can sort
/// outside the literal's own key run: <c>'Ab' &lt; 'Á'</c> (key <c>60 61</c> after <c>60 01</c>) and, under SPANISH,
/// <c>'chico' = 'c'</c> (head <c>ch</c> = 63 after <c>c</c> = 62). Rushmore over the tag must still return the rows of a
/// plain scan.
/// </summary>
public sealed class QueryOptimizerCutComparisonTests
{
    private static readonly string[] Values =
        { "Ab", "Á", "Ábc", "A", "Aa", "chico", "cuna", "Cesar", "ch", "c", "dedo", "llama", "luz", "Lima", "l", "zeta" };

    [Theory]
    [InlineData("GENERAL")]
    [InlineData("SPANISH")]
    public void IndexWindow_CoversEveryCutMatch(string collation)
    {
        using var dir = new MicroVfpTestSupport.TempDir("cutcmp");
        string dbf = dir.File("t.dbf");
        using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("NAME", 'C', 10) },
                   new DbfCreateOptions { CodePage = 0x03 }))
        {
            foreach (var v in Values) w.AppendRecord(v);
            w.CreateTag(new CdxTagDefinition("k", "name") { Collation = collation });
        }

        using var table = DbfTable.Open(dbf);
        using var cdx = CdxFile.Open(Path.ChangeExtension(dbf, ".cdx"), table);
        var coll = VfpCollations.ByName(collation, 1252);
        foreach (bool exact in new[] { false, true })
        foreach (var needle in new[] { "Á", "A", "c", "C", "ch", "l", "ll", "L" })
        foreach (var op in new[] { "=", "<", "<=", ">", ">=" })
        {
            var ctx = new EvaluationContext { Collation = coll, Exact = exact };
            string filter = $"name {op} '{needle}'";
            var viaIndex = QueryOptimizer.FindRecords(table, cdx, filter, ctx);
            var scan = QueryOptimizer.FindRecords(table, null, filter, ctx);
            Assert.True(viaIndex.Optimized, filter);
            Assert.True(scan.RecordNumbers.SequenceEqual(viaIndex.RecordNumbers),
                $"{collation} EXACT={exact} {filter}: scan [{string.Join(" ", scan.RecordNumbers)}] index [{string.Join(" ", viaIndex.RecordNumbers)}]");
        }
    }
}
