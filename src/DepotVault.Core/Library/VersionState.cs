using System.Collections.Concurrent;
using DepotVault.Core.Persistence;

namespace DepotVault.Core.Library;

public enum LinkKind
{
    None,
    Hardlink,
    Reflink,
    Symlink,
    Copy,
}

public sealed class FileSnapshot
{
    public string RelPath { get; set; }
    public uint DepotId { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public LinkKind Link { get; set; }
    public string Sha1 { get; set; }
}

public sealed class VersionState : ISchemaVersioned
{
    public int SchemaVersion { get; set; }
    public List<FileSnapshot> Files { get; set; } = [];
}

public static class VersionStateStore
{
    private static readonly ConcurrentDictionary<string, Lock> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static VersionState Load(string path)
    {
        lock (Locks.GetOrAdd(path, static _ => new Lock()))
        {
            using var store = new AtomicJsonStore<VersionState>(path, JsonContext.Default.VersionState);
            var s = store.Load();
            s.Files ??= [];
            return s;
        }
    }

    public static void Save(string path, VersionState state)
    {
        lock (Locks.GetOrAdd(path, static _ => new Lock()))
        {
            using var store = new AtomicJsonStore<VersionState>(path, JsonContext.Default.VersionState);
            store.Save(state);
        }
    }

    public static void MergeDepot(string path, uint depotId, IEnumerable<FileSnapshot> files)
    {
        lock (Locks.GetOrAdd(path, static _ => new Lock()))
        {
            using var store = new AtomicJsonStore<VersionState>(path, JsonContext.Default.VersionState);
            var state = store.Load();
            state.Files ??= [];
            var incoming = files.ToList();
            var paths = new HashSet<string>(incoming.Select(f => f.RelPath), StringComparer.OrdinalIgnoreCase);
            state.Files.RemoveAll(f => f.DepotId == depotId || paths.Contains(f.RelPath));
            state.Files.AddRange(incoming);
            store.Save(state);
        }
    }
}
