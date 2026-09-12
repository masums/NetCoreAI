using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Chat;

/// <summary>A request from the playground or the public chat API.</summary>
public sealed record ChatRequest
{
    public string? SessionId { get; init; }
    /// <summary>Model id or alias. Null = the session's model, then "default".</summary>
    public string? Model { get; init; }
    public required string Message { get; init; }
    public ModelParameters? Parameters { get; init; }
    public string? UserId { get; init; }
    /// <summary>Replace the conversation from this message id onward (edit-and-resend / regenerate).</summary>
    public string? ReplaceFromMessageId { get; init; }
}

/// <summary>Streamed unit of a chat response.</summary>
public sealed record ChatStreamEvent(string Type, string? Text = null, ChatSession? Session = null, ChatMessageRecord? Message = null, string? Error = null)
{
    public const string SessionType = "session";
    public const string DeltaType = "delta";
    public const string DoneType = "done";
    public const string ErrorType = "error";
}

/// <summary>Chat sessions with persistence and streaming, shared by the dashboard playground and the HTTP API.</summary>
public interface IChatService
{
    Task<IReadOnlyList<ChatSession>> ListSessionsAsync(string? userId, CancellationToken cancellationToken = default);
    Task<ChatSession?> GetSessionAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessageRecord>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<ChatSession> RenameSessionAsync(string id, string title, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
    Task<string> ExportAsync(string sessionId, string format, CancellationToken cancellationToken = default);
}

internal sealed class ChatService(IMetadataStore store, IChatClientFactory clients, IModelRegistry registry, ILogger<ChatService> logger) : IChatService
{
    private static readonly System.Text.Json.JsonSerializerOptions ExportJson = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
    public Task<IReadOnlyList<ChatSession>> ListSessionsAsync(string? userId, CancellationToken cancellationToken = default) => store.Sessions.ListAsync(userId, cancellationToken);

    public Task<ChatSession?> GetSessionAsync(string id, CancellationToken cancellationToken = default) => store.Sessions.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<ChatMessageRecord>> GetMessagesAsync(string sessionId, CancellationToken cancellationToken = default) => store.Sessions.GetMessagesAsync(sessionId, cancellationToken);

    public async Task<ChatSession> RenameSessionAsync(string id, string title, CancellationToken cancellationToken = default)
    {
        var session = await store.Sessions.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(id);
        session = session with { Title = title.Trim(), UpdatedAt = DateTimeOffset.UtcNow };
        await store.Sessions.UpsertAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default) => store.Sessions.DeleteAsync(id, cancellationToken);

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Session
        var session = request.SessionId is null ? null : await store.Sessions.GetAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        var modelRef = request.Model ?? session?.ModelId ?? ModelAlias.Default;
        var entry = await registry.GetAsync(modelRef, cancellationToken).ConfigureAwait(false) ?? throw new ModelNotFoundException(modelRef);
        var parameters = request.Parameters ?? session?.Parameters ?? entry.Descriptor.DefaultParameters;

        if (session is null)
        {
            session = new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                ModelId = entry.Descriptor.Id,
                UserId = request.UserId,
                Title = Truncate(request.Message, 60),
                Parameters = parameters,
            };
        }
        else
        {
            session = session with { ModelId = entry.Descriptor.Id, Parameters = parameters, UpdatedAt = DateTimeOffset.UtcNow };
        }

        await store.Sessions.UpsertAsync(session, cancellationToken).ConfigureAwait(false);
        yield return new ChatStreamEvent(ChatStreamEvent.SessionType, Session: session);

        // 2. History (optionally truncated for edit/regenerate)
        var history = (await store.Sessions.GetMessagesAsync(session.Id, cancellationToken).ConfigureAwait(false)).ToList();
        if (request.ReplaceFromMessageId is not null)
        {
            var index = history.FindIndex(m => m.Id == request.ReplaceFromMessageId);
            if (index >= 0)
            {
                history = history.Take(index).ToList();
                await store.Sessions.ReplaceMessagesAsync(session.Id, history, cancellationToken).ConfigureAwait(false);
            }
        }

        var userMessage = new ChatMessageRecord { Id = Guid.NewGuid().ToString("N"), SessionId = session.Id, Role = ChatRole.User.Value, Content = request.Message };
        await store.Sessions.AppendMessageAsync(userMessage, cancellationToken).ConfigureAwait(false);
        history.Add(userMessage);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(parameters.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, parameters.SystemPrompt));
        }

        messages.AddRange(history.Select(m => new ChatMessage(new ChatRole(m.Role), m.Content)));

        var options = new ChatOptions
        {
            Temperature = parameters.Temperature,
            TopP = parameters.TopP,
            TopK = parameters.TopK,
            MaxOutputTokens = parameters.MaxOutputTokens,
            FrequencyPenalty = parameters.FrequencyPenalty,
            PresencePenalty = parameters.PresencePenalty,
            Seed = parameters.Seed,
            StopSequences = parameters.StopSequences?.ToList(),
        };

        // 3. Stream
        var client = clients.Get(entry.Descriptor.Id);
        var sw = Stopwatch.StartNew();
        var text = new System.Text.StringBuilder();
        UsageDetails? usage = null;
        var error = default(string);

        var enumerator = client.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    break; // user pressed stop: keep partial text
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Generation failed for session {SessionId} on {ModelId}", session.Id, entry.Descriptor.Id);
                    error = ex.Message;
                    break;
                }

                foreach (var content in update.Contents)
                {
                    if (content is TextContent t && !string.IsNullOrEmpty(t.Text))
                    {
                        text.Append(t.Text);
                        yield return new ChatStreamEvent(ChatStreamEvent.DeltaType, t.Text);
                    }
                    else if (content is UsageContent u)
                    {
                        usage = u.Details;
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        sw.Stop();
        if (error is not null && text.Length == 0)
        {
            yield return new ChatStreamEvent(ChatStreamEvent.ErrorType, Error: error);
            yield break;
        }

        var assistant = new ChatMessageRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = session.Id,
            Role = ChatRole.Assistant.Value,
            Content = text.ToString(),
            ModelId = entry.Descriptor.Id,
            InputTokens = (int?)usage?.InputTokenCount,
            OutputTokens = (int?)usage?.OutputTokenCount,
            LatencyMs = sw.ElapsedMilliseconds,
        };
        await store.Sessions.AppendMessageAsync(assistant, CancellationToken.None).ConfigureAwait(false);
        await store.Sessions.UpsertAsync(session with { UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        yield return new ChatStreamEvent(ChatStreamEvent.DoneType, Message: assistant, Error: error);
    }

    public async Task<string> ExportAsync(string sessionId, string format, CancellationToken cancellationToken = default)
    {
        var session = await store.Sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException(sessionId);
        var messages = await store.Sessions.GetMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return System.Text.Json.JsonSerializer.Serialize(new { session, messages }, ExportJson);
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("# ").AppendLine(session.Title).AppendLine();
        foreach (var m in messages)
        {
            sb.Append("**").Append(m.Role).AppendLine("**").AppendLine().AppendLine(m.Content).AppendLine();
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max)
    {
        var line = s.Trim().Split('\n')[0];
        return line.Length <= max ? line : line[..max] + "…";
    }
}
