using DepotVault.Core.Download;
using DepotVault.Core.Linking;
using SteamKit2;

namespace DepotVault.Core.Library;

public readonly record struct ContentKey(Sha1Hash Hash, ulong Size);

public readonly record struct FileRef(string VersionId, string RelPath);

public sealed class ContentIndex(LibraryIndex library, ILinkStrategy strategy, Func<string, int> maxHardlinks = null) : IContentIndex
{
    private readonly Dictionary<ContentKey, List<FileRef>> _map = [];
    private readonly Dictionary<string, List<ContentKey>> _byVersion = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
                return _map.Count;
        }
    }

    public void Rebuild()
    {
        lock (_lock)
        {
            _map.Clear();
            _byVersion.Clear();
        }
        foreach (var v in library.Versions)
            AddVersion(v.Id);
    }

    public void AddVersion(string versionId)
    {
        var v = library.Find(versionId);
        if (v is null)
            return;
        foreach (var m in v.Manifests)
        {
            if (!m.Complete)
                continue;
            var path = library.Paths.ManifestFile(versionId, m.DepotId);
            if (!File.Exists(path))
                continue;
            var manifest = DepotManifest.LoadFromFile(path);
            if (manifest is not null)
                AddManifest(versionId, manifest);
        }
    }

    public void AddManifest(string versionId, DepotManifest manifest)
    {
        lock (_lock)
        {
            if (!_byVersion.TryGetValue(versionId, out var keys))
                _byVersion[versionId] = keys = [];
            foreach (var f in manifest.Files)
            {
                if ((f.Flags & (EDepotFileFlag.Directory | EDepotFileFlag.Symlink)) != 0 || f.TotalSize == 0 || f.FileHash is not { Length: 20 })
                    continue;
                var key = new ContentKey(new Sha1Hash(f.FileHash), f.TotalSize);
                var rel = FilePlanner.NormalizeRelPath(f.FileName);
                if (!_map.TryGetValue(key, out var refs))
                    _map[key] = refs = new List<FileRef>(1);
                var r = new FileRef(versionId, rel);
                if (!refs.Contains(r))
                {
                    refs.Add(r);
                    keys.Add(key);
                }
            }
        }
    }

    public void RemoveVersion(string versionId)
    {
        lock (_lock)
        {
            if (!_byVersion.Remove(versionId, out var keys))
                return;
            foreach (var key in keys)
            {
                if (!_map.TryGetValue(key, out var refs))
                    continue;
                refs.RemoveAll(r => r.VersionId == versionId);
                if (refs.Count == 0)
                    _map.Remove(key);
            }
        }
    }

    public IReadOnlyList<FileRef> Lookup(Sha1Hash hash, ulong size)
    {
        lock (_lock)
            return _map.TryGetValue(new ContentKey(hash, size), out var refs) ? refs.ToArray() : [];
    }

    public bool TryFindSource(Sha1Hash hash, ulong size, string targetVersionId, out ContentRef source)
    {
        source = default;
        var target = library.Find(targetVersionId);
        if (target is null)
            return false;
        var limit = maxHardlinks?.Invoke(target.Root) ?? strategy.DefaultMaxHardlinks;
        foreach (var r in Lookup(hash, size))
        {
            if (r.VersionId == targetVersionId)
                continue;
            var v = library.Find(r.VersionId);
            if (v is null || !string.Equals(v.Root, target.Root, StringComparison.OrdinalIgnoreCase))
                continue;
            var full = Path.Combine(library.GetVersionDir(v), r.RelPath);
            FileInfo info;
            try
            {
                info = new FileInfo(full);
                if (!info.Exists || info.Length != (long)size || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (strategy.GetFileIdentity(full).LinkCount >= limit)
                    continue;
            }
            catch (IOException)
            {
                continue;
            }
            source = new ContentRef(r.VersionId, r.RelPath, full);
            return true;
        }
        return false;
    }
}
