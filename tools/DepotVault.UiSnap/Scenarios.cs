using Avalonia.Controls;
using DepotVault.App.ViewModels;
using DepotVault.App.Views;
using DepotVault.Core;
using DepotVault.Core.Steam;
using Microsoft.Extensions.DependencyInjection;

internal static class Scenarios
{
    public static void Run(string name, IServiceProvider services, ShellViewModel shell, Window window, Action<TopLevel, string> save, Action pump)
    {
        var vault = services.GetRequiredService<Vault>();
        switch (name)
        {
            case "login":
            {
                var vm = new LoginViewModel(vault) { Username = "someaccount" };
                var dlg = new LoginDialog { DataContext = vm };
                dlg.Show();
                save(dlg, "login-account");
                ((IGuardPrompt)vm).AcceptDeviceConfirmationAsync();
                save(dlg, "login-guard-confirm");
                ((IGuardPrompt)vm).GetEmailCodeAsync("j***@example.com", false);
                save(dlg, "login-guard-email");
                vm.Guard = GuardMode.None;
                vm.SelectedTab = 1;
                Wait(() => vm.QrImage is not null || vm.QrStatus.Contains("failed"), pump, 30);
                save(dlg, "login-qr");
                Console.WriteLine($"QR status: {vm.QrStatus}");
                dlg.Close();
                break;
            }
            case "autologin":
            {
                shell.Initialize();
                Wait(() => shell.IsSignedIn || (!shell.IsBusy && !vault.Session.HasSavedToken), pump, 30);
                Console.WriteLine($"Session: {vault.Session.State}, account indicator: {shell.AccountText}");
                save(window, "autologin");
                break;
            }
            default:
                throw new ArgumentException($"Unknown scenario {name}");
        }
    }

    public static void Wait(Func<bool> condition, Action pump, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < end)
            pump();
        pump();
    }
}
