using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using NetCoreAI.Agents;
using NetCoreAI.Security;

namespace NetCoreAI.Dashboard.Api;

/// <summary>
/// The OpenAI wire format, served by this host.
/// </summary>
/// <remarks>
/// <para>
/// So that a tool already pointed at OpenAI can be pointed here instead by changing a base URL and a key.
/// That is the whole ambition: it is a translation layer, not a second API. Anything NetCoreAI can do that
/// OpenAI's shape cannot express — citations, tool traces, run ids — lives on the native API, and nothing
/// here invents a field to carry it.
/// </para>
/// <para>
/// <c>model</c> accepts a model id, an alias, or an <em>agent id</em>. Pointing an existing client at an
/// agent is the useful part: the agent's prompt, tools and knowledge bases apply, and the client neither
/// knows nor needs to.
/// </para>
/// </remarks>
internal static class OpenAiCompatApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Map(RouteGroupBuilder group)
    {
        var v1 = group.MapGroup("/v1");

        v1.MapGet("/models", async (IModelRegistry models, IAgentService agents, CancellationToken ct) =>
        {
            var entries = new List<object>();
            foreach (var model in await models.ListAsync(ct))
            {
                entries.Add(Model(model.Descriptor.Id, "netcoreai-model"));
            }

            foreach (var agent in await agents.ListAsync(ct))
            {
                // Agents are listed too, because a client that populates a model picker from this is how
                // most people will discover they can point at one.
                entries.Add(Model(agent.Id, "netcoreai-agent"));
            }

            return Results.Ok(new { @object = "list", data = entries });
        }).WithName("NetCoreAI.OpenAI.Models");

        v1.MapPost("/chat/completions", async (
            JsonElement body,
            HttpContext http,
            IChatClientFactory clients,
            IAgentService agents,
            CancellationToken ct) =>
        {
            if (Read(body) is not { } request)
            {
                return Error("A 'model' and a non-empty 'messages' array are required.", "invalid_request_error", StatusCodes.Status400BadRequest);
            }

            var agent = await agents.GetAsync(request.Model, ct);
            if (agent is not null && http.ApiKey() is { } key && !key.CanRun(agent.Id))
            {
                return Error($"This API key is not scoped to run '{agent.Id}'.", "permission_error", StatusCodes.Status403Forbidden);
            }

            try
            {
                return request.Stream
                    ? await StreamAsync(http, request, agent, clients, agents, ct)
                    : await CompleteAsync(http, request, agent, clients, agents, ct);
            }
            catch (ModelNotFoundException)
            {
                // The code OpenAI uses for this, because a client's error handling is written against it.
                return Error($"The model '{request.Model}' does not exist.", "invalid_request_error", StatusCodes.Status404NotFound, "model_not_found");
            }
            catch (NetCoreAIException ex)
            {
                return Error(ex.Message, "invalid_request_error", StatusCodes.Status400BadRequest);
            }
        }).WithName("NetCoreAI.OpenAI.ChatCompletions");

        v1.MapPost("/embeddings", async (JsonElement body, IChatClientFactory clients, CancellationToken ct) =>
        {
            var model = body.TryGetProperty("model", out var m) ? m.GetString() : null;
            var inputs = Inputs(body);

            if (model is not { Length: > 0 } || inputs.Count == 0)
            {
                return Error("A 'model' and a non-empty 'input' are required.", "invalid_request_error", StatusCodes.Status400BadRequest);
            }

            try
            {
                var embeddings = await clients.GetEmbeddingGenerator(model).GenerateAsync(inputs, cancellationToken: ct);
                var data = embeddings.Select((e, i) => new { @object = "embedding", index = i, embedding = e.Vector.ToArray() });

                return Results.Json(
                    new
                    {
                        @object = "list",
                        data,
                        model,
                        usage = new { prompt_tokens = 0, total_tokens = 0 },
                    },
                    Json);
            }
            catch (ModelNotFoundException)
            {
                return Error($"The model '{model}' does not exist.", "invalid_request_error", StatusCodes.Status404NotFound, "model_not_found");
            }
        }).WithName("NetCoreAI.OpenAI.Embeddings");
    }

    // ---------- answering ----------

    private static async Task<IResult> CompleteAsync(
        HttpContext http,
        Request request,
        AgentDefinition? agent,
        IChatClientFactory clients,
        IAgentService agents,
        CancellationToken ct)
    {
        string text;
        int? input = null;
        int? output = null;

        if (agent is not null)
        {
            var response = await agents.RunAsync(agent.Id, AgentRequest(request), Caller(http), ct);
            text = response.Text;
        }
        else
        {
            var response = await clients.Get(request.Model).GetResponseAsync(request.Messages, Options(request), ct);
            text = response.Text;
            input = (int?)response.Usage?.InputTokenCount;
            output = (int?)response.Usage?.OutputTokenCount;
        }

        return Results.Json(
            new
            {
                id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = request.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = text },
                        finish_reason = "stop",
                    },
                },
                usage = new
                {
                    prompt_tokens = input ?? 0,
                    completion_tokens = output ?? 0,
                    total_tokens = (input ?? 0) + (output ?? 0),
                },
            },
            Json);
    }

    private static async Task<IResult> StreamAsync(
        HttpContext http,
        Request request,
        AgentDefinition? agent,
        IChatClientFactory clients,
        IAgentService agents,
        CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // The first chunk carries the role and no content, which is what OpenAI sends and what more than
        // one client library assumes before it will read any of the rest.
        await WriteAsync(http, Chunk(id, created, request.Model, role: "assistant"), ct);

        try
        {
            if (agent is not null)
            {
                await foreach (var evt in agents.RunStreamingAsync(agent.Id, AgentRequest(request), Caller(http), ct))
                {
                    if (evt.Type == AgentEvent.DeltaType && evt.Text is { Length: > 0 } delta)
                    {
                        await WriteAsync(http, Chunk(id, created, request.Model, content: delta), ct);
                    }
                    else if (evt.Type == AgentEvent.ErrorType)
                    {
                        // Mid-stream the status is already 200, so the only honest thing left is to say so
                        // in the stream and stop. A client sees a truncated answer with a reason attached,
                        // which beats one that ends for no stated reason.
                        await WriteAsync(http, Chunk(id, created, request.Model, finish: "error"), ct);
                        await WriteAsync(http, "[DONE]", ct);
                        return Results.Empty;
                    }
                }
            }
            else
            {
                await foreach (var update in clients.Get(request.Model).GetStreamingResponseAsync(request.Messages, Options(request), ct))
                {
                    if (update.Text is { Length: > 0 } delta)
                    {
                        await WriteAsync(http, Chunk(id, created, request.Model, content: delta), ct);
                    }
                }
            }

            await WriteAsync(http, Chunk(id, created, request.Model, finish: "stop"), ct);
            await WriteAsync(http, "[DONE]", ct);
        }
        catch (OperationCanceledException)
        {
            // The caller went away. Nothing more to write, and nothing to apologise to.
        }

        return Results.Empty;
    }

    // ---------- shapes ----------

    private sealed record Request(string Model, List<ChatMessage> Messages, bool Stream)
    {
        public float? Temperature { get; init; }

        public float? TopP { get; init; }

        public int? MaxTokens { get; init; }

        public List<string>? Stop { get; init; }
    }

    /// <summary>
    /// Reads the parts of a request this host can honour, and ignores the rest.
    /// </summary>
    /// <remarks>
    /// Unknown fields are skipped rather than rejected. A client sending <c>frequency_penalty</c> to a
    /// model that has no such notion should get an answer, not a 400 about a field it sends to everybody.
    /// </remarks>
    private static Request? Read(JsonElement body)
    {
        if (!body.TryGetProperty("model", out var model) || model.GetString() is not { Length: > 0 } modelId
            || !body.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var chat = new List<ChatMessage>();
        foreach (var message in messages.EnumerateArray())
        {
            var role = message.TryGetProperty("role", out var r) ? r.GetString() : "user";
            var content = message.TryGetProperty("content", out var c) ? Content(c) : "";
            if (content is { Length: > 0 })
            {
                chat.Add(new ChatMessage(Role(role), content));
            }
        }

        return chat.Count == 0
            ? null
            : new Request(modelId, chat, body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True)
            {
                Temperature = Number(body, "temperature"),
                TopP = Number(body, "top_p"),
                MaxTokens = (int?)(Number(body, "max_tokens") ?? Number(body, "max_completion_tokens")),
                Stop = Stop(body),
            };
    }

    /// <summary>
    /// A message's content, which may be a string or the newer array of parts.
    /// </summary>
    /// <remarks>
    /// Text parts are joined and anything else is dropped. A host with no vision model cannot do
    /// something useful with an image part, and answering the text is better than refusing the message.
    /// </remarks>
    private static string Content(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } value)
            {
                parts.Add(value);
            }
        }

        return string.Join("\n", parts);
    }

    private static ChatRole Role(string? role) => role switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,

        // "developer" is OpenAI's newer name for a system message, and anything unrecognised is safest
        // read as the user talking.
        "developer" => ChatRole.System,
        _ => ChatRole.User,
    };

    private static float? Number(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : null;

    private static List<string>? Stop(JsonElement body)
    {
        if (!body.TryGetProperty("stop", out var stop))
        {
            return null;
        }

        return stop.ValueKind switch
        {
            JsonValueKind.String => [stop.GetString()!],
            JsonValueKind.Array => [.. stop.EnumerateArray().Select(s => s.GetString()).OfType<string>().Where(s => s.Length > 0)],
            _ => null,
        };
    }

    private static List<string> Inputs(JsonElement body)
    {
        if (!body.TryGetProperty("input", out var input))
        {
            return [];
        }

        return input.ValueKind switch
        {
            JsonValueKind.String => [input.GetString()!],
            JsonValueKind.Array => [.. input.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)],
            _ => [],
        };
    }

    private static ChatOptions Options(Request request) => new()
    {
        Temperature = request.Temperature,
        TopP = request.TopP,
        MaxOutputTokens = request.MaxTokens,
        StopSequences = request.Stop,
    };

    /// <summary>
    /// The agent request behind an OpenAI-shaped one.
    /// </summary>
    /// <remarks>
    /// Only the last user message is the question; the rest is history the agent keeps itself. A client
    /// that resends the whole conversation every turn — which most do — would otherwise have it counted
    /// twice, once as its own memory and once as the question.
    /// </remarks>
    private static AgentRequest AgentRequest(Request request) =>
        new() { Message = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? request.Messages[^1].Text ?? "" };

    private static AgentCaller Caller(HttpContext http) => new()
    {
        User = http.User,
        UserId = http.User.Identity?.IsAuthenticated == true
            ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.Identity.Name
            : null,
        BaseAddress = new Uri($"{http.Request.Scheme}://{http.Request.Host}"),
        AuthorizationHeader = http.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null,
    };

    private static object Model(string id, string owner) => new
    {
        id,
        @object = "model",
        created = 0,
        owned_by = owner,
    };

    private static string Chunk(string id, long created, string model, string? role = null, string? content = null, string? finish = null)
    {
        var delta = new JsonObject();
        if (role is not null)
        {
            delta["role"] = role;
        }

        if (content is not null)
        {
            delta["content"] = content;
        }

        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finish,
            }),
        }.ToJsonString();
    }

    private static async Task WriteAsync(HttpContext http, string payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {payload}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// An error in OpenAI's envelope.
    /// </summary>
    /// <remarks>
    /// Not <c>Results.Problem</c>. A client written against OpenAI reads <c>error.message</c>, and giving
    /// it an RFC 9110 problem document here would mean every one of them reports "an error occurred".
    /// </remarks>
    private static IResult Error(string message, string type, int status, string? code = null) =>
        Results.Json(new { error = new { message, type, param = (string?)null, code } }, Json, statusCode: status);
}
