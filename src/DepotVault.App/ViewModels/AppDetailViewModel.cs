using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Steam;
using DepotVault.Core.SteamInstall;
using Microsoft.Extensions.Logging;

namespace DepotVault.App.ViewModels;

public partial class DepotItemViewModel(DepotInfo depot, bool selected, Action changed) : ObservableObject
{
    public DepotInfo Depot { get; } = depot;
    public uint DepotId => Depot.DepotId;
    public string Name => string.IsNullOrEmpty(Depot.Name) ? $"Depot {Depot.DepotId}" : Depot.Name;
    public string Platform => string.Join(" ", new[] { Depot.OsList, Depot.OsArch is { Length: > 0 } a ? a + "-bit" : null, Depot.Language }.Where(s => !string.IsNullOrEmpty(s)));
    public string Current => Depot.CurrentManifestId == 0 ? "" : Depot.CurrentManifestId.ToString();
    public bool IsDownloadable => Depot.IsDownloadable;

    [ObservableProperty]
    private bool isSelected = selected;

    partial void OnIsSelectedChanged(bool value) => changed();
}

public partial class HistoryItemViewModel(ManifestHistoryEntry entry, Action changed) : ObservableObject
{
    public ManifestHistoryEntry Entry { get; } = entry;
    public uint DepotId => Entry.DepotId;
    public ulong ManifestId => Entry.ManifestId;
    public string Date => Format.Date(Entry.DateUtc);
    public bool Unavailable => Entry.Unavailable;

    [ObservableProperty]
    private bool isDownloaded;

    public string Label
    {
        get => Entry.Label;
        set
        {
            if (Entry.Label == value)
                return;
            Entry.Label = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged();
            changed();
        }
    }
}

public partial class VersionItemViewModel(VersionRecord record, Action<VersionItemViewModel> changed) : ObservableObject
{
    public VersionRecord Record { get; private set; } = record;
    public string Id => Record.Id;
    public string Date => Format.Date(Record.ManifestDateUtc == default ? Record.CreatedUtc : Record.ManifestDateUtc);
    public string Manifests => string.Join(", ", Record.Manifests.Select(m => $"{m.DepotId}:{m.ManifestId}"));

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    private string status;

    [ObservableProperty]
    private string uniqueSize = "...";

    [ObservableProperty]
    private string label = record.Label;

    [ObservableProperty]
    private string notes = record.Notes;

    public bool IsComplete => Record.IsComplete;
    public bool IsAdopted => Record.Adopted;

    partial void OnLabelChanged(string value) => changed(this);
    partial void OnNotesChanged(string value) => changed(this);

    public void Update(VersionRecord r)
    {
        Record = r;
        OnPropertyChanged(nameof(Manifests));
        OnPropertyChanged(nameof(IsComplete));
    }
}

public partial class AppDetailViewModel : ObservableObject, IDisposable
{
    private readonly Vault _vault;
    private readonly DialogService _dialogs;
    private readonly AppDialogs _appDialogs;
    private readonly ISwitchPromptsFactory _prompts;
    private readonly ILogger<AppDetailViewModel> _log;
    private readonly DispatcherTimer _timer;
    private AppRecord _app;

    public AppDetailViewModel(uint appId, Vault vault, DialogService dialogs, AppDialogs appDialogs, ISwitchPromptsFactory prompts, ILogger<AppDetailViewModel> log)
    {
        AppId = appId;
        _vault = vault;
        _dialogs = dialogs;
        _appDialogs = appDialogs;
        _prompts = prompts;
        _log = log;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateJobStatus());
        _vault.Queue.JobChanged += OnJobChanged;
        _vault.HealCompleted += OnHealCompleted;
        Refresh();
    }

    public uint AppId { get; }
    public ObservableCollection<DepotItemViewModel> Depots { get; } = [];
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];
    public ObservableCollection<VersionItemViewModel> Versions { get; } = [];

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string installText;

    [ObservableProperty]
    private string metadataText;

    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadHistoryCommand))]
    private HistoryItemViewModel selectedHistory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand), nameof(DeleteCommand), nameof(OpenFolderCommand), nameof(VerifyCommand))]
    private VersionItemViewModel selectedVersion;

    public bool HasHistory => History.Count > 0;
    public bool HasAdopted => _vault.Library.GetApp(AppId)?.AdoptedVersionId is not null;
    public int PendingReviewCount => _app?.ReviewCandidates?.Count ?? 0;
    public IReadOnlyList<string> StrategyOptions { get; } = ["Automatic (recommended)", "Whole-folder link", "Hardlinks", "Symbolic links", "Copy files (full disk space)"];
    private static readonly string[] StrategyValues = [null, "junction", "hardlink", "symlink", "copy"];
    private bool _loadingStrategy;

    [ObservableProperty]
    private int strategyIndex;

    partial void OnStrategyIndexChanged(int value)
    {
        if (_loadingStrategy || _app is null)
            return;
        _app.ForceStrategy = StrategyValues[Math.Clamp(value, 0, StrategyValues.Length - 1)];
        _vault.Apps.Save(_app);
    }
    public bool HasPendingReview => PendingReviewCount > 0;

    public void OnActivated()
    {
        _timer.Start();
        Refresh();
    }

    public void Refresh()
    {
        _app = _vault.Apps.Get(AppId);
        Name = _app.Name ?? _vault.Library.GetApp(AppId)?.Name ?? $"App {AppId}";
        MetadataText = _app.MetadataFetchedUtc == default ? "Depot info not loaded yet. Sign in and refresh." : $"Depot info from {Format.Date(_app.MetadataFetchedUtc)}, public build {_app.PublicBuildId}";
        var installed = _vault.Locator?.FindApp(AppId);
        InstallText = installed is null ? "Not installed in Steam" : $"Installed: build {installed.Acf.BuildId} at {installed.InstallPath}";

        var selected = _app.SelectedDepots ?? DepotMetadataParser.SelectDefault(_app.Depots, DepotFilter.ForCurrentPlatform(), id => OwnsApp(id));
        Depots.Clear();
        foreach (var d in _app.Depots.Where(d => d.IsDownloadable || d.DlcAppId != 0))
            Depots.Add(new DepotItemViewModel(d, selected.Contains(d.DepotId), SaveDepotSelection));

        var downloaded = _vault.Library.VersionsFor(AppId).SelectMany(v => v.Manifests.Where(m => m.Complete).Select(m => m.ManifestId)).ToHashSet();
        History.Clear();
        foreach (var h in _app.History.OrderByDescending(h => h.DateUtc))
            History.Add(new HistoryItemViewModel(h, () => _vault.Apps.Save(_app)) { IsDownloaded = downloaded.Contains(h.ManifestId) });
        OnPropertyChanged(nameof(HasHistory));

        var active = _vault.Library.GetApp(AppId)?.ActiveVersionId;
        var versions = _vault.Library.VersionsFor(AppId);
        for (var i = Versions.Count - 1; i >= 0; i--)
        {
            if (!versions.Any(v => v.Id == Versions[i].Id))
                Versions.RemoveAt(i);
        }
        foreach (var v in versions)
        {
            var item = Versions.FirstOrDefault(x => x.Id == v.Id);
            if (item is null)
                Versions.Add(item = new VersionItemViewModel(v, SaveVersion));
            else
                item.Update(v);
            item.IsActive = v.Id == active;
            _ = UpdateUniqueSizeAsync(item);
        }
        OnPropertyChanged(nameof(HasAdopted));
        OnPropertyChanged(nameof(PendingReviewCount));
        _loadingStrategy = true;
        StrategyIndex = Math.Max(0, Array.IndexOf(StrategyValues, _app.ForceStrategy?.Trim().ToLowerInvariant() is { Length: > 0 } f ? f : null));
        _loadingStrategy = false;
        OnPropertyChanged(nameof(HasPendingReview));
        UpdateJobStatus();
    }

    private void OnHealCompleted(uint appId, HealReport report)
    {
        if (appId == AppId)
            Dispatcher.UIThread.Post(Refresh);
    }

    private async Task<bool> ReviewAsync()
    {
        var saved = await _appDialogs.ShowMutableReviewAsync(AppId);
        Refresh();
        return saved;
    }

    private bool OwnsApp(uint appId) => false;

    private async Task UpdateUniqueSizeAsync(VersionItemViewModel item)
    {
        var size = await Task.Run(() => _vault.Library.ComputeUniqueSize(item.Record, _vault.Strategy));
        item.UniqueSize = Format.Bytes(size);
    }

    private void SaveDepotSelection()
    {
        _app.SelectedDepots = Depots.Where(d => d.IsSelected).Select(d => d.DepotId).ToList();
        _vault.Apps.Save(_app);
    }

    private void SaveVersion(VersionItemViewModel item) => _vault.Library.Update(item.Id, v =>
    {
        v.Label = string.IsNullOrWhiteSpace(item.Label) ? null : item.Label.Trim();
        v.Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes;
    });

    private void OnJobChanged(DownloadJob job)
    {
        if (job.AppId != AppId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (job.State == JobState.Done)
                Refresh();
            else
                UpdateJobStatus();
        });
    }

    private void UpdateJobStatus()
    {
        var jobs = _vault.Queue.Jobs.Where(j => j.AppId == AppId && !j.IsFinished).ToList();
        foreach (var item in Versions)
        {
            var mine = jobs.Where(j => j.TargetVersionId == item.Id).ToList();
            if (mine.Count > 0)
            {
                long done = mine.Sum(j => j.Counters.CompletedBytes), total = mine.Sum(j => j.Counters.TotalBytes);
                var state = mine.Any(j => j.State == JobState.Running) ? "Downloading" : mine.Any(j => j.State == JobState.Paused) ? "Paused" : "Queued";
                item.Status = total > 0 ? $"{state} {done * 100 / total}%" : state;
            }
            else
            {
                var failed = _vault.Queue.Jobs.FirstOrDefault(j => j.TargetVersionId == item.Id && j.State == JobState.Failed);
                item.Status = item.IsComplete ? (item.IsAdopted ? "Original install" : "Ready") : failed is not null ? $"Failed: {failed.Error}" : "Incomplete";
            }
        }
    }

    [RelayCommand]
    private async Task RefreshMetadataAsync()
    {
        if (_vault.Session.State != SessionState.LoggedOn)
        {
            StatusText = "Sign in to load depot information.";
            return;
        }
        IsBusy = true;
        StatusText = "Loading depot information...";
        try
        {
            var rec = await _vault.Metadata.RefreshAsync(AppId);
            _vault.Library.EnsureApp(AppId, rec.Name);
            StatusText = null;
            Refresh();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Metadata refresh failed for {App}", AppId);
            StatusText = $"Could not load depot info: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadCurrentAsync()
    {
        var manifests = Depots.Where(d => d.IsSelected && d.Depot.CurrentManifestId != 0).Select(d => (d.DepotId, d.Depot.CurrentManifestId)).ToList();
        await EnqueueAsync(manifests, $"Build {_app.PublicBuildId}", DateTime.UtcNow);
    }

    private bool CanDownloadHistory() => SelectedHistory is not null;

    [RelayCommand(CanExecute = nameof(CanDownloadHistory))]
    private async Task DownloadHistoryAsync()
    {
        var pick = SelectedHistory.Entry;
        var manifests = new List<(uint, ulong)> { (pick.DepotId, pick.ManifestId) };
        var missing = new List<uint>();
        foreach (var d in Depots.Where(d => d.IsSelected && d.DepotId != pick.DepotId))
        {
            var match = _app.History.Where(h => h.DepotId == d.DepotId && !h.Unavailable && (pick.DateUtc == default || h.DateUtc <= pick.DateUtc)).MaxBy(h => h.DateUtc);
            if (match is null)
                missing.Add(d.DepotId);
            else
                manifests.Add((d.DepotId, match.ManifestId));
        }
        await EnqueueAsync(manifests, pick.Label, pick.DateUtc);
        if (missing.Count > 0)
            StatusText += $" No history imported for depot(s) {string.Join(", ", missing)}; they were left out.";
    }

    private async Task EnqueueAsync(List<(uint DepotId, ulong ManifestId)> manifests, string label, DateTime date)
    {
        if (manifests.Count == 0)
        {
            StatusText = "Select at least one depot.";
            return;
        }
        if (!_app.MutableReviewed && _vault.Settings.Current.DedupeEnabled && _vault.Library.VersionsFor(AppId).Any(v => v.IsComplete))
            await ReviewAsync();
        try
        {
            var v = _vault.EnqueueVersion(AppId, manifests, label: label, manifestDateUtc: date);
            StatusText = v.IsComplete ? "That version is already in the library." : $"Queued version {v.Label ?? v.Id}.";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool HasVersion() => SelectedVersion is not null;
    private bool CanSwitch() => SelectedVersion is { IsComplete: true, IsActive: false } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private async Task SwitchAsync()
    {
        var installed = _vault.Locator?.FindApp(AppId);
        if (installed is null)
        {
            StatusText = "The game is not installed in Steam, so there is nothing to switch. Install it in Steam first.";
            return;
        }
        if (!_app.MutableReviewed)
            await ReviewAsync();
        IsBusy = true;
        StatusText = "Checking library integrity...";
        try
        {
            await _vault.CheckAndHealAsync(AppId);
            StatusText = "Switching...";
            var report = await _vault.CreateSwitcher(_prompts.Create()).SwitchAsync(new SwitchRequest
            {
                AppId = AppId,
                TargetVersionId = SelectedVersion.Id,
                Install = installed,
                AcfBuildId = _app.PublicBuildId,
            });
            StatusText = report.Canceled ? "Switch canceled." : report.Success ? $"Switched ({report.Mode})." : $"Switch finished with {report.Failures.Count} problem(s).";
            if (!report.Success && !report.Canceled)
                _ = _appDialogs.ShowSwitchReportAsync(report);
            Refresh();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Switch failed for {App}", AppId);
            StatusText = $"Switch failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            SwitchCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task RevertAsync()
    {
        var installed = _vault.Locator?.FindApp(AppId);
        if (installed is null)
        {
            StatusText = "The game is not installed in Steam.";
            return;
        }
        IsBusy = true;
        try
        {
            var report = await _vault.CreateSwitcher(_prompts.Create()).RevertAsync(AppId, installed);
            StatusText = report.Canceled ? "Revert canceled." : report.Success ? "Reverted to the original install." : $"Revert finished with {report.Failures.Count} problem(s).";
            if (!report.Success && !report.Canceled)
                _ = _appDialogs.ShowSwitchReportAsync(report);
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = $"Revert failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Launch() => Open($"steam://run/{AppId}");

    [RelayCommand(CanExecute = nameof(HasVersion))]
    private void OpenFolder() => Open(_vault.Library.GetVersionDir(SelectedVersion.Record));

    [RelayCommand(CanExecute = nameof(HasVersion))]
    private async Task VerifyAsync()
    {
        IsBusy = true;
        StatusText = "Verifying...";
        try
        {
            var report = await _vault.CheckAndHealAsync(AppId);
            StatusText = $"Verified. Restored {report.Restored.Count + report.Refetched.Count}, kept modified {report.KeptDiverged.Count}, failed {report.Failed.Count}.";
            Refresh();
            if (report.HasNewDivergence)
                _ = ReviewAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Verify failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasVersion))]
    private void Delete()
    {
        var v = SelectedVersion;
        if (v.IsActive)
        {
            StatusText = "Switch to another version before deleting this one.";
            return;
        }
        foreach (var j in _vault.Queue.Jobs.Where(j => j.TargetVersionId == v.Id && !j.IsFinished))
            _vault.Queue.Cancel(j.Id);
        try
        {
            _vault.DeleteVersion(v.Id);
            StatusText = $"Deleted {v.Label ?? v.Id}. Files shared with other versions were kept.";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
        Refresh();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var added = await _appDialogs.ShowImportAsync(AppId);
        if (added == 0)
            return;
        Refresh();
        StatusText = $"Imported {added} manifest(s).";
    }

    [RelayCommand]
    private Task ReviewMutableAsync() => ReviewAsync();

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open {target}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _vault.Queue.JobChanged -= OnJobChanged;
        _vault.HealCompleted -= OnHealCompleted;
    }
}
