using CommunityToolkit.Mvvm.Input;

namespace DepotVault.App.ViewModels;

public sealed partial class ConfirmViewModel(string title, string message, IReadOnlyList<string> details, string confirmText) : IDialogViewModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public IReadOnlyList<string> Details { get; } = details ?? [];
    public bool HasDetails => Details.Count > 0;
    public string ConfirmText { get; } = confirmText;
    public bool Confirmed { get; private set; }

    public event Action CloseRequested;

    public void OnOpened() { }

    public void OnClosed() { }

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
