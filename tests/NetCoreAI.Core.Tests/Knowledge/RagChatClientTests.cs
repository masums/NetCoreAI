using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// The RAG decorator: what reaches the model, what comes back attached to the answer, and what happens
/// when the knowledge base has nothing to say.
/// </summary>
public class RagChatClientTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";
    private RecordingProvider _provider = default!;

    private async Task<(IKnowledgeService Knowledge, IRagChatClientFactory Rag)> StartAsync()
    {
        _provider = new RecordingProvider();
        _host = await TestHost.StartAsync(
            b => b.Services.AddSingleton<IModelProvider>(_provider),
            o => _dataDirectory = o.DataDirectory);

        await _host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "model",
            Name = "Recording model",
            Format = ModelFormat.Gguf,
            ProviderId = "recording",
            Path = "model.gguf",
        }, TestContext.Current.CancellationToken);

        return (_host.Services.GetRequiredService<IKnowledgeService>(), _host.Services.GetRequiredService<IRagChatClientFactory>());
    }

    private static KnowledgeBase NewBase() => new()
    {
        Id = "kb1",
        Name = "Handbook",
        EmbeddingModel = "model",
        Chunking = new ChunkingOptions { MaxTokens = 200, OverlapTokens = 0, MinTokens = 1 },
        Retrieval = new RetrievalOptions { TopK = 3, MinScore = null },
    };

    private static SourceDocument Doc(string id, string title, string text) => new(id, title)
    {
        FileName = $"{id}.md",
        ContentType = "text/markdown",
        OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text))),
    };

    [Fact]
    public async Task Retrieved_passages_are_put_in_front_of_the_model_with_numbered_sources()
    {
        var (knowledge, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday policy", "# Holiday\n\nEvery employee receives twenty five days of paid holiday."), cancellationToken: ct);

        var client = rag.Create("model", new RagOptions { KnowledgeBaseIds = ["kb1"] });
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "how much holiday")], cancellationToken: ct);

        var system = _provider.LastMessages!.First(m => m.Role == ChatRole.System).Text;
        Assert.Contains("[1] Holiday policy", system, StringComparison.Ordinal);
        Assert.Contains("twenty five days", system, StringComparison.Ordinal);

        // The model is told to cite, and told what to do when the sources do not answer.
        Assert.Contains("Cite them inline as [1]", system, StringComparison.Ordinal);
        Assert.Contains("say so rather than guessing", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Citations_come_back_attached_to_the_answer()
    {
        var (knowledge, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday policy", "# Holiday\n\nEvery employee receives twenty five days of paid holiday."), cancellationToken: ct);

        var client = rag.Create("model", new RagOptions { KnowledgeBaseIds = ["kb1"] });
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "holiday")], cancellationToken: ct);

        var content = Assert.Single(response.Messages[^1].Contents.OfType<CitationContent>());
        var citation = Assert.Single(content.Citations);
        Assert.Equal("Holiday policy", citation.Title);
        Assert.Equal(1, citation.Ordinal);
        Assert.Equal("Holiday", citation.Section);
    }

    [Fact]
    public async Task Retrieved_passages_are_attached_only_when_they_were_asked_for()
    {
        var (knowledge, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday policy", "# Holiday\n\nEvery employee receives twenty five days of paid holiday."), cancellationToken: ct);

        var quiet = await rag.Create("model", new RagOptions { KnowledgeBaseIds = ["kb1"] })
            .GetResponseAsync([new ChatMessage(ChatRole.User, "holiday")], cancellationToken: ct);
        var verbose = await rag.Create("model", new RagOptions { KnowledgeBaseIds = ["kb1"], IncludeRetrievedPassages = true })
            .GetResponseAsync([new ChatMessage(ChatRole.User, "holiday")], cancellationToken: ct);

        // Whole passages dwarf the answer they produced, so an ordinary turn must not carry them.
        Assert.Empty(quiet.Messages[^1].Contents.OfType<RetrievedContext>());

        var passage = Assert.Single(Assert.Single(verbose.Messages[^1].Contents.OfType<RetrievedContext>()).Passages);
        Assert.Equal("Holiday policy", passage.Title);
        Assert.Equal(1, passage.Ordinal);

        // The whole chunk, not the citation's shortened snippet: the point is to see where it was cut.
        Assert.Contains("twenty five days", passage.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Citations_are_streamed_before_the_first_token()
    {
        var (knowledge, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday policy", "Every employee receives twenty five days of paid holiday."), cancellationToken: ct);

        var client = rag.Create("model", new RagOptions { KnowledgeBaseIds = ["kb1"] });
        var order = new List<string>();

        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "holiday")], cancellationToken: ct))
        {
            if (update.Contents.OfType<CitationContent>().Any())
            {
                order.Add("citations");
            }
            else if (update.Text is { Length: > 0 })
            {
                order.Add("text");
            }
        }

        // Sources arrive first so the UI can show them beside the answer as it streams.
        Assert.Equal("citations", order[0]);
        Assert.Contains("text", order);
    }

    [Fact]
    public async Task With_no_knowledge_bases_the_call_passes_straight_through()
    {
        var (_, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var client = rag.Create("model", new RagOptions());
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct);

        Assert.DoesNotContain(_provider.LastMessages!, m => m.Role == ChatRole.System);
        Assert.Empty(response.Messages[^1].Contents.OfType<CitationContent>());
    }

    [Fact]
    public async Task When_nothing_is_retrieved_the_model_is_told_not_to_answer_from_memory()
    {
        var (knowledge, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday", "holiday policy text"), cancellationToken: ct);

        // A threshold nothing can meet stands in for "the base has no answer to this question".
        var client = rag.Create("model", new RagOptions
        {
            KnowledgeBaseIds = ["kb1"],
            Retrieval = new RetrievalOptions { TopK = 3, MinScore = 2f },
        });

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "what is the capital of France")], cancellationToken: ct);

        // A confident answer with no sources is the worst thing a RAG system can produce.
        var system = _provider.LastMessages!.First(m => m.Role == ChatRole.System).Text;
        Assert.Contains("could not find an answer", system, StringComparison.Ordinal);
        Assert.Contains("Do not answer from your own knowledge", system, StringComparison.Ordinal);
        Assert.Empty(response.Messages[^1].Contents.OfType<CitationContent>());
    }

    [Fact]
    public async Task Answering_without_context_can_be_allowed_explicitly()
    {
        var (knowledge, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday", "holiday policy text"), cancellationToken: ct);

        var client = rag.Create("model", new RagOptions
        {
            KnowledgeBaseIds = ["kb1"],
            Retrieval = new RetrievalOptions { TopK = 3, MinScore = 2f },
            AnswerWithoutContext = true,
        });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "anything")], cancellationToken: ct);

        // The caller opted in, so nothing is injected and the model answers as it normally would.
        Assert.DoesNotContain(_provider.LastMessages!, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task Retrieval_failing_does_not_take_the_conversation_down()
    {
        var (_, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        // The base does not exist, so retrieval throws; the turn should still be answered.
        var client = rag.Create("model", new RagOptions { KnowledgeBaseIds = ["missing-base"] });

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Empty(response.Messages[^1].Contents.OfType<CitationContent>());
    }

    [Fact]
    public async Task Access_tags_are_carried_into_retrieval()
    {
        var (knowledge, rag) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("open", "Open", "holiday policy for everyone"), cancellationToken: ct);
        await knowledge.IngestAsync("kb1", new SourceDocument("secret", "Restricted")
        {
            FileName = "secret.md",
            ContentType = "text/markdown",
            AclTags = ["role:hr"],
            OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("holiday exceptions only HR may see"))),
        }, cancellationToken: ct);

        var client = rag.Create("model", new RagOptions { KnowledgeBaseIds = ["kb1"], CallerTags = [] });
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "holiday")], cancellationToken: ct);

        // The restricted passage must not reach the prompt, let alone the citations.
        var system = _provider.LastMessages!.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        Assert.DoesNotContain("only HR may see", system, StringComparison.Ordinal);
        Assert.DoesNotContain(response.Messages[^1].Contents.OfType<CitationContent>().SelectMany(c => c.Citations), c => c.Title == "Restricted");
    }

    [Fact]
    public void The_context_block_names_pages_and_sections()
    {
        var record = new VectorRecord("c1", "d1", new float[] { 1 }, "The text of the passage.", new Dictionary<string, string>(), []);
        var hits = new List<RetrievedChunk>
        {
            new(record, 0.9f, new Citation("d1", "Handbook", "snippet", 0.9f) { Page = 12, Ordinal = 1 }),
            new(record, 0.8f, new Citation("d2", "Policy", "snippet", 0.8f) { Section = "Expenses", Ordinal = 2 }),
        };

        var context = RagChatClient.BuildContext(hits);

        // A model asked "where does it say that?" can only answer if the location is in the prompt.
        Assert.Contains("[1] Handbook, page 12", context, StringComparison.Ordinal);
        Assert.Contains("[2] Policy, section \"Expenses\"", context, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Records what reached the model, and embeds by keyword so retrieval is predictable.</summary>
    private sealed class RecordingProvider : IModelProvider
    {
        public List<ChatMessage>? LastMessages { get; set; }

        public string Id => "recording";

        public string DisplayName => "Recording";

        public ProviderKind Kind => ProviderKind.Local;

        public IReadOnlyList<ModelFormat> SupportedFormats { get; } = [ModelFormat.Gguf];

        public bool CanLoad(ModelDescriptor model) => true;

        public ModelCapabilities GetCapabilities(ModelDescriptor model) =>
            new(ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.Embeddings, 4096, 3);

        public ValueTask<MemoryEstimate> EstimateMemoryAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MemoryEstimate(1, 0, FitVerdict.Fits));

        public ValueTask<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LoadedModel(model, Id, null, 1, options));

        public ValueTask UnloadAsync(LoadedModel model, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public IChatClient CreateChatClient(LoadedModel model) => new RecordingClient(this);

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model) => new HolidayEmbedder();
    }

    private sealed class RecordingClient(RecordingProvider owner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            owner.LastMessages = [.. messages];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "An answer citing [1].")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var word in response.Text.Split(' '))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Two dimensions: how much a text is about holiday, and how long it is.</summary>
    private sealed class HolidayEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = values.Select(text =>
            {
                var holiday = text.Contains("holiday", StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
                return new Embedding<float>(new[] { holiday, holiday == 0 ? 1f : 0.1f });
            });

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([.. embeddings]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
