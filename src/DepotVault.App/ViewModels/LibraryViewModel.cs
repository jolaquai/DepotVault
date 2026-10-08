using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace DepotVault.App.ViewModels;

public sealed record InstalledAppChoice(uint AppId, string Name)
{
    public override string ToString() => $"{Name} ({AppId})";
}

public partial class AppItemViewModel(uint appId) : ObservableObject
{
    public uint AppId { get; } = appId;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string activeVersion;

    [ObservableProperty]
    private int versionCount;

    [ObservableProperty]
    private string sizeText = "...";
}

public partial class LibraryViewModel : PageViewModel
{
    private readonly Vault _vault;
    private readonly IServiceProvider _services;
    private readonly AppDialogs _dialogs;
    private bool _refreshQueued;

    public LibraryViewModel(Vault vault, IServiceProvider services, AppDialogs dialogs) : base("Library")
    {
        _vault = vault;
        _services = services;
        _dialogs = dialogs;
        _vault.Library.Changed += () =>
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            Ui(() =>
            {
                _refreshQueued = false;
                Refresh();
            });
        };
    }

    public ObservableCollection<AppItemViewModel> Apps { get; } = [];
    public ObservableCollection<InstalledAppChoice> InstalledApps { get; } = [];

    [ObservableProperty]
    private AppItemViewModel selectedApp;

    [ObservableProperty]
    private AppDetailViewModel detail;

    [ObservableProperty]
    private InstalledAppChoice selectedInstalled;

    [ObservableProperty]
    private string addAppId;

    [ObservableProperty]
    private string statusText;

    public bool IsEmpty => Apps.Count == 0;

    public override void OnActivated()
    {
        Refresh();
        if (InstalledApps.Count == 0 && _vault.Locator is not null)
        {
            _ = Task.Run(() => _vault.Locator.EnumerateInstalled().Select(a => new InstalledAppChoice(a.AppId, a.Acf.Name ?? a.AppId.ToString())).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList())
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        Ui(() => { foreach (var a in t.Result) InstalledApps.Add(a); });
                }, TaskScheduler.Default);
        }
        Detail?.OnActivated();
    }

    public void Refresh()
    {
        var apps = _vault.Library.Apps;
        var keep = SelectedApp?.AppId;
        for (var i = Apps.Count - 1; i >= 0; i--)
        {
            if (!apps.Any(a => a.AppId == Apps[i].AppId))
                Apps.RemoveAt(i);
        }
        foreach (var a in apps.OrderBy(a => a.Name ?? a.AppId.ToString(), StringComparer.CurrentCultureIgnoreCase))
        {
            var item = Apps.FirstOrDefault(x => x.AppId == a.AppId);
            if (item is null)
                Apps.Add(item = new AppItemViewModel(a.AppId));
            var versions = _vault.Library.VersionsFor(a.AppId);
            item.Name = a.Name ?? _vault.Apps.Get(a.AppId).Name ?? $"App {a.AppId}";
            item.VersionCount = versions.Count;
            var active = versions.FirstOrDefault(v => v.Id == a.ActiveVersionId);
            item.ActiveVersion = active is null ? "Steam default" : active.Label ?? Format.Date(active.ManifestDateUtc == default ? active.CreatedUtc : active.ManifestDateUtc);
            _ = UpdateSizeAsync(item);
        }
        OnPropertyChanged(nameof(IsEmpty));
        if (keep is { } id && SelectedApp is null)
            SelectedApp = Apps.FirstOrDefault(a => a.AppId == id);
        SelectedApp ??= Apps.FirstOrDefault();
        Detail?.Refresh();
    }

    private async Task UpdateSizeAsync(AppItemViewModel item)
    {
        var (total, unique) = await Task.Run(() =>
        {
            long t = 0, u = 0;
            foreach (var v in _vault.Library.VersionsFor(item.AppId))
            {
                t += Core.Library.LibraryIndex.ComputeTotalSize(_vault.Library.GetVersionDir(v));
                u += _vault.Library.ComputeUniqueSize(v, _vault.Strategy);
            }
            return (t, u);
        });
        item.SizeText = $"{Format.Bytes(total)} logical, {Format.Bytes(unique)} on disk";
    }

    partial void OnSelectedAppChanged(AppItemViewModel value)
    {
        Detail?.Dispose();
        Detail = value is null ? null : ActivatorUtilities.CreateInstance<AppDetailViewModel>(_services, value.AppId);
        if (Detail is not { } d)
            return;
        d.ImportRequested += () => _ = ImportAsync(d);
        d.SwitchFailed += r => _ = _dialogs.ShowSwitchReportAsync(r);
        d.OnActivated();
    }

    private async Task ImportAsync(AppDetailViewModel d)
    {
        var added = await _dialogs.ShowImportAsync(d.AppId);
        if (added == 0)
            return;
        d.Refresh();
        d.StatusText = $"Imported {added} manifest(s).";
    }

    [RelayCommand]
    private Task ShowTutorialAsync() => _dialogs.ShowTutorialAsync();

    [RelayCommand]
    private async Task AddAppAsync()
    {
        uint id;
        string name = null;
        if (SelectedInstalled is { } chosen && string.IsNullOrWhiteSpace(AddAppId))
        {
            id = chosen.AppId;
            name = chosen.Name;
        }
        else if (!uint.TryParse(AddAppId?.Trim(), out id) || id == 0)
        {
            StatusText = "Enter a numeric App ID or pick an installed game.";
            return;
        }
        _vault.Library.EnsureApp(id, name);
        AddAppId = null;
        SelectedInstalled = null;
        Refresh();
        SelectedApp = Apps.FirstOrDefault(a => a.AppId == id);
        if (_vault.Session.State == SessionState.LoggedOn && Detail is not null)
            await Detail.RefreshMetadataCommand.ExecuteAsync(null);
        StatusText = null;
    }
}
