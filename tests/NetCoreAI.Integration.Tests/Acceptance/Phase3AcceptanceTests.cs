using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Agents;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Client;
using NetCoreAI.Providers;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests.Acceptance;

/// <summary>
/// Phase 3 against a real model, rather than a stub that answers however the test wants.
/// </summary>
/// <remarks>
/// Skipped unless <c>OPENROUTER_FREE_KEY</c> is set, so the suite still runs offline and in CI without
/// secrets. What a fake cannot show is whether a real model, given a tool built from an ordinary endpoint
/// and nothing but its description to go on, decides to call it and with what — which is the whole claim
/// Phase 3 makes.
/// </remarks>
public sealed class Phase3AcceptanceTests : IAsyncLifetime
{
    private WebApplication? _app;
    private ServiceProvider? _remote;
    private string _dataDir = "";
    private static readonly List<string> Calls = [];

    /// <summary>
    /// The key, from the process environment or the user's.
    /// </summary>
    /// <remarks>
    /// A variable set with <c>setx</c> or in the Windows settings dialog does not reach a process that was
    /// already running, which is the commonest reason a developer's "I set it" and a test's "it is not
    /// set" are both true.
    /// </remarks>
    private static string? Key =>
        Environment.GetEnvironmentVariable("OPENROUTER_FREE_KEY")
        ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetEnvironmentVariable("OPENROUTER_FREE_KEY", EnvironmentVariableTarget.User)
            : null);

    private static string Model =>
        Environment.GetEnvironmentVariable("OPENROUTER_FREE_MODEL")
        ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetEnvironmentVariable("OPENROUTER_FREE_MODEL", EnvironmentVariableTarget.User)
            : null)
        ?? "openrouter/free";

    private const string Skip = "Set OPENROUTER_FREE_KEY to run the Phase 3 acceptance against a real model.";

    public async ValueTask InitializeAsync()
    {
        if (Key is null)
        {
            return;
        }

        Calls.Clear();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
            o.Tools.BaseAddress = new Uri("http://localhost/");
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        builder.Services.AddHttpClient(ToolInvoker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler());

        _app = builder.Build();

        // An ordinary endpoint of an ordinary application, marked as usable by a model. This is the whole
        // of what a developer writes for goal G3.
        _app.MapGet("/api/orders/{id}", (string id, HttpContext http) =>
        {
            Calls.Add(id);
            return Results.Ok(new { id, status = "shipped", total = 42.50m, carrier = "DHL" });
        }).WithAITool("get_order", "Look up one customer order by its id, returning its status, total and carrier.");

        _app.MapNetCoreAI();
        await _app.StartAsync();

        var connection = await _app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection
            {
                Id = "openrouter",
                Name = "OpenRouter",
                ProviderId = OpenAICompatibleProvider.ProviderId,
                BaseUrl = "https://openrouter.ai/api/v1",
            },
            Key,
            TestContext.Current.CancellationToken);

        await _app.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "default",
            Name = "OpenRouter free",
            Format = ModelFormat.Remote,
            ProviderId = OpenAICompatibleProvider.ProviderId,
            ConnectionId = connection.Id,
            RemoteModelId = Model,
            Capabilities = new ModelCapabilities(ModelCapability.Chat | ModelCapability.ToolCalling),
        }, TestContext.Current.CancellationToken);

        var remote = new ServiceCollection();
        remote.AddLogging();
        remote.AddNetCoreAIClient(o => o.BaseUrl = new Uri("http://localhost/netcoreai"));
        remote.AddHttpClient(NetCoreAIClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _app.GetTestServer().CreateHandler());

        _remote = remote.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_remote is not null)
        {
            await _remote.DisposeAsync();
        }

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Runs the body, skipping the test when the provider rate-limits rather than failing it.
    /// </summary>
    /// <remarks>
    /// A free tier refusing a burst is a fact about the tier, not a defect in NetCoreAI, and a suite that
    /// went red for it would train everyone to ignore red. Only an explicit rate-limit refusal is skipped:
    /// anything else is a real failure and stays one.
    /// </remarks>
    private static async Task UnlessRateLimitedAsync(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (NetCoreAIException ex) when (ex.Message.Contains("429", StringComparison.Ordinal)
            || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip($"The provider rate-limited this run: {ex.Message}");
        }
    }

    private async Task<AgentDefinition> AgentAsync()
    {
        var endpoint = Assert.Single(
            _app!.Services.GetRequiredService<IEndpointDiscovery>().Discover(),
            e => e.Method == "GET" && e.Route == "/api/orders/{id}").Id;

        var tool = await _app.Services.GetRequiredService<IToolService>().CreateFromEndpointAsync(endpoint, Ct);

        return await _app.Services.GetRequiredService<IAgentService>().SaveAsync(
            new AgentDefinition
            {
                Id = "support",
                Name = "Support agent",
                Model = "default",
                SystemPrompt = "You help customers with their orders. Use the tools available to you. Be brief.",
                ToolIds = [tool.Id],
                Parameters = new ModelParameters { Temperature = 0 },
            },
            Ct);
    }

    [Fact]
    public async Task G3_a_real_model_calls_a_tool_built_from_an_ordinary_endpoint()
    {
        Assert.SkipUnless(Key is not null, Skip);
        await UnlessRateLimitedAsync(async () =>
        {
            await AgentAsync();

            var response = await _app!.Services.GetRequiredService<IAgentService>()
                .RunAsync("support", new AgentRequest { Message = "What is the status of order A-7?" }, new AgentCaller(), Ct);

            // The model was told nothing about this endpoint but the description on the attribute. That it
            // calls it, with the right id, is the whole claim: an endpoint becomes a tool without glue code.
            Assert.Equal("A-7", Assert.Single(Calls));

            var step = Assert.Single(response.Steps, s => s.Kind == RunStep.ToolKind);
            Assert.Equal("get_order", step.Name);
            Assert.Contains("shipped", step.Output!, StringComparison.OrdinalIgnoreCase);

            // And that it answers from what came back, rather than from whatever it imagined.
            Assert.Contains("shipped", response.Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task G4_the_same_agent_answers_over_http_as_it_does_in_process()
    {
        Assert.SkipUnless(Key is not null, Skip);
        await UnlessRateLimitedAsync(async () =>
        {
            await AgentAsync();
            var question = new AgentRequest { Message = "What is the status of order A-7?" };

            var inside = await _app!.Services.GetRequiredService<IAgentClient>().RunAsync("support", question, Ct);
            var outside = await _remote!.GetRequiredService<IAgentClient>().RunAsync("support", question, Ct);

            // A real model at temperature zero is not perfectly deterministic, so this asserts that both paths
            // did the same work and reached the same facts — not that two generations matched character for
            // character, which would be a test of the provider rather than of NetCoreAI.
            Assert.Equal(["A-7", "A-7"], Calls);
            Assert.Contains("shipped", inside.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("shipped", outside.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                inside.Steps.Select(s => (s.Kind, s.Name)),
                outside.Steps.Select(s => (s.Kind, s.Name)));
        });
    }

    [Fact]
    public async Task A_locked_parameter_is_never_filled_in_by_a_real_model()
    {
        Assert.SkipUnless(Key is not null, Skip);
        await UnlessRateLimitedAsync(async () =>
        {
            var agent = await AgentAsync();
            var tools = _app!.Services.GetRequiredService<IToolService>();
            var tool = Assert.Single(await tools.ListAsync(Ct), t => t.Name == "get_order");

            // The id becomes the host's to supply. A model that has been told about orders and is asked about
            // one will try to send an id; the point is that it cannot.
            await tools.SaveAsync(
                tool with
                {
                    Parameters = [new ToolParameter { Name = "id", Location = ParameterLocation.Route, Required = true, Binding = ParameterBinding.Static, BindingSource = "LOCKED-1" }],
                },
                cancellationToken: Ct);

            await _app.Services.GetRequiredService<IAgentService>()
                .RunAsync(agent.Id, new AgentRequest { Message = "What is the status of order A-7?" }, new AgentCaller(), Ct);

            Assert.Equal("LOCKED-1", Assert.Single(Calls));
        });
    }

}
