using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>
/// The passages an answer was grounded in, carried alongside the text so callers can show sources without
/// parsing them back out of the reply.
/// </summary>
public sealed class CitationContent(IReadOnlyList<Citation> citations) : AIContent
{
    public IReadOnlyList<Citation> Citations { get; } = citations;
}

/// <summary>How retrieved context is put in front of the model.</summary>
public sealed record RagOptions
{
    /// <summary>Knowledge bases to search. Empty means the decorator passes the call straight through.</summary>
    public IReadOnlyList<string> KnowledgeBaseIds { get; init; } = [];

    /// <summary>Retrieval settings; the base's own defaults are used when null.</summary>
    public RetrievalOptions? Retrieval { get; init; }

    /// <summary>Access tags of the caller. Empty means only public passages; null means no filtering.</summary>
    public IReadOnlyList<string>? CallerTags { get; init; }

    /// <summary>
    /// Answer only from the retrieved passages. On by default: a knowledge base is usually consulted
    /// precisely because the model's own memory is not trusted for that subject.
    /// </summary>
    public bool GroundedOnly { get; init; } = true;

    /// <summary>Say so when nothing was retrieved, rather than letting the model answer from memory.</summary>
    public bool AnswerWithoutContext { get; init; }
}

/// <summary>
/// Wraps a chat client so each turn is answered from a knowledge base.
/// </summary>
/// <remarks>
/// The last user message is used as the query, the retrieved passages are put in a system message with
/// numbered sources, and the citations for those passages are attached to the response. Phase 3's agents
/// reuse this decorator rather than reimplementing retrieval.
/// </remarks>
internal sealed class RagChatClient(IChatClient inner, IRetriever retriever, RagOptions options, ILogger logger) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var (prepared, citations) = await PrepareAsync(messages, cancellationToken).ConfigureAwait(false);
        var response = await base.GetResponseAsync(prepared, chatOptions, cancellationToken).ConfigureAwait(false);

        if (citations.Count > 0)
        {
            response.Messages[^1].Contents.Add(new CitationContent(citations));
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var (prepared, citations) = await PrepareAsync(messages, cancellationToken).ConfigureAwait(false);

        // Sources first: the UI can render them beside the answer as it streams rather than after it ends.
        if (citations.Count > 0)
        {
            yield return new ChatResponseUpdate { Contents = [new CitationContent(citations)] };
        }

        await foreach (var update in base.GetStreamingResponseAsync(prepared, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Retrieves for the latest user message and returns the messages to send, plus the citations.</summary>
    private async Task<(List<ChatMessage> Messages, IReadOnlyList<Citation> Citations)> PrepareAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        if (options.KnowledgeBaseIds.Count == 0)
        {
            return (list, []);
        }

        var query = list.LastOrDefault(m => m.Role == ChatRole.User) is { } last
            ? string.Concat(last.Contents.OfType<TextContent>().Select(c => c.Text))
            : null;

        if (string.IsNullOrWhiteSpace(query))
        {
            return (list, []);
        }

        IReadOnlyList<RetrievedChunk> hits;
        try
        {
            hits = await retriever.SearchManyAsync(options.KnowledgeBaseIds, query, options.Retrieval, options.CallerTags, cancellationToken).ConfigureAwait(false);
        }
        catch (NetCoreAIException ex)
        {
            // Retrieval failing should not take the conversation down; the model answers without context
            // and the absence of citations shows the answer is ungrounded.
            logger.LogWarning(ex, "Retrieval failed; answering without context.");
            return (list, []);
        }

        if (hits.Count == 0)
        {
            return (Ungrounded(list), []);
        }

        var grounded = new List<ChatMessage>(list.Count + 1)
        {
            new(ChatRole.System, BuildContext(hits)),
        };

        grounded.AddRange(list);
        return (grounded, [.. hits.Select(h => h.Citation)]);
    }

    /// <summary>
    /// Tells the model to say it does not know rather than inventing an answer, when the knowledge base
    /// had nothing. A fabricated answer with no citations is the worst outcome a RAG system can produce.
    /// </summary>
    private List<ChatMessage> Ungrounded(List<ChatMessage> messages)
    {
        if (options.AnswerWithoutContext || !options.GroundedOnly)
        {
            return messages;
        }

        var instructed = new List<ChatMessage>(messages.Count + 1)
        {
            new(ChatRole.System,
                "The knowledge base has nothing relevant to this question. Say that you could not find an answer in the available documents. Do not answer from your own knowledge."),
        };

        instructed.AddRange(messages);
        return instructed;
    }

    /// <summary>Renders the retrieved passages as numbered sources the model is told to cite.</summary>
    internal static string BuildContext(IReadOnlyList<RetrievedChunk> hits)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Answer the question using only the sources below. Cite them inline as [1], [2] and so on, matching the numbers here.");
        builder.AppendLine("If the sources do not contain the answer, say so rather than guessing.");
        builder.AppendLine();

        foreach (var hit in hits)
        {
            var citation = hit.Citation;
            builder.Append('[').Append(citation.Ordinal).Append("] ").Append(citation.Title);

            // The location goes in the prompt too, so a model asked "where does it say that?" can answer.
            if (citation.Page is { } page)
            {
                builder.Append(", page ").Append(page);
            }
            else if (citation.Section is { Length: > 0 } section)
            {
                builder.Append(", section \"").Append(section).Append('"');
            }

            builder.AppendLine();
            builder.AppendLine(hit.Chunk.Text);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>Builds chat clients that answer from knowledge bases.</summary>
public interface IRagChatClientFactory
{
    /// <summary>Wraps the model behind <paramref name="idOrAlias"/> so its answers are grounded and cited.</summary>
    IChatClient Create(string idOrAlias, RagOptions options);
}

internal sealed class RagChatClientFactory(IChatClientFactory clients, IRetriever retriever, ILoggerFactory loggerFactory) : IRagChatClientFactory
{
    public IChatClient Create(string idOrAlias, RagOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RagChatClient(clients.Get(idOrAlias), retriever, options, loggerFactory.CreateLogger<RagChatClient>());
    }
}
