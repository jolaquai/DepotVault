using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;
using SteamKit2;

namespace DepotVault.Core.SteamInstall;

public sealed class Switcher(LibraryIndex library, AppRepository apps, SettingsStore settings, Linker linker, ISwitchPrompts prompts, IInstalledManifestSource installedManifests, Func<bool> isSteamRunning = null, ContentIndex index = null)
{
    private const string OriginalAcfName = "original.acf";

    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

    private readonly Func<bool> _isSteamRunning = isSteamRunning ?? SteamLocator.IsSteamRunning;

    private async Task<bool> WaitForSteamAsync(SwitchReport report, CancellationToken ct)
    {
        while (_isSteamRunning())
        {
            if (!await prompts.WaitForSteamExitAsync(ct).ConfigureAwait(false))
            {
                report.Canceled = true;
                return false;
            }
        }
        return true;
    }

    public async Task<SwitchReport> SwitchAsync(SwitchRequest request, CancellationToken ct = default)
    {
        var report = new SwitchReport();
        if (!await WaitForSteamAsync(report, ct).ConfigureAwait(false))
            return report;

        var target = library.Find(request.TargetVersionId);
        if (target is null || target.AppId != request.AppId)
            return report.Fail(request.TargetVersionId, "Version not found.");
        if (!target.IsComplete)
            return report.Fail(target.Id, "Version is not fully downloaded.");

        var app = apps.Get(request.AppId);
        var install = app.Install ??= new InstallState();
        var installDir = Path.GetFullPath(request.Install.InstallPath);
        var acfPath = request.Install.AcfPath;
        var targetDir = library.GetVersionDir(target);

        if (install.Mode == InstallMode.None && library.GetApp(request.AppId)?.AdoptedVersionId is null && Directory.Exists(installDir) && !LinkStrategy.IsDirectoryLink(installDir))
        {
            var adopted = await AdoptAsync(request, target.Root, installDir, report, ct).ConfigureAwait(false);
            if (adopted is null)
                return report;
            report.AdoptedVersionId = adopted.Id;
        }

        var targetFiles = VersionStateStore.Load(library.Paths.VersionStateFile(target.Id)).Files;
        var parent = Path.GetDirectoryName(installDir);
        Directory.CreateDirectory(parent);
        var caps = linker.Capabilities.Get(parent);
        var sameVolume = linker.Capabilities.SameVolume(targetDir, parent);

        string oldJunctionDir = null;
        List<string> foreignInOld = [];
        List<string> foreignInInstall = [];
        if (LinkStrategy.IsDirectoryLink(installDir))
        {
            oldJunctionDir = linker.Strategy.GetDirectoryLinkTarget(installDir);
            if (oldJunctionDir is not null && install.VersionId is { } oldId && library.Find(oldId) is not null)
                foreignInOld = Foreign(oldJunctionDir, VersionStateStore.Load(library.Paths.VersionStateFile(oldId)).Files.Select(f => f.RelPath));
        }
        else if (Directory.Exists(installDir))
        {
            foreignInInstall = Foreign(installDir, install.Placed.Select(p => p.RelPath));
        }

        var force = app.ForceStrategy?.Trim().ToLowerInvariant();
        var junction = caps.DirectoryLink && foreignInOld.Count == 0 && foreignInInstall.Count == 0 && force is null or "" or "junction";
        var copyAllowed = force == "copy";
        if (!junction)
        {
            var canLink = force != "copy" && (sameVolume && (caps.Reflink || (caps.Hardlink && force != "symlink")) || (caps.Symlink && force != "hardlink"));
            if (!canLink && !copyAllowed)
            {
                var needing = targetFiles.Where(f => app.GetDecision(f.RelPath) != MutableDecision.Isolate).ToList();
                if (needing.Count > 0)
                {
                    copyAllowed = await CopyAllowedAsync(needing.Count, needing.Sum(f => f.Size), ct).ConfigureAwait(false);
                    if (!copyAllowed)
                        return report.Fail(installDir, "No link method is available between the library and the Steam install, and copying was not allowed.");
                }
            }
        }

        report.StagingDir = Path.Combine(library.Paths.Root, "staging", request.AppId.ToString(), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        TearDown(installDir, install, report);

        if (junction)
        {
            if (Directory.Exists(installDir))
                Directory.Delete(installDir, false);
            DetachIsolated(app, target, targetDir, targetFiles);
            linker.Strategy.CreateDirectoryLink(installDir, targetDir);
            install.Mode = InstallMode.Junction;
        }
        else
        {
            Directory.CreateDirectory(installDir);
            var owned = new HashSet<string>(targetFiles.Select(f => f.RelPath), StringComparer.OrdinalIgnoreCase);
            foreach (var rel in foreignInOld)
            {
                var dst = Path.Combine(installDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (owned.Contains(rel))
                    Stage(Path.Combine(oldJunctionDir, rel), rel, report);
                else
                    File.Move(Path.Combine(oldJunctionDir, rel), dst);
            }
            foreach (var f in targetFiles)
            {
                ct.ThrowIfCancellationRequested();
                var src = Path.Combine(targetDir, f.RelPath);
                var dst = Path.Combine(installDir, f.RelPath);
                if (!File.Exists(src))
                {
                    report.Failures.Add(new SwitchFailure(src, "Missing in library version."));
                    continue;
                }
                if (File.Exists(dst))
                    Stage(dst, f.RelPath, report);
                var isolate = app.GetDecision(f.RelPath) == MutableDecision.Isolate;
                var rules = force switch
                {
                    "copy" => new LinkRules(AllowHardlink: false, AllowSymlink: false, AllowCopy: true),
                    _ when isolate => new LinkRules(AllowHardlink: false, AllowSymlink: false, AllowCopy: true),
                    "symlink" => new LinkRules(AllowHardlink: false, AllowSymlink: true, AllowCopy: copyAllowed),
                    _ => new LinkRules(AllowHardlink: true, AllowSymlink: force != "hardlink", AllowCopy: copyAllowed),
                };
                LinkKind kind;
                try
                {
                    kind = linker.Link(src, dst, rules);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Failures.Add(new SwitchFailure(dst, ex.Message));
                    continue;
                }
                if (kind == LinkKind.None)
                {
                    report.Failures.Add(new SwitchFailure(dst, "No permitted link method succeeded."));
                    continue;
                }
                report.Links[kind] = report.Links.GetValueOrDefault(kind) + 1;
                var info = new FileInfo(dst);
                install.Placed.Add(new PlacedFile { RelPath = f.RelPath, Link = kind, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
            }
            install.Mode = InstallMode.PerFile;
        }
        install.VersionId = target.Id;
        report.Mode = install.Mode;

        PatchAcf(acfPath, target, request.AcfBuildId);
        library.SetActive(request.AppId, target.Id);
        apps.Save(app, debounced: false);
        report.Success = report.Failures.Count == 0;
        return report;
    }

    public async Task<SwitchReport> RevertAsync(uint appId, InstalledApp installed, CancellationToken ct = default)
    {
        var adoptedId = library.GetApp(appId)?.AdoptedVersionId;
        if (adoptedId is null)
            return new SwitchReport().Fail(installed.InstallPath, "No adopted original install to revert to.");
        var report = await SwitchAsync(new SwitchRequest { AppId = appId, TargetVersionId = adoptedId, Install = installed }, ct).ConfigureAwait(false);
        if (report.Canceled)
            return report;
        var original = Path.Combine(library.Paths.VersionDir(adoptedId), OriginalAcfName);
        if (File.Exists(original))
        {
            if (File.Exists(installed.AcfPath))
                FileUtil.SetReadOnly(installed.AcfPath, false);
            File.Copy(original, installed.AcfPath, true);
            FileUtil.SetReadOnly(installed.AcfPath, false);
        }
        return report;
    }

    public async Task<SwitchReport> ReleaseAsync(uint appId, InstalledApp installed, bool detachShared, CancellationToken ct = default)
    {
        var report = new SwitchReport();
        if (!await WaitForSteamAsync(report, ct).ConfigureAwait(false))
            return report;
        var app = apps.Get(appId);
        var install = app.Install ??= new InstallState();
        var installDir = Path.GetFullPath(installed.InstallPath);
        report.StagingDir = Path.Combine(library.Paths.Root, "staging", appId.ToString(), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        TearDown(installDir, install, report);
        install.VersionId = null;
        library.UpdateApp(appId, a => a.ActiveVersionId = null);

        if (library.GetApp(appId)?.AdoptedVersionId is { } adoptedId && library.Find(adoptedId) is { } adopted)
        {
            var src = library.GetVersionDir(adopted);
            if (Directory.Exists(src))
            {
                ReadOnlyProtection.Remove(src);
                var parent = Path.GetDirectoryName(installDir);
                Directory.CreateDirectory(parent);
                if (!Directory.Exists(installDir) && linker.Capabilities.SameVolume(src, parent))
                {
                    Directory.Move(src, installDir);
                }
                else
                {
                    foreach (var file in Directory.EnumerateFiles(src, "*", Recursive))
                    {
                        var dst = Path.Combine(installDir, Path.GetRelativePath(src, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        try
                        {
                            File.Move(file, dst, true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            report.Failures.Add(new SwitchFailure(dst, ex.Message));
                        }
                    }
                }
                if (detachShared && Directory.Exists(installDir))
                    DetachAll(installDir, report);
            }
            if (report.Failures.Count > 0)
                return report;
            var original = Path.Combine(library.Paths.VersionDir(adoptedId), OriginalAcfName);
            if (File.Exists(original))
            {
                if (File.Exists(installed.AcfPath))
                    FileUtil.SetReadOnly(installed.AcfPath, false);
                File.Copy(original, installed.AcfPath, true);
                FileUtil.SetReadOnly(installed.AcfPath, false);
            }
            library.UpdateApp(appId, a => a.AdoptedVersionId = null);
            index?.RemoveVersion(adoptedId);
            library.DeleteVersion(adoptedId);
        }
        else if (File.Exists(installed.AcfPath))
        {
            FileUtil.SetReadOnly(installed.AcfPath, false);
        }
        app.Install = null;
        apps.Save(app, debounced: false);
        report.Success = report.Failures.Count == 0;
        return report;
    }

    private void DetachAll(string dir, SwitchReport report)
    {
        foreach (var full in Directory.EnumerateFiles(dir, "*", Recursive))
        {
            if (linker.Strategy.GetFileIdentity(full).LinkCount <= 1)
                continue;
            var tmp = full + ".dvdetach";
            try
            {
                FileUtil.ForceDelete(tmp);
                if (linker.Link(full, tmp, new LinkRules(AllowHardlink: false, AllowSymlink: false, AllowCopy: true)) == LinkKind.None)
                {
                    report.Failures.Add(new SwitchFailure(full, "Could not make an independent copy."));
                    continue;
                }
                FileUtil.ForceDelete(full);
                File.Move(tmp, full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Failures.Add(new SwitchFailure(full, ex.Message));
            }
        }
    }

    private async Task<VersionRecord> AdoptAsync(SwitchRequest request, string root, string installDir, SwitchReport report, CancellationToken ct)
    {
        var acf = AcfFile.Load(request.Install.AcfPath);
        var depots = acf.InstalledDepots;
        var manifests = new List<DepotManifest>(depots.Count);
        foreach (var d in depots)
        {
            try
            {
                manifests.Add(await installedManifests.GetAsync(request.AppId, d.DepotId, d.ManifestId, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Fail(request.Install.AcfPath, $"Cannot load installed manifest {d.ManifestId} of depot {d.DepotId}: {ex.Message}");
                return null;
            }
        }

        var owned = new Dictionary<string, (uint DepotId, DepotManifest.FileData File)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < manifests.Count; i++)
        {
            foreach (var f in manifests[i].Files)
            {
                if ((f.Flags & EDepotFileFlag.Directory) == 0)
                    owned[FilePlanner.NormalizeRelPath(f.FileName)] = (depots[i].DepotId, f);
            }
        }
        var foreign = Foreign(installDir, owned.Keys);

        var adopted = library.CreateVersion(request.AppId, root, depots.Select(d => (d.DepotId, d.ManifestId)), $"Original (build {acf.BuildId})");
        library.Update(adopted.Id, v => { v.Adopted = true; v.BuildId = acf.BuildId; });
        var adoptedDir = library.GetVersionDir(adopted);
        var sameVolume = linker.Capabilities.SameVolume(installDir, adoptedDir);
        if (!sameVolume)
        {
            var bytes = owned.Keys.Select(k => Path.Combine(installDir, k)).Where(File.Exists).Sum(p => new FileInfo(p).Length);
            if (!await CopyAllowedAsync(owned.Count, bytes, ct).ConfigureAwait(false))
            {
                library.DeleteVersion(adopted.Id);
                report.Fail(installDir, "The library root is on another volume; adopting the current install requires copying, which was not allowed.");
                return null;
            }
        }

        for (var i = 0; i < manifests.Count; i++)
            manifests[i].SaveToFile(library.Paths.ManifestFile(adopted.Id, depots[i].DepotId));
        File.Copy(request.Install.AcfPath, Path.Combine(library.Paths.VersionDir(adopted.Id), OriginalAcfName), true);

        if (foreign.Count == 0 && sameVolume)
        {
            Directory.Delete(adoptedDir, false);
            Directory.Move(installDir, adoptedDir);
        }
        else
        {
            foreach (var rel in owned.Keys)
            {
                var src = Path.Combine(installDir, rel);
                if (!File.Exists(src))
                    continue;
                var dst = Path.Combine(adoptedDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Move(src, dst);
            }
            RemoveEmptyDirs(installDir);
        }

        var snapshots = new List<FileSnapshot>();
        foreach (var (rel, (depotId, file)) in owned)
        {
            var full = Path.Combine(adoptedDir, rel);
            if (!File.Exists(full))
                continue;
            var info = new FileInfo(full);
            snapshots.Add(new FileSnapshot { RelPath = rel, DepotId = depotId, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Sha1 = file.FileHash is { Length: 20 } h ? Convert.ToHexStringLower(h) : null });
        }
        VersionStateStore.Save(library.Paths.VersionStateFile(adopted.Id), new VersionState { Files = snapshots });
        foreach (var d in depots)
            library.MarkDepotComplete(adopted.Id, d.DepotId, d.ManifestId);
        library.UpdateApp(request.AppId, a => a.AdoptedVersionId = adopted.Id);
        index?.AddVersion(adopted.Id);
        return library.Find(adopted.Id);
    }

    private void TearDown(string installDir, InstallState install, SwitchReport report)
    {
        if (LinkStrategy.IsDirectoryLink(installDir))
        {
            LinkStrategy.RemoveDirectoryLink(installDir);
        }
        else if (Directory.Exists(installDir))
        {
            foreach (var p in install.Placed)
            {
                var full = Path.Combine(installDir, p.RelPath);
                var info = new FileInfo(full);
                if (!info.Exists && info.LinkTarget is null)
                    continue;
                var changed = info.Exists && (info.Length != p.Size || info.LastWriteTimeUtc != p.LastWriteUtc);
                if (changed && p.Link is LinkKind.Copy or LinkKind.Reflink)
                    Stage(full, p.RelPath, report);
                else
                    FileUtil.ForceDelete(full);
            }
            RemoveEmptyDirs(installDir);
        }
        install.Placed.Clear();
        install.Mode = InstallMode.None;
    }

    private void Stage(string path, string rel, SwitchReport report)
    {
        var dst = Path.Combine(report.StagingDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        File.Move(path, dst, true);
        report.Staged.Add(rel);
    }

    private void DetachIsolated(AppRecord app, VersionRecord target, string targetDir, List<FileSnapshot> files)
    {
        var changed = false;
        foreach (var f in files)
        {
            if (app.GetDecision(f.RelPath) != MutableDecision.Isolate)
                continue;
            var full = Path.Combine(targetDir, f.RelPath);
            if (!File.Exists(full) || linker.Strategy.GetFileIdentity(full).LinkCount <= 1)
                continue;
            var tmp = full + ".dvdetach";
            FileUtil.ForceDelete(tmp);
            var kind = linker.Link(full, tmp, new LinkRules(AllowHardlink: false, AllowSymlink: false, AllowCopy: true));
            FileUtil.ForceDelete(full);
            File.Move(tmp, full);
            var info = new FileInfo(full);
            f.Link = kind;
            f.Size = info.Length;
            f.LastWriteUtc = info.LastWriteTimeUtc;
            changed = true;
        }
        if (changed)
            VersionStateStore.Save(library.Paths.VersionStateFile(target.Id), new VersionState { Files = files });
    }

    private void PatchAcf(string acfPath, VersionRecord target, uint buildId)
    {
        if (!File.Exists(acfPath))
            return;
        var acf = AcfFile.Load(acfPath);
        acf.SetAutoUpdateBehavior(1);
        foreach (var m in target.Manifests)
            acf.SetInstalledDepot(m.DepotId, m.ManifestId, 0);
        if (buildId != 0)
            acf.SetBuildId(buildId);
        FileUtil.SetReadOnly(acfPath, false);
        acf.Save();
        if (settings.Current.AcfLock)
            FileUtil.SetReadOnly(acfPath, true);
    }

    private async Task<bool> CopyAllowedAsync(int count, long bytes, CancellationToken ct)
    {
        switch (settings.Current.CopyFallback)
        {
            case CopyFallback.Always:
                return true;
            case CopyFallback.Never:
                return false;
        }
        var consent = await prompts.AskCopyAsync(count, bytes, ct).ConfigureAwait(false);
        if (consent.Remember)
        {
            settings.Current.CopyFallback = consent.Allow ? CopyFallback.Always : CopyFallback.Never;
            settings.Save();
        }
        return consent.Allow;
    }

    private static List<string> Foreign(string dir, IEnumerable<string> owned)
    {
        if (!Directory.Exists(dir))
            return [];
        var set = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", Recursive))
        {
            var rel = Path.GetRelativePath(dir, f);
            if (!set.Contains(rel))
                result.Add(rel);
        }
        return result;
    }

    private static void RemoveEmptyDirs(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var d in Directory.EnumerateDirectories(root, "*", Recursive).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(d).Any())
                Directory.Delete(d);
        }
    }
}
