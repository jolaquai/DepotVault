using DepotVault.Core.Download;
using SteamKit2;

namespace DepotVault.Core.Library;

[Flags]
public enum MutableReason
{
    None = 0,
    SteamUserConfig = 1,
    ConfigExtension = 2,
    MutableDirectory = 4,
    ModifiedSinceLink = 8,
    ReportedBySelfHeal = 16,
}

public sealed record MutableCandidate(string RelPath, long Size, MutableReason Reasons, MutableDecision Decision);

public static class MutableFileScanner
{
    private const long MaxConfigSize = 16L << 20;

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".cfg", ".conf", ".config", ".json", ".xml", ".yaml", ".yml", ".toml", ".sav", ".save", ".dat",
        ".log", ".db", ".sqlite", ".settings", ".prefs", ".profile", ".reg", ".bak", ".cache", ".txt",
    };

    private static readonly HashSet<string> WeakExtensions = new(StringComparer.OrdinalIgnoreCase) { ".dat", ".txt", ".json", ".xml" };

    private static readonly HashSet<string> MutableDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", "caches", "save", "saves", "savegame", "savegames", "savedata", "saved", "log", "logs",
        "config", "configs", "cfg", "settings", "profiles", "profile", "userdata", "user", "prefs", "shadercache",
    };

    public static List<MutableCandidate> Scan(IEnumerable<DepotManifest.FileData> files, AppRecord app = null, Func<string, bool> modifiedSinceLink = null, IEnumerable<string> reported = null)
    {
        var result = new Dictionary<string, MutableCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if ((f.Flags & (EDepotFileFlag.Directory | EDepotFileFlag.Symlink)) != 0)
                continue;
            var rel = FilePlanner.NormalizeRelPath(f.FileName);
            var reasons = Classify(rel, (long)f.TotalSize, f.Flags);
            if (modifiedSinceLink?.Invoke(rel) == true)
                reasons |= MutableReason.ModifiedSinceLink;
            if (reasons != MutableReason.None)
                result[rel] = new MutableCandidate(rel, (long)f.TotalSize, reasons, app?.GetDecision(rel) ?? MutableDecision.Unreviewed);
        }
        foreach (var rel in reported ?? app?.ReviewCandidates ?? [])
        {
            var key = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            result[key] = result.TryGetValue(key, out var c)
                ? c with { Reasons = c.Reasons | MutableReason.ReportedBySelfHeal }
                : new MutableCandidate(key, 0, MutableReason.ReportedBySelfHeal, app?.GetDecision(key) ?? MutableDecision.Unreviewed);
        }
        return result.Values.OrderBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<MutableCandidate> ScanApp(LibraryIndex library, AppRecord app, Func<string, bool> modifiedSinceLink = null)
    {
        var files = new Dictionary<string, DepotManifest.FileData>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in library.VersionsFor(app.AppId))
        {
            foreach (var m in v.Manifests)
            {
                var path = library.Paths.ManifestFile(v.Id, m.DepotId);
                if (!File.Exists(path) || DepotManifest.LoadFromFile(path)?.Files is not { } list)
                    continue;
                foreach (var f in list)
                    files.TryAdd(FilePlanner.NormalizeRelPath(f.FileName), f);
            }
        }
        return Scan(files.Values, app, modifiedSinceLink);
    }

    public static MutableReason Classify(string relPath, long size, EDepotFileFlag flags)
    {
        var reasons = MutableReason.None;
        if ((flags & (EDepotFileFlag.UserConfig | EDepotFileFlag.VersionedUserConfig)) != 0)
            reasons |= MutableReason.SteamUserConfig;
        var inMutableDir = false;
        var path = relPath.AsSpan();
        var dirEnd = path.LastIndexOfAny('\\', '/');
        if (dirEnd > 0)
        {
            foreach (var seg in path[..dirEnd].SplitAny('\\', '/'))
            {
                if (MutableDirs.Contains(path[seg].ToString()))
                {
                    inMutableDir = true;
                    break;
                }
            }
        }
        if (inMutableDir)
            reasons |= MutableReason.MutableDirectory;
        var ext = Path.GetExtension(relPath);
        if (size <= MaxConfigSize && ConfigExtensions.Contains(ext) && (!WeakExtensions.Contains(ext) || inMutableDir || size <= 64 * 1024))
            reasons |= MutableReason.ConfigExtension;
        return reasons;
    }

    public static SharePolicy BuildPolicy(Func<uint, AppRecord> apps, Func<IReadOnlyList<string>> globalGlobs, Func<bool> dedupeEnabled) => new()
    {
        Enabled = dedupeEnabled,
        NeverShare = appId =>
        {
            var app = apps(appId);
            var globs = globalGlobs();
            return rel => app.IsExcluded(rel, globs);
        },
        Isolate = appId =>
        {
            var app = apps(appId);
            return rel => app.GetDecision(rel) == MutableDecision.Isolate;
        },
    };
}
