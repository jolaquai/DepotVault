using Avalonia.Controls;
using DepotVault.App.ViewModels;
using DepotVault.App.Views;
using DepotVault.Core;

namespace DepotVault.App;

public sealed class AppDialogs(Vault vault, DialogService dialogs)
{
    internal Window ActiveImport { get; private set; }

    public async Task<int> ShowImportAsync(uint appId)
    {
        if (ActiveImport is not null)
        {
            ActiveImport.Activate();
            return 0;
        }
        var vm = new ImportViewModel(appId, vault, dialogs);
        var dlg = new ImportDialog { DataContext = vm };
        vm.TutorialRequested += () => _ = ShowTutorialAsync(dlg);
        if (!vault.Settings.Current.TutorialDontShowAgain)
            dlg.Opened += (_, _) => _ = ShowTutorialAsync(dlg);
        ActiveImport = dlg;
        try
        {
            await dialogs.ShowAsync<bool>(dlg);
        }
        finally
        {
            ActiveImport = null;
        }
        return vm.Added;
    }

    public Task ShowTutorialAsync(Window owner = null) => dialogs.ShowAsync<object>(new TutorialDialog { DataContext = new TutorialViewModel(vault.Settings) }, owner);
}
