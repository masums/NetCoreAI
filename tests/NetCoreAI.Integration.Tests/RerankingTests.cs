using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Re-reading the candidates and putting them in a better order.
/// </summary>
/// <remarks>
/// A stand-in reranker rather than a real cross-encoder, because what is worth testing here is the seam:
/// that a wider net is cast when something is going to re-read it, that the reranker decides the final
/// order, that it cuts to the requested size, and that a broken one costs quality rather than the answer.
/// Whether <c>bge-reranker-base</c> ranks well is the model's business, and is checked by a test gated
/// behind a model download.
/// </remarks>
public sealed class RerankingTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";
    private static readonly StubReranker Stub = new();

    public async ValueTask InitializeAsync()
    {
        Stub.Reset();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        builder.Services.AddSingleton<IReranker>(Stub);

        // Retrieval has to embed the query. A fixed vector is enough: what is being tested is the order
        // things come back in, not whether the embedding is any good.
        builder.Services.AddSingleton<IChatClientFactory, FakeEmbeddings>();

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RetrievedChunk Chunk(string id, float score) =>
        new(new VectorRecord(id, "doc1", ReadOnlyMemory<float>.Empty, id, new Dictionary<string, string>(), []),
            score,
            new Citation("doc1", "Doc", id, score));

    // ---------- the ordering ----------

    [Fact]
    public async Task The_reranker_decides_the_final_order()
    {
        Stub.Order = ["third", "first", "second"];

        var reranked = await Stub.RerankAsync("q", [Chunk("first", 0.9f), Chunk("second", 0.8f), Chunk("third", 0.1f)], 3, Ct);

        // The retrieval score put "third" last. A model given ten passages leans on the first two, so this
        // reordering is what decides the answer.
        Assert.Equal(["third", "first", "second"], reranked.Select(r => r.Chunk.Id));
    }

    [Fact]
    public async Task It_cuts_to_the_size_asked_for()
    {
        Stub.Order = ["c", "a", "b"];

        Assert.Equal(2, (await Stub.RerankAsync("q", [Chunk("a", 1f), Chunk("b", 1f), Chunk("c", 1f)], 2, Ct)).Count);
    }

    [Fact]
    public async Task It_returns_fewer_when_given_fewer_rather_than_padding()
    {
        Assert.Single(await Stub.RerankAsync("q", [Chunk("a", 1f)], 5, Ct));
    }

    // ---------- in the retrieval path ----------

    /// <summary>
    /// A knowledge base with three indexed chunks, so retrieval actually produces candidates.
    /// </summary>
    /// <remarks>
    /// Without this the base is empty, retrieval short-circuits before the reranker, and a test asserting
    /// on the reranker proves only that nothing ran.
    /// </remarks>
    private async Task IndexAsync()
    {
        await _app.Services.GetRequiredService<IKnowledgeService>()
            .CreateAsync(new KnowledgeBase { Id = "docs", Name = "Docs", EmbeddingModel = "fake" }, Ct);

        var store = _app.Services.GetRequiredService<IEnumerable<IVectorStore>>().First();
        var knowledgeBase = (await _app.Services.GetRequiredService<IKnowledgeService>().GetAsync("docs", Ct))!;

        await store.EnsureCollectionAsync(knowledgeBase.Collection, 2, VectorDistance.Cosine, Ct);
        await store.UpsertAsync(
            knowledgeBase.Collection,
            [
                new VectorRecord("first", "doc1", new float[] { 1f, 0f }, "first passage", new Dictionary<string, string>(), []),
                new VectorRecord("second", "doc1", new float[] { 0.9f, 0.1f }, "second passage", new Dictionary<string, string>(), []),
                new VectorRecord("third", "doc1", new float[] { 0.8f, 0.2f }, "third passage", new Dictionary<string, string>(), []),
            ],
            Ct);
    }

    [Fact]
    public async Task The_reranker_reorders_what_retrieval_found()
    {
        await IndexAsync();
        Stub.Order = ["third", "first", "second"];

        var hits = await _app.Services.GetRequiredService<IRetriever>()
            .SearchAsync("docs", "anything", new RetrievalOptions { TopK = 3, Mode = RetrievalMode.Vector }, null, Ct);

        // Retrieval ranked "third" last by similarity. The reranker's opinion is the one that reaches the
        // model, which is the whole point of having it.
        Assert.Equal(1, Stub.Calls);
        Assert.Equal("third", hits[0].Chunk.Id);
    }

    [Fact]
    public async Task More_candidates_are_fetched_than_the_answer_needs()
    {
        await IndexAsync();

        await _app.Services.GetRequiredService<IRetriever>()
            .SearchAsync("docs", "anything", new RetrievalOptions { TopK = 1, RerankCandidates = 30, Mode = RetrievalMode.Vector }, null, Ct);

        // Asked for one and offered three. The passages worth promoting are the ones the first stage
        // ranked below the cut, so a reranker handed only the top result has nothing to do.
        Assert.Equal(3, Stub.LastOffered);
    }

    [Fact]
    public async Task Turning_reranking_off_leaves_the_retrieval_order_alone()
    {
        await IndexAsync();
        Stub.Order = ["third", "first", "second"];

        var hits = await _app.Services.GetRequiredService<IRetriever>()
            .SearchAsync("docs", "anything", new RetrievalOptions { TopK = 3, Rerank = false, Mode = RetrievalMode.Vector }, null, Ct);

        Assert.Equal(0, Stub.Calls);
        Assert.Equal("first", hits[0].Chunk.Id);
    }

    // ---------- when it goes wrong ----------

    [Fact]
    public async Task A_reranker_that_throws_costs_quality_rather_than_the_answer()
    {
        await IndexAsync();
        Stub.Throw = true;

        var hits = await _app.Services.GetRequiredService<IRetriever>()
            .SearchAsync("docs", "anything", new RetrievalOptions { TopK = 2, Mode = RetrievalMode.Vector }, null, Ct);

        // The candidates were already a reasonable answer. Losing them because the reordering failed would
        // turn a quality feature into an availability one.
        Assert.Equal(2, hits.Count);
        Assert.Equal("first", hits[0].Chunk.Id);
    }

    [Fact]
    public async Task An_empty_query_or_no_candidates_is_left_alone()
    {
        Assert.Empty(await Stub.RerankAsync("q", [], 5, Ct));
        Assert.Single(await Stub.RerankAsync("   ", [Chunk("a", 1f)], 5, Ct));
    }

    /// <summary>A factory whose embeddings are a fixed vector, so no model has to be downloaded.</summary>
    private sealed class FakeEmbeddings : IChatClientFactory
    {
        public Microsoft.Extensions.AI.IChatClient Get(string idOrAlias = ModelAlias.Default) =>
            throw new NotSupportedException("These tests do not chat.");

        public bool TryGet(string idOrAlias, out Microsoft.Extensions.AI.IChatClient? client)
        {
            client = null;
            return false;
        }

        public Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> GetEmbeddingGenerator(
            string idOrAlias = ModelAlias.Embed) => new Generator();

        private sealed class Generator : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
        {
            public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
                IEnumerable<string> values,
                Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>(
                    [.. values.Select(_ => new Microsoft.Extensions.AI.Embedding<float>(new float[] { 1f, 0f }))]));

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>A reranker with an opinion the test dictates, so the seam can be checked without a model.</summary>
    private sealed class StubReranker : IReranker
    {
        public string Id => "stub";

        public IReadOnlyList<string> Order { get; set; } = [];

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public int LastOffered { get; private set; }

        public void Reset()
        {
            Order = [];
            Throw = false;
            Calls = 0;
            LastOffered = 0;
        }

        public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
            string query,
            IReadOnlyList<RetrievedChunk> chunks,
            int topN,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOffered = chunks.Count;

            if (Throw)
            {
                throw new InvalidOperationException("the reranker is unavailable");
            }

            if (chunks.Count == 0 || string.IsNullOrWhiteSpace(query))
            {
                return Task.FromResult(chunks);
            }

            var ranked = chunks
                .OrderBy(c => Order.Count == 0 ? 0 : Math.Max(0, Order.ToList().IndexOf(c.Chunk.Id)))
                .Take(Math.Max(1, topN))
                .ToList();

            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(ranked);
        }
    }
}
