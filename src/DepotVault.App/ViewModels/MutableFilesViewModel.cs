using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Library;

namespace DepotVault.App.ViewModels;

public partial class MutableFileItemViewModel(MutableCandidate candidate) : ObservableObject
{
    public string RelPath => candidate.RelPath;
    public string Size => candidate.Size > 0 ? Format.Bytes(candidate.Size) : "";
    public string Reasons { get; } = Describe(candidate.Reasons);

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private int decisionIndex = (int)candidate.Decision;

    public MutableDecision Decision
    {
        get => (MutableDecision)DecisionIndex;
        set => DecisionIndex = (int)value;
    }

    private static string Describe(MutableReason r)
    {
        var parts = new List<string>(5);
        if ((r & MutableReason.ModifiedSinceLink) != 0)
            parts.Add("changed since linked");
        if ((r & MutableReason.ReportedBySelfHeal) != 0)
            parts.Add("changed in the library");
        if ((r & MutableReason.SteamUserConfig) != 0)
            parts.Add("Steam marks it as user config");
        if ((r & MutableReason.MutableDirectory) != 0)
            parts.Add("in a save/config/cache folder");
        if ((r & MutableReason.ConfigExtension) != 0)
            parts.Add("config-like file type");
        return string.Join(", ", parts);
    }
}

public partial class MutableRuleItemViewModel(string pattern, MutableDecision decision) : ObservableObject
{
    public string Pattern { get; } = pattern;
    public MutableDecision Decision { get; } = decision;
    public string DecisionText => Decision == MutableDecision.Share ? "Share" : "Isolate";
}

public partial class MutableFilesViewModel : ObservableObject, IDialogViewModel
{
    private readonly Vault _vault;
    private readonly AppRecord _app;
    private List<MutableFileItemViewModel> _all = [];

    public MutableFilesViewModel(uint appId, Vault vault)
    {
        _vault = vault;
        _app = vault.Apps.Get(appId);
        AppName = _app.Name ?? vault.Library.GetApp(appId)?.Name ?? $"App {appId}";
        foreach (var r in _app.MutableRules ?? [])
        {
            if (IsGlob(r.Pattern))
                Patterns.Add(new MutableRuleItemViewModel(r.Pattern, r.Decision));
        }
    }

    public string AppName { get; }
    public ObservableCollection<MutableFileItemViewModel> Items { get; } = [];
    public ObservableCollection<MutableRuleItemViewModel> Patterns { get; } = [];
    public IReadOnlyList<string> DecisionOptions { get; } = ["Not decided", "Share", "Isolate"];
    public IReadOnlyList<string> PatternDecisionOptions { get; } = ["Share", "Isolate"];
    public bool Saved { get; private set; }
    public bool HasPatterns => Patterns.Count > 0;

    [ObservableProperty]
    private bool isLoading = true;

    [ObservableProperty]
    private string filterText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPatternCommand))]
    private string newPattern;

    [ObservableProperty]
    private int newPatternDecisionIndex;

    [ObservableProperty]
    private string summary;

    public event Action CloseRequested;

    public async void OnOpened()
    {
        var app = _app;
        var library = _vault.Library;
        var integrity = _vault.Integrity;
        try
        {
            var candidates = await Task.Run(() =>
            {
                var modified = integrity.ScanApp(app.AppId).Select(i => i.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return MutableFileScanner.ScanApp(library, app, modified.Contains);
            });
            _all = candidates.Select(c => new MutableFileItemViewModel(c)).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Summary = $"Could not scan the game's files: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OnClosed() { }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        var f = FilterText?.Trim();
        var glob = !string.IsNullOrEmpty(f) && IsGlob(f);
        foreach (var i in _all)
        {
            if (string.IsNullOrEmpty(f) || (glob ? PathGlob.IsMatch(f, i.RelPath) : i.RelPath.Contains(f, StringComparison.OrdinalIgnoreCase)))
                Items.Add(i);
        }
        Summary = _all.Count == 0 ? "No files in this game look like they change while playing." : $"{_all.Count} file(s) might change while playing; {Items.Count} shown.";
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items)
            i.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in _all)
            i.IsSelected = false;
    }

    [RelayCommand]
    private void ShareSelected() => SetSelected(MutableDecision.Share);

    [RelayCommand]
    private void IsolateSelected() => SetSelected(MutableDecision.Isolate);

    [RelayCommand]
    private void ClearSelected() => SetSelected(MutableDecision.Unreviewed);

    private void SetSelected(MutableDecision d)
    {
        foreach (var i in _all)
        {
            if (i.IsSelected)
                i.Decision = d;
        }
    }

    private bool CanAddPattern() => !string.IsNullOrWhiteSpace(NewPattern);

    [RelayCommand(CanExecute = nameof(CanAddPattern))]
    private void AddPattern()
    {
        var pattern = NewPattern.Trim();
        var decision = NewPatternDecisionIndex == 0 ? MutableDecision.Share : MutableDecision.Isolate;
        for (var i = Patterns.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Patterns[i].Pattern, pattern, StringComparison.OrdinalIgnoreCase))
                Patterns.RemoveAt(i);
        }
        Patterns.Add(new MutableRuleItemViewModel(pattern, decision));
        foreach (var i in _all)
        {
            if (PathGlob.IsMatch(pattern, i.RelPath) || string.Equals(pattern.Replace('\\', '/'), i.RelPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                i.Decision = decision;
        }
        NewPattern = null;
        OnPropertyChanged(nameof(HasPatterns));
    }

    [RelayCommand]
    private void RemovePattern(MutableRuleItemViewModel rule)
    {
        Patterns.Remove(rule);
        OnPropertyChanged(nameof(HasPatterns));
    }

    [RelayCommand]
    private void Save()
    {
        var patterns = Patterns.Select(p => new MutableRule { Pattern = p.Pattern, Decision = p.Decision }).ToList();
        var probe = new AppRecord { MutableRules = patterns };
        var listed = _all.Select(i => i.RelPath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rules = (_app.MutableRules ?? []).Where(r => !IsGlob(r.Pattern) && !listed.Contains(r.Pattern.Replace('\\', '/'))).ToList();
        rules.AddRange(patterns);
        foreach (var i in _all)
        {
            if (i.Decision != MutableDecision.Unreviewed && i.Decision != probe.GetDecision(i.RelPath))
                rules.Add(new MutableRule { Pattern = i.RelPath, Decision = i.Decision });
        }
        _app.MutableRules = rules;
        _app.ReviewCandidates?.Clear();
        _app.MutableReviewed = true;
        _vault.Apps.Save(_app);
        if (_vault.ReadOnly.Enabled)
            _ = Task.Run(() => _vault.ReadOnly.ApplyApp(_app.AppId));
        Saved = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private static bool IsGlob(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') >= 0;
}
