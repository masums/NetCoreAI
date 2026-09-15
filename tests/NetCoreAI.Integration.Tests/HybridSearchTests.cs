using NetCoreAI.Knowledge;
using NetCoreAI.VectorStores.Sqlite;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Searching by words as well as by meaning.
/// </summary>
/// <remarks>
/// The two fail differently, and that is the whole argument for having both. A vector search misses
/// <c>ERR-4021</c> because nothing else means the same thing as an error code; a keyword search misses
/// "the login screen hangs" when the document says "authentication times out".
/// </remarks>
public sealed class HybridSearchTests : IAsyncLifetime
{
    private string _dir = "";
    private SqliteVectorStore _store = default!;
    private const string Collection = "kb_test";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _store = new SqliteVectorStore(Path.Combine(_dir, "vectors.db"));

        await _store.EnsureCollectionAsync(Collection, 3, VectorDistance.Cosine, Ct);
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task AddAsync(string id, string text, float[]? embedding = null, IReadOnlyList<string>? acl = null) =>
        _store.UpsertAsync(
            Collection,
            [new VectorRecord(id, "doc1", embedding ?? [1f, 0f, 0f], text, new Dictionary<string, string>(), acl ?? [])],
            Ct);

    // ---------- what keywords are for ----------

    [Fact]
    public async Task An_exact_token_is_found()
    {
        await AddAsync("a", "The authentication service times out under load.");
        await AddAsync("b", "Error ERR-4021 means the upstream refused the request.");

        var hits = await _store.SearchKeywordAsync(Collection, "ERR-4021", 5, cancellationToken: Ct);

        // The case vectors are worst at: nothing else means the same thing as an error code.
        Assert.Equal("b", Assert.Single(hits).Record.Id);
    }

    [Fact]
    public async Task A_query_with_punctuation_does_not_break_the_search()
    {
        await AddAsync("a", "Part number AB-1234/X is discontinued.");

        // FTS5 reads - and * as operators, so an unquoted token like this is a syntax error rather than a
        // search. A person typing a part number should get an answer, not an exception.
        var hits = await _store.SearchKeywordAsync(Collection, "AB-1234/X", 5, cancellationToken: Ct);

        Assert.Single(hits);
    }

    [Fact]
    public async Task Words_are_matched_by_stem()
    {
        await AddAsync("a", "The service is restarting automatically.");

        Assert.Single(await _store.SearchKeywordAsync(Collection, "restart", 5, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_query_matching_nothing_returns_nothing_rather_than_failing()
    {
        await AddAsync("a", "Something unrelated.");

        Assert.Empty(await _store.SearchKeywordAsync(Collection, "xylophone", 5, cancellationToken: Ct));
        Assert.Empty(await _store.SearchKeywordAsync(Collection, "   ", 5, cancellationToken: Ct));
    }

    [Fact]
    public async Task Higher_is_better_like_every_other_score_here()
    {
        await AddAsync("a", "refunds refunds refunds refunds");
        await AddAsync("b", "a passing mention of refunds among other things entirely");

        var hits = await _store.SearchKeywordAsync(Collection, "refunds", 5, cancellationToken: Ct);

        // bm25() is negative and better when more negative. Nothing downstream should have to know that.
        Assert.Equal("a", hits[0].Record.Id);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    // ---------- the index keeping up ----------

    [Fact]
    public async Task Changed_text_is_searchable_and_the_old_text_is_not()
    {
        await AddAsync("a", "the original wording");
        await AddAsync("a", "the replacement wording");

        Assert.Single(await _store.SearchKeywordAsync(Collection, "replacement", 5, cancellationToken: Ct));

        // An index maintained by whichever code path remembers is an index that is wrong after the first
        // one that forgets. These are database triggers for that reason.
        Assert.Empty(await _store.SearchKeywordAsync(Collection, "original", 5, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_deleted_document_stops_being_findable()
    {
        await AddAsync("a", "findable text");
        await _store.DeleteByDocumentAsync(Collection, "doc1", Ct);

        Assert.Empty(await _store.SearchKeywordAsync(Collection, "findable", 5, cancellationToken: Ct));
    }

    // ---------- the filters still apply ----------

    [Fact]
    public async Task A_restricted_chunk_is_not_returned_to_somebody_without_the_tag()
    {
        await AddAsync("secret", "the quarterly numbers", acl: ["role:finance"]);

        // A keyword index that ignored access tags would be a way to read a restricted passage by guessing
        // a word in it.
        Assert.Empty(await _store.SearchKeywordAsync(Collection, "quarterly", 5, new VectorFilter { CallerTags = [] }, Ct));
        Assert.Single(await _store.SearchKeywordAsync(Collection, "quarterly", 5, new VectorFilter { CallerTags = ["role:finance"] }, Ct));
    }

    // ---------- fusion ----------

    [Fact]
    public void Fusion_prefers_what_both_searches_liked_over_what_one_loved()
    {
        // The reason for fusing on rank rather than picking a winner. "both" is second in each list and
        // first in neither; "vector-only" is first in one and absent from the other. The passage two
        // different kinds of search agree about is the better answer, and rank fusion says so.
        var vectors = Results("vector-only", "both", "filler");
        var keywords = Results("keyword-only", "both", "filler");

        var fused = Retriever.FuseForTests(vectors, keywords, 3);

        Assert.Equal("both", fused[0].Record.Id);
    }

    [Fact]
    public void Fusion_keeps_a_passage_only_one_search_found()
    {
        // The other half of the argument: a keyword hit on an error code that no embedding would surface
        // must still reach the answer, further down.
        var fused = Retriever.FuseForTests(Results("a", "b"), Results("err-code"), 5);

        Assert.Contains(fused, f => f.Record.Id == "err-code");
    }

    [Fact]
    public void Fusion_returns_the_record_that_has_the_embedding()
    {
        // The keyword leg leaves the embedding empty. A caller asking for a chunk back should get the
        // whole one, whichever search happened to rank it higher.
        var withEmbedding = new VectorSearchResult(
            new VectorRecord("a", "doc1", new float[] { 1f, 0f, 0f }, "text", new Dictionary<string, string>(), []), 0.9f);
        var without = new VectorSearchResult(
            new VectorRecord("a", "doc1", ReadOnlyMemory<float>.Empty, "text", new Dictionary<string, string>(), []), 0.5f);

        Assert.Equal(3, Retriever.FuseForTests([withEmbedding], [without], 5)[0].Record.Embedding.Length);
        Assert.Equal(3, Retriever.FuseForTests([without], [withEmbedding], 5)[0].Record.Embedding.Length);
    }

    private static List<VectorSearchResult> Results(params string[] ids) =>
        [.. ids.Select((id, i) => new VectorSearchResult(
            new VectorRecord(id, "doc1", ReadOnlyMemory<float>.Empty, id, new Dictionary<string, string>(), []),
            1f - (i * 0.1f)))];

    [Fact]
    public async Task Only_the_collection_asked_for_is_searched()
    {
        await _store.EnsureCollectionAsync("kb_other", 3, VectorDistance.Cosine, Ct);
        await _store.UpsertAsync(
            "kb_other",
            [new VectorRecord("x", "doc9", new float[] { 1f, 0f, 0f }, "shared word", new Dictionary<string, string>(), [])],
            Ct);

        await AddAsync("a", "shared word");

        Assert.Equal("a", Assert.Single(await _store.SearchKeywordAsync(Collection, "shared", 5, cancellationToken: Ct)).Record.Id);
    }
}
