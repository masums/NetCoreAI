using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>
/// Token bucket shared by every download, so the cap in settings limits total bandwidth rather than each
/// file separately. A limit of null lets everything through without allocating or waiting.
/// </summary>
internal sealed class BandwidthLimiter(IOptionsMonitor<NetCoreAIOptions> options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private double _available;
    private long _lastRefillTicks = Environment.TickCount64;

    /// <summary>Bytes per second, or null when downloads are unthrottled.</summary>
    public long? Limit => options.CurrentValue.Network.BandwidthLimitBytesPerSecond is > 0 and var limit ? limit : null;

    /// <summary>Waits until <paramref name="bytes"/> may be transferred. Returns at once when there is no limit.</summary>
    public async ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0 || Limit is not { } limit)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Refill(limit);
                if (_available >= bytes)
                {
                    _available -= bytes;
                    return;
                }

                // Sleep for exactly as long as the missing tokens need, so the average rate lands on the limit.
                var deficit = bytes - _available;
                var milliseconds = Math.Clamp(deficit / limit * 1000, 1, 1000);
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Refill(long limit)
    {
        var now = Environment.TickCount64;
        var elapsed = (now - _lastRefillTicks) / 1000.0;
        _lastRefillTicks = now;

        // One second of burst is allowed, which keeps small reads from stalling on a fresh bucket.
        _available = Math.Min(limit, _available + (elapsed * limit));
    }
}
