using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;

namespace DepotVault.Core.Library;

public sealed class LibraryIndex : IDisposable
{
    private readonly AtomicJsonStore<LibraryDocument> _store;
    private readonly AppPaths _paths;
    private readonly Lock _lock = new();
    private readonly LibraryDocument _doc;

    public LibraryIndex(AppPaths paths)
    {
        _paths = paths;
        _store = new AtomicJsonStore<LibraryDocument>(paths.Library, JsonContext.Default.LibraryDocument);
        _doc = _store.Load();
        _doc.Apps ??= [];
        _doc.Versions ??= [];
    }

    public event Action Changed;

    public AppPaths Paths => _paths;

    public IReadOnlyList<LibraryApp> Apps
    {
        get
        {
            lock (_lock)
                return _doc.Apps.ToArray();
        }
    }

    public IReadOnlyList<VersionRecord> Versions
    {
        get
        {
            lock (_lock)
                return _doc.Versions.ToArray();
        }
    }

    public IReadOnlyList<VersionRecord> VersionsFor(uint appId)
    {
        lock (_lock)
            return _doc.Versions.Where(v => v.AppId == appId).OrderByDescending(v => v.CreatedUtc).ToArray();
    }

    public VersionRecord Find(string versionId)
    {
        lock (_lock)
            return _doc.Versions.Find(v => v.Id == versionId);
    }

    public LibraryApp GetApp(uint appId)
    {
        lock (_lock)
            return _doc.Apps.Find(a => a.AppId == appId);
    }

    public LibraryApp EnsureApp(uint appId, string name)
    {
        LibraryApp app;
        lock (_lock)
        {
            app = _doc.Apps.Find(a => a.AppId == appId);
            if (app is null)
                _doc.Apps.Add(app = new LibraryApp { AppId = appId, Name = name });
            else if (!string.IsNullOrEmpty(name))
                app.Name = name;
        }
        Save();
        return app;
    }

    public string GetVersionDir(VersionRecord v) => Path.Combine(v.Root, v.AppId.ToString(), v.Id);

    public VersionRecord CreateVersion(uint appId, string root, IEnumerable<(uint DepotId, ulong ManifestId)> manifests, string label = null, DateTime manifestDateUtc = default)
    {
        var v = new VersionRecord
        {
            Id = VersionRecord.NewId(),
            AppId = appId,
            Root = LibraryRoots.Normalize(root),
            Label = label,
            CreatedUtc = DateTime.UtcNow,
            ManifestDateUtc = manifestDateUtc,
            Manifests = manifests.Select(m => new DepotManifestRef { DepotId = m.DepotId, ManifestId = m.ManifestId }).ToList(),
        };
        lock (_lock)
        {
            while (_doc.Versions.Exists(x => x.Id == v.Id))
                v.Id = VersionRecord.NewId();
            _doc.Versions.Add(v);
            if (!_doc.Apps.Exists(a => a.AppId == appId))
                _doc.Apps.Add(new LibraryApp { AppId = appId });
        }
        Directory.CreateDirectory(GetVersionDir(v));
        Directory.CreateDirectory(_paths.VersionDir(v.Id));
        Save();
        return v;
    }

    public VersionRecord FindVersionWith(uint appId, IReadOnlyCollection<(uint DepotId, ulong ManifestId)> manifests)
    {
        lock (_lock)
        {
            return _doc.Versions.Find(v => v.AppId == appId && v.Manifests.Count == manifests.Count &&
                manifests.All(m => v.Manifests.Exists(x => x.DepotId == m.DepotId && x.ManifestId == m.ManifestId)));
        }
    }

    public void AddDepot(string versionId, uint depotId, ulong manifestId)
    {
        lock (_lock)
        {
            var v = _doc.Versions.Find(x => x.Id == versionId) ?? throw new KeyNotFoundException(versionId);
            var existing = v.Manifests.Find(m => m.DepotId == depotId);
            if (existing is null)
                v.Manifests.Add(new DepotManifestRef { DepotId = depotId, ManifestId = manifestId });
            else if (existing.ManifestId != manifestId)
            {
                existing.ManifestId = manifestId;
                existing.Complete = false;
            }
        }
        Save();
    }

    public void MarkDepotComplete(string versionId, uint depotId, ulong manifestId)
    {
        lock (_lock)
        {
            var v = _doc.Versions.Find(x => x.Id == versionId) ?? throw new KeyNotFoundException(versionId);
            var m = v.Manifests.Find(x => x.DepotId == depotId);
            if (m is null)
                v.Manifests.Add(m = new DepotManifestRef { DepotId = depotId, ManifestId = manifestId });
            m.ManifestId = manifestId;
            m.Complete = true;
        }
        Save();
    }

    public void Update(string versionId, Action<VersionRecord> mutate)
    {
        lock (_lock)
            mutate(_doc.Versions.Find(x => x.Id == versionId) ?? throw new KeyNotFoundException(versionId));
        Save();
    }

    public void UpdateApp(uint appId, Action<LibraryApp> mutate)
    {
        lock (_lock)
        {
            var app = _doc.Apps.Find(a => a.AppId == appId);
            if (app is null)
                _doc.Apps.Add(app = new LibraryApp { AppId = appId });
            mutate(app);
        }
        Save();
    }

    public void SetActive(uint appId, string versionId) => UpdateApp(appId, a => a.ActiveVersionId = versionId);

    public void DeleteVersion(string versionId)
    {
        VersionRecord v;
        lock (_lock)
        {
            v = _doc.Versions.Find(x => x.Id == versionId);
            if (v is null)
                return;
            var app = _doc.Apps.Find(a => a.AppId == v.AppId);
            if (app?.ActiveVersionId == versionId)
                throw new InvalidOperationException("Cannot delete the active version; switch away first.");
        }
        DeleteTree(GetVersionDir(v));
        DeleteTree(_paths.VersionDir(v.Id));
        lock (_lock)
        {
            _doc.Versions.Remove(v);
            var app = _doc.Apps.Find(a => a.AppId == v.AppId);
            if (app?.AdoptedVersionId == versionId)
                app.AdoptedVersionId = null;
        }
        Save();
    }

    public long ComputeUniqueSize(VersionRecord v, ILinkStrategy strategy)
    {
        var dir = GetVersionDir(v);
        if (!Directory.Exists(dir))
            return 0;
        var state = VersionStateStore.Load(_paths.VersionStateFile(v.Id));
        var reflinked = new HashSet<string>(state.Files.Where(f => f.Link == LinkKind.Reflink).Select(f => f.RelPath), StringComparer.OrdinalIgnoreCase);
        long unique = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (reflinked.Contains(Path.GetRelativePath(dir, file)))
                continue;
            if (strategy.GetFileIdentity(file).LinkCount <= 1)
                unique += new FileInfo(file).Length;
        }
        return unique;
    }

    public static long ComputeTotalSize(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;
        long total = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            total += f.Length;
        return total;
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        if (LinkStrategy.IsDirectoryLink(dir))
        {
            LinkStrategy.RemoveDirectoryLink(dir);
            return;
        }
        ReadOnlyProtection.Remove(dir);
        Directory.Delete(dir, true);
    }

    public void Save()
    {
        _store.ScheduleSave(Snapshot);
        Changed?.Invoke();
    }

    private LibraryDocument Snapshot()
    {
        lock (_lock)
        {
            return new LibraryDocument
            {
                SchemaVersion = _doc.SchemaVersion,
                Apps = _doc.Apps.Select(a => new LibraryApp { AppId = a.AppId, Name = a.Name, ActiveVersionId = a.ActiveVersionId, AdoptedVersionId = a.AdoptedVersionId }).ToList(),
                Versions = _doc.Versions.Select(v => new VersionRecord
                {
                    Id = v.Id, AppId = v.AppId, Label = v.Label, Notes = v.Notes, CreatedUtc = v.CreatedUtc, ManifestDateUtc = v.ManifestDateUtc,
                    BuildId = v.BuildId, Root = v.Root, Adopted = v.Adopted,
                    Manifests = v.Manifests.Select(m => new DepotManifestRef { DepotId = m.DepotId, ManifestId = m.ManifestId, Complete = m.Complete }).ToList(),
                }).ToList(),
            };
        }
    }

    public void Flush() => _store.Flush();

    public void Dispose() => _store.Dispose();
}
