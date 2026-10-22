namespace CrossVault.FoxDbf;

/// <summary>
/// Locates a table's sidecar files (<c>.cdx</c>, <c>.fpt</c>, <c>.dbt</c>, <c>.dct</c>, …) whatever the case of
/// their names. VFP writes <c>NAME.CDX</c> / <c>NAME.FPT</c>; on a case-sensitive file system (Linux, a
/// container) a plain <c>Path.ChangeExtension(dbf, ".cdx")</c> misses such a file, so the index would be neither
/// read nor maintained and a transaction or DROP TABLE would overlook it.
/// </summary>
internal static class SidecarFile
{
    /// <summary>
    /// Like <see cref="Path.ChangeExtension(string, string)"/>, but when a file with that name exists in a
    /// different case it returns that file's actual path. When none exists the plain result is returned, so a
    /// sidecar created from it gets the default (lower-case) name.
    /// </summary>
    public static string ChangeExtension(string path, string extension)
    {
        string candidate = Path.ChangeExtension(path, extension);
        return FindCaseInsensitive(candidate) ?? candidate;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> ignoring the case of its file name; <see langword="null"/> when no such
    /// file exists. The directory is scanned only on a file system that distinguishes case — on Windows and
    /// macOS (case-insensitive by default) <see cref="File.Exists(string)"/> already answers.
    /// </summary>
    public static string? FindCaseInsensitive(string path)
    {
        if (File.Exists(path))
            return path;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return null;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        if (!Directory.Exists(dir))
            return null;

        var name = Path.GetFileName(path);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }
}
