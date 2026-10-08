using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using DepotVault.Core.Library;
using Microsoft.Win32.SafeHandles;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotVault.Core.Download;

public interface IChunkSource
{
    Task<int> FetchAsync(uint depotId, DepotManifest.ChunkData chunk, byte[] destination, CancellationToken ct);
}

public interface IFileSharer
{
    bool TryShare(FilePlan plan, string targetPath, out LinkKind kind);
}

public interface IChunkTracker
{
    bool IsDone(FilePlan plan, int chunkIndex);
    void MarkDone(FilePlan plan, int chunkIndex);
    void ResetFile(FilePlan plan);
}

public sealed class PipelineOptions
{
    public required IChunkSource Source { get; init; }
    public int MaxConcurrentChunks { get; init; } = 16;
    public IFileSharer Sharer { get; init; }
    public BandwidthLimiter Limiter { get; init; }
    public IChunkTracker Tracker { get; init; }
}

public sealed class ChunkPipeline
{
    private sealed class FileWork(FilePlan plan, string fullPath, SafeFileHandle handle, int remaining) : IDisposable
    {
        public FilePlan Plan { get; } = plan;
        public string FullPath { get; } = fullPath;
        public SafeFileHandle Handle { get; } = handle;
        public int Remaining = remaining;
        private SafeFileHandle _diff;
        private readonly Lock _diffLock = new();

        public SafeFileHandle DiffHandle
        {
            get
            {
                lock (_diffLock)
                    return _diff ??= File.OpenHandle(Plan.DiffSourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            }
        }

        public void Dispose()
        {
            Handle.Dispose();
            lock (_diffLock)
                _diff?.Dispose();
        }
    }

    private readonly record struct ChunkWork(FileWork File, int Index);

    public async Task<List<FileSnapshot>> RunAsync(uint depotId, IReadOnlyList<FilePlan> plans, string targetDir, JobCounters counters, PipelineOptions options, CancellationToken ct)
    {
        long total = 0;
        foreach (var p in plans)
        {
            if (p.Action != FileAction.Directory)
                total += (long)p.Size;
        }
        counters.Reset(total);
        Directory.CreateDirectory(targetDir);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var workers = Math.Max(1, options.MaxConcurrentChunks);
        var channel = Channel.CreateBounded<ChunkWork>(new BoundedChannelOptions(workers * 4) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var opened = new ConcurrentBag<FileWork>();
        var completed = new ConcurrentQueue<FileWork>();
        var links = new Dictionary<FilePlan, LinkKind>();

        var workerTasks = new Task[workers];
        for (var i = 0; i < workers; i++)
            workerTasks[i] = Task.Run(() => WorkerAsync(depotId, channel.Reader, counters, options, completed, token), token);

        try
        {
            try
            {
                foreach (var plan in plans)
                {
                    token.ThrowIfCancellationRequested();
                    var full = Path.Combine(targetDir, plan.RelPath);
                    switch (plan.Action)
                    {
                        case FileAction.Directory:
                            Directory.CreateDirectory(full);
                            continue;
                        case FileAction.Symlink:
                            CreateSymlink(full, plan.File.LinkTarget);
                            continue;
                        case FileAction.Empty:
                            Directory.CreateDirectory(Path.GetDirectoryName(full));
                            FileUtil.ForceDelete(full);
                            using (File.Create(full)) { }
                            continue;
                        case FileAction.Share:
                            if (options.Sharer is not null && options.Sharer.TryShare(plan, full, out var kind))
                            {
                                links[plan] = kind;
                                counters.AddDeduped((long)plan.Size);
                                continue;
                            }
                            plan.Action = FileAction.Fetch;
                            break;
                    }

                    var chunks = plan.File.Chunks;
                    var pending = new List<int>(chunks.Count);
                    long resumed = 0;
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        if (options.Tracker?.IsDone(plan, i) == true)
                            resumed += chunks[i].UncompressedLength;
                        else
                            pending.Add(i);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    var resume = resumed > 0 && File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0 && new FileInfo(full).Length == (long)plan.Size;
                    if (!resume)
                    {
                        if (resumed > 0)
                        {
                            options.Tracker.ResetFile(plan);
                            pending.Clear();
                            for (var i = 0; i < chunks.Count; i++)
                                pending.Add(i);
                        }
                        FileUtil.ForceDelete(full);
                    }
                    else
                    {
                        counters.AddReused(resumed);
                    }
                    var handle = File.OpenHandle(full, resume ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous, resume ? 0 : (long)plan.Size);
                    if (RandomAccess.GetLength(handle) != (long)plan.Size)
                        RandomAccess.SetLength(handle, (long)plan.Size);
                    var fw = new FileWork(plan, full, handle, pending.Count);
                    opened.Add(fw);
                    if (pending.Count == 0)
                    {
                        fw.Dispose();
                        completed.Enqueue(fw);
                        continue;
                    }
                    foreach (var i in pending)
                        await channel.Writer.WriteAsync(new ChunkWork(fw, i), token).ConfigureAwait(false);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }

            try
            {
                await Task.WhenAll(workerTasks).ConfigureAwait(false);
            }
            catch
            {
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            try { await Task.WhenAll(workerTasks).ConfigureAwait(false); }
            catch { }
            foreach (var fw in opened)
                fw.Dispose();
        }

        await VerifyAsync(completed, options.Tracker, token).ConfigureAwait(false);
        return BuildSnapshots(depotId, plans, targetDir, links);
    }

    private static async Task WorkerAsync(uint depotId, ChannelReader<ChunkWork> reader, JobCounters counters, PipelineOptions options, ConcurrentQueue<FileWork> completed, CancellationToken ct)
    {
        await foreach (var work in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var chunk = work.File.Plan.File.Chunks[work.Index];
            var len = (int)chunk.UncompressedLength;
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(len, 1));
            try
            {
                var local = false;
                if (work.File.Plan.DiffChunks is { } diff && diff.TryGetValue(new Sha1Hash(chunk.ChunkID), out var lc) && lc.Length == chunk.UncompressedLength)
                {
                    var read = await ReadFullyAsync(work.File.DiffHandle, buffer.AsMemory(0, len), (long)lc.Offset, ct).ConfigureAwait(false);
                    local = read == len && DepotChunk.AdlerHash(buffer.AsSpan(0, len)) == chunk.Checksum;
                }

                if (!local)
                {
                    if (options.Limiter is not null)
                        await options.Limiter.WaitAsync(chunk.CompressedLength, ct).ConfigureAwait(false);
                    var n = await options.Source.FetchAsync(depotId, chunk, buffer, ct).ConfigureAwait(false);
                    if (n != len)
                        throw new InvalidDataException($"Chunk {Convert.ToHexStringLower(chunk.ChunkID)} returned {n} bytes, expected {len}.");
                    counters.AddDownloaded(chunk.CompressedLength);
                }

                await RandomAccess.WriteAsync(work.File.Handle, buffer.AsMemory(0, len), (long)chunk.Offset, ct).ConfigureAwait(false);
                if (local)
                    counters.AddReused(len);
                else
                    counters.AddWritten(len);
                options.Tracker?.MarkDone(work.File.Plan, work.Index);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (Interlocked.Decrement(ref work.File.Remaining) == 0)
            {
                work.File.Dispose();
                completed.Enqueue(work.File);
            }
        }
    }

    private static async ValueTask<int> ReadFullyAsync(SafeFileHandle handle, Memory<byte> buffer, long offset, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await RandomAccess.ReadAsync(handle, buffer[total..], offset + total, ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    private static async Task VerifyAsync(ConcurrentQueue<FileWork> files, IChunkTracker tracker, CancellationToken ct)
    {
        var bad = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, async (fw, token) =>
        {
            var expected = fw.Plan.File.FileHash;
            if (expected is not { Length: 20 })
                return;
            if (!await HashMatchesAsync(fw.FullPath, expected, token).ConfigureAwait(false))
            {
                bad.Add(fw.Plan.RelPath);
                tracker?.ResetFile(fw.Plan);
                try { FileUtil.ForceDelete(fw.FullPath); }
                catch (IOException) { }
            }
        }).ConfigureAwait(false);
        if (!bad.IsEmpty)
            throw new InvalidDataException($"{bad.Count} file(s) failed SHA-1 verification: {string.Join(", ", bad.Take(5))}");
    }

    public static async Task<bool> HashMatchesAsync(string path, ReadOnlyMemory<byte> expected, CancellationToken ct)
    {
        await using var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 0 });
        var hash = await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false);
        return hash.AsSpan().SequenceEqual(expected.Span);
    }

    private static void CreateSymlink(string full, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        if (File.Exists(full) || Directory.Exists(full))
            FileUtil.ForceDelete(full);
        try
        {
            File.CreateSymbolicLink(full, target.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not create symlink {full} -> {target}.", ex);
        }
    }

    private static List<FileSnapshot> BuildSnapshots(uint depotId, IReadOnlyList<FilePlan> plans, string targetDir, Dictionary<FilePlan, LinkKind> links)
    {
        var result = new List<FileSnapshot>(plans.Count);
        foreach (var plan in plans)
        {
            if (plan.Action is FileAction.Directory or FileAction.Symlink)
                continue;
            var full = Path.Combine(targetDir, plan.RelPath);
            if (!OperatingSystem.IsWindows() && (plan.File.Flags & EDepotFileFlag.Executable) != 0)
                File.SetUnixFileMode(full, File.GetUnixFileMode(full) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            var info = new FileInfo(full);
            result.Add(new FileSnapshot
            {
                RelPath = plan.RelPath,
                DepotId = depotId,
                Size = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Link = links.GetValueOrDefault(plan),
                Sha1 = plan.File.FileHash is { Length: 20 } h ? Convert.ToHexStringLower(h) : null,
            });
        }
        return result;
    }
}
