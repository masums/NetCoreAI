using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Hub;
using NetCoreAI.Providers;
using NetCoreAI.Tools;
using NetCoreAI.Tools.BuiltIn;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The data-residency switch, on the clients rather than in the abstract.
/// </summary>
/// <remarks>
/// The unit tests say the policy decides correctly. These say every way out of the process actually asks
/// it — which is the part that was not true: the handler was attached to hub browsing and downloads, and
/// not to tool invocation or the built-in fetch tool, both of which are a URL a model can reach.
/// </remarks>
public sealed class DataResidencyTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    private IOptionsMonitor<NetCoreAIOptions> Options => _app.Services.GetRequiredService<IOptionsMonitor<NetCoreAIOptions>>();

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
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend()
            .AddBuiltInTools(t => t.FetchAllowedHosts.Add("docs.example.com"));

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

    /// <summary>Turns residency on for the rest of the test, allowing the named hosts.</summary>
    private void GoOffline(params string[] allowed)
    {
        var network = Options.CurrentValue.Network;
        network.OfflineMode = true;
        network.AllowedHosts.Clear();
        foreach (var host in allowed)
        {
            network.AllowedHosts.Add(host);
        }
    }

    private HttpClient Client(string name) =>
        _app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    [Theory]
    [InlineData(ToolInvoker.HttpClientName)]
    [InlineData(FetchTool.HttpClientName)]
    [InlineData("NetCoreAI.Hub")]
    [InlineData("NetCoreAI.Download")]
    public async Task Every_client_netcoreai_owns_refuses_an_unapproved_host(string clientName)
    {
        GoOffline("mirror.internal");

        // Including the two that were missing it. A residency switch that covers some of the ways out of
        // the process is not a residency switch.
        var error = await Assert.ThrowsAsync<OfflineModeException>(() =>
            Client(clientName).GetAsync(new Uri("https://api.openai.com/v1/models"), Ct));

        Assert.Contains("api.openai.com", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_call_to_a_loopback_endpoint_still_works()
    {
        GoOffline();

        // The switch must not break tools that call this host's own endpoints, which is most of them —
        // and is the whole design of endpoint tools. Nothing is listening on this port, so the call fails
        // at the socket; what matters is that it got that far rather than being refused by the policy.
        var error = await Record.ExceptionAsync(() =>
            Client(ToolInvoker.HttpClientName).GetAsync(new Uri("http://localhost:1/api/orders/1"), Ct));

        Assert.IsNotType<OfflineModeException>(error);
    }

    [Fact]
    public async Task The_fetch_tool_refuses_a_site_it_would_otherwise_allow()
    {
        GoOffline("mirror.internal");

        var fetch = new FetchTool(
            _app.Services.GetRequiredService<IHttpClientFactory>(),
            _app.Services.GetRequiredService<IOptionsMonitor<BuiltInToolOptions>>());

        // docs.example.com is on the tool's own allow-list, so this gets past that gate and is stopped by
        // the host's. Two allow-lists, and a fetch has to satisfy both.
        var result = await fetch.FetchAsync("https://docs.example.com/page", Ct);

        Assert.Contains("did not call", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remote_provider_connection_outside_the_allow_list_is_refused()
    {
        var connections = _app.Services.GetRequiredService<IConnectionManager>();
        await connections.SaveAsync(
            new ProviderConnection
            {
                Id = "remote",
                Name = "Somewhere else",
                ProviderId = OpenAICompatibleProvider.ProviderId,
                BaseUrl = "https://api.openai.com/v1",
            },
            "sk-test",
            Ct);

        GoOffline("mirror.internal");

        // Refused when the connection is resolved, which is before any request is built — so a provider
        // package bringing its own HttpClient is covered without needing the handler.
        var result = await connections.TestAsync("remote", Ct);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task With_the_switch_off_nothing_changes()
    {
        // The default. A host that has not asked for residency should not be able to tell it exists, and
        // the refusal above must be the switch rather than a broken client.
        var error = await Record.ExceptionAsync(() =>
            Client(ToolInvoker.HttpClientName).GetAsync(new Uri("https://localhost:1/unreachable"), Ct));

        Assert.IsNotType<OfflineModeException>(error);
    }
}
