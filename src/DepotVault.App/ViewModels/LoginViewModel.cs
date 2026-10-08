using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Steam;
using QRCoder;
using SteamKit2.Authentication;

namespace DepotVault.App.ViewModels;

public enum GuardMode
{
    None,
    EmailCode,
    DeviceCode,
    DeviceConfirm,
}

public partial class LoginViewModel(Vault vault) : ObservableObject, IGuardPrompt, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<string> _guardCode;
    private bool _qrStarted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string username;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string password;

    [ObservableProperty]
    private bool remember = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string errorText;

    [ObservableProperty]
    private Bitmap qrImage;

    [ObservableProperty]
    private string qrStatus = "Preparing QR code...";

    [ObservableProperty]
    private int selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuardVisible), nameof(IsGuardCodeVisible))]
    private GuardMode guard;

    [ObservableProperty]
    private string guardText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitGuardCommand))]
    private string guardCode;

    public bool IsGuardVisible => Guard != GuardMode.None;
    public bool IsGuardCodeVisible => Guard is GuardMode.EmailCode or GuardMode.DeviceCode;

    public event Action<bool> CloseRequested;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 0)
            _ = StartQrAsync();
    }

    public void OnOpened() => _ = StartQrAsync();

    private bool CanSignIn() => !IsBusy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            await vault.Session.LogOnWithCredentialsAsync(Username.Trim(), Password, Remember, this, _cts.Token);
            Password = null;
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException) { }
        catch (AuthenticationException ex)
        {
            ErrorText = ex.Result switch
            {
                SteamKit2.EResult.InvalidPassword => "Wrong username or password.",
                SteamKit2.EResult.RateLimitExceeded => "Too many attempts. Wait a while and try again.",
                _ => $"Sign-in failed: {ex.Result}",
            };
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Guard = GuardMode.None;
        }
    }

    public async Task StartQrAsync()
    {
        if (_qrStarted)
            return;
        _qrStarted = true;
        try
        {
            var qr = await vault.Session.BeginQrLogOnAsync(_cts.Token);
            QrImage = Render(qr.ChallengeUrl);
            QrStatus = "Scan with the Steam mobile app (Steam Guard > Scan a QR code).";
            qr.ChallengeUrlChanged += url => Ui(() => QrImage = Render(url));
            await vault.Session.CompleteQrLogOnAsync(qr, Remember, _cts.Token);
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            QrStatus = $"QR sign-in failed: {ex.Message}";
            _qrStarted = false;
        }
    }

    public static Bitmap Render(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var ms = new MemoryStream(png);
        return new Bitmap(ms);
    }

    private bool CanSubmitGuard() => !string.IsNullOrWhiteSpace(GuardCode);

    [RelayCommand(CanExecute = nameof(CanSubmitGuard))]
    private void SubmitGuard()
    {
        var code = GuardCode.Trim();
        GuardCode = null;
        _guardCode?.TrySetResult(code);
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts.Cancel();
        _guardCode?.TrySetCanceled();
        CloseRequested?.Invoke(false);
    }

    Task<string> IGuardPrompt.GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        AskCode(GuardMode.EmailCode, (previousCodeWasIncorrect ? "That code was wrong. " : "") + $"Enter the Steam Guard code sent to {email}.");

    Task<string> IGuardPrompt.GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        AskCode(GuardMode.DeviceCode, (previousCodeWasIncorrect ? "That code was wrong. " : "") + "Enter the code from your Steam mobile authenticator.");

    Task<bool> IGuardPrompt.AcceptDeviceConfirmationAsync()
    {
        Ui(() =>
        {
            Guard = GuardMode.DeviceConfirm;
            GuardText = "Approve this sign-in in the Steam mobile app.";
        });
        return Task.FromResult(true);
    }

    private Task<string> AskCode(GuardMode mode, string text)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _guardCode = tcs;
        Ui(() =>
        {
            Guard = mode;
            GuardText = text;
        });
        return tcs.Task;
    }

    private static void Ui(Action a)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            a();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(a);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
