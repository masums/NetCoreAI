using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Alerts;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Being told when something has gone wrong, and — the harder half — not being told so often that the
/// telling stops working.
/// </summary>
public sealed class AlertTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";
    private static readonly List<Alert> Taken = [];
    private static bool _sinkThrows;

    private IAlertService Alerts => _app.Services.GetRequiredService<IAlertService>();

    private AlertOptions Options => _app.Services.GetRequiredService<IOptionsMonitor<AlertOptions>>().CurrentValue;

    public async ValueTask InitializeAsync()
    {
        Taken.Clear();
        _sinkThrows = false;
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")

            // One sink that records, and one that always fails, because the interesting question is what
            // happens to the first when the second is broken.
            .AddAlertSink((alert, _) =>
            {
                Taken.Add(alert);
                return Task.CompletedTask;
            })
            .AddAlertSink((_, _) => _sinkThrows
                ? throw new InvalidOperationException("this sink is down")
                : Task.CompletedTask);

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Alert Sample(string key = "disk.low") =>
        new() { Key = key, Title = "Something happened.", Severity = AlertSeverity.Warning };

    // ---------- delivery ----------

    [Fact]
    public async Task An_alert_reaches_every_sink()
    {
        await Alerts.RaiseAsync(Sample(), Ct);

        Assert.Single(Taken);
    }

    [Fact]
    public async Task A_sink_that_is_down_does_not_stop_the_others()
    {
        // The whole reason to have several. A host whose webhook is unreachable must still get the alert
        // by email, and must still find it in the log.
        _sinkThrows = true;

        await Alerts.RaiseAsync(Sample(), Ct);

        Assert.Single(Taken);
    }

    [Fact]
    public async Task Raising_an_alert_never_throws()
    {
        _sinkThrows = true;

        // An alert is about something that has already happened. Failing the caller because the
        // notification could not be delivered would turn a warning into an outage.
        var error = await Record.ExceptionAsync(() => Alerts.RaiseAsync(Sample(), Ct));

        Assert.Null(error);
    }

    // ---------- not being a nuisance ----------

    [Fact]
    public async Task The_same_condition_is_reported_once_rather_than_every_time_it_is_noticed()
    {
        await Alerts.RaiseAsync(Sample(), Ct);
        await Alerts.RaiseAsync(Sample(), Ct);
        await Alerts.RaiseAsync(Sample(), Ct);

        // A full disk is still full a minute later. An alert that arrives every minute is one nobody
        // reads, which is the same as no alerting at all.
        Assert.Single(Taken);
    }

    [Fact]
    public async Task A_different_condition_still_gets_through()
    {
        await Alerts.RaiseAsync(Sample("disk.low"), Ct);
        await Alerts.RaiseAsync(Sample("runs.error-rate"), Ct);

        Assert.Equal(2, Taken.Count);
    }

    [Fact]
    public async Task It_is_reported_again_once_the_quiet_period_has_passed()
    {
        Options.ResendAfter = TimeSpan.Zero;

        await Alerts.RaiseAsync(Sample(), Ct);
        await Alerts.RaiseAsync(Sample(), Ct);

        Assert.Equal(2, Taken.Count);
    }

    [Fact]
    public async Task Turning_alerts_off_stops_them()
    {
        Options.Enabled = false;
        try
        {
            await Alerts.RaiseAsync(Sample(), Ct);
            Assert.Empty(Taken);
        }
        finally
        {
            Options.Enabled = true;
        }
    }

    // ---------- what is watched ----------

    [Fact]
    public async Task A_run_failure_rate_above_the_threshold_raises_an_alert()
    {
        Options.ErrorRatePercent = 25;
        Options.ErrorRateMinimumRuns = 4;
        await RunsAsync(failed: 2, succeeded: 2);

        await Monitor().CheckAsync(Ct);

        var alert = Assert.Single(Taken, a => a.Key == "runs.error-rate");
        Assert.Contains("50%", alert.Title, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.Error, alert.Severity);
    }

    [Fact]
    public async Task One_failure_on_a_quiet_host_is_not_a_spike()
    {
        Options.ErrorRateMinimumRuns = 20;
        await RunsAsync(failed: 1, succeeded: 0);

        await Monitor().CheckAsync(Ct);

        // One failed run out of one is 100%, and paging somebody for it is how alerting gets switched off.
        Assert.DoesNotContain(Taken, a => a.Key == "runs.error-rate");
    }

    [Fact]
    public async Task A_healthy_host_is_told_nothing()
    {
        Options.ErrorRateMinimumRuns = 4;
        await RunsAsync(failed: 0, succeeded: 10);

        await Monitor().CheckAsync(Ct);

        Assert.DoesNotContain(Taken, a => a.Key == "runs.error-rate");
    }

    [Fact]
    public async Task Old_failures_do_not_count_towards_the_current_rate()
    {
        Options.ErrorRateMinimumRuns = 4;
        Options.CheckInterval = TimeSpan.FromMinutes(5);
        await RunsAsync(failed: 10, succeeded: 0, daysAgo: 2);
        await RunsAsync(failed: 0, succeeded: 10);

        await Monitor().CheckAsync(Ct);

        // Yesterday's outage is not today's. A rate measured over all of history never recovers.
        Assert.DoesNotContain(Taken, a => a.Key == "runs.error-rate");
    }

    [Fact]
    public async Task A_test_alert_is_never_suppressed_as_a_repeat()
    {
        var client = _app.GetTestClient();
        await client.PostAsync(new Uri("/netcoreai/api/alerts/test", UriKind.Relative), null, Ct);
        await client.PostAsync(new Uri("/netcoreai/api/alerts/test", UriKind.Relative), null, Ct);

        // The one alert that must always arrive is the one somebody sent to find out whether they arrive.
        Assert.Equal(2, Taken.Count(a => a.Key.StartsWith("test:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Recent_alerts_are_kept_whether_or_not_a_sink_took_them()
    {
        _sinkThrows = true;
        await Alerts.RaiseAsync(Sample(), Ct);

        // So "why did nobody tell me" can be answered with "we did, at 04:12" rather than with a guess
        // about the webhook.
        Assert.Single(Alerts.Recent());
    }

    private AlertMonitor Monitor() =>
        _app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AlertMonitor>().Single();

    private async Task RunsAsync(int failed, int succeeded, int daysAgo = 0)
    {
        var store = _app.Services.GetRequiredService<IMetadataStore>();
        for (var i = 0; i < failed + succeeded; i++)
        {
            await store.Runs.UpsertAsync(
                new RunTrace
                {
                    Id = Guid.NewGuid().ToString("N"),
                    AgentId = "helper",
                    Success = i >= failed,
                    StartedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
                },
                Ct);
        }
    }
}
