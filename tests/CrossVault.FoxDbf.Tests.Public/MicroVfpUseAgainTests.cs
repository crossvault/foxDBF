using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpUseAgainTests
{
    [Fact]
    public void UseAgainInZero_DefaultAlias_PreservesOriginalAreaAndCreatesFreshFallbackArea()
    {
        using var f = new Fixture();
        f.Run(
            "USE f28\n" +
            "SET ORDER TO idtag\n" +
            "SET FILTER TO id >= 2\n" +
            "SET KEY TO RANGE 2, 3\n" +
            "GO 2\n" +
            "USE f28 AGAIN IN 0");

        Assert.Equal(1m, f.Num("SELECT()"));
        Assert.Equal("F28", f.Str("ALIAS()"));
        Assert.Equal(2m, f.Num("RECNO()"));
        Assert.Equal("IDTAG", f.Str("ORDER()").ToUpperInvariant());
        Assert.Equal("ID=>2", f.Str("FILTER()")); // VFP9 decompiles >= as => (M5 operator oracle).
        f.Run("GO BOTTOM");
        Assert.Equal(3m, f.Num("id"));

        f.Run("SELECT B");
        Assert.Equal(2m, f.Num("SELECT()"));
        Assert.Equal("B", f.Str("ALIAS()"));
        Assert.Equal(1m, f.Num("RECNO()"));
        Assert.Equal("", f.Str("ORDER()"));
        Assert.Equal("", f.Str("FILTER()"));
        f.Run("GO TOP");
        Assert.Equal(1m, f.Num("id"));
    }

    [Fact]
    public void UseAgainInZero_IndexAndOrderOptions_ApplyOnlyToTargetArea()
    {
        using var f = new Fixture();
        f.Run(
            "USE f28\n" +
            "INDEX ON name TAG ext OF extra.cdx\n" +
            "SET INDEX TO\n" +
            "SET ORDER TO idtag\n" +
            "SET FILTER TO id >= 2\n" +
            "GO 2\n" +
            "USE f28 AGAIN IN 0 INDEX extra.cdx ORDER ext");

        Assert.Equal("F28", f.Str("ALIAS()"));
        Assert.Equal(2m, f.Num("RECNO()"));
        Assert.Equal("IDTAG", f.Str("ORDER()").ToUpperInvariant());
        Assert.Equal("ID=>2", f.Str("FILTER()"));
        Assert.Equal("", f.Str("CDX(2)"));

        f.Run("SELECT B");
        Assert.Equal("EXT", f.Str("ORDER()").ToUpperInvariant());
        Assert.EndsWith("extra.cdx", f.Str("CDX(2)"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", f.Str("FILTER()"));
    }

    [Fact]
    public void UseAgainInZero_ExplicitAlias_RemainsUnchanged()
    {
        using var f = new Fixture();
        f.Run("USE f28\nGO 2\nUSE f28 AGAIN IN 0 ALIAS copy");

        Assert.Equal(1m, f.Num("SELECT()"));
        Assert.Equal("F28", f.Str("ALIAS()"));
        Assert.Equal(2m, f.Num("RECNO()"));
        f.Run("SELECT copy");
        Assert.Equal(2m, f.Num("SELECT()"));
        Assert.Equal("COPY", f.Str("ALIAS()"));
        Assert.Equal(1m, f.Num("RECNO()"));
    }

    [Fact]
    public void SessionUse_DefaultAliasCollision_UsesAreaFallbackThroughW11()
    {
        using var dir = BuildTable();
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        session.Use("f28");
        for (int area = 2; area <= 11; area++) session.Use("f28", inArea: 0, again: true);

        session.SelectArea(2);
        Assert.Equal("B", session.CurrentAlias);
        session.SelectArea(10);
        Assert.Equal("J", session.CurrentAlias);
        session.SelectArea(11);
        Assert.Equal("W11", session.CurrentAlias);
    }

    private static MicroVfpTestSupport.TempDir BuildTable()
    {
        var dir = new MicroVfpTestSupport.TempDir("use_again");
        using var writer = DbfWriter.Create(dir.File("f28.dbf"),
            [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("NAME", 'C', 12)]);
        writer.AppendRecord(1, "charlie");
        writer.AppendRecord(2, "alpha");
        writer.AppendRecord(3, "bravo");
        writer.CreateTag(new CdxTagDefinition("idtag", "id"));
        return dir;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = BuildTable();
        private readonly VfpSession _session;
        private readonly VfpInterpreter _interpreter;

        public Fixture()
        {
            _session = new VfpSession();
            _session.OpenDirectory(_dir.Path);
            _interpreter = new VfpInterpreter(_session);
        }

        public void Run(string source) => _interpreter.Execute(source);
        public decimal Num(string expression) => _interpreter.EvalExpression(expression).AsNumber;
        public string Str(string expression) => _interpreter.EvalExpression(expression).AsString;

        public void Dispose()
        {
            _session.Dispose();
            _dir.Dispose();
        }
    }
}
