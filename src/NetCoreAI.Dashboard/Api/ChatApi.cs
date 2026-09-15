using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetCoreAI.Chat;

namespace NetCoreAI.Dashboard.Api;

internal static class ChatApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder api)
    {
        // Streams Server-Sent Events: session, delta*, done | error.
        api.MapPost("/chat", async (ChatRequest request, IChatService chat, HttpContext http, CancellationToken ct) =>
        {
            var userId = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.Identity.Name : null;
            var scoped = request with { UserId = request.UserId ?? userId };

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.StartAsync(ct);

            try
            {
                await foreach (var evt in chat.StreamAsync(scoped, ct))
                {
                    await http.Response.WriteAsync($"event: {evt.Type}\ndata: {JsonSerializer.Serialize(evt, Json)}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (NetCoreAIException ex)
            {
                await http.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(new ChatStreamEvent(ChatStreamEvent.ErrorType, Error: ex.Message), Json)}\n\n", CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // client went away
            }
        }).WithName("NetCoreAI.Chat");

        // Reading a file for one turn of a conversation. The text comes straight back rather than being
        // stored: it belongs to the message it came with, and a file somebody wants answers from
        // repeatedly should be ingested into a knowledge base instead.
        api.MapPost("/chat/attachments", async (
            HttpRequest request,
            NetCoreAI.Knowledge.IAttachmentReader reader,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.Problem("Send the file as multipart/form-data.", statusCode: StatusCodes.Status400BadRequest, title: "Not a file upload");
            }

            Microsoft.AspNetCore.Http.IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (InvalidDataException ex)
            {
                // A malformed body is the caller's mistake, not a fault here. Without this it surfaces as
                // an unhandled exception and a 500, which tells an upload client nothing it can act on.
                return Results.Problem(
                    $"That upload could not be read: {ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Malformed upload");
            }

            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return Results.Problem("No file was attached.", statusCode: StatusCodes.Status400BadRequest, title: "Nothing to read");
            }

            await using var content = file.OpenReadStream();
            var attachment = await reader.ReadAsync(content, file.FileName, file.ContentType, cancellationToken: ct);

            return Results.Ok(attachment);
        }).WithName("NetCoreAI.Chat.Attach").DisableAntiforgery();

        api.MapGet("/chat/attachments/supported", (NetCoreAI.Knowledge.IAttachmentReader reader) =>
            Results.Ok(reader.SupportedExtensions)).WithName("NetCoreAI.Chat.AttachmentTypes");

        var sessions = api.MapGroup("/sessions");
        sessions.MapGet("/", async (IChatService chat, HttpContext http, CancellationToken ct) =>
            Results.Ok(await chat.ListSessionsAsync(UserId(http), ct))).WithName("NetCoreAI.Sessions.List");

        sessions.MapGet("/{id}", async (string id, IChatService chat, CancellationToken ct) =>
        {
            var session = await chat.GetSessionAsync(id, ct);
            return session is null ? Results.NotFound() : Results.Ok(new { session, messages = await chat.GetMessagesAsync(id, ct) });
        }).WithName("NetCoreAI.Sessions.Get");

        sessions.MapPut("/{id}", async (string id, RenameRequest body, IChatService chat, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await chat.RenameSessionAsync(id, body.Title, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("NetCoreAI.Sessions.Rename");

        sessions.MapDelete("/{id}", async (string id, IChatService chat, CancellationToken ct) =>
        {
            await chat.DeleteSessionAsync(id, ct);
            return Results.NoContent();
        }).WithName("NetCoreAI.Sessions.Delete");

        sessions.MapGet("/{id}/export", async (string id, IChatService chat, string format = "markdown", CancellationToken ct = default) =>
        {
            try
            {
                var content = await chat.ExportAsync(id, format, ct);
                var isJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
                return Results.Text(content, isJson ? "application/json" : "text/markdown");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("NetCoreAI.Sessions.Export");
    }

    private static string? UserId(HttpContext http)
        => http.User.Identity?.IsAuthenticated == true ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.Identity.Name : null;

    public sealed record RenameRequest(string Title);
}
