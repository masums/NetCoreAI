using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Alerts;

/// <summary>Raising an alert, and reading the recent ones.</summary>
public interface IAlertService
{
    /// <summary>
    /// Sends an alert to every sink, unless the same one went out recently.
    /// </summary>
    /// <remarks>
    /// Never throws. An alert is about something that has already happened, and failing the caller
    /// because the notification could not be delivered would turn a warning into an outage.
    /// </remarks>
    Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default);

    /// <summary>The alerts raised recently, newest first, whether or not a sink took them.</summary>
    IReadOnlyList<Alert> Recent(int limit = 50);
}

/// <summary>When to alert, how often, and where to.</summary>
public sealed class AlertOptions
{
    /// <summary>
    /// Watch for trouble. On by default, because the built-in sink only writes to the log — a host that
    /// has configured nowhere to send alerts still gets them where it is already looking.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A URL that receives each alert as JSON. Nothing is sent when this is unset.</summary>
    public Uri? WebhookUrl { get; set; }

    /// <summary>How often the monitor looks. Below a minute is noise rather than vigilance.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the same alert stays quiet after it has been sent.
    /// </summary>
    /// <remarks>
    /// A full disk is still full a minute later, and being told so every minute is how a host learns to
    /// filter the alerts out.
    /// </remarks>
    public TimeSpan ResendAfter { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Warn when free disk falls below this. Null uses the storage page's own judgement.</summary>
    public long? LowDiskBytes { get; set; }

    /// <summary>Percentage of runs failing that counts as a spike.</summary>
    public int ErrorRatePercent { get; set; } = 25;

    /// <summary>
    /// Runs needed before an error rate means anything.
    /// </summary>
    /// <remarks>
    /// One failed run out of one is 100%, and alerting on it would page somebody every time a developer
    /// typed a bad prompt on a quiet host.
    /// </remarks>
    public int ErrorRateMinimumRuns { get; set; } = 20;
}

internal sealed class AlertService(
    IEnumerable<IAlertSink> sinks,
    IOptionsMonitor<AlertOptions> options,
    ILogger<AlertService> logger) : IAlertService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Alert> _recent = new();

    public async Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (!options.CurrentValue.Enabled)
        {
            return;
        }

        var quietUntil = options.CurrentValue.ResendAfter;
        if (_sent.TryGetValue(alert.Key, out var last) && DateTimeOffset.UtcNow - last < quietUntil)
        {
            return;
        }

        _sent[alert.Key] = DateTimeOffset.UtcNow;

        // Kept whether or not a sink takes it, so "why did nobody tell me" can be answered with "we did,
        // at 04:12" rather than with a guess about the webhook.
        _recent.Enqueue(alert);
        while (_recent.Count > 200 && _recent.TryDequeue(out _))
        {
        }

        foreach (var sink in sinks)
        {
            try
            {
                await sink.SendAsync(alert, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One sink being down is survivable; that is the point of having several.
                logger.LogError(ex, "An alert sink ({Sink}) could not take {Key}.", sink.GetType().Name, alert.Key);
            }
        }
    }

    public IReadOnlyList<Alert> Recent(int limit = 50) =>
        [.. _recent.Reverse().Take(Math.Clamp(limit, 1, 200))];
}

/// <summary>
/// The sink every host has.
/// </summary>
/// <remarks>
/// Alerts go to the log first, at a level matching their severity, so a host that has configured nowhere
/// to send them still finds them where it is already looking. Everything else is additional.
/// </remarks>
internal sealed class LogAlertSink(ILogger<LogAlertSink> logger) : IAlertSink
{
    public Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

#pragma warning disable CA2254 // The template is chosen from a fixed set; the message is the alert's.
        logger.Log(
            alert.Severity switch
            {
                AlertSeverity.Error => LogLevel.Error,
                AlertSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Information,
            },
            "NetCoreAI alert [{Key}]: {Title} {Detail}",
            alert.Key,
            alert.Title,
            alert.Detail);
#pragma warning restore CA2254

        return Task.CompletedTask;
    }
}

/// <summary>
/// Posts each alert to a URL as JSON.
/// </summary>
/// <remarks>
/// Goes through the same client as everything else NetCoreAI calls out with, so the data-residency switch
/// applies: a host that has said nothing may leave will not find its alerts leaving.
/// </remarks>
internal sealed class WebhookAlertSink(IHttpClientFactory factory, IOptionsMonitor<AlertOptions> options) : IAlertSink
{
    /// <summary>The named client, so a host can give alert traffic its own handler.</summary>
    public const string HttpClientName = "NetCoreAI.Alerts";

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        if (options.CurrentValue.WebhookUrl is not { } url)
        {
            return;
        }

        using var response = await factory.CreateClient(HttpClientName)
            .PostAsJsonAsync(url, alert, cancellationToken).ConfigureAwait(false);

        // Thrown rather than swallowed: AlertService catches it and logs which sink failed, which is more
        // use than a webhook that quietly does nothing for a month.
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// A sink made from a lambda, for wiring a host's own mailer or incident tool in one line.
/// </summary>
/// <remarks>
/// NetCoreAI does not take a dependency on ASP.NET Core Identity to get at its <c>IEmailSender</c>, which
/// is where that interface lives. A host that has one wires it here and keeps its own mailer.
/// </remarks>
internal sealed class DelegateAlertSink(Func<Alert, CancellationToken, Task> send) : IAlertSink
{
    public Task SendAsync(Alert alert, CancellationToken cancellationToken = default) => send(alert, cancellationToken);
}
