using DepotVault.Core.Linking;

namespace DepotVault.Core.Library;

public sealed class LibraryRoots(Func<IReadOnlyList<string>> configured, ILinkStrategy strategy)
{
    public const string FolderName = "DepotVault";

    public IReadOnlyList<string> All => configured();

    public static IEnumerable<string> Suggest(IEnumerable<string> steamLibraries, IEnumerable<string> existing)
    {
        var have = new HashSet<string>(existing.Select(Normalize), PathComparer);
        foreach (var lib in steamLibraries)
        {
            var candidate = Normalize(Path.Combine(lib, FolderName));
            if (have.Add(candidate))
                yield return candidate;
        }
    }

    public string FindForPath(string path)
    {
        var probe = ExistingAncestor(path);
        if (probe is null)
            return null;
        var volume = strategy.GetVolumeId(probe);
        foreach (var root in All)
        {
            var r = ExistingAncestor(root);
            if (r is not null && strategy.GetVolumeId(r) == volume)
                return Normalize(root);
        }
        return null;
    }

    private static string ExistingAncestor(string path)
    {
        var p = Path.GetFullPath(path);
        while (p is not null && !Directory.Exists(p) && !File.Exists(p))
            p = Path.GetDirectoryName(p);
        return p;
    }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
