using System.Diagnostics;
using System.Globalization;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlSingleTableRushmoreTests
{
    private readonly ITestOutputHelper _output;

    public SqlSingleTableRushmoreTests(ITestOutputHelper output) => _output = output;

    internal static readonly string[] SelectCases =
    {
        "SELECT id, name FROM person p WHERE p.id >= 3 AND p.id <= 8",
        "SELECT id, name FROM person p WHERE p.name = 'Sm' ORDER BY id DESC",
        "SELECT id FROM person WHERE name == 'Brown' ORDER BY id",
        "SELECT id FROM person WHERE name < 'Smith' ORDER BY id",
        "SELECT id FROM person WHERE name >= 'Smith' ORDER BY id",
        "SELECT TOP 3 id, name FROM person WHERE id >= 3 ORDER BY amount DESC, id",
        "SELECT DISTINCT city FROM person WHERE id >= 3 ORDER BY city",
        "SELECT id FROM person WHERE id = 7",
        "SELECT id FROM person WHERE name = '' ORDER BY id",
        "SELECT id FROM person WHERE id < 3 OR id > 8 ORDER BY id",
        "SELECT id FROM person WHERE name = 'Smith               x' ORDER BY id",
        "SELECT id FROM person WHERE name >= 'Smith               x' ORDER BY id",
        "SELECT id FROM person WHERE ALLTRIM(name) = 'Smithson' ORDER BY id",
        "SELECT id FROM person WHERE (ALLTRIM(name) >= 'Smithson') AND id < 5 ORDER BY id",
    };

    public static IEnumerable<object[]> Settings()
    {
        foreach (bool deleted in new[] { false, true })
        foreach (bool exact in new[] { false, true })
        foreach (bool ansi in new[] { false, true })
            yield return new object[] { deleted, exact, ansi };
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void IndexedAndPlain_SelectAndDmlAreIdentical(bool deleted, bool exact, bool ansi)
    {
        using var indexed = new SqlTestSupport.TempDir();
        using var plain = new SqlTestSupport.TempDir();
        CreateTable(indexed.File("person.dbf"));
        CreateTable(plain.File("person.dbf"), withIndex: false);
        using var si = NewSession(indexed.Path, deleted, exact, ansi);
        using var sp = NewSession(plain.Path, deleted, exact, ansi);
        var probe = new RecordingAccelerator();
        si.Accelerator = probe;
        foreach (string sql in SelectCases)
            Assert.Equal(Format(sp.Execute(sql)!), Format(si.Execute(sql)!));
        Assert.True(probe.Queries[0].Optimized);

        // Every original key below 5 moves to another matching key: an index cursor must not revisit it.
        foreach (string sql in new[]
        {
            "UPDATE person SET id = id + 1 WHERE id < 5",
            "UPDATE person SET name = 'Brown' WHERE name = 'Sm'",
            "DELETE FROM person WHERE id >= 3 AND id <= 7",
        })
            Assert.Equal(sp.Execute(sql)!.AffectedRecords, si.Execute(sql)!.AffectedRecords);
        si.Context.Deleted = false;
        sp.Context.Deleted = false;
        Assert.Equal(Format(sp.Execute("SELECT * FROM person")!), Format(si.Execute("SELECT * FROM person")!));
        // The header's structural-index flag differs; every stored record byte must agree.
        Assert.Equal(RecordBytes(plain.File("person.dbf")), RecordBytes(indexed.File("person.dbf")));
    }

    internal static void CreateTable(string path, bool withIndex = true)
    {
        SqlTestSupport.CreatePersonTable(path, withIndex);
        if (!withIndex) return;
        using var writer = DbfWriter.Open(path);
        writer.CreateTag(new CdxTagDefinition("TTRIM", "ALLTRIM(NAME)"));
    }

    [Fact]
    public void AnsiOff_ShortExpressionKey_DoesNotDropPrefixMatches()
    {
        using var dir = new SqlTestSupport.TempDir();
        CreateTable(dir.File("person.dbf"));
        using var session = NewSession(dir.Path, true, false, false);
        Assert.Equal("1;2;3", Format(session.Execute("SELECT id FROM person WHERE ALLTRIM(name) = 'Smithson' ORDER BY id")!));
        using var table = DbfTable.Open(dir.File("person.dbf"));
        using var cdx = CdxFile.Open(dir.File("person.cdx"), table);
        var context = session.SqlContext();
        const string filter = "ALLTRIM(name) = 'Smithson'";
        Assert.Equal(3, QueryOptimizer.Count(table, cdx, filter, context));
        Assert.Equal(OptimizationLevel.None, QueryOptimizer.Explain(table, cdx, filter, context).Overall);
        Assert.Equal(3, QueryOptimizer.Count(table, cdx, "(" + filter + ") AND id < 5", context));
        Assert.Equal(OptimizationLevel.Partial,
            QueryOptimizer.Explain(table, cdx, "(" + filter + ") AND id < 5", context).Overall);
    }

    [Fact]
    public void Dml_UsesRedirectedIndex_AndClosesHandles()
    {
        using var live = new SqlTestSupport.TempDir();
        using var copy = new SqlTestSupport.TempDir();
        SqlTestSupport.CreatePersonTable(live.File("person.dbf"));
        SqlTestSupport.CreatePersonTable(copy.File("person.dbf"));
        using (var session = NewSession(copy.Path, true, false, false))
            Assert.Equal(1, session.Execute("UPDATE person SET id = 30 WHERE id = 3")!.AffectedRecords);
        byte[] liveDbf = File.ReadAllBytes(live.File("person.dbf"));
        byte[] liveCdx = File.ReadAllBytes(live.File("person.cdx"));
        var probe = new RecordingAccelerator();
        using (var session = NewSession(live.Path, true, false, false))
        {
            session.Accelerator = probe;
            session.TxBeginWritePath = _ => copy.File("person.dbf");
            session.TxRedirectReadPath = _ => copy.File("person.dbf");
            Assert.Equal(1, session.Execute("UPDATE person SET id = 31 WHERE id = 30")!.AffectedRecords);
            Assert.Single(session.Execute("SELECT id FROM person WHERE id = 31")!.Rows);
            Assert.Equal(1, session.Execute("DELETE FROM person WHERE id = 31")!.AffectedRecords);
            Assert.Empty(session.Execute("SELECT id FROM person WHERE id = 31")!.Rows);
            Assert.All(probe.Queries, q => Assert.True(q.Optimized));
        }
        Assert.Equal(liveDbf, File.ReadAllBytes(live.File("person.dbf")));
        Assert.Equal(liveCdx, File.ReadAllBytes(live.File("person.cdx")));
        using var exclusive = new FileStream(copy.File("person.cdx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Measurement_100000Rows()
    {
        using var dir = new SqlTestSupport.TempDir();
        using (var writer = DbfWriter.Create(dir.File("large.dbf"), new[]
        {
            new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("VALUE", 'I', 4),
        }))
        {
            for (int i = 1; i <= 100_000; i++) writer.AppendRecord(i, i);
            writer.CreateTag(new CdxTagDefinition("TID", "ID"));
        }
        var probe = new RecordingAccelerator();
        using var session = NewSession(dir.Path, true, false, false);
        session.Accelerator = probe;
        foreach (bool optimize in new[] { false, true })
        {
            session.Context.Optimize = optimize;
            foreach (string sql in new[]
            {
                "SELECT id FROM large WHERE id = 50000",
                "UPDATE large SET value = value + 1 WHERE id = 50000",
                "DELETE FROM large WHERE id = 60000",
            })
            {
                // Restore the delete target for the second measurement.
                if (sql.StartsWith("DELETE", StringComparison.Ordinal))
                {
                    using var writer = DbfWriter.Open(dir.File("large.dbf"));
                    writer.Recall(59999);
                }
                var watch = Stopwatch.StartNew();
                var result = session.Execute(sql)!;
                watch.Stop();
                var query = probe.Queries[^1];
                _output.WriteLine($"100000 rows; optimize={optimize}; {sql}; {watch.Elapsed.TotalMilliseconds:F2} ms; scanned={query.RecordsScanned}");
                Assert.Equal(optimize, query.Optimized);
                if (sql.StartsWith("SELECT", StringComparison.Ordinal)) Assert.Single(result.Rows);
                else Assert.Equal(1, result.AffectedRecords);
            }
        }
    }

    internal static VfpSession NewSession(string dir, bool deleted, bool exact, bool ansi)
    {
        var session = new VfpSession(new EvaluationContext { Deleted = deleted, Exact = exact, Ansi = ansi });
        session.OpenDirectory(dir);
        return session;
    }

    internal static byte[] RecordBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int start = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        return bytes[start..];
    }

    internal static string Format(SqlResult result)
        => string.Join(";", result.Rows.Select(row => string.Join("|", row.Select(v =>
            v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture)))));

    [Theory]
    [InlineData("SELECT id FROM person WHERE person.id = 3", false)]
    [InlineData("UPDATE person SET amount = amount + 1 WHERE person.id = 3", false)]
    [InlineData("DELETE FROM person WHERE person.id = 3", false)]
    [InlineData("SELECT id FROM p WHERE p.id = 3", true)]
    [InlineData("UPDATE p SET amount = amount + 1 WHERE p.id = 3", true)]
    [InlineData("DELETE FROM p WHERE p.id = 3", true)]
    public void SingleTable_UsesStructuralCdx(string sql, bool openAlias)
    {
        using var dir = new SqlTestSupport.TempDir();
        SqlTestSupport.CreatePersonTable(dir.File("person.dbf"));
        var probe = new RecordingAccelerator();
        using var session = new VfpSession { Accelerator = probe };
        session.OpenDirectory(dir.Path);
        if (openAlias) session.Use("person", alias: "p");

        var result = session.Execute(sql)!;

        var query = Assert.Single(probe.Queries);
        Assert.True(query.Optimized, "SQL must pass the structural CDX to Rushmore.");
        Assert.Equal(new[] { 3 }, query.RecordNumbers);
        Assert.Equal(1, query.RecordsScanned);
        if (sql.StartsWith("SELECT", StringComparison.Ordinal)) Assert.Single(result.Rows);
        else Assert.Equal(1, result.AffectedRecords);
    }

    internal sealed class RecordingAccelerator : IQueryAccelerator
    {
        public List<QueryResult> Queries { get; } = new();

        public QueryResult FindRecords(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
        {
            var result = QueryOptimizer.FindRecords(table, cdx, filter, context);
            Queries.Add(result);
            return result;
        }

        public QueryPlan Explain(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
            => QueryOptimizer.Explain(table, cdx, filter, context);
    }
}
