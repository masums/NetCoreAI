using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Endpoint discovery against a host with real endpoints on it: what it finds, what it leaves out, and
/// what it says about each.
/// </summary>
public sealed class EndpointDiscoveryTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _client = default!;
    private string _dataDir = "";

    /// <summary>A host service, to prove an injected dependency is never offered to the model as a parameter.</summary>
    private sealed class OrderRepository;

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<OrderRepository>();
        builder.Services.AddAuthorization();
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

        // An ordinary host: one endpoint opted in, one not, one authorized, one taking a file.
        _app.MapGet("/api/orders/{id}", (string id, bool includeLines, OrderRepository repo, CancellationToken ct) => Results.Ok(new { id }))
            .WithAITool("get_order", "Look up one order by its id.");

        _app.MapGet("/api/customers", (string? search, int page = 1) => Results.Ok(Array.Empty<string>()));

        _app.MapPost("/api/orders/{id}/refund", (string id, [FromBody] RefundRequest body) => Results.Ok())
            .RequireAuthorization()
            .WithAITool("refund_order", "Refund an order.", ToolSafety.SideEffecting);

        _app.MapPost("/api/uploads", (IFormFile file) => Results.Ok()).DisableAntiforgery();

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

    private sealed record RefundRequest(decimal Amount, string Reason);

    private IReadOnlyList<DiscoveredEndpoint> Discover() =>
        _app.Services.GetRequiredService<IEndpointDiscovery>().Discover();

    private DiscoveredEndpoint One(string method, string route) =>
        Assert.Single(Discover(), e => e.Method == method && e.Route == route);

    [Fact]
    public void The_hosts_own_endpoints_are_found()
    {
        var found = Discover();

        Assert.Contains(found, e => e.Route == "/api/orders/{id}" && e.Method == "GET");
        Assert.Contains(found, e => e.Route == "/api/customers");
    }

    [Fact]
    public void NetCoreAIs_own_api_is_not_offered_as_a_tool()
    {
        // Handing a model the endpoint that deletes knowledge bases is not a tool anybody meant to build,
        // and it would be the easiest one in the list to click.
        Assert.DoesNotContain(Discover(), e => e.Route.StartsWith("/netcoreai", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_opted_in_endpoint_carries_the_name_and_description_its_author_gave_it()
    {
        var order = One("GET", "/api/orders/{id}");

        Assert.True(order.OptedIn);
        Assert.Equal("get_order", order.SuggestedName);
        Assert.Equal("Look up one order by its id.", order.Summary);
    }

    [Fact]
    public void An_endpoint_nobody_opted_in_is_listed_but_not_marked()
    {
        // Listed, because an administrator may still choose to expose it; not marked, because nobody has.
        var customers = One("GET", "/api/customers");

        Assert.False(customers.OptedIn);
        Assert.Equal("get_api_customers", customers.SuggestedName);
    }

    [Fact]
    public void Injected_services_and_framework_parameters_are_not_part_of_a_tools_surface()
    {
        var order = One("GET", "/api/orders/{id}");
        var names = order.Parameters.Select(p => p.Name).ToList();

        Assert.Equal(["id", "includeLines"], names);

        // A model asked to invent an OrderRepository or a CancellationToken would produce nonsense, and
        // showing it one suggests it is something the caller controls.
        Assert.DoesNotContain(order.Parameters, p => p.Name is "repo" or "ct");
    }

    [Fact]
    public void Parameters_carry_where_they_ride_and_whether_they_are_required()
    {
        var order = One("GET", "/api/orders/{id}");

        var id = Assert.Single(order.Parameters, p => p.Name == "id");
        Assert.Equal(ParameterLocation.Route, id.Location);
        Assert.True(id.Required);
        Assert.Equal("string", id.Type);

        var includeLines = Assert.Single(order.Parameters, p => p.Name == "includeLines");
        Assert.Equal(ParameterLocation.Query, includeLines.Location);
        Assert.Equal("boolean", includeLines.Type);
    }

    [Fact]
    public void A_parameter_with_a_default_is_optional()
    {
        var page = Assert.Single(One("GET", "/api/customers").Parameters, p => p.Name == "page");

        Assert.False(page.Required);
        Assert.Equal("1", page.Default);
        Assert.Equal("integer", page.Type);
    }

    [Fact]
    public void A_nullable_parameter_is_optional()
    {
        Assert.False(Assert.Single(One("GET", "/api/customers").Parameters, p => p.Name == "search").Required);
    }

    [Fact]
    public void A_post_body_is_a_body_parameter()
    {
        var body = Assert.Single(One("POST", "/api/orders/{id}/refund").Parameters, p => p.Name == "body");

        Assert.Equal(ParameterLocation.Body, body.Location);
        Assert.Equal("object", body.Type);
    }

    [Fact]
    public void The_authorization_an_endpoint_declares_is_reported()
    {
        var refund = One("POST", "/api/orders/{id}/refund");

        // Shown in the designer so nobody exposes an endpoint without seeing who can reach it. A tool call
        // runs as the caller, so this is what will be enforced.
        Assert.NotEmpty(refund.RequiredPolicies);
        Assert.False(refund.AllowsAnonymous);
        Assert.True(One("GET", "/api/customers").AllowsAnonymous is false);
    }

    [Fact]
    public void An_endpoint_taking_a_file_upload_says_why_it_cannot_be_a_tool()
    {
        var upload = One("POST", "/api/uploads");

        // Listed with a reason rather than hidden: "the endpoint I expected is missing" is a worse thing
        // to debug than "here it is, and here is why not".
        Assert.NotNull(upload.Unsuitable);
        Assert.Contains("file", upload.Unsuitable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_endpoints_id_survives_a_rename_of_its_handler()
    {
        // Ids are built from the method and route, so a tool saved yesterday still matches its endpoint
        // after the handler is renamed or moved to another file.
        Assert.Equal(
            DiscoveredEndpoint.IdFor("GET", "/api/orders/{id}"),
            One("GET", "/api/orders/{id}").Id);
    }

    [Theory]
    [InlineData("GET", "/api/orders/{id}", "get_api_orders_by_id")]
    [InlineData("POST", "/api/orders", "post_api_orders")]
    [InlineData("DELETE", "/api/orders/{id:guid}", "delete_api_orders_by_id")]
    public void A_tool_name_derived_from_a_route_reads_as_what_it_does(string method, string route, string expected) =>
        Assert.Equal(expected, EndpointDiscoveryService.NameFor(method, route));

    [Fact]
    public async Task The_discover_endpoint_reports_what_was_found()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/netcoreai/api/tools/discover", TestContext.Current.CancellationToken);

        Assert.Equal(2, body.GetProperty("optedIn").GetInt32());
        Assert.Contains(
            body.GetProperty("endpoints").EnumerateArray(),
            e => e.GetProperty("route").GetString() == "/api/orders/{id}");
    }
}
