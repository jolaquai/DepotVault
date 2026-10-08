using System.Collections.Concurrent;
using DepotVault.Core.Download;
using DepotVault.Core.Linking;

namespace DepotVault.Core.Library;

public sealed class Deduper(LibraryIndex library, Linker linker)
{
    private sealed record StateCache(DateTime Stamp, Dictionary<string, FileSnapshot> Files);

    private readonly ConcurrentDictionary<string, StateCache> _states = new();

    public IFileSharer ForVersion(Func<string, bool> isolate) => new Sharer(this, isolate);

    public bool IsIntact(ContentRef source, FilePlan plan)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(source.FullPath);
            if (!info.Exists || info.Length != (long)plan.Size)
                return false;
        }
        catch (IOException)
        {
            return false;
        }
        var snapshots = GetState(source.VersionId);
        if (snapshots.TryGetValue(source.RelPath, out var snap) && snap.Size == info.Length && snap.LastWriteUtc == info.LastWriteTimeUtc)
            return true;
        return plan.File.FileHash is { Length: 20 } hash && ChunkPipeline.HashMatchesAsync(source.FullPath, hash, CancellationToken.None).GetAwaiter().GetResult();
    }

    private Dictionary<string, FileSnapshot> GetState(string versionId)
    {
        var path = library.Paths.VersionStateFile(versionId);
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (_states.TryGetValue(versionId, out var cached) && cached.Stamp == stamp)
            return cached.Files;
        var files = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in VersionStateStore.Load(path).Files)
            files[f.RelPath] = f;
        _states[versionId] = new StateCache(stamp, files);
        return files;
    }

    private sealed class Sharer(Deduper owner, Func<string, bool> isolate) : IFileSharer
    {
        public bool TryShare(FilePlan plan, string targetPath, out LinkKind kind)
        {
            kind = LinkKind.None;
            if (!owner.IsIntact(plan.ShareSource, plan))
                return false;
            FileUtil.ForceDelete(targetPath);
            var rules = isolate?.Invoke(plan.RelPath) == true ? new LinkRules(AllowHardlink: false) : new LinkRules();
            try
            {
                kind = owner.Link(plan.ShareSource.FullPath, targetPath, rules);
            }
            catch (IOException)
            {
                return false;
            }
            return kind != LinkKind.None;
        }
    }

    private LinkKind Link(string source, string target, LinkRules rules) => linker.Link(source, target, rules);
}
