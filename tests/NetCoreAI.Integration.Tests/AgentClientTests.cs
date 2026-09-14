using System.Security.Claims;
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

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Goal G4: the same agent, asked the same thing, answers identically whether it is run through
/// <c>IAgentClient</c> in the host or over HTTP from somewhere else.
/// </summary>
public sealed class AgentClientTests : IAsyncLifetime
{
    private FakeOpenAIServer _openAI = default!;
    private WebApplication _app = default!;
    private ServiceProvider _remote = default!;
    private string _dataDir = "";
    private static readonly List<string> Calls = [];

    /// <summary>The client an application inside the host would inject.</summary>
    private IAgentClient InProcess => _app.Services.GetRequiredService<IAgentClient>();

    /// <summary>The client an application somewhere else would inject.</summary>
    private IAgentClient OverHttp => _remote.GetRequiredService<IAgentClient>();

    public async ValueTask InitializeAsync()
    {
        Calls.Clear();
        _openAI = await FakeOpenAIServer.StartAsync();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;

            // Agents here run outside any request, which is what a background job does. Without a
            // configured address their loopback tool calls have nowhere to go — the same setup a host
            // needs when its agents run on a schedule.
            o.Tools.BaseAddress = new Uri("http://localhost/");
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        builder.Services.AddHttpClient(ToolInvoker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler());

        _app = builder.Build();

        _app.MapGet("/api/orders/{id}", (string id, HttpContext http) =>
        {
            Calls.Add($"GET {http.Request.Path}");
            return Results.Ok(new { id, total = 42 });
        }).WithAITool("get_order", "Look up one order by its id.");

        _app.MapNetCoreAI();
        await _app.StartAsync();

        var connection = await _app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection
            {
                Id = "fake",
                Name = "Fake OpenAI",
                ProviderId = OpenAICompatibleProvider.ProviderId,
                BaseUrl = _openAI.BaseUrl,
            },
            "sk-test",
            TestContext.Current.CancellationToken);

        await _app.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "default",
            Name = "Fake chat",
            Format = ModelFormat.Remote,
            ProviderId = OpenAICompatibleProvider.ProviderId,
            ConnectionId = connection.Id,
            RemoteModelId = "fake-chat",
            Capabilities = new ModelCapabilities(ModelCapability.Chat | ModelCapability.ToolCalling),
        }, TestContext.Current.CancellationToken);

        // The separate application: nothing but the client package, pointed at the host.
        var remote = new ServiceCollection();
        remote.AddLogging();
        remote.AddNetCoreAIClient(o => o.BaseUrl = new Uri("http://localhost/netcoreai"));
        remote.AddHttpClient(NetCoreAIClientServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _app.GetTestServer().CreateHandler());

        _remote = remote.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _remote.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _openAI.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AgentDefinition> SaveAgentAsync(bool withTool = false)
    {
        var agents = _app.Services.GetRequiredService<IAgentService>();
        var toolIds = new List<string>();

        if (withTool)
        {
            var endpoint = Assert.Single(
                _app.Services.GetRequiredService<IEndpointDiscovery>().Discover(),
                e => e.Method == "GET" && e.Route == "/api/orders/{id}").Id;

            toolIds.Add((await _app.Services.GetRequiredService<IToolService>().CreateFromEndpointAsync(endpoint, Ct)).Id);
        }

        return await agents.SaveAsync(
            new AgentDefinition
            {
                Id = "helper",
                Name = "Helper",
                Model = "default",
                SystemPrompt = "You are a helpful assistant.",
                ToolIds = toolIds,

                // Temperature zero, so a difference between the two paths would be a real difference and
                // not the model being creative.
                Parameters = new ModelParameters { Temperature = 0 },
            },
            Ct);
    }

    // ---------- G4 ----------

    [Fact]
    public async Task The_same_agent_answers_the_same_in_process_and_over_http()
    {
        await SaveAgentAsync();
        var request = new AgentRequest { Message = "what is the holiday policy?" };

        var inside = await InProcess.RunAsync("helper", request, Ct);
        var outside = await OverHttp.RunAsync("helper", request, Ct);

        Assert.Equal(inside.Text, outside.Text);
        Assert.Equal(inside.ModelId, outside.ModelId);
        Assert.NotEqual(inside.RunId, outside.RunId);
    }

    [Fact]
    public async Task A_tool_calling_agent_behaves_the_same_both_ways()
    {
        await SaveAgentAsync(withTool: true);
        var request = new AgentRequest { Message = """Order A-1 args:{"id":"A-1"}""" };

        var inside = await InProcess.RunAsync("helper", request, Ct);
        var outside = await OverHttp.RunAsync("helper", request, Ct);

        Assert.Equal(inside.Text, outside.Text);
        Assert.Equal(["GET /api/orders/A-1", "GET /api/orders/A-1"], Calls);

        // The steps are what a caller shows as "what it did", so they have to survive the wire too.
        Assert.Equal(
            inside.Steps.Select(s => (s.Kind, s.Name)),
            outside.Steps.Select(s => (s.Kind, s.Name)));
    }

    [Fact]
    public async Task Streaming_produces_the_same_answer_as_waiting_for_it()
    {
        await SaveAgentAsync();

        var whole = await OverHttp.RunAsync("helper", new AgentRequest { Message = "hello" }, Ct);

        var streamed = new System.Text.StringBuilder();
        await foreach (var text in OverHttp.StreamTextAsync("helper", "hello", Ct))
        {
            streamed.Append(text);
        }

        Assert.Equal(whole.Text, streamed.ToString());
    }

    [Fact]
    public async Task Streaming_over_http_carries_the_steps_and_the_final_response()
    {
        await SaveAgentAsync(withTool: true);

        var types = new List<string>();
        AgentResponse? final = null;
        await foreach (var evt in OverHttp.RunStreamingAsync("helper", new AgentRequest { Message = """Order A-1 args:{"id":"A-1"}""" }, Ct))
        {
            types.Add(evt.Type);
            if (evt.Type == AgentEvent.DoneType)
            {
                final = evt.Response;
            }
        }

        Assert.Contains(AgentEvent.StepType, types);
        Assert.Equal(AgentEvent.DoneType, types[^1]);
        Assert.NotNull(final);
        Assert.Contains(final!.Steps, s => s.Name == "get_order");
    }

    [Fact]
    public async Task Agents_can_be_listed_from_either_side()
    {
        await SaveAgentAsync();

        Assert.Equal(
            (await InProcess.ListAsync(Ct)).Select(a => a.Id),
            (await OverHttp.ListAsync(Ct)).Select(a => a.Id));
    }

    // ---------- failures travel too ----------

    [Fact]
    public async Task An_agent_that_does_not_exist_fails_the_same_way_both_ways()
    {
        var inside = await Assert.ThrowsAsync<NetCoreAIException>(() => InProcess.RunAsync("nope", new AgentRequest { Message = "hi" }, Ct));
        var outside = await Assert.ThrowsAsync<NetCoreAIException>(() => OverHttp.RunAsync("nope", new AgentRequest { Message = "hi" }, Ct));

        Assert.Contains("No agent", inside.Message, StringComparison.Ordinal);
        Assert.Contains("No agent", outside.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_host_names_the_url_it_tried()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetCoreAIClient(o =>
        {
            o.BaseUrl = new Uri("http://127.0.0.1:1/netcoreai");
            o.Timeout = TimeSpan.FromSeconds(5);
        });

        await using var provider = services.BuildServiceProvider();

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            provider.GetRequiredService<IAgentClient>().RunAsync("helper", new AgentRequest { Message = "hi" }, Ct));

        Assert.Contains("127.0.0.1:1", error.Message, StringComparison.Ordinal);
    }

    // ---------- who a run acts as ----------

    [Fact]
    public async Task A_run_with_no_request_behind_it_acts_as_nobody()
    {
        // A background service has no caller, so an agent restricted by access tags must not run for it.
        // The alternative — treating "no user" as "any user" — would make every restriction optional.
        await _app.Services.GetRequiredService<IAgentService>().SaveAsync(
            new AgentDefinition { Id = "restricted", Name = "Restricted", Model = "default", AclTags = ["role:finance"] },
            Ct);

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            InProcess.RunAsync("restricted", new AgentRequest { Message = "hi" }, Ct));

        Assert.Contains("not allowed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_inside_a_request_acts_as_the_caller()
    {
        await _app.Services.GetRequiredService<IAgentService>().SaveAsync(
            new AgentDefinition { Id = "restricted", Name = "Restricted", Model = "default", AclTags = ["role:finance"] },
            Ct);

        var accessor = _app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "finance")], "test")),
            RequestServices = _app.Services,
        };

        try
        {
            var response = await InProcess.RunAsync("restricted", new AgentRequest { Message = "hi" }, Ct);
            Assert.NotEmpty(response.Text);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
