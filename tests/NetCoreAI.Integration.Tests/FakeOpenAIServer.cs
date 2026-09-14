using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Minimal OpenAI-compatible server (models, chat completions with streaming, embeddings) so the whole
/// connection → registry → factory → chat pipeline runs in CI without secrets.
/// </summary>
public sealed class FakeOpenAIServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string BaseUrl { get; private set; } = "";
    public List<JsonNode> Requests { get; } = [];
    public string ExpectedKey { get; set; } = "sk-test";

    private FakeOpenAIServer(WebApplication app) => _app = app;

    public static async Task<FakeOpenAIServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeOpenAIServer(app);

        app.Use(async (ctx, next) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth != $"Bearer {server.ExpectedKey}")
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Incorrect API key" } });
                return;
            }

            await next();
        });

        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list",
            data = new[] { new { id = "fake-chat", @object = "model", created = 0, owned_by = "test" }, new { id = "fake-embed", @object = "model", created = 0, owned_by = "test" } },
        }));

        // A fault in the double must not present as a dropped connection: the client retries, reports
        // "the response ended prematurely", and the actual mistake — here, in test code — stays invisible.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = new { message = $"FakeOpenAIServer failed: {ex}" } });
            }
        });

        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            server.Requests.Add(body!);
            var last = body!["messages"]!.AsArray().Last()!["content"]!.ToString();
            var reply = $"echo: {last}";
            var model = body["model"]!.ToString();

            // Tool calls, without pretending the fake is intelligent: a prompt may carry the arguments it
            // wants the "model" to choose, as `args:{...}`. A test that wants a refusal simply omits them.
            if (body["tools"] is JsonArray { Count: > 0 } offered && last.Contains("args:", StringComparison.Ordinal))
            {
                var arguments = last[(last.IndexOf("args:", StringComparison.Ordinal) + 5)..].Trim();

                // The wire shape for a tool has moved around: older clients nest it under "function",
                // newer ones flatten it. Read either, rather than failing halfway through a response.
                var first = offered[0]!;
                var called = (first["function"]?["name"] ?? first["name"])?.ToString() ?? "unknown";

                // Serialized before anything is written, so a mistake in this double surfaces as a 500 with
                // the reason rather than as a dropped connection the client reports as "response ended".
                var toolPayload = JsonSerializer.Serialize(new
                {
                    id = "c1",
                    @object = "chat.completion",
                    created = 0,
                    model,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = (string?)null,
                                tool_calls = new[]
                                {
                                    new { id = "call_1", type = "function", function = new { name = called, arguments } },
                                },
                            },
                            finish_reason = "tool_calls",
                        },
                    },
                    usage = new { prompt_tokens = 4, completion_tokens = 2, total_tokens = 6 },
                });

                if (body["stream"]?.GetValue<bool>() == true)
                {
                    // Agents always stream, so a tool call has to be expressible here too — otherwise a
                    // streaming run silently gets prose where the test expects a call.
                    var call = new
                    {
                        id = "c1",
                        @object = "chat.completion.chunk",
                        created = 0,
                        model,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                delta = new
                                {
                                    role = "assistant",
                                    tool_calls = new[]
                                    {
                                        new { index = 0, id = "call_1", type = "function", function = new { name = called, arguments } },
                                    },
                                },
                                finish_reason = (string?)null,
                            },
                        },
                    };

                    var stop = new { id = "c1", @object = "chat.completion.chunk", created = 0, model, choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } } };
                    var chunks = $"data: {JsonSerializer.Serialize(call)}\n\ndata: {JsonSerializer.Serialize(stop)}\n\ndata: [DONE]\n\n";

                    ctx.Response.ContentType = "text/event-stream";
                    await ctx.Response.WriteAsync(chunks);
                    return;
                }

                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(toolPayload);
                return;
            }

            if (body["stream"]?.GetValue<bool>() == true)
            {
                ctx.Response.ContentType = "text/event-stream";
                foreach (var chunk in reply.Split(' '))
                {
                    var payload = new { id = "c1", @object = "chat.completion.chunk", created = 0, model, choices = new[] { new { index = 0, delta = new { role = "assistant", content = chunk + " " }, finish_reason = (string?)null } } };
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n");
                }

                var final = new { id = "c1", @object = "chat.completion.chunk", created = 0, model, choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 4, completion_tokens = 2, total_tokens = 6 } };
                await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(final)}\n\ndata: [DONE]\n\n");
                return;
            }

            await ctx.Response.WriteAsJsonAsync(new
            {
                id = "c1",
                @object = "chat.completion",
                created = 0,
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = reply }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 4, completion_tokens = 2, total_tokens = 6 },
            });
        });

        app.MapPost("/v1/embeddings", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            var inputs = body!["input"] is JsonArray arr ? arr.Select(n => n!.ToString()).ToList() : [body["input"]!.ToString()];
            await ctx.Response.WriteAsJsonAsync(new
            {
                @object = "list",
                data = inputs.Select((s, i) => new { @object = "embedding", index = i, embedding = new float[] { s.Length, 1, 0 } }),
                model = body["model"]!.ToString(),
                usage = new { prompt_tokens = 1, total_tokens = 1 },
            });
        });

        await app.StartAsync();
        server.BaseUrl = app.Urls.First() + "/v1";
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
