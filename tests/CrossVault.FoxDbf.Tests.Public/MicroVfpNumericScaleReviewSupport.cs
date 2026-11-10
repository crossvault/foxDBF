using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
namespace CrossVault.FoxDbf.Tests;
internal static class MicroVfpNumericScaleReviewSupport
{
    internal static string Run(string script)
    {
        using var dir = new MicroVfpTestSupport.TempDir("numeric_review");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        try
        {
            vfp.Execute("SET DECIMALS TO 4\n" + script);
            return vfp.Memory.Get("result").AsString;
        }
        catch (Exception e) when (e is MicroVfpRuntimeException or ExpressionException or FoxDbfSqlException or Write.DbfWriteException)
        {
            return "ERR:" + VfpInterpreter.ErrorNumberOf(e);
        }
    }
}
