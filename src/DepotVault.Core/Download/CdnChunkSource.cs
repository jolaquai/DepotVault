using DepotVault.Core.Steam;
using SteamKit2;
using CdnClient = SteamKit2.CDN.Client;

namespace DepotVault.Core.Download;

public sealed class CdnChunkSource(uint appId, CdnPool pool, CdnClient client, DepotKeyCache keys) : IChunkSource
{
    private const int MaxAttempts = 6;

    public async Task<int> FetchAsync(uint depotId, DepotManifest.ChunkData chunk, byte[] destination, CancellationToken ct)
    {
        var key = await keys.GetAsync(depotId, appId).ConfigureAwait(false);
        Exception last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var server = await pool.RentAsync(appId, ct).ConfigureAwait(false);
            try
            {
                var n = await client.DownloadDepotChunkAsync(depotId, chunk, server.Server, destination, key, null, null).WaitAsync(ct).ConfigureAwait(false);
                pool.Return(server, true);
                return n;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                pool.Return(server, false);
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
        throw new IOException($"Failed to download chunk {Convert.ToHexStringLower(chunk.ChunkID)} of depot {depotId}.", last);
    }
}
