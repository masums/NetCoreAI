using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// Extract → chunk → embed → store, end to end over a fake extractor and the fake provider's embeddings.
/// </summary>
public class IngestionPipelineTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";

    private async Task<(IIngestionPipeline Pipeline, IMetadataStore Store, IVectorStore Vectors)> StartAsync()
    {
        _host = await TestHost.StartAsync(
            b =>
            {
                b.Services.AddSingleton<IModelProvider>(new FakeProvider("fake", ProviderKind.Local, ModelFormat.Gguf));
                b.Services.AddSingleton<IDocumentExtractor, TextExtractor>();
            },
            o => _dataDirectory = o.DataDirectory);

        await RegisterEmbeddingModelAsync();
        return (
            _host.Services.GetRequiredService<IIngestionPipeline>(),
            _host.Services.GetRequiredService<IMetadataStore>(),
            _host.Services.GetRequiredService<IVectorStore>());
    }

    private async Task RegisterEmbeddingModelAsync() =>
        await _host!.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "embedder",
            Name = "Embedder",
            Format = ModelFormat.Gguf,
            ProviderId = "fake",
            Path = "embedder.gguf",
        }, TestContext.Current.CancellationToken);

    private static KnowledgeBase Base(ChunkingOptions? chunking = null) => new()
    {
        Id = "kb1",
        Name = "Handbook",
        EmbeddingModel = "embedder",
        Chunking = chunking ?? new ChunkingOptions { MaxTokens = 60, OverlapTokens = 0, MinTokens = 1 },
    };

    private static SourceDocument Doc(string id, string text, string title = "Doc", IReadOnlyList<string>? acl = null) => new(id, title)
    {
        FileName = $"{id}.txt",
        ContentType = "text/plain",
        OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text))),
        AclTags = acl,
    };

    private static string Prose(int sentences) =>
        string.Join(' ', Enumerable.Range(1, sentences).Select(i => $"Sentence {i} about the policy and its many details."));

    [Fact]
    public async Task A_document_is_chunked_embedded_stored_and_recorded()
    {
        var (pipeline, store, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = Base();

        var result = await pipeline.IngestAsync(kb, Doc("d1", Prose(20), "Employee handbook"), cancellationToken: ct);

        Assert.False(result.Skipped);
        Assert.True(result.ChunkCount > 1, "long text should produce several chunks");
        Assert.Equal("Employee handbook", result.Document.Title);
        Assert.NotNull(result.Document.ContentHash);

        // The row and the vectors agree on how many chunks exist.
        Assert.Equal(result.ChunkCount, await vectors.CountAsync(kb.Collection, ct));
        var stored = await store.Knowledge.GetDocumentAsync(result.Document.Id, ct);
        Assert.Equal(result.ChunkCount, stored!.ChunkCount);
    }

    [Fact]
    public async Task Re_ingesting_unchanged_content_does_no_work()
    {
        var (pipeline, _, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = Base();
        var document = Doc("d1", Prose(10));

        var first = await pipeline.IngestAsync(kb, document, cancellationToken: ct);
        var second = await pipeline.IngestAsync(kb, document, cancellationToken: ct);

        // Same id, same content hash: nothing to embed, which is what makes a nightly sync cheap.
        Assert.False(first.Skipped);
        Assert.True(second.Skipped);
        Assert.Equal(first.ChunkCount, await vectors.CountAsync(kb.Collection, ct));
    }

    [Fact]
    public async Task Changed_content_replaces_the_old_chunks_rather_than_adding_to_them()
    {
        var (pipeline, _, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = Base();

        await pipeline.IngestAsync(kb, Doc("d1", Prose(20)), cancellationToken: ct);
        var countBefore = await vectors.CountAsync(kb.Collection, ct);

        var updated = await pipeline.IngestAsync(kb, Doc("d1", "A single short paragraph replaces the whole document."), cancellationToken: ct);

        // A paragraph removed from a document must stop being retrievable.
        Assert.False(updated.Skipped);
        Assert.True(countBefore > updated.ChunkCount);
        Assert.Equal(updated.ChunkCount, await vectors.CountAsync(kb.Collection, ct));
    }

    [Fact]
    public async Task Page_and_section_are_stored_so_a_citation_can_name_them()
    {
        var (pipeline, _, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = Base();

        // The fake extractor turns "--- page N ---" markers into paged sections.
        await pipeline.IngestAsync(kb, Doc("d1", "--- page 1 ---\nThe introduction explains the policy in general terms.\n--- page 4 ---\nThe appendix lists the exceptions in detail."), cancellationToken: ct);

        var all = await vectors.SearchAsync(kb.Collection, new float[] { 1, 0, 0 }, 10, cancellationToken: ct);

        Assert.Contains(all, r => r.Record.Metadata.GetValueOrDefault("page") == "1");
        Assert.Contains(all, r => r.Record.Metadata.GetValueOrDefault("page") == "4");
        Assert.All(all, r => Assert.Equal("Doc", r.Record.Metadata["title"]));
    }

    [Fact]
    public async Task Acl_tags_from_the_base_and_the_document_are_combined()
    {
        var (pipeline, _, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = Base() with { DefaultAclTags = ["tenant:acme"] };

        await pipeline.IngestAsync(kb, Doc("d1", Prose(5), acl: ["role:finance"]), cancellationToken: ct);

        var stored = await vectors.SearchAsync(kb.Collection, new float[] { 1, 0, 0 }, 5, cancellationToken: ct);
        Assert.All(stored, r =>
        {
            Assert.Contains("tenant:acme", r.Record.AclTags);
            Assert.Contains("role:finance", r.Record.AclTags);
        });
    }

    [Fact]
    public async Task A_format_with_no_extractor_says_which_is_missing()
    {
        var (pipeline, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var document = Doc("d1", "irrelevant") with { FileName = "spreadsheet.xlsx", ContentType = "application/vnd.ms-excel" };

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await pipeline.IngestAsync(Base(), document, cancellationToken: ct));

        Assert.Contains("No extractor handles", ex.Message, StringComparison.Ordinal);
        Assert.Contains("spreadsheet.xlsx", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_document_is_refused_with_a_reason_a_user_can_act_on()
    {
        var (pipeline, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await pipeline.IngestAsync(Base(), Doc("empty", "   "), cancellationToken: ct));

        // Scanned PDFs land here, and "no text" alone would leave the user guessing.
        Assert.Contains("no extractable text", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Documents_are_isolated_per_knowledge_base()
    {
        var (pipeline, store, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = Base();
        var second = Base() with { Id = "kb2", Name = "Other" };

        await pipeline.IngestAsync(first, Doc("shared-id", Prose(5)), cancellationToken: ct);
        await pipeline.IngestAsync(second, Doc("shared-id", Prose(5)), cancellationToken: ct);

        // The same source id in two bases is two documents, in two collections.
        Assert.Single(await store.Knowledge.ListDocumentsAsync(first.Id, cancellationToken: ct));
        Assert.Single(await store.Knowledge.ListDocumentsAsync(second.Id, cancellationToken: ct));
        Assert.True(await vectors.CountAsync(first.Collection, ct) > 0);
        Assert.True(await vectors.CountAsync(second.Collection, ct) > 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    /// <summary>Plain-text extractor standing in for the real ones, with "--- page N ---" markers for paging.</summary>
    private sealed class TextExtractor : IDocumentExtractor
    {
        // Outranks the real plain-text extractor Core registers, so these tests exercise paged sections.
        public int Priority => 100;

        public bool CanHandle(string fileName, string? contentType) =>
            fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || contentType == "text/plain";

        public async Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(content);
            var text = await reader.ReadToEndAsync(cancellationToken);

            var sections = new List<DocumentSection>();
            int? page = null;
            var buffer = new StringBuilder();

            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("--- page ", StringComparison.Ordinal))
                {
                    Flush(sections, buffer, page);
                    page = int.Parse(line.Replace("--- page ", "", StringComparison.Ordinal).Replace("---", "", StringComparison.Ordinal).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }

                buffer.AppendLine(line);
            }

            Flush(sections, buffer, page);
            return new ExtractedDocument { Title = Path.GetFileNameWithoutExtension(fileName), Sections = sections };
        }

        private static void Flush(List<DocumentSection> sections, StringBuilder buffer, int? page)
        {
            var text = buffer.ToString().Trim();
            buffer.Clear();
            if (text.Length > 0)
            {
                sections.Add(new DocumentSection(text) { Page = page });
            }
        }
    }
}
