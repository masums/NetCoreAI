using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Telemetry;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Reading back what was run, what it used, and what it cost.
/// </summary>
/// <remarks>
/// Read from the run traces that were being written anyway. There is no separate accounting table,
/// because a second copy kept for reporting disagrees with the traces the first time a run is written by
/// a path that forgot to update it.
/// </remarks>
public sealed class UsageAnalyticsTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IMetadataStore Store => _app.Services.GetRequiredService<IMetadataStore>();

    private IUsageAnalytics Analytics => _app.Services.GetRequiredService<IUsageAnalytics>();

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

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

    private Task SaveRunAsync(
        string id,
        string agentId,
        string? modelId = "gpt",
        string? userId = "alice",
        int? input = 100,
        int? output = 50,
        decimal? cost = 0.01m,
        bool success = true,
        int daysAgo = 0,
        long elapsedMs = 1000,
        string? text = null) =>
        Store.Runs.UpsertAsync(
            new RunTrace
            {
                Id = id,
                AgentId = agentId,
                ModelId = modelId,
                UserId = userId,
                InputTokens = input,
                OutputTokens = output,
                EstimatedCost = cost,
                Success = success,
                ElapsedMs = elapsedMs,
                Input = text ?? "a question",
                Output = "an answer",
                StartedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
            },
            Ct);

    // ---------- totals ----------

    [Fact]
    public async Task Tokens_and_cost_are_totalled_across_the_period()
    {
        await SaveRunAsync("r1", "helper");
        await SaveRunAsync("r2", "helper");

        var summary = await Analytics.SummariseAsync(new RunQuery(), Ct);

        Assert.Equal(2, summary.Runs);
        Assert.Equal(200, summary.InputTokens);
        Assert.Equal(100, summary.OutputTokens);
        Assert.Equal(0.02m, summary.Cost);
    }

    [Fact]
    public async Task A_run_whose_tokens_were_never_recorded_is_counted_separately_rather_than_as_zero()
    {
        await SaveRunAsync("measured", "helper");
        await SaveRunAsync("unmeasured", "helper", input: null, output: null, cost: null);

        var summary = await Analytics.SummariseAsync(new RunQuery(), Ct);

        // Reported rather than folded in. A provider that returns no usage, and a run from before these
        // figures were kept, both land here — and a total that hides them reads as precise when it is not.
        Assert.Equal(2, summary.Runs);
        Assert.Equal(1, summary.Unmeasured);
        Assert.Equal(100, summary.InputTokens);
    }

    [Fact]
    public async Task The_median_run_time_is_not_moved_by_one_slow_one()
    {
        await SaveRunAsync("r1", "helper", elapsedMs: 100);
        await SaveRunAsync("r2", "helper", elapsedMs: 200);
        await SaveRunAsync("r3", "helper", elapsedMs: 300_000);

        // The mean here is over 100 seconds, which is not a number anybody should quote about this agent.
        Assert.Equal(200, (await Analytics.SummariseAsync(new RunQuery(), Ct)).MedianElapsedMs);
    }

    [Fact]
    public async Task Failures_are_counted()
    {
        await SaveRunAsync("ok", "helper");
        await SaveRunAsync("bad", "helper", success: false);

        var summary = await Analytics.SummariseAsync(new RunQuery(), Ct);

        Assert.Equal(2, summary.Runs);
        Assert.Equal(1, summary.Failed);
    }

    // ---------- breakdowns ----------

    [Fact]
    public async Task Usage_is_broken_down_by_agent_model_and_person()
    {
        await SaveRunAsync("r1", "helper", modelId: "gpt", userId: "alice");
        await SaveRunAsync("r2", "helper", modelId: "gpt", userId: "bob");
        await SaveRunAsync("r3", "other", modelId: "llama", userId: "alice");

        var summary = await Analytics.SummariseAsync(new RunQuery(), Ct);

        Assert.Equal(2, Assert.Single(summary.ByAgent, b => b.Key == "helper").Runs);
        Assert.Equal(1, Assert.Single(summary.ByModel, b => b.Key == "llama").Runs);
        Assert.Equal(2, Assert.Single(summary.ByUser, b => b.Key == "alice").Runs);
    }

    [Fact]
    public async Task A_run_with_nobody_signed_in_is_labelled_rather_than_dropped()
    {
        await SaveRunAsync("r1", "helper", userId: null);

        // Dropping it would make the per-person breakdown quietly disagree with the total.
        Assert.Equal(1, Assert.Single((await Analytics.SummariseAsync(new RunQuery(), Ct)).ByUser).Runs);
    }

    [Fact]
    public async Task The_daily_series_includes_days_nothing_happened()
    {
        await SaveRunAsync("old", "helper", daysAgo: 4);
        await SaveRunAsync("new", "helper", daysAgo: 0);

        var summary = await Analytics.SummariseAsync(new RunQuery { Since = DateTimeOffset.UtcNow.AddDays(-4) }, Ct);

        // A chart drawn only from days that have runs joins Monday to Friday with a straight line and
        // invents three days of activity that did not happen.
        Assert.Equal(5, summary.ByDay.Count);
        Assert.Contains(summary.ByDay, d => d.Runs == 0);
    }

    // ---------- browsing ----------

    [Fact]
    public async Task Runs_can_be_filtered_and_paged_newest_first()
    {
        for (var i = 0; i < 5; i++)
        {
            await SaveRunAsync($"r{i}", i < 3 ? "helper" : "other", daysAgo: i);
        }

        var (page, total) = await Analytics.BrowseAsync(new RunQuery { AgentId = "helper", Limit = 2 }, Ct);

        Assert.Equal(3, total);
        Assert.Equal(2, page.Count);
        Assert.Equal("r0", page[0].Id);

        var (second, _) = await Analytics.BrowseAsync(new RunQuery { AgentId = "helper", Limit = 2, Offset = 2 }, Ct);
        Assert.Equal("r2", Assert.Single(second).Id);
    }

    [Fact]
    public async Task A_period_filter_leaves_out_what_is_older()
    {
        await SaveRunAsync("recent", "helper", daysAgo: 1);
        await SaveRunAsync("ancient", "helper", daysAgo: 60);

        var (runs, _) = await Analytics.BrowseAsync(new RunQuery { Since = DateTimeOffset.UtcNow.AddDays(-30) }, Ct);

        Assert.Equal("recent", Assert.Single(runs).Id);
    }

    [Fact]
    public async Task Only_failures_can_be_asked_for()
    {
        await SaveRunAsync("ok", "helper");
        await SaveRunAsync("bad", "helper", success: false);

        var (runs, _) = await Analytics.BrowseAsync(new RunQuery { Success = false }, Ct);

        Assert.Equal("bad", Assert.Single(runs).Id);
    }

    [Fact]
    public async Task Free_text_matches_the_question_and_the_answer()
    {
        await SaveRunAsync("about-refunds", "helper", text: "how do refunds work");
        await SaveRunAsync("about-shipping", "helper", text: "where is my parcel");

        var (runs, _) = await Analytics.BrowseAsync(new RunQuery { Search = "REFUNDS" }, Ct);

        Assert.Equal("about-refunds", Assert.Single(runs).Id);
    }

    // ---------- export ----------

    [Fact]
    public async Task The_export_has_a_header_and_a_row_per_run()
    {
        await SaveRunAsync("r1", "helper");

        var csv = await Analytics.ExportCsvAsync(new RunQuery(), Ct);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("started_at,run_id,agent_id", lines[0], StringComparison.Ordinal);
        Assert.Contains("helper", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_that_a_spreadsheet_would_run_is_neutralised()
    {
        // An agent id is text somebody chose. A leading = makes Excel treat the cell as a formula, which
        // turns a usage export into a way to run something on the machine of whoever opens it.
        await SaveRunAsync("r1", "=cmd|'/c calc'!A1");

        var csv = await Analytics.ExportCsvAsync(new RunQuery(), Ct);

        Assert.DoesNotContain(",=cmd", csv, StringComparison.Ordinal);
        Assert.Contains("'=cmd", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_with_no_tokens_exports_an_empty_cell_rather_than_a_zero()
    {
        await SaveRunAsync("r1", "helper", input: null, output: null, cost: null);

        var csv = await Analytics.ExportCsvAsync(new RunQuery(), Ct);

        // Whoever sums this column in a spreadsheet must not be told the run was free.
        Assert.EndsWith(",,,", csv.Split('\n')[1].TrimEnd('\r'), StringComparison.Ordinal);
    }
}
