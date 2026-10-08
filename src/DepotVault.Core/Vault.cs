using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;
using DepotVault.Core.Steam;
using DepotVault.Core.SteamInstall;
using Microsoft.Extensions.Logging;
using CdnClient = SteamKit2.CDN.Client;

namespace DepotVault.Core;

public sealed class Vault : IAsyncDisposable, IDisposable
{
    private readonly ILogger _log;

    public Vault(AppPaths paths, ILoggerFactory logs)
    {
        Paths = paths;
        _log = logs.CreateLogger<Vault>();
        Settings = new SettingsStore(paths);
        Secrets = new SecretStore(paths.Auth, interactive: true);
        Session = new SteamSession(Secrets);
        Apps = new AppRepository(paths);
        Library = new LibraryIndex(paths);
        Strategy = LinkStrategy.CreateForCurrentPlatform();
        Capabilities = new CapabilityCache(Strategy);
        Linker = new Linker(Capabilities);
        Index = new ContentIndex(Library, Strategy, root => Capabilities.Get(root).MaxHardlinks);
        Deduper = new Deduper(Library, Linker);
        Metadata = new DepotMetadataService(Session, Apps);
        Keys = new DepotKeyCache(Session);
        Cdn = new CdnPool(Session);
        CdnClient = new CdnClient(Session.Client);
        Manifests = new ManifestService(Session, Keys, Cdn, CdnClient);
        Roots = new LibraryRoots(() => Settings.Current.LibraryRoots, Strategy);
        Locator = SteamLocator.Detect();
        Integrity = new IntegrityChecker(Library);
        Healer = new Healer(Library, Apps, Index, Linker, CreateChunkSource);
        ReadOnly = new ReadOnlyProtection(Library, Apps, Strategy, () => Settings.Current.ReadOnlyProtection);
        var policy = MutableFileScanner.BuildPolicy(Apps.Get, () => Settings.Current.GlobalExclusionGlobs, () => Settings.Current.DedupeEnabled);
        Resolver = new LibraryTargetResolver(Library, Index, Deduper, policy);
        var runner = new DepotJobRunner(Manifests, CreateChunkSource, Resolver, () => Settings.Current.MaxConcurrentChunks, new BandwidthLimiter(() => Settings.Current.BandwidthLimitBytesPerSecond));
        Queue = new DownloadQueue(new AtomicJsonStore<QueueDocument>(paths.Queue, JsonContext.Default.QueueDocument), runner, () => Settings.Current.MaxConcurrentJobs);
        Queue.JobChanged += OnJobChanged;
        Session.StateChanged += OnSessionStateChanged;
        Remover = new Remover(Library, Apps, Index, Queue, CreateSwitcher, id => Locator?.FindApp(id));
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public SecretStore Secrets { get; }
    public SteamSession Session { get; }
    public AppRepository Apps { get; }
    public LibraryIndex Library { get; }
    public ILinkStrategy Strategy { get; }
    public CapabilityCache Capabilities { get; }
    public Linker Linker { get; }
    public ContentIndex Index { get; }
    public Deduper Deduper { get; }
    public DepotMetadataService Metadata { get; }
    public DepotKeyCache Keys { get; }
    public CdnPool Cdn { get; }
    public CdnClient CdnClient { get; }
    public ManifestService Manifests { get; }
    public LibraryRoots Roots { get; }
    public SteamLocator Locator { get; }
    public IntegrityChecker Integrity { get; }
    public Healer Healer { get; }
    public ReadOnlyProtection ReadOnly { get; }
    public LibraryTargetResolver Resolver { get; }
    public DownloadQueue Queue { get; }
    public Remover Remover { get; }
    public Func<bool> IsSteamRunning { get; set; } = SteamLocator.IsSteamRunning;

    public event Action<uint, HealReport> HealCompleted;

    public IChunkSource CreateChunkSource(uint appId) => new CdnChunkSource(appId, Cdn, CdnClient, Keys);

    public void Start()
    {
        Index.Rebuild();
        Queue.Start();
        if (Settings.Current.IntegrityCheckOnStartup)
            _ = Task.Run(StartupIntegrityAsync);
    }

    public string SuggestRoot(uint appId)
    {
        var installed = Locator?.FindApp(appId);
        if (installed is not null && Roots.FindForPath(installed.Library.Path) is { } existing)
            return existing;
        if (installed is not null)
            return Path.Combine(installed.Library.Path, LibraryRoots.FolderName);
        return Roots.All.FirstOrDefault() ?? Locator?.GetLibraries().Select(l => Path.Combine(l.Path, LibraryRoots.FolderName)).FirstOrDefault();
    }

    public void EnsureRoot(string root)
    {
        var norm = LibraryRoots.Normalize(root);
        if (Settings.Current.LibraryRoots.Contains(norm, LibraryRoots.PathComparer))
            return;
        Settings.Current.LibraryRoots.Add(norm);
        Settings.Save();
    }

    public int CountVersionsIn(string root)
    {
        var norm = LibraryRoots.Normalize(root);
        return Library.Versions.Count(v => v.Root is not null && LibraryRoots.PathComparer.Equals(LibraryRoots.Normalize(v.Root), norm));
    }

    public bool RemoveRoot(string root)
    {
        if (CountVersionsIn(root) > 0)
            return false;
        var norm = LibraryRoots.Normalize(root);
        if (Settings.Current.LibraryRoots.RemoveAll(r => LibraryRoots.PathComparer.Equals(LibraryRoots.Normalize(r), norm)) > 0)
            Settings.Save();
        return true;
    }

    public int ApplyReadOnlyProtection()
    {
        var count = 0;
        foreach (var v in Library.Versions)
            count += ReadOnly.Enabled ? ReadOnly.Apply(v) : ReadOnly.Remove(v);
        return count;
    }

    public int ApplyAcfLock()
    {
        var count = 0;
        foreach (var a in Library.Apps)
        {
            if (a.ActiveVersionId is null || a.ActiveVersionId == a.AdoptedVersionId || Locator?.FindApp(a.AppId) is not { } installed || !File.Exists(installed.AcfPath))
                continue;
            FileUtil.SetReadOnly(installed.AcfPath, Settings.Current.AcfLock);
            count++;
        }
        return count;
    }

    public VersionRecord EnqueueVersion(uint appId, IReadOnlyList<(uint DepotId, ulong ManifestId)> manifests, string root = null, string label = null, DateTime manifestDateUtc = default)
    {
        var existing = Library.FindVersionWith(appId, manifests);
        if (existing is not null && existing.IsComplete)
            return existing;
        root ??= SuggestRoot(appId) ?? throw new InvalidOperationException("No library root configured.");
        EnsureRoot(root);
        var rec = Apps.Get(appId);
        Library.EnsureApp(appId, rec.Name);
        var version = existing ?? Library.CreateVersion(appId, root, manifests, label, manifestDateUtc);
        foreach (var (depot, manifest) in manifests)
        {
            if (version.Manifests.Find(m => m.DepotId == depot) is { Complete: true })
                continue;
            if (Queue.Jobs.Any(j => j.TargetVersionId == version.Id && j.DepotId == depot && !j.IsFinished))
                continue;
            Queue.Enqueue(appId, depot, manifest, version.Id);
        }
        _log.LogInformation("Queued version {Version} of app {App}: {Manifests}", version.Id, appId, string.Join(", ", manifests.Select(m => $"{m.DepotId}:{m.ManifestId}")));
        return version;
    }

    public Switcher CreateSwitcher(ISwitchPrompts prompts) => new(Library, Apps, Settings, Linker, prompts, new SessionManifestSource(Session, Manifests, Paths), () => IsSteamRunning(), Index);

    public async Task<HealReport> CheckAndHealAsync(uint appId, CancellationToken ct = default)
    {
        var issues = Integrity.ScanApp(appId);
        if (issues.Count == 0)
            return new HealReport();
        var report = await Healer.HealAsync(appId, issues, ct).ConfigureAwait(false);
        _log.LogInformation("Integrity app {App}: {Issues} issues, {Restored} restored, {Refetched} refetched, {Kept} kept diverged, {Failed} failed", appId, issues.Count, report.Restored.Count, report.Refetched.Count, report.KeptDiverged.Count, report.Failed.Count);
        HealCompleted?.Invoke(appId, report);
        return report;
    }

    public void DeleteVersion(string versionId)
    {
        Index.RemoveVersion(versionId);
        Library.DeleteVersion(versionId);
        _log.LogInformation("Deleted version {Version}", versionId);
    }

    private async Task StartupIntegrityAsync()
    {
        foreach (var app in Library.Apps)
        {
            try
            {
                await CheckAndHealAsync(app.AppId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Startup integrity check failed for app {App}", app.AppId);
            }
        }
    }

    private void OnJobChanged(DownloadJob job)
    {
        switch (job.State)
        {
            case JobState.Done:
                _log.LogInformation("Job {Depot}:{Manifest} -> {Version} done: {Written} written, {Deduped} deduped, {Reused} reused", job.DepotId, job.ManifestId, job.TargetVersionId, job.Counters.WrittenBytes, job.Counters.DedupedBytes, job.Counters.ReusedBytes);
                if (Library.Find(job.TargetVersionId) is { IsComplete: true } v && ReadOnly.Enabled)
                    ReadOnly.ApplyApp(v.AppId);
                break;
            case JobState.Failed:
                _log.LogWarning("Job {Depot}:{Manifest} -> {Version} failed ({Kind}): {Error}", job.DepotId, job.ManifestId, job.TargetVersionId, job.ErrorKind, job.Error);
                if (job.ErrorKind == JobErrorKind.ManifestUnavailable)
                {
                    var app = Apps.Get(job.AppId);
                    if (app.MarkUnavailable(job.DepotId, job.ManifestId))
                        Apps.Save(app);
                }
                break;
        }
    }

    private void OnSessionStateChanged(SessionState state)
    {
        if (state != SessionState.LoggedOn)
            return;
        foreach (var j in Queue.Jobs)
        {
            if (j.State == JobState.Failed && JobErrors.IsTransient(j.ErrorKind))
                Queue.Resume(j.Id);
        }
    }

    private int _disposed;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        Queue.Dispose();
        Apps.Dispose();
        Library.Dispose();
        Settings.Dispose();
        CdnClient.Dispose();
        await Session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class SessionManifestSource(SteamSession session, ManifestService manifests, AppPaths paths) : IInstalledManifestSource
    {
        public Task<SteamKit2.DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct)
        {
            var path = Path.Combine(paths.Root, "manifest-cache", $"{depotId}_{manifestId}.manifest.bin");
            if (session.State != SessionState.LoggedOn && !File.Exists(path))
                throw new InvalidOperationException("Sign in to Steam first; the installed manifest is needed to take over the current install.");
            return manifests.GetAsync(appId, depotId, manifestId, path, ct);
        }
    }
}
