using System.Text.Json;
using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Persistence;
using SteamKit2;

namespace DepotVault.Tests.Download;

public class ResumeTests
{
    private sealed class FixedManifest(DepotManifest m) : IManifestProvider
    {
        public Task<DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, string savePath, CancellationToken ct) => Task.FromResult(m);
    }

    private sealed class DirResolver(string dir) : IDownloadTargetResolver
    {
        public int Completed;
        public DownloadTarget Resolve(DownloadJob job) => new() { VersionDir = dir, ManifestPath = null };
        public void OnCompleted(DownloadJob job, DownloadTarget target, DepotManifest manifest, IReadOnlyList<FileSnapshot> files) => Completed++;
    }

    private static (FakeDepot Depot, DepotManifest Manifest, byte[] A, byte[] B) Build()
    {
        var depot = new FakeDepot();
        var a = string.Concat(Enumerable.Range(0, 40).Select(i => $"A{i:D4}-----------------")).Select(c => (byte)c).ToArray();
        var b = string.Concat(Enumerable.Range(0, 40).Select(i => $"B{i:D4}+++++++++++++++++")).Select(c => (byte)c).ToArray();
        return (depot, depot.Build(1, 10, 32, ("a.bin", a), (@"sub\b.bin", b)), a, b);
    }

    private static DownloadJob RoundTrip(DownloadJob job)
    {
        var json = JsonSerializer.Serialize(new QueueDocument { Jobs = [job] }, JsonContext.Default.QueueDocument);
        return JsonSerializer.Deserialize(json, JsonContext.Default.QueueDocument).Jobs[0];
    }

    [Fact]
    public async Task CanceledJobResumesWithoutRefetchingCompletedChunks()
    {
        using var dir = new TempDir();
        var (depot, manifest, a, b) = Build();
        var total = manifest.Files.Sum(f => f.Chunks.Count);
        var resolver = new DirResolver(dir.Combine("v"));
        var runner = new DepotJobRunner(new FixedManifest(manifest), _ => depot, resolver, () => 2);
        var job = new DownloadJob { AppId = 1, DepotId = 1, ManifestId = 10, TargetVersionId = "v" };

        using (var cts = new CancellationTokenSource())
        {
            depot.OnFetch = n => { if (n == total / 2) cts.Cancel(); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(job, cts.Token));
        }
        Assert.NotNull(job.Resume);
        Assert.NotEmpty(job.Resume);

        var restored = RoundTrip(job);
        var before = depot.Fetches;
        depot.OnFetch = null;
        await runner.RunAsync(restored, TestContext.Current.CancellationToken);

        Assert.Equal(1, resolver.Completed);
        Assert.Equal(a, File.ReadAllBytes(dir.Combine("v", "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(dir.Combine("v", "sub", "b.bin")));
        Assert.True(depot.Fetches - before < total, $"refetched {depot.Fetches - before} of {total}");
        Assert.True(restored.Counters.ReusedBytes > 0);
        Assert.Null(restored.Resume);
    }

    [Fact]
    public async Task TamperedClaimedChunkIsRefetched()
    {
        using var dir = new TempDir();
        var (depot, manifest, a, _) = Build();
        var total = manifest.Files.Sum(f => f.Chunks.Count);
        var runner = new DepotJobRunner(new FixedManifest(manifest), _ => depot, new DirResolver(dir.Combine("v")), () => 1);
        var job = new DownloadJob { AppId = 1, DepotId = 1, ManifestId = 10, TargetVersionId = "v" };

        using (var cts = new CancellationTokenSource())
        {
            depot.OnFetch = n => { if (n == total - 2) cts.Cancel(); };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(job, cts.Token));
        }
        var path = dir.Combine("v", "a.bin");
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            fs.WriteByte((byte)'Z');

        depot.OnFetch = null;
        await runner.RunAsync(RoundTrip(job), TestContext.Current.CancellationToken);
        Assert.Equal(a, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task QueuePauseKeepsResumeStateAndResumeCompletes()
    {
        using var dir = new TempDir();
        var (depot, manifest, a, b) = Build();
        var gate = new TaskCompletionSource();
        depot.OnFetch = n => { if (n == 5) gate.TrySetResult(); };
        var slow = new SlowSource(depot);
        var runner = new DepotJobRunner(new FixedManifest(manifest), _ => slow, new DirResolver(dir.Combine("v")), () => 1);
        using var store = new AtomicJsonStore<QueueDocument>(dir.Combine("queue.json"), JsonContext.Default.QueueDocument, debounce: TimeSpan.FromMilliseconds(20));
        using var q = new DownloadQueue(store, runner, () => 1);
        q.Start();
        var job = q.Enqueue(1, 1, 10, "v");
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        q.Pause(job.Id);
        await q.WhenIdleAsync();
        Assert.Equal(JobState.Paused, job.State);
        Assert.NotEmpty(job.Resume);
        slow.Delay = TimeSpan.Zero;
        q.Resume(job.Id);
        for (var i = 0; i < 300 && job.State != JobState.Done; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(a, File.ReadAllBytes(dir.Combine("v", "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(dir.Combine("v", "sub", "b.bin")));
    }

    private sealed class SlowSource(IChunkSource inner) : IChunkSource
    {
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(20);

        public async Task<int> FetchAsync(uint depotId, DepotManifest.ChunkData chunk, byte[] destination, CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);
            return await inner.FetchAsync(depotId, chunk, destination, ct);
        }
    }
}
