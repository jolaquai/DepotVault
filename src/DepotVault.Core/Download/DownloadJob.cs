using System.Text.Json.Serialization;
using DepotVault.Core.Persistence;

namespace DepotVault.Core.Download;

public enum JobState
{
    Queued,
    Running,
    Paused,
    Done,
    Failed,
    Canceled,
}

public sealed class DownloadJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public uint AppId { get; set; }
    public uint DepotId { get; set; }
    public ulong ManifestId { get; set; }
    public string TargetVersionId { get; set; }
    public JobState State { get; set; }
    public string Error { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }
    public JobCounters Counters { get; set; } = new();

    [JsonIgnore]
    internal CancellationTokenSource Cts { get; set; }

    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Canceled;
}

public sealed class QueueDocument : ISchemaVersioned
{
    public int SchemaVersion { get; set; }
    public List<DownloadJob> Jobs { get; set; } = [];
}
