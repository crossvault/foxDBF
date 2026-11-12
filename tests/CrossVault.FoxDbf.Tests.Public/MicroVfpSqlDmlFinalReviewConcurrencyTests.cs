using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSqlDmlFinalReviewConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertValidationSeesStoredAutoincrement(bool fallback)
    {
        using var dir = new MicroVfpTestSupport.TempDir("rule_autoinc_race");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE t (id I AUTOINC, marker I)");
        int seen = 0, seenRecno = 0;
        bool attempted = false;
        session.InsertRowValidator = (_, _, values, _, recno) =>
        {
            seen = Convert.ToInt32(values["id"]);
            seenRecno = recno;
            if (attempted) return;
            attempted = true;
            // A competing writer changes the counter during validation if the append header
            // is unprotected. The validator must then run again, or prevent that append.
            try
            {
                using var other = DbfWriter.Open(dir.File("t.dbf"));
                other.AppendRecord(new object?[] { 0, 99 });
            }
            catch (IOException) { }
        };
        if (fallback) session.Execute("INSERT INTO t (marker) VALUES (7)");
        else
        {
            using var writer = DbfWriter.Open(dir.File("t.dbf"));
            DmlExecutor.AppendInsertValuesRow(session, writer, (InsertStatement)SqlParser.Parse("INSERT INTO t (marker) VALUES (7)"));
        }
        var row = session.Execute("SELECT id, RECNO() FROM t WHERE marker=7")!.Rows!.Single();
        Assert.True(attempted);
        Assert.Equal(Convert.ToInt32(row[0]), seen);
        Assert.Equal(Convert.ToInt32(row[1]), seenRecno);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NestedInsertUsesItsOwnRules(bool outerHasRule)
    {
        using var dir = new MicroVfpTestSupport.TempDir("nested_rule_target");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session) { EnforceReferentialIntegrity = true };
        vfp.Execute("CREATE TABLE parent (n I" + (outerHasRule ? " CHECK n<0" : "") + ")\nCREATE TABLE child (n I CHECK n>0)\nCLOSE TABLES ALL");
        session.MemoryBridge = new NestedBridge(session);
        var error = Assert.ThrowsAny<Exception>(() => vfp.Execute("INSERT INTO parent FROM ARRAY rows"));
        Assert.Equal(1582, Assert.IsAssignableFrom<IVfpErrorCode>(error).VfpErrorNumber);
        Assert.Empty(session.Execute("SELECT n FROM child")!.Rows!);
    }

    private sealed class NestedBridge(VfpSession session) : IVfpMemoryBridge
    {
        public IReadOnlyList<IReadOnlyList<object?>>? ReadArrayRows(string name)
        {
            session.Execute("INSERT INTO child VALUES (-1)");
            return new[] { new object?[] { -1 } };
        }
        public void StoreQueryIntoArray(string name, int rows, int cols, IReadOnlyList<object?[]> data, IReadOnlyList<SqlColumn>? columns = null) => throw new NotSupportedException();
        public bool TryReadMemvar(string name, out object? value) { value = null; return false; }
        public bool IsObjectQualifier(string name) => false;
    }

    [Theory]
    [InlineData("legacy", false)]
    [InlineData("compact", false)]
    [InlineData("cdx", false)]
    [InlineData("legacy", true)]
    [InlineData("compact", true)]
    [InlineData("cdx", true)]
    public void NestedLateIndexRollbackRestoresBytes(string format, bool commitInner)
    {
        using var dir = new MicroVfpTestSupport.TempDir("nested_idx_bytes");
        DbfDatabaseBuilder.Create(dir.File("probe.dbc"), Array.Empty<DbcTableSpec>());
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.OpenDatabase(dir.File("probe.dbc"));
        using var vfp = new VfpInterpreter(session);
        string index = dir.File("extra." + (format == "cdx" ? "cdx" : "idx"));
        vfp.Execute(MicroVfpNonStructuralTxnTests.Setup(format, false) + "\nUSE t");
        byte[] originalDbf = ReadShared(dir.File("t.dbf"));
        byte[] originalIndex = ReadShared(index);
        vfp.Execute("BEGIN TRANSACTION\nREPLACE id WITH 2");
        byte[] innerDbf = ReadShared(dir.File("t.dbf"));
        vfp.Execute("BEGIN TRANSACTION\nSET INDEX TO " + Path.GetFileName(index) + "\nREPLACE id WITH 3");
        Assert.False(originalIndex.SequenceEqual(ReadShared(index)));
        vfp.Execute(commitInner ? "END TRANSACTION" : "ROLLBACK");
        if (!commitInner)
        {
            Assert.Equal(innerDbf, ReadShared(dir.File("t.dbf")));
            Assert.Equal(originalIndex, ReadShared(index));
        }
        vfp.Execute("ROLLBACK\nUSE");
        Assert.Equal(originalDbf, ReadShared(dir.File("t.dbf")));
        Assert.Equal(originalIndex, ReadShared(index));
    }
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

}
