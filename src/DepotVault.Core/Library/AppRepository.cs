using System.Collections.Concurrent;
using DepotVault.Core.Persistence;

namespace DepotVault.Core.Library;

public sealed class AppRepository(AppPaths paths) : IDisposable
{
    private readonly ConcurrentDictionary<uint, (AtomicJsonStore<AppRecord> Store, AppRecord Record)> _cache = new();

    public AppRecord Get(uint appId) => Entry(appId).Record;

    public bool Exists(uint appId) => _cache.ContainsKey(appId) || File.Exists(paths.AppFile(appId));

    public IEnumerable<uint> EnumerateStored()
    {
        if (!Directory.Exists(paths.AppsDir))
            yield break;
        foreach (var f in Directory.EnumerateFiles(paths.AppsDir, "*.json"))
        {
            if (uint.TryParse(Path.GetFileNameWithoutExtension(f), out var id))
                yield return id;
        }
    }

    public void Save(AppRecord record, bool debounced = true)
    {
        var e = Entry(record.AppId);
        if (!ReferenceEquals(e.Record, record))
            _cache[record.AppId] = e = (e.Store, record);
        if (debounced)
            e.Store.ScheduleSave(record);
        else
            e.Store.Save(record);
    }

    private (AtomicJsonStore<AppRecord> Store, AppRecord Record) Entry(uint appId) => _cache.GetOrAdd(appId, static (id, p) =>
    {
        var store = new AtomicJsonStore<AppRecord>(p.AppFile(id), JsonContext.Default.AppRecord);
        var rec = store.Load();
        rec.AppId = id;
        rec.Depots ??= [];
        rec.History ??= [];
        return (store, rec);
    }, paths);

    public void Flush()
    {
        foreach (var e in _cache.Values)
            e.Store.Flush();
    }

    public void Dispose()
    {
        foreach (var e in _cache.Values)
            e.Store.Dispose();
        _cache.Clear();
    }
}
