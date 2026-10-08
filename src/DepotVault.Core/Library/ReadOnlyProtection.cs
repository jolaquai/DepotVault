using DepotVault.Core.Linking;

namespace DepotVault.Core.Library;

public sealed class ReadOnlyProtection(LibraryIndex library, AppRepository apps, ILinkStrategy strategy, Func<bool> enabled)
{
    public bool Enabled => enabled();

    public int Apply(VersionRecord version)
    {
        if (!enabled())
            return 0;
        var app = apps.Get(version.AppId);
        var dir = library.GetVersionDir(version);
        var count = 0;
        foreach (var f in VersionStateStore.Load(library.Paths.VersionStateFile(version.Id)).Files)
        {
            var full = Path.Combine(dir, f.RelPath);
            if (!File.Exists(full) || app.GetDecision(f.RelPath) == MutableDecision.Share)
                continue;
            if (f.Link != LinkKind.Hardlink && strategy.GetFileIdentity(full).LinkCount <= 1)
                continue;
            FileUtil.SetReadOnly(full, true);
            count++;
        }
        return count;
    }

    public int ApplyApp(uint appId)
    {
        var count = 0;
        foreach (var v in library.VersionsFor(appId))
            count += Apply(v);
        return count;
    }

    public static int Remove(string versionDir)
    {
        if (!Directory.Exists(versionDir))
            return 0;
        var count = 0;
        foreach (var full in Directory.EnumerateFiles(versionDir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (!FileUtil.IsReadOnly(full))
                continue;
            FileUtil.SetReadOnly(full, false);
            count++;
        }
        return count;
    }

    public int Remove(VersionRecord version) => Remove(library.GetVersionDir(version));
}
