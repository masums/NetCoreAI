using System.Globalization;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Downloads the small model files the local-provider tests need, once, into a cache shared by every run.
/// </summary>
/// <remarks>
/// Nothing here runs unless <c>NETCOREAI_TEST_MODELS=1</c>, so the default suite stays offline and fast.
/// Override the cache location with <c>NETCOREAI_TEST_MODEL_DIR</c> to reuse models you already have.
/// </remarks>
public static class ModelFixtures
{
    /// <summary>Qwen2.5 0.5B Instruct, Q4_K_M: 469 MB, chat template included, fast enough on CPU for tests.</summary>
    public const string ChatModelUrl = "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf";

    private const string ChatModelFile = "qwen2.5-0.5b-instruct-q4_k_m.gguf";

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    /// <summary>True when the suite is allowed to use real weights.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("NETCOREAI_TEST_MODELS") is "1" or "true" or "TRUE";

    public static string CacheDirectory =>
        Environment.GetEnvironmentVariable("NETCOREAI_TEST_MODEL_DIR")
        ?? Path.Combine(Path.GetTempPath(), "netcoreai-test-models");

    /// <summary>Path to the chat fixture, downloading it if needed. Null when model tests are disabled.</summary>
    public static async Task<string?> GetChatModelAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return null;
        }

        var path = Path.Combine(CacheDirectory, ChatModelFile);
        if (File.Exists(path) && new FileInfo(path).Length > 100_000_000)
        {
            return path;
        }

        await DownloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 100_000_000)
            {
                return path;
            }

            Directory.CreateDirectory(CacheDirectory);
            await DownloadAsync(ChatModelUrl, path, cancellationToken).ConfigureAwait(false);
            return path;
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    /// <summary>Path to the chat fixture, skipping the calling test when model tests are switched off.</summary>
    public static async Task<string> RequireChatModelAsync(CancellationToken cancellationToken = default)
    {
        Assert.SkipUnless(Enabled, "Model tests are off. Set NETCOREAI_TEST_MODELS=1 to download the fixture and run them.");
        var path = await GetChatModelAsync(cancellationToken).ConfigureAwait(false);
        Assert.SkipWhen(path is null, "The test model could not be downloaded.");
        return path!;
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        // Download beside the target, then move, so an interrupted run never leaves a half file in place.
        var temporary = destination + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } token)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        TestContext.Current.SendDiagnosticMessage($"Downloading test model from {url} ...");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(temporary))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, destination, overwrite: true);
        var megabytes = new FileInfo(destination).Length / 1_048_576;
        TestContext.Current.SendDiagnosticMessage(string.Create(CultureInfo.InvariantCulture, $"Test model ready at {destination} ({megabytes} MB)."));
    }
}
