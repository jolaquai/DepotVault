namespace DepotVault.Core.Download;

public sealed class JobCounters
{
    private long _total;
    private long _downloaded;
    private long _written;
    private long _deduped;
    private long _reused;

    public long TotalBytes { get => Volatile.Read(ref _total); set => Volatile.Write(ref _total, value); }
    public long DownloadedBytes { get => Volatile.Read(ref _downloaded); set => Volatile.Write(ref _downloaded, value); }
    public long WrittenBytes { get => Volatile.Read(ref _written); set => Volatile.Write(ref _written, value); }
    public long DedupedBytes { get => Volatile.Read(ref _deduped); set => Volatile.Write(ref _deduped, value); }
    public long ReusedBytes { get => Volatile.Read(ref _reused); set => Volatile.Write(ref _reused, value); }

    public long CompletedBytes => WrittenBytes + DedupedBytes + ReusedBytes;

    public void AddDownloaded(long n) => Interlocked.Add(ref _downloaded, n);
    public void AddWritten(long n) => Interlocked.Add(ref _written, n);
    public void AddDeduped(long n) => Interlocked.Add(ref _deduped, n);
    public void AddReused(long n) => Interlocked.Add(ref _reused, n);

    public void Reset(long total)
    {
        Volatile.Write(ref _total, total);
        Volatile.Write(ref _downloaded, 0);
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _deduped, 0);
        Volatile.Write(ref _reused, 0);
    }
}
