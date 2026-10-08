using CommunityToolkit.Mvvm.Input;

namespace DepotVault.App.ViewModels;

public partial class HelpViewModel(AppDialogs dialogs) : PageViewModel("Help")
{
    [RelayCommand]
    private Task ShowTutorialAsync() => dialogs.ShowTutorialAsync();
}
