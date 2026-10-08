using DepotVault.Core.Library;
using DepotVault.Core.Persistence;
using DepotVault.Core.Steam;
using SteamKit2;

namespace DepotVault.Tests.Steam;

public class DepotMetadataParserTests
{
    private static AppMetadata Load() => DepotMetadataParser.Parse(480000, KeyValue.LoadAsText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "appinfo.vdf")));

    [Fact]
    public void ParsesAppAndDepots()
    {
        var meta = Load();
        Assert.Equal("Sample Game", meta.Name);
        Assert.Equal("Sample Game", meta.InstallDir);
        Assert.Equal(9876543u, meta.PublicBuildId);
        Assert.Equal([228983u, 480001, 480002, 480003, 480004, 480005, 480006, 480007], meta.Depots.Select(d => d.DepotId));

        var content = meta.Depots.Single(d => d.DepotId == 480001);
        Assert.Equal("Sample Game Content", content.Name);
        Assert.Equal(1234567890123456789ul, content.CurrentManifestId);
        Assert.Equal(4294967296ul, content.CurrentManifestSize);
        Assert.Equal(5368709120ul, content.MaxSize);

        Assert.Equal(2345678901234567890ul, meta.Depots.Single(d => d.DepotId == 480002).CurrentManifestId);

        var shared = meta.Depots.Single(d => d.DepotId == 228983);
        Assert.True(shared.SharedInstall);
        Assert.Equal(228980u, shared.DepotFromApp);
        Assert.False(shared.IsDownloadable);
        Assert.Equal(480100u, meta.Depots.Single(d => d.DepotId == 480006).DlcAppId);
    }

    [Fact]
    public void DefaultSelectionFiltersByPlatform()
    {
        var meta = Load();
        Assert.Equal([480001u, 480002], DepotMetadataParser.SelectDefault(meta.Depots, new DepotFilter("windows", "64", "english")));
        Assert.Equal([480001u, 480003], DepotMetadataParser.SelectDefault(meta.Depots, new DepotFilter("linux", "64", "english")));
        Assert.Equal([480001u, 480004, 480007], DepotMetadataParser.SelectDefault(meta.Depots, new DepotFilter("windows", "32", "german")));
    }

    [Fact]
    public void DlcIncludedOnlyWhenOwned()
    {
        var meta = Load();
        var sel = DepotMetadataParser.SelectDefault(meta.Depots, new DepotFilter("windows", "64", "english"), id => id == 480100);
        Assert.Contains(480006u, sel);
    }

    [Fact]
    public void AppRecordPersists()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        using (var repo = new AppRepository(paths))
        {
            var rec = repo.Get(480000);
            rec.Name = "Sample";
            rec.Depots = Load().Depots;
            rec.SelectedDepots = [480001];
            repo.Save(rec, debounced: false);
        }
        using var repo2 = new AppRepository(paths);
        Assert.Equal([480000u], repo2.EnumerateStored());
        var loaded = repo2.Get(480000);
        Assert.Equal("Sample", loaded.Name);
        Assert.Equal(8, loaded.Depots.Count);
        Assert.Equal([480001u], loaded.SelectedDepots);
    }
}
