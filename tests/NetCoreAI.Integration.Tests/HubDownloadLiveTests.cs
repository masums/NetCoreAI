using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCoreAI.Hub;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The whole hub path with a real repository: browse, pick a variant, download it, and find it registered
/// and loadable. Gated behind NETCOREAI_TEST_MODELS=1 because it fetches ~84 MB from Hugging Face.
/// </summary>
[Trait("Category", "Model")]
public class HubDownloadLiveTests
{
    /// <summary>Nomic Embed Text v1.5, Q4_K_M: the smallest useful real model, at about 84 MB.</summary>
    private const string RepoId = "nomic-ai/nomic-embed-text-v1.5-GGUF";

    private const string File = "nomic-embed-text-v1.5.Q4_K_M.gguf";

    [Fact]
    public async Task A_model_is_browsed_downloaded_and_registered()
    {
        Assert.SkipUnless(ModelFixtures.Enabled, "Network tests are off. Set NETCOREAI_TEST_MODELS=1 to run them.");

        // A fresh data directory every run: leftover jobs from a previous run would otherwise be re-queued
        // at startup and the assertions below would be racing that worker rather than this test's download.
        var dataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = dataDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddNetCoreAI(o => o.DataDirectory = dataDirectory).AddGgufBackend()
            .AddSqliteStorage($"Data Source={Path.Combine(dataDirectory, "netcoreai.db")};Pooling=False");

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var hub = host.Services.GetRequiredService<IHubService>();

            // Browse: the repository resolves to variants, each with a verdict for this machine.
            var view = await hub.GetAsync(RepoId, cancellationToken: ct);
            Assert.NotNull(view);
            var variant = view!.Variants.FirstOrDefault(v => v.Files.Contains(File));
            Assert.NotNull(variant);
            Assert.Equal(ModelFormat.Gguf, variant!.Format);
            Assert.True(variant.SizeBytes > 50_000_000, $"expected real weights, got {variant.SizeBytes} bytes");
            Assert.Equal(FitVerdict.Fits, variant.Fit?.Verdict);

            // Download: queued, fetched, SHA-256 verified against the LFS metadata.
            var downloads = host.Services.GetRequiredService<IDownloadManager>();
            var job = await downloads.EnqueueAsync(new DownloadRequest(HuggingFaceClient.SourceId, RepoId, [File]), ct);

            // Asking for the same variant while it is in flight is a double click, not a second transfer.
            var duplicate = await downloads.EnqueueAsync(new DownloadRequest(HuggingFaceClient.SourceId, RepoId, [File]), ct);
            Assert.Equal(job.Id, duplicate.Id);

            var finished = await WaitAsync(downloads, job.Id, ct);
            Assert.Equal(DownloadState.Completed, finished.State);
            Assert.NotNull(finished.ResultModelId);

            // Registered: the GGUF detector read the header, so the entry knows what it is without loading.
            var registry = host.Services.GetRequiredService<IModelRegistry>();
            var model = await registry.GetAsync(finished.ResultModelId!, ct);
            Assert.NotNull(model);
            Assert.Equal(ModelFormat.Gguf, model!.Descriptor.Format);
            Assert.Equal("gguf", model.Descriptor.ProviderId);
            Assert.Equal("Q4_K_M", model.Descriptor.Quantization);
            Assert.True(model.Descriptor.Capabilities.Supports(ModelCapability.Embeddings), "the header says this is an embedding model");
            Assert.Equal($"huggingface:{RepoId}", model.Descriptor.Source);

            // The bytes are where the descriptor says, and the file passed verification on the way in.
            Assert.True(System.IO.File.Exists(model.Descriptor.Path), $"no file at {model.Descriptor.Path}");
            Assert.True(new FileInfo(model.Descriptor.Path!).Length > 50_000_000);

            // A verified download leaves no scratch files behind.
            Assert.Empty(Directory.EnumerateFiles(dataDirectory, "*.part", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(dataDirectory, "*.part.json", SearchOption.AllDirectories));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<DownloadJob> WaitAsync(IDownloadManager downloads, string id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        while (true)
        {
            var job = await downloads.GetAsync(id, timeout.Token) ?? throw new InvalidOperationException($"Job {id} disappeared.");
            if (job.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
            {
                Assert.Null(job.Error);
                return job;
            }

            await Task.Delay(250, timeout.Token);
        }
    }
}
