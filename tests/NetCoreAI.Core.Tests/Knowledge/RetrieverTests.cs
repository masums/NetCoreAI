using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// Retrieval: what comes back, in what order, with what citation, and — the part that has to be right —
/// what a caller is not allowed to see.
/// </summary>
public class RetrieverTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";

    /// <summary>
    /// Embeds text by keyword rather than meaning, so the tests assert on retrieval behaviour instead of
    /// on how good a model is: a document mentioning "holiday" is near a query mentioning "holiday".
    /// </summary>
    private static readonly string[] Vocabulary = ["holiday", "expenses", "security", "payroll"];

    private async Task<(IKnowledgeService Knowledge, IRetriever Retriever, IKnowledgeClient Client)> StartAsync()
    {
        _host = await TestHost.StartAsync(
            b => b.Services.AddSingleton<IModelProvider>(new KeywordProvider()),
            o => _dataDirectory = o.DataDirectory);

        await _host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "embedder",
            Name = "Keyword embedder",
            Format = ModelFormat.Gguf,
            ProviderId = "keyword",
            Path = "embedder.gguf",
        }, TestContext.Current.CancellationToken);

        return (
            _host.Services.GetRequiredService<IKnowledgeService>(),
            _host.Services.GetRequiredService<IRetriever>(),
            _host.Services.GetRequiredService<IKnowledgeClient>());
    }

    private static KnowledgeBase NewBase(string id = "kb1") => new()
    {
        Id = id,
        Name = "Handbook",
        EmbeddingModel = "embedder",
        Chunking = new ChunkingOptions { MaxTokens = 200, OverlapTokens = 0, MinTokens = 1 },
        Retrieval = new RetrievalOptions { TopK = 5, MinScore = null },
    };

    private static SourceDocument Doc(string id, string title, string text, IReadOnlyList<string>? acl = null) => new(id, title)
    {
        FileName = $"{id}.txt",
        ContentType = "text/plain",
        AclTags = acl,
        OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text))),
    };

    [Fact]
    public async Task The_closest_passage_comes_back_first_with_its_citation()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday policy", "Every employee gets holiday, and holiday accrues monthly."), cancellationToken: ct);
        await knowledge.IngestAsync("kb1", Doc("d2", "Expenses policy", "Expenses must be claimed promptly; expenses over a limit need approval."), cancellationToken: ct);

        var hits = await retriever.SearchAsync("kb1", "how much holiday do I get", cancellationToken: ct);

        Assert.NotEmpty(hits);
        Assert.Equal("Holiday policy", hits[0].Citation.Title);
        Assert.Equal(hits[0].Chunk.DocumentId, hits[0].Citation.DocumentId);
        Assert.True(hits[0].Score > 0);

        // Citations are numbered so "[1]" in an answer lines up with the first source shown.
        Assert.Equal(1, hits[0].Citation.Ordinal);
        Assert.Equal("kb1", hits[0].Citation.KnowledgeBaseId);
    }

    [Fact]
    public async Task Results_are_ordered_by_score()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday", "holiday holiday holiday"), cancellationToken: ct);
        await knowledge.IngestAsync("kb1", Doc("d2", "Mixed", "holiday expenses security payroll"), cancellationToken: ct);

        var hits = await retriever.SearchAsync("kb1", "holiday", cancellationToken: ct);

        Assert.Equal([.. hits.Select(h => h.Score).OrderByDescending(s => s)], [.. hits.Select(h => h.Score)]);
        Assert.Equal(1, hits[0].Citation.Ordinal);
        Assert.Equal(2, hits[1].Citation.Ordinal);
    }

    [Fact]
    public async Task A_caller_only_sees_what_their_tags_allow()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("open", "Open handbook", "holiday policy for everyone"), cancellationToken: ct);
        await knowledge.IngestAsync("kb1", Doc("hr", "HR only", "holiday carry-over exceptions", acl: ["role:hr"]), cancellationToken: ct);
        await knowledge.IngestAsync("kb1", Doc("fin", "Finance only", "holiday accrual accounting", acl: ["role:finance"]), cancellationToken: ct);

        var asHr = await retriever.SearchAsync("kb1", "holiday", callerTags: ["role:hr"], cancellationToken: ct);
        var asNobody = await retriever.SearchAsync("kb1", "holiday", callerTags: [], cancellationToken: ct);
        var asSystem = await retriever.SearchAsync("kb1", "holiday", cancellationToken: ct);

        // HR sees the public document and its own, and must not see finance's.
        Assert.Contains(asHr, h => h.Citation.Title == "Open handbook");
        Assert.Contains(asHr, h => h.Citation.Title == "HR only");
        Assert.DoesNotContain(asHr, h => h.Citation.Title == "Finance only");

        // A caller with no tags sees only what is unrestricted.
        Assert.Equal(["Open handbook"], asNobody.Select(h => h.Citation.Title));

        // Null tags mean "no filtering", which is for system callers: everything is visible.
        Assert.Equal(3, asSystem.Count);
    }

    [Fact]
    public async Task A_document_with_no_tags_is_visible_to_a_filtered_caller()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        // The pipeline writes an empty tag list when nothing restricts a document; treating that as
        // "deny everyone" would hide every ordinary document from every real user.
        await knowledge.IngestAsync("kb1", Doc("open", "Unrestricted", "holiday policy"), cancellationToken: ct);

        Assert.Single(await retriever.SearchAsync("kb1", "holiday", callerTags: ["role:anything"], cancellationToken: ct));
    }

    [Fact]
    public async Task Top_k_limits_how_much_comes_back()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        for (var i = 0; i < 5; i++)
        {
            await knowledge.IngestAsync("kb1", Doc($"d{i}", $"Doc {i}", "holiday policy text"), cancellationToken: ct);
        }

        var hits = await retriever.SearchAsync("kb1", "holiday", new RetrievalOptions { TopK = 2, MinScore = null }, cancellationToken: ct);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task A_score_threshold_drops_weak_matches()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday", "holiday"), cancellationToken: ct);
        await knowledge.IngestAsync("kb1", Doc("d2", "Payroll", "payroll"), cancellationToken: ct);

        var hits = await retriever.SearchAsync("kb1", "holiday", new RetrievalOptions { TopK = 10, MinScore = 0.9f }, cancellationToken: ct);

        // Returning an unrelated passage is worse than returning nothing: the model would cite it.
        Assert.Equal(["Holiday"], hits.Select(h => h.Citation.Title));
    }

    [Fact]
    public async Task Metadata_filters_narrow_the_search()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        await knowledge.IngestAsync("kb1", Doc("en", "English", "holiday policy") with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["lang"] = "en" },
        }, cancellationToken: ct);

        await knowledge.IngestAsync("kb1", Doc("bn", "Bengali", "holiday policy") with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["lang"] = "bn" },
        }, cancellationToken: ct);

        var hits = await retriever.SearchAsync("kb1", "holiday", new RetrievalOptions
        {
            TopK = 10,
            MinScore = null,
            MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["lang"] = "bn" },
        }, cancellationToken: ct);

        Assert.Equal(["Bengali"], hits.Select(h => h.Citation.Title));
    }

    [Fact]
    public async Task Searching_an_empty_base_returns_nothing_rather_than_failing()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        // Nothing has been ingested, so there is no collection yet; that is a normal state, not an error.
        Assert.Empty(await retriever.SearchAsync("kb1", "holiday", cancellationToken: ct));
    }

    [Fact]
    public async Task Searching_a_base_that_does_not_exist_says_so()
    {
        var (_, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await retriever.SearchAsync("nope", "holiday", cancellationToken: ct));

        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_query_returns_nothing()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday", "holiday policy"), cancellationToken: ct);

        Assert.Empty(await retriever.SearchAsync("kb1", "   ", cancellationToken: ct));
    }

    [Fact]
    public async Task Several_bases_are_searched_together_and_merged_by_score()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.CreateAsync(NewBase("kb2") with { Name = "Security" }, ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday", "holiday holiday"), cancellationToken: ct);
        await knowledge.IngestAsync("kb2", Doc("d2", "Security", "security security"), cancellationToken: ct);

        var hits = await retriever.SearchManyAsync(["kb1", "kb2"], "security", new RetrievalOptions { TopK = 5, MinScore = null }, cancellationToken: ct);

        Assert.Equal(2, hits.Count);

        // The citation names which base a passage came from, so a merged answer can attribute it.
        Assert.Equal("kb2", hits[0].Citation.KnowledgeBaseId);
        Assert.Equal("Security", hits[0].Citation.Title);
    }

    [Fact]
    public async Task A_missing_base_in_a_multi_base_search_is_skipped()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        await knowledge.IngestAsync("kb1", Doc("d1", "Holiday", "holiday policy"), cancellationToken: ct);

        var hits = await retriever.SearchManyAsync(["kb1", "deleted-base"], "holiday", cancellationToken: ct);

        // One base going missing should not deny the user the answers the others have.
        Assert.Single(hits);
    }

    [Fact]
    public async Task A_citation_carries_the_page_it_came_from()
    {
        var (knowledge, retriever, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        // Markdown gives sections rather than pages; the section travels the same way a page would.
        await knowledge.IngestAsync("kb1", Doc("d1", "Handbook", "# Holiday\n\nholiday accrues monthly") with
        {
            FileName = "handbook.md",
            ContentType = "text/markdown",
        }, cancellationToken: ct);

        var hit = Assert.Single(await retriever.SearchAsync("kb1", "holiday", cancellationToken: ct));
        Assert.Equal("Holiday", hit.Citation.Section);
    }

    [Fact]
    public async Task The_knowledge_client_ingests_and_searches_without_touching_the_internals()
    {
        var (knowledge, _, client) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        // What host application code actually writes.
        await client.IngestTextAsync("kb1", "policy-1", "Holiday policy", "Everyone gets holiday every year.", cancellationToken: ct);
        var hits = await client.SearchAsync("kb1", "holiday", cancellationToken: ct);

        Assert.Equal("Holiday policy", Assert.Single(hits).Citation.Title);
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

    /// <summary>
    /// A provider whose embeddings are keyword counts. Retrieval behaviour can then be asserted exactly,
    /// without depending on a real model's judgement about what is similar to what.
    /// </summary>
    private sealed class KeywordProvider : IModelProvider
    {
        public string Id => "keyword";

        public string DisplayName => "Keyword embedder";

        public ProviderKind Kind => ProviderKind.Local;

        public IReadOnlyList<ModelFormat> SupportedFormats { get; } = [ModelFormat.Gguf];

        public bool CanLoad(ModelDescriptor model) => true;

        public ModelCapabilities GetCapabilities(ModelDescriptor model) =>
            new(ModelCapability.Embeddings, 2048, Vocabulary.Length);

        public ValueTask<MemoryEstimate> EstimateMemoryAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MemoryEstimate(1, 0, FitVerdict.Fits));

        public ValueTask<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LoadedModel(model, Id, null, 1, options));

        public ValueTask UnloadAsync(LoadedModel model, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public IChatClient CreateChatClient(LoadedModel model) => throw new NotSupportedException("This test provider only embeds.");

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model) => new KeywordEmbedder();
    }

    private sealed class KeywordEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = values.Select(text =>
            {
                var vector = new float[Vocabulary.Length];
                for (var i = 0; i < Vocabulary.Length; i++)
                {
                    var count = 0;
                    var index = 0;
                    while ((index = text.IndexOf(Vocabulary[i], index, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        count++;
                        index += Vocabulary[i].Length;
                    }

                    vector[i] = count;
                }

                // A vector of all zeros has no direction, so an unrelated text still gets one dimension.
                if (vector.All(v => v == 0))
                {
                    vector[^1] = 0.01f;
                }

                return new Embedding<float>(vector);
            });

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([.. embeddings]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
