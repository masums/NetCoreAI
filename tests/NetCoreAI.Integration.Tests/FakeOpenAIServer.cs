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

        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            server.Requests.Add(body!);
            var last = body!["messages"]!.AsArray().Last()!["content"]!.ToString();
            var reply = $"echo: {last}";
            var model = body["model"]!.ToString();
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
