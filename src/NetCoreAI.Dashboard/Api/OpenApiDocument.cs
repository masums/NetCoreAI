using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NetCoreAI.Dashboard.Api;

/// <summary>
/// An OpenAPI 3.1 description of NetCoreAI's own API.
/// </summary>
/// <remarks>
/// Written from the route table rather than by adding <c>Microsoft.AspNetCore.OpenApi</c> to the package
/// every host references. Generating a document for a fixed, known set of endpoints does not need a
/// generator, and a host that already produces its own OpenAPI document should not end up with two.
/// <para>
/// It describes paths, methods and parameters — enough for a client generator or an HTTP client to work
/// from. Request and response bodies are named rather than fully schematised; the guides carry the shapes.
/// </para>
/// </remarks>
internal static class OpenApiDocument
{
    public static void Map(RouteGroupBuilder group, string basePath)
    {
        group.MapGet("/openapi/v1.json", (EndpointDataSource endpoints, HttpContext http) =>
        {
            var document = Build(endpoints, basePath, $"{http.Request.Scheme}://{http.Request.Host}");
            return Results.Text(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "application/json");
        }).AllowAnonymous().ExcludeFromDescription().WithName("NetCoreAI.OpenApi");
    }

    internal static JsonObject Build(EndpointDataSource endpoints, string basePath, string serverUrl)
    {
        var paths = new JsonObject();

        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            // A group's own "/" route comes through as "…/agents/", which would appear beside "…/agents"
            // as a second path. Callers do not distinguish them, so neither does this.
            var route = ("/" + endpoint.RoutePattern.RawText?.TrimStart('/')).TrimEnd('/');
            if (route.Length == 0)
            {
                route = "/";
            }

            if (!route.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
                || !route.Contains("/api/", StringComparison.OrdinalIgnoreCase)
                || endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription == true)
            {
                // Pages, assets and the streaming endpoints that are documented in the guides instead.
                continue;
            }

            var item = paths[route] as JsonObject;
            if (item is null)
            {
                item = [];
                paths[route] = item;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods.Where(m => m is not "OPTIONS" and not "HEAD"))
            {
                item[method.ToLowerInvariant()] = Operation(endpoint, method, route);
            }
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "NetCoreAI",
                ["version"] = typeof(OpenApiDocument).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                ["description"] = "Models, knowledge bases, tools and agents in this host. Authenticate with an API key as a bearer token.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = serverUrl }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["apiKey"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["description"] = "A key issued by this host. Shown once when created; only its hash is stored.",
                    },
                },
            },
            ["security"] = new JsonArray(new JsonObject { ["apiKey"] = new JsonArray() }),
        };
    }

    private static JsonObject Operation(RouteEndpoint endpoint, string method, string route)
    {
        var operation = new JsonObject
        {
            ["operationId"] = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName
                ?? $"{method.ToLowerInvariant()}{route.Replace('/', '_').Replace('{', ' ').Replace('}', ' ').Trim()}",
            ["summary"] = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IEndpointSummaryMetadata>()?.Summary
                ?? $"{method} {route}",
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject { ["description"] = "Success." },
                ["401"] = new JsonObject { ["description"] = "No usable API key was presented." },
                ["403"] = new JsonObject { ["description"] = "The key is not scoped to this." },
            },
        };

        var parameters = new JsonArray();
        foreach (var parameter in endpoint.RoutePattern.Parameters)
        {
            parameters.Add(new JsonObject
            {
                ["name"] = parameter.Name,
                ["in"] = "path",
                ["required"] = !parameter.IsOptional,
                ["schema"] = new JsonObject { ["type"] = "string" },
            });
        }

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        if (method is "POST" or "PUT" or "PATCH")
        {
            operation["requestBody"] = new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } },
                },
            };
        }

        return operation;
    }
}
