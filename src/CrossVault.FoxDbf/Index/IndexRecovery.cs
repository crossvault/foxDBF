using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Index;

internal static class IndexRecovery
{
    internal static void RequireHealthy(string cdxPath)
    {
        if (File.Exists(cdxPath + ".needs-reindex"))
            throw new DbfWriteException($"Index maintenance could not be rolled back for '{cdxPath}'. Open a DbfWriter and Reindex before reading the table.");
    }
}
