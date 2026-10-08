using DepotVault.Core.Download;
using SteamKit2;

namespace DepotVault.Core.Library;

public sealed class SharePolicy
{
    public Func<bool> Enabled { get; init; } = static () => true;
    public Func<uint, Func<string, bool>> NeverShare { get; init; }
    public Func<uint, Func<string, bool>> Isolate { get; init; }
}

public sealed class LibraryTargetResolver(LibraryIndex library, ContentIndex index = null, Deduper deduper = null, SharePolicy policy = null) : IDownloadTargetResolver
{
    public DownloadTarget Resolve(DownloadJob job)
    {
        var version = library.Find(job.TargetVersionId) ?? throw new InvalidOperationException($"Version {job.TargetVersionId} not found.");
        var share = index is not null && deduper is not null && (policy?.Enabled() ?? true);
        return new DownloadTarget
        {
            VersionDir = library.GetVersionDir(version),
            ManifestPath = library.Paths.ManifestFile(version.Id, job.DepotId),
            Previous = FindPrevious(version, job.DepotId),
            Index = share ? index : null,
            Sharer = share ? deduper.ForVersion(policy?.Isolate?.Invoke(job.AppId)) : null,
            NeverShare = share ? policy?.NeverShare?.Invoke(job.AppId) : null,
        };
    }

    public PreviousVersion FindPrevious(VersionRecord target, uint depotId)
    {
        foreach (var v in library.VersionsFor(target.AppId))
        {
            if (v.Id == target.Id || !string.Equals(v.Root, target.Root, StringComparison.OrdinalIgnoreCase))
                continue;
            var m = v.Manifests.Find(x => x.DepotId == depotId && x.Complete);
            if (m is null)
                continue;
            var path = library.Paths.ManifestFile(v.Id, depotId);
            if (!File.Exists(path))
                continue;
            var manifest = DepotManifest.LoadFromFile(path);
            if (manifest is null)
                continue;
            return new PreviousVersion { VersionId = v.Id, Directory = library.GetVersionDir(v), Manifest = manifest };
        }
        return null;
    }

    public void OnCompleted(DownloadJob job, DownloadTarget target, DepotManifest manifest, IReadOnlyList<FileSnapshot> files)
    {
        VersionStateStore.MergeDepot(library.Paths.VersionStateFile(job.TargetVersionId), job.DepotId, files);
        library.MarkDepotComplete(job.TargetVersionId, job.DepotId, job.ManifestId);
        index?.AddManifest(job.TargetVersionId, manifest);
    }
}
