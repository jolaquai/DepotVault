using Avalonia.Controls;
using DepotVault.App.ViewModels;

namespace DepotVault.App.Views;

public partial class LoginDialog : Window
{
    public LoginDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LoginViewModel vm)
            vm.CloseRequested += ok => Close(ok);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (DataContext as LoginViewModel)?.OnOpened();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose();
    }
}
