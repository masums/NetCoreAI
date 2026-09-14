using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Providers;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The testing panel: running a tool on purpose, with arguments given or with arguments the model chose.
/// </summary>
public sealed class ToolTesterTests : IAsyncLifetime
{
    private FakeOpenAIServer _openAI = default!;
    private WebApplication _app = default!;
    private string _dataDir = "";
    private static readonly List<string> Calls = [];

    private IToolService Tools => _app.Services.GetRequiredService<IToolService>();

    private IToolTester Tester => _app.Services.GetRequiredService<IToolTester>();

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

    private ToolCallContext Context() => new() { BaseAddress = _app.GetTestServer().BaseAddress };

    private async Task<ToolDefinition> ToolAsync()
    {
        var id = Assert.Single(
            _app.Services.GetRequiredService<IEndpointDiscovery>().Discover(),
            e => e.Method == "GET" && e.Route == "/api/orders/{id}").Id;

        return await Tools.CreateFromEndpointAsync(id, Ct);
    }

    [Fact]
    public async Task Given_arguments_the_tool_is_called_with_them()
    {
        var tool = await ToolAsync();

        var result = await Tester.TestAsync(tool.Id, new Dictionary<string, object?> { ["id"] = "A-1" }, prompt: null, Context(), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Contains("\"total\":42", result.Output, StringComparison.Ordinal);
        Assert.Equal("GET /api/orders/A-1", Assert.Single(Calls));
    }

    [Fact]
    public async Task Given_a_prompt_the_model_picks_the_arguments_and_they_are_shown_back()
    {
        var tool = await ToolAsync();

        var result = await Tester.TestAsync(tool.Id, arguments: null, prompt: """What is order A-7? args:{"id":"A-7"}""", Context(), Ct);

        Assert.True(result.Success, result.Error);

        // Which arguments it chose is the interesting part: a tool that is called with the wrong ones has a
        // description problem, and that is invisible unless they are shown.
        Assert.Equal("A-7", result.Arguments["id"]);
        Assert.Equal("GET /api/orders/A-7", Assert.Single(Calls));
    }

    [Fact]
    public async Task When_the_model_does_not_call_the_tool_that_is_the_answer_rather_than_an_error()
    {
        var tool = await ToolAsync();

        var result = await Tester.TestAsync(tool.Id, arguments: null, prompt: "What is the weather like?", Context(), Ct);

        // Most tool problems are not "the call failed" but "the model did not think this tool applied",
        // and the description is what it reads to decide. Saying so beats a stack trace.
        Assert.False(result.Success);
        Assert.Contains("did not call the tool", result.Error!, StringComparison.Ordinal);
        Assert.Contains("description", result.Error!, StringComparison.Ordinal);
        Assert.Empty(Calls);
    }

    [Fact]
    public async Task A_prompt_test_does_not_fire_the_tool_twice()
    {
        var tool = await ToolAsync();

        await Tester.TestAsync(tool.Id, arguments: null, prompt: """Order A-7 args:{"id":"A-7"}""", Context(), Ct);

        // The function is offered to the model but the invocation loop is not run, so the call happens once,
        // here, under the tester's control — rather than a side-effecting tool firing inside the model's turn.
        Assert.Single(Calls);
    }

    [Fact]
    public async Task Testing_a_disabled_tool_says_it_is_off_rather_than_quietly_doing_nothing()
    {
        var tool = await Tools.SaveAsync((await ToolAsync()) with { Enabled = false }, cancellationToken: Ct);

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tester.TestAsync(tool.Id, new Dictionary<string, object?> { ["id"] = "1" }, null, Context(), Ct));

        Assert.Contains("turned off", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_a_tool_that_does_not_exist_says_so()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tester.TestAsync("nope", null, null, Context(), Ct));

        Assert.Contains("No tool", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_api_runs_a_test_and_reports_what_happened()
    {
        var tool = await ToolAsync();
        var client = _app.GetTestClient();

        var response = await client.PostAsJsonAsync($"/netcoreai/api/tools/{tool.Id}/test", new { arguments = new { id = "A-9" } }, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(response.IsSuccessStatusCode, body.ToString());
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Contains("42", body.GetProperty("output").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tools_page_lists_tools_and_the_endpoints_they_could_come_from()
    {
        await ToolAsync();
        var html = await _app.GetTestClient().GetStringAsync("/netcoreai/tools", Ct);

        Assert.Contains("get_order", html, StringComparison.Ordinal);
        Assert.Contains("/api/orders/{id}", html, StringComparison.Ordinal);

        // The endpoint already has a tool, so the row offers editing it rather than making a second one.
        Assert.Contains("Edit tool…", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_tools_from_one_endpoint_do_not_break_the_page()
    {
        // Legitimate: the same endpoint with different bindings, response mapping or safety. Keying the
        // endpoint-to-tool lookup assumed otherwise and threw, which turned the whole page into a 500.
        await ToolAsync();
        await ToolAsync();

        var client = _app.GetTestClient();
        var html = await client.GetStringAsync("/netcoreai/tools", Ct);
        Assert.Contains("get_order_2", html, StringComparison.Ordinal);

        var discover = await client.GetAsync("/netcoreai/api/tools/discover", Ct);
        Assert.True(discover.IsSuccessStatusCode, await discover.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_tools_page_is_in_the_navigation()
    {
        var html = await _app.GetTestClient().GetStringAsync("/netcoreai", Ct);

        Assert.Contains("/netcoreai/tools", html, StringComparison.Ordinal);
    }
}
