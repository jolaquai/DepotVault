using DepotVault.Core;
using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;
using DepotVault.Core.SteamInstall;
using DepotVault.Tests.Download;
using SteamKit2;

namespace DepotVault.Tests.SteamInstall;

public class SwitcherTests
{
    private static readonly byte[] Big = FakeDepot.Bytes("shared-big-data-", 200);

    private sealed class Prompts : ISwitchPrompts
    {
        public int SteamPrompts;
        public int CopyPrompts;
        public Func<bool> OnSteam = () => false;
        public CopyConsent Copy = new(false, false);

        public Task<bool> WaitForSteamExitAsync(CancellationToken ct)
        {
            SteamPrompts++;
            return Task.FromResult(OnSteam());
        }

        public Task<CopyConsent> AskCopyAsync(int fileCount, long bytes, CancellationToken ct)
        {
            CopyPrompts++;
            return Task.FromResult(Copy);
        }
    }

    private sealed class NoLinks(ILinkStrategy inner) : ILinkStrategy
    {
        public bool TryReflink(string source, string target) => false;
        public bool TryHardlink(string source, string target) => false;
        public bool TrySymlink(string source, string target) => false;
        public void CreateDirectoryLink(string linkPath, string targetDir) => throw new IOException("no junctions");
        public string GetDirectoryLinkTarget(string path) => inner.GetDirectoryLinkTarget(path);
        public ulong GetVolumeId(string path) => inner.GetVolumeId(path);
        public FileIdentity GetFileIdentity(string path) => inner.GetFileIdentity(path);
        public int DefaultMaxHardlinks => inner.DefaultMaxHardlinks;
    }

    private sealed class Env : IDisposable, IInstalledManifestSource, IManifestProvider
    {
        public readonly TempDir Dir = new();
        public readonly AppPaths Paths;
        public readonly LibraryIndex Lib;
        public readonly AppRepository Apps;
        public readonly SettingsStore Settings;
        public readonly FakeDepot Depot = new();
        public readonly Dictionary<ulong, DepotManifest> Manifests = [];
        public readonly Prompts Prompts = new();
        public readonly ContentIndex Index;
        public Linker Linker;
        public bool SteamRunning;
        public readonly string SteamLib;
        public readonly string InstallDir;
        public readonly string AcfPath;
        public readonly string Root;
        public readonly string OriginalAcf;

        public Env(bool foreign = false)
        {
            Paths = new AppPaths(Dir.Combine("data"));
            Lib = new LibraryIndex(Paths);
            Apps = new AppRepository(Paths);
            Settings = new SettingsStore(Paths);
            var strategy = LinkStrategy.CreateForCurrentPlatform();
            Index = new ContentIndex(Lib, strategy);
            Linker = new Linker(new CapabilityCache(strategy));
            SteamLib = Dir.Combine("SteamLibrary");
            Root = Path.Combine(SteamLib, "DepotVault");
            InstallDir = Path.Combine(SteamLib, "steamapps", "common", "Game");
            AcfPath = Path.Combine(SteamLib, "steamapps", "appmanifest_10.acf");

            Manifests[1] = Depot.Build(11, 1, 128, ("game.exe", FakeDepot.Bytes("v1")), ("data.pak", Big), (@"cfg\settings.ini", FakeDepot.Bytes("a=1")), ("old.txt", FakeDepot.Bytes("legacy")));
            Manifests[2] = Depot.Build(11, 2, 128, ("game.exe", FakeDepot.Bytes("v2")), ("data.pak", Big), (@"cfg\settings.ini", FakeDepot.Bytes("a=1")), ("new.txt", FakeDepot.Bytes("fresh")));
            Manifests[3] = Depot.Build(11, 3, 128, ("game.exe", FakeDepot.Bytes("v3")), ("data.pak", Big));

            Directory.CreateDirectory(Path.Combine(InstallDir, "cfg"));
            File.WriteAllText(Path.Combine(InstallDir, "game.exe"), "v1");
            File.WriteAllBytes(Path.Combine(InstallDir, "data.pak"), Big);
            File.WriteAllText(Path.Combine(InstallDir, "cfg", "settings.ini"), "a=1");
            File.WriteAllText(Path.Combine(InstallDir, "old.txt"), "legacy");
            if (foreign)
                File.WriteAllText(Path.Combine(InstallDir, "mod.dll"), "user mod");
            OriginalAcf = "\"AppState\"\r\n{\r\n\t\"appid\"\t\t\"10\"\r\n\t\"name\"\t\t\"Game\"\r\n\t\"installdir\"\t\t\"Game\"\r\n\t\"buildid\"\t\t\"100\"\r\n\t\"AutoUpdateBehavior\"\t\t\"0\"\r\n\t\"InstalledDepots\"\r\n\t{\r\n\t\t\"11\"\r\n\t\t{\r\n\t\t\t\"manifest\"\t\t\"1\"\r\n\t\t\t\"size\"\t\t\"100\"\r\n\t\t}\r\n\t}\r\n}\r\n";
            File.WriteAllText(AcfPath, OriginalAcf);
        }

        public InstalledApp Installed => new(10, new SteamLibrary(SteamLib, new HashSet<uint> { 10 }), AcfPath, InstallDir, AcfFile.Load(AcfPath));

        public Switcher Switcher() => new(Lib, Apps, Settings, Linker, Prompts, this, () => SteamRunning, Index);

        public async Task<VersionRecord> Download(ulong manifestId)
        {
            var v = Lib.CreateVersion(10, Root, [(11, manifestId)]);
            var resolver = new LibraryTargetResolver(Lib, Index, new Deduper(Lib, Linker));
            await new DepotJobRunner(this, _ => Depot, resolver, () => 2).RunAsync(new DownloadJob { AppId = 10, DepotId = 11, ManifestId = manifestId, TargetVersionId = v.Id }, TestContext.Current.CancellationToken);
            return Lib.Find(v.Id);
        }

        public Task<SwitchReport> Switch(VersionRecord v, uint buildId = 0) => Switcher().SwitchAsync(new SwitchRequest { AppId = 10, TargetVersionId = v.Id, Install = Installed, AcfBuildId = buildId }, TestContext.Current.CancellationToken);

        public string Read(string rel) => File.ReadAllText(Path.Combine(InstallDir, rel));

        Task<DepotManifest> IInstalledManifestSource.GetAsync(uint appId, uint depotId, ulong manifestId, CancellationToken ct) => Task.FromResult(Manifests[manifestId]);

        Task<DepotManifest> IManifestProvider.GetAsync(uint appId, uint depotId, ulong manifestId, string savePath, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            Manifests[manifestId].SaveToFile(savePath);
            return Task.FromResult(Manifests[manifestId]);
        }

        public void Dispose()
        {
            if (File.Exists(AcfPath))
                FileUtil.SetReadOnly(AcfPath, false);
            if (LinkStrategy.IsDirectoryLink(InstallDir))
                LinkStrategy.RemoveDirectoryLink(InstallDir);
            Settings.Dispose();
            Apps.Dispose();
            Lib.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task FirstSwitchAdoptsInstallAndUsesJunction()
    {
        using var env = new Env();
        var v2 = await env.Download(2);
        var report = await env.Switch(v2, buildId: 555);

        Assert.True(report.Success, string.Join("; ", report.Failures));
        Assert.Equal(InstallMode.Junction, report.Mode);
        Assert.True(LinkStrategy.IsDirectoryLink(env.InstallDir));
        Assert.Equal("v2", env.Read("game.exe"));
        Assert.Equal("fresh", env.Read("new.txt"));
        Assert.False(File.Exists(Path.Combine(env.InstallDir, "old.txt")));

        var adopted = env.Lib.Find(report.AdoptedVersionId);
        Assert.True(adopted.Adopted);
        Assert.True(adopted.IsComplete);
        Assert.Equal(100u, adopted.BuildId);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(env.Lib.GetVersionDir(adopted), "old.txt")));
        Assert.Equal(adopted.Id, env.Lib.GetApp(10).AdoptedVersionId);
        Assert.Equal(v2.Id, env.Lib.GetApp(10).ActiveVersionId);

        var acf = AcfFile.Load(env.AcfPath);
        Assert.Equal(1, acf.AutoUpdateBehavior);
        Assert.Equal(2ul, acf.InstalledDepots.Single().ManifestId);
        Assert.Equal(555u, acf.BuildId);
        Assert.True(FileUtil.IsReadOnly(env.AcfPath));
    }

    [Fact]
    public async Task SwitchBetweenVersionsAndRevert()
    {
        using var env = new Env();
        var v2 = await env.Download(2);
        var v3 = await env.Download(3);
        Assert.True((await env.Switch(v2)).Success);
        var r3 = await env.Switch(v3);
        Assert.True(r3.Success);
        Assert.Equal("v3", env.Read("game.exe"));
        Assert.False(File.Exists(Path.Combine(env.InstallDir, "new.txt")));

        var revert = await env.Switcher().RevertAsync(10, env.Installed, TestContext.Current.CancellationToken);
        Assert.True(revert.Success);
        Assert.Equal("v1", env.Read("game.exe"));
        Assert.Equal("legacy", env.Read("old.txt"));
        Assert.Equal(env.OriginalAcf, File.ReadAllText(env.AcfPath));
        Assert.False(FileUtil.IsReadOnly(env.AcfPath));
        Assert.Equal(env.Lib.GetApp(10).AdoptedVersionId, env.Lib.GetApp(10).ActiveVersionId);
    }

    [Fact]
    public async Task ForeignFilesForcePerFileModeAndStayUntouched()
    {
        using var env = new Env(foreign: true);
        var v2 = await env.Download(2);
        var v3 = await env.Download(3);
        var report = await env.Switch(v2);

        Assert.True(report.Success, string.Join("; ", report.Failures));
        Assert.Equal(InstallMode.PerFile, report.Mode);
        Assert.False(LinkStrategy.IsDirectoryLink(env.InstallDir));
        Assert.Equal("user mod", env.Read("mod.dll"));
        Assert.Equal("v2", env.Read("game.exe"));
        Assert.False(File.Exists(Path.Combine(env.InstallDir, "old.txt")));
        Assert.False(File.Exists(Path.Combine(env.Lib.GetVersionDir(env.Lib.Find(report.AdoptedVersionId)), "mod.dll")));
        Assert.DoesNotContain(LinkKind.Copy, report.Links.Keys);

        Assert.True((await env.Switch(v3)).Success);
        Assert.Equal("v3", env.Read("game.exe"));
        Assert.False(File.Exists(Path.Combine(env.InstallDir, "new.txt")));
        Assert.False(File.Exists(Path.Combine(env.InstallDir, "cfg", "settings.ini")));
        Assert.Equal("user mod", env.Read("mod.dll"));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(env.Lib.GetVersionDir(v2), "game.exe")));
    }

    [Fact]
    public async Task SteamRunningPromptsAndCancelChangesNothing()
    {
        using var env = new Env();
        var v2 = await env.Download(2);
        env.SteamRunning = true;
        var report = await env.Switch(v2);
        Assert.True(report.Canceled);
        Assert.Equal(1, env.Prompts.SteamPrompts);
        Assert.False(LinkStrategy.IsDirectoryLink(env.InstallDir));
        Assert.Equal(env.OriginalAcf, File.ReadAllText(env.AcfPath));

        env.Prompts.OnSteam = () => { env.SteamRunning = false; return true; };
        Assert.True((await env.Switch(v2)).Success);
    }

    [Fact]
    public async Task CopyFallbackIsAskedAndRemembered()
    {
        using var env = new Env(foreign: true);
        var v2 = await env.Download(2);
        env.Linker = new Linker(new CapabilityCache(new NoLinks(LinkStrategy.CreateForCurrentPlatform())));

        var denied = await env.Switch(v2);
        Assert.False(denied.Success);
        Assert.NotEmpty(denied.Failures);
        Assert.Equal(1, env.Prompts.CopyPrompts);
        Assert.Equal(CopyFallback.Unset, env.Settings.Current.CopyFallback);
        Assert.Equal(1ul, AcfFile.Load(env.AcfPath).InstalledDepots.Single().ManifestId);

        env.Prompts.Copy = new CopyConsent(true, true);
        var ok = await env.Switch(v2);
        Assert.True(ok.Success, string.Join("; ", ok.Failures));
        Assert.Equal(CopyFallback.Always, env.Settings.Current.CopyFallback);
        Assert.True(ok.Links[LinkKind.Copy] > 0);
        Assert.Equal("v2", env.Read("game.exe"));
        Assert.Equal(2, env.Prompts.CopyPrompts);
    }

    [Fact]
    public async Task IsolatedFilesAreDetachedBeforeJunction()
    {
        using var env = new Env();
        var v2 = await env.Download(2);
        var v3 = await env.Download(3);
        var strategy = LinkStrategy.CreateForCurrentPlatform();
        var caps = env.Linker.Capabilities.Get(env.Root);
        Assert.SkipWhen(caps.Reflink, "Reflinked files are already isolated");
        Assert.Equal(2u, strategy.GetFileIdentity(Path.Combine(env.Lib.GetVersionDir(v3), "data.pak")).LinkCount);

        env.Apps.Get(10).SetDecision("data.pak", MutableDecision.Isolate);
        Assert.True((await env.Switch(v3)).Success);
        Assert.Equal(1u, strategy.GetFileIdentity(Path.Combine(env.Lib.GetVersionDir(v3), "data.pak")).LinkCount);
        Assert.Equal(Big, File.ReadAllBytes(Path.Combine(env.InstallDir, "data.pak")));
        Assert.Empty(new IntegrityChecker(env.Lib).Scan(v3));
    }
}
