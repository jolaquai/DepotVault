using DepotVault.Core.Library;
using SteamKit2;

namespace DepotVault.Core.Steam;

public sealed record AppMetadata(uint AppId, string Name, string InstallDir, uint PublicBuildId, List<DepotInfo> Depots);

public readonly record struct DepotFilter(string Os, string Arch, string Language, bool LowViolence = false)
{
    public static DepotFilter ForCurrentPlatform(string language = "english") => new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        Environment.Is64BitOperatingSystem ? "64" : "32",
        language);
}

public static class DepotMetadataParser
{
    public static AppMetadata Parse(uint appId, KeyValue root)
    {
        var name = root["common"]["name"].AsString();
        var installDir = root["config"]["installdir"].AsString();
        var buildId = root["depots"]["branches"]["public"]["buildid"].AsUnsignedInteger(0);
        var depots = new List<DepotInfo>();
        foreach (var kv in root["depots"].Children)
        {
            if (!uint.TryParse(kv.Name, out var depotId))
                continue;
            var config = kv["config"];
            var (gid, size) = ReadPublicManifest(kv["manifests"]["public"]);
            depots.Add(new DepotInfo
            {
                DepotId = depotId,
                Name = kv["name"].AsString(),
                OsList = config["oslist"].AsString(),
                OsArch = config["osarch"].AsString(),
                Language = config["language"].AsString(),
                LowViolence = config["lowviolence"].AsBoolean(false),
                DepotFromApp = kv["depotfromapp"].AsUnsignedInteger(0),
                SharedInstall = kv["sharedinstall"].AsBoolean(false),
                DlcAppId = kv["dlcappid"].AsUnsignedInteger(0),
                MaxSize = kv["maxsize"].AsUnsignedLong(0),
                CurrentManifestId = gid,
                CurrentManifestSize = size,
            });
        }
        return new AppMetadata(appId, name, installDir, buildId, depots);
    }

    private static (ulong Gid, ulong Size) ReadPublicManifest(KeyValue node)
    {
        if (node == KeyValue.Invalid)
            return default;
        if (node.Children.Count == 0)
            return (node.AsUnsignedLong(0), 0);
        return (node["gid"].AsUnsignedLong(0), node["size"].AsUnsignedLong(0));
    }

    public static List<uint> SelectDefault(IEnumerable<DepotInfo> depots, DepotFilter filter, Func<uint, bool> ownsDlc = null)
    {
        var result = new List<uint>();
        foreach (var d in depots)
        {
            if (!d.IsDownloadable)
                continue;
            if (!string.IsNullOrEmpty(d.OsList) && !ListContains(d.OsList, filter.Os))
                continue;
            if (!string.IsNullOrEmpty(d.OsArch) && !string.Equals(d.OsArch, filter.Arch, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(d.Language) && !string.Equals(d.Language, filter.Language, StringComparison.OrdinalIgnoreCase))
                continue;
            if (d.LowViolence && !filter.LowViolence)
                continue;
            if (d.DlcAppId != 0 && (ownsDlc is null || !ownsDlc(d.DlcAppId)))
                continue;
            result.Add(d.DepotId);
        }
        return result;
    }

    private static bool ListContains(string list, string value)
    {
        foreach (var range in list.AsSpan().Split(','))
        {
            if (list.AsSpan()[range].Trim().Equals(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
