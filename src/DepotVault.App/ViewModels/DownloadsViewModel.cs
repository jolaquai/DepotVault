using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core;
using DepotVault.Core.Download;

namespace DepotVault.App.ViewModels;

public partial class JobItemViewModel(DownloadJob job, DownloadsViewModel owner) : ObservableObject
{
    private const double Alpha = 0.3;
    private long _lastBytes = -1;
    private DateTime _lastTick;
    private double _speed;

    public DownloadJob Job { get; } = job;
    public DownloadsViewModel Owner { get; } = owner;

    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private string subtitle;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string progressText;

    [ObservableProperty]
    private string speedText;

    [ObservableProperty]
    private string savedText;

    [ObservableProperty]
    private string stateText;

    [ObservableProperty]
    private bool canPause;

    [ObservableProperty]
    private bool canResume;

    [ObservableProperty]
    private bool canCancel;

    [ObservableProperty]
    private bool canRemove;

    public void Tick(DateTime now)
    {
        var c = Job.Counters;
        var total = c.TotalBytes;
        var done = c.CompletedBytes;
        Progress = total > 0 ? done * 100.0 / total : Job.State == JobState.Done ? 100 : 0;
        ProgressText = total > 0 ? $"{Format.Bytes(done)} of {Format.Bytes(total)}" : "";
        var saved = c.DedupedBytes + c.ReusedBytes;
        SavedText = saved > 0 ? $"{Format.Bytes(saved)} saved by dedupe/reuse" : "";

        if (Job.State == JobState.Running)
        {
            var net = c.DownloadedBytes + c.ReusedBytes + c.DedupedBytes;
            if (_lastBytes >= 0)
            {
                var dt = (now - _lastTick).TotalSeconds;
                if (dt > 0)
                    _speed = Alpha * ((net - _lastBytes) / dt) + (1 - Alpha) * _speed;
            }
            _lastBytes = net;
            _lastTick = now;
            var eta = _speed > 1 && total > done ? TimeSpan.FromSeconds((total - done) / _speed) : TimeSpan.Zero;
            SpeedText = _speed > 1 ? $"{Format.Speed(_speed)}{(eta > TimeSpan.Zero ? $", {Format.Duration(eta)} left" : "")}" : "Starting...";
        }
        else
        {
            _lastBytes = -1;
            _speed = 0;
            SpeedText = "";
        }

        StateText = Job.State switch
        {
            JobState.Failed => $"Failed: {Job.Error}",
            JobState.Done => "Done",
            _ => Job.State.ToString(),
        };
        CanPause = Job.State is JobState.Queued or JobState.Running;
        CanResume = Job.State is JobState.Paused or JobState.Failed;
        CanCancel = !Job.IsFinished;
        CanRemove = Job.IsFinished;
    }

    [RelayCommand]
    private void Pause() => Owner.Queue.Pause(Job.Id);

    [RelayCommand]
    private void Resume() => Owner.Queue.Resume(Job.Id);

    [RelayCommand]
    private void Cancel() => Owner.Queue.Cancel(Job.Id);

    [RelayCommand]
    private void Remove() => Owner.RemoveItem(this);

    [RelayCommand]
    private void MoveUp() => Owner.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => Owner.Move(this, 1);
}

public partial class DownloadsViewModel : PageViewModel
{
    private readonly Vault _vault;
    private readonly DispatcherTimer _timer;

    public DownloadsViewModel(Vault vault) : base("Downloads")
    {
        _vault = vault;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _vault.Queue.JobChanged += _ => Ui(Sync);
        _timer.Start();
        Sync();
    }

    public DownloadQueue Queue => _vault.Queue;
    public ObservableCollection<JobItemViewModel> Jobs { get; } = [];

    [ObservableProperty]
    private string summary;

    public bool IsEmpty => Jobs.Count == 0;

    public override void OnActivated() => Sync();

    public void Sync()
    {
        var jobs = _vault.Queue.Jobs;
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            if (!jobs.Any(j => j.Id == Jobs[i].Job.Id))
                Jobs.RemoveAt(i);
        }
        for (var i = 0; i < jobs.Count; i++)
        {
            var idx = IndexOf(jobs[i].Id);
            if (idx < 0)
                Jobs.Insert(i, Create(jobs[i]));
            else if (idx != i)
                Jobs.Move(idx, i);
        }
        OnPropertyChanged(nameof(IsEmpty));
        Tick();
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < Jobs.Count; i++)
        {
            if (Jobs[i].Job.Id == id)
                return i;
        }
        return -1;
    }

    private JobItemViewModel Create(DownloadJob job)
    {
        var app = _vault.Apps.Get(job.AppId);
        var version = _vault.Library.Find(job.TargetVersionId);
        var depotName = app.Depots.FirstOrDefault(d => d.DepotId == job.DepotId)?.Name;
        return new JobItemViewModel(job, this)
        {
            Title = $"{app.Name ?? $"App {job.AppId}"}: {version?.Label ?? Format.Date(version?.ManifestDateUtc ?? default)}",
            Subtitle = $"Depot {job.DepotId}{(string.IsNullOrEmpty(depotName) ? "" : $" ({depotName})")}, manifest {job.ManifestId}",
        };
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        long total = 0, done = 0, saved = 0;
        var active = 0;
        foreach (var j in Jobs)
        {
            j.Tick(now);
            if (j.Job.IsFinished)
                continue;
            active++;
            total += j.Job.Counters.TotalBytes;
            done += j.Job.Counters.CompletedBytes;
            saved += j.Job.Counters.DedupedBytes + j.Job.Counters.ReusedBytes;
        }
        Summary = active == 0 ? "No active downloads." : $"{active} active, {Format.Bytes(done)} of {Format.Bytes(total)}, {Format.Bytes(saved)} saved by dedupe";
    }

    public void RemoveItem(JobItemViewModel item)
    {
        _vault.Queue.Remove(item.Job.Id);
        Sync();
    }

    public void Move(JobItemViewModel item, int delta)
    {
        var idx = Jobs.IndexOf(item);
        _vault.Queue.Move(item.Job.Id, idx + delta);
        Sync();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var j in Jobs.Where(j => j.Job.IsFinished).ToList())
            _vault.Queue.Remove(j.Job.Id);
        Sync();
    }

    [RelayCommand]
    private void PauseAll()
    {
        foreach (var j in Jobs.Where(j => j.Job.State is JobState.Queued or JobState.Running).ToList())
            _vault.Queue.Pause(j.Job.Id);
    }

    [RelayCommand]
    private void ResumeAll()
    {
        foreach (var j in Jobs.Where(j => j.Job.State == JobState.Paused).ToList())
            _vault.Queue.Resume(j.Job.Id);
    }
}
