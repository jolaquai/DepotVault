using DepotVault.Core.Download;
using DepotVault.Core.Library;

namespace DepotVault.Tests.Download;

public class ChunkPipelineTests
{
    [Fact]
    public async Task FetchesAllFilesAndVerifies()
    {
        using var dir = new TempDir();
        var depot = new FakeDepot();
        var big = FakeDepot.Bytes("0123456789abcdef", 100);
        var manifest = depot.Build(1, 10, 64, ("bin", null), (@"bin\game.exe", big), ("readme.txt", FakeDepot.Bytes("hello")), ("empty.dat", []));
        var plans = FilePlanner.Plan(manifest, new PlannerOptions());
        var counters = new JobCounters();
        var target = dir.Combine("v1");

        var snaps = await new ChunkPipeline().RunAsync(1, plans, target, counters, new PipelineOptions { Source = depot, MaxConcurrentChunks = 4 }, TestContext.Current.CancellationToken);

        Assert.Equal(big, File.ReadAllBytes(Path.Combine(target, "bin", "game.exe")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(target, "empty.dat")).Length);
        Assert.Equal(big.Length + 5, counters.TotalBytes);
        Assert.Equal(big.Length + 5, counters.WrittenBytes);
        Assert.Equal(26, depot.Fetches);
        Assert.Equal(3, snaps.Count);
        Assert.All(snaps, s => Assert.Equal(LinkKind.None, s.Link));
    }

    [Fact]
    public async Task DiffReusesUnchangedChunksFromPreviousVersion()
    {
        using var dir = new TempDir();
        var depot = new FakeDepot();
        var ct = TestContext.Current.CancellationToken;
        var v1 = string.Concat(Enumerable.Range(0, 20).Select(i => $"block{i:D3}-------------------------------------------------------")).Select(c => (byte)c).ToArray();
        var v2 = (byte[])v1.Clone();
        v2[64 * 5 + 3] = (byte)'X';
        v2 = [.. v2, .. FakeDepot.Bytes("tail")];

        var m1 = depot.Build(1, 10, 64, ("data.pak", v1));
        await new ChunkPipeline().RunAsync(1, FilePlanner.Plan(m1, new PlannerOptions()), dir.Combine("v1"), new JobCounters(), new PipelineOptions { Source = depot }, ct);
        var fetchesAfterV1 = depot.Fetches;

        var m2 = depot.Build(1, 11, 64, ("data.pak", v2));
        var plans = FilePlanner.Plan(m2, new PlannerOptions { Previous = new PreviousVersion { VersionId = "v1", Directory = dir.Combine("v1"), Manifest = m1 } });
        Assert.Equal(FileAction.Diff, plans[0].Action);
        var counters = new JobCounters();
        await new ChunkPipeline().RunAsync(1, plans, dir.Combine("v2"), counters, new PipelineOptions { Source = depot }, ct);

        Assert.Equal(v2, File.ReadAllBytes(dir.Combine("v2", "data.pak")));
        Assert.Equal(2, depot.Fetches - fetchesAfterV1);
        Assert.True(counters.ReusedBytes > 0);
    }

    [Fact]
    public async Task CorruptDataFailsVerification()
    {
        using var dir = new TempDir();
        var depot = new FakeDepot { Corrupt = c => c.Offset == 0 };
        var manifest = depot.Build(1, 10, 64, ("a.bin", FakeDepot.Bytes("abcdefgh", 20)));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new ChunkPipeline().RunAsync(1, FilePlanner.Plan(manifest, new PlannerOptions()), dir.Combine("v"), new JobCounters(), new PipelineOptions { Source = depot }, TestContext.Current.CancellationToken));
        Assert.Contains("a.bin", ex.Message);
        Assert.False(File.Exists(dir.Combine("v", "a.bin")));
    }

    [Fact]
    public async Task SourceFailurePropagates()
    {
        using var dir = new TempDir();
        var depot = new FakeDepot { FailAt = n => n == 3 };
        var manifest = depot.Build(1, 10, 16, ("a.bin", FakeDepot.Bytes("abcdefgh", 20)));
        await Assert.ThrowsAsync<IOException>(() => new ChunkPipeline().RunAsync(1, FilePlanner.Plan(manifest, new PlannerOptions()), dir.Combine("v"), new JobCounters(), new PipelineOptions { Source = depot, MaxConcurrentChunks = 2 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RejectsPathTraversal()
    {
        var depot = new FakeDepot();
        var manifest = depot.Build(1, 10, 64, (@"..\evil.dll", FakeDepot.Bytes("x")));
        Assert.Throws<InvalidDataException>(() => FilePlanner.Plan(manifest, new PlannerOptions()));
    }
}
