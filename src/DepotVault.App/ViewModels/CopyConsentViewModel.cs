using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DepotVault.App.ViewModels;

public partial class CopyConsentViewModel(int fileCount, long bytes) : ObservableObject, IDialogViewModel
{
    public string Message { get; } = $"{fileCount} file(s) ({Format.Bytes(bytes)}) cannot be linked into the Steam install on this drive. DepotVault can copy them instead, which takes that much extra disk space.";

    public bool Allow { get; private set; }

    [ObservableProperty]
    private bool remember;

    public event Action CloseRequested;

    public void OnOpened() { }

    public void OnClosed() { }

    [RelayCommand]
    private void Copy()
    {
        Allow = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    public void Decline()
    {
        Allow = false;
        CloseRequested?.Invoke();
    }
}
