using DepotVault.Core.Library;
using DepotVault.Core.Steam;
using SteamKit2;

namespace DepotVault.Core.Download;

public sealed class DownloadTarget
{
    public required string VersionDir { get; init; }
    public required string ManifestPath { get; init; }
    public PreviousVersion Previous { get; init; }
    public IContentIndex Index { get; init; }
    public IFileSharer Sharer { get; init; }
    public Func<string, bool> NeverShare { get; init; }
}

public interface IDownloadTargetResolver
{
    DownloadTarget Resolve(DownloadJob job);
    void OnCompleted(DownloadJob job, DownloadTarget target, DepotManifest manifest, IReadOnlyList<FileSnapshot> files);
}

public interface IManifestProvider
{
    Task<DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, string savePath, CancellationToken ct);
}

internal sealed class ManifestServiceProvider(ManifestService service) : IManifestProvider
{
    public Task<DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, string savePath, CancellationToken ct) => service.GetAsync(appId, depotId, manifestId, savePath, ct);
}

public sealed class DepotJobRunner(IManifestProvider manifests, Func<uint, IChunkSource> sourceFactory, IDownloadTargetResolver resolver, Func<int> maxConcurrentChunks, BandwidthLimiter limiter = null) : IJobRunner
{
    public DepotJobRunner(ManifestService manifests, Func<uint, IChunkSource> sourceFactory, IDownloadTargetResolver resolver, Func<int> maxConcurrentChunks, BandwidthLimiter limiter = null)
        : this(new ManifestServiceProvider(manifests), sourceFactory, resolver, maxConcurrentChunks, limiter) { }

    public async Task RunAsync(DownloadJob job, CancellationToken ct)
    {
        var target = resolver.Resolve(job);
        var manifest = await manifests.GetAsync(job.AppId, job.DepotId, job.ManifestId, target.ManifestPath, ct).ConfigureAwait(false);
        var plans = FilePlanner.Plan(manifest, new PlannerOptions
        {
            Index = target.Index ?? NullContentIndex.Instance,
            Previous = target.Previous,
            TargetVersionId = job.TargetVersionId,
            NeverShare = target.NeverShare,
        });
        var tracker = new ResumeTracker(job.Resume, target.VersionDir, job.Dirty);
        job.Tracker = tracker;
        List<FileSnapshot> files;
        try
        {
            files = await new ChunkPipeline().RunAsync(job.DepotId, plans, target.VersionDir, job.Counters, new PipelineOptions
            {
                Source = sourceFactory(job.AppId),
                MaxConcurrentChunks = maxConcurrentChunks(),
                Sharer = target.Sharer,
                Limiter = limiter,
                Tracker = tracker,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            job.Resume = tracker.Export();
            job.Tracker = null;
            tracker.Dispose();
            throw;
        }
        job.Tracker = null;
        job.Resume = null;
        tracker.Dispose();
        resolver.OnCompleted(job, target, manifest, files);
    }
}
