using System.Security.Claims;
using System.Text.Json;

namespace NetCoreAI.Tools;

/// <summary>The values a tool call will actually send, once the model's arguments and the host's are joined.</summary>
/// <param name="Route">Route values, substituted into the route pattern.</param>
/// <param name="Query">Query string values.</param>
/// <param name="Headers">Header values.</param>
/// <param name="Body">The request body, or null when there is none.</param>
internal sealed record BoundArguments(
    IReadOnlyDictionary<string, string> Route,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    JsonElement? Body);

/// <summary>
/// Joins what the model supplied with what the host supplies, and refuses a call the host cannot complete.
/// </summary>
/// <remarks>
/// The model's arguments are consulted only for parameters the definition says it may set. An argument
/// naming a locked parameter is dropped rather than merged, so a model that has somehow learned a tenant
/// id cannot send one: the value used is always the one bound from the caller.
/// </remarks>
internal static class ToolBinding
{
    public static BoundArguments Bind(ToolDefinition tool, IReadOnlyDictionary<string, object?>? arguments, ToolCallContext context)
    {
        var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        JsonElement? wholeBody = null;

        foreach (var parameter in tool.Parameters)
        {
            var value = parameter.IsModelSupplied
                ? FromModel(arguments, parameter)
                : FromHost(parameter, context);

            if (value is null)
            {
                if (parameter.Required && parameter.Binding != ParameterBinding.Model)
                {
                    // A required value the host was meant to supply and could not. Calling anyway would
                    // send null for something like a tenant id, which is the difference between "this
                    // tenant" and "all of them".
                    throw new NetCoreAIException(
                        $"'{tool.Name}' needs '{parameter.Name}' from {parameter.Binding} '{parameter.BindingSource}', and the caller has no such value.");
                }

                continue;
            }

            switch (parameter.Location)
            {
                case ParameterLocation.Route:
                    route[parameter.Name] = Text(value);
                    break;
                case ParameterLocation.Query:
                    query[parameter.Name] = Text(value);
                    break;
                case ParameterLocation.Header:
                    headers[parameter.Name] = Text(value);
                    break;
                case ParameterLocation.Body when string.Equals(parameter.Name, "body", StringComparison.OrdinalIgnoreCase):
                    // A single parameter called "body" is the payload itself rather than one field of it.
                    wholeBody = AsElement(value);
                    break;
                default:
                    body[parameter.Name] = value;
                    break;
            }
        }

        var payload = wholeBody ?? (body.Count > 0
            ? JsonSerializer.SerializeToElement(body)
            : null);

        return new BoundArguments(route, query, headers, payload);
    }

    private static object? FromModel(IReadOnlyDictionary<string, object?>? arguments, ToolParameter parameter)
    {
        if (arguments is not null && arguments.TryGetValue(parameter.Name, out var supplied) && supplied is not null)
        {
            return supplied;
        }

        return parameter.Default;
    }

    private static string? FromHost(ToolParameter parameter, ToolCallContext context) => parameter.Binding switch
    {
        ParameterBinding.Static => parameter.BindingSource,
        ParameterBinding.Claim => Claim(context.User, parameter.BindingSource),
        ParameterBinding.RequestMetadata => parameter.BindingSource is { } key ? context.RequestMetadata?.GetValueOrDefault(key) : null,
        _ => parameter.Default,
    };

    /// <summary>
    /// Reads a claim, accepting either the full type URI or the short name an author would write.
    /// </summary>
    /// <remarks>
    /// Identity stacks spell the same claim several ways — <c>role</c> and the long
    /// <c>schemas.microsoft.com/.../role</c> among them — and a binding that silently found nothing would
    /// fail open into "no tenant" rather than obviously.
    /// </remarks>
    private static string? Claim(ClaimsPrincipal? user, string? type)
    {
        if (user is null || type is not { Length: > 0 })
        {
            return null;
        }

        return user.FindFirst(type)?.Value
            ?? user.Claims.FirstOrDefault(c => Short(c.Type).Equals(type, StringComparison.OrdinalIgnoreCase))?.Value;

        static string Short(string claimType) =>
            claimType.Contains('/', StringComparison.Ordinal) ? claimType[(claimType.LastIndexOf('/') + 1)..] : claimType;
    }

    private static string Text(object value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
        JsonElement e => e.ToString(),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static JsonElement AsElement(object value) => value switch
    {
        JsonElement e => e,

        // A model that produced a JSON object as text should not have it sent as a quoted string.
        string s when s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('[') => Parse(s),
        _ => JsonSerializer.SerializeToElement(value),
    };

    private static JsonElement Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }
}
