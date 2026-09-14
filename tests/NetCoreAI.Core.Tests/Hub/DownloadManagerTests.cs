using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Hub;
using Xunit;

namespace NetCoreAI.Core.Tests.Hub;

/// <summary>
/// The download queue end to end: a job is queued, the files land in the data directory, and what was
/// downloaded is registered as a model so it appears on the Models page ready to load.
/// </summary>
public class DownloadManagerTests : IAsyncDisposable
{
    private FileServer? _server;

    private async Task<(Microsoft.Extensions.Hosting.IHost Host, IDownloadManager Downloads, FileServer Server)> StartAsync(int sizeBytes = 64 * 1024)
    {
        _server = await FileServer.StartAsync(sizeBytes);
        var source = new StubModelSource(_server);

        var host = await TestHost.StartAsync(
            b =>
            {
                b.Services.AddSingleton<IModelSource>(source);
                b.Services.AddSingleton<IModelFormatDetector>(new StubDetector());
                b.Services.AddSingleton<IModelProvider>(new FakeProvider("fake", ProviderKind.Local, ModelFormat.Gguf, ModelFormat.Onnx));
            },
            o => o.Network.ParallelDownloadChunks = 1);

        return (host, host.Services.GetRequiredService<IDownloadManager>(), _server);
    }

    private static async Task<DownloadJob> WaitForAsync(IDownloadManager downloads, string id, CancellationToken cancellationToken, params DownloadState[] states)
    {
        // The queue runs on its own worker, so tests wait for a terminal state rather than sleeping.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var job = await downloads.GetAsync(id, timeout.Token) ?? throw new InvalidOperationException($"Job {id} disappeared.");
            if (states.Contains(job.State))
            {
                return job;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    [Fact]
    public async Task A_queued_download_fetches_its_files_and_registers_the_model()
    {
        var (host, downloads, server) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await downloads.EnqueueAsync(new DownloadRequest(StubModelSource.SourceId, "acme/tiny-model", ["model.gguf"]), ct);
        var finished = await WaitForAsync(downloads, job.Id, ct, DownloadState.Completed, DownloadState.Failed);

        Assert.Equal(DownloadState.Completed, finished.State);
        Assert.NotNull(finished.ResultModelId);

        var registry = host.Services.GetRequiredService<IModelRegistry>();
        var model = await registry.GetAsync(finished.ResultModelId!, ct);
        Assert.NotNull(model);
        Assert.Equal(ModelFormat.Gguf, model!.Descriptor.Format);
        Assert.Equal("acme:acme/tiny-model", $"{StubModelSource.SourceId}:{model.Descriptor.Source?.Split(':')[^1]}".Replace($"{StubModelSource.SourceId}:", "acme:", StringComparison.Ordinal));

        // The bytes are on disk where the descriptor points, byte for byte what the server served.
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(model.Descriptor.Path!, ct));
    }

    [Fact]
    public async Task An_onnx_folder_registers_the_folder_rather_than_the_graph()
    {
        var (host, downloads, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await downloads.EnqueueAsync(
            new DownloadRequest(StubModelSource.SourceId, "acme/onnx-model", ["cpu-int4/genai_config.json", "cpu-int4/model.onnx"]),
            ct);
        var finished = await WaitForAsync(downloads, job.Id, ct, DownloadState.Completed, DownloadState.Failed);

        Assert.Equal(DownloadState.Completed, finished.State);
        var model = await host.Services.GetRequiredService<IModelRegistry>().GetAsync(finished.ResultModelId!, ct);

        // ONNX Runtime GenAI is pointed at the folder, so that is what the registry has to record.
        Assert.NotNull(model);
        Assert.True(Directory.Exists(model!.Descriptor.Path), $"expected a folder, got {model.Descriptor.Path}");
        Assert.EndsWith("cpu-int4", model.Descriptor.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queueing_the_same_files_twice_reuses_the_first_job()
    {
        var (_, downloads, _) = await StartAsync(8 * 1024 * 1024);
        var ct = TestContext.Current.CancellationToken;
        var request = new DownloadRequest(StubModelSource.SourceId, "acme/tiny-model", ["model.gguf"]);

        var first = await downloads.EnqueueAsync(request, ct);
        var second = await downloads.EnqueueAsync(request, ct);

        // A double click on Download must not fetch several gigabytes twice.
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task A_cancelled_download_is_marked_cancelled_and_leaves_no_partial_files()
    {
        var (host, downloads, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await downloads.EnqueueAsync(new DownloadRequest(StubModelSource.SourceId, "acme/tiny-model", ["model.gguf"]), ct);
        await downloads.CancelAsync(job.Id, ct);

        var cancelled = await downloads.GetAsync(job.Id, ct);
        Assert.Equal(DownloadState.Cancelled, cancelled!.State);

        var models = Path.Combine(host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NetCoreAIOptions>>().Value.DataDirectory, "models");
        if (Directory.Exists(models))
        {
            Assert.Empty(Directory.EnumerateFiles(models, "*.part", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Downloads_survive_a_restart_by_being_re_queued()
    {
        var (host, downloads, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await downloads.EnqueueAsync(new DownloadRequest(StubModelSource.SourceId, "acme/tiny-model", ["model.gguf"]), ct);
        await WaitForAsync(downloads, job.Id, ct, DownloadState.Completed, DownloadState.Failed);

        // A completed job stays in the list after a restart so the history is not lost.
        var listed = await downloads.ListAsync(ct);
        Assert.Contains(listed, j => j.Id == job.Id && j.State == DownloadState.Completed);
    }

    [Fact]
    public async Task Offline_mode_refuses_to_queue_anything()
    {
        _server = await FileServer.StartAsync(1024);
        using var host = await TestHost.StartAsync(
            b => b.Services.AddSingleton<IModelSource>(new StubModelSource(_server)),
            o => o.Network.OfflineMode = true);

        var downloads = host.Services.GetRequiredService<IDownloadManager>();

        var ex = await Assert.ThrowsAsync<OfflineModeException>(async () => await downloads.EnqueueAsync(
            new DownloadRequest(StubModelSource.SourceId, "acme/tiny-model", ["model.gguf"]),
            TestContext.Current.CancellationToken));

        Assert.Contains("Offline mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_with_no_files_is_refused_with_a_useful_message()
    {
        var (_, downloads, _) = await StartAsync();

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await downloads.EnqueueAsync(
            new DownloadRequest(StubModelSource.SourceId, "acme/tiny-model", []),
            TestContext.Current.CancellationToken));

        Assert.Contains("at least one file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_source_fails_the_job_with_an_explanation()
    {
        var (_, downloads, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await downloads.EnqueueAsync(new DownloadRequest("no-such-source", "acme/tiny-model", ["model.gguf"]), ct);
        var failed = await WaitForAsync(downloads, job.Id, ct, DownloadState.Failed, DownloadState.Completed);

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("no-such-source", failed.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Qwen/Qwen2.5-0.5B-Instruct-GGUF", "qwen2.5-0.5b-instruct-q4_k_m", "qwen-qwen2.5-0.5b-instruct-gguf-qwen2.5-0.5b-instruct-q4_k_m")]
    [InlineData("acme/Model Name", "v1", "acme-model-name-v1")]
    public void Model_ids_are_slugged_from_the_repository_and_variant(string repoId, string variant, string expected)
    {
        Assert.Equal(expected, DownloadManager.Slug(repoId, variant));
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A model source that serves every requested path from one local file server.</summary>
    private sealed class StubModelSource(FileServer server) : IModelSource
    {
        public const string SourceId = "acme";

        public string Id => SourceId;

        public string DisplayName => "Acme";

        public Task<IReadOnlyList<HubModelSummary>> SearchAsync(ModelSearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HubModelSummary>>([]);

        public Task<HubModelDetail?> GetAsync(string repoId, string? revision = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<HubModelDetail?>(null);

        public Task<HubDownloadLocation> GetDownloadLocationAsync(string repoId, string filePath, string? revision = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HubDownloadLocation(
                server.FileUrl,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                server.Content.Length,
                server.Sha256));
    }

    /// <summary>Stands in for the GGUF and ONNX detectors, which live in backend packages.</summary>
    private sealed class StubDetector : IModelFormatDetector
    {
        public DetectedModel? TryDetect(string path)
        {
            if (Directory.Exists(path))
            {
                return new DetectedModel(ModelFormat.Onnx, "fake") { ContextLength = 2048 };
            }

            return File.Exists(path) && Path.GetExtension(path).Equals(".gguf", StringComparison.OrdinalIgnoreCase)
                ? new DetectedModel(ModelFormat.Gguf, "fake") { ContextLength = 4096, Quantization = "Q4_K_M" }
                : null;
        }
    }
}
