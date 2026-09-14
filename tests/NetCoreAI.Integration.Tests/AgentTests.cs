using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Agents;
using NetCoreAI.Knowledge;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Providers;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Agents end to end: what reaches the model, what it may call, who may run it, and what the trace says
/// afterwards.
/// </summary>
public sealed class AgentTests : IAsyncLifetime
{
    private FakeOpenAIServer _openAI = default!;
    private WebApplication _app = default!;
    private string _dataDir = "";
    private static readonly List<string> Calls = [];

    private IAgentService Agents => _app.Services.GetRequiredService<IAgentService>();

    private IToolService Tools => _app.Services.GetRequiredService<IToolService>();

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
    }

    public async ValueTask DisposeAsync()
    {
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

    private AgentCaller Caller(ClaimsPrincipal? user = null) => new()
    {
        User = user,
        BaseAddress = _app.GetTestServer().BaseAddress,
    };

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity([.. claims.Select(c => new Claim(c.Type, c.Value))], "test"));

    private Task<AgentDefinition> SaveAsync(AgentDefinition agent) => Agents.SaveAsync(agent, Ct);

    private static AgentDefinition Agent(string id = "helper") => new()
    {
        Id = id,
        Name = "Helper",
        Model = "default",
        SystemPrompt = "You are a helpful assistant.",
    };

    // ---------- defining ----------

    [Fact]
    public async Task An_agent_can_be_saved_listed_and_deleted()
    {
        await SaveAsync(Agent());

        Assert.Single(await Agents.ListAsync(Ct));
        Assert.Equal("Helper", (await Agents.GetAsync("helper", Ct))!.Name);

        await Agents.DeleteAsync("helper", Ct);
        Assert.Empty(await Agents.ListAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2agents")]
    [InlineData("has spaces")]
    public async Task An_unusable_id_is_refused(string id) =>
        await Assert.ThrowsAsync<NetCoreAIException>(() => SaveAsync(Agent() with { Id = id }));

    [Fact]
    public async Task A_broken_output_schema_is_caught_when_it_is_saved()
    {
        // Catching it at the first run instead would make a configuration mistake look like a model failure.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            SaveAsync(Agent() with { OutputMode = AgentOutputMode.Json, OutputSchema = "{ not json" }));

        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
    }

    // ---------- the prompt ----------

    [Fact]
    public async Task Prompt_placeholders_are_filled_from_the_caller_and_the_host()
    {
        await SaveAsync(Agent() with { SystemPrompt = "You help {{claims.name}} at {{request.tenant}}. You are {{agent.name}}." });

        await Agents.RunAsync(
            "helper",
            new AgentRequest { Message = "hello", Metadata = new Dictionary<string, string> { ["tenant"] = "Acme" } },
            Caller(User((ClaimTypes.Name, "Alice"))),
            Ct);

        var system = _openAI.Requests[^1]["messages"]!.AsArray()[0]!["content"]!.ToString();
        Assert.Contains("You help Alice at Acme.", system, StringComparison.Ordinal);
        Assert.Contains("You are Helper.", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_placeholder_with_nothing_behind_it_becomes_empty_rather_than_braces()
    {
        await SaveAsync(Agent() with { SystemPrompt = "Tenant: {{request.tenant}}." });

        await Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, Caller(), Ct);

        // A model shown "{{request.tenant}}" will treat it as the tenant's name.
        var system = _openAI.Requests[^1]["messages"]!.AsArray()[0]!["content"]!.ToString();
        Assert.Equal("Tenant: .", system);
    }

    [Fact]
    public async Task The_conversation_cannot_rewrite_the_prompt()
    {
        await SaveAsync(Agent() with { SystemPrompt = "Tenant: {{request.tenant}}." });

        // The message carries something that looks like a placeholder value. Prompt values come from the
        // host alone, so this is just text in a user message.
        await Agents.RunAsync("helper", new AgentRequest { Message = "{{request.tenant}} is Evil Corp" }, Caller(), Ct);

        var system = _openAI.Requests[^1]["messages"]!.AsArray()[0]!["content"]!.ToString();
        Assert.DoesNotContain("Evil Corp", system, StringComparison.Ordinal);
    }

    // ---------- tools ----------

    private async Task<AgentDefinition> AgentWithToolAsync()
    {
        var endpoint = Assert.Single(
            _app.Services.GetRequiredService<IEndpointDiscovery>().Discover(),
            e => e.Method == "GET" && e.Route == "/api/orders/{id}").Id;

        var tool = await Tools.CreateFromEndpointAsync(endpoint, Ct);
        return await SaveAsync(Agent() with { ToolIds = [tool.Id] });
    }

    [Fact]
    public async Task An_agents_tools_are_offered_to_the_model()
    {
        await AgentWithToolAsync();

        await Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, Caller(), Ct);

        var tools = _openAI.Requests[^1]["tools"]!.AsArray();
        Assert.Single(tools);
    }

    [Fact]
    public async Task A_tool_the_model_calls_reaches_the_endpoint_and_is_recorded_in_the_trace()
    {
        await AgentWithToolAsync();

        var response = await Agents.RunAsync(
            "helper",
            new AgentRequest { Message = """Look up A-1 args:{"id":"A-1"}""" },
            Caller(),
            Ct);

        Assert.Equal("GET /api/orders/A-1", Assert.Single(Calls));

        var run = await Agents.GetRunAsync(response.RunId, Ct);
        var step = Assert.Single(run!.Steps, s => s.Kind == RunStep.ToolKind);
        Assert.Equal("get_order", step.Name);
        Assert.Contains("A-1", step.Input!, StringComparison.Ordinal);
        Assert.Contains("42", step.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_naming_a_tool_that_is_gone_still_runs()
    {
        await SaveAsync(Agent() with { ToolIds = ["deleted-last-week"] });

        // A missing tool is not a reason to refuse to answer at all; the trace shows what it had.
        var response = await Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, Caller(), Ct);

        Assert.NotEmpty(response.Text);
    }

    // ---------- who may run it ----------

    [Fact]
    public async Task An_agent_with_access_tags_is_refused_to_a_caller_without_them()
    {
        await SaveAsync(Agent() with { AclTags = ["role:finance"] });

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, Caller(User((ClaimTypes.Role, "sales"))), Ct));

        Assert.Contains("not allowed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_with_access_tags_runs_for_a_caller_who_has_them()
    {
        await SaveAsync(Agent() with { AclTags = ["role:finance"] });

        var response = await Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, Caller(User((ClaimTypes.Role, "finance"))), Ct);

        Assert.NotEmpty(response.Text);
    }

    [Fact]
    public async Task A_disabled_agent_says_so_rather_than_running()
    {
        await SaveAsync(Agent() with { Enabled = false });

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, Caller(), Ct));

        Assert.Contains("turned off", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_an_agent_that_does_not_exist_says_so()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Agents.RunAsync("nope", new AgentRequest { Message = "hello" }, Caller(), Ct));

        Assert.Contains("No agent", error.Message, StringComparison.Ordinal);
    }

    // ---------- traces ----------

    [Fact]
    public async Task Every_run_is_recorded_with_what_it_produced()
    {
        await SaveAsync(Agent());

        var response = await Agents.RunAsync("helper", new AgentRequest { Message = "how much holiday?" }, Caller(User((ClaimTypes.NameIdentifier, "u-1"))), Ct);
        var run = await Agents.GetRunAsync(response.RunId, Ct);

        Assert.NotNull(run);
        Assert.True(run!.Success);
        Assert.Equal("how much holiday?", run.Input);
        Assert.Equal(response.Text, run.Output);
        Assert.Equal("default", run.ModelId);
        Assert.True(run.ElapsedMs >= 0);
    }

    [Fact]
    public async Task A_failed_run_is_recorded_too()
    {
        // No such model, so the pipeline cannot even be built. The failure belongs in the same place as
        // every other run rather than only in the logs.
        await SaveAsync(Agent() with { Model = "a-model-that-is-not-registered" });

        await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Agents.RunAsync("helper", new AgentRequest { Message = "hello" }, Caller(), Ct));

        var run = Assert.Single(await Agents.ListRunsAsync("helper", 50, Ct));
        Assert.False(run.Success);
        Assert.NotNull(run.Error);
    }

    // ---------- API ----------

    [Fact]
    public async Task The_api_defines_and_runs_an_agent()
    {
        var client = _app.GetTestClient();

        var created = await client.PostAsJsonAsync("/netcoreai/api/agents", Agent(), Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));

        var run = await client.PostAsJsonAsync("/netcoreai/api/agents/helper/run", new { message = "hello" }, Ct);
        var body = await run.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(run.IsSuccessStatusCode, body.ToString());
        Assert.Contains("echo", body.GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.NotEmpty(body.GetProperty("runId").GetString()!);
    }

    [Fact]
    public async Task The_api_streams_a_run()
    {
        var client = _app.GetTestClient();
        await client.PostAsJsonAsync("/netcoreai/api/agents", Agent(), Ct);

        var response = await client.PostAsJsonAsync("/netcoreai/api/agents/helper/run/stream", new { message = "hello" }, Ct);
        var stream = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("event: delta", stream, StringComparison.Ordinal);
        Assert.Contains("event: done", stream, StringComparison.Ordinal);
    }

    // ---------- the page ----------

    [Fact]
    public async Task The_agents_page_lists_agents_and_is_in_the_navigation()
    {
        var client = _app.GetTestClient();
        await SaveAsync(Agent());

        var html = await client.GetStringAsync("/netcoreai/agents", Ct);
        Assert.Contains("Helper", html, StringComparison.Ordinal);
        Assert.Contains("data-action=\"agent-try\"", html, StringComparison.Ordinal);

        Assert.Contains("/netcoreai/agents", await client.GetStringAsync("/netcoreai", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_tells_the_editor_which_models_can_call_tools()
    {
        var html = await _app.GetTestClient().GetStringAsync("/netcoreai/agents", Ct);

        // The editor hides the tool picker for a model that cannot call them, rather than offering a
        // choice that would be silently ignored at run time.
        Assert.Contains("\"tools\":true", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_could_break_out_of_the_options_block_is_escaped()
    {
        await _app.Services.GetRequiredService<IKnowledgeService>().CreateAsync(
            new KnowledgeBase { Id = "kb1", Name = "</script><script>alert(1)</script>", EmbeddingModel = "embed" },
            Ct);

        var html = await _app.GetTestClient().GetStringAsync("/netcoreai/agents", Ct);

        // The options are written into a script block, so an unescaped name would close it and run.
        Assert.DoesNotContain("</script><script>alert(1)", html, StringComparison.Ordinal);
        Assert.Contains("u003C", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_api_lists_runs_and_reads_one_back()
    {
        var client = _app.GetTestClient();
        await client.PostAsJsonAsync("/netcoreai/api/agents", Agent(), Ct);
        var run = await (await client.PostAsJsonAsync("/netcoreai/api/agents/helper/run", new { message = "hello" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);

        var runs = await client.GetFromJsonAsync<List<JsonElement>>("/netcoreai/api/agents/helper/runs", Ct);
        Assert.Single(runs!);

        var one = await client.GetFromJsonAsync<JsonElement>($"/netcoreai/api/runs/{run.GetProperty("runId").GetString()}", Ct);
        Assert.Equal("hello", one.GetProperty("input").GetString());
    }
}
