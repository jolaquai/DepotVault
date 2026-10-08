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
                Wait(() => vm.QrImage is not null || vm.QrStatus.Contains("failed"), pump, 30);
                save(dlg, "login-qr");
                Console.WriteLine($"QR status: {vm.QrStatus}");
                vm.SelectedTab = 1;
                save(dlg, "login-account");
                ((IGuardPrompt)vm).AcceptDeviceConfirmationAsync();
                save(dlg, "login-guard-confirm");
                ((IGuardPrompt)vm).GetEmailCodeAsync("j***@example.com", false);
                save(dlg, "login-guard-email");
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
            case "library-actions":
            {
                var lib = services.GetRequiredService<LibraryViewModel>();
                shell.CurrentPage = lib;
                lib.OnActivated();
                pump();
                var d = lib.Detail;
                d.DownloadCurrentCommand.Execute(null);
                pump();
                Console.WriteLine($"After download current: {d.StatusText}; queued jobs {vault.Queue.Jobs.Count}: {string.Join(", ", vault.Queue.Jobs.Select(j => $"{j.DepotId}:{j.ManifestId}->{j.TargetVersionId} {j.State}"))}");
                d.SelectedHistory = d.History.First(h => h.ManifestId == 999999999999999999);
                d.DownloadHistoryCommand.Execute(null);
                pump();
                Console.WriteLine($"After download history: {d.StatusText}; versions {d.Versions.Count}");
                d.SelectedVersion = d.Versions.First(v => !v.IsActive && v.IsComplete);
                var deleting = d.SelectedVersion.Id;
                d.DeleteCommand.Execute(null);
                pump();
                Console.WriteLine($"After delete {deleting}: {d.StatusText}; exists={vault.Library.Find(deleting) is not null}");
                d.SelectedVersion = d.Versions.First(v => v.IsActive);
                d.SelectedVersion.Label = "Renamed";
                pump();
                Console.WriteLine($"Label persisted: {vault.Library.Find(d.SelectedVersion.Id).Label}");
                save(window, "library-actions");
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
