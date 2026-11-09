using System.Text;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SessionTextTests
{
    // Literal cells from the all-byte VFP9 write/read oracle, independent of the product's map resource.
    [Theory]
    [InlineData(1252, 0x6a, 0xe4, 0xdd, 0x7c)]
    [InlineData(1252, 0x64, 0xff, 0xfe, 0x5f)]
    [InlineData(1252, 0x65, 0xe9, 0xe5, 0xe9)]
    [InlineData(1252, 0xc8, 0x9f, 0x59, 0x59)]
    [InlineData(1250, 0x6a, 0xa5, 0xa4, 0xa5)]
    public void BatchAndAdo_PreserveBinaryBytesAndConvertText(int sessionCp, byte mark, byte input, byte stored, byte read)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "t.dbf");
        var encoding = Encoding.GetEncoding(sessionCp);
        string text = encoding.GetString([input, 97]);
        using (var writer = DbfWriter.Create(path,
            [new("txt", 'C', 2), new("memo", 'M'), new("vary", 'V', 2), new("bin", 'C', 2, binary: true), new("bm", 'M', binary: true), new("bv", 'V', 2, binary: true), new("qb", 'Q', 2)],
            new DbfCreateOptions { CodePage = mark, SessionCodePage = sessionCp }))
            writer.AppendRecords(new[] { new object?[] { text, text, text, text, text, text, new byte[] { input, 97 } } });
        using (var table = DbfTable.Open(path))
        {
            var row = table.Records.Single();
            Assert.Equal(stored, row.GetRawField(0)[0]);
            Assert.Equal(stored, row.GetRawField(2)[0]);
            Assert.Equal(input, row.GetRawField(3)[0]);
            Assert.Equal(input, row.GetRawField(5)[0]);
            Assert.Equal(new byte[] { input, 97 }, row["qb"]);
        }
        using var connection = new FoxDbfConnection($"Data Source={temp.Path};SessionCodePage={sessionCp}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT txt,memo,vary,bin,bm,bv,qb FROM t";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        for (int i = 0; i < 6; i++) Assert.Equal(i < 3 ? read : input, encoding.GetBytes(reader.GetString(i))[0]);
        Assert.Equal(new byte[] { input, 97 }, reader.GetValue(6));
    }

    [Fact]
    public void Ado_WithoutSessionCodePage_ReadsTableNativeTextLosslessly()
    {
        Assert.Null(new FoxDbfConnectionStringBuilder("Data Source=x").SessionCodePage);
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "t.dbf");
        using (var writer = DbfWriter.Create(path, [new("txt", 'C', 9)], new DbfCreateOptions { CodePage = 0x6a }))
            writer.AppendRecord("αβγ");
        using var connection = new FoxDbfConnection($"Data Source={temp.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT txt FROM t";
        Assert.Equal("αβγ", ((string)command.ExecuteScalar()!).TrimEnd());
    }

    [Theory]
    [InlineData(0x03, 1252)]
    [InlineData(0xc8, 1250)]
    [InlineData(0x6a, 737)]
    public void NativeSession_KeepsTheOriginalEncoder(byte mark, int sessionCp)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        using var writer = DbfWriter.Create(Path.Combine(temp.Path, "t.dbf"), [new("txt", 'C', 2)],
            new DbfCreateOptions { CodePage = mark, SessionCodePage = sessionCp });
        Assert.Same(writer.Schema.Encoding, writer.Schema.TextEncoding);
    }

    [Theory]
    [InlineData("note", "▌")]
    [InlineData("note+'x'", "|x")]
    public void SqlMemoAssignment_DistinguishesCopyFromSessionExpression(string expression, string expected)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "t.dbf");
        using (var writer = DbfWriter.Create(path, [new("note", 'M')], new DbfCreateOptions { CodePage = 0x6a }))
            writer.AppendRecord("▌");
        using (var session = new VfpSession(new EvaluationContext { SessionCodePage = 1252 }))
        {
            session.OpenDirectory(temp.Path);
            session.Execute("UPDATE t SET note=" + expression);
        }
        using var table = DbfTable.Open(path);
        Assert.Equal(expected, table.Records.Single()[0]);
    }

    // VFP9, startup CP1252, table CP737: E4 F6 FC DF -> DD F2 F9 D8.
    // Reading those bytes back produces 7C 3E B7 2B; conversion is deliberately lossy.
    [Theory]
    [InlineData("writer")]
    [InlineData("sql")]
    [InlineData("ado")]
    [InlineData("micro")]
    public void MemoAndCharacter_WriteAndReadUseVfpBestFit(string pathKind)
    {
        using var temp = new IndexMaintTestSupport.TempDir();
        string path = Path.Combine(temp.Path, "t.dbf");
        using (DbfWriter.Create(path, [new("txt", 'C', 9), new("memo", 'M'), new("vary", 'V', 9)], new DbfCreateOptions { CodePage = 0x6a })) { }
        if (pathKind == "writer")
        {
            using var writer = DbfWriter.Open(path, new DbfOptions { SessionCodePage = 1252 });
            writer.AppendRecord("memo äöüß", "memo äöüß", "memo äöüß");
            writer.UpdateRecord(0, new object?[] { "memo äöüß", "memo äöüß", "memo äöüß" });
        }
        else if (pathKind == "ado")
        {
            using var connection = new FoxDbfConnection($"Data Source={temp.Path};SessionCodePage=1252");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO t VALUES ('memo äöüß','memo äöüß','memo äöüß')";
            command.ExecuteNonQuery();
            command.CommandText = "UPDATE t SET memo='memo äöüß'";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT memo FROM t";
            Assert.Equal("memo |>·+", command.ExecuteScalar());
        }
        else
        {
            using var session = new VfpSession(new EvaluationContext { SessionCodePage = 1252 });
            session.OpenDirectory(temp.Path);
            if (pathKind == "micro")
            {
                using var interpreter = new VfpInterpreter(session, 1252);
                interpreter.Execute("INSERT INTO t VALUES ('memo äöüß','memo äöüß','memo äöüß')\nUSE t\nREPLACE memo WITH 'memo äöüß'");
                Assert.Equal("memo |>·+", interpreter.EvalExpression("memo").AsString);
            }
            else
            {
                session.Execute("INSERT INTO t VALUES ('memo äöüß','memo äöüß','memo äöüß')");
                session.Execute("UPDATE t SET memo='memo äöüß'");
            }
        }
        using var table = DbfTable.Open(path);
        var record = table.Records.Single();
        Assert.Equal("memo ▌≥∙╪", record["memo"]);
        Assert.Equal(new byte[] { 109, 101, 109, 111, 32, 0xdd, 0xf2, 0xf9, 0xd8 }, record.GetRawField(0).ToArray());
        using var view = DbfTable.Open(path, new DbfOptions { SessionCodePage = 1252 });
        foreach (string column in new[] { "txt", "memo", "vary" }) Assert.Equal("memo |>·+", view.Records.Single()[column]);
    }
}
