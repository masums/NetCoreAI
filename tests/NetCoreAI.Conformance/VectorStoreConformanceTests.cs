using Xunit;

namespace NetCoreAI.Conformance;

/// <summary>Contract every <see cref="IVectorStore"/> must satisfy.</summary>
public abstract class VectorStoreConformanceTests : IAsyncLifetime
{
    protected IVectorStore Store { get; private set; } = default!;

    protected abstract Task<IVectorStore> CreateStoreAsync();

    public async ValueTask InitializeAsync() => Store = await CreateStoreAsync();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static VectorRecord Rec(string id, string doc, float[] v, string? tag = null, params (string, string)[] meta)
        => new(id, doc, v, $"text {id}", meta.ToDictionary(m => m.Item1, m => m.Item2), tag is null ? ["*"] : [tag]);

    [Fact]
    public async Task Search_returns_nearest_by_cosine_in_descending_order()
    {
        await Store.EnsureCollectionAsync("kb", 3);
        await Store.UpsertAsync("kb", [Rec("a", "d1", [1, 0, 0]), Rec("b", "d1", [0, 1, 0]), Rec("c", "d2", [0.9f, 0.1f, 0])]);
        var hits = await Store.SearchAsync("kb", new float[] { 1, 0, 0 }, 2);
        Assert.Equal(["a", "c"], hits.Select(h => h.Record.Id));
        Assert.True(hits[0].Score >= hits[1].Score);
        Assert.Equal(1f, hits[0].Score, 0.001f);
    }

    [Fact]
    public async Task Upsert_replaces_existing_record()
    {
        await Store.EnsureCollectionAsync("kb", 2);
        await Store.UpsertAsync("kb", [Rec("a", "d1", [1, 0])]);
        await Store.UpsertAsync("kb", [Rec("a", "d1", [0, 1])]);
        Assert.Equal(1, await Store.CountAsync("kb"));
        var hit = Assert.Single(await Store.SearchAsync("kb", new float[] { 0, 1 }, 1));
        Assert.Equal(1f, hit.Score, 0.001f);
    }

    [Fact]
    public async Task Delete_by_document_removes_only_that_document()
    {
        await Store.EnsureCollectionAsync("kb", 2);
        await Store.UpsertAsync("kb", [Rec("a", "d1", [1, 0]), Rec("b", "d2", [0, 1])]);
        await Store.DeleteByDocumentAsync("kb", "d1");
        Assert.Equal(1, await Store.CountAsync("kb"));
        Assert.Equal("b", (await Store.SearchAsync("kb", new float[] { 1, 0 }, 5))[0].Record.Id);
    }

    [Fact]
    public async Task Metadata_filter_and_min_score_apply()
    {
        await Store.EnsureCollectionAsync("kb", 2);
        await Store.UpsertAsync("kb", [Rec("a", "d1", [1, 0], null, ("lang", "en")), Rec("b", "d1", [1, 0], null, ("lang", "bn")), Rec("c", "d1", [0, 1], null, ("lang", "en"))]);
        var hits = await Store.SearchAsync("kb", new float[] { 1, 0 }, 10, new VectorFilter { MetadataEquals = new Dictionary<string, string> { ["lang"] = "en" }, MinScore = 0.5f });
        Assert.Equal(["a"], hits.Select(h => h.Record.Id));
    }

    [Fact]
    public async Task Acl_filter_respects_public_and_caller_tags()
    {
        await Store.EnsureCollectionAsync("kb", 2);
        await Store.UpsertAsync("kb", [Rec("pub", "d1", [1, 0]), Rec("hr", "d1", [1, 0], "role:hr"), Rec("fin", "d1", [1, 0], "role:finance")]);
        var asHr = await Store.SearchAsync("kb", new float[] { 1, 0 }, 10, new VectorFilter { CallerTags = ["role:hr"] });
        Assert.Equal(["hr", "pub"], asHr.Select(h => h.Record.Id).OrderBy(x => x));
        var anonymous = await Store.SearchAsync("kb", new float[] { 1, 0 }, 10, new VectorFilter { CallerTags = [] });
        Assert.Equal(["pub"], anonymous.Select(h => h.Record.Id));
        var admin = await Store.SearchAsync("kb", new float[] { 1, 0 }, 10, null);
        Assert.Equal(3, admin.Count);
    }

    [Fact]
    public async Task A_record_with_no_tags_at_all_is_public()
    {
        await Store.EnsureCollectionAsync("kb", 2);

        // This is what the ingestion pipeline writes when neither the base nor the document restricts
        // access. Treating an empty tag list as "deny everyone" would hide every unrestricted document
        // from every filtered caller, which is the opposite of what it means.
        await Store.UpsertAsync("kb", [new VectorRecord("untagged", "d1", new float[] { 1, 0 }, "text", new Dictionary<string, string>(), [])]);

        var filtered = await Store.SearchAsync("kb", new float[] { 1, 0 }, 10, new VectorFilter { CallerTags = ["role:hr"] });
        var anonymous = await Store.SearchAsync("kb", new float[] { 1, 0 }, 10, new VectorFilter { CallerTags = [] });

        Assert.Equal(["untagged"], filtered.Select(h => h.Record.Id));
        Assert.Equal(["untagged"], anonymous.Select(h => h.Record.Id));
    }

    [Fact]
    public async Task Dimension_mismatch_is_rejected()
    {
        await Store.EnsureCollectionAsync("kb", 3);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.UpsertAsync("kb", [Rec("a", "d1", [1, 0])]));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SearchAsync("kb", new float[] { 1, 0 }, 1));
    }

    [Fact]
    public async Task Collections_can_be_listed_and_deleted()
    {
        await Store.EnsureCollectionAsync("one", 2);
        await Store.EnsureCollectionAsync("two", 2);
        await Store.UpsertAsync("one", [Rec("a", "d", [1, 0])]);
        Assert.Contains("one", await Store.ListCollectionsAsync());
        await Store.DeleteCollectionAsync("one");
        Assert.DoesNotContain("one", await Store.ListCollectionsAsync());
        Assert.Contains("two", await Store.ListCollectionsAsync());
    }
}
