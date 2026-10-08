using DepotVault.Core.Library;
using SteamKit2;

namespace DepotVault.Core.SteamInstall;

public enum InstallMode
{
    None,
    Junction,
    PerFile,
}

public sealed class PlacedFile
{
    public string RelPath { get; set; }
    public LinkKind Link { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

public sealed class InstallState
{
    public InstallMode Mode { get; set; }
    public string VersionId { get; set; }
    public List<PlacedFile> Placed { get; set; } = [];
}

public sealed record SwitchFailure(string Path, string Reason);

public sealed class SwitchReport
{
    public bool Success { get; set; }
    public bool Canceled { get; set; }
    public InstallMode Mode { get; set; }
    public string AdoptedVersionId { get; set; }
    public List<SwitchFailure> Failures { get; } = [];
    public List<string> Staged { get; } = [];
    public Dictionary<LinkKind, int> Links { get; } = [];
    public string StagingDir { get; set; }

    internal SwitchReport Fail(string path, string reason)
    {
        Failures.Add(new SwitchFailure(path, reason));
        Success = false;
        return this;
    }
}

public readonly record struct CopyConsent(bool Allow, bool Remember);

public interface ISwitchPrompts
{
    Task<bool> WaitForSteamExitAsync(CancellationToken ct);
    Task<CopyConsent> AskCopyAsync(int fileCount, long bytes, CancellationToken ct);
}

public interface IInstalledManifestSource
{
    Task<DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct);
}

public sealed class SwitchRequest
{
    public required uint AppId { get; init; }
    public required string TargetVersionId { get; init; }
    public required InstalledApp Install { get; init; }
    public uint AcfBuildId { get; init; }
}
