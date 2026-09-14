using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Telemetry;
using Xunit;

namespace NetCoreAI.Core.Tests;

/// <summary>
/// The live traffic counters behind the overview page, and the cost estimate shown per message.
/// </summary>
public class UsageAndCostTests
{
    private static UsageEvent Event(string model, bool success = true, double ms = 100, long input = 10, long output = 20, decimal? cost = null) =>
        new(model, success, ms, input, output, cost);

    [Fact]
    public void An_empty_tracker_reports_nothing_rather_than_dividing_by_zero()
    {
        var snapshot = new UsageTracker().GetSnapshot();

        Assert.Equal(0, snapshot.RequestsLastHour);
        Assert.Equal(0, snapshot.ErrorRate);
        Assert.Empty(snapshot.Models);
    }

    [Fact]
    public void Requests_errors_and_tokens_are_summed_over_the_window()
    {
        var tracker = new UsageTracker();
        tracker.Record(Event("a", ms: 100, input: 10, output: 20));
        tracker.Record(Event("a", ms: 300, input: 5, output: 5));
        tracker.Record(Event("b", success: false, ms: 50));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(3, snapshot.RequestsLastHour);
        Assert.Equal(3, snapshot.RequestsLastMinute);
        Assert.Equal(1, snapshot.ErrorsLastHour);
        Assert.Equal(1d / 3, snapshot.ErrorRate, 3);
        Assert.Equal(25, snapshot.InputTokensLastHour);
        Assert.Equal(45, snapshot.OutputTokensLastHour);
    }

    [Fact]
    public void Latency_is_reported_as_a_median_so_one_slow_call_does_not_dominate()
    {
        var tracker = new UsageTracker();
        foreach (var ms in (double[])[100, 110, 120, 10_000])
        {
            tracker.Record(Event("a", ms: ms));
        }

        // The mean here would be 2582 ms and describe nothing anyone experienced.
        Assert.Equal(115, tracker.GetSnapshot().MedianLatencyMs);
    }

    [Fact]
    public void Busiest_models_come_first_with_their_own_totals()
    {
        var tracker = new UsageTracker();
        tracker.Record(Event("quiet"));
        for (var i = 0; i < 5; i++)
        {
            tracker.Record(Event("busy", success: i != 0, cost: 0.5m));
        }

        var snapshot = tracker.GetSnapshot();

        Assert.Equal("busy", snapshot.Models[0].ModelId);
        Assert.Equal(5, snapshot.Models[0].Requests);
        Assert.Equal(1, snapshot.Models[0].Errors);
        Assert.Equal(2.5m, snapshot.Models[0].Cost);
        Assert.Equal(2.5m, snapshot.CostLastHour);
    }

    [Fact]
    public void Events_outside_the_window_are_ignored()
    {
        var tracker = new UsageTracker();
        tracker.Record(Event("a") with { At = DateTimeOffset.UtcNow.AddHours(-3) });
        tracker.Record(Event("a"));

        Assert.Equal(1, tracker.GetSnapshot(TimeSpan.FromMinutes(30)).RequestsLastHour);
    }

    [Fact]
    public async Task A_generation_through_the_factory_is_counted_with_its_tokens()
    {
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(new FakeProvider("fake", ProviderKind.Local, ModelFormat.Gguf)));
        var ct = TestContext.Current.CancellationToken;
        await host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "counted",
            Name = "Counted",
            Format = ModelFormat.Gguf,
            ProviderId = "fake",
            Path = "counted.gguf",
        }, ct);

        var chat = host.Services.GetRequiredService<IChatClientFactory>().Get("counted");
        await chat.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")], cancellationToken: ct);

        // The counters the overview page reads come from the pipeline, not from the page asking providers.
        var snapshot = host.Services.GetRequiredService<IUsageTracker>().GetSnapshot();
        Assert.Equal(1, snapshot.RequestsLastHour);
        Assert.Equal(0, snapshot.ErrorsLastHour);
        Assert.Equal(3, snapshot.InputTokensLastHour);
        Assert.Equal(5, snapshot.OutputTokensLastHour);
        Assert.Equal("counted", Assert.Single(snapshot.Models).ModelId);
    }

    [Fact]
    public async Task A_failed_generation_is_counted_as_an_error_and_still_throws()
    {
        var provider = new FakeProvider("fake", ProviderKind.Local, ModelFormat.Gguf)
        {
            FailWith = new InvalidOperationException("the model fell over"),
        };

        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(provider));
        var ct = TestContext.Current.CancellationToken;
        await host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "broken",
            Name = "Broken",
            Format = ModelFormat.Gguf,
            ProviderId = "fake",
            Path = "broken.gguf",
        }, ct);

        var chat = host.Services.GetRequiredService<IChatClientFactory>().Get("broken");

        // Counting a failure must not swallow it: the caller still sees what went wrong.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await chat.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")], cancellationToken: ct));

        var snapshot = host.Services.GetRequiredService<IUsageTracker>().GetSnapshot();
        Assert.Equal(1, snapshot.RequestsLastHour);
        Assert.Equal(1, snapshot.ErrorsLastHour);
        Assert.Equal(1, snapshot.ErrorRate);
    }

    [Fact]
    public async Task A_local_model_has_no_cost_because_its_price_is_electricity()
    {
        using var host = await TestHost.StartAsync();
        var costs = host.Services.GetRequiredService<ICostEstimator>();

        var local = new ModelDescriptor
        {
            Id = "qwen-local",
            Name = "Qwen local",
            Format = ModelFormat.Gguf,
            ProviderId = "gguf",
            Path = "model.gguf",
        };

        // Null rather than zero: "free" and "not priced" are different answers.
        Assert.Null(costs.Estimate(local, 1000, 1000));
    }

    [Fact]
    public async Task A_remote_model_is_priced_from_its_connection()
    {
        using var host = await TestHost.StartAsync();
        var store = host.Services.GetRequiredService<IMetadataStore>();
        await store.Connections.UpsertAsync(new ProviderConnection
        {
            Id = "openai-prod",
            Name = "OpenAI prod",
            ProviderId = "openai",
            CostPer1KInputTokens = 0.15m,
            CostPer1KOutputTokens = 0.60m,
        }, TestContext.Current.CancellationToken);

        var costs = new CostEstimator(store, NullLogger<CostEstimator>.Instance);
        var remote = new ModelDescriptor
        {
            Id = "gpt-4o-mini",
            Name = "GPT-4o mini",
            Format = ModelFormat.Remote,
            ProviderId = "openai",
            ConnectionId = "openai-prod",
            RemoteModelId = "gpt-4o-mini",
        };

        // 2000 in at 0.15/1K + 1000 out at 0.60/1K.
        Assert.Equal(0.90m, costs.Estimate(remote, 2000, 1000));
    }

    [Fact]
    public async Task A_connection_with_no_pricing_reports_no_cost()
    {
        using var host = await TestHost.StartAsync();
        var store = host.Services.GetRequiredService<IMetadataStore>();
        await store.Connections.UpsertAsync(new ProviderConnection
        {
            Id = "local-vllm",
            Name = "vLLM",
            ProviderId = "openai",
        }, TestContext.Current.CancellationToken);

        var costs = new CostEstimator(store, NullLogger<CostEstimator>.Instance);
        var remote = new ModelDescriptor
        {
            Id = "llama",
            Name = "Llama",
            Format = ModelFormat.Remote,
            ProviderId = "openai",
            ConnectionId = "local-vllm",
        };

        // Self-hosted behind an OpenAI-compatible endpoint: remote, but nobody is billing per token.
        Assert.Null(costs.Estimate(remote, 5000, 5000));
    }

    [Fact]
    public async Task A_missing_connection_does_not_break_a_generation()
    {
        using var host = await TestHost.StartAsync();
        var costs = new CostEstimator(host.Services.GetRequiredService<IMetadataStore>(), NullLogger<CostEstimator>.Instance);

        var orphaned = new ModelDescriptor
        {
            Id = "ghost",
            Name = "Ghost",
            Format = ModelFormat.Remote,
            ProviderId = "openai",
            ConnectionId = "deleted-connection",
        };

        Assert.Null(costs.Estimate(orphaned, 100, 100));
    }
}
