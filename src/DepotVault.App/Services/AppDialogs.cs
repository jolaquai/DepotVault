using Avalonia.Controls;
using DepotVault.App.ViewModels;
using DepotVault.App.Views;
using DepotVault.App.Views.Dialogs;
using DepotVault.Core;
using DepotVault.Core.SteamInstall;

namespace DepotVault.App;

public sealed record ImportOutcome(int Added, IReadOnlyList<(uint DepotId, ulong ManifestId)> Remove);

public sealed class AppDialogs(Vault vault, DialogService dialogs)
{
    internal Window ActiveImport { get; private set; }

    public async Task<ImportOutcome> ShowImportAsync(uint appId)
    {
        if (ActiveImport is not null)
        {
            ActiveImport.Activate();
            return new ImportOutcome(0, []);
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
        return new ImportOutcome(vm.Added, vm.ToRemove);
    }

    public async Task<bool> ShowMutableReviewAsync(uint appId)
    {
        var vm = new MutableFilesViewModel(appId, vault);
        await dialogs.ShowAsync<object>(new MutableFilesDialog { DataContext = vm });
        return vm.Saved;
    }

    public async Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string> details, string confirmText)
    {
        var vm = new ConfirmViewModel(title, message, details, confirmText);
        await dialogs.ShowAsync<object>(new ConfirmDialog { DataContext = vm });
        return vm.Confirmed;
    }

    public Task ShowSwitchReportAsync(SwitchReport report) => dialogs.ShowAsync<object>(new SwitchReportDialog { DataContext = new SwitchReportViewModel(report, dialogs) });

    public Task ShowTutorialAsync(Window owner = null) => dialogs.ShowAsync<object>(new TutorialDialog { DataContext = new TutorialViewModel(vault.Settings) }, owner);
}
