using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;
using DepotVault.Tests.Download;

namespace DepotVault.Tests.Library;

public class LibraryIndexTests
{
    private static readonly ILinkStrategy Strategy = LinkStrategy.CreateForCurrentPlatform();

    [Fact]
    public void CreatesVersionsAndPersists()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Combine("data"));
        string id;
        using (var lib = new LibraryIndex(paths))
        {
            var v = lib.CreateVersion(10, dir.Combine("root"), [(11, 111ul), (12, 112ul)], "launch");
            id = v.Id;
            Assert.Equal(8, id.Length);
            Assert.True(Directory.Exists(lib.GetVersionDir(v)));
            Assert.EndsWith(Path.Combine("root", "10", id), lib.GetVersionDir(v));
            lib.MarkDepotComplete(id, 11, 111);
            Assert.False(lib.Find(id).IsComplete);
            lib.MarkDepotComplete(id, 12, 112);
            Assert.True(lib.Find(id).IsComplete);
            lib.SetActive(10, id);
        }
        using var reloaded = new LibraryIndex(paths);
        var r = reloaded.Find(id);
        Assert.Equal("launch", r.Label);
        Assert.True(r.IsComplete);
        Assert.Equal(id, reloaded.GetApp(10).ActiveVersionId);
        Assert.Same(r, reloaded.FindVersionWith(10, [(12, 112ul), (11, 111ul)]));
        Assert.Null(reloaded.FindVersionWith(10, [(12, 112ul)]));
    }

    [Fact]
    public void UniqueSizeIgnoresHardlinkedFilesAndDeleteKeepsSiblings()
    {
        using var dir = new TempDir();
        using var lib = new LibraryIndex(new AppPaths(dir.Combine("data")));
        var a = lib.CreateVersion(10, dir.Combine("root"), [(11, 1ul)]);
        var b = lib.CreateVersion(10, dir.Combine("root"), [(11, 2ul)]);
        var da = lib.GetVersionDir(a);
        var db = lib.GetVersionDir(b);
        File.WriteAllBytes(Path.Combine(da, "shared.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(da, "onlyA.bin"), new byte[300]);
        Assert.True(Strategy.TryHardlink(Path.Combine(da, "shared.bin"), Path.Combine(db, "shared.bin")));
        File.WriteAllBytes(Path.Combine(db, "onlyB.bin"), new byte[50]);

        Assert.Equal(300, lib.ComputeUniqueSize(a, Strategy));
        Assert.Equal(50, lib.ComputeUniqueSize(b, Strategy));
        Assert.Equal(1300, LibraryIndex.ComputeTotalSize(da));

        lib.DeleteVersion(a.Id);
        Assert.False(Directory.Exists(da));
        Assert.Null(lib.Find(a.Id));
        Assert.Equal(1000, new FileInfo(Path.Combine(db, "shared.bin")).Length);
        Assert.Equal(1050, lib.ComputeUniqueSize(b, Strategy));
    }

    [Fact]
    public void ActiveVersionCannotBeDeleted()
    {
        using var dir = new TempDir();
        using var lib = new LibraryIndex(new AppPaths(dir.Combine("data")));
        var a = lib.CreateVersion(10, dir.Combine("root"), [(11, 1ul)]);
        lib.SetActive(10, a.Id);
        Assert.Throws<InvalidOperationException>(() => lib.DeleteVersion(a.Id));
    }

    [Fact]
    public void RootsSuggestedPerSteamLibraryAndMatchedByVolume()
    {
        using var dir = new TempDir();
        var suggestions = LibraryRoots.Suggest([dir.Combine("SteamLibrary"), dir.Combine("Other")], [dir.Combine("Other", "DepotVault")]).ToList();
        Assert.Equal([LibraryRoots.Normalize(dir.Combine("SteamLibrary", "DepotVault"))], suggestions);

        var root = dir.Combine("vault");
        var roots = new LibraryRoots(() => [root], Strategy);
        Assert.Equal(LibraryRoots.Normalize(root), roots.FindForPath(dir.Combine("steamapps", "common", "Game")));
    }

    [Fact]
    public async Task MultiDepotDownloadsMergeIntoOneVersionWithChunkDiff()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Combine("data"));
        using var lib = new LibraryIndex(paths);
        var depot = new FakeDepot();
        var content = string.Concat(Enumerable.Range(0, 30).Select(i => $"chunk{i:D3}-----------------")).Select(c => (byte)c).ToArray();
        var m1 = depot.Build(11, 1, 32, ("game.pak", content));
        var m2 = depot.Build(12, 2, 32, ("lang.pak", FakeDepot.Bytes("en", 40)));
        var changed = (byte[])content.Clone();
        changed[100] = (byte)'#';
        var m3 = depot.Build(11, 3, 32, ("game.pak", changed));

        var resolver = new LibraryTargetResolver(lib);
        var manifests = new Dictionary<ulong, SteamKit2.DepotManifest> { [1] = m1, [2] = m2, [3] = m3 };
        var runner = new DepotJobRunner(new DictManifests(manifests), _ => depot, resolver, () => 4);

        var v1 = lib.CreateVersion(10, dir.Combine("root"), [(11, 1ul), (12, 2ul)]);
        await runner.RunAsync(new DownloadJob { AppId = 10, DepotId = 11, ManifestId = 1, TargetVersionId = v1.Id }, TestContext.Current.CancellationToken);
        await runner.RunAsync(new DownloadJob { AppId = 10, DepotId = 12, ManifestId = 2, TargetVersionId = v1.Id }, TestContext.Current.CancellationToken);
        Assert.True(lib.Find(v1.Id).IsComplete);
        Assert.True(File.Exists(Path.Combine(lib.GetVersionDir(v1), "game.pak")));
        Assert.True(File.Exists(Path.Combine(lib.GetVersionDir(v1), "lang.pak")));
        Assert.Equal(2, VersionStateStore.Load(paths.VersionStateFile(v1.Id)).Files.Count);

        var v2 = lib.CreateVersion(10, dir.Combine("root"), [(11, 3ul)]);
        var job = new DownloadJob { AppId = 10, DepotId = 11, ManifestId = 3, TargetVersionId = v2.Id };
        await runner.RunAsync(job, TestContext.Current.CancellationToken);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(lib.GetVersionDir(v2), "game.pak")));
        Assert.True(job.Counters.ReusedBytes > 0);
    }

    private sealed class DictManifests(Dictionary<ulong, SteamKit2.DepotManifest> manifests) : IManifestProvider
    {
        public Task<SteamKit2.DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, string savePath, CancellationToken ct)
        {
            var m = manifests[manifestId];
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            m.SaveToFile(savePath);
            return Task.FromResult(m);
        }
    }
}
