using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core.Persistence;

namespace DepotVault.App.ViewModels;

public sealed record TutorialStep(string Title, string Body, Bitmap Image)
{
    public bool HasImage => Image is not null;
}

public partial class TutorialViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    public TutorialViewModel(SettingsStore settings)
    {
        _settings = settings;
        dontShowAgain = settings.Current.TutorialDontShowAgain;
        Steps =
        [
            new("Find the depot on SteamDB",
                "Open steamdb.info, search for the game and open its page. Switch to the Depots tab and open the depot you want to roll back, usually the one holding the game content for your system. DepotVault lists the same depot IDs under Depots in the game's detail view.",
                LoadImage(1)),
            new("Open the Manifests tab",
                "On the depot page, open the Manifests tab. It lists every known manifest with the date it went live. SteamDB may ask you to sign in with Steam before it shows older entries.",
                LoadImage(2)),
            new("Copy the rows",
                "Select the rows of the manifest table, including the date and manifest ID columns, and copy them. If the page address is part of the copied text, DepotVault picks the depot automatically. A plain list of manifest IDs, one per line, works too.",
                LoadImage(3)),
            new("Paste into DepotVault",
                "In DepotVault, open the game and click \"Import manifests from SteamDB...\". Paste the rows, check the preview, untick anything you do not want and click Import. The manifests then appear under Manifest history, ready to download.",
                LoadImage(4)),
        ];
    }

    public IReadOnlyList<TutorialStep> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(StepText), nameof(IsLast), nameof(NextText))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private int index;

    [ObservableProperty]
    private bool dontShowAgain;

    public TutorialStep Current => Steps[Index];
    public string StepText => $"Step {Index + 1} of {Steps.Count}";
    public bool IsLast => Index == Steps.Count - 1;
    public string NextText => IsLast ? "Done" : "Next";

    public event Action CloseRequested;

    private bool CanBack() => Index > 0;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back() => Index--;

    [RelayCommand]
    private void Next()
    {
        if (IsLast)
            CloseRequested?.Invoke();
        else
            Index++;
    }

    public void Persist()
    {
        var s = _settings.Current;
        if (s.TutorialSeen && s.TutorialDontShowAgain == DontShowAgain)
            return;
        s.TutorialSeen = true;
        s.TutorialDontShowAgain = DontShowAgain;
        _settings.Save();
    }

    private static Bitmap LoadImage(int step)
    {
        var uri = new Uri($"avares://DepotVault/Assets/tutorial/step{step}.png");
        return AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
    }
}
