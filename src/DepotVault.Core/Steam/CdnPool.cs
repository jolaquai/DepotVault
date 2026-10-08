using SteamKit2.CDN;

namespace DepotVault.Core.Steam;

public sealed record CdnServer(Server Server, string Host, string Type, float WeightedLoad, uint[] AllowedAppIds, bool SteamChinaOnly, bool UseAsProxy)
{
    public static CdnServer From(Server s) => new(s, s.Host, s.Type, s.WeightedLoad, s.AllowedAppIds, s.SteamChinaOnly, s.UseAsProxy);
}

public interface ICdnServerSource
{
    Task<IReadOnlyList<CdnServer>> GetServersAsync(CancellationToken ct);
}

internal sealed class SteamPipeServerSource(SteamSession session) : ICdnServerSource
{
    public async Task<IReadOnlyList<CdnServer>> GetServersAsync(CancellationToken ct)
    {
        var servers = await session.Content.GetServersForSteamPipe(session.CellId == 0 ? null : session.CellId, null).WaitAsync(ct).ConfigureAwait(false);
        return servers.Select(CdnServer.From).ToArray();
    }
}

public sealed class CdnPool(ICdnServerSource source, TimeProvider time = null)
{
    private sealed class Entry(CdnServer server, int rank)
    {
        public CdnServer Server { get; } = server;
        public int Rank { get; } = rank;
        public int Failures;
        public long BlockedUntil;
        public long InFlight;
    }

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Entry[] _entries = [];
    private long _fetchedAt;

    public CdnPool(SteamSession session) : this(new SteamPipeServerSource(session)) { }

    public int Count => _entries.Length;

    public async Task<CdnServer> RentAsync(uint appId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var entries = _entries;
            if (entries.Length == 0 || _time.GetElapsedTime(_fetchedAt) > RefreshAfter)
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                entries = _entries;
                if (entries.Length == 0)
                    throw new IOException("No content servers available.");
            }

            var now = _time.GetTimestamp();
            Entry best = null;
            foreach (var e in entries)
            {
                if (Volatile.Read(ref e.BlockedUntil) > now || !Allowed(e.Server, appId))
                    continue;
                if (best is null || Score(e) < Score(best))
                    best = e;
            }
            if (best is not null)
            {
                Interlocked.Increment(ref best.InFlight);
                return best.Server;
            }
            if (attempt >= 1)
            {
                long soonest = long.MaxValue;
                foreach (var e in entries)
                    soonest = Math.Min(soonest, Volatile.Read(ref e.BlockedUntil));
                var wait = soonest == long.MaxValue ? TimeSpan.FromSeconds(1) : _time.GetElapsedTime(now, soonest);
                await Task.Delay(wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait, _time, ct).ConfigureAwait(false);
            }
            else
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public void Return(CdnServer server, bool success)
    {
        var e = Find(server);
        if (e is null)
            return;
        Interlocked.Decrement(ref e.InFlight);
        if (success)
        {
            if (Volatile.Read(ref e.Failures) > 0)
                Interlocked.Decrement(ref e.Failures);
            return;
        }
        var failures = Interlocked.Increment(ref e.Failures);
        var backoff = TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(failures, 6)));
        Volatile.Write(ref e.BlockedUntil, _time.GetTimestamp() + (long)(backoff.TotalSeconds * _time.TimestampFrequency));
    }

    private static bool Allowed(CdnServer s, uint appId) => s.AllowedAppIds is not { Length: > 0 } allowed || Array.IndexOf(allowed, appId) >= 0;

    private static long Score(Entry e) => e.Rank + Volatile.Read(ref e.Failures) * 100L + Volatile.Read(ref e.InFlight) * 2;

    private Entry Find(CdnServer server)
    {
        foreach (var e in _entries)
        {
            if (ReferenceEquals(e.Server, server))
                return e;
        }
        return null;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var before = _fetchedAt;
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_fetchedAt != before && _entries.Length > 0)
                return;
            var servers = await source.GetServersAsync(ct).ConfigureAwait(false);
            var ranked = servers
                .Where(s => !s.SteamChinaOnly && !s.UseAsProxy && s.Type is "SteamCache" or "CDN")
                .OrderBy(s => s.Type == "SteamCache" ? 0 : 1)
                .ThenBy(s => s.WeightedLoad)
                .Select((s, i) => new Entry(s, i))
                .ToArray();
            _entries = ranked;
            _fetchedAt = _time.GetTimestamp();
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
