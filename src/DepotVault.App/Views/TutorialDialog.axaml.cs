using Avalonia.Controls;
using DepotVault.App.ViewModels;

namespace DepotVault.App.Views;

public partial class TutorialDialog : Window
{
    public TutorialDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TutorialViewModel vm)
            vm.CloseRequested += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as TutorialViewModel)?.Persist();
    }
}
