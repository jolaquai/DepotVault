using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Steam;
using Microsoft.Extensions.Logging;

namespace DepotVault.App.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly Vault _vault;
    private readonly ILogger<ShellViewModel> _log;

    public ShellViewModel(Vault vault, ILogger<ShellViewModel> log, LibraryViewModel library, DownloadsViewModel downloads, SettingsViewModel settings, HelpViewModel help)
    {
        _vault = vault;
        _log = log;
        Library = library;
        Pages = [library, downloads, settings, help];
        currentPage = library;
    }

    public IReadOnlyList<PageViewModel> Pages { get; }
    public LibraryViewModel Library { get; }

    [ObservableProperty]
    private PageViewModel currentPage;

    [ObservableProperty]
    private string accountText = "Not signed in";

    [ObservableProperty]
    private bool isSignedIn;

    [ObservableProperty]
    private bool isBusy;

    public event Action SignInRequested;

    partial void OnCurrentPageChanged(PageViewModel value) => value?.OnActivated();

    public void Initialize()
    {
        _vault.Session.StateChanged += _ => Dispatcher.UIThread.Post(UpdateAccount);
        _vault.Session.SessionLost += r => Dispatcher.UIThread.Post(() =>
        {
            AccountText = $"Session expired ({r})";
            SignInRequested?.Invoke();
        });
        CurrentPage?.OnActivated();
        _ = AutoSignInAsync();
    }

    private async Task AutoSignInAsync()
    {
        if (!_vault.Session.HasSavedToken)
        {
            UpdateAccount();
            return;
        }
        IsBusy = true;
        AccountText = "Signing in...";
        try
        {
            if (!await _vault.Session.TryLogOnWithSavedTokenAsync())
                SignInRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Automatic sign-in failed");
            AccountText = "Offline";
        }
        finally
        {
            IsBusy = false;
            UpdateAccount();
        }
    }

    private void UpdateAccount()
    {
        IsSignedIn = _vault.Session.State == SessionState.LoggedOn;
        AccountText = _vault.Session.State switch
        {
            SessionState.LoggedOn => _vault.Session.AccountName,
            SessionState.LoggingOn or SessionState.Connecting => "Signing in...",
            _ => IsBusy ? AccountText : "Not signed in",
        };
    }

    [RelayCommand]
    private void SignIn() => SignInRequested?.Invoke();

    [RelayCommand]
    private void SignOut()
    {
        _vault.Session.LogOff(forget: true);
        UpdateAccount();
    }
}
