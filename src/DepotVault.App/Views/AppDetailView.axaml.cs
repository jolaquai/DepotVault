using Avalonia.Controls;
using DepotVault.App.ViewModels;

namespace DepotVault.App.Views;

public partial class AppDetailView : UserControl
{
    public AppDetailView()
    {
        InitializeComponent();
        HistoryGrid.SelectionChanged += (_, _) => (DataContext as AppDetailViewModel)?.SetHistorySelection(HistoryGrid.SelectedItems.OfType<HistoryItemViewModel>());
    }
}
