using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

// Full-branch review of work/twidth (live VFP9 probe): a dynamically typed projection whose first value is a
// numeric overflow stays a numeric column (VFP9 and 0.14.0), not a character column holding "Infinity".
public sealed class TransformWidthDynamicTypeTests
{
    [Fact]
    public void EvaluateOverflow_IntoCursor_StaysNumeric()
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthdyntype");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute("SET DECIMALS TO 2\nCREATE TABLE t FREE (k I)\nINSERT INTO t VALUES (1)\n"
            + "SELECT EVALUATE('1/0') AS v FROM t INTO CURSOR zz\n"
            + "kind=TYPE('zz.v')\nGO TOP IN zz\nrendered=TRANSFORM(zz.v)\n"
            + "SELECT EVALUATE('1/0') AS v FROM t INTO ARRAY aa\nakind=TYPE('aa[1]')\narendered=TRANSFORM(aa[1])");
        Assert.Equal("N", vfp.Memory.Get("kind").AsString);
        Assert.Matches("^\\*+$", vfp.Memory.Get("rendered").AsString);
        Assert.Equal("N", vfp.Memory.Get("akind").AsString);
        Assert.Matches("^\\*+$", vfp.Memory.Get("arendered").AsString);
    }
}
