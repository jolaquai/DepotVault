using System.Collections.Concurrent;
using SteamKit2;

namespace DepotVault.Core.Steam;

public sealed class DepotAccessException(uint depotId, EResult result) : Exception($"No access to depot {depotId}: {result}")
{
    public uint DepotId { get; } = depotId;
    public EResult Result { get; } = result;
}

public sealed class DepotKeyCache(SteamSession session)
{
    private readonly ConcurrentDictionary<uint, Lazy<Task<byte[]>>> _keys = new();

    public Task<byte[]> GetAsync(uint depotId, uint appId)
    {
        var lazy = _keys.GetOrAdd(depotId, id => new Lazy<Task<byte[]>>(() => FetchAsync(id, appId)));
        var task = lazy.Value;
        if (task.IsFaulted || task.IsCanceled)
            _keys.TryRemove(new KeyValuePair<uint, Lazy<Task<byte[]>>>(depotId, lazy));
        return task;
    }

    private async Task<byte[]> FetchAsync(uint depotId, uint appId)
    {
        var cb = await session.Apps.GetDepotDecryptionKey(depotId, appId).ToTask().ConfigureAwait(false);
        if (cb.Result != EResult.OK)
        {
            _keys.TryRemove(depotId, out _);
            throw new DepotAccessException(depotId, cb.Result);
        }
        return cb.DepotKey;
    }
}
