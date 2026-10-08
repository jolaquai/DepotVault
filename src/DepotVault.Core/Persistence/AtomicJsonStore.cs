using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace DepotVault.Core.Persistence;

public sealed class AtomicJsonStore<T> : IAsyncDisposable, IDisposable where T : class, ISchemaVersioned, new()
{
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly IJsonMigrator _migrator;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _pendingLock = new();
    private readonly Timer _timer;
    private Func<T> _pending;
    private long _pendingSince;
    private int _writeCount;

    public AtomicJsonStore(string path, JsonTypeInfo<T> typeInfo, IJsonMigrator migrator = null, TimeSpan debounce = default)
    {
        Path = path;
        _typeInfo = typeInfo;
        _migrator = migrator;
        _debounce = debounce > TimeSpan.Zero ? debounce : TimeSpan.FromMilliseconds(500);
        _timer = new Timer(static s => ((AtomicJsonStore<T>)s).FlushPendingCore(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public string Path { get; }
    internal int WriteCount => Volatile.Read(ref _writeCount);
    private int CurrentVersion => _migrator?.CurrentVersion ?? 1;

    public T Load()
    {
        if (!File.Exists(Path))
            return new T { SchemaVersion = CurrentVersion };
        var bytes = File.ReadAllBytes(Path);
        if (bytes.Length == 0)
            return new T { SchemaVersion = CurrentVersion };
        var value = JsonSerializer.Deserialize(bytes, _typeInfo) ?? new T();
        if (_migrator is null || value.SchemaVersion >= _migrator.CurrentVersion)
            return value;
        var node = JsonNode.Parse(bytes).AsObject();
        _migrator.Migrate(node, value.SchemaVersion);
        value = node.Deserialize(_typeInfo) ?? new T();
        value.SchemaVersion = _migrator.CurrentVersion;
        return value;
    }

    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { return Load(); }
        finally { _writeLock.Release(); }
    }

    public void Save(T value)
    {
        CancelPending();
        _writeLock.Wait();
        try { WriteCore(value); }
        finally { _writeLock.Release(); }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        CancelPending();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { WriteCore(value); }
        finally { _writeLock.Release(); }
    }

    public void ScheduleSave(T value) => ScheduleSave(() => value);

    public void ScheduleSave(Func<T> snapshot)
    {
        lock (_pendingLock)
        {
            var starving = _pending is not null && System.Diagnostics.Stopwatch.GetElapsedTime(_pendingSince) > _debounce * 4;
            if (_pending is null)
                _pendingSince = System.Diagnostics.Stopwatch.GetTimestamp();
            _pending = snapshot;
            if (!starving)
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush() => FlushPendingCore();

    private void CancelPending()
    {
        lock (_pendingLock)
        {
            _pending = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void FlushPendingCore()
    {
        Func<T> snapshot;
        lock (_pendingLock)
        {
            snapshot = _pending;
            _pending = null;
        }
        if (snapshot is null)
            return;
        _writeLock.Wait();
        try { WriteCore(snapshot()); }
        finally { _writeLock.Release(); }
    }

    private void WriteCore(T value)
    {
        if (value.SchemaVersion == 0)
            value.SchemaVersion = CurrentVersion;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 16384))
        {
            JsonSerializer.Serialize(fs, value, _typeInfo);
            fs.Flush(true);
        }
        File.Move(tmp, Path, true);
        Interlocked.Increment(ref _writeCount);
    }

    public void Dispose()
    {
        _timer.Dispose();
        FlushPendingCore();
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
        FlushPendingCore();
    }
}
