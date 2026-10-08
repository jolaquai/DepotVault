using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;
using DepotVault.Tests.Download;
using SteamKit2;

namespace DepotVault.Tests.Library;

public class DedupeTests
{
    private static readonly ILinkStrategy Strategy = LinkStrategy.CreateForCurrentPlatform();

    private sealed class Manifests(Dictionary<ulong, DepotManifest> map) : IManifestProvider
    {
        public Task<DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, string savePath, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            map[manifestId].SaveToFile(savePath);
            return Task.FromResult(map[manifestId]);
        }
    }

    private sealed class Env : IDisposable
    {
        public TempDir Dir = new();
        public LibraryIndex Lib;
        public FakeDepot Depot = new();
        public ContentIndex Index;
        public Linker Linker = new(new CapabilityCache(Strategy));
        public Dictionary<ulong, DepotManifest> Map = [];
        public List<string> Globs = [];
        public HashSet<string> Isolated = new(StringComparer.OrdinalIgnoreCase);

        public Env()
        {
            Lib = new LibraryIndex(new AppPaths(Dir.Combine("data")));
            Index = new ContentIndex(Lib, Strategy);
        }

        public DepotJobRunner Runner() => new(new Manifests(Map), _ => Depot, new LibraryTargetResolver(Lib, Index, new Deduper(Lib, Linker), new SharePolicy
        {
            NeverShare = _ => PathGlob.AnyOf(Globs),
            Isolate = _ => rel => Isolated.Contains(rel),
        }), () => 4);

        public async Task<(VersionRecord V, DownloadJob Job)> Download(ulong manifestId)
        {
            var v = Lib.CreateVersion(10, Dir.Combine("root"), [(11, manifestId)]);
            var job = new DownloadJob { AppId = 10, DepotId = 11, ManifestId = manifestId, TargetVersionId = v.Id };
            await Runner().RunAsync(job, TestContext.Current.CancellationToken);
            return (v, job);
        }

        public string FilePath(VersionRecord v, string rel) => Path.Combine(Lib.GetVersionDir(v), rel);

        public void Dispose()
        {
            Lib.Dispose();
            Dir.Dispose();
        }
    }

    private static readonly byte[] Big = FakeDepot.Bytes("big-shared-asset-", 200);

    [Fact]
    public async Task IdenticalFilesAreSharedAcrossVersions()
    {
        using var env = new Env();
        env.Map[1] = env.Depot.Build(11, 1, 256, ("asset.pak", Big), ("exe.bin", FakeDepot.Bytes("v1")));
        env.Map[2] = env.Depot.Build(11, 2, 256, ("asset.pak", Big), ("exe.bin", FakeDepot.Bytes("v2!")));
        var (v1, _) = await env.Download(1);
        var fetches = env.Depot.Fetches;
        var (v2, job) = await env.Download(2);

        Assert.Equal(Big.Length, job.Counters.DedupedBytes);
        Assert.Equal(1, env.Depot.Fetches - fetches);
        var caps = env.Linker.Capabilities.Get(env.Dir.Path);
        if (!caps.Reflink)
            Assert.Equal(Strategy.GetFileIdentity(env.FilePath(v1, "asset.pak")).FileId, Strategy.GetFileIdentity(env.FilePath(v2, "asset.pak")).FileId);
        var snap = VersionStateStore.Load(env.Lib.Paths.VersionStateFile(v2.Id)).Files.Single(f => f.RelPath == "asset.pak");
        Assert.Equal(caps.Reflink ? LinkKind.Reflink : LinkKind.Hardlink, snap.Link);
        Assert.Equal(Big, File.ReadAllBytes(env.FilePath(v2, "asset.pak")));
    }

    [Fact]
    public async Task ExcludedAndIsolatedFilesAreNotHardlinked()
    {
        using var env = new Env();
        var cfg = FakeDepot.Bytes("setting=1\n", 50);
        env.Map[1] = env.Depot.Build(11, 1, 256, ("asset.pak", Big), (@"cfg\game.cfg", cfg));
        env.Map[2] = env.Depot.Build(11, 2, 256, ("asset.pak", Big), (@"cfg\game.cfg", cfg));
        env.Globs.Add("*.pak");
        env.Isolated.Add(Path.Combine("cfg", "game.cfg"));
        var (v1, _) = await env.Download(1);
        var (v2, job) = await env.Download(2);

        var caps = env.Linker.Capabilities.Get(env.Dir.Path);
        Assert.NotEqual(Strategy.GetFileIdentity(env.FilePath(v1, "asset.pak")).FileId, Strategy.GetFileIdentity(env.FilePath(v2, "asset.pak")).FileId);
        Assert.Equal(1u, Strategy.GetFileIdentity(env.FilePath(v2, Path.Combine("cfg", "game.cfg"))).LinkCount);
        Assert.Equal(caps.Reflink ? cfg.Length : 0, job.Counters.DedupedBytes);
        Assert.Equal(cfg, File.ReadAllBytes(env.FilePath(v2, Path.Combine("cfg", "game.cfg"))));
    }

    [Fact]
    public async Task ModifiedSourceIsNotUsed()
    {
        using var env = new Env();
        env.Map[1] = env.Depot.Build(11, 1, 256, ("asset.pak", Big));
        env.Map[2] = env.Depot.Build(11, 2, 256, ("asset.pak", Big));
        var (v1, _) = await env.Download(1);
        var tampered = (byte[])Big.Clone();
        tampered[0] = (byte)'#';
        File.WriteAllBytes(env.FilePath(v1, "asset.pak"), tampered);
        var (v2, job) = await env.Download(2);
        Assert.Equal(0, job.Counters.DedupedBytes);
        Assert.Equal(Big, File.ReadAllBytes(env.FilePath(v2, "asset.pak")));
        Assert.Equal(tampered, File.ReadAllBytes(env.FilePath(v1, "asset.pak")));
    }

    [Theory]
    [InlineData("*.cfg", @"cfg\game.cfg", true)]
    [InlineData("cfg/*", @"cfg\game.cfg", true)]
    [InlineData("save/**", @"save\slot1\a.sav", true)]
    [InlineData("*.cfg", @"bin\game.exe", false)]
    [InlineData("game.CFG", @"cfg\game.cfg", true)]
    public void GlobMatching(string pattern, string path, bool expected) => Assert.Equal(expected, PathGlob.IsMatch(pattern, path));
}
