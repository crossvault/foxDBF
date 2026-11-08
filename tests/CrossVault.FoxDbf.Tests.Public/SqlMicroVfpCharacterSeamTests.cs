using System.Text;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlMicroVfpCharacterSeamTests
{
    public static TheoryData<string, decimal> Expressions => new()
    {
        { "LEN(bin)", 4 },
        { "ASC(bin)", 128 },
        { "IIF(bin = CHR(128), 1, 0)", 1 },
        // The euro literal in a CP1252 PRG is byte 0x80; ParseFile preserves literal bytes as Latin1.
        { "IIF(bin = '\u0080', 1, 0)", 1 },
        { "LEN(utf)", 4 },
        { "ASC(utf)", 195 },
        { "LEN(vc)", 3 },
        { "ASC(RIGHT(vc, 1))", 32 },
        { "IIF(ISNULL(vn), 1, 0)", 1 },
        { "IIF(ISNULL(cn), 1, 0)", 1 },
    };

    internal static void Create(string path)
    {
        using var writer = DbfWriter.Create(path, new[]
        {
            new DbfColumnDef("bin", 'C', 4, binary: true),
            new DbfColumnDef("utf", 'C', 4),
            new DbfColumnDef("vc", 'V', 6),
            new DbfColumnDef("vn", 'V', 6, nullable: true),
            new DbfColumnDef("cn", 'C', 4, nullable: true),
        }, new DbfCreateOptions { CodePage = 0x03 });
        writer.AppendRecord(new object?[] { "\u0080", "\u00c3\u00a4", "ab ", null, null });
    }

    [Theory]
    [MemberData(nameof(Expressions))]
    public void CharacterReads_MatchVfp9_InSqlAndPrg(string expression, decimal expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("char_seam");
        Create(dir.File("chars.dbf"));
        AssertReads(dir.Path, expression, expected);
    }

    internal static void AssertReads(string path, string expression, decimal expected)
    {
        using var session = new VfpSession();
        session.OpenDirectory(path);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("USE chars");
        var program = Path.Combine(path, "readchar.prg");
        File.WriteAllText(program, "FUNCTION ReadChar\nRETURN " + expression + "\nENDFUNC", Encoding.Latin1);
        interpreter.LoadFile(program);
        var prg = interpreter.Call("ReadChar").AsNumber;
        var sql = Convert.ToDecimal(session.Execute("SELECT " + expression + " AS value FROM chars")!.Rows.Single()[0]);
        interpreter.Execute("SELECT " + expression + " AS value FROM chars INTO ARRAY result");
        var hosted = interpreter.EvalExpression("result[1, 1]").AsNumber;
        Assert.True(prg == expected && sql == expected && hosted == expected,
            $"{expression}: expected={expected}, PRG={prg}, SQL={sql}, hosted SQL={hosted}");
    }
}
