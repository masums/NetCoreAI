using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Storage;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// What the Storage page says about a file no registered model claims.
/// </summary>
/// <remarks>
/// "Left over from a removed model or a manual copy" was asserted for every unclaimed file. It is wrong
/// for a model whose format no installed backend can read: that model was downloaded, is intact, and is
/// unclaimed because nothing in the host can open it. Telling somebody it is a leftover invites them to
/// delete a model they still want, when the fix is a package reference. Found after an ONNX folder was
/// listed as reclaimable on a host with no ONNX backend registered.
/// </remarks>
public sealed class OrphanReasonTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private string _dataDir = "";
    private string _models = "";

    public async ValueTask InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        _models = Directory.CreateDirectory(Path.Combine(_dataDir, "models")).FullName;

        // A GGUF, which the registered backend below can read. Unclaimed, so a genuine leftover.
        await File.WriteAllBytesAsync(Path.Combine(_models, "leftover.gguf"), Gguf());

        // An ONNX model folder, weights plus the files that go with them. No ONNX backend is registered
        // on this host, which is the whole point.
        var onnx = Directory.CreateDirectory(Path.Combine(_models, "all-MiniLM-L6-v2")).FullName;
        Directory.CreateDirectory(Path.Combine(onnx, "onnx"));
        await File.WriteAllTextAsync(Path.Combine(onnx, "onnx", "model.onnx"), "weights");
        await File.WriteAllTextAsync(Path.Combine(onnx, "tokenizer.json"), "{}");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddGgufBackend()
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False");

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();
    }

    /// <summary>The smallest thing the GGUF detector will recognise: the magic, a version and two counts.</summary>
    private static byte[] Gguf()
    {
        var bytes = new List<byte>();
        bytes.AddRange("GGUF"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(3));
        bytes.AddRange(BitConverter.GetBytes(0L));
        bytes.AddRange(BitConverter.GetBytes(0L));
        return [.. bytes];
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<IReadOnlyList<OrphanedFile>> ScanAsync() =>
        await _app.Services.GetRequiredService<IStorageService>().ScanOrphansAsync(TestContext.Current.CancellationToken);

    private async Task<string> ReasonForAsync(string endingWith)
    {
        var orphans = await ScanAsync();
        return orphans.Single(o => o.Path.EndsWith(endingWith, StringComparison.OrdinalIgnoreCase)).Reason;
    }

    [Fact]
    public async Task Weights_no_installed_backend_can_read_say_so_rather_than_calling_themselves_leftovers()
    {
        var reason = await ReasonForAsync("model.onnx");

        Assert.Contains("No installed backend can read", reason, StringComparison.Ordinal);
        Assert.Contains("NetCoreAI.Backend.Onnx", reason, StringComparison.Ordinal);

        // And it says what to do instead of deleting, because deleting is the action the page offers.
        Assert.Contains("rather than deleting it", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_files_that_sit_beside_those_weights_get_the_same_explanation()
    {
        // A model in a folder is weights plus a tokenizer plus a config; they are unclaimed together. If
        // only the weights explained themselves, the rest would still read as junk worth deleting.
        Assert.Contains("No installed backend can read", await ReasonForAsync("tokenizer.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_a_registered_backend_does_recognise_is_still_called_a_leftover()
    {
        var reason = await ReasonForAsync("leftover.gguf");

        // The GGUF backend is registered and reads this file. Unclaimed here really does mean left behind,
        // and softening that would make the whole list useless.
        Assert.Contains("left over from a removed model", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("No installed backend can read", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_are_still_offered_for_reclaiming()
    {
        var orphans = await ScanAsync();

        // Explaining a file is not the same as hiding it. The space is real either way, and it stays the
        // reader's decision.
        Assert.Contains(orphans, o => o.Path.EndsWith("model.onnx", StringComparison.Ordinal));
        Assert.Contains(orphans, o => o.Path.EndsWith("leftover.gguf", StringComparison.Ordinal));
    }
}
