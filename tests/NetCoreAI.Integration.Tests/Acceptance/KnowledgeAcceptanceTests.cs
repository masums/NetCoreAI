using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Integration.Tests.Acceptance;

/// <summary>
/// The Phase 2 exit criterion: chat over a 500-page PDF set answers with correct page-level citations.
/// </summary>
/// <remarks>
/// Runs against a real GGUF embedding model (nomic-embed-text), because the thing being measured is
/// whether retrieval actually finds the right page — a stub embedder would only measure the plumbing.
/// Gated behind NETCOREAI_TEST_MODELS=1 and intended for the nightly job.
/// </remarks>
[Trait("Category", "Model")]
public class KnowledgeAcceptanceTests : IAsyncDisposable
{
    /// <summary>The bar from the plan: at least this share of questions cite the right page.</summary>
    private const double RequiredHitRate = 0.80;

    /// <summary>Also from the plan: ingestion keeps up with at least this many pages a minute on CPU.</summary>
    private const double RequiredPagesPerMinute = 50;

    private IHost? _host;
    private string _dataDirectory = "";

    [Fact]
    public async Task Five_hundred_pages_are_ingested_and_answered_with_correct_page_citations()
    {
        var modelPath = await ModelFixtures.RequireEmbeddingModelAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        // 1. Build the corpus.
        _dataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(_dataDirectory, "corpus");
        var pages = Corpus.Write(documents);
        Assert.Equal(Corpus.DocumentCount * Corpus.PagesPerDocument, pages);

        // 2. Start a host with the real embedding model.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = Directory.CreateDirectory(_dataDirectory).FullName });
        builder.Logging.ClearProviders();
        builder.Services.AddNetCoreAI(o => o.DataDirectory = _dataDirectory)
            .AddGgufBackend()
            .AddDocumentExtractors()
            // Pooling off: a pooled SQLite connection keeps the database file open after the test that
            // made it is done, and the process-global ClearAllPools() that used to compensate disposed
            // connections belonging to other test classes running in parallel.
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDirectory, "netcoreai.db")};Pooling=False");

        _host = builder.Build();
        await _host.StartAsync(ct);

        var registry = _host.Services.GetRequiredService<IModelRegistry>();
        await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "nomic-embed",
            Name = "Nomic Embed Text v1.5",
            Format = ModelFormat.Gguf,
            ProviderId = "gguf",
            Path = modelPath,
        }, ct);

        // 3. Create the base and ingest the corpus.
        var knowledge = _host.Services.GetRequiredService<IKnowledgeService>();
        var jobs = _host.Services.GetRequiredService<IBackgroundJobRunner>();

        await knowledge.CreateAsync(new KnowledgeBase
        {
            Id = "acceptance",
            Name = "Acceptance corpus",
            EmbeddingModel = "nomic-embed",

            // Structure-aware chunking with a page per section: the page has to survive into the citation.
            Chunking = new ChunkingOptions { Strategy = ChunkingStrategy.RecursiveStructure, MaxTokens = 256, OverlapTokens = 32, MinTokens = 8 },
            Retrieval = new RetrievalOptions { TopK = 5, MinScore = null },
        }, ct);

        await knowledge.SaveSourceAsync(new DataSourceDefinition
        {
            Id = "corpus",
            KnowledgeBaseId = "acceptance",
            Name = "Corpus",
            Type = FileDataSource.TypeName,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { [FileDataSource.FolderSetting] = documents },
        }, ct);

        var stopwatch = Stopwatch.StartNew();
        var job = await WaitAsync(jobs, (await knowledge.SyncAsync("acceptance", cancellationToken: ct)).Id, ct);
        stopwatch.Stop();

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(0, job.ItemsFailed);
        Assert.Equal(Corpus.DocumentCount, (await knowledge.ListDocumentsAsync("acceptance", cancellationToken: ct)).Count);

        var pagesPerMinute = pages / stopwatch.Elapsed.TotalMinutes;
        TestContext.Current.SendDiagnosticMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"Ingested {pages} pages in {stopwatch.Elapsed.TotalSeconds:F1}s ({pagesPerMinute:F0} pages/min)."));

        // 4. Ask the questions and score the citations.
        var retriever = _host.Services.GetRequiredService<IRetriever>();
        var hits = 0;
        var documentOnly = 0;
        var misses = new List<string>();

        foreach (var item in Corpus.Questions)
        {
            var results = await retriever.SearchAsync("acceptance", item.Question, cancellationToken: ct);
            var citations = results.Select(r => r.Citation).ToList();

            // A hit means the right page of the right document is among what the model would be shown.
            if (citations.Any(c => Matches(c, item) && c.Page == item.Page))
            {
                hits++;
            }
            else if (citations.Any(c => Matches(c, item)))
            {
                documentOnly++;
                misses.Add($"{item.Question} -> right document, wrong page (expected {item.Page}, got {string.Join(", ", citations.Where(c => Matches(c, item)).Select(c => c.Page))})");
            }
            else
            {
                misses.Add($"{item.Question} -> expected {item.Document} p{item.Page}, got {string.Join("; ", citations.Select(c => $"{c.Title} p{c.Page}"))}");
            }
        }

        var hitRate = (double)hits / Corpus.Questions.Count;
        TestContext.Current.SendDiagnosticMessage(string.Create(
            CultureInfo.InvariantCulture,
            $"Citation hit rate: {hits}/{Corpus.Questions.Count} exact page ({hitRate:P0}), {documentOnly} right document but wrong page."));

        foreach (var miss in misses)
        {
            TestContext.Current.SendDiagnosticMessage("  miss: " + miss);
        }

        Assert.True(
            hitRate >= RequiredHitRate,
            string.Create(CultureInfo.InvariantCulture, $"Citation hit rate was {hitRate:P0}, below the required {RequiredHitRate:P0}. Misses:{Environment.NewLine}{string.Join(Environment.NewLine, misses)}"));

        Assert.True(
            pagesPerMinute >= RequiredPagesPerMinute,
            string.Create(CultureInfo.InvariantCulture, $"Ingestion managed {pagesPerMinute:F0} pages/min, below the required {RequiredPagesPerMinute:F0}."));
    }

    [Fact]
    public async Task A_restricted_document_never_reaches_an_unauthorised_answer()
    {
        var modelPath = await ModelFixtures.RequireEmbeddingModelAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        _dataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = Directory.CreateDirectory(_dataDirectory).FullName });
        builder.Logging.ClearProviders();
        builder.Services.AddNetCoreAI(o => o.DataDirectory = _dataDirectory).AddGgufBackend()
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDirectory, "netcoreai.db")};Pooling=False");

        _host = builder.Build();
        await _host.StartAsync(ct);

        await _host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "nomic-embed",
            Name = "Nomic Embed Text v1.5",
            Format = ModelFormat.Gguf,
            ProviderId = "gguf",
            Path = modelPath,
        }, ct);

        var knowledge = _host.Services.GetRequiredService<IKnowledgeService>();
        await knowledge.CreateAsync(new KnowledgeBase
        {
            Id = "acl",
            Name = "Mixed access",
            EmbeddingModel = "nomic-embed",
            Chunking = new ChunkingOptions { MaxTokens = 128, MinTokens = 4 },
            Retrieval = new RetrievalOptions { TopK = 10, MinScore = null },
        }, ct);

        var client = _host.Services.GetRequiredService<IKnowledgeClient>();
        await client.IngestTextAsync("acl", "public", "Public handbook", "Every permanent employee receives twenty-five days of paid annual leave.", cancellationToken: ct);
        await client.IngestTextAsync("acl", "secret", "Board minutes", "The board agreed a confidential retention bonus for two named executives.", aclTags: ["role:board"], cancellationToken: ct);

        // With real embeddings and a broad top-k, the restricted document is well within reach: only the
        // ACL filter keeps it out. That is the property worth proving.
        var asBoard = await client.SearchAsync("acl", "retention bonus for executives", callerTags: ["role:board"], cancellationToken: ct);
        var asStaff = await client.SearchAsync("acl", "retention bonus for executives", callerTags: ["role:staff"], cancellationToken: ct);

        Assert.Contains(asBoard, h => h.Citation.Title == "Board minutes");
        Assert.DoesNotContain(asStaff, h => h.Citation.Title == "Board minutes");
    }

    private static async Task<JobRecord> WaitAsync(IBackgroundJobRunner jobs, string id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        while (true)
        {
            var job = await jobs.GetAsync(id, timeout.Token) ?? throw new InvalidOperationException($"Job {id} vanished.");
            if (job.IsTerminal)
            {
                return job;
            }

            await Task.Delay(250, timeout.Token);
        }
    }

    /// <summary>
    /// Compares a citation to the expected document. The two spell the same identity differently: the
    /// citation title comes from the PDF file name ("field-operations-manual") while the QA sheet names
    /// the document as a human would, so both are reduced to letters and digits before comparing.
    /// </summary>
    private static bool Matches(Citation citation, QaItem item) => Slug(citation.Title) == Slug(item.Document);

    private static string Slug(string value) =>
        new([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

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
}
