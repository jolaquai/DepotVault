namespace DepotVault.Core.Persistence;

public enum CopyFallback
{
    Unset,
    Never,
    Always,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class Settings : ISchemaVersioned
{
    public int SchemaVersion { get; set; }
    public List<string> LibraryRoots { get; set; } = [];
    public int MaxConcurrentJobs { get; set; } = 1;
    public int MaxConcurrentChunks { get; set; } = 16;
    public long BandwidthLimitBytesPerSecond { get; set; }
    public CopyFallback CopyFallback { get; set; }
    public bool DedupeEnabled { get; set; } = true;
    public List<string> GlobalExclusionGlobs { get; set; } = [];
    public bool ReadOnlyProtection { get; set; }
    public bool IntegrityCheckOnStartup { get; set; } = true;
    public bool AcfLock { get; set; } = true;
    public bool TutorialSeen { get; set; }
    public bool TutorialDontShowAgain { get; set; }
    public AppTheme Theme { get; set; }

    public Settings Normalize()
    {
        LibraryRoots ??= [];
        GlobalExclusionGlobs ??= [];
        MaxConcurrentJobs = Math.Clamp(MaxConcurrentJobs, 1, 16);
        MaxConcurrentChunks = Math.Clamp(MaxConcurrentChunks, 1, 128);
        if (BandwidthLimitBytesPerSecond < 0)
            BandwidthLimitBytesPerSecond = 0;
        return this;
    }
}

public sealed class SettingsStore : IDisposable
{
    private readonly AtomicJsonStore<Settings> _store;

    public SettingsStore(AppPaths paths)
    {
        _store = new AtomicJsonStore<Settings>(paths.Settings, JsonContext.Default.Settings);
        Current = _store.Load().Normalize();
    }

    public Settings Current { get; }

    public event Action<Settings> Changed;

    public void Save()
    {
        Current.Normalize();
        _store.ScheduleSave(Current);
        Changed?.Invoke(Current);
    }

    public void Flush() => _store.Flush();

    public void Dispose() => _store.Dispose();
}
