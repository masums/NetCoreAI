using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Agents;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Providers;
using NetCoreAI.Telemetry;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// What an operator can see: spans for agent runs and tool calls, and a description of the API itself.
/// </summary>
public sealed class TelemetryAndOpenApiTests : IAsyncLifetime
{
    private FakeOpenAIServer _openAI = default!;
    private WebApplication _app = default!;
    private string _dataDir = "";
    private ActivityListener _listener = default!;
    private readonly List<Activity> _spans = [];

    /// <summary>
    /// Marks the async flow this test drives.
    /// </summary>
    /// <remarks>
    /// An ActivityListener is process-wide, and other test classes run agents and tools at the same time,
    /// so without this the collected spans are everybody's. The flag flows into the work this test starts
    /// and nowhere else.
    /// </remarks>
    private static readonly AsyncLocal<bool> Mine = new();

    private static readonly List<string> Endpoints = [];

    public async ValueTask InitializeAsync()
    {
        _openAI = await FakeOpenAIServer.StartAsync();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        // Nothing records a span unless something is listening, which is also why this has to be tested
        // with a listener rather than by reading the code.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NetCoreAITelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (!Mine.Value)
                {
                    return;
                }

                lock (_spans)
                {
                    _spans.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(_listener);

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
        Endpoints.Clear();
        _app.MapGet("/api/orders/{id}", (string id) => { Endpoints.Add(id); return Results.Ok(new { id, total = 42 }); })
            .WithAITool("get_order", "Look up one order by its id.");
        _app.MapNetCoreAI();
        await _app.StartAsync();

        var connection = await _app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection { Id = "fake", Name = "Fake", ProviderId = OpenAICompatibleProvider.ProviderId, BaseUrl = _openAI.BaseUrl },
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
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
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

    private Activity Span(string name)
    {
        lock (_spans)
        {
            return Assert.Single(_spans, s => s.OperationName == name);
        }
    }

    private async Task RunAsync(string message, bool withTool = false)
    {
        Mine.Value = true;
        var toolIds = new List<string>();
        if (withTool)
        {
            var endpoint = Assert.Single(
                _app.Services.GetRequiredService<IEndpointDiscovery>().Discover(),
                e => e.Method == "GET" && e.Route == "/api/orders/{id}").Id;

            toolIds.Add((await _app.Services.GetRequiredService<IToolService>().CreateFromEndpointAsync(endpoint, Ct)).Id);
        }

        var agents = _app.Services.GetRequiredService<IAgentService>();
        await agents.SaveAsync(new AgentDefinition { Id = "helper", Name = "Helper", Model = "default", ToolIds = toolIds }, Ct);
        await agents.RunAsync("helper", new AgentRequest { Message = message }, new AgentCaller(), Ct);
    }

    // ---------- spans ----------

    [Fact]
    public async Task An_agent_run_produces_a_span_named_by_the_genai_conventions()
    {
        await RunAsync("hello");

        var span = Span("invoke_agent Helper");
        Assert.Equal("invoke_agent", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("helper", span.GetTagItem("gen_ai.agent.id"));
        Assert.Equal("default", span.GetTagItem("gen_ai.request.model"));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
    }

    [Fact]
    public async Task A_run_span_carries_the_run_id_so_a_trace_leads_to_the_stored_one()
    {
        await RunAsync("hello");

        var runId = Span("invoke_agent Helper").GetTagItem("netcoreai.run.id") as string;

        // A span says a run happened; the stored trace says what it did. Without the id joining them, an
        // operator who sees a slow span has nowhere to go next.
        Assert.NotNull(await _app.Services.GetRequiredService<IAgentService>().GetRunAsync(runId!, Ct));
    }

    [Fact]
    public async Task A_tool_call_produces_its_own_span()
    {
        await RunAsync("""Order A-1 args:{"id":"A-1"}""", withTool: true);

        var span = Span("execute_tool get_order");
        Assert.Equal("execute_tool", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("get_order", span.GetTagItem("gen_ai.tool.name"));
        Assert.Equal(200, span.GetTagItem("http.response.status_code"));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
    }

    [Fact]
    public async Task A_failed_run_is_marked_as_an_error_on_its_span()
    {
        Mine.Value = true;
        var agents = _app.Services.GetRequiredService<IAgentService>();
        await agents.SaveAsync(new AgentDefinition { Id = "broken", Name = "Broken", Model = "no-such-model" }, Ct);

        await Assert.ThrowsAsync<NetCoreAIException>(() =>
            agents.RunAsync("broken", new AgentRequest { Message = "hi" }, new AgentCaller(), Ct));

        Assert.Equal(ActivityStatusCode.Error, Span("invoke_agent Broken").Status);
    }

    [Fact]
    public async Task Spans_do_not_carry_the_question_or_the_answer()
    {
        await RunAsync("my national insurance number is QQ123456C");

        // Telemetry goes wherever the host exports it. The stored trace holds the content; the span holds
        // the shape, so turning on tracing does not quietly start shipping people's messages elsewhere.
        var span = Span("invoke_agent Helper");
        foreach (var (_, value) in span.TagObjects)
        {
            Assert.DoesNotContain("QQ123456C", value?.ToString() ?? "", StringComparison.Ordinal);
        }
    }

    // ---------- the API description ----------

    [Fact]
    public async Task The_openapi_document_describes_the_api()
    {
        var document = await _app.GetTestClient().GetFromJsonAsync<JsonElement>("/netcoreai/openapi/v1.json", Ct);

        Assert.Equal("3.1.0", document.GetProperty("openapi").GetString());
        Assert.Equal("NetCoreAI", document.GetProperty("info").GetProperty("title").GetString());

        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/netcoreai/api/agents", out _));
        Assert.True(paths.TryGetProperty("/netcoreai/api/kb", out _));
        Assert.True(paths.TryGetProperty("/netcoreai/api/tools", out _));
    }

    [Fact]
    public async Task A_path_parameter_is_described_as_one()
    {
        var document = await _app.GetTestClient().GetFromJsonAsync<JsonElement>("/netcoreai/openapi/v1.json", Ct);

        var parameters = document.GetProperty("paths").GetProperty("/netcoreai/api/agents/{id}")
            .GetProperty("get").GetProperty("parameters");

        var id = Assert.Single(parameters.EnumerateArray());
        Assert.Equal("id", id.GetProperty("name").GetString());
        Assert.Equal("path", id.GetProperty("in").GetString());
        Assert.True(id.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task The_document_says_how_to_authenticate()
    {
        var document = await _app.GetTestClient().GetFromJsonAsync<JsonElement>("/netcoreai/openapi/v1.json", Ct);

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("apiKey");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task The_document_leaves_out_the_host_own_pages_and_assets()
    {
        var document = await _app.GetTestClient().GetFromJsonAsync<JsonElement>("/netcoreai/openapi/v1.json", Ct);
        var paths = document.GetProperty("paths");

        // A client generator should produce a client for the API, not for the dashboard's HTML.
        Assert.False(paths.TryGetProperty("/netcoreai/agents", out _));
        Assert.False(paths.TryGetProperty("/netcoreai/_content/{**file}", out _));

        // And not the host's own endpoints either: this describes NetCoreAI, not the application it is in.
        Assert.False(paths.TryGetProperty("/api/orders/{id}", out _));
    }

    [Fact]
    public async Task The_document_is_readable_without_a_key()
    {
        // A description of how to authenticate is no use only to callers who already can.
        var response = await _app.GetTestClient().GetAsync("/netcoreai/openapi/v1.json", Ct);

        Assert.True(response.IsSuccessStatusCode);
    }
}
