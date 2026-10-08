using DepotVault.Core.Library;
using SteamKit2;

namespace DepotVault.Core.Steam;

public sealed class DepotMetadataService(SteamSession session, AppRepository apps)
{
    private readonly Dictionary<uint, ulong> _appTokens = [];

    public async Task<ulong> GetAppAccessTokenAsync(uint appId)
    {
        lock (_appTokens)
        {
            if (_appTokens.TryGetValue(appId, out var cached))
                return cached;
        }
        var tokens = await session.Apps.PICSGetAccessTokens(appId, null).ToTask().ConfigureAwait(false);
        tokens.AppTokens.TryGetValue(appId, out var token);
        lock (_appTokens)
            _appTokens[appId] = token;
        return token;
    }

    public async Task<KeyValue> GetAppInfoAsync(uint appId)
    {
        var token = await GetAppAccessTokenAsync(appId).ConfigureAwait(false);
        var result = await session.Apps.PICSGetProductInfo(new SteamApps.PICSRequest(appId, token), null, false).ToTask().ConfigureAwait(false);
        foreach (var cb in result.Results)
        {
            if (cb.Apps.TryGetValue(appId, out var info))
                return info.KeyValues;
        }
        throw new InvalidOperationException($"App {appId} not returned by PICS.");
    }

    public async Task<AppRecord> RefreshAsync(uint appId, CancellationToken ct = default)
    {
        var kv = await GetAppInfoAsync(appId).WaitAsync(ct).ConfigureAwait(false);
        var meta = DepotMetadataParser.Parse(appId, kv);
        var rec = apps.Get(appId);
        rec.Name = meta.Name;
        rec.InstallDir = meta.InstallDir;
        rec.PublicBuildId = meta.PublicBuildId;
        rec.Depots = meta.Depots;
        rec.MetadataFetchedUtc = DateTime.UtcNow;
        apps.Save(rec);
        return rec;
    }

    public async Task<AppRecord> GetOrRefreshAsync(uint appId, TimeSpan maxAge, CancellationToken ct = default)
    {
        var rec = apps.Get(appId);
        if (rec.Depots.Count > 0 && DateTime.UtcNow - rec.MetadataFetchedUtc < maxAge)
            return rec;
        return await RefreshAsync(appId, ct).ConfigureAwait(false);
    }
}
