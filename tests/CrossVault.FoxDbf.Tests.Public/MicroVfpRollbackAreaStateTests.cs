using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpRollbackAreaStateTests
{
    [Fact]
    public void Rollback_PreservesPerAreaFilterAndSameFileRelationState()
    {
        using var f = new Fixture();
        f.Run(
            "USE items IN 0 ALIAS parent\n" +
            "USE items AGAIN IN 0 ALIAS child\n" +
            "SELECT child\nSET ORDER TO tid\n" +
            "SELECT parent\nSET FILTER TO id >= 2\n" +
            "SET RELATION TO id INTO child\nSET SKIP TO child\nGO TOP");
        Assert.Equal(2m, f.Num("RECNO()"));
        Assert.Equal(2m, f.Num("RECNO('child')"));

        f.Run("BEGIN TRANSACTION\nREPLACE name WITH 'changed'\nROLLBACK");

        Assert.Equal("ID=>2", f.Str("FILTER()")); // VFP9 decompiles >= as => (M5 operator oracle).
        Assert.Equal("id INTO CHILD", f.Str("SET('RELATION')"));
        Assert.Equal("CHILD", f.Str("SET('SKIP')"));
        Assert.Equal(2m, f.Num("RECNO()"));
        Assert.Equal(2m, f.Num("RECNO('child')"));
    }

    [Fact]
    public void Rollback_PreservesDescendingOrderAndSetKeyRangeNavigation()
    {
        using var f = new Fixture();
        f.Run("USE items\nSET ORDER TO tid DESCENDING\nSET KEY TO RANGE 2, 4\nGO TOP");
        Assert.Equal(4m, f.Num("id"));

        f.Run("BEGIN TRANSACTION\nREPLACE name WITH 'changed'\nROLLBACK");

        Assert.Equal(4m, f.Num("id"));
        f.Run("GO BOTTOM");
        Assert.Equal(2m, f.Num("id"));
    }

    [Fact]
    public void Rollback_PreservesExternalIndexControllingOrderAndSeek()
    {
        using var f = new Fixture();
        f.Run(
            "USE items\n" +
            "INDEX ON name TAG ext OF extra.cdx\n" +
            "SET INDEX TO extra.cdx ORDER ext\nGO TOP");
        Assert.Equal(2m, f.Num("id"));
        Assert.True(f.Bool("SEEK('echo', 'items', 'ext')"));
        Assert.Equal(3m, f.Num("RECNO()"));
        f.Run("GO TOP");

        f.Run("BEGIN TRANSACTION\nREPLACE name WITH 'changed'\nROLLBACK");

        Assert.Equal(2m, f.Num("id"));
        Assert.True(f.Bool("SEEK('echo', 'items', 'ext')"));
        Assert.Equal(3m, f.Num("RECNO()"));
    }

    [Fact]
    public void Rollback_PreservesLocateContinueState()
    {
        using var f = new Fixture();
        f.Run("USE items\nLOCATE FOR id = 2 OR id = 4");
        Assert.Equal(2m, f.Num("RECNO()"));

        f.Run("BEGIN TRANSACTION\nREPLACE name WITH 'changed'\nROLLBACK\nGO 2");
        f.Run("CONTINUE");

        Assert.True(f.Bool("FOUND()"));
        Assert.Equal(4m, f.Num("RECNO()"));
    }

    [Fact]
    public void Rollback_OfChildFile_ReappliesRelationOwnedByStillOpenParent()
    {
        using var dir = new MicroVfpTestSupport.TempDir("rollback_child_relation");
        using (var writer = DbfWriter.Create(dir.File("parent.dbf"),
            [new DbfColumnDef("ID", 'I', 4)]))
        {
            writer.AppendRecord(1);
            writer.AppendRecord(2);
        }
        using (var writer = DbfWriter.Create(dir.File("child.dbf"),
            [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("VALUE", 'I', 4)]))
        {
            writer.AppendRecord(1, 10);
            writer.AppendRecord(2, 20);
            writer.CreateTag(new CdxTagDefinition("tid", "id"));
        }

        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interp = new VfpInterpreter(session);
        interp.Execute(
            "USE parent IN 0 ALIAS parent\n" +
            "USE child IN 0 ALIAS child\n" +
            "SELECT child\nSET ORDER TO tid\n" +
            "SELECT parent\nGO 2\nSET RELATION TO id INTO child");
        Assert.Equal(2m, interp.EvalExpression("RECNO('child')").AsNumber);

        interp.Execute("SELECT child\nBEGIN TRANSACTION\nREPLACE value WITH 99\nROLLBACK");

        Assert.Equal(2m, interp.EvalExpression("RECNO('parent')").AsNumber);
        Assert.Equal(2m, interp.EvalExpression("RECNO('child')").AsNumber);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = new("rollback_area_state");
        private readonly VfpSession _session;
        private readonly VfpInterpreter _interp;

        public Fixture()
        {
            string dbf = _dir.File("items.dbf");
            using (var writer = DbfWriter.Create(dbf,
                [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("NAME", 'C', 12)]))
            {
                string[] names = ["delta", "alpha", "echo", "bravo", "charlie"];
                for (int id = 1; id <= 5; id++) writer.AppendRecord(id, names[id - 1]);
                writer.CreateTag(new CdxTagDefinition("tid", "id"));
            }

            _session = new VfpSession();
            _session.OpenDirectory(_dir.Path);
            _interp = new VfpInterpreter(_session);
        }

        public void Run(string source) => _interp.Execute(source);
        public decimal Num(string expression) => _interp.EvalExpression(expression).AsNumber;
        public string Str(string expression) => _interp.EvalExpression(expression).AsString;
        public bool Bool(string expression) => _interp.EvalExpression(expression).AsLogical;

        public void Dispose()
        {
            _interp.Dispose();
            _session.Dispose();
            _dir.Dispose();
        }
    }
}
