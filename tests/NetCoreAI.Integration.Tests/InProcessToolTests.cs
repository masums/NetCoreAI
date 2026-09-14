using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Tools;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// In-process invocation against the same endpoints as loopback, because the two modes must not differ in
/// what they let a caller do. Every behaviour here is asserted for both modes from one test body.
/// </summary>
public sealed class InProcessToolTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";
    private static readonly List<string> Calls = [];

    private IToolService Tools => _app.Services.GetRequiredService<IToolService>();

    private IToolInvoker Invoker => _app.Services.GetRequiredService<IToolInvoker>();

    /// <summary>
    /// Authenticates whoever the <c>Authorization: Test name:role</c> header names.
    /// </summary>
    /// <remarks>
    /// Loopback calls arrive as real requests and are signed in by this scheme from the forwarded header;
    /// in-process calls carry the principal directly. Both end up with the same claims, which is what lets
    /// one test body cover both modes.
    /// </remarks>
    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var header)
                || header.Scheme != "Test"
                || header.Parameter is not { Length: > 0 } user)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var parts = user.Split(':');
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, parts[0]), new Claim(ClaimTypes.Role, parts.Length > 1 ? parts[1] : "user")], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    public async ValueTask InitializeAsync()
    {
        Calls.Clear();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("Test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("managers", p => p.RequireRole("manager")));
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        builder.Services.AddHttpClient(ToolInvoker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler());

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.MapGet("/api/orders/{id}", (string id, string? note, HttpContext http) =>
        {
            Calls.Add($"GET {http.Request.Path}{http.Request.QueryString} as {http.User.Identity?.Name ?? "anonymous"}");
            return Results.Ok(new { id, note, total = 42 });
        }).WithAITool("get_order", "Look up one order.");

        _app.MapPost("/api/orders/{id}/refund", async (string id, HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            Calls.Add($"POST {http.Request.Path} body={await reader.ReadToEndAsync()} as {http.User.Identity?.Name}");
            return Results.Ok(new { refunded = id });
        }).RequireAuthorization("managers").WithAITool("refund_order", "Refund an order.", ToolSafety.SideEffecting);

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

    private static ClaimsPrincipal? User(string? name, string role = "user") =>
        name is null ? null : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], "Test"));

    private ToolCallContext Context(string? user, string role = "user") => new()
    {
        User = User(user, role),
        BaseAddress = _app.GetTestServer().BaseAddress,

        // The same caller, expressed the two ways the two modes need. Loopback goes out as a real request
        // and is signed in from this header; in-process carries the principal itself.
        AuthorizationHeader = user is null ? null : $"Test {user}:{role}",
    };

    /// <summary>Saves the tool in the given mode and calls it; used once per mode from each test.</summary>
    private async Task<ToolCallResult> CallAsync(
        string method,
        string route,
        ToolInvocationMode mode,
        IReadOnlyDictionary<string, object?> arguments,
        string? user = null,
        string role = "user")
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId(method, route), Ct);
        var saved = await Tools.SaveAsync(tool with { InvocationMode = mode }, allowInProcessBy: "test", cancellationToken: Ct);

        var result = await Invoker.InvokeAsync(saved, arguments, Context(user, role), Ct);
        await Tools.DeleteAsync(saved.Id, Ct);
        return result;
    }

    public static TheoryData<ToolInvocationMode> BothModes => [ToolInvocationMode.HttpLoopback, ToolInvocationMode.InProcess];

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_tool_call_reaches_the_endpoint_and_returns_its_body(ToolInvocationMode mode)
    {
        var result = await CallAsync("GET", "/api/orders/{id}", mode, new Dictionary<string, object?> { ["id"] = "A-1" });

        Assert.True(result.Success, result.Output);
        Assert.Contains("\"total\":42", result.Output, StringComparison.Ordinal);
        Assert.Contains("GET /api/orders/A-1", Assert.Single(Calls), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Query_values_arrive_as_query_values(ToolInvocationMode mode)
    {
        var result = await CallAsync("GET", "/api/orders/{id}", mode, new Dictionary<string, object?> { ["id"] = "A-1", ["note"] = "urgent" });

        Assert.Contains("\"note\":\"urgent\"", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task An_unauthenticated_caller_is_refused_by_the_endpoints_own_authorization(ToolInvocationMode mode)
    {
        var result = await CallAsync("POST", "/api/orders/{id}/refund", mode, new Dictionary<string, object?> { ["id"] = "A-1" });

        // The whole safety argument for in-process invocation is that this refusal still happens. If it
        // did not, every opted-in endpoint would be reachable by anyone who could reach a model.
        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.Empty(Calls);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task An_authenticated_caller_without_the_role_is_refused(ToolInvocationMode mode)
    {
        var result = await CallAsync("POST", "/api/orders/{id}/refund", mode, new Dictionary<string, object?> { ["id"] = "A-1" }, user: "bob", role: "clerk");

        Assert.False(result.Success);
        Assert.Empty(Calls);
    }

    [Fact]
    public async Task An_in_process_call_runs_as_the_caller_it_was_given()
    {
        var result = await CallAsync(
            "POST",
            "/api/orders/{id}/refund",
            ToolInvocationMode.InProcess,
            new Dictionary<string, object?> { ["id"] = "A-1" },
            user: "alice",
            role: "manager");

        Assert.True(result.Success, result.Output);
        Assert.Contains("as alice", Assert.Single(Calls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_in_process_call_is_refused_outright_when_the_tool_was_never_approved_for_it()
    {
        var tool = await Tools.CreateFromEndpointAsync(EndpointId("GET", "/api/orders/{id}"), Ct);

        // Bypassing the service's own check, as an edit straight into the store would. The transport must
        // refuse it anyway: approval is the thing that makes this mode safe, so it is checked where it is used.
        var forged = tool with { InvocationMode = ToolInvocationMode.InProcess, InProcessAllowed = false };
        var result = await Invoker.InvokeAsync(forged, new Dictionary<string, object?> { ["id"] = "1" }, Context(null), Ct);

        Assert.False(result.Success);
        Assert.Contains("not approved", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls);
    }

    [Fact]
    public async Task An_in_process_tool_pointed_at_a_route_that_no_longer_exists_says_so()
    {
        var tool = await Tools.SaveAsync(
            new ToolDefinition
            {
                Id = "gone",
                Name = "gone_tool",
                Kind = ToolKind.Endpoint,
                Method = "GET",
                Route = "/api/removed-last-release",
                InvocationMode = ToolInvocationMode.InProcess,
            },
            allowInProcessBy: "test",
            cancellationToken: Ct);

        var result = await Invoker.InvokeAsync(tool, null, Context(null), Ct);

        Assert.False(result.Success);
        Assert.Contains("no longer routes", result.Output, StringComparison.Ordinal);
    }
}
