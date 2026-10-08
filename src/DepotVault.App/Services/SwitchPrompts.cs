using Avalonia.Threading;
using DepotVault.App.ViewModels;
using DepotVault.App.Views.Dialogs;
using DepotVault.Core;
using DepotVault.Core.SteamInstall;

namespace DepotVault.App;

public interface ISwitchPromptsFactory
{
    ISwitchPrompts Create();
}

public sealed class SwitchPrompts(Vault vault, DialogService dialogs) : ISwitchPromptsFactory, ISwitchPrompts
{
    public ISwitchPrompts Create() => this;

    public Task<bool> WaitForSteamExitAsync(CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var vm = new SteamRunningViewModel(() => vault.IsSteamRunning());
        await using var reg = ct.Register(() => Dispatcher.UIThread.Post(vm.Cancel));
        await dialogs.ShowAsync<object>(new SteamRunningDialog { DataContext = vm });
        return vm.Result;
    });

    public Task<CopyConsent> AskCopyAsync(int fileCount, long bytes, CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var vm = new CopyConsentViewModel(fileCount, bytes);
        await using var reg = ct.Register(() => Dispatcher.UIThread.Post(vm.Decline));
        await dialogs.ShowAsync<object>(new CopyConsentDialog { DataContext = vm });
        return new CopyConsent(vm.Allow, vm.Remember);
    });
}
