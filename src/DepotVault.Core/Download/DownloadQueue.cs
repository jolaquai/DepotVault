using DepotVault.Core.Persistence;

namespace DepotVault.Core.Download;

public interface IJobRunner
{
    Task RunAsync(DownloadJob job, CancellationToken ct);
}

public sealed class DownloadQueue : IDisposable
{
    private readonly AtomicJsonStore<QueueDocument> _store;
    private readonly IJobRunner _runner;
    private readonly Func<int> _maxConcurrent;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _lock = new();
    private readonly QueueDocument _doc;
    private readonly List<Task> _running = [];
    private bool _started;

    public DownloadQueue(AtomicJsonStore<QueueDocument> store, IJobRunner runner, Func<int> maxConcurrent)
    {
        _store = store;
        _runner = runner;
        _maxConcurrent = maxConcurrent;
        _doc = store.Load();
        _doc.Jobs ??= [];
        foreach (var j in _doc.Jobs)
        {
            j.Counters ??= new JobCounters();
            if (j.State == JobState.Running)
                j.State = JobState.Queued;
        }
    }

    public event Action<DownloadJob> JobChanged;

    public IReadOnlyList<DownloadJob> Jobs
    {
        get
        {
            lock (_lock)
                return _doc.Jobs.ToArray();
        }
    }

    public DownloadJob Find(Guid id)
    {
        lock (_lock)
            return _doc.Jobs.Find(j => j.Id == id);
    }

    public void Start()
    {
        lock (_lock)
            _started = true;
        Pump();
    }

    public DownloadJob Enqueue(uint appId, uint depotId, ulong manifestId, string targetVersionId)
    {
        var job = new DownloadJob { AppId = appId, DepotId = depotId, ManifestId = manifestId, TargetVersionId = targetVersionId };
        lock (_lock)
            _doc.Jobs.Add(job);
        Changed(job);
        Pump();
        return job;
    }

    public void Pause(Guid id)
    {
        DownloadJob job;
        lock (_lock)
        {
            job = _doc.Jobs.Find(j => j.Id == id);
            if (job is null || job.State is not (JobState.Queued or JobState.Running))
                return;
            job.State = JobState.Paused;
            job.Cts?.Cancel();
        }
        Changed(job);
        Pump();
    }

    public void Resume(Guid id)
    {
        DownloadJob job;
        lock (_lock)
        {
            job = _doc.Jobs.Find(j => j.Id == id);
            if (job is null || job.State is not (JobState.Paused or JobState.Failed))
                return;
            job.State = JobState.Queued;
            job.Error = null;
            job.ErrorKind = JobErrorKind.None;
        }
        Changed(job);
        Pump();
    }

    public void Cancel(Guid id)
    {
        DownloadJob job;
        lock (_lock)
        {
            job = _doc.Jobs.Find(j => j.Id == id);
            if (job is null || job.IsFinished)
                return;
            job.State = JobState.Canceled;
            job.FinishedUtc = DateTime.UtcNow;
            job.Cts?.Cancel();
        }
        Changed(job);
        Pump();
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            var idx = _doc.Jobs.FindIndex(j => j.Id == id);
            if (idx < 0 || _doc.Jobs[idx].Cts is not null)
                return;
            _doc.Jobs.RemoveAt(idx);
        }
        Persist();
    }

    public async Task WhenStoppedAsync(Guid id)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_doc.Jobs.Find(j => j.Id == id) is not { Cts: not null })
                    return;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    public void Move(Guid id, int newIndex)
    {
        lock (_lock)
        {
            var idx = _doc.Jobs.FindIndex(j => j.Id == id);
            if (idx < 0)
                return;
            var job = _doc.Jobs[idx];
            _doc.Jobs.RemoveAt(idx);
            _doc.Jobs.Insert(Math.Clamp(newIndex, 0, _doc.Jobs.Count), job);
        }
        Persist();
        Pump();
    }

    public void Persist() => _store.ScheduleSave(Snapshot);

    private QueueDocument Snapshot()
    {
        lock (_lock)
            return new QueueDocument { SchemaVersion = _doc.SchemaVersion, Jobs = [.. _doc.Jobs] };
    }

    public Task WhenIdleAsync()
    {
        lock (_lock)
            return Task.WhenAll(_running.ToArray());
    }

    private void Pump()
    {
        List<DownloadJob> toStart = null;
        lock (_lock)
        {
            if (!_started || _lifetime.IsCancellationRequested)
                return;
            var running = 0;
            foreach (var j in _doc.Jobs)
            {
                if (j.Cts is not null)
                    running++;
            }
            var max = Math.Max(1, _maxConcurrent());
            foreach (var j in _doc.Jobs)
            {
                if (running >= max)
                    break;
                if (j.State != JobState.Queued || j.Cts is not null)
                    continue;
                j.State = JobState.Running;
                j.Cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                j.Dirty = Persist;
                (toStart ??= []).Add(j);
                running++;
            }
        }
        if (toStart is null)
            return;
        foreach (var j in toStart)
        {
            Changed(j);
            var task = Task.Run(() => RunJobAsync(j));
            lock (_lock)
            {
                _running.RemoveAll(static t => t.IsCompleted);
                _running.Add(task);
            }
        }
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        var cts = job.Cts;
        try
        {
            await _runner.RunAsync(job, cts.Token).ConfigureAwait(false);
            lock (_lock)
            {
                if (job.State == JobState.Running)
                {
                    job.FinishedUtc = DateTime.UtcNow;
                    job.State = JobState.Done;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (job.State == JobState.Running)
                    job.State = JobState.Queued;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (job.State == JobState.Running)
                {
                    (job.ErrorKind, job.Error) = JobErrors.Classify(ex);
                    job.FinishedUtc = DateTime.UtcNow;
                    job.State = JobState.Failed;
                }
            }
        }
        finally
        {
            lock (_lock)
                job.Cts = null;
            cts.Dispose();
        }
        Changed(job);
        Pump();
    }

    private void Changed(DownloadJob job)
    {
        Persist();
        JobChanged?.Invoke(job);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try { WhenIdleAsync().Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        Persist();
        _store.Flush();
        _lifetime.Dispose();
    }
}
