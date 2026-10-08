using DepotVault.Core.Persistence;

namespace DepotVault.Core.Library;

public sealed class DepotInfo
{
    public uint DepotId { get; set; }
    public string Name { get; set; }
    public string OsList { get; set; }
    public string OsArch { get; set; }
    public string Language { get; set; }
    public bool LowViolence { get; set; }
    public uint DepotFromApp { get; set; }
    public bool SharedInstall { get; set; }
    public uint DlcAppId { get; set; }
    public ulong MaxSize { get; set; }
    public ulong CurrentManifestId { get; set; }
    public ulong CurrentManifestSize { get; set; }

    public bool IsDownloadable => DepotFromApp == 0 && !SharedInstall && CurrentManifestId != 0;
}

public sealed class ManifestHistoryEntry
{
    public uint DepotId { get; set; }
    public ulong ManifestId { get; set; }
    public DateTime DateUtc { get; set; }
    public string Label { get; set; }
    public bool Unavailable { get; set; }
}

public enum MutableDecision
{
    Unreviewed,
    Share,
    Isolate,
}

public sealed class MutableRule
{
    public string Pattern { get; set; }
    public MutableDecision Decision { get; set; }
}

public sealed class AppRecord : ISchemaVersioned
{
    public int SchemaVersion { get; set; }
    public uint AppId { get; set; }
    public string Name { get; set; }
    public string InstallDir { get; set; }
    public uint PublicBuildId { get; set; }
    public DateTime MetadataFetchedUtc { get; set; }
    public List<DepotInfo> Depots { get; set; } = [];
    public List<uint> SelectedDepots { get; set; }
    public List<ManifestHistoryEntry> History { get; set; } = [];
    public string Notes { get; set; }
    public List<MutableRule> MutableRules { get; set; } = [];
    public bool MutableReviewed { get; set; }
    public List<string> Exclusions { get; set; } = [];
    public List<string> ReviewCandidates { get; set; } = [];
    public string ForceStrategy { get; set; }
    public SteamInstall.InstallState Install { get; set; }

    public MutableDecision GetDecision(string relPath)
    {
        if (MutableRules is null)
            return MutableDecision.Unreviewed;
        var norm = relPath.Replace('\\', '/');
        foreach (var r in MutableRules)
        {
            if (string.Equals(r.Pattern.Replace('\\', '/'), norm, StringComparison.OrdinalIgnoreCase))
                return r.Decision;
        }
        foreach (var r in MutableRules)
        {
            if (r.Pattern.AsSpan().IndexOfAny('*', '?') >= 0 && PathGlob.IsMatch(r.Pattern, relPath))
                return r.Decision;
        }
        return MutableDecision.Unreviewed;
    }

    public void SetDecision(string pattern, MutableDecision decision)
    {
        MutableRules ??= [];
        MutableRules.RemoveAll(r => string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
        if (decision != MutableDecision.Unreviewed)
            MutableRules.Add(new MutableRule { Pattern = pattern, Decision = decision });
        ReviewCandidates?.RemoveAll(c => string.Equals(c, pattern, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsExcluded(string relPath, IReadOnlyList<string> globalGlobs)
    {
        if (GetDecision(relPath) == MutableDecision.Share)
            return false;
        if (Exclusions is not null)
        {
            foreach (var e in Exclusions)
            {
                if (string.Equals(e.Replace('\\', '/'), relPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) || PathGlob.IsMatch(e, relPath))
                    return true;
            }
        }
        if (globalGlobs is not null)
        {
            foreach (var g in globalGlobs)
            {
                if (PathGlob.IsMatch(g, relPath))
                    return true;
            }
        }
        return false;
    }
}
