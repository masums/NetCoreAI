using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Tools;

/// <summary>The host's own endpoints, as candidates for tools.</summary>
public interface IEndpointDiscovery
{
    /// <summary>
    /// Every endpoint the host routes, with what is known about each: parameters, the authorization it
    /// declares, and whether the developer opted it in for in-process invocation.
    /// </summary>
    IReadOnlyList<DiscoveredEndpoint> Discover();
}

/// <summary>
/// Reads the route table to work out what could become a tool.
/// </summary>
/// <remarks>
/// Discovery only looks. Nothing here exposes anything to a model: a tool exists when somebody saves one,
/// and in-process invocation needs an opt-in on top of that (ADR-0004).
/// </remarks>
internal sealed class EndpointDiscoveryService(
    EndpointDataSource endpoints,
    IServiceProviderIsService isService,
    IOptionsMonitor<NetCoreAIOptions> options) : IEndpointDiscovery
{
    /// <summary>
    /// Parameters the framework fills in. They are not part of a tool's surface, and a model asked to
    /// supply a <c>CancellationToken</c> would produce nonsense.
    /// </summary>
    private static readonly HashSet<Type> FrameworkTypes =
    [
        typeof(CancellationToken), typeof(HttpContext), typeof(HttpRequest), typeof(HttpResponse),
        typeof(ClaimsPrincipal), typeof(IFormFile), typeof(IFormFileCollection), typeof(IFormCollection),
        typeof(Stream), typeof(System.IO.Pipelines.PipeReader), typeof(IServiceProvider),
    ];

    public IReadOnlyList<DiscoveredEndpoint> Discover()
    {
        var dashboard = "/" + options.CurrentValue.Dashboard.Path.Trim('/');
        var found = new List<DiscoveredEndpoint>();

        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var route = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            if (route.StartsWith(dashboard, StringComparison.OrdinalIgnoreCase))
            {
                // NetCoreAI's own API. Handing a model the endpoint that deletes knowledge bases, or the
                // one that issues API keys, is not a tool anybody meant to build.
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods.Where(m => m is not "OPTIONS" and not "HEAD"))
            {
                found.Add(Describe(endpoint, method, route));
            }
        }

        return [.. found.OrderBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)];
    }

    private DiscoveredEndpoint Describe(RouteEndpoint endpoint, string method, string route)
    {
        var optIn = endpoint.Metadata.GetMetadata<IAIToolEndpointMetadata>();
        var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var policies = endpoint.Metadata.OfType<IAuthorizeData>()
            .Select(a => a.Policy ?? (a.Roles is { Length: > 0 } roles ? $"roles:{roles}" : "authorized"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
        var parameters = Parameters(endpoint.RoutePattern, handler, method, out var unsuitable);

        return new DiscoveredEndpoint
        {
            Id = DiscoveredEndpoint.IdFor(method, route),
            Method = method,
            Route = route,
            DisplayName = endpoint.DisplayName,
            Summary = optIn?.Description
                ?? endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary
                ?? endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description,
            Parameters = parameters,
            AllowsAnonymous = allowsAnonymous,
            RequiredPolicies = policies,
            OptedIn = optIn is not null,
            SuggestedName = optIn?.Name ?? NameFor(method, route),
            Unsuitable = unsuitable,
        };
    }

    /// <summary>
    /// A tool name from the route: <c>GET /api/orders/{id}</c> becomes <c>get_api_orders_by_id</c>.
    /// </summary>
    /// <remarks>
    /// The model calls the tool by this name and reads it as part of deciding what the tool does, so route
    /// segments are kept rather than reduced to something shorter and emptier.
    /// </remarks>
    internal static string NameFor(string method, string route)
    {
        var parts = new List<string> { method.ToLowerInvariant() };
        foreach (var segment in route.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('{'))
            {
                var name = segment.Trim('{', '}', '?', '*').Split(':')[0];
                parts.Add("by");
                parts.Add(name);
            }
            else
            {
                parts.Add(segment);
            }
        }

        var joined = string.Concat(string.Join('_', parts).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));

        // A name that is mostly underscores tells the model nothing, and some providers cap the length.
        return joined.Length > 60 ? joined[..60].TrimEnd('_') : joined;
    }

    private List<ToolParameter> Parameters(RoutePattern pattern, MethodInfo? handler, string method, out string? unsuitable)
    {
        unsuitable = null;
        var parameters = new List<ToolParameter>();
        var routeNames = new HashSet<string>(pattern.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var routeParameter in pattern.Parameters)
        {
            parameters.Add(new ToolParameter
            {
                Name = routeParameter.Name,
                Type = "string",
                Required = !routeParameter.IsOptional,
                Location = ParameterLocation.Route,
            });
        }

        if (handler is null)
        {
            // A controller action or a delegate the framework did not record. The route parameters are
            // still usable, so this is a partial description rather than a dead end.
            return parameters;
        }

        var nullability = new NullabilityInfoContext();
        foreach (var parameter in handler.GetParameters())
        {
            if (parameter.Name is not { Length: > 0 } name)
            {
                continue;
            }

            // Checked before the framework-supplied skip, which would otherwise swallow IFormFile and leave
            // an upload endpoint looking like a tool that takes no arguments at all.
            if (IsMultipart(parameter.ParameterType))
            {
                unsuitable = "This endpoint takes a file upload, which a model cannot supply.";
                continue;
            }

            if (IsFrameworkSupplied(parameter))
            {
                continue;
            }

            if (routeNames.Contains(name))
            {
                // Already added from the pattern; the declared type is better than the "string" assumed there.
                var index = parameters.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                parameters[index] = parameters[index] with { Type = SchemaType(parameter.ParameterType) };
                continue;
            }

            parameters.Add(new ToolParameter
            {
                Name = name,
                Type = SchemaType(parameter.ParameterType),
                Required = IsRequired(parameter, nullability),
                Location = LocationOf(parameter, method),
                Enum = parameter.ParameterType is { IsEnum: true } enumType ? [.. Enum.GetNames(enumType)] : null,
                Default = parameter.HasDefaultValue ? parameter.DefaultValue?.ToString() : null,
            });
        }

        return parameters;
    }

    /// <summary>
    /// Whether the framework fills this parameter rather than the caller.
    /// </summary>
    /// <remarks>
    /// <c>IServiceProviderIsService</c> catches the host's own injected services, which is the case a
    /// hard-coded list of framework types would miss — and a repository shown to the model as a parameter
    /// it should invent would be both useless and alarming.
    /// </remarks>
    private bool IsFrameworkSupplied(ParameterInfo parameter)
    {
        if (parameter.GetCustomAttribute<FromServicesAttribute>() is not null
            || parameter.GetCustomAttributes().Any(a => a.GetType().Name == "FromKeyedServicesAttribute"))
        {
            return true;
        }

        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        return FrameworkTypes.Contains(type)
            || typeof(Stream).IsAssignableFrom(type)
            || (!IsSimple(type) && isService.IsService(type));
    }

    /// <summary>
    /// Whether the caller must supply a value.
    /// </summary>
    /// <remarks>
    /// <c>string?</c> is optional even though it has no <see cref="Nullable{T}"/> to read: reference-type
    /// nullability lives in an attribute, not in the type. Getting this wrong makes a model believe it has
    /// to invent a search term for a search that was meant to be optional.
    /// </remarks>
    private static bool IsRequired(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        if (parameter.HasDefaultValue || Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
        {
            return false;
        }

        return parameter.ParameterType.IsValueType
            || nullability.Create(parameter).WriteState != NullabilityState.Nullable;
    }

    private static bool IsMultipart(Type type) =>
        typeof(IFormFile).IsAssignableFrom(type) || typeof(IFormFileCollection).IsAssignableFrom(type) || typeof(IFormCollection).IsAssignableFrom(type);

    private static ParameterLocation LocationOf(ParameterInfo parameter, string method)
    {
        if (parameter.GetCustomAttribute<FromRouteAttribute>() is not null)
        {
            return ParameterLocation.Route;
        }

        if (parameter.GetCustomAttribute<FromQueryAttribute>() is not null)
        {
            return ParameterLocation.Query;
        }

        if (parameter.GetCustomAttribute<FromHeaderAttribute>() is not null)
        {
            return ParameterLocation.Header;
        }

        if (parameter.GetCustomAttribute<FromBodyAttribute>() is not null)
        {
            return ParameterLocation.Body;
        }

        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

        // Minimal APIs bind a complex type from the body and a simple one from the query string; a GET has
        // no body, so a complex type there is a bound-from-query record rather than a payload.
        return IsSimple(type) || method is "GET" or "DELETE" ? ParameterLocation.Query : ParameterLocation.Body;
    }

    private static bool IsSimple(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
        || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri);

    private static string SchemaType(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual == typeof(bool))
        {
            return "boolean";
        }

        if (actual == typeof(string) || actual.IsEnum || actual == typeof(Guid) || actual == typeof(Uri)
            || actual == typeof(DateTime) || actual == typeof(DateTimeOffset) || actual == typeof(DateOnly)
            || actual == typeof(TimeOnly) || actual == typeof(TimeSpan))
        {
            return "string";
        }

        if (actual == typeof(byte) || actual == typeof(short) || actual == typeof(int) || actual == typeof(long)
            || actual == typeof(sbyte) || actual == typeof(ushort) || actual == typeof(uint) || actual == typeof(ulong))
        {
            return "integer";
        }

        if (actual == typeof(float) || actual == typeof(double) || actual == typeof(decimal))
        {
            return "number";
        }

        return typeof(System.Collections.IEnumerable).IsAssignableFrom(actual) ? "array" : "object";
    }
}
