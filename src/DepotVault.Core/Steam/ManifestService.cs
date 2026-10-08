using System.Net;
using SteamKit2;
using CdnClient = SteamKit2.CDN.Client;

namespace DepotVault.Core.Steam;

public sealed class ManifestUnavailableException(uint depotId, ulong manifestId, string reason) : Exception($"Manifest {manifestId} of depot {depotId} is unavailable: {reason}")
{
    public uint DepotId { get; } = depotId;
    public ulong ManifestId { get; } = manifestId;
}

public sealed class ManifestService(SteamSession session, DepotKeyCache keys, CdnPool cdn, CdnClient client)
{
    private const int MaxAttempts = 5;

    public async Task<DepotManifest> GetAsync(uint appId, uint depotId, ulong manifestId, string savePath, CancellationToken ct = default)
    {
        if (savePath is not null && File.Exists(savePath))
        {
            var cached = DepotManifest.LoadFromFile(savePath);
            if (cached is not null && !cached.FilenamesEncrypted)
                return cached;
        }

        var key = await keys.GetAsync(depotId, appId).ConfigureAwait(false);
        var manifest = await DownloadAsync(appId, depotId, manifestId, key, ct).ConfigureAwait(false);
        if (manifest.FilenamesEncrypted && !manifest.DecryptFilenames(key))
            throw new InvalidDataException($"Could not decrypt filenames of manifest {manifestId}.");

        if (savePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            var tmp = savePath + ".tmp";
            manifest.SaveToFile(tmp);
            File.Move(tmp, savePath, true);
        }
        return manifest;
    }

    private async Task<DepotManifest> DownloadAsync(uint appId, uint depotId, ulong manifestId, byte[] key, CancellationToken ct)
    {
        Exception last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var code = await session.Content.GetManifestRequestCode(depotId, appId, manifestId, "public", null).WaitAsync(ct).ConfigureAwait(false);
            if (code == 0)
                throw new ManifestUnavailableException(depotId, manifestId, "no manifest request code granted");

            var server = await cdn.RentAsync(appId, ct).ConfigureAwait(false);
            try
            {
                var manifest = await client.DownloadManifestAsync(depotId, manifestId, code, server.Server, key, null, null).WaitAsync(ct).ConfigureAwait(false);
                cdn.Return(server, true);
                return manifest;
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                cdn.Return(server, true);
                last = ex;
                if (attempt >= 1)
                    throw new ManifestUnavailableException(depotId, manifestId, $"CDN returned {(int)ex.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                cdn.Return(server, false);
                last = ex;
            }
        }
        throw new IOException($"Failed to download manifest {manifestId} of depot {depotId}.", last);
    }
}
