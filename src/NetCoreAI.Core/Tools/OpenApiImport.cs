using System.Text.Json;

namespace NetCoreAI.Tools;

/// <summary>
/// Turns an OpenAPI 3.x document into tool definitions.
/// </summary>
/// <remarks>
/// Reads the document with <c>System.Text.Json</c> rather than taking a dependency on an OpenAPI library.
/// Only a small part of the specification matters here — paths, operations, parameters and the shape of a
/// request body — and pulling a full parser and its YAML dependency into the package every host references
/// is a real cost for one import feature. The price is that JSON documents are understood and YAML ones are
/// refused with an explanation rather than being half-read.
/// </remarks>
internal static class OpenApiImport
{
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];

    public static IReadOnlyList<ToolDefinition> Read(string document, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new NetCoreAIException("The OpenAPI document is empty.");
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(document);
        }
        catch (JsonException ex)
        {
            throw new NetCoreAIException(
                document.TrimStart().StartsWith("openapi:", StringComparison.OrdinalIgnoreCase) || document.TrimStart().StartsWith("swagger:", StringComparison.OrdinalIgnoreCase)
                    ? "This looks like a YAML document. Convert it to JSON first — most tools that serve a spec offer both."
                    : $"The OpenAPI document could not be read as JSON: {ex.Message}",
                ex);
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("paths", out var paths))
            {
                throw new NetCoreAIException("The document has no 'paths', so there are no operations to import.");
            }

            var server = baseUrl ?? FirstServer(root);
            if (server is not { Length: > 0 })
            {
                throw new NetCoreAIException("The document names no server, so there is nowhere to send a call. Give a base URL with the import.");
            }

            var tools = new List<ToolDefinition>();
            foreach (var path in paths.EnumerateObject())
            {
                foreach (var method in Methods)
                {
                    if (path.Value.TryGetProperty(method, out var operation))
                    {
                        tools.Add(ToolFor(root, path.Name, method, operation, server));
                    }
                }
            }

            if (tools.Count == 0)
            {
                throw new NetCoreAIException("The document has paths but no GET, POST, PUT, PATCH or DELETE operations.");
            }

            return tools;
        }
    }

    private static string? FirstServer(JsonElement root) =>
        root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array
            && servers.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
            && first.TryGetProperty("url", out var url)
            ? url.GetString()
            : null;

    private static ToolDefinition ToolFor(JsonElement root, string path, string method, JsonElement operation, string server)
    {
        var upper = method.ToUpperInvariant();
        var name = Text(operation, "operationId") is { Length: > 0 } operationId
            ? Sanitize(operationId)
            : EndpointDiscoveryService.NameFor(upper, path);

        var parameters = new List<ToolParameter>();
        if (operation.TryGetProperty("parameters", out var declared) && declared.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in declared.EnumerateArray())
            {
                if (Parameter(root, parameter) is { } mapped)
                {
                    parameters.Add(mapped);
                }
            }
        }

        if (operation.TryGetProperty("requestBody", out var body))
        {
            parameters.Add(new ToolParameter
            {
                Name = "body",
                Type = "object",
                Description = Text(body, "description") ?? "The request body.",
                Required = body.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
                Location = ParameterLocation.Body,
            });
        }

        return new ToolDefinition
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name,
            Description = Text(operation, "summary") ?? Text(operation, "description"),
            Kind = ToolKind.OpenApi,
            Method = upper,
            Route = path,
            BaseUrl = server.TrimEnd('/'),

            // An imported tool describes a service somewhere else. It has no place in this host's pipeline,
            // so in-process invocation is not on the table for it at all.
            InvocationMode = ToolInvocationMode.HttpExternal,
            Safety = upper is "GET" ? ToolSafety.ReadOnly : ToolSafety.SideEffecting,

            // The caller's credentials are for this host. Sending them to a third-party service because a
            // spec was imported would leak them, so an imported tool starts with none.
            ForwardCallerCredentials = false,
            Parameters = parameters,
        };
    }

    private static ToolParameter? Parameter(JsonElement root, JsonElement parameter)
    {
        var resolved = Resolve(root, parameter);
        if (Text(resolved, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        var location = Text(resolved, "in") switch
        {
            "path" => ParameterLocation.Route,
            "header" => ParameterLocation.Header,
            "cookie" => null as ParameterLocation?,
            _ => ParameterLocation.Query,
        };

        if (location is null)
        {
            // A cookie parameter is part of a session, not something a model supplies.
            return null;
        }

        var schema = resolved.TryGetProperty("schema", out var s) ? Resolve(root, s) : default;
        return new ToolParameter
        {
            Name = name,
            Type = (schema.ValueKind == JsonValueKind.Object ? Text(schema, "type") : null) ?? "string",
            Description = Text(resolved, "description"),
            Required = resolved.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
            Location = location.Value,
            Enum = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
                ? [.. values.EnumerateArray().Select(v => v.ToString())]
                : null,
            Default = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("default", out var fallback) ? fallback.ToString() : null,
        };
    }

    /// <summary>
    /// Follows a local <c>$ref</c> into the document's own components.
    /// </summary>
    /// <remarks>
    /// Local refs only. A ref into another file would need that file fetched, and fetching URLs named by an
    /// uploaded document is a request-forgery hole rather than a feature.
    /// </remarks>
    private static JsonElement Resolve(JsonElement root, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("$ref", out var reference))
        {
            return element;
        }

        if (reference.GetString() is not { } pointer || !pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return element;
        }

        var current = root;
        foreach (var segment in pointer[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
            {
                return element;
            }
        }

        return current;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>An operationId as a tool name: providers accept letters, digits and underscores only.</summary>
    private static string Sanitize(string operationId)
    {
        var cleaned = string.Concat(operationId.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        if (cleaned.Length == 0 || !char.IsLetter(cleaned[0]))
        {
            cleaned = "op_" + cleaned;
        }

        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
