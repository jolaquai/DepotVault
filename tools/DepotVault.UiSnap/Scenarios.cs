using Avalonia.Controls;
using DepotVault.App.ViewModels;
using DepotVault.App.Views;
using DepotVault.Core;
using DepotVault.Core.Download;
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
            case "downloads-live":
            {
                shell.Initialize();
                Wait(() => shell.IsSignedIn, pump, 30);
                if (!shell.IsSignedIn)
                    throw new InvalidOperationException("Not signed in; copy auth.bin into the data dir.");
                var meta = vault.Metadata.RefreshAsync(228980);
                Wait(() => meta.IsCompleted, pump, 30);
                var manifest = meta.Result.Depots.Single(d => d.DepotId == 228988).CurrentManifestId;
                vault.Settings.Current.BandwidthLimitBytesPerSecond = 3 << 20;
                vault.Settings.Current.MaxConcurrentChunks = 4;
                var root = Path.Combine(vault.Paths.Root, "vault");
                var v = vault.EnqueueVersion(228980, [(228988, manifest)], root, "VC++ 2019 redist");
                vault.Queue.Start();
                var downloads = services.GetRequiredService<DownloadsViewModel>();
                shell.CurrentPage = downloads;
                downloads.OnActivated();
                var job = vault.Queue.Jobs.Single(j => j.TargetVersionId == v.Id);
                Wait(() => job.Counters.CompletedBytes > 6 << 20, pump, 60);
                Thread.Sleep(600);
                pump();
                save(window, "downloads-running");
                Console.WriteLine($"Running: {downloads.Jobs[0].ProgressText} {downloads.Jobs[0].SpeedText}");
                downloads.Jobs[0].PauseCommand.Execute(null);
                Wait(() => job.State == JobState.Paused && vault.Queue.WhenIdleAsync().IsCompleted, pump, 10);
                save(window, "downloads-paused");
                Console.WriteLine($"Paused: {downloads.Jobs[0].StateText} {downloads.Jobs[0].ProgressText}, resume chunks {job.Resume?.Count}");
                vault.Settings.Current.BandwidthLimitBytesPerSecond = 0;
                downloads.Jobs[0].ResumeCommand.Execute(null);
                Wait(() => job.IsFinished, pump, 60);
                save(window, "downloads-done");
                Console.WriteLine($"Finished: {job.State} {job.Error} reused {job.Counters.ReusedBytes} written {job.Counters.WrittenBytes}; version complete={vault.Library.Find(v.Id).IsComplete}");
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
