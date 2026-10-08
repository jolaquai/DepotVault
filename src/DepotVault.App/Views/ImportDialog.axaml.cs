using Avalonia.Controls;
using DepotVault.App.ViewModels;

namespace DepotVault.App.Views;

public partial class ImportDialog : Window
{
    public ImportDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ImportViewModel vm)
            vm.CloseRequested += ok => Close(ok);
    }
}
