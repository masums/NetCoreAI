using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// Knowledge bases, data sources and the sync job that joins them to the ingestion pipeline.
/// </summary>
public class KnowledgeServiceTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";

    private async Task<(IKnowledgeService Knowledge, IBackgroundJobRunner Jobs, IVectorStore Vectors)> StartAsync()
    {
        _host = await TestHost.StartAsync(
            b => b.Services.AddSingleton<IModelProvider>(new FakeProvider("fake", ProviderKind.Local, ModelFormat.Gguf)),
            o => _dataDirectory = o.DataDirectory);

        await _host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "embedder",
            Name = "Embedder",
            Format = ModelFormat.Gguf,
            ProviderId = "fake",
            Path = "embedder.gguf",
        }, TestContext.Current.CancellationToken);

        return (
            _host.Services.GetRequiredService<IKnowledgeService>(),
            _host.Services.GetRequiredService<IBackgroundJobRunner>(),
            _host.Services.GetRequiredService<IVectorStore>());
    }

    private static KnowledgeBase NewBase(string id = "kb1") => new()
    {
        Id = id,
        Name = "Handbook",
        EmbeddingModel = "embedder",
        Chunking = new ChunkingOptions { MaxTokens = 100, OverlapTokens = 0, MinTokens = 1 },
    };

    /// <summary>Writes files into the base's upload folder, which is what a file source reads by default.</summary>
    private string WriteUpload(string knowledgeBaseId, string fileName, string text)
    {
        var folder = FileDataSource.UploadFolder(_dataDirectory, knowledgeBaseId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    private static DataSourceDefinition FileSource(string knowledgeBaseId, string id = "src1") => new()
    {
        Id = id,
        KnowledgeBaseId = knowledgeBaseId,
        Name = "Uploads",
        Type = FileDataSource.TypeName,
    };

    private static async Task<JobRecord> WaitAsync(IBackgroundJobRunner jobs, string id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var job = await jobs.GetAsync(id, timeout.Token) ?? throw new InvalidOperationException($"Job {id} vanished.");
            if (job.IsTerminal)
            {
                return job;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task A_knowledge_base_can_be_created_listed_and_deleted()
    {
        var (knowledge, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await knowledge.CreateAsync(NewBase(), ct);
        Assert.Single(await knowledge.ListAsync(ct));

        await knowledge.DeleteAsync("kb1", ct);
        Assert.Empty(await knowledge.ListAsync(ct));
    }

    [Fact]
    public async Task Creating_the_same_id_twice_is_refused()
    {
        var (knowledge, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await knowledge.CreateAsync(NewBase(), ct));
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_embedding_model_of_an_indexed_base_is_refused_with_the_reason()
    {
        var (knowledge, jobs, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "policy.txt", "The holiday policy grants twenty five days of paid leave every year.");
        await knowledge.SaveSourceAsync(FileSource("kb1"), ct);
        await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);

        var updated = (await knowledge.GetAsync("kb1", ct))! with { EmbeddingModel = "some-other-model" };
        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await knowledge.UpdateAsync(updated, ct));

        // Vectors from two models are not comparable; silently mixing them would return nonsense.
        Assert.Contains("cannot be compared", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Syncing_a_file_source_ingests_every_file_and_counts_them()
    {
        var (knowledge, jobs, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "holiday.txt", "Every employee gets twenty five days of paid holiday each year.");
        WriteUpload("kb1", "expenses.md", "# Expenses\n\nClaims must be submitted within thirty days of the spend.");
        await knowledge.SaveSourceAsync(FileSource("kb1"), ct);

        var job = await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.ItemsTotal);
        Assert.Equal(0, job.ItemsFailed);

        var documents = await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct);
        Assert.Equal(2, documents.Count);

        // The base's counters come from the rows, so the UI and the store cannot drift apart.
        var updated = await knowledge.GetAsync("kb1", ct);
        Assert.Equal(2, updated!.DocumentCount);
        Assert.Equal(updated.ChunkCount, await vectors.CountAsync(updated.Collection, ct));
    }

    [Fact]
    public async Task A_second_sync_skips_files_that_have_not_changed()
    {
        var (knowledge, jobs, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "holiday.txt", "Every employee gets twenty five days of paid holiday each year.");
        await knowledge.SaveSourceAsync(FileSource("kb1"), ct);

        await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);
        var before = (await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct))[0];

        await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);
        var after = (await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct))[0];

        // Same id, same hash, same ingest timestamp: nothing was re-embedded.
        Assert.Single(await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct));
        Assert.Equal(before.IngestedAt, after.IngestedAt);
    }

    [Fact]
    public async Task One_unreadable_file_does_not_abandon_the_rest_of_the_sync()
    {
        var (knowledge, jobs, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "good.txt", "This document has plenty of perfectly readable text in it.");

        // No extractor handles .bin, so this one document fails while the other succeeds.
        WriteUpload("kb1", "mystery.bin", "binary-ish");
        await knowledge.SaveSourceAsync(FileSource("kb1"), ct);

        var job = await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(1, job.ItemsFailed);
        Assert.Contains("mystery", Assert.Single(job.Failures).Item, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct));
    }

    [Fact]
    public async Task A_file_pattern_limits_what_is_ingested()
    {
        var (knowledge, jobs, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "keep.md", "# Keep\n\nThis markdown file should be ingested by the source.");
        WriteUpload("kb1", "skip.txt", "This plain text file should not be ingested at all.");

        await knowledge.SaveSourceAsync(FileSource("kb1") with
        {
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { [FileDataSource.PatternSetting] = "*.md" },
        }, ct);

        await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);

        var document = Assert.Single(await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct));
        Assert.Equal("keep", document.Title);
    }

    [Fact]
    public async Task Testing_a_file_source_reports_what_it_found()
    {
        var (knowledge, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "one.txt", "text");
        WriteUpload("kb1", "two.txt", "text");

        var result = await knowledge.TestSourceAsync(FileSource("kb1"), ct);

        Assert.True(result.Success);
        Assert.Equal(2, result.DocumentCount);
        Assert.Equal(2, result.SampleTitles.Count);
    }

    [Fact]
    public async Task Testing_a_folder_that_does_not_exist_explains_that_the_server_reads_it()
    {
        var (knowledge, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        var source = FileSource("kb1") with
        {
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FileDataSource.FolderSetting] = Path.Combine(Path.GetTempPath(), "netcoreai-not-here"),
            },
        };

        var result = await knowledge.TestSourceAsync(source, ct);

        Assert.False(result.Success);
        Assert.Contains("server", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_document_removes_its_chunks_too()
    {
        var (knowledge, jobs, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "holiday.txt", "Every employee gets twenty five days of paid holiday each year.");
        await knowledge.SaveSourceAsync(FileSource("kb1"), ct);
        await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);

        var document = Assert.Single(await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct));
        await knowledge.DeleteDocumentAsync("kb1", document.Id, ct);

        // A deleted document must stop being retrievable, not just disappear from the list.
        Assert.Empty(await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct));
        Assert.Equal(0, await vectors.CountAsync(NewBase().Collection, ct));
        Assert.Equal(0, (await knowledge.GetAsync("kb1", ct))!.DocumentCount);
    }

    [Fact]
    public async Task Deleting_a_source_removes_the_documents_it_brought_in()
    {
        var (knowledge, jobs, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "holiday.txt", "Every employee gets twenty five days of paid holiday each year.");
        await knowledge.SaveSourceAsync(FileSource("kb1"), ct);
        await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);

        await knowledge.DeleteSourceAsync("src1", deleteDocuments: true, ct);

        Assert.Empty(await knowledge.ListDocumentsAsync("kb1", cancellationToken: ct));
        Assert.Equal(0, await vectors.CountAsync(NewBase().Collection, ct));
    }

    [Fact]
    public async Task Deleting_a_base_drops_its_vectors_sources_and_uploads()
    {
        var (knowledge, jobs, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload("kb1", "holiday.txt", "Every employee gets twenty five days of paid holiday each year.");
        await knowledge.SaveSourceAsync(FileSource("kb1"), ct);
        await WaitAsync(jobs, (await knowledge.SyncAsync("kb1", cancellationToken: ct)).Id, ct);

        await knowledge.DeleteAsync("kb1", ct);

        Assert.Empty(await knowledge.ListSourcesAsync("kb1", ct));
        Assert.Empty(await vectors.ListCollectionsAsync(ct));
        Assert.False(Directory.Exists(FileDataSource.UploadFolder(_dataDirectory, "kb1")));
    }

    [Fact]
    public async Task Syncing_a_base_with_no_sources_says_what_to_do_instead()
    {
        var (knowledge, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await knowledge.SyncAsync("kb1", cancellationToken: ct));

        Assert.Contains("no enabled data source", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IngestAsync", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_source_type_names_the_ones_that_exist()
    {
        var (knowledge, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await knowledge.SaveSourceAsync(FileSource("kb1") with { Type = "sharepoint" }, ct));

        Assert.Contains("sharepoint", ex.Message, StringComparison.Ordinal);
        Assert.Contains(FileDataSource.TypeName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_can_be_pushed_straight_in_without_a_data_source()
    {
        var (knowledge, _, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await knowledge.CreateAsync(NewBase(), ct);

        var pushed = await knowledge.IngestAsync("kb1", new SourceDocument("host-1", "Pushed document")
        {
            FileName = "pushed.txt",
            ContentType = "text/plain",
            OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "This document was pushed by the host application rather than crawled from a source."))),
        }, cancellationToken: ct);

        Assert.Equal("Pushed document", pushed.Title);
        Assert.Null(pushed.DataSourceId);
        Assert.True(await vectors.CountAsync(NewBase().Collection, ct) > 0);
    }

    [Fact]
    public async Task The_available_source_types_are_listed_for_the_picker()
    {
        var (knowledge, _, _) = await StartAsync();

        Assert.Contains(FileDataSource.TypeName, knowledge.SourceTypes);
        Assert.Contains(RestApiDataSource.TypeName, knowledge.SourceTypes);
    }

    // ---------- uploads ----------

    private static MemoryStream Bytes(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task An_uploaded_file_is_stored_in_the_base_folder_and_becomes_searchable()
    {
        var (knowledge, _, vectors) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = await knowledge.CreateAsync(NewBase(), ct);

        var document = await knowledge.UploadAsync(kb.Id, "holiday.txt", Bytes("Everyone gets twenty five days of holiday."), "text/plain", cancellationToken: ct);

        // On disk, because the base's own file source owns this folder and will see it on the next sync.
        Assert.True(File.Exists(Path.Combine(FileDataSource.UploadFolder(_dataDirectory, kb.Id), "holiday.txt")));
        Assert.True(document.ChunkCount > 0);
        Assert.True(await vectors.CountAsync(kb.Collection, ct) > 0);
    }

    [Fact]
    public async Task Uploading_the_same_name_twice_replaces_the_document_rather_than_duplicating_it()
    {
        var (knowledge, _, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = await knowledge.CreateAsync(NewBase(), ct);

        await knowledge.UploadAsync(kb.Id, "policy.txt", Bytes("The old policy grants ten days."), "text/plain", cancellationToken: ct);
        await knowledge.UploadAsync(kb.Id, "policy.txt", Bytes("The new policy grants twenty days."), "text/plain", cancellationToken: ct);

        // The id is the file name, so a corrected upload is an edit: two copies would both stay retrievable.
        var documents = await knowledge.ListDocumentsAsync(kb.Id, cancellationToken: ct);
        Assert.Single(documents);
    }

    [Fact]
    public async Task An_upload_that_re_uploads_a_file_a_folder_sync_already_saw_stays_one_document()
    {
        var (knowledge, jobs, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kb = await knowledge.CreateAsync(NewBase(), ct);
        WriteUpload(kb.Id, "handbook.txt", "The handbook explains the holiday policy.");
        await knowledge.SaveSourceAsync(FileSource(kb.Id), ct);
        await WaitAsync(jobs, (await knowledge.SyncAsync(kb.Id, cancellationToken: ct)).Id, ct);

        await knowledge.UploadAsync(kb.Id, "handbook.txt", Bytes("The handbook now explains the new holiday policy."), "text/plain", cancellationToken: ct);

        // The upload path and the file source must agree on a document's identity, or every upload of an
        // already-synced file would leave a stale second copy behind.
        Assert.Single(await knowledge.ListDocumentsAsync(kb.Id, cancellationToken: ct));
    }

    [Theory]
    [InlineData("../../appsettings.json", "appsettings.json")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "hosts")]
    [InlineData("notes/../secret.txt", "secret.txt")]
    [InlineData("report.pdf.", "report.pdf")]
    public void An_upload_name_cannot_escape_the_upload_folder(string sent, string expected) =>
        Assert.Equal(expected, KnowledgeService.SafeFileName(sent));

    [Fact]
    public void An_upload_with_no_usable_name_is_refused_rather_than_given_one()
    {
        // Inventing a name would hide a broken client; the message says what the caller has to send.
        var error = Assert.Throws<NetCoreAIException>(() => KnowledgeService.SafeFileName("   "));

        Assert.Contains("fileName", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploading_to_a_base_that_does_not_exist_says_so()
    {
        var (knowledge, _, _) = await StartAsync();

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            knowledge.UploadAsync("nope", "a.txt", Bytes("text"), "text/plain", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
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
}
