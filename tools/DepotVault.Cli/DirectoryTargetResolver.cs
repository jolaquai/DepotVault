using DepotVault.Core.Download;
using DepotVault.Core.Library;
using SteamKit2;

namespace DepotVault.Cli;

internal sealed class DirectoryTargetResolver(string dir) : IDownloadTargetResolver
{
    public DownloadTarget Resolve(DownloadJob job) => new()
    {
        VersionDir = dir,
        ManifestPath = Path.Combine(dir + ".meta", $"{job.DepotId}.manifest.bin"),
    };

    public void OnCompleted(DownloadJob job, DownloadTarget target, DepotManifest manifest, IReadOnlyList<FileSnapshot> files) =>
        VersionStateStore.MergeDepot(Path.Combine(dir + ".meta", "state.json"), job.DepotId, files);
}
