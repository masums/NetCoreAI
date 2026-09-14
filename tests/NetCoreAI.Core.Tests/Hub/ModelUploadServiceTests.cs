using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Hub;
using Xunit;

namespace NetCoreAI.Core.Tests.Hub;

/// <summary>
/// Chunked upload from the browser. The file name and the chunk offsets come from the client, so the
/// guards around both are what these tests are mostly about.
/// </summary>
public class ModelUploadServiceTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";

    private async Task<IModelUploadService> StartAsync()
    {
        _host = await TestHost.StartAsync(
            b =>
            {
                b.Services.AddSingleton<IModelProvider>(new FakeProvider("fake", ProviderKind.Local, ModelFormat.Gguf));
                b.Services.AddSingleton<IModelFormatDetector>(new StubDetector());
            },
            o => _dataDirectory = o.DataDirectory);

        return _host.Services.GetRequiredService<IModelUploadService>();
    }

    private static MemoryStream Chunk(byte[] data, int offset, int length) => new MemoryStream(data, offset, length);

    private static byte[] Content(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task A_file_uploaded_in_chunks_is_reassembled_and_registered()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var content = Content(30_000);

        var session = await uploads.BeginAsync("my-model.gguf", content.Length, ct);
        for (var offset = 0; offset < content.Length; offset += 10_000)
        {
            var length = Math.Min(10_000, content.Length - offset);
            var state = await uploads.AppendAsync(session.UploadId, offset, Chunk(content, offset, length), ct);
            Assert.Equal(offset + length, state.ReceivedBytes);
        }

        var model = await uploads.CompleteAsync(session.UploadId, "My model", ct);

        Assert.Equal("My model", model.Name);
        Assert.Equal(ModelFormat.Gguf, model.Format);

        // Byte-for-byte what the browser sent, and out of the scratch folder.
        Assert.Equal(content, await File.ReadAllBytesAsync(model.Path!, ct));
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}uploads{Path.DirectorySeparatorChar}", model.Path!, StringComparison.Ordinal);
        Assert.Null(await uploads.GetAsync(session.UploadId, ct));
    }

    [Fact]
    public async Task Progress_can_be_read_back_so_an_interrupted_upload_knows_where_to_resume()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var content = Content(20_000);

        var session = await uploads.BeginAsync("half.gguf", content.Length, ct);
        await uploads.AppendAsync(session.UploadId, 0, Chunk(content, 0, 8_000), ct);

        var reloaded = await uploads.GetAsync(session.UploadId, ct);

        Assert.Equal(8_000, reloaded!.ReceivedBytes);
        Assert.Equal(0.4, reloaded.Progress, 2);
    }

    [Fact]
    public async Task A_chunk_at_the_wrong_offset_is_refused_rather_than_corrupting_the_file()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var content = Content(20_000);
        var session = await uploads.BeginAsync("gap.gguf", content.Length, ct);
        await uploads.AppendAsync(session.UploadId, 0, Chunk(content, 0, 5_000), ct);

        // A retry that skips ahead would leave a hole no hash check could explain later.
        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await uploads.AppendAsync(session.UploadId, 9_000, Chunk(content, 9_000, 1_000), ct));

        Assert.Contains("in order", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5_000, (await uploads.GetAsync(session.UploadId, ct))!.ReceivedBytes);
    }

    [Fact]
    public async Task Completing_early_is_refused()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var content = Content(20_000);
        var session = await uploads.BeginAsync("partial.gguf", content.Length, ct);
        await uploads.AppendAsync(session.UploadId, 0, Chunk(content, 0, 5_000), ct);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () => await uploads.CompleteAsync(session.UploadId, null, ct));

        Assert.Contains("incomplete", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sending_more_than_was_declared_discards_the_upload()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var session = await uploads.BeginAsync("liar.gguf", 1_000, ct);

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await uploads.AppendAsync(session.UploadId, 0, new MemoryStream(Content(5_000)), ct));

        Assert.Contains("more bytes than it declared", ex.Message, StringComparison.Ordinal);
        Assert.Null(await uploads.GetAsync(session.UploadId, ct));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\config", "config")]
    [InlineData("/absolute/path/model.gguf", "model.gguf")]
    [InlineData("...", "upload.bin")]
    [InlineData("model.gguf", "model.gguf")]
    public void A_client_supplied_name_cannot_escape_the_upload_folder(string given, string expected)
    {
        // The browser controls this string; traversal segments and separators are stripped, not trusted.
        Assert.Equal(expected, ModelUploadService.Sanitize(given));
    }

    [Fact]
    public async Task An_unknown_upload_id_is_refused_with_a_clear_message()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await uploads.AppendAsync(new string('a', 32), 0, new MemoryStream([1, 2, 3]), ct));

        Assert.Contains("not in progress", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_id_that_is_not_a_server_generated_token_resolves_to_nothing()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        // Anything that is not 32 hex characters cannot address a directory, traversal attempts included.
        Assert.Null(await uploads.GetAsync("../../models", ct));
        Assert.Null(await uploads.GetAsync("short", ct));
    }

    [Fact]
    public async Task Aborting_removes_the_partial_file()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var session = await uploads.BeginAsync("abandoned.gguf", 10_000, ct);
        await uploads.AppendAsync(session.UploadId, 0, new MemoryStream(Content(4_000)), ct);

        await uploads.AbortAsync(session.UploadId, ct);

        Assert.Null(await uploads.GetAsync(session.UploadId, ct));
        Assert.False(Directory.Exists(Path.Combine(_dataDirectory, "uploads", session.UploadId)));
    }

    [Fact]
    public async Task A_file_no_backend_recognises_is_kept_rather_than_making_the_user_upload_it_again()
    {
        var uploads = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("not a model");
        var session = await uploads.BeginAsync("notes.txt", content.Length, ct);
        await uploads.AppendAsync(session.UploadId, 0, new MemoryStream(content), ct);

        await Assert.ThrowsAsync<NetCoreAIException>(async () => await uploads.CompleteAsync(session.UploadId, null, ct));

        // Gigabytes of upload must not evaporate because detection failed.
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "models", "uploaded", "notes.txt")));
    }

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

    /// <summary>Stands in for the GGUF detector, which lives in a backend package.</summary>
    private sealed class StubDetector : IModelFormatDetector
    {
        public DetectedModel? TryDetect(string path) =>
            File.Exists(path) && Path.GetExtension(path).Equals(".gguf", StringComparison.OrdinalIgnoreCase)
                ? new DetectedModel(ModelFormat.Gguf, "fake") { ContextLength = 4096 }
                : null;
    }
}
