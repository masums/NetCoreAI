using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Security;

namespace NetCoreAI.Storage;

/// <summary>
/// Deletes what is older than the host wants to keep.
/// </summary>
/// <remarks>
/// Run traces and audit entries are both append-only and both written on the busiest paths there are, so
/// without this they grow until somebody notices the disk. <c>PruneAsync</c> existed on both stores from
/// the start and nothing ever called it; this is what calls it.
/// </remarks>
internal sealed class RetentionService(
    IMetadataStore store,
    IOptionsMonitor<NetCoreAIOptions> options,
    IOptionsMonitor<AuditOptions> audit,
    ILogger<RetentionService> logger) : BackgroundService
{
    /// <summary>Daily. Retention is measured in days, so sweeping more often only costs writes.</summary>
    private static readonly TimeSpan Period = TimeSpan.FromHours(24);

    /// <summary>
    /// A short wait before the first sweep, so startup is not competing with a delete over the same
    /// database while the host is still coming up.
    /// </summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(Period);
            do
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await PruneAsync(
            "run trace",
            options.CurrentValue.Storage.RunRetentionDays,
            cutoff => store.Runs.PruneAsync(cutoff, cancellationToken)).ConfigureAwait(false);

        await PruneAsync(
            "audit entry",
            audit.CurrentValue.RetentionDays,
            cutoff => store.Audit.PruneAsync(cutoff, cancellationToken)).ConfigureAwait(false);
    }

    private async Task PruneAsync(string what, int days, Func<DateTimeOffset, Task<int>> prune)
    {
        if (days <= 0)
        {
            // Keep forever. A deliberate choice, so it is not second-guessed here.
            return;
        }

        try
        {
            var removed = await prune(DateTimeOffset.UtcNow.AddDays(-days)).ConfigureAwait(false);
            if (removed > 0)
            {
                logger.LogInformation("Retention removed {Count} {What}(s) older than {Days} days.", removed, what, days);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tidying is not worth taking the host down for; it will try again tomorrow.
            logger.LogWarning(ex, "Could not prune {What}s.", what);
        }
    }
}
