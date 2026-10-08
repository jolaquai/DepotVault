using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Import;
using DepotVault.Core.Library;

namespace DepotVault.App.ViewModels;

public sealed record DepotChoice(uint DepotId, string Name)
{
    public override string ToString() => string.IsNullOrEmpty(Name) ? $"Depot {DepotId}" : $"{Name} ({DepotId})";
}

public partial class ImportRowViewModel(ImportRow row, Action changed) : ObservableObject
{
    public ImportRow Row { get; } = row;
    public int Line => Row.Line;
    public string Date => Row.Status == ImportStatus.Invalid ? "" : Format.Date(Row.DateUtc);
    public string ManifestId => Row.ManifestId == 0 ? "" : Row.ManifestId.ToString(CultureInfo.InvariantCulture);
    public string Raw => Row.Raw;
    public string Branch => Row.Branch ?? "";
    public bool CanInclude => Row.Status == ImportStatus.New;

    public string Status => Row.Status switch
    {
        ImportStatus.New => "New",
        ImportStatus.Duplicate => "Already imported",
        _ => "Not recognized",
    };

    [ObservableProperty]
    private bool include = row.Status == ImportStatus.New;

    partial void OnIncludeChanged(bool value) => changed();
}

public partial class ImportViewModel : ObservableObject
{
    private readonly Vault _vault;
    private readonly DialogService _dialogs;
    private readonly AppRecord _app;

    public ImportViewModel(uint appId, Vault vault, DialogService dialogs)
    {
        _vault = vault;
        _dialogs = dialogs;
        _app = vault.Apps.Get(appId);
        AppId = appId;
        AppName = _app.Name ?? vault.Library.GetApp(appId)?.Name ?? $"App {appId}";
        foreach (var d in _app.Depots.Where(d => d.IsDownloadable || d.DlcAppId != 0))
            Depots.Add(new DepotChoice(d.DepotId, d.Name));
        var preselect = _app.SelectedDepots is { Count: 1 } sel ? sel[0] : Depots.Count == 1 ? Depots[0].DepotId : 0;
        if (preselect != 0)
            DepotIdText = preselect.ToString(CultureInfo.InvariantCulture);
    }

    public uint AppId { get; }
    public string AppName { get; }
    public ObservableCollection<DepotChoice> Depots { get; } = [];
    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public int Added { get; private set; }
    public IReadOnlyList<(uint DepotId, ulong ManifestId)> ToRemove { get; private set; } = [];

    [ObservableProperty]
    private bool keepOnly;

    partial void OnKeepOnlyChanged(bool value) => UpdateSummary();

    [ObservableProperty]
    private string pasteText;

    [ObservableProperty]
    private DepotChoice selectedDepot;

    [ObservableProperty]
    private string depotIdText;

    [ObservableProperty]
    private string summary;

    [ObservableProperty]
    private string warningText;

    public event Action<bool> CloseRequested;
    public event Action TutorialRequested;

    private uint DepotId => uint.TryParse(DepotIdText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    partial void OnPasteTextChanged(string value)
    {
        var detected = string.IsNullOrEmpty(value) ? 0 : SteamDbParser.FindDepotId(value);
        if (detected != 0 && detected != DepotId)
            DepotIdText = detected.ToString(CultureInfo.InvariantCulture);
        else
            Reparse();
    }

    partial void OnSelectedDepotChanged(DepotChoice value)
    {
        if (value is not null)
            DepotIdText = value.DepotId.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnDepotIdTextChanged(string value)
    {
        var id = DepotId;
        SelectedDepot = Depots.FirstOrDefault(d => d.DepotId == id);
        Reparse();
    }

    private void Reparse()
    {
        Rows.Clear();
        if (!string.IsNullOrWhiteSpace(PasteText))
        {
            var depot = DepotId;
            var result = SteamDbParser.Parse(PasteText, _app.History.Where(h => h.DepotId == depot).Select(h => h.ManifestId));
            foreach (var r in result.Rows)
                Rows.Add(new ImportRowViewModel(r, UpdateSummary));
        }
        OnPropertyChanged(nameof(HasRows));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int fresh = 0, included = 0, dup = 0, invalid = 0;
        foreach (var r in Rows)
        {
            switch (r.Row.Status)
            {
                case ImportStatus.New:
                    fresh++;
                    if (r.Include)
                        included++;
                    break;
                case ImportStatus.Duplicate:
                    dup++;
                    break;
                default:
                    invalid++;
                    break;
            }
        }
        var depot = DepotId;
        var removing = ComputeRemovals().Count;
        Summary = Rows.Count == 0 ? null : $"{included} of {fresh} new manifest(s) selected, {dup} already imported, {invalid} line(s) not recognized."
            + (KeepOnly && removing > 0 ? $" {removing} other imported manifest(s) of depot {depot} will be removed, together with their downloads." : "");
        var warnings = new List<string>(2);
        if (Rows.Count > 0 && depot == 0)
            warnings.Add("Pick the depot these manifests belong to (or paste the SteamDB page URL along with the rows).");
        else if (Rows.Count > 0 && Depots.Count > 0 && SelectedDepot is null)
            warnings.Add($"Depot {depot} is not in this game's depot list. Check that you copied the right depot.");
        var branches = Rows.Where(r => r.Row.Branch is not null && (r.Row.Status == ImportStatus.Duplicate || (r.CanInclude && r.Include))).Select(r => r.Row.Branch).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (branches.Count > 1)
            warnings.Add($"This paste mixes manifests from different branches ({string.Join(", ", branches)}). Builds of different branches usually don't belong together; untick the rows you don't want or paste one branch at a time.");
        WarningText = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
        ImportCommand.NotifyCanExecuteChanged();
    }

    private List<(uint DepotId, ulong ManifestId)> ComputeRemovals()
    {
        var depot = DepotId;
        if (!KeepOnly || depot == 0 || Rows.Count == 0)
            return [];
        var keep = Rows.Where(r => r.Row.Status == ImportStatus.Duplicate || (r.CanInclude && r.Include)).Select(r => r.Row.ManifestId).ToHashSet();
        return _app.History.Where(h => h.DepotId == depot && !keep.Contains(h.ManifestId)).Select(h => (h.DepotId, h.ManifestId)).ToList();
    }

    private bool CanImport() => DepotId != 0 && (Rows.Any(r => r.Include && r.CanInclude) || ComputeRemovals().Count > 0);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        ToRemove = ComputeRemovals();
        Added = SteamDbParser.Commit(_app, DepotId, Rows.Where(r => r.Include).Select(r => r.Row));
        _vault.Apps.Save(_app);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private async Task PasteFromClipboardAsync()
    {
        var text = await _dialogs.GetClipboardTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
            PasteText = text;
    }

    [RelayCommand]
    private void ShowTutorial() => TutorialRequested?.Invoke();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
