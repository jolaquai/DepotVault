using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DepotVault.App.ViewModels;

public partial class SteamRunningViewModel(Func<bool> isSteamRunning) : ObservableObject, IDialogViewModel
{
    private DispatcherTimer _timer;
    private bool _checking;

    public bool Result { get; private set; }

    [ObservableProperty]
    private string statusText = "Waiting for Steam to exit...";

    public event Action CloseRequested;

    public void OnOpened()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = CheckAsync(false));
        _timer.Start();
    }

    public void OnClosed() => _timer?.Stop();

    private async Task CheckAsync(bool manual)
    {
        if (_checking)
            return;
        _checking = true;
        try
        {
            if (!await Task.Run(isSteamRunning))
            {
                Result = true;
                CloseRequested?.Invoke();
            }
            else if (manual)
            {
                StatusText = $"Steam is still running (checked {DateTime.Now:T}).";
            }
        }
        finally
        {
            _checking = false;
        }
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(true);

    [RelayCommand]
    public void Cancel() => CloseRequested?.Invoke();
}
