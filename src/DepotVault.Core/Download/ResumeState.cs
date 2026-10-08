using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SteamKit2.CDN;

namespace DepotVault.Core.Download;

public sealed class ResumeTracker : IChunkTracker, IDisposable
{
    private readonly ConcurrentDictionary<string, int[]> _bits = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dir;
    private readonly Action _dirty;
    private string _openPath;
    private SafeFileHandle _open;
    private long _openLength;

    public ResumeTracker(Dictionary<string, byte[]> saved, string versionDir, Action dirty)
    {
        _dir = versionDir;
        _dirty = dirty;
        if (saved is null)
            return;
        foreach (var (path, bytes) in saved)
        {
            var words = new int[(bytes.Length + 3) / 4];
            bytes.CopyTo(MemoryMarshal.AsBytes(words.AsSpan()));
            _bits[path] = words;
        }
    }

    public bool IsDone(FilePlan plan, int chunkIndex)
    {
        if (!_bits.TryGetValue(plan.RelPath, out var words) || chunkIndex >> 5 >= words.Length || (Volatile.Read(ref words[chunkIndex >> 5]) & (1 << (chunkIndex & 31))) == 0)
            return false;
        if (VerifyChunk(plan, chunkIndex))
            return true;
        Interlocked.And(ref words[chunkIndex >> 5], ~(1 << (chunkIndex & 31)));
        _dirty?.Invoke();
        return false;
    }

    public void MarkDone(FilePlan plan, int chunkIndex)
    {
        var words = _bits.GetOrAdd(plan.RelPath, static (_, n) => new int[(n + 31) >> 5], plan.File.Chunks.Count);
        Interlocked.Or(ref words[chunkIndex >> 5], 1 << (chunkIndex & 31));
        _dirty?.Invoke();
    }

    public void ResetFile(FilePlan plan)
    {
        if (_bits.TryRemove(plan.RelPath, out _))
            _dirty?.Invoke();
    }

    public Dictionary<string, byte[]> Export()
    {
        var result = new Dictionary<string, byte[]>(_bits.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, words) in _bits)
            result[path] = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
        return result;
    }

    private bool VerifyChunk(FilePlan plan, int index)
    {
        var full = Path.Combine(_dir, plan.RelPath);
        if (!string.Equals(_openPath, full, StringComparison.OrdinalIgnoreCase))
        {
            _open?.Dispose();
            _open = null;
            _openPath = full;
            if (!File.Exists(full))
                return false;
            try
            {
                _open = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _openLength = RandomAccess.GetLength(_open);
            }
            catch (IOException)
            {
                return false;
            }
        }
        if (_open is null || _openLength != (long)plan.Size)
            return false;

        var chunk = plan.File.Chunks[index];
        var len = (int)chunk.UncompressedLength;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, len));
        try
        {
            var total = 0;
            while (total < len)
            {
                var n = RandomAccess.Read(_open, buffer.AsSpan(total, len - total), (long)chunk.Offset + total);
                if (n == 0)
                    return false;
                total += n;
            }
            return DepotChunk.AdlerHash(buffer.AsSpan(0, len)) == chunk.Checksum;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        _open?.Dispose();
        _open = null;
        _openPath = null;
    }
}
