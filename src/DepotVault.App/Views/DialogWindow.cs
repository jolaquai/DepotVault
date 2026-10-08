using Avalonia.Controls;
using DepotVault.App.ViewModels;

namespace DepotVault.App.Views;

public class DialogWindow : Window
{
    protected override Type StyleKeyOverride => typeof(Window);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is IDialogViewModel vm)
            vm.CloseRequested += Close;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (DataContext as IDialogViewModel)?.OnOpened();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDialogViewModel)?.OnClosed();
    }
}
