using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Saving tools: what a tool built from an endpoint inherits, what the name rules refuse, and the ADR-0004
/// rule that in-process invocation needs somebody's explicit say-so.
/// </summary>
public sealed class ToolServiceTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _client = default!;
    private string _dataDir = "";

    private IToolService Tools => _app.Services.GetRequiredService<IToolService>();

    private IEndpointDiscovery Discovery => _app.Services.GetRequiredService<IEndpointDiscovery>();

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
            // Pooling off: a pooled SQLite connection keeps the database file open after the test
            // that made it is done, and the process-global ClearAllPools() that used to compensate
            // disposed connections belonging to other test classes running in parallel.
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        _app = builder.Build();

        // One endpoint whose author opted it in, one nobody has touched.
        _app.MapGet("/api/orders/{id}", (string id) => Results.Ok(new { id }))
            .WithAITool("get_order", "Look up one order by its id.");
        _app.MapPost("/api/orders", (string customer) => Results.Ok());
        _app.MapDelete("/api/orders/{id}", (string id) => Results.NoContent());

        _app.MapNetCoreAI();
        await _app.StartAsync();
        _client = _app.GetTestClient();
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
        Assert.Single(Discovery.Discover(), e => e.Method == method && e.Route == route).Id;

    [Fact]
    public async Task A_tool_built_from_an_endpoint_takes_its_name_parameters_and_description()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);

        Assert.Equal("get_order", tool.Name);
        Assert.Equal("Look up one order by its id.", tool.Description);
        Assert.Equal(ToolKind.Endpoint, tool.Kind);
        Assert.Equal("/api/orders/{id}", tool.Route);
        Assert.Contains(tool.Parameters, p => p.Name == "id" && p.Location == ParameterLocation.Route);
    }

    [Fact]
    public async Task A_get_is_read_only_and_anything_else_is_assumed_to_change_something()
    {
        // Opting an endpoint in says "safe to expose", not "safe to modify anything". A POST is treated as
        // side-effecting until a person says otherwise, because the reverse mistake is the expensive one.
        Assert.Equal(ToolSafety.ReadOnly, (await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct)).Safety);
        Assert.Equal(ToolSafety.SideEffecting, (await Tools.CreateFromEndpointAsync(EndpointId("POST", "/api/orders"), Ct)).Safety);
        Assert.Equal(ToolSafety.SideEffecting, (await Tools.CreateFromEndpointAsync(EndpointId("DELETE", "/api/orders/{id}"), Ct)).Safety);
    }

    [Fact]
    public async Task A_new_tool_runs_over_http_even_when_its_endpoint_was_opted_in()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);

        // In-process is faster, but choosing it is an act in the designer rather than something that
        // happens because an attribute was present.
        Assert.Equal(ToolInvocationMode.HttpLoopback, tool.InvocationMode);
        Assert.True(tool.InProcessAllowed);
    }

    [Fact]
    public async Task In_process_is_refused_for_an_endpoint_nobody_opted_in()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("POST", "/api/orders"), Ct);

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.SaveAsync(tool with { InvocationMode = ToolInvocationMode.InProcess }, cancellationToken: Ct));

        // The message has to say what to do about it, because "refused" on its own leaves the reader
        // guessing whether the feature is broken or the configuration is.
        Assert.Contains("AIToolEndpoint", error.Message, StringComparison.Ordinal);
        Assert.Contains("administrator", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_administrator_can_enable_in_process_and_is_recorded_as_having_done_it()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("POST", "/api/orders"), Ct);

        var saved = await Tools.SaveAsync(tool with { InvocationMode = ToolInvocationMode.InProcess }, allowInProcessBy: "alice@example.com", cancellationToken: Ct);

        Assert.True(saved.InProcessAllowed);
        Assert.Equal("alice@example.com", saved.InProcessAllowedBy);
    }

    [Fact]
    public async Task An_opted_in_endpoint_needs_no_administrator_to_run_in_process()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);

        var saved = await Tools.SaveAsync(tool with { InvocationMode = ToolInvocationMode.InProcess }, cancellationToken: Ct);

        Assert.Equal("[AIToolEndpoint]", saved.InProcessAllowedBy);
    }

    [Fact]
    public async Task An_imported_tool_can_never_run_in_process()
    {
        var imported = Assert.Single(await Tools.ImportOpenApiAsync(Spec, cancellationToken: Ct));

        // It describes a service somewhere else; there is no endpoint of ours to run.
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.SaveAsync(imported with { InvocationMode = ToolInvocationMode.InProcess }, allowInProcessBy: "alice@example.com", cancellationToken: Ct));

        Assert.Contains("own endpoints", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_tools_cannot_share_a_name()
    {
        var first = await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);
        var second = await Tools.CreateFromEndpointAsync(EndpointId("POST", "/api/orders"), Ct);

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.SaveAsync(second with { Name = first.Name }, cancellationToken: Ct));

        Assert.Contains("already called", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("get order")]
    [InlineData("2get")]
    [InlineData("get-order")]
    [InlineData("")]
    public async Task A_name_a_provider_would_reject_is_refused_here_first(string name)
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);

        await Assert.ThrowsAsync<NetCoreAIException>(() => Tools.SaveAsync(tool with { Name = name }, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_locked_parameter_with_nothing_to_bind_from_is_refused()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);
        var locked = tool with
        {
            Parameters = [new ToolParameter { Name = "tenantId", Binding = ParameterBinding.Claim }],
        };

        // Sending null for a tenant id is the difference between "this tenant" and "all of them".
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() => Tools.SaveAsync(locked, cancellationToken: Ct));

        Assert.Contains("names no source", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_tool_from_an_endpoint_that_is_no_longer_routed_says_so()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.CreateFromEndpointAsync(DiscoveredEndpoint.IdFor("GET", "/api/gone"), Ct));

        Assert.Contains("route table", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- OpenAPI import ----------

    private const string Spec = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Shipping", "version": "1.0" },
          "servers": [{ "url": "https://shipping.example.com/v1" }],
          "paths": {
            "/shipments/{trackingNumber}": {
              "get": {
                "operationId": "getShipment",
                "summary": "Track a shipment.",
                "parameters": [
                  { "name": "trackingNumber", "in": "path", "required": true, "schema": { "type": "string" } },
                  { "name": "carrier", "in": "query", "schema": { "$ref": "#/components/schemas/Carrier" } },
                  { "name": "session", "in": "cookie", "schema": { "type": "string" } }
                ]
              }
            }
          },
          "components": { "schemas": { "Carrier": { "type": "string", "enum": ["ups", "dhl"], "default": "ups" } } }
        }
        """;

    [Fact]
    public async Task An_imported_operation_becomes_a_tool_pointed_at_the_documents_server()
    {
        var tool = Assert.Single(await Tools.ImportOpenApiAsync(Spec, cancellationToken: Ct));

        Assert.Equal("getShipment", tool.Name);
        Assert.Equal("Track a shipment.", tool.Description);
        Assert.Equal(ToolKind.OpenApi, tool.Kind);
        Assert.Equal("https://shipping.example.com/v1", tool.BaseUrl);
        Assert.Equal(ToolInvocationMode.HttpExternal, tool.InvocationMode);
    }

    [Fact]
    public async Task An_imported_tool_does_not_carry_the_callers_credentials_to_a_third_party()
    {
        // The caller's credentials are for this host. Sending them somewhere else because a spec was
        // imported would leak them to whoever wrote the spec.
        Assert.False(Assert.Single(await Tools.ImportOpenApiAsync(Spec, cancellationToken: Ct)).ForwardCallerCredentials);
    }

    [Fact]
    public async Task A_schema_reference_inside_the_document_is_followed()
    {
        var carrier = Assert.Single(
            Assert.Single(await Tools.ImportOpenApiAsync(Spec, cancellationToken: Ct)).Parameters,
            p => p.Name == "carrier");

        Assert.Equal(["ups", "dhl"], carrier.Enum!);
        Assert.Equal("ups", carrier.Default);
    }

    [Fact]
    public async Task A_cookie_parameter_is_not_something_a_model_supplies()
    {
        var tool = Assert.Single(await Tools.ImportOpenApiAsync(Spec, cancellationToken: Ct));

        Assert.DoesNotContain(tool.Parameters, p => p.Name == "session");
    }

    [Fact]
    public async Task Re_importing_an_updated_spec_updates_rather_than_duplicating()
    {
        var first = Assert.Single(await Tools.ImportOpenApiAsync(Spec, cancellationToken: Ct));
        var second = Assert.Single(await Tools.ImportOpenApiAsync(Spec.Replace("Track a shipment.", "Track a parcel.", StringComparison.Ordinal), cancellationToken: Ct));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Track a parcel.", second.Description);
        Assert.Single(await Tools.ListAsync(Ct));
    }

    [Fact]
    public async Task A_base_url_given_at_import_overrides_the_documents_own()
    {
        // A spec's servers entry is often a placeholder, or points at the vendor's production host when
        // the person importing it means to call their own staging one.
        var tool = Assert.Single(await Tools.ImportOpenApiAsync(Spec, "https://staging.internal/", Ct));

        Assert.Equal("https://staging.internal", tool.BaseUrl);
    }

    [Fact]
    public async Task A_yaml_document_is_refused_with_something_to_do_about_it()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.ImportOpenApiAsync("openapi: 3.0.1\npaths: {}\n", cancellationToken: Ct));

        Assert.Contains("YAML", error.Message, StringComparison.Ordinal);
        Assert.Contains("JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_with_no_server_and_no_base_url_says_where_the_problem_is()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Tools.ImportOpenApiAsync("""{ "openapi": "3.0.1", "paths": { "/x": { "get": {} } } }""", cancellationToken: Ct));

        Assert.Contains("base URL", error.Message, StringComparison.Ordinal);
    }

    // ---------- API ----------

    [Fact]
    public async Task The_api_creates_lists_and_deletes_tools()
    {
        var created = await _client.PostAsync($"/netcoreai/api/tools/from-endpoint/{EndpointId("GET", "/api/orders/{id}")}", null, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        var tool = await created.Content.ReadFromJsonAsync<ToolDefinition>(Ct);

        Assert.Single((await _client.GetFromJsonAsync<List<ToolDefinition>>("/netcoreai/api/tools", Ct))!);

        var deleted = await _client.DeleteAsync($"/netcoreai/api/tools/{tool!.Id}", Ct);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<List<ToolDefinition>>("/netcoreai/api/tools", Ct))!);
    }

    [Fact]
    public async Task Discovery_says_which_endpoints_already_have_a_tool()
    {
        await _client.PostAsync($"/netcoreai/api/tools/from-endpoint/{EndpointId("GET", "/api/orders/{id}")}", null, Ct);

        var body = await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/tools/discover", Ct);
        var order = body.GetProperty("endpoints").EnumerateArray()
            .Single(e => e.GetProperty("route").GetString() == "/api/orders/{id}" && e.GetProperty("method").GetString() == "GET");

        // So the designer offers "edit" rather than a second "create" that would fail on the duplicate name.
        Assert.False(order.GetProperty("existingToolId").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task The_api_imports_an_openapi_document()
    {
        var response = await _client.PostAsJsonAsync("/netcoreai/api/tools/import-openapi", new { document = Spec }, Ct);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task A_bad_import_comes_back_as_a_bad_request_rather_than_a_500()
    {
        var response = await _client.PostAsJsonAsync("/netcoreai/api/tools/import-openapi", new { document = "not a spec" }, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
