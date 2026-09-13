using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Storage;
using Xunit;

namespace NetCoreAI.Core.Tests;

/// <summary>
/// Disk accounting and orphan cleanup. The deletion guards matter most: this is the one place in NetCoreAI
/// where a UI action removes files, so a stale list must never take model weights with it.
/// </summary>
public class StorageServiceTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";

    private async Task<(IStorageService Storage, IModelRegistry Registry)> StartAsync(Action<NetCoreAIOptions>? options = null)
    {
        _host = await TestHost.StartAsync(
            b => b.Services.AddSingleton<IModelProvider>(new FakeProvider("fake", ProviderKind.Local, ModelFormat.Gguf, ModelFormat.Onnx)),
            o =>
            {
                options?.Invoke(o);
                _dataDirectory = o.DataDirectory;
            });

        return (_host.Services.GetRequiredService<IStorageService>(), _host.Services.GetRequiredService<IModelRegistry>());
    }

    private string WriteModelFile(string name, int bytes)
    {
        var path = Path.Combine(_dataDirectory, "models", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static async Task<ModelDescriptor> RegisterAsync(IModelRegistry registry, string id, string path, CancellationToken ct) =>
        await registry.RegisterAsync(new ModelDescriptor
        {
            Id = id,
            Name = id,
            Format = ModelFormat.Gguf,
            ProviderId = "fake",
            Path = path,
        }, ct);

    [Fact]
    public async Task Usage_reports_each_model_and_the_directory_total()
    {
        var (storage, registry) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(registry, "small", WriteModelFile("small.gguf", 4096), ct);
        await RegisterAsync(registry, "large", WriteModelFile("large.gguf", 40960), ct);

        var usage = await storage.GetUsageAsync(ct);

        Assert.Equal(2, usage.Models.Count);

        // Largest first: that is the one worth deleting when space runs short.
        Assert.Equal("large", usage.Models[0].ModelId);
        Assert.Equal(40960, usage.Models[0].SizeBytes);
        Assert.Equal(4096 + 40960, usage.ModelBytes);
        Assert.True(usage.TotalBytes >= usage.ModelBytes, "the directory total includes the models and the metadata database");
    }

    [Fact]
    public async Task A_remote_model_costs_no_disk()
    {
        var (storage, registry) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "gpt-4o-mini",
            Name = "GPT-4o mini",
            Format = ModelFormat.Remote,
            ProviderId = "openai",
            ConnectionId = "prod",
        }, ct);

        var usage = await storage.GetUsageAsync(ct);

        Assert.Empty(usage.Models);
        Assert.Equal(0, usage.ModelBytes);
    }

    [Fact]
    public async Task A_model_whose_files_vanished_is_flagged_rather_than_hidden()
    {
        var (storage, registry) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var path = WriteModelFile("gone.gguf", 2048);
        await RegisterAsync(registry, "gone", path, ct);
        File.Delete(path);

        var usage = await storage.GetUsageAsync(ct);

        var model = Assert.Single(usage.Models);
        Assert.False(model.Exists);
        Assert.Equal(0, model.SizeBytes);
    }

    [Fact]
    public async Task Files_no_model_claims_are_reported_as_reclaimable()
    {
        var (storage, registry) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(registry, "kept", WriteModelFile("kept.gguf", 1024), ct);
        var leftover = WriteModelFile("leftover.gguf", 8192);

        var orphans = await storage.ScanOrphansAsync(ct);

        var orphan = Assert.Single(orphans);
        Assert.Equal(leftover, orphan.Path);
        Assert.Equal(8192, orphan.SizeBytes);
        Assert.Contains("No registered model claims", orphan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_inside_a_registered_onnx_folder_are_not_orphans()
    {
        var (storage, registry) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var folder = Path.Combine(_dataDirectory, "models", "onnx-export");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(folder, "genai_config.json"), new byte[128]);
        File.WriteAllBytes(Path.Combine(folder, "tokenizer.json"), new byte[256]);

        await registry.RegisterAsync(new ModelDescriptor
        {
            Id = "onnx-model",
            Name = "ONNX model",
            Format = ModelFormat.Onnx,
            ProviderId = "fake",
            Path = folder,
        }, ct);

        // The registry points at the folder, so everything in it belongs to that model.
        Assert.Empty(await storage.ScanOrphansAsync(ct));
    }

    [Fact]
    public async Task An_interrupted_download_is_reclaimable_and_says_so()
    {
        var (storage, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        WriteModelFile("half-downloaded.gguf.part", 5000);

        var orphan = Assert.Single(await storage.ScanOrphansAsync(ct));

        Assert.Contains("interrupted download", orphan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_orphans_frees_the_space_and_leaves_models_alone()
    {
        var (storage, registry) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var kept = WriteModelFile("kept.gguf", 1024);
        await RegisterAsync(registry, "kept", kept, ct);
        var junk = WriteModelFile("junk.gguf", 8192);

        var freed = await storage.DeleteOrphansAsync([junk], ct);

        Assert.Equal(8192, freed);
        Assert.False(File.Exists(junk));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task Deleting_a_registered_model_file_is_refused()
    {
        var (storage, registry) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var path = WriteModelFile("precious.gguf", 4096);
        await RegisterAsync(registry, "precious", path, ct);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await storage.DeleteOrphansAsync([path], ct));

        Assert.Contains("belongs to a registered model", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(path), "the weights must survive a stale delete request");
    }

    [Fact]
    public async Task Deleting_outside_the_data_directory_is_refused()
    {
        var (storage, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var outside = Path.Combine(Path.GetTempPath(), $"netcoreai-outside-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(outside, new byte[16], ct);
        try
        {
            var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await storage.DeleteOrphansAsync([outside], ct));

            Assert.Contains("outside the data directory", ex.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task The_quota_warning_trips_once_usage_passes_the_threshold()
    {
        var (storage, registry) = await StartAsync(o => o.Models.StorageQuotaWarningBytes = 10_000);
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(registry, "big", WriteModelFile("big.gguf", 50_000), ct);

        var usage = await storage.GetUsageAsync(ct);

        Assert.True(usage.OverQuota);
        Assert.Equal(10_000, usage.QuotaWarningBytes);
    }

    [Fact]
    public async Task No_quota_configured_means_no_warning()
    {
        var (storage, registry) = await StartAsync(o => o.Models.StorageQuotaWarningBytes = null);
        var ct = TestContext.Current.CancellationToken;
        await RegisterAsync(registry, "big", WriteModelFile("big.gguf", 50_000), ct);

        Assert.False((await storage.GetUsageAsync(ct)).OverQuota);
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
