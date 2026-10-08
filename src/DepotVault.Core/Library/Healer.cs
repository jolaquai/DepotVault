using DepotVault.Core.Download;
using DepotVault.Core.Linking;
using SteamKit2;

namespace DepotVault.Core.Library;

public sealed class HealReport
{
    public List<string> Benign { get; } = [];
    public List<string> Restored { get; } = [];
    public List<string> Refetched { get; } = [];
    public List<string> KeptDiverged { get; } = [];
    public List<string> SkippedShared { get; } = [];
    public List<string> Failed { get; } = [];

    public bool HasNewDivergence => KeptDiverged.Count > 0;
}

public sealed class Healer(LibraryIndex library, AppRepository apps, ContentIndex index, Linker linker, Func<uint, IChunkSource> sources = null)
{
    private readonly record struct ManifestEntry(uint DepotId, DepotManifest.FileData File);

    public async Task<HealReport> HealAsync(uint appId, IReadOnlyList<IntegrityIssue> issues, CancellationToken ct = default)
    {
        var report = new HealReport();
        if (issues.Count == 0)
            return report;
        var app = apps.Get(appId);
        var active = library.GetApp(appId)?.ActiveVersionId;
        var manifests = new Dictionary<string, Dictionary<string, ManifestEntry>>();
        var states = new Dictionary<string, VersionState>();

        foreach (var group in issues.OrderBy(i => i.VersionId == active ? 1 : 0).GroupBy(i => i.VersionId))
        {
            var version = library.Find(group.Key);
            if (version is null)
                continue;
            var files = manifests[version.Id] = LoadManifests(version);
            var state = states[version.Id] = VersionStateStore.Load(library.Paths.VersionStateFile(version.Id));
            var dir = library.GetVersionDir(version);

            foreach (var issue in group)
            {
                ct.ThrowIfCancellationRequested();
                var label = $"{version.Id}:{issue.RelPath}";
                var full = Path.Combine(dir, issue.RelPath);
                if (!files.TryGetValue(issue.RelPath, out var entry))
                {
                    report.Failed.Add(label);
                    continue;
                }
                if (app.GetDecision(issue.RelPath) == MutableDecision.Share)
                {
                    if (issue.Kind == IssueKind.Changed)
                        UpdateSnapshot(state, issue.RelPath, full);
                    report.SkippedShared.Add(label);
                    continue;
                }
                if (issue.Kind == IssueKind.Changed && await ChunkPipeline.HashMatchesAsync(full, entry.File.FileHash, ct).ConfigureAwait(false))
                {
                    UpdateSnapshot(state, issue.RelPath, full);
                    report.Benign.Add(label);
                    continue;
                }
                if (issue.Kind == IssueKind.Changed && version.Id == active)
                {
                    UpdateSnapshot(state, issue.RelPath, full, LinkKind.None);
                    MarkDiverged(app, issue.RelPath);
                    report.KeptDiverged.Add(label);
                    continue;
                }

                FileUtil.ForceDelete(full);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                if (issue.Kind == IssueKind.Changed)
                    MarkDiverged(app, issue.RelPath);

                var kind = RestoreFromSibling(version, entry.File, full);
                if (kind != LinkKind.None)
                {
                    UpdateSnapshot(state, issue.RelPath, full, kind);
                    report.Restored.Add(label);
                    continue;
                }
                if (sources is not null && await RefetchAsync(appId, entry, issue.RelPath, dir, ct).ConfigureAwait(false))
                {
                    UpdateSnapshot(state, issue.RelPath, full, LinkKind.None);
                    report.Refetched.Add(label);
                    continue;
                }
                report.Failed.Add(label);
            }
        }

        foreach (var (versionId, state) in states)
            VersionStateStore.Save(library.Paths.VersionStateFile(versionId), state);
        apps.Save(app);
        return report;
    }

    private static void MarkDiverged(AppRecord app, string relPath)
    {
        app.Exclusions ??= [];
        app.ReviewCandidates ??= [];
        if (!app.Exclusions.Contains(relPath, StringComparer.OrdinalIgnoreCase))
            app.Exclusions.Add(relPath);
        if (app.GetDecision(relPath) == MutableDecision.Unreviewed && !app.ReviewCandidates.Contains(relPath, StringComparer.OrdinalIgnoreCase))
            app.ReviewCandidates.Add(relPath);
    }

    private LinkKind RestoreFromSibling(VersionRecord version, DepotManifest.FileData file, string target)
    {
        if (file.FileHash is not { Length: 20 })
            return LinkKind.None;
        foreach (var r in index.Lookup(new Sha1Hash(file.FileHash), file.TotalSize))
        {
            if (r.VersionId == version.Id)
                continue;
            var v = library.Find(r.VersionId);
            if (v is null)
                continue;
            var candidate = Path.Combine(library.GetVersionDir(v), r.RelPath);
            if (!File.Exists(candidate) || new FileInfo(candidate).Length != (long)file.TotalSize)
                continue;
            if (!ChunkPipeline.HashMatchesAsync(candidate, file.FileHash, CancellationToken.None).GetAwaiter().GetResult())
                continue;
            try
            {
                var kind = linker.Link(candidate, target, new LinkRules(AllowHardlink: false, AllowCopy: true));
                if (kind != LinkKind.None)
                    return kind;
            }
            catch (IOException) { }
        }
        return LinkKind.None;
    }

    private async Task<bool> RefetchAsync(uint appId, ManifestEntry entry, string relPath, string dir, CancellationToken ct)
    {
        var plan = new FilePlan { File = entry.File, RelPath = relPath, Action = FileAction.Fetch };
        try
        {
            await new ChunkPipeline().RunAsync(entry.DepotId, [plan], dir, new JobCounters(), new PipelineOptions { Source = sources(appId), MaxConcurrentChunks = 4 }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException)
        {
            return false;
        }
    }

    private static FileSnapshot UpdateSnapshot(VersionState state, string relPath, string full)
    {
        var snap = state.Files.Find(f => string.Equals(f.RelPath, relPath, StringComparison.OrdinalIgnoreCase));
        if (snap is null)
            return null;
        var info = new FileInfo(full);
        snap.Size = info.Length;
        snap.LastWriteUtc = info.LastWriteTimeUtc;
        return snap;
    }

    private static void UpdateSnapshot(VersionState state, string relPath, string full, LinkKind kind)
    {
        if (UpdateSnapshot(state, relPath, full) is { } snap)
            snap.Link = kind;
    }

    private Dictionary<string, ManifestEntry> LoadManifests(VersionRecord version)
    {
        var result = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in version.Manifests)
        {
            var path = library.Paths.ManifestFile(version.Id, m.DepotId);
            if (!File.Exists(path))
                continue;
            var manifest = DepotManifest.LoadFromFile(path);
            if (manifest is null)
                continue;
            foreach (var f in manifest.Files)
            {
                if ((f.Flags & EDepotFileFlag.Directory) == 0)
                    result[FilePlanner.NormalizeRelPath(f.FileName)] = new ManifestEntry(m.DepotId, f);
            }
        }
        return result;
    }
}
