using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace NetCoreAI.Tools;

/// <summary>What an in-process call produced.</summary>
/// <param name="StatusCode">Status the endpoint set.</param>
/// <param name="Body">Everything the endpoint wrote.</param>
internal sealed record InProcessResponse(int StatusCode, string Body);

/// <summary>Runs one of this host's own endpoints without a socket.</summary>
internal interface IInProcessToolTransport
{
    Task<InProcessResponse> SendAsync(ToolDefinition tool, BoundArguments bound, ToolCallContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Invokes a routed endpoint directly, with a synthetic request carrying the caller's identity (ADR-0004).
/// </summary>
/// <remarks>
/// <para>
/// Worth being exact about what "through the real pipeline" means here, because the security of the whole
/// feature rests on it. Invoking an endpoint's <c>RequestDelegate</c> runs the endpoint and its endpoint
/// filters; it does <b>not</b> run the application's middleware, so the authorization middleware that would
/// normally enforce <c>RequireAuthorization</c> never sees this request. Relying on it would mean every
/// in-process tool call ran unauthorized — the precise failure the ADR rejected direct method calls for.
/// </para>
/// <para>
/// So authorization is evaluated here, explicitly, from the endpoint's own metadata and through the host's
/// real <see cref="IAuthorizationService"/> and policy provider: the same data and the same evaluator the
/// middleware uses. What is not reproduced is middleware unrelated to the endpoint's own authorization —
/// IP allow-lists, forwarded headers, rate limiters — and a synthetic request has no connection for those
/// to read. That is why in-process invocation is opt-in per endpoint rather than a global default.
/// </para>
/// </remarks>
internal sealed class InProcessToolTransport(
    EndpointDataSource endpoints,
    IServiceScopeFactory scopes,
    IServiceProvider services,
    ILogger<InProcessToolTransport> logger) : IInProcessToolTransport
{
    public async Task<InProcessResponse> SendAsync(ToolDefinition tool, BoundArguments bound, ToolCallContext context, CancellationToken cancellationToken)
    {
        if (!tool.InProcessAllowed)
        {
            // Checked again here, not only when the tool was saved: a definition edited straight into the
            // store must not be able to grant itself a mode nobody approved.
            throw new NetCoreAIException(
                $"'{tool.Name}' is not approved for in-process invocation. Mark the endpoint [AIToolEndpoint], or enable it in the designer.");
        }

        var endpoint = Match(tool)
            ?? throw new NetCoreAIException($"'{tool.Name}' points at {tool.Method} {tool.Route}, which this host no longer routes.");

        await using var scope = scopes.CreateAsyncScope();
        var http = BuildContext(tool, bound, context, scope.ServiceProvider, endpoint);

        if (await Refuse(endpoint, http, context.User).ConfigureAwait(false) is { } refusal)
        {
            logger.LogInformation("In-process call to {Tool} refused with {Status}: the caller does not satisfy the endpoint's authorization.", tool.Name, refusal.StatusCode);
            return refusal;
        }

        var body = new MemoryStream();
        http.Response.Body = body;

        await endpoint.RequestDelegate!(http).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);
        return new InProcessResponse(http.Response.StatusCode, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
    }

    private RouteEndpoint? Match(ToolDefinition tool) =>
        endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RequestDelegate is not null)
            .FirstOrDefault(e =>
                string.Equals("/" + e.RoutePattern.RawText?.TrimStart('/'), tool.Route, StringComparison.OrdinalIgnoreCase)
                && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(tool.Method, StringComparer.OrdinalIgnoreCase) ?? true));

    /// <summary>
    /// The endpoint's own authorization, evaluated against this caller.
    /// </summary>
    /// <remarks>
    /// Returns the refusal to send back, or null to go ahead. An unauthenticated caller gets 401 and an
    /// authenticated one 403, which is what the middleware would have done and what a model can tell apart:
    /// "you need to sign in" and "you may not do this" are different answers to give a user.
    /// </remarks>
    private async Task<InProcessResponse?> Refuse(RouteEndpoint endpoint, HttpContext http, ClaimsPrincipal? user)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return null;
        }

        var data = endpoint.Metadata.OfType<IAuthorizeData>().ToList();
        if (data.Count == 0)
        {
            return null;
        }

        // Resolved here rather than injected, because a host that never calls AddAuthorization() has none
        // of these — and NetCoreAI must not stop such a host from starting over a feature it is not using.
        // An endpoint that declares authorization when the host cannot evaluate it is refused rather than
        // allowed: the one safe reading of "I cannot tell whether you may do this" is no.
        var policies = services.GetService<IAuthorizationPolicyProvider>();
        var authorization = services.GetService<IAuthorizationService>();
        if (policies is null || authorization is null)
        {
            logger.LogWarning(
                "{Endpoint} declares authorization, but this host has no authorization services registered, so it cannot be evaluated. The call was refused.",
                endpoint.DisplayName);
            return new InProcessResponse(StatusCodes.Status403Forbidden, "This action's authorization could not be evaluated, so it was refused.");
        }

        var policy = await AuthorizationPolicy.CombineAsync(policies, data).ConfigureAwait(false);
        if (policy is null)
        {
            return null;
        }

        var principal = user ?? new ClaimsPrincipal(new ClaimsIdentity());
        if (principal.Identity?.IsAuthenticated != true)
        {
            return new InProcessResponse(StatusCodes.Status401Unauthorized, "This action needs a signed-in caller.");
        }

        var result = await authorization.AuthorizeAsync(principal, http.GetEndpoint(), policy).ConfigureAwait(false);
        return result.Succeeded
            ? null
            : new InProcessResponse(StatusCodes.Status403Forbidden, "The caller is not allowed to perform this action.");
    }

    private static DefaultHttpContext BuildContext(
        ToolDefinition tool,
        BoundArguments bound,
        ToolCallContext context,
        IServiceProvider services,
        RouteEndpoint endpoint)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = tool.Method;
        http.Request.Scheme = context.BaseAddress?.Scheme ?? "http";
        http.Request.Host = new HostString(context.BaseAddress?.Authority ?? "localhost");
        http.Request.Path = Path(tool, bound);

        if (bound.Query.Count > 0)
        {
            http.Request.QueryString = QueryString.Create(bound.Query.ToDictionary(q => q.Key, q => new StringValues(q.Value)));
        }

        foreach (var (name, value) in bound.Headers)
        {
            http.Request.Headers[name] = value;
        }

        // Route values are normally filled in by the routing middleware, which is not in this path.
        foreach (var (name, value) in bound.Route)
        {
            http.Request.RouteValues[name] = value;
        }

        if (bound.Body is { } body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.GetRawText());
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
            http.Request.ContentType = "application/json";
        }

        // The caller travels with the call. A tool never runs with more authority than the person who
        // caused it to run, so this is the whole of the identity the endpoint will see.
        http.User = context.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        http.Features.Set<IEndpointFeature>(new Endpoints(endpoint));
        return http;
    }

    private static string Path(ToolDefinition tool, BoundArguments bound)
    {
        var path = tool.Route ?? "/";
        foreach (var (name, value) in bound.Route)
        {
            path = System.Text.RegularExpressions.Regex.Replace(
                path,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"(:[^}]*)?\??\}",
                Uri.EscapeDataString(value),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }

        return "/" + path.TrimStart('/');
    }

    private sealed class Endpoints(Endpoint endpoint) : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; } = endpoint;
    }
}
