using DepotVault.Core.Download;
using DepotVault.Core.SteamInstall;

namespace DepotVault.Core.Library;

public sealed record RemovalPlan(IReadOnlyList<VersionRecord> Versions, VersionRecord Active, bool ReleasesOriginal);

public sealed class Remover(LibraryIndex library, AppRepository apps, ContentIndex index, DownloadQueue queue, Func<ISwitchPrompts, Switcher> createSwitcher, Func<uint, InstalledApp> findInstall)
{
    public RemovalPlan PlanHistoryRemoval(uint appId, IReadOnlyCollection<(uint DepotId, ulong ManifestId)> entries)
    {
        var set = entries.ToHashSet();
        var versions = library.VersionsFor(appId).Where(v => !v.Adopted && v.Manifests.Exists(m => set.Contains((m.DepotId, m.ManifestId)))).ToList();
        var activeId = library.GetApp(appId)?.ActiveVersionId;
        return new RemovalPlan(versions, versions.Find(v => v.Id == activeId), false);
    }

    public RemovalPlan PlanVersionRemoval(string versionId)
    {
        var v = library.Find(versionId);
        if (v is null)
            return new RemovalPlan([], null, false);
        return new RemovalPlan([v], library.GetApp(v.AppId)?.ActiveVersionId == v.Id ? v : null, v.Adopted);
    }

    public RemovalPlan PlanAppRemoval(uint appId)
    {
        var lib = library.GetApp(appId);
        var versions = library.VersionsFor(appId);
        return new RemovalPlan(versions, versions.FirstOrDefault(v => v.Id == lib?.ActiveVersionId), lib?.AdoptedVersionId is not null);
    }

    public async Task<SwitchReport> DeleteVersionAsync(string versionId, ISwitchPrompts prompts, CancellationToken ct = default)
    {
        var v = library.Find(versionId);
        if (v is null)
            return new SwitchReport { Success = true };
        await StopJobsAsync(j => j.TargetVersionId == versionId).ConfigureAwait(false);
        var installed = findInstall(v.AppId);
        var lib = library.GetApp(v.AppId);
        if (v.Adopted)
        {
            if (installed is not null)
                return await createSwitcher(prompts).ReleaseAsync(v.AppId, installed, detachShared: true, ct).ConfigureAwait(false);
            library.UpdateApp(v.AppId, a =>
            {
                a.AdoptedVersionId = null;
                if (a.ActiveVersionId == versionId)
                    a.ActiveVersionId = null;
            });
        }
        else if (lib?.ActiveVersionId == versionId)
        {
            var report = await ResetAsync(v.AppId, installed, lib, prompts, ct).ConfigureAwait(false);
            if (!report.Success)
                return report;
        }
        Delete(versionId);
        return new SwitchReport { Success = true };
    }

    public async Task<SwitchReport> DeleteHistoryAsync(uint appId, IReadOnlyCollection<(uint DepotId, ulong ManifestId)> entries, ISwitchPrompts prompts, CancellationToken ct = default)
    {
        var plan = PlanHistoryRemoval(appId, entries);
        if (plan.Active is not null)
        {
            var report = await DeleteVersionAsync(plan.Active.Id, prompts, ct).ConfigureAwait(false);
            if (!report.Success)
                return report;
        }
        foreach (var v in plan.Versions)
        {
            if (v != plan.Active)
                await DeleteVersionAsync(v.Id, prompts, ct).ConfigureAwait(false);
        }
        var set = entries.ToHashSet();
        var app = apps.Get(appId);
        if (app.History.RemoveAll(h => set.Contains((h.DepotId, h.ManifestId))) > 0)
            apps.Save(app, debounced: false);
        return new SwitchReport { Success = true };
    }

    public async Task<SwitchReport> RemoveAppAsync(uint appId, ISwitchPrompts prompts, CancellationToken ct = default)
    {
        await StopJobsAsync(j => j.AppId == appId).ConfigureAwait(false);
        var installed = findInstall(appId);
        var lib = library.GetApp(appId);
        var app = apps.Get(appId);
        if (installed is not null && (lib?.AdoptedVersionId is not null || lib?.ActiveVersionId is not null || app.Install is { Mode: not InstallMode.None }))
        {
            var report = await createSwitcher(prompts).ReleaseAsync(appId, installed, detachShared: false, ct).ConfigureAwait(false);
            if (!report.Success)
                return report;
        }
        library.UpdateApp(appId, a =>
        {
            a.ActiveVersionId = null;
            a.AdoptedVersionId = null;
        });
        foreach (var v in library.VersionsFor(appId))
            Delete(v.Id);
        library.RemoveApp(appId);
        apps.Delete(appId);
        return new SwitchReport { Success = true };
    }

    private async Task<SwitchReport> ResetAsync(uint appId, InstalledApp installed, LibraryApp lib, ISwitchPrompts prompts, CancellationToken ct)
    {
        if (installed is null)
        {
            library.SetActive(appId, null);
            return new SwitchReport { Success = true };
        }
        var switcher = createSwitcher(prompts);
        return lib.AdoptedVersionId is not null
            ? await switcher.RevertAsync(appId, installed, ct).ConfigureAwait(false)
            : await switcher.ReleaseAsync(appId, installed, detachShared: false, ct).ConfigureAwait(false);
    }

    private void Delete(string versionId)
    {
        index?.RemoveVersion(versionId);
        library.DeleteVersion(versionId);
    }

    private async Task StopJobsAsync(Func<DownloadJob, bool> match)
    {
        if (queue is null)
            return;
        var jobs = queue.Jobs.Where(match).ToList();
        foreach (var j in jobs)
            queue.Cancel(j.Id);
        foreach (var j in jobs)
        {
            await queue.WhenStoppedAsync(j.Id).ConfigureAwait(false);
            queue.Remove(j.Id);
        }
    }
}
