using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>
/// Runs data sources on their cron schedules.
/// </summary>
/// <remarks>
/// A source that has never synced runs at its next due time rather than immediately on startup: a host
/// that restarts often would otherwise re-crawl everything each time. Due times are evaluated against the
/// last sync recorded on the source, so a host that was down over a scheduled slot picks the work up when
/// it comes back rather than skipping it silently.
/// </remarks>
internal sealed class SyncScheduler(
    IMetadataStore store,
    IKnowledgeService knowledge,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    /// <summary>How often due sources are looked for. Cron granularity is a minute, so this is enough.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The scheduler must survive a bad source: one broken cron expression should not stop
                // every other schedule in the host.
                logger.LogError(ex, "The sync scheduler hit an error; it will try again on the next tick.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var knowledgeBase in await store.Knowledge.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var source in await store.Knowledge.ListSourcesAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false))
            {
                if (!source.Enabled || source.Schedule is not { Length: > 0 } schedule)
                {
                    continue;
                }

                if (!IsDue(schedule, source.LastSyncedAt, source.CreatedAt, now, out var error))
                {
                    if (error is not null)
                    {
                        logger.LogWarning("Source {Source} has an unusable schedule '{Schedule}': {Error}", source.Name, schedule, error);
                    }

                    continue;
                }

                logger.LogInformation("Schedule '{Schedule}' is due for {Source}; queueing a sync.", schedule, source.Name);

                // Marked as synced before the job runs, so a long ingest is not queued again on the next
                // tick. A failure is recorded on the job, and the next slot will try again.
                await store.Knowledge.UpsertSourceAsync(source with { LastSyncedAt = now }, cancellationToken).ConfigureAwait(false);
                await knowledge.SyncAsync(knowledgeBase.Id, source.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether a schedule's next slot after the last run has passed. Evaluating from the last run rather
    /// than from now means a missed slot is picked up on the next tick instead of being lost.
    /// </summary>
    internal static bool IsDue(string schedule, DateTimeOffset? lastSynced, DateTimeOffset createdAt, DateTimeOffset now, out string? error)
    {
        error = null;
        CronExpression expression;
        try
        {
            // Five fields is the common spelling; six adds seconds, which some hosts write.
            expression = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6
                ? CronExpression.Parse(schedule, CronFormat.IncludeSeconds)
                : CronExpression.Parse(schedule);
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }

        var from = lastSynced ?? createdAt;
        var next = expression.GetNextOccurrence(from.UtcDateTime, TimeZoneInfo.Utc);
        return next is { } due && due <= now.UtcDateTime;
    }
}
