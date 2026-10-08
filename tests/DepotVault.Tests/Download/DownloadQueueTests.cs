using System.Collections.Concurrent;
using DepotVault.Core.Download;
using DepotVault.Core.Persistence;

namespace DepotVault.Tests.Download;

public class DownloadQueueTests
{
    private sealed class GateRunner : IJobRunner
    {
        public readonly ConcurrentDictionary<Guid, TaskCompletionSource> Gates = new();
        public int Running;
        public int MaxObserved;
        public Func<DownloadJob, Exception> Fail;

        public async Task RunAsync(DownloadJob job, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Running);
            int seen;
            while ((seen = Volatile.Read(ref MaxObserved)) < now && Interlocked.CompareExchange(ref MaxObserved, now, seen) != seen) { }
            try
            {
                if (Fail?.Invoke(job) is { } ex)
                    throw ex;
                var gate = Gates.GetOrAdd(job.Id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                await gate.Task.WaitAsync(ct);
                job.Counters.AddWritten(10);
            }
            finally
            {
                Interlocked.Decrement(ref Running);
            }
        }

        public void Release(Guid id) => Gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private static AtomicJsonStore<QueueDocument> Store(TempDir dir) => new(dir.Combine("queue.json"), JsonContext.Default.QueueDocument, debounce: TimeSpan.FromMilliseconds(20));

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++)
            await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task RespectsConcurrencyAndOrder()
    {
        using var dir = new TempDir();
        var runner = new GateRunner();
        using var store = Store(dir);
        using var q = new DownloadQueue(store, runner, () => 1);
        q.Start();
        var a = q.Enqueue(1, 10, 100, "v1");
        var b = q.Enqueue(1, 11, 101, "v1");
        await Until(() => a.State == JobState.Running);
        Assert.Equal(JobState.Queued, b.State);
        runner.Release(a.Id);
        await Until(() => b.State == JobState.Running);
        Assert.Equal(JobState.Done, a.State);
        runner.Release(b.Id);
        await Until(() => b.State == JobState.Done);
        Assert.Equal(1, runner.MaxObserved);
        Assert.Equal(10, a.Counters.WrittenBytes);
    }

    [Fact]
    public async Task PauseResumeAndCancel()
    {
        using var dir = new TempDir();
        var runner = new GateRunner();
        using var store = Store(dir);
        using var q = new DownloadQueue(store, runner, () => 2);
        q.Start();
        var a = q.Enqueue(1, 10, 100, "v1");
        var b = q.Enqueue(1, 11, 101, "v1");
        await Until(() => a.State == JobState.Running && b.State == JobState.Running);
        q.Pause(a.Id);
        await Until(() => runner.Running == 1);
        Assert.Equal(JobState.Paused, a.State);
        q.Cancel(b.Id);
        await Until(() => runner.Running == 0);
        Assert.Equal(JobState.Canceled, b.State);
        q.Resume(a.Id);
        await Until(() => a.State == JobState.Running);
        runner.Release(a.Id);
        await Until(() => a.State == JobState.Done);
    }

    [Fact]
    public async Task FailureIsRecordedAndRetryable()
    {
        using var dir = new TempDir();
        var runner = new GateRunner { Fail = _ => new IOException("boom") };
        using var store = Store(dir);
        using var q = new DownloadQueue(store, runner, () => 1);
        q.Start();
        var a = q.Enqueue(1, 10, 100, "v1");
        await Until(() => a.State == JobState.Failed);
        Assert.Equal("boom", a.Error);
        runner.Fail = null;
        q.Resume(a.Id);
        await Until(() => a.State == JobState.Running);
        runner.Release(a.Id);
        await Until(() => a.State == JobState.Done);
    }

    [Fact]
    public async Task InterruptedJobsRequeueOnReload()
    {
        using var dir = new TempDir();
        var runner = new GateRunner();
        Guid id;
        using (var store = Store(dir))
        using (var q = new DownloadQueue(store, runner, () => 1))
        {
            q.Start();
            var a = q.Enqueue(1, 10, 100, "v1");
            q.Enqueue(1, 11, 101, "v1");
            id = a.Id;
            await Until(() => a.State == JobState.Running);
        }
        using var store2 = Store(dir);
        using var q2 = new DownloadQueue(store2, runner, () => 1);
        Assert.Equal(2, q2.Jobs.Count);
        Assert.All(q2.Jobs, j => Assert.Equal(JobState.Queued, j.State));
        Assert.Equal(id, q2.Jobs[0].Id);
    }

    [Fact]
    public void MoveReorders()
    {
        using var dir = new TempDir();
        using var store = Store(dir);
        using var q = new DownloadQueue(store, new GateRunner(), () => 1);
        var a = q.Enqueue(1, 10, 100, "v1");
        var b = q.Enqueue(1, 11, 101, "v1");
        var c = q.Enqueue(1, 12, 102, "v1");
        q.Move(c.Id, 0);
        Assert.Equal([c.Id, a.Id, b.Id], q.Jobs.Select(j => j.Id));
    }
}
