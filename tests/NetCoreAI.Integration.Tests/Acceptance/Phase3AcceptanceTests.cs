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
/// Phase 3 against real models, rather than a stub that answers however the test wants.
/// </summary>
/// <remarks>
/// Runs once per provider this machine has a key for, and skips when there is none — so the suite still
/// runs offline and in CI without secrets. What a fake cannot show is whether a real model, given a tool
/// built from an ordinary endpoint and nothing but its description to go on, decides to call it and with
/// what. Running against more than one provider is deliberate: one provider hides the assumptions made
/// about it.
/// </remarks>
public sealed class Phase3AcceptanceTests
{
    private const string Skip = "No live provider key is configured. Set OPENROUTER_FREE_KEY or GEMINI_FREE_KEY.";

    /// <summary>Provider names, so the test output names the one that ran.</summary>
    public static TheoryData<string> Providers
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var provider in LiveProvider.Available)
            {
                data.Add(provider.Name);
            }

            // A Theory with no data does not run at all, which reads as "passed" rather than "skipped".
            if (data.Count == 0)
            {
                data.Add("none");
            }

            return data;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One host with one real provider behind it, and the endpoint a tool is built from.</summary>
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required ServiceProvider Remote { get; init; }

        public required List<string> Calls { get; init; }

        public required string DataDirectory { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Remote.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
            try
            {
                if (Directory.Exists(DataDirectory))
                {
                    Directory.Delete(DataDirectory, true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<Host> StartAsync(LiveProvider provider)
    {
        var calls = new List<string>();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = dataDirectory;
            o.Dashboard.AllowAnonymous = true;
            o.Tools.BaseAddress = new Uri("http://localhost/");
        })
            .AddSqliteStorage($"Data Source={Path.Combine(dataDirectory, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        builder.Services.AddHttpClient(ToolInvoker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler());

        var app = builder.Build();

        // An ordinary endpoint of an ordinary application, marked usable by a model. This is the whole of
        // what a developer writes for goal G3.
        app.MapGet("/api/orders/{id}", (string id) =>
        {
            calls.Add(id);
            return Results.Ok(new { id, status = "shipped", total = 42.50m, carrier = "DHL" });
        }).WithAITool("get_order", "Look up one customer order by its id, returning its status, total and carrier.");

        app.MapNetCoreAI();
        await app.StartAsync();

        var connection = await app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection
            {
                Id = "live",
                Name = provider.Name,
                ProviderId = OpenAICompatibleProvider.ProviderId,
                BaseUrl = provider.BaseUrl,
            },
            provider.Key,
            TestContext.Current.CancellationToken);

        await app.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "default",
            Name = provider.Model,
            Format = ModelFormat.Remote,
            ProviderId = OpenAICompatibleProvider.ProviderId,
            ConnectionId = connection.Id,
            RemoteModelId = provider.Model,
            Capabilities = new ModelCapabilities(ModelCapability.Chat | ModelCapability.ToolCalling),
        }, TestContext.Current.CancellationToken);

        var remote = new ServiceCollection();
        remote.AddLogging();
        remote.AddNetCoreAIClient(o => o.BaseUrl = new Uri("http://localhost/netcoreai"));
        remote.AddHttpClient(NetCoreAIClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());

        return new Host
        {
            App = app,
            Remote = remote.BuildServiceProvider(),
            Calls = calls,
            DataDirectory = dataDirectory,
        };
    }

    private static async Task<AgentDefinition> AgentAsync(Host host)
    {
        var endpoint = Assert.Single(
            host.App.Services.GetRequiredService<IEndpointDiscovery>().Discover(),
            e => e.Method == "GET" && e.Route == "/api/orders/{id}").Id;

        var tool = await host.App.Services.GetRequiredService<IToolService>().CreateFromEndpointAsync(endpoint, Ct);

        return await host.App.Services.GetRequiredService<IAgentService>().SaveAsync(
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

    /// <summary>
    /// Runs the body against the named provider, skipping rather than failing when it is unavailable.
    /// </summary>
    /// <remarks>
    /// A free tier refusing a burst is a fact about the tier, not a defect here, and a suite that goes red
    /// for it teaches everyone to ignore red. Only an explicit rate-limit refusal is skipped; anything else
    /// is a real failure and stays one.
    /// </remarks>
    private static async Task AgainstAsync(string providerName, Func<Host, Task> body, bool needsMultiTurnTools = false)
    {
        var provider = LiveProvider.Available.FirstOrDefault(p => p.Name == providerName);
        Assert.SkipUnless(provider is not null, Skip);
        Assert.SkipWhen(
            needsMultiTurnTools && !provider!.MultiTurnTools,
            $"{providerName} cannot continue a conversation after a tool call through its OpenAI-compatible endpoint. See LiveProvider.MultiTurnTools and GeminiToolLimitationTests.");

        await using var host = await StartAsync(provider!);
        try
        {
            await body(host);
        }
        catch (NetCoreAIException ex) when (ex.Message.Contains("429", StringComparison.Ordinal)
            || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip($"{providerName} rate-limited this run: {ex.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public Task G3_a_real_model_calls_a_tool_built_from_an_ordinary_endpoint(string provider) =>
        AgainstAsync(provider, async host =>
        {
            await AgentAsync(host);

            var response = await host.App.Services.GetRequiredService<IAgentService>()
                .RunAsync("support", new AgentRequest { Message = "What is the status of order A-7?" }, new AgentCaller(), Ct);

            // The model was told nothing about this endpoint but the description on the attribute. That it
            // calls it, with the right id, is the whole claim: an endpoint becomes a tool without glue code.
            Assert.Equal("A-7", Assert.Single(host.Calls));

            var step = Assert.Single(response.Steps, s => s.Kind == RunStep.ToolKind);
            Assert.Equal("get_order", step.Name);
            Assert.Contains("shipped", step.Output!, StringComparison.OrdinalIgnoreCase);

            // And that it answers from what came back, rather than from whatever it imagined.
            Assert.Contains("shipped", response.Text, StringComparison.OrdinalIgnoreCase);
        }, needsMultiTurnTools: true);

    [Theory]
    [MemberData(nameof(Providers))]
    public Task G4_the_same_agent_answers_over_http_as_it_does_in_process(string provider) =>
        AgainstAsync(provider, async host =>
        {
            await AgentAsync(host);
            var question = new AgentRequest { Message = "What is the status of order A-7?" };

            var inside = await host.App.Services.GetRequiredService<IAgentClient>().RunAsync("support", question, Ct);
            var outside = await host.Remote.GetRequiredService<IAgentClient>().RunAsync("support", question, Ct);

            // A real model at temperature zero is not perfectly deterministic, so this asserts both paths
            // did the same work and reached the same facts — not that two generations matched character for
            // character, which would test the provider rather than NetCoreAI.
            Assert.Equal(["A-7", "A-7"], host.Calls);
            Assert.Contains("shipped", inside.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("shipped", outside.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                inside.Steps.Select(s => (s.Kind, s.Name)),
                outside.Steps.Select(s => (s.Kind, s.Name)));
        }, needsMultiTurnTools: true);

    [Theory]
    [MemberData(nameof(Providers))]
    public Task A_locked_parameter_is_never_filled_in_by_a_real_model(string provider) =>
        AgainstAsync(provider, async host =>
        {
            var agent = await AgentAsync(host);
            var tools = host.App.Services.GetRequiredService<IToolService>();
            var tool = Assert.Single(await tools.ListAsync(Ct), t => t.Name == "get_order");

            // The id becomes the host's to supply. A model told about orders and asked about one will try
            // to send an id; the point is that it cannot.
            await tools.SaveAsync(
                tool with
                {
                    Parameters = [new ToolParameter { Name = "id", Location = ParameterLocation.Route, Required = true, Binding = ParameterBinding.Static, BindingSource = "LOCKED-1" }],
                },
                cancellationToken: Ct);

            await host.App.Services.GetRequiredService<IAgentService>()
                .RunAsync(agent.Id, new AgentRequest { Message = "What is the status of order A-7?" }, new AgentCaller(), Ct);

            Assert.Equal("LOCKED-1", Assert.Single(host.Calls));
        }, needsMultiTurnTools: true);
}
