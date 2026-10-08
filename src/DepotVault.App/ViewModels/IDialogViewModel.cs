namespace DepotVault.App.ViewModels;

public interface IDialogViewModel
{
    event Action CloseRequested;
    void OnOpened();
    void OnClosed();
}
