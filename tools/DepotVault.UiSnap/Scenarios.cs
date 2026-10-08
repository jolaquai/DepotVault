using Avalonia.Controls;
using DepotVault.App;
using DepotVault.App.ViewModels;
using DepotVault.App.Views;
using DepotVault.App.Views.Dialogs;
using DepotVault.Core;
using DepotVault.Core.Download;
using DepotVault.Core.Steam;
using Microsoft.Extensions.DependencyInjection;

internal static class Scenarios
{
    public static void Prepare(string scenario, string dataDir)
    {
        if (scenario is not ("switch-dialogs" or "mutable"))
            return;
        var home = Path.Combine(dataDir, "home");
        var steamApps = Path.Combine(home, ".steam", "steam", "steamapps");
        var install = Path.Combine(steamApps, "common", "Sample Game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "game.bin"), "installed");
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_480000.acf"), """
            "AppState"
            {
            	"appid"		"480000"
            	"name"		"Sample Game"
            	"installdir"		"Sample Game"
            	"buildid"		"9876543"
            	"StateFlags"		"4"
            	"AutoUpdateBehavior"		"0"
            	"InstalledDepots"
            	{
            		"480001"
            		{
            			"manifest"		"1234567890123456789"
            			"size"		"9"
            		}
            	}
            }
            """);
        Environment.SetEnvironmentVariable("HOME", home);
    }

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
            case "import":
            {
                var tvm = new TutorialViewModel(vault.Settings);
                var tut = new TutorialDialog { DataContext = tvm };
                tut.Show();
                for (var i = 1; i <= tvm.Steps.Count; i++)
                {
                    if (tvm.IsLast)
                        tvm.DontShowAgain = true;
                    save(tut, $"tutorial-{i}");
                    tvm.NextCommand.Execute(null);
                }
                pump();
                Console.WriteLine($"Tutorial closed={!tut.IsVisible}, seen={vault.Settings.Current.TutorialSeen}, dontShowAgain={vault.Settings.Current.TutorialDontShowAgain}");

                var lib = services.GetRequiredService<LibraryViewModel>();
                shell.CurrentPage = lib;
                lib.OnActivated();
                pump();
                var d = lib.Detail;
                var before = d.History.Count;
                d.ImportCommand.Execute(null);
                pump();
                var dialogs = services.GetRequiredService<AppDialogs>();
                var dlg = dialogs.ActiveImport ?? throw new InvalidOperationException("Import dialog did not open.");
                var vm = (ImportViewModel)dlg.DataContext;
                vm.PasteText = """
                    https://steamdb.info/depot/480001/manifests/
                    Date	Relative	ManifestID
                    15 August 2026 - 10:22:01 UTC	2 months ago	7777777777777777777
                    3 May 2026 - 18:00:00 UTC	5 months ago	6666666666666666666
                    14 March 2025 - 08:30:00 UTC	1 year ago	1111111111111111111
                    not a manifest 12345678901
                    5555555555555555555
                    """;
                pump();
                Console.WriteLine($"Depot {vm.DepotIdText} ({vm.SelectedDepot}); {vm.Summary} {vm.WarningText}");
                vm.Rows.First(r => r.ManifestId == "6666666666666666666").Include = false;
                save(dlg, "import-preview");
                vm.ImportCommand.Execute(null);
                Wait(() => dialogs.ActiveImport is null, pump, 5);
                Console.WriteLine($"Imported {vm.Added}; history {before} -> {d.History.Count}; status: {d.StatusText}; persisted {string.Join(", ", vault.Apps.Get(480000).History.Select(h => h.ManifestId))}");
                save(window, "import-history");
                break;
            }
            case "settings":
            {
                var s = services.GetRequiredService<SettingsViewModel>();
                shell.CurrentPage = s;
                window.Height = 1250;
                pump();
                save(window, "settings");
                s.MaxConcurrentJobs = 3;
                s.MaxConcurrentChunks = 24;
                s.BandwidthLimitMiB = 5.5m;
                s.DedupeEnabled = false;
                s.ExclusionText = "*.log\n  saves/*  \n\n*.log";
                s.CopyFallbackIndex = 2;
                s.AcfLock = false;
                s.IntegrityCheckOnStartup = false;
                s.ShowTutorial = false;
                s.ThemeIndex = 2;
                s.ReadOnlyProtection = true;
                Wait(() => s.StatusText is not null, pump, 10);
                Console.WriteLine($"Read-only: {s.StatusText}");
                var root = s.Roots[0];
                s.RemoveRootCommand.Execute(root);
                Console.WriteLine($"Remove in-use root: {s.StatusText}; roots {s.Roots.Count}");
                var extra = Path.Combine(vault.Paths.Root, "extra-root");
                s.AddSuggestedCommand.Execute(new RootItem(extra, "", 0));
                Console.WriteLine($"Added root, roots {s.Roots.Count}");
                save(window, "settings-changed");
                s.RemoveRootCommand.Execute(s.Roots.First(r => r.Path.EndsWith("extra-root")));
                s.ReadOnlyProtection = false;
                Wait(() => s.StatusText?.StartsWith("Unprotected") == true, pump, 10);
                Console.WriteLine($"Read-only off: {s.StatusText}; roots {s.Roots.Count}");
                vault.Settings.Flush();
                using var reloaded = new DepotVault.Core.Persistence.SettingsStore(vault.Paths);
                var c = reloaded.Current;
                Console.WriteLine($"Persisted: jobs={c.MaxConcurrentJobs} chunks={c.MaxConcurrentChunks} bw={c.BandwidthLimitBytesPerSecond} dedupe={c.DedupeEnabled} globs=[{string.Join("|", c.GlobalExclusionGlobs)}] copy={c.CopyFallback} acf={c.AcfLock} integrity={c.IntegrityCheckOnStartup} dontShow={c.TutorialDontShowAgain} theme={c.Theme} ro={c.ReadOnlyProtection} roots=[{string.Join("|", c.LibraryRoots)}]");
                break;
            }
            case "switch-dialogs":
            {
                var dialogs = services.GetRequiredService<DialogService>();
                Window shown = null;
                dialogs.Showing += w => shown = w;
                var running = true;
                vault.IsSteamRunning = () => running;
                var lib = services.GetRequiredService<LibraryViewModel>();
                shell.CurrentPage = lib;
                lib.OnActivated();
                pump();
                var d = lib.Detail;
                Console.WriteLine($"Install: {d.InstallText}");
                d.SelectedVersion = d.Versions.First(v => !v.IsActive && v.IsComplete);
                d.SwitchCommand.Execute(null);
                Wait(() => shown is SteamRunningDialog, pump, 10);
                save(shown, "steam-running");
                running = false;
                Wait(() => shown is SwitchReportDialog || !d.IsBusy, pump, 60);
                Console.WriteLine($"Switch: {d.StatusText} (steam dialog visible={shown.IsVisible}, {shown.GetType().Name}, busy={d.IsBusy})");
                if (shown is SwitchReportDialog report)
                {
                    save(report, "switch-report");
                    var rvm = (SwitchReportViewModel)report.DataContext;
                    foreach (var f in rvm.Failures)
                        Console.WriteLine($"  {f.Path}: {f.Reason}");
                    rvm.CloseCommand.Execute(null);
                    pump();
                }

                CacheInstalledManifest(vault);
                var installDir = d.InstallText[(d.InstallText.IndexOf(" at ") + 4)..];
                d.SwitchCommand.Execute(null);
                Wait(() => !d.IsBusy, pump, 30);
                var link = new DirectoryInfo(installDir).LinkTarget;
                Console.WriteLine($"Switch with cached manifest: {d.StatusText}; install link -> {link}; active={vault.Library.GetApp(480000).ActiveVersionId}, versions={d.Versions.Count}");
                save(window, "switched");
                d.RevertCommand.Execute(null);
                Wait(() => !d.IsBusy, pump, 30);
                Console.WriteLine($"Revert: {d.StatusText}; link={new DirectoryInfo(installDir).LinkTarget ?? "none"}; game.bin={File.Exists(Path.Combine(installDir, "game.bin"))}");

                var prompts = services.GetRequiredService<ISwitchPromptsFactory>().Create();
                var ask = prompts.AskCopyAsync(42, 3L << 30, default);
                Wait(() => shown is CopyConsentDialog, pump, 5);
                save(shown, "copy-consent");
                var cvm = (CopyConsentViewModel)shown.DataContext;
                cvm.Remember = true;
                cvm.CopyCommand.Execute(null);
                Wait(() => ask.IsCompleted, pump, 5);
                Console.WriteLine($"Copy consent: {ask.Result}");

                running = true;
                var waitExit = prompts.WaitForSteamExitAsync(default);
                Wait(() => shown is SteamRunningDialog && shown.IsVisible, pump, 5);
                ((SteamRunningViewModel)shown.DataContext).CancelCommand.Execute(null);
                Wait(() => waitExit.IsCompleted, pump, 5);
                Console.WriteLine($"Steam wait after cancel: {waitExit.Result}");
                break;
            }
            case "mutable":
            {
                var dialogs = services.GetRequiredService<DialogService>();
                Window shown = null;
                dialogs.Showing += w => shown = w;
                vault.IsSteamRunning = () => false;
                CacheInstalledManifest(vault);
                var versions = vault.Library.VersionsFor(480000);
                var older = versions.First(v => v.Label == "Pre-patch");
                var newer = versions.First(v => v.Id != older.Id);
                (string Rel, string Content)[] files = [("game.exe", "exe"), (Path.Combine("config", "settings.ini"), "a=1"), (Path.Combine("saves", "slot1.sav"), "save")];
                foreach (var (rel, content) in files)
                {
                    var src = Path.Combine(vault.Library.GetVersionDir(newer), rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(src));
                    File.WriteAllText(src, content);
                    var dst = Path.Combine(vault.Library.GetVersionDir(older), rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    vault.Strategy.TryHardlink(src, dst);
                }
                foreach (var v in versions)
                {
                    var dir = vault.Library.GetVersionDir(v);
                    var m = new SteamKit2.DepotManifest { DepotID = 480001, ManifestGID = v.Manifests[0].ManifestId, Files = [], CreationTime = DateTime.UtcNow };
                    var snaps = new List<DepotVault.Core.Library.FileSnapshot>();
                    foreach (var rel in files.Select(f => f.Rel).Append("data.pak"))
                    {
                        var full = Path.Combine(dir, rel);
                        var sha = System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(full));
                        var info = new FileInfo(full);
                        m.Files.Add(new SteamKit2.DepotManifest.FileData(rel, new byte[20], 0, (ulong)info.Length, sha, null, false, 0));
                        snaps.Add(new DepotVault.Core.Library.FileSnapshot { RelPath = rel, DepotId = 480001, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Sha1 = Convert.ToHexStringLower(sha), Link = rel == "data.pak" ? DepotVault.Core.Library.LinkKind.None : DepotVault.Core.Library.LinkKind.Hardlink });
                    }
                    m.SaveToFile(vault.Paths.ManifestFile(v.Id, 480001));
                    DepotVault.Core.Library.VersionStateStore.Save(vault.Paths.VersionStateFile(v.Id), new DepotVault.Core.Library.VersionState { Files = snaps });
                }
                var app = vault.Apps.Get(480000);
                app.MutableReviewed = false;
                app.ReviewCandidates.Add("game.exe");

                var lib = services.GetRequiredService<LibraryViewModel>();
                shell.CurrentPage = lib;
                lib.OnActivated();
                pump();
                var d = lib.Detail;
                Console.WriteLine($"Pending review: {d.PendingReviewCount}");
                save(window, "mutable-pending");
                d.SelectedVersion = d.Versions.First(v => v.Id == older.Id);
                d.SwitchCommand.Execute(null);
                Wait(() => shown is MutableFilesDialog, pump, 10);
                var mvm = (MutableFilesViewModel)shown.DataContext;
                Wait(() => !mvm.IsLoading, pump, 10);
                foreach (var i in mvm.Items)
                    Console.WriteLine($"  {i.RelPath}: {i.Reasons} [{i.Decision}]");
                mvm.Items.First(i => i.RelPath.StartsWith("saves")).IsSelected = true;
                mvm.IsolateSelectedCommand.Execute(null);
                mvm.Items.First(i => i.RelPath.EndsWith("settings.ini")).Decision = DepotVault.Core.Library.MutableDecision.Share;
                mvm.NewPattern = "*.log";
                mvm.NewPatternDecisionIndex = 1;
                mvm.AddPatternCommand.Execute(null);
                save(shown, "mutable-review");
                mvm.SaveCommand.Execute(null);
                Wait(() => d.StatusText is { } st && (st.StartsWith("Switched") || st.Contains("problem") || st.Contains("failed")), pump, 30);
                Console.WriteLine($"Switch: {d.StatusText}; rules: {string.Join(", ", app.MutableRules.Select(r => $"{r.Pattern}={r.Decision}"))}; reviewed={app.MutableReviewed}; pending={d.PendingReviewCount}");
                foreach (var (rel, _) in files)
                    Console.WriteLine($"  {rel}: link count {vault.Strategy.GetFileIdentity(Path.Combine(vault.Library.GetVersionDir(older), rel)).LinkCount}");
                break;
            }
            default:
                throw new ArgumentException($"Unknown scenario {name}");
        }
    }

    private static void CacheInstalledManifest(Vault vault)
    {
        var cache = Path.Combine(vault.Paths.Root, "manifest-cache", "480001_1234567890123456789.manifest.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(cache));
        var installed = new SteamKit2.DepotManifest { DepotID = 480001, ManifestGID = 1234567890123456789, Files = [], CreationTime = DateTime.UtcNow };
        installed.Files.Add(new SteamKit2.DepotManifest.FileData("game.bin", new byte[20], 0, 9, System.Security.Cryptography.SHA1.HashData("installed"u8), null, false, 0));
        installed.SaveToFile(cache);
    }

    public static void Wait(Func<bool> condition, Action pump, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < end)
            pump();
        pump();
    }
}
