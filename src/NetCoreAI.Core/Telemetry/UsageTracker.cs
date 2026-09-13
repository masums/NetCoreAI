using System.Collections.Concurrent;

namespace NetCoreAI.Telemetry;

/// <summary>What one generation cost, recorded when it finishes.</summary>
/// <param name="ModelId">Registry id of the model that served it.</param>
/// <param name="Success">False when the call threw or the provider failed.</param>
/// <param name="DurationMs">Wall-clock time of the call.</param>
/// <param name="InputTokens">Prompt tokens, when the provider reported them.</param>
/// <param name="OutputTokens">Generated tokens, when the provider reported them.</param>
/// <param name="Cost">Estimated cost in the connection's currency; null for local models.</param>
public readonly record struct UsageEvent(string ModelId, bool Success, double DurationMs, long InputTokens, long OutputTokens, decimal? Cost)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Traffic over a window, as the overview page shows it.</summary>
public sealed record UsageSnapshot
{
    public int RequestsLastMinute { get; init; }

    public int RequestsLastHour { get; init; }

    /// <summary>Requests per minute averaged over the last hour; smoother than the last-minute count.</summary>
    public double RequestsPerMinute { get; init; }

    /// <summary>Share of the last hour's requests that failed, 0 to 1.</summary>
    public double ErrorRate { get; init; }

    public int ErrorsLastHour { get; init; }

    /// <summary>Median duration over the last hour, which says more than a mean when one call is slow.</summary>
    public double MedianLatencyMs { get; init; }

    public long InputTokensLastHour { get; init; }

    public long OutputTokensLastHour { get; init; }

    public decimal CostLastHour { get; init; }

    /// <summary>Busiest models over the window, most requests first.</summary>
    public IReadOnlyList<ModelUsage> Models { get; init; } = [];

    public static readonly UsageSnapshot Empty = new();
}

/// <summary>Per-model slice of a <see cref="UsageSnapshot"/>.</summary>
public sealed record ModelUsage(string ModelId, int Requests, int Errors, double MedianLatencyMs, long InputTokens, long OutputTokens, decimal Cost);

/// <summary>
/// Rolling in-process record of recent generations, so the dashboard can show requests per minute and an
/// error rate without a metrics backend. Bounded and lossy by design: OpenTelemetry is the durable path,
/// this only has to answer "what is happening right now".
/// </summary>
public interface IUsageTracker
{
    void Record(in UsageEvent usage);

    UsageSnapshot GetSnapshot(TimeSpan? window = null);
}

internal sealed class UsageTracker : IUsageTracker
{
    /// <summary>An hour of moderate traffic; older entries are dropped as new ones arrive.</summary>
    private const int Capacity = 5000;

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentQueue<UsageEvent> _events = new();

    public void Record(in UsageEvent usage)
    {
        _events.Enqueue(usage);
        while (_events.Count > Capacity && _events.TryDequeue(out _))
        {
            // Trim to the cap; a burst can push a little past it before this catches up, which is fine.
        }
    }

    public UsageSnapshot GetSnapshot(TimeSpan? window = null)
    {
        var span = window ?? DefaultWindow;
        var now = DateTimeOffset.UtcNow;
        var since = now - span;
        var lastMinute = now - TimeSpan.FromMinutes(1);

        var recent = _events.Where(e => e.At >= since).ToList();
        if (recent.Count == 0)
        {
            return UsageSnapshot.Empty;
        }

        var errors = recent.Count(e => !e.Success);
        return new UsageSnapshot
        {
            RequestsLastMinute = recent.Count(e => e.At >= lastMinute),
            RequestsLastHour = recent.Count,
            RequestsPerMinute = Math.Round(recent.Count / Math.Max(1, span.TotalMinutes), 2),
            ErrorsLastHour = errors,
            ErrorRate = (double)errors / recent.Count,
            MedianLatencyMs = Median([.. recent.Select(e => e.DurationMs)]),
            InputTokensLastHour = recent.Sum(e => e.InputTokens),
            OutputTokensLastHour = recent.Sum(e => e.OutputTokens),
            CostLastHour = recent.Sum(e => e.Cost ?? 0m),
            Models = [.. recent
                .GroupBy(e => e.ModelId, StringComparer.Ordinal)
                .Select(g => new ModelUsage(
                    g.Key,
                    g.Count(),
                    g.Count(e => !e.Success),
                    Median([.. g.Select(e => e.DurationMs)]),
                    g.Sum(e => e.InputTokens),
                    g.Sum(e => e.OutputTokens),
                    g.Sum(e => e.Cost ?? 0m)))
                .OrderByDescending(m => m.Requests)],
        };
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
