using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpSnapshotCleanupTests
{
    [Fact]
    public void SuccessfulBoundInsert_CleansStatementSnapshotAfterEveryStatement()
    {
        using var f = new Fixture(initialRows: 0);
        f.LoadTrigger("__RI_INSERT_items", allowed: true);

        for (int id = 1; id <= 4; id++)
        {
            f.Run($"INSERT INTO items (id, value) VALUES ({id}, {id * 10})");

            Assert.Equal(0, f.Interp.SnapshotTempFileCount());
            Assert.Equal(id, f.Num("RECCOUNT()"));
        }
    }

    [Fact]
    public void FailedBoundInsert_CleansStatementSnapshot()
    {
        using var f = new Fixture(initialRows: 1);
        f.LoadTrigger("__RI_INSERT_items", allowed: true);

        // MicroVfpInsertFailureVfp9OracleTests: error 12, with no appended row.
        f.Run("""
            DIMENSION errors[1]
            TRY
                INSERT INTO items (missing) VALUES (2)
            CATCH TO problem
                savedError = ERROR()
                errorCount = AERROR(errors)
            ENDTRY
            """);

        Assert.Equal(12m, f.Num("problem.ErrorNo"));
        Assert.Equal(0m, f.Num("savedError"));
        Assert.Equal(1m, f.Num("errorCount"));
        Assert.Equal(12m, f.Num("errors[1]"));
        Assert.Equal(0, f.Interp.SnapshotTempFileCount());
        Assert.Equal(1m, f.Num("RECCOUNT()"));
    }

    [Fact]
    public void SuccessfulReplace_OnDeletedRow_CleansParentFallbackSnapshot()
    {
        using var f = new Fixture(initialRows: 1);
        f.LoadTrigger("__RI_UPDATE_items", allowed: true);

        f.Run("GO 1\nDELETE\nREPLACE value WITH 99");

        Assert.Equal(0, f.Interp.SnapshotTempFileCount());
        Assert.True(f.Bool("DELETED()"));
        f.Run("RECALL");
        Assert.Equal(99m, f.Num("value"));
    }

    [Fact]
    public void SuccessfulCandidateReplace_OnDeletedRow_CleansFallbackSnapshotAndMaintainsIndex()
    {
        using var f = new Fixture(initialRows: 2);
        f.Run("INDEX ON id TAG cid CANDIDATE\nGO 2\nDELETE\nREPLACE id WITH 3");

        Assert.Equal(0, f.Interp.SnapshotTempFileCount());
        Assert.True(f.Bool("DELETED()"));
        f.Run("RECALL");
        Assert.True(f.Bool("SEEK(3, 'items', 'cid')"));
        Assert.Equal(2m, f.Num("RECNO()"));
        Assert.False(f.Bool("SEEK(2, 'items', 'cid')"));
    }

    [Fact]
    public void AbortedBoundInsert_CleanupRemainsIdempotent()
    {
        using var f = new Fixture(initialRows: 1);
        f.LoadTrigger("__RI_INSERT_items", allowed: false);

        Assert.Equal(1539, Assert.Throws<MicroVfpRuntimeException>(() =>
            f.Run("INSERT INTO items (id, value) VALUES (2, 20)")).VfpErrorNumber);

        Assert.Equal(0, f.Interp.SnapshotTempFileCount());
        Assert.Equal(1m, f.Num("RECCOUNT()"));
    }

    [Fact]
    public void CandidateViolation_OnDeletedRow_CleanupRemainsIdempotent()
    {
        using var f = new Fixture(initialRows: 2);
        f.Run("INDEX ON id TAG cid CANDIDATE\nGO 2\nDELETE");

        var ex = Assert.Throws<MicroVfpRuntimeException>(() => f.Run("REPLACE id WITH 1"));

        Assert.Equal(1884, ex.VfpErrorNumber);
        Assert.Equal(0, f.Interp.SnapshotTempFileCount());
        using var table = DbfTable.Open(f.DbfPath, new DbfOptions { LockMode = LockMode.Shared });
        Assert.True(table.IsRecordDeleted(1));
    }

    [Theory]
    [InlineData("END TRANSACTION", 2)]
    [InlineData("ROLLBACK", 1)]
    public void TransactionRetainsOnlyOwnedSnapshotUntilFrameEnds(string finish, int expectedRows)
    {
        using var f = new Fixture(initialRows: 1);
        f.LoadTrigger("__RI_INSERT_items", allowed: true);

        f.Run("BEGIN TRANSACTION\nINSERT INTO items (id, value) VALUES (2, 20)");

        Assert.True(f.Interp.SnapshotTempFileCount() > 0,
            "the transaction-owned pre-image must remain live until commit or rollback");

        f.Run(finish);

        Assert.Equal(0, f.Interp.SnapshotTempFileCount());
        Assert.Equal(expectedRows, f.Num("RECCOUNT()"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = new("snapshot_cleanup");
        public string DbfPath { get; }
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }

        public Fixture(int initialRows)
        {
            DbfPath = _dir.File("items.dbf");
            using (var writer = DbfWriter.Create(DbfPath,
                [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("VALUE", 'I', 4)]))
            {
                for (int id = 1; id <= initialRows; id++) writer.AppendRecord(id, id * 10);
            }

            Session = new VfpSession();
            Session.OpenDirectory(_dir.Path);
            Interp = new VfpInterpreter(Session) { EnforceReferentialIntegrity = true };
            Run("USE items");
        }

        public void LoadTrigger(string name, bool allowed)
            => Run($"PROCEDURE {name}\nRETURN {(allowed ? ".T." : ".F.")}\nENDPROC");

        public void Run(string source) => Interp.Execute(source);
        public decimal Num(string expression) => Interp.EvalExpression(expression).AsNumber;
        public bool Bool(string expression) => Interp.EvalExpression(expression).AsLogical;

        public void Dispose()
        {
            Interp.Dispose();
            Session.Dispose();
            _dir.Dispose();
        }
    }
}
