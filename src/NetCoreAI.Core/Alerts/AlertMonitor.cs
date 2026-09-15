using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Models;
using NetCoreAI.Storage;

namespace NetCoreAI.Alerts;

/// <summary>
/// Watches for the three things that go wrong quietly.
/// </summary>
/// <remarks>
/// A full disk, a model that will not load, and a run failure rate that has climbed. None of them stops
/// the host, all of them are noticed late, and the first sign is usually a person saying it has been
/// broken since Tuesday.
/// </remarks>
internal sealed class AlertMonitor(
    IAlertService alerts,
    IStorageService storage,
    IMetadataStore store,
    IModelLifecycleManager lifecycle,
    IOptionsMonitor<AlertOptions> options,
    ILogger<AlertMonitor> logger) : BackgroundService
{
    /// <summary>Long enough for the host to finish starting before anything is measured.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // A model failing to load is an event, not something to discover on the next sweep: by then the
        // request that wanted it has long since been answered with an error.
        lifecycle.StatusChanged += OnStatusChanged;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lifecycle.StatusChanged -= OnStatusChanged;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);

                var interval = options.CurrentValue.CheckInterval;
                await Task.Delay(interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : interval, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            return;
        }

        try
        {
            await CheckDiskAsync(cancellationToken).ConfigureAwait(false);
            await CheckErrorRateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Watching for trouble must not become the trouble.
            logger.LogWarning(ex, "The alert check could not finish; it will run again.");
        }
    }

    private async Task CheckDiskAsync(CancellationToken cancellationToken)
    {
        var usage = await storage.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        if (usage.FreeDiskBytes is not { } free)
        {
            return;
        }

        var low = options.CurrentValue.LowDiskBytes is { } configured ? free < configured : usage.LowDisk;
        if (!low)
        {
            return;
        }

        await alerts.RaiseAsync(
            new Alert
            {
                Key = "disk.low",
                Severity = AlertSeverity.Warning,
                Title = $"Free disk is down to {Megabytes(free)} where NetCoreAI keeps its data.",
                Detail = "Downloads and ingestion will start failing. Delete unused models on the Storage page, or move the data directory to a larger volume.",
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckErrorRateAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;

        // The window is the check interval, doubled: a rate measured over exactly the gap between checks
        // misses a spike that straddles two of them.
        var since = DateTimeOffset.UtcNow - (settings.CheckInterval * 2);
        var summary = await store.Runs.SummariseAsync(new RunQuery { Since = since }, cancellationToken).ConfigureAwait(false);

        if (summary.Runs < Math.Max(1, settings.ErrorRateMinimumRuns))
        {
            // One failure out of one is 100%, and paging somebody for it is how alerting gets switched off.
            return;
        }

        var rate = summary.Failed * 100 / summary.Runs;
        if (rate < settings.ErrorRatePercent)
        {
            return;
        }

        await alerts.RaiseAsync(
            new Alert
            {
                Key = "runs.error-rate",
                Severity = AlertSeverity.Error,
                Title = $"{rate}% of agent runs are failing ({summary.Failed} of {summary.Runs}).",
                Detail = "Look at the failed runs on the Usage page: a provider outage, an expired key and a model that will not load all look like this.",
            },
            cancellationToken).ConfigureAwait(false);
    }

    private void OnStatusChanged(object? sender, (ModelDescriptor Model, ModelStatus Status, string? Message) e)
    {
        if (e.Status != ModelStatus.Error)
        {
            return;
        }

        // Fire-and-forget on purpose: this is an event handler on the load path, and making a load wait
        // for a webhook would be a strange way to find out about a slow webhook.
        _ = alerts.RaiseAsync(new Alert
        {
            Key = $"model.load-failed:{e.Model.Id}",
            Severity = AlertSeverity.Error,
            Title = $"{e.Model.Name} could not be loaded.",
            Detail = e.Message,
        });
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}
