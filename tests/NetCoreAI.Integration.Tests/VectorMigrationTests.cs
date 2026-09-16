using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Knowledge;
using NetCoreAI.VectorStores.Sqlite;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Moving indexed vectors from one store to another.
/// </summary>
/// <remarks>
/// Two SQLite stores standing in for two different databases. What is being tested is the migrator's
/// behaviour — batching, refusing to merge, leaving the source alone — none of which is about which
/// databases are at either end.
/// </remarks>
public sealed class VectorMigrationTests : IAsyncLifetime
{
    private string _dir = "";
    private NamedStore _source = default!;
    private NamedStore _target = default!;
    private VectorStoreMigrator _migrator = default!;
    private const string Collection = "kb_docs";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _source = new NamedStore(new SqliteVectorStore(Path.Combine(_dir, "from.db")), "from");
        _target = new NamedStore(new SqliteVectorStore(Path.Combine(_dir, "to.db")), "to");
        _migrator = new VectorStoreMigrator([_source, _target], NullLogger<VectorStoreMigrator>.Instance);

        await _source.EnsureCollectionAsync(Collection, 2, VectorDistance.Cosine, Ct);
    }

    public ValueTask DisposeAsync()
    {
        _source.Dispose();
        _target.Dispose();
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

    private Task FillAsync(int count, IVectorStore? store = null) =>
        (store ?? _source).UpsertAsync(
            Collection,
            [.. Enumerable.Range(0, count).Select(i => new VectorRecord(
                $"c{i:D5}",
                $"doc{i % 3}",
                new float[] { i, 1f },
                $"chunk {i}",
                new Dictionary<string, string> { ["page"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                i % 2 == 0 ? ["role:finance"] : []))],
            Ct);

    // ---------- copying ----------

    [Fact]
    public async Task Everything_is_copied_across()
    {
        await FillAsync(10);

        var result = await _migrator.MigrateAsync("from", "to", dryRun: false, cancellationToken: Ct);

        Assert.Equal(1, result.Collections);
        Assert.Equal(10, result.Chunks);
        Assert.Equal(10, await _target.CountAsync(Collection, Ct));
    }

    [Fact]
    public async Task More_chunks_than_one_batch_are_all_copied()
    {
        // The migrator reads and writes 500 at a time. A test under that size would never exercise the
        // paging, which is where a copy of a real knowledge base spends all of its time.
        await FillAsync(1200);

        var result = await _migrator.MigrateAsync("from", "to", dryRun: false, cancellationToken: Ct);

        Assert.Equal(1200, result.Chunks);
        Assert.Equal(1200, await _target.CountAsync(Collection, Ct));
    }

    [Fact]
    public async Task A_chunk_arrives_whole()
    {
        await FillAsync(3);

        await _migrator.MigrateAsync("from", "to", dryRun: false, cancellationToken: Ct);

        // Chunk 0 is stored as [0, 1], so this query picks it out rather than whichever happened to be
        // nearest to something vaguer.
        var hit = Assert.Single(
            await _target.SearchAsync(Collection, new float[] { 0f, 1f }, 1, new VectorFilter { CallerTags = ["role:finance"] }, Ct));

        // Text, metadata, access tags and the vector itself. A migration that dropped the tags would
        // quietly publish restricted passages to everybody in the new store.
        Assert.Equal("doc0", hit.Record.DocumentId);
        Assert.Equal("chunk 0", hit.Record.Text);
        Assert.Equal("0", hit.Record.Metadata["page"]);
        Assert.Equal(["role:finance"], hit.Record.AclTags);
        Assert.Equal(2, hit.Record.Embedding.Length);
    }

    [Fact]
    public async Task The_vectors_are_copied_rather_than_recomputed()
    {
        await _source.UpsertAsync(
            Collection,
            [new VectorRecord("c1", "doc1", new float[] { 0.25f, 0.75f }, "text", new Dictionary<string, string>(), [])],
            Ct);

        await _migrator.MigrateAsync("from", "to", dryRun: false, cancellationToken: Ct);

        // Re-embedding would cost a call per chunk and, worse, produce different numbers if the model has
        // moved on since — turning a change of database into a silent change of what the base retrieves.
        var hit = Assert.Single(await _target.SearchAsync(Collection, new float[] { 0.25f, 0.75f }, 1, null, Ct));
        Assert.Equal([0.25f, 0.75f], hit.Record.Embedding.ToArray());
    }

    // ---------- being careful ----------

    [Fact]
    public async Task A_dry_run_reports_what_would_happen_and_writes_nothing()
    {
        await FillAsync(5);

        var result = await _migrator.MigrateAsync("from", "to", cancellationToken: Ct);

        Assert.True(result.DryRun);
        Assert.Equal(5, result.Chunks);
        Assert.Empty(await _target.ListCollectionsAsync(Ct));
    }

    [Fact]
    public async Task The_source_is_left_alone()
    {
        await FillAsync(5);

        await _migrator.MigrateAsync("from", "to", dryRun: false, cancellationToken: Ct);

        // A migration that emptied the old store as it went would leave a host with no way back from a
        // half-finished one.
        Assert.Equal(5, await _source.CountAsync(Collection, Ct));
    }

    [Fact]
    public async Task A_collection_the_target_already_has_is_skipped_rather_than_merged()
    {
        await FillAsync(5);
        await _target.EnsureCollectionAsync(Collection, 2, VectorDistance.Cosine, Ct);
        await FillAsync(2, _target);

        var result = await _migrator.MigrateAsync("from", "to", dryRun: false, cancellationToken: Ct);

        // Two stores holding overlapping chunk ids from different indexing runs produce a collection that
        // is neither, and nobody would know which.
        Assert.Equal(0, result.Collections);
        Assert.Equal(Collection, Assert.Single(result.Skipped));
        Assert.Equal(2, await _target.CountAsync(Collection, Ct));
    }

    [Fact]
    public async Task Replacing_is_possible_when_asked_for()
    {
        await FillAsync(5);
        await _target.EnsureCollectionAsync(Collection, 2, VectorDistance.Cosine, Ct);
        await FillAsync(2, _target);

        await _migrator.MigrateAsync("from", "to", replace: true, dryRun: false, cancellationToken: Ct);

        // Replaced, not merged: the old contents go before the new ones arrive.
        Assert.Equal(5, await _target.CountAsync(Collection, Ct));
    }

    [Fact]
    public async Task Only_the_collections_asked_for_are_copied()
    {
        await FillAsync(3);
        await _source.EnsureCollectionAsync("kb_other", 2, VectorDistance.Cosine, Ct);
        await _source.UpsertAsync(
            "kb_other",
            [new VectorRecord("x", "doc9", new float[] { 1f, 1f }, "other", new Dictionary<string, string>(), [])],
            Ct);

        var result = await _migrator.MigrateAsync("from", "to", [Collection], dryRun: false, cancellationToken: Ct);

        Assert.Equal(1, result.Collections);
        Assert.Equal([Collection], await _target.ListCollectionsAsync(Ct));
    }

    // ---------- being told what is wrong ----------

    [Fact]
    public async Task A_store_that_does_not_exist_is_named_along_with_the_ones_that_do()
    {
        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            _migrator.MigrateAsync("from", "nowhere", cancellationToken: Ct));

        Assert.Contains("nowhere", error.Message, StringComparison.Ordinal);
        Assert.Contains("from", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrating_a_store_into_itself_is_refused()
    {
        await Assert.ThrowsAsync<NetCoreAIException>(() =>
            _migrator.MigrateAsync("from", "from", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_source_that_cannot_list_its_chunks_says_so_rather_than_copying_nothing()
    {
        var migrator = new VectorStoreMigrator([new OpaqueStore(), _target], NullLogger<VectorStoreMigrator>.Instance);

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            migrator.MigrateAsync("opaque", "to", [Collection], dryRun: false, cancellationToken: Ct));

        // Reporting success having copied nothing is the worst possible outcome for a migration.
        Assert.Contains("Re-index", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A store with an id of its own, so two SQLite files can stand in for two databases.</summary>
    private sealed class NamedStore(SqliteVectorStore inner, string id) : IVectorStore, IVectorEnumerable, IDisposable
    {
        public string Id => id;

        public Task EnsureCollectionAsync(string collection, int dimensions, VectorDistance distance = VectorDistance.Cosine, CancellationToken ct = default) =>
            inner.EnsureCollectionAsync(collection, dimensions, distance, ct);

        public Task DeleteCollectionAsync(string collection, CancellationToken ct = default) => inner.DeleteCollectionAsync(collection, ct);

        public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default) => inner.ListCollectionsAsync(ct);

        public Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct = default) =>
            inner.UpsertAsync(collection, records, ct);

        public Task DeleteByDocumentAsync(string collection, string documentId, CancellationToken ct = default) =>
            inner.DeleteByDocumentAsync(collection, documentId, ct);

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string collection, ReadOnlyMemory<float> query, int topK, VectorFilter? filter = null, CancellationToken ct = default) =>
            inner.SearchAsync(collection, query, topK, filter, ct);

        public Task<long> CountAsync(string collection, CancellationToken ct = default) => inner.CountAsync(collection, ct);

        public IAsyncEnumerable<VectorRecord> ReadAllAsync(string collection, CancellationToken ct = default) =>
            inner.ReadAllAsync(collection, ct);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>A store that cannot hand its chunks back, which is allowed and has to be said.</summary>
    private sealed class OpaqueStore : IVectorStore
    {
        public string Id => "opaque";

        public Task EnsureCollectionAsync(string collection, int dimensions, VectorDistance distance = VectorDistance.Cosine, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteCollectionAsync(string collection, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([Collection]);

        public Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteByDocumentAsync(string collection, string documentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string collection, ReadOnlyMemory<float> query, int topK, VectorFilter? filter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);

        public Task<long> CountAsync(string collection, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
