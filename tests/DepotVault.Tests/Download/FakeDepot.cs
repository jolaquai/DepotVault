using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DepotVault.Core;
using DepotVault.Core.Download;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotVault.Tests.Download;

public sealed class FakeDepot : IChunkSource
{
    private readonly ConcurrentDictionary<Sha1Hash, byte[]> _chunks = new();
    private int _fetches;

    public int Fetches => Volatile.Read(ref _fetches);
    public Func<DepotManifest.ChunkData, bool> Corrupt { get; set; }
    public Func<int, bool> FailAt { get; set; }
    public Action<int> OnFetch { get; set; }

    public static byte[] Bytes(string s, int repeat = 1) => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(s, repeat)));

    public DepotManifest Build(uint depotId, ulong manifestId, int chunkSize, params (string Name, byte[] Content)[] files)
    {
        var m = new DepotManifest { DepotID = depotId, ManifestGID = manifestId, Files = [], CreationTime = DateTime.UtcNow };
        foreach (var (name, content) in files)
        {
            if (content is null)
            {
                m.Files.Add(new DepotManifest.FileData(name, new byte[20], EDepotFileFlag.Directory, 0, new byte[20], null, false, 0));
                continue;
            }
            var fd = new DepotManifest.FileData(name, SHA1.HashData(Encoding.UTF8.GetBytes(name)), 0, (ulong)content.Length, SHA1.HashData(content), null, false, 0);
            for (var off = 0; off < content.Length; off += chunkSize)
            {
                var part = content.AsSpan(off, Math.Min(chunkSize, content.Length - off)).ToArray();
                var id = SHA1.HashData(part);
                _chunks[new Sha1Hash(id)] = part;
                fd.Chunks.Add(new DepotManifest.ChunkData(id, DepotChunk.AdlerHash(part), (ulong)off, (uint)part.Length + 7, (uint)part.Length));
            }
            m.Files.Add(fd);
        }
        return m;
    }

    public Task<int> FetchAsync(uint depotId, DepotManifest.ChunkData chunk, byte[] destination, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _fetches);
        if (FailAt?.Invoke(n) == true)
            throw new IOException("simulated CDN failure");
        OnFetch?.Invoke(n);
        ct.ThrowIfCancellationRequested();
        var data = _chunks[new Sha1Hash(chunk.ChunkID)];
        data.CopyTo(destination, 0);
        if (Corrupt?.Invoke(chunk) == true)
            destination[0] ^= 0xFF;
        return Task.FromResult(data.Length);
    }
}
