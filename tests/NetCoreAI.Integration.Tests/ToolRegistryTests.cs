using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Tools as the model sees and calls them: the schema it is shown, the arguments it is allowed to set, and
/// what actually reaches the endpoint when it calls one.
/// </summary>
public sealed class ToolRegistryTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";

    /// <summary>What the last call to the test endpoint actually received.</summary>
    private static readonly List<string> Calls = [];

    private IToolService Tools => _app.Services.GetRequiredService<IToolService>();

    private IToolRegistry Registry => _app.Services.GetRequiredService<IToolRegistry>();

    public async ValueTask InitializeAsync()
    {
        Calls.Clear();
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

        // Loopback tool calls go out through the named tool client. Here that client is pointed at the
        // in-memory server, which is the same path a real host takes over a socket.
        builder.Services.AddHttpClient(ToolInvoker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler());

        _app = builder.Build();

        _app.MapGet("/api/orders/{id}", (string id, string? tenantId, HttpContext http) =>
        {
            Calls.Add($"GET {http.Request.Path}{http.Request.QueryString}");
            return Results.Ok(new { id, tenantId, total = 42, lines = new[] { "widget" } });
        }).WithAITool("get_order", "Look up one order.");

        _app.MapPost("/api/orders", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            Calls.Add($"POST body={await reader.ReadToEndAsync()}");
            return Results.Ok(new { created = true });
        }).WithAITool("create_order", "Create an order.", ToolSafety.SideEffecting);

        _app.MapGet("/api/broken", () => Results.Problem("the database is down", statusCode: 503));

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

    private string EndpointId(string method, string route) =>
        Assert.Single(_app.Services.GetRequiredService<IEndpointDiscovery>().Discover(), e => e.Method == method && e.Route == route).Id;

    private ToolCallContext Context(ClaimsPrincipal? user = null) => new()
    {
        User = user,
        BaseAddress = _app.GetTestServer().BaseAddress,
    };

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity([.. claims.Select(c => new Claim(c.Type, c.Value))], "test"));

    private async Task<AIFunction> FunctionAsync(ToolDefinition tool, ClaimsPrincipal? user = null) =>
        Assert.Single(await Registry.GetFunctionsAsync([tool.Id], Context(user), Ct));

    private async Task<ToolDefinition> GetOrderToolAsync() =>
        await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);

    // ---------- what the model is shown ----------

    [Fact]
    public async Task The_schema_describes_the_parameters_the_model_may_set()
    {
        var function = await FunctionAsync(await GetOrderToolAsync());
        var properties = function.JsonSchema.GetProperty("properties");

        Assert.Equal("get_order", function.Name);
        Assert.Equal("Look up one order.", function.Description);
        Assert.True(properties.TryGetProperty("id", out _));
        Assert.Contains("id", function.JsonSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_locked_parameter_is_not_in_the_schema_at_all()
    {
        var tool = await GetOrderToolAsync();
        var locked = await Tools.SaveAsync(tool with
        {
            Parameters =
            [
                new ToolParameter { Name = "id", Location = ParameterLocation.Route, Required = true },
                new ToolParameter { Name = "tenantId", Location = ParameterLocation.Query, Binding = ParameterBinding.Claim, BindingSource = "tenant" },
            ],
        }, cancellationToken: Ct);

        var schema = (await FunctionAsync(locked)).JsonSchema.GetProperty("properties");

        // The model is never told this parameter exists, so there is nothing for it to try to set. This is
        // the first of two defences; the binder ignoring a supplied value is the second.
        Assert.False(schema.TryGetProperty("tenantId", out _));
        Assert.True(schema.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task A_model_that_supplies_a_locked_parameter_anyway_is_ignored()
    {
        var tool = await GetOrderToolAsync();
        var locked = await Tools.SaveAsync(tool with
        {
            Parameters =
            [
                new ToolParameter { Name = "id", Location = ParameterLocation.Route, Required = true },
                new ToolParameter { Name = "tenantId", Location = ParameterLocation.Query, Binding = ParameterBinding.Claim, BindingSource = "tenant" },
            ],
        }, cancellationToken: Ct);

        var function = await FunctionAsync(locked, User(("tenant", "acme")));
        await function.InvokeAsync(new AIFunctionArguments { ["id"] = "1", ["tenantId"] = "someone-elses-tenant" }, Ct);

        // The value that reached the endpoint is the caller's, not the one the model asked for.
        Assert.Contains("tenantId=acme", Assert.Single(Calls), StringComparison.Ordinal);
        Assert.DoesNotContain("someone-elses-tenant", Assert.Single(Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_claim_binding_that_finds_nothing_refuses_the_call_rather_than_sending_null()
    {
        var tool = await GetOrderToolAsync();
        var locked = await Tools.SaveAsync(tool with
        {
            Parameters =
            [
                new ToolParameter { Name = "id", Location = ParameterLocation.Route, Required = true },
                new ToolParameter { Name = "tenantId", Location = ParameterLocation.Query, Required = true, Binding = ParameterBinding.Claim, BindingSource = "tenant" },
            ],
        }, cancellationToken: Ct);

        // An anonymous caller has no tenant. Calling anyway would query every tenant's orders.
        var result = await (await FunctionAsync(locked)).InvokeAsync(new AIFunctionArguments { ["id"] = "1" }, Ct);

        Assert.Empty(Calls);
        Assert.Contains("tenant", result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_claim_binds_whether_it_is_written_long_or_short()
    {
        var tool = await GetOrderToolAsync();
        var locked = await Tools.SaveAsync(tool with
        {
            Parameters =
            [
                new ToolParameter { Name = "id", Location = ParameterLocation.Route, Required = true },
                new ToolParameter { Name = "tenantId", Location = ParameterLocation.Query, Binding = ParameterBinding.Claim, BindingSource = "nameidentifier" },
            ],
        }, cancellationToken: Ct);

        // The identity stack wrote the long URI form; the person configuring the tool wrote the short one.
        var function = await FunctionAsync(locked, User((ClaimTypes.NameIdentifier, "u-7")));
        await function.InvokeAsync(new AIFunctionArguments { ["id"] = "1" }, Ct);

        Assert.Contains("tenantId=u-7", Assert.Single(Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_static_binding_always_sends_the_same_value()
    {
        var tool = await GetOrderToolAsync();
        var locked = await Tools.SaveAsync(tool with
        {
            Parameters =
            [
                new ToolParameter { Name = "id", Location = ParameterLocation.Route, Required = true },
                new ToolParameter { Name = "tenantId", Location = ParameterLocation.Query, Binding = ParameterBinding.Static, BindingSource = "fixed-tenant" },
            ],
        }, cancellationToken: Ct);

        await (await FunctionAsync(locked)).InvokeAsync(new AIFunctionArguments { ["id"] = "1" }, Ct);

        Assert.Contains("tenantId=fixed-tenant", Assert.Single(Calls), StringComparison.Ordinal);
    }

    // ---------- what happens when it is called ----------

    [Fact]
    public async Task Calling_a_tool_reaches_the_endpoint_and_returns_its_body()
    {
        var function = await FunctionAsync(await GetOrderToolAsync());

        var result = await function.InvokeAsync(new AIFunctionArguments { ["id"] = "A-1" }, Ct);

        Assert.Equal("GET /api/orders/A-1", Assert.Single(Calls));
        Assert.Contains("\"total\":42", result?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_value_is_escaped_rather_than_pasted_into_the_url()
    {
        var function = await FunctionAsync(await GetOrderToolAsync());

        await function.InvokeAsync(new AIFunctionArguments { ["id"] = "a b/../secret" }, Ct);

        // A model-supplied value must not be able to climb out of the route segment it belongs to.
        Assert.DoesNotContain("/secret", Assert.Single(Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_parameter_is_sent_as_the_request_body()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("POST", "/api/orders"), Ct);
        var withBody = await Tools.SaveAsync(tool with
        {
            Parameters = [new ToolParameter { Name = "body", Type = "object", Location = ParameterLocation.Body, Required = true }],
        }, cancellationToken: Ct);

        await (await FunctionAsync(withBody)).InvokeAsync(new AIFunctionArguments { ["body"] = """{"sku":"widget"}""" }, Ct);

        Assert.Contains("""body={"sku":"widget"}""", Assert.Single(Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_endpoint_is_reported_to_the_model_rather_than_thrown()
    {
        var tool = await Tools.SaveAsync(
            new ToolDefinition { Id = "t1", Name = "broken", Kind = ToolKind.Endpoint, Method = "GET", Route = "/api/broken" },
            cancellationToken: Ct);

        var result = (await (await FunctionAsync(tool)).InvokeAsync(new AIFunctionArguments(), Ct))?.ToString() ?? "";

        // A model told "that returned 503" can say so; an exception ends the turn and the person asking
        // sees nothing useful.
        Assert.Contains("503", result, StringComparison.Ordinal);
        Assert.Contains("database is down", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_can_be_cut_down_to_the_part_worth_returning()
    {
        var tool = await GetOrderToolAsync();
        var mapped = await Tools.SaveAsync(
            tool with { Response = new ToolResponseMapping { SelectPath = "total" } },
            cancellationToken: Ct);

        var result = (await (await FunctionAsync(mapped)).InvokeAsync(new AIFunctionArguments { ["id"] = "1" }, Ct))?.ToString() ?? "";

        Assert.Equal("42", result);
    }

    [Fact]
    public async Task A_response_too_large_for_the_model_is_cut_and_says_so()
    {
        var tool = await GetOrderToolAsync();
        var capped = await Tools.SaveAsync(
            tool with { Response = new ToolResponseMapping { MaxBytes = 20 } },
            cancellationToken: Ct);

        var result = (await (await FunctionAsync(capped)).InvokeAsync(new AIFunctionArguments { ["id"] = "1" }, Ct))?.ToString() ?? "";

        // A model reading a cut-off object as though it were whole answers confidently from half a list.
        Assert.Contains("Response cut", result, StringComparison.Ordinal);
    }

    // ---------- which tools a caller is offered ----------

    [Fact]
    public async Task A_loopback_call_with_no_address_anywhere_says_what_to_set()
    {
        var tool = await GetOrderToolAsync();

        // A host behind a proxy, or a run with no request behind it, has no address to read. The message
        // has to name the setting rather than leaving a bare connection failure.
        var result = await _app.Services.GetRequiredService<IToolInvoker>()
            .InvokeAsync(tool, new Dictionary<string, object?> { ["id"] = "1" }, new ToolCallContext(), Ct);

        Assert.False(result.Success);
        Assert.Contains("NetCoreAI:Tools:BaseAddress", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_tool_is_not_offered()
    {
        var tool = await Tools.SaveAsync((await GetOrderToolAsync()) with { Enabled = false }, cancellationToken: Ct);

        Assert.Empty(await Registry.GetFunctionsAsync([tool.Id], Context(), Ct));
    }

    [Fact]
    public async Task An_admin_only_tool_is_withheld_from_everyone_else()
    {
        var tool = await Tools.SaveAsync(
            (await GetOrderToolAsync()) with { Safety = ToolSafety.SideEffecting, Confirmation = ConfirmationPolicy.AdminOnly },
            cancellationToken: Ct);

        // Withheld rather than offered and refused: a model told about a tool will try it, and a refusal
        // mid-turn spends a call and invites it to look for a way round.
        Assert.Empty(await Registry.GetFunctionsAsync([tool.Id], Context(User(("name", "bob"))), Ct));
        Assert.Single(await Registry.GetFunctionsAsync([tool.Id], Context(User((ClaimTypes.Role, "Administrator"))), Ct));
    }

    [Fact]
    public async Task Tools_can_be_asked_for_by_name_as_well_as_by_id()
    {
        var tool = await GetOrderToolAsync();

        Assert.Single(await Registry.GetFunctionsAsync(["get_order"], Context(), Ct));
        Assert.Single(await Registry.GetFunctionsAsync([tool.Id], Context(), Ct));
    }

    [Fact]
    public void A_host_that_never_configured_authorization_still_starts_and_can_call_tools()
    {
        // This host calls neither AddAuthorization() nor UseAuthorization() — the ordinary minimal API that
        // NetCoreAI promises to work in with two lines. Requiring them for tool support once broke every
        // such host at the first resolve of IToolInvoker.
        Assert.Null(_app.Services.GetService<Microsoft.AspNetCore.Authorization.IAuthorizationService>());
        Assert.NotNull(_app.Services.GetRequiredService<IToolInvoker>());
    }

    [Fact]
    public async Task An_unknown_tool_is_left_out_rather_than_failing_the_whole_turn()
    {
        await GetOrderToolAsync();

        // An agent naming a tool that has since been deleted should still answer with the rest.
        Assert.Single(await Registry.GetFunctionsAsync(["get_order", "deleted_long_ago"], Context(), Ct));
    }
}
