using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCoreAI.Hub;
using Xunit;

namespace NetCoreAI.Core.Tests.Hub;

/// <summary>
/// The single-file downloader against a real HTTP server: resume, parallel ranges, rate limiting and the
/// hash check that stands between a corrupt transfer and a model that fails to load hours later.
/// </summary>
public class FileDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

    private (FileDownloader Downloader, NetCoreAIOptions Options) Create(Action<NetCoreAIOptions>? configure = null)
    {
        var options = new NetCoreAIOptions { DataDirectory = _directory };
        configure?.Invoke(options);
        var monitor = new StaticOptionsMonitor(options);

        var services = new ServiceCollection();
        services.AddHttpClient(NetCoreAIHttp.DownloadClient);
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        return (new FileDownloader(factory, new BandwidthLimiter(monitor), monitor, NullLogger<FileDownloader>.Instance), options);
    }

    private static HubDownloadLocation Location(FileServer server, string? sha256 = null) =>
        new(server.FileUrl, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, sha256);

    private string Destination(string name = "model.bin") => Path.Combine(_directory, name);

    [Fact]
    public async Task A_file_is_downloaded_and_verified()
    {
        await using var server = await FileServer.StartAsync(256 * 1024);
        var (downloader, _) = Create();
        var destination = Destination();

        await downloader.DownloadAsync(Location(server, server.Sha256), destination, null, TestContext.Current.CancellationToken);

        Assert.Equal(server.Content, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));

        // The part file and its resume sidecar are gone once the download lands.
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.json"));
    }

    [Fact]
    public async Task A_wrong_hash_deletes_the_file_and_explains_why()
    {
        await using var server = await FileServer.StartAsync(64 * 1024);
        var (downloader, _) = Create();
        var destination = Destination();

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await downloader.DownloadAsync(
            Location(server, new string('a', 64)),
            destination,
            null,
            TestContext.Current.CancellationToken));

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);

        // A file that failed verification must not be left where a provider could load it.
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task An_interrupted_download_resumes_from_the_bytes_already_on_disk()
    {
        await using var server = await FileServer.StartAsync(512 * 1024);
        var (downloader, _) = Create(o => o.Network.ParallelDownloadChunks = 1);
        var destination = Destination();

        // First attempt: the connection drops half way.
        server.TruncateAfterBytes = 200 * 1024;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await downloader.DownloadAsync(Location(server, server.Sha256), destination, null, TestContext.Current.CancellationToken));

        var partial = new FileInfo(destination + ".part").Length;
        Assert.InRange(partial, 1, 512 * 1024 - 1);

        // Second attempt: the rest is fetched with a range request rather than the whole file again.
        server.TruncateAfterBytes = null;
        server.RangeHeaders.Clear();
        await downloader.DownloadAsync(Location(server, server.Sha256), destination, null, TestContext.Current.CancellationToken);

        Assert.Equal(server.Content, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Contains(server.RangeHeaders, h => h is not null && h.Contains($"bytes={partial}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_changed_file_restarts_instead_of_splicing_two_versions()
    {
        await using var server = await FileServer.StartAsync(512 * 1024);
        var (downloader, _) = Create(o => o.Network.ParallelDownloadChunks = 1);
        var destination = Destination();

        server.TruncateAfterBytes = 100 * 1024;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await downloader.DownloadAsync(Location(server), destination, null, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(destination + ".part"));

        // A different file of a different size now lives at the same URL.
        await using var replacement = await FileServer.StartAsync(300 * 1024);
        var moved = new HubDownloadLocation(replacement.FileUrl, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, replacement.Sha256);
        File.Move(destination + ".part", Path.Combine(_directory, "moved.bin.part"));
        File.Move(destination + ".part.json", Path.Combine(_directory, "moved.bin.part.json"));

        await downloader.DownloadAsync(moved, Path.Combine(_directory, "moved.bin"), null, TestContext.Current.CancellationToken);

        // The stale bytes were thrown away rather than prefixed onto the new file.
        Assert.Equal(replacement.Content, await File.ReadAllBytesAsync(Path.Combine(_directory, "moved.bin"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Large_files_are_fetched_in_parallel_ranges()
    {
        await using var server = await FileServer.StartAsync(40 * 1024 * 1024);
        var (downloader, _) = Create(o => o.Network.ParallelDownloadChunks = 4);

        await downloader.DownloadAsync(Location(server, server.Sha256), Destination(), null, TestContext.Current.CancellationToken);

        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Destination(), TestContext.Current.CancellationToken));

        // 40 MB across 4 chunks of at least 8 MB each: every request carried a range.
        Assert.Equal(4, server.RangeHeaders.Count);
        Assert.All(server.RangeHeaders, h => Assert.NotNull(h));
    }

    [Fact]
    public async Task A_server_without_range_support_still_downloads()
    {
        await using var server = await FileServer.StartAsync(20 * 1024 * 1024);
        server.SupportsRanges = false;
        var (downloader, _) = Create(o => o.Network.ParallelDownloadChunks = 4);

        await downloader.DownloadAsync(Location(server, server.Sha256), Destination(), null, TestContext.Current.CancellationToken);

        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Destination(), TestContext.Current.CancellationToken));
        Assert.All(server.RangeHeaders, Assert.Null);
    }

    [Fact]
    public async Task Progress_is_reported_as_bytes_arrive()
    {
        await using var server = await FileServer.StartAsync(4 * 1024 * 1024);
        var (downloader, _) = Create(o => o.Network.ParallelDownloadChunks = 1);
        var reports = new List<FileProgress>();

        await downloader.DownloadAsync(
            Location(server, server.Sha256),
            Destination(),
            p => { lock (reports) { reports.Add(p); } },
            TestContext.Current.CancellationToken);

        // Reports are sampled about once a second, so a fast local transfer may produce none; what must
        // hold is that anything reported is within the file and never goes backwards.
        Assert.All(reports, r => Assert.InRange(r.BytesDone, 1, 4 * 1024 * 1024));
        Assert.Equal([.. reports.Select(r => r.BytesDone).Order()], [.. reports.Select(r => r.BytesDone)]);
    }

    [Fact]
    public async Task The_bandwidth_limit_slows_the_transfer_down()
    {
        await using var server = await FileServer.StartAsync(512 * 1024);
        var (downloader, _) = Create(o =>
        {
            o.Network.ParallelDownloadChunks = 1;
            o.Network.BandwidthLimitBytesPerSecond = 256 * 1024;
        });

        var stopwatch = Stopwatch.StartNew();
        await downloader.DownloadAsync(Location(server, server.Sha256), Destination(), null, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // 512 KB at 256 KB/s cannot finish in well under a second, even on loopback.
        Assert.True(stopwatch.Elapsed > TimeSpan.FromMilliseconds(700), $"expected throttling, finished in {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Destination(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancelling_leaves_the_partial_file_for_a_later_resume()
    {
        await using var server = await FileServer.StartAsync(8 * 1024 * 1024);
        var (downloader, _) = Create(o =>
        {
            o.Network.ParallelDownloadChunks = 1;
            o.Network.BandwidthLimitBytesPerSecond = 512 * 1024;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await downloader.DownloadAsync(Location(server, server.Sha256), Destination(), null, cts.Token));

        // The point of the part file: a cancelled download is not wasted work.
        Assert.True(File.Exists(Destination() + ".part"));
        Assert.False(File.Exists(Destination()));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class StaticOptionsMonitor(NetCoreAIOptions value) : IOptionsMonitor<NetCoreAIOptions>
    {
        public NetCoreAIOptions CurrentValue => value;

        public NetCoreAIOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NetCoreAIOptions, string?> listener) => null;
    }
}
