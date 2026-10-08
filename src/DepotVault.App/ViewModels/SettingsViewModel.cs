using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Library;
using DepotVault.Core.Persistence;

namespace DepotVault.App.ViewModels;

public sealed record RootItem(string Path, string Info, int Versions);

public partial class SettingsViewModel : PageViewModel
{
    private const decimal MiB = 1 << 20;

    private readonly Vault _vault;
    private readonly DialogService _dialogs;
    private bool _loading;

    public SettingsViewModel(Vault vault, DialogService dialogs) : base("Settings")
    {
        _vault = vault;
        _dialogs = dialogs;
        Load();
    }

    private Settings S => _vault.Settings.Current;

    public ObservableCollection<RootItem> Roots { get; } = [];
    public ObservableCollection<RootItem> SuggestedRoots { get; } = [];
    public IReadOnlyList<string> CopyFallbackOptions { get; } = ["Ask every time", "Never copy", "Always copy"];
    public IReadOnlyList<string> ThemeOptions { get; } = ["Follow system", "Light", "Dark"];
    public bool HasSuggestions => SuggestedRoots.Count > 0;
    public bool HasNoRoots => Roots.Count == 0;

    [ObservableProperty]
    private decimal maxConcurrentJobs;

    [ObservableProperty]
    private decimal maxConcurrentChunks;

    [ObservableProperty]
    private decimal bandwidthLimitMiB;

    [ObservableProperty]
    private bool dedupeEnabled;

    [ObservableProperty]
    private string exclusionText;

    [ObservableProperty]
    private bool readOnlyProtection;

    [ObservableProperty]
    private int copyFallbackIndex;

    [ObservableProperty]
    private bool acfLock;

    [ObservableProperty]
    private bool integrityCheckOnStartup;

    [ObservableProperty]
    private bool showTutorial;

    [ObservableProperty]
    private int themeIndex;

    [ObservableProperty]
    private string statusText;

    public override void OnActivated() => Load();

    private void Load()
    {
        _loading = true;
        try
        {
            MaxConcurrentJobs = S.MaxConcurrentJobs;
            MaxConcurrentChunks = S.MaxConcurrentChunks;
            BandwidthLimitMiB = Math.Round(S.BandwidthLimitBytesPerSecond / MiB, 1);
            DedupeEnabled = S.DedupeEnabled;
            ExclusionText = string.Join(Environment.NewLine, S.GlobalExclusionGlobs);
            ReadOnlyProtection = S.ReadOnlyProtection;
            CopyFallbackIndex = (int)S.CopyFallback;
            AcfLock = S.AcfLock;
            IntegrityCheckOnStartup = S.IntegrityCheckOnStartup;
            ShowTutorial = !S.TutorialDontShowAgain;
            ThemeIndex = (int)S.Theme;
        }
        finally
        {
            _loading = false;
        }
        LoadRoots();
    }

    private void LoadRoots()
    {
        Roots.Clear();
        foreach (var r in S.LibraryRoots)
        {
            var n = _vault.CountVersionsIn(r);
            var exists = Directory.Exists(r);
            var info = n == 1 ? "1 version" : $"{n} versions";
            if (!exists)
                info += ", folder will be created on first download";
            else if (FreeSpace(r) is var free and >= 0)
                info += $", {Format.Bytes(free)} free";
            Roots.Add(new RootItem(r, info, n));
        }
        SuggestedRoots.Clear();
        var libs = _vault.Locator?.GetLibraries().Select(l => l.Path) ?? [];
        foreach (var r in LibraryRoots.Suggest(libs, S.LibraryRoots))
            SuggestedRoots.Add(new RootItem(r, "Next to a Steam library", 0));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(HasNoRoots));
    }

    private static long FreeSpace(string path)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void Save(Action<Settings> apply)
    {
        if (_loading)
            return;
        apply(S);
        _vault.Settings.Save();
    }

    partial void OnMaxConcurrentJobsChanged(decimal value) => Save(s => s.MaxConcurrentJobs = (int)value);
    partial void OnMaxConcurrentChunksChanged(decimal value) => Save(s => s.MaxConcurrentChunks = (int)value);
    partial void OnBandwidthLimitMiBChanged(decimal value) => Save(s => s.BandwidthLimitBytesPerSecond = (long)(Math.Max(0, value) * MiB));
    partial void OnDedupeEnabledChanged(bool value) => Save(s => s.DedupeEnabled = value);
    partial void OnCopyFallbackIndexChanged(int value) => Save(s => s.CopyFallback = (CopyFallback)Math.Clamp(value, 0, 2));
    partial void OnIntegrityCheckOnStartupChanged(bool value) => Save(s => s.IntegrityCheckOnStartup = value);
    partial void OnShowTutorialChanged(bool value) => Save(s => s.TutorialDontShowAgain = !value);
    partial void OnThemeIndexChanged(int value) => Save(s => s.Theme = (AppTheme)Math.Clamp(value, 0, 2));

    partial void OnExclusionTextChanged(string value) => Save(s =>
    {
        s.GlobalExclusionGlobs.Clear();
        if (string.IsNullOrEmpty(value))
            return;
        foreach (var line in value.AsSpan().EnumerateLines())
        {
            var t = line.Trim();
            if (!t.IsEmpty && !s.GlobalExclusionGlobs.Contains(t.ToString()))
                s.GlobalExclusionGlobs.Add(t.ToString());
        }
    });

    partial void OnReadOnlyProtectionChanged(bool value)
    {
        if (_loading)
            return;
        Save(s => s.ReadOnlyProtection = value);
        _ = RunAsync(_vault.ApplyReadOnlyProtection, n => value ? $"Protected {n} shared file(s)." : $"Unprotected {n} file(s).");
    }

    partial void OnAcfLockChanged(bool value)
    {
        if (_loading)
            return;
        Save(s => s.AcfLock = value);
        _ = RunAsync(_vault.ApplyAcfLock, n => n == 0 ? null : value ? $"Locked {n} Steam app manifest(s)." : $"Unlocked {n} Steam app manifest(s).");
    }

    private async Task RunAsync(Func<int> work, Func<int, string> message)
    {
        try
        {
            var n = await Task.Run(work);
            StatusText = message(n);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddRootAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose a library folder");
        if (string.IsNullOrEmpty(folder))
            return;
        AddRoot(folder);
    }

    [RelayCommand]
    private void AddSuggested(RootItem item) => AddRoot(item.Path);

    private void AddRoot(string path)
    {
        _vault.EnsureRoot(path);
        StatusText = null;
        LoadRoots();
    }

    [RelayCommand]
    private void RemoveRoot(RootItem item)
    {
        StatusText = _vault.RemoveRoot(item.Path) ? null : $"{item.Path} still holds {item.Versions} version(s). Delete them first.";
        LoadRoots();
    }
}
