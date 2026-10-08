using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;
using DepotVault.Tests.Download;
using SteamKit2;

namespace DepotVault.Tests.Library;

public class HealTests
{
    private static readonly ILinkStrategy Strategy = LinkStrategy.CreateForCurrentPlatform();
    private static readonly byte[] Asset = FakeDepot.Bytes("pristine-asset-data-", 300);

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
        public readonly TempDir Dir = new();
        public readonly LibraryIndex Lib;
        public readonly AppRepository Apps;
        public readonly FakeDepot Depot = new();
        public readonly ContentIndex Index;
        public readonly Linker Linker = new(new CapabilityCache(Strategy));
        public readonly Dictionary<ulong, DepotManifest> Map = [];

        public Env()
        {
            var paths = new AppPaths(Dir.Combine("data"));
            Lib = new LibraryIndex(paths);
            Apps = new AppRepository(paths);
            Index = new ContentIndex(Lib, Strategy);
            Map[1] = Depot.Build(11, 1, 256, ("asset.pak", Asset), ("exe.bin", FakeDepot.Bytes("one")));
            Map[2] = Depot.Build(11, 2, 256, ("asset.pak", Asset), ("exe.bin", FakeDepot.Bytes("two")));
            Map[3] = Depot.Build(11, 3, 256, ("asset.pak", Asset), ("exe.bin", FakeDepot.Bytes("three")));
        }

        public async Task<VersionRecord> Download(ulong manifestId, bool dedupe = true)
        {
            var v = Lib.CreateVersion(10, Dir.Combine("root"), [(11, manifestId)]);
            var resolver = dedupe ? new LibraryTargetResolver(Lib, Index, new Deduper(Lib, Linker)) : new LibraryTargetResolver(Lib, Index);
            await new DepotJobRunner(new Manifests(Map), _ => Depot, resolver, () => 2).RunAsync(new DownloadJob { AppId = 10, DepotId = 11, ManifestId = manifestId, TargetVersionId = v.Id }, TestContext.Current.CancellationToken);
            return v;
        }

        public Healer Healer() => new(Lib, Apps, Index, Linker, _ => Depot);
        public string P(VersionRecord v, string rel = "asset.pak") => Path.Combine(Lib.GetVersionDir(v), rel);

        public void Dispose()
        {
            Apps.Dispose();
            Lib.Dispose();
            Dir.Dispose();
        }
    }

    private static void ModifyInPlace(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.Write("MODIFIED"u8);
    }

    [Fact]
    public async Task ModifiedHardlinkIsHealedForInactiveVersionsAndKeptForActive()
    {
        using var env = new Env();
        var caps = env.Linker.Capabilities.Get(env.Dir.Path);
        Assert.SkipWhen(caps.Reflink, "Reflinked files are not shared inodes");
        var v1 = await env.Download(1);
        var v2 = await env.Download(2);
        Assert.Equal(2u, Strategy.GetFileIdentity(env.P(v1)).LinkCount);
        env.Lib.SetActive(10, v2.Id);

        await FsTime.WaitUntilNewerAsync(env.P(v2), TestContext.Current.CancellationToken);
        ModifyInPlace(env.P(v2));

        var issues = new IntegrityChecker(env.Lib).ScanApp(10);
        Assert.Contains(issues, i => i.VersionId == v1.Id && i.RelPath == "asset.pak" && i.Kind == IssueKind.Changed);
        Assert.Contains(issues, i => i.VersionId == v2.Id && i.RelPath == "asset.pak");

        var fetches = env.Depot.Fetches;
        var report = await env.Healer().HealAsync(10, issues, TestContext.Current.CancellationToken);

        Assert.Equal(Asset, File.ReadAllBytes(env.P(v1)));
        Assert.StartsWith("MODIFIED", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(env.P(v2))));
        Assert.Equal(1u, Strategy.GetFileIdentity(env.P(v1)).LinkCount);
        Assert.Single(report.Refetched);
        Assert.Single(report.KeptDiverged);
        Assert.True(env.Depot.Fetches > fetches);
        var app = env.Apps.Get(10);
        Assert.Contains("asset.pak", app.Exclusions);
        Assert.Contains("asset.pak", app.ReviewCandidates);
        Assert.Empty(new IntegrityChecker(env.Lib).ScanApp(10));
    }

    [Fact]
    public async Task IntactSiblingIsUsedInsteadOfRefetch()
    {
        using var env = new Env();
        var caps = env.Linker.Capabilities.Get(env.Dir.Path);
        Assert.SkipWhen(caps.Reflink, "Reflinked files are not shared inodes");
        var v1 = await env.Download(1);
        var v2 = await env.Download(2);
        var v3 = await env.Download(3, dedupe: false);
        env.Index.Rebuild();
        Assert.Equal(1u, Strategy.GetFileIdentity(env.P(v3)).LinkCount);

        await FsTime.WaitUntilNewerAsync(env.P(v1), TestContext.Current.CancellationToken);
        ModifyInPlace(env.P(v1));
        var fetches = env.Depot.Fetches;
        var report = await env.Healer().HealAsync(10, new IntegrityChecker(env.Lib).ScanApp(10), TestContext.Current.CancellationToken);

        Assert.Equal(fetches, env.Depot.Fetches);
        Assert.Equal(2, report.Restored.Count);
        Assert.Equal(Asset, File.ReadAllBytes(env.P(v1)));
        Assert.Equal(Asset, File.ReadAllBytes(env.P(v2)));
    }

    [Fact]
    public async Task SharedDecisionSkipsHealAndTouchedFileIsBenign()
    {
        using var env = new Env();
        var v1 = await env.Download(1);
        var app = env.Apps.Get(10);
        app.SetDecision("exe.bin", MutableDecision.Share);
        await FsTime.WaitUntilNewerAsync(env.P(v1, "exe.bin"), TestContext.Current.CancellationToken);
        File.WriteAllText(env.P(v1, "exe.bin"), "user edit");
        File.SetLastWriteTimeUtc(env.P(v1), DateTime.UtcNow.AddHours(-1));

        var report = await env.Healer().HealAsync(10, new IntegrityChecker(env.Lib).ScanApp(10), TestContext.Current.CancellationToken);
        Assert.Single(report.SkippedShared);
        Assert.Single(report.Benign);
        Assert.Equal("user edit", File.ReadAllText(env.P(v1, "exe.bin")));
        Assert.DoesNotContain("exe.bin", app.Exclusions);
        Assert.Empty(new IntegrityChecker(env.Lib).ScanApp(10));
    }

    [Fact]
    public async Task MissingFileIsRestored()
    {
        using var env = new Env();
        var v1 = await env.Download(1);
        File.Delete(env.P(v1, "exe.bin"));
        var issues = new IntegrityChecker(env.Lib).ScanApp(10);
        Assert.Equal(IssueKind.Missing, Assert.Single(issues).Kind);
        var report = await env.Healer().HealAsync(10, issues, TestContext.Current.CancellationToken);
        Assert.Single(report.Refetched);
        Assert.Equal("one", File.ReadAllText(env.P(v1, "exe.bin")));
    }
}
