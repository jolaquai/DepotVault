using DepotVault.Core.Persistence;

namespace DepotVault.Tests.Persistence;

public class SettingsTests
{
    [Fact]
    public void DefaultsMatchPlan()
    {
        using var dir = new TempDir();
        using var store = new SettingsStore(new AppPaths(dir.Path));
        var s = store.Current;
        Assert.Equal(1, s.MaxConcurrentJobs);
        Assert.Equal(16, s.MaxConcurrentChunks);
        Assert.Equal(CopyFallback.Unset, s.CopyFallback);
        Assert.True(s.DedupeEnabled);
        Assert.Empty(s.GlobalExclusionGlobs);
        Assert.False(s.ReadOnlyProtection);
        Assert.True(s.IntegrityCheckOnStartup);
        Assert.True(s.AcfLock);
        Assert.Equal(AppTheme.System, s.Theme);
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        using (var store = new SettingsStore(paths))
        {
            store.Current.CopyFallback = CopyFallback.Always;
            store.Current.LibraryRoots.Add(@"D:\Vault");
            store.Current.GlobalExclusionGlobs.Add("*.cfg");
            store.Current.Theme = AppTheme.Dark;
            store.Current.MaxConcurrentChunks = 1000;
            store.Save();
        }
        Assert.Contains("\"copyFallback\": \"Always\"", File.ReadAllText(paths.Settings));
        using var reloaded = new SettingsStore(paths);
        Assert.Equal(CopyFallback.Always, reloaded.Current.CopyFallback);
        Assert.Equal([@"D:\Vault"], reloaded.Current.LibraryRoots);
        Assert.Equal(["*.cfg"], reloaded.Current.GlobalExclusionGlobs);
        Assert.Equal(AppTheme.Dark, reloaded.Current.Theme);
        Assert.Equal(128, reloaded.Current.MaxConcurrentChunks);
    }

    [Fact]
    public void MissingKeysKeepDefaults()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.Settings, """{ "schemaVersion": 1, "acfLock": false }""");
        using var store = new SettingsStore(paths);
        Assert.False(store.Current.AcfLock);
        Assert.True(store.Current.DedupeEnabled);
        Assert.Equal(16, store.Current.MaxConcurrentChunks);
    }
}
