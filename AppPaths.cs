namespace Spectraxia;

internal static class AppPaths
{
    private static readonly string Root = AppContext.BaseDirectory;
    internal static readonly bool Portable = File.Exists(Path.Combine(Root, "portable.flag"));
    internal static string Settings => Path.Combine(Portable ? Path.Combine(Root, "Data") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spectraxia"), "frequency.txt");

    internal static string Recordings(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved))
        {
            if (Path.IsPathFullyQualified(saved)) return saved;
            if (Portable && !Path.IsPathRooted(saved))
            {
                var full = Path.GetFullPath(saved, Root);
                if (InsidePackage(Path.GetRelativePath(Root, full))) return full;
            }
        }
        return Portable ? Path.Combine(Root, "Recordings") :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Spectraxia");
    }

    internal static string SaveRecordingFolder(string full)
    {
        if (!Portable) return full;
        var relative = Path.GetRelativePath(Root, full);
        return InsidePackage(relative) ? relative : full;
    }

    private static bool InsidePackage(string relative) => !Path.IsPathRooted(relative) && relative != ".." &&
        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
