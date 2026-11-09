using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class SessionCollationTests
{
    [Fact]
    public void FullAvailabilityMatrix_IsExactOrUnsupported()
    {
        // Union of VFP9 COLL|cp|name|OK cells from the October 22 probes,
        // completed by the November session matrix for the three CP10029 cells.
        // Phase 4 sequences are deliberately excluded. Refused cells must never be approximate.
        int[] pages = [437, 620, 737, 850, 852, 857, 861, 863, 865, 866, 874, 895, 932, 936, 949, 950, 1250, 1251, 1252, 1253, 1254, 1257, 10000, 10006, 10007, 10029];
        var allowed = new Dictionary<string, int[]>
        {
            ["MACHINE"] = [437, 620, 737, 850, 852, 857, 861, 863, 865, 866, 874, 895, 932, 936, 949, 950, 1250, 1251, 1252, 1253, 1254, 1257, 10000, 10006, 10007, 10029],
            ["GENERAL"] = [437, 620, 850, 852, 857, 861, 863, 865, 895, 1250, 1252, 1254, 10000, 10029],
            ["UNIQWT"] = [437, 850, 1252, 10000],
            ["CZECH"] = [852, 895, 1250, 10029],
            ["DUTCH"] = [437, 850, 1252, 10000],
            ["GERMAN"] = [437, 1252, 10000],
            ["GREEK"] = [737, 1253, 10006],
            ["HUNGARY"] = [852, 1250],
            ["ICELAND"] = [437, 850, 861, 1252],
            ["NORDAN"] = [437, 850, 865, 1252, 10000],
            ["POLISH"] = [620, 852, 1250, 10029],
            ["RUSSIAN"] = [866, 1251, 10007],
            ["SLOVAK"] = [852, 895, 1250, 10029],
            ["SPANISH"] = [437, 850, 1252, 10000],
            ["SWEFIN"] = [437, 850, 865, 1252, 10000],
            ["TURKISH"] = [857, 1254],
        };
        var failures = new List<string>();
        foreach (var (sequence, supported) in allowed)
            foreach (int page in pages)
            {
                var expected = supported.Contains(page) ? CollationSupport.Exact : CollationSupport.Unsupported;
                var actual = VfpCollations.Resolve(sequence, page, out _);
                if (expected != actual) failures.Add($"{sequence}/{page}: expected {expected}, got {actual}");
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void IndexCase_UsesSessionBytesNotTableLanguage()
    {
        var tableEncoding = Encodings.ResolveEncoding(0xca);
        var context = new EvaluationContext { Encoding = tableEncoding };
        context.SessionCodePage = 1252;
        string value = tableEncoding.GetString([0xfd]);
        string actual = VfpExpression.Parse($"UPPER('{value}')").Evaluate(null!, context).AsString;
        Assert.Equal(new byte[] { 0xdd }, tableEncoding.GetBytes(actual));
    }

    [Theory]
    [InlineData(null, 0xfd)]
    [InlineData(1254, 0xfd)]
    [InlineData(1252, 0xdd)]
    public void WriterSession_CdxAndBothIdxFormatsUseTheSameCaseTable(int? sessionCp, byte expected)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "t.dbf");
        string compact = Path.Combine(temp.Path, "compact.idx");
        string legacy = Path.Combine(temp.Path, "legacy.idx");
        string value = Encodings.ResolveEncoding(0xca).GetString([0xfd]);
        using (var writer = DbfWriter.Create(path, [new DbfColumnDef("name", 'C', 1)],
            new DbfCreateOptions { CodePage = 0xca, SessionCodePage = sessionCp }))
        {
            writer.AppendRecord(value);
            writer.CreateTag(new CdxTagDefinition("u", "UPPER(name)"));
            writer.CreateStandaloneIdx(legacy, "UPPER(name)");
            writer.CreateCompactIdx(compact, "UPPER(name)");
        }
        using (var writer = DbfWriter.Open(path, new DbfOptions { SessionCodePage = sessionCp, LockMode = LockMode.Exclusive }))
        {
            writer.OpenCompactIdx(compact);
            writer.AppendRecord(value);
            writer.UpdateRecord(0, new object?[] { value });
            writer.Reindex();
            writer.Pack();
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        Assert.Equal(new[] { expected, expected }, cdx.Tag("u")!.EnumerateEntries().Select(e => e.Key[0]));
        using var compactIndex = IdxFile.Open(compact);
        Assert.Equal(new[] { expected, expected }, compactIndex.EnumerateEntries().Select(e => e.Key[0]));
        using var legacyIndex = IdxFile.Open(legacy);
        Assert.Equal(expected, Assert.Single(legacyIndex.EnumerateEntries()).Key[0]);
    }

    [Fact]
    public void InterpreterSession_IsExplicitAndSurvivesDataSessionChanges()
    {
        using var session = new VfpSession();
        using var interpreter = new VfpInterpreter(session, 1254);
        Assert.Equal(1254m, interpreter.EvalExpression("CPCURRENT()").AsNumber);
        interpreter.Execute("SET COLLATE TO 'TURKISH'");
        Assert.Equal(253m, interpreter.EvalExpression("ASC(UPPER(CHR(253)))").AsNumber);
        int id = interpreter.CreateDataSession();
        interpreter.Execute($"SET DATASESSION TO {id}");
        Assert.Equal(1254m, interpreter.EvalExpression("CPCURRENT()").AsNumber);
        Assert.Equal(253m, interpreter.EvalExpression("ASC(UPPER(CHR(253)))").AsNumber);
        interpreter.Execute("SET COLLATE TO 'TURKISH'\nSET DATASESSION TO 1");
        Assert.Equal("TURKISH", interpreter.EvalExpression("SET('COLLATE')").AsString);
    }

    [Fact]
    public void SeekCaseExpression_ConvertsSessionBytesWithVfpBestFit()
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        using (var writer = DbfWriter.Create(Path.Combine(temp.Path, "t.dbf"), [new DbfColumnDef("name", 'C', 1)],
            new DbfCreateOptions { CodePage = 1 }))
        {
            writer.AppendRecord("Y");
            writer.AppendRecord("S");
        }
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session, 1252);
        interpreter.Execute("USE t\nINDEX ON UPPER(name) TAG u");
        Assert.True(interpreter.EvalExpression("SEEK(UPPER(CHR(253)))").AsLogical);
        Assert.Equal(1m, interpreter.EvalExpression("RECNO()").AsNumber);
        Assert.True(interpreter.EvalExpression("SEEK(UPPER(CHR(138)))").AsLogical);
        Assert.Equal(2m, interpreter.EvalExpression("RECNO()").AsNumber);
    }

    [Fact]
    public void WesternSession_RejectsCzechAtSetCollate()
    {
        using var session = new VfpSession();
        using var interpreter = new VfpInterpreter(session, 1252);
        var error = Assert.Throws<MicroVfpRuntimeException>(() => interpreter.Execute("SET COLLATE TO 'CZECH'"));
        Assert.Equal(1915, error.VfpErrorNumber);
        Assert.Equal("MACHINE", interpreter.EvalExpression("SET('COLLATE')").AsString);
    }
}
