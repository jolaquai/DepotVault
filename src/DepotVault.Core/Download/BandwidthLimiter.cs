using System.Diagnostics;

namespace DepotVault.Core.Download;

public sealed class BandwidthLimiter(Func<long> bytesPerSecond)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private double _available;
    private long _last = Stopwatch.GetTimestamp();

    public async ValueTask WaitAsync(long bytes, CancellationToken ct)
    {
        var rate = bytesPerSecond();
        if (rate <= 0)
            return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                _available = Math.Min(rate, _available + Stopwatch.GetElapsedTime(_last, now).TotalSeconds * rate);
                _last = now;
                if (_available >= bytes || _available >= rate)
                {
                    _available -= bytes;
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds((Math.Min(bytes, rate) - _available) / rate), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
