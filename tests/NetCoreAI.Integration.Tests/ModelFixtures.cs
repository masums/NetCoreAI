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

    /// <summary>Qwen2.5 0.5B Instruct built for ONNX Runtime GenAI, int4 on the CPU.</summary>
    private const string OnnxChatBaseUrl = "https://huggingface.co/xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32";

    private const string OnnxChatFolder = "qwen2.5-0.5b-instruct-onnx-int4";

    private static readonly string[] OnnxChatFiles =
    [
        "genai_config.json", "model.onnx", "model.onnx.data", "tokenizer.json", "tokenizer_config.json",
        "special_tokens_map.json", "vocab.json", "merges.txt", "added_tokens.json", "chat_template.jinja",
    ];

    /// <summary>all-MiniLM-L6-v2: the reference sentence-transformers ONNX export, 90 MB, 384 dimensions.</summary>
    private const string OnnxEmbeddingBaseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main";

    private const string OnnxEmbeddingFolder = "all-MiniLM-L6-v2-onnx";

    private static readonly string[] OnnxEmbeddingFiles =
    [
        "onnx/model.onnx", "config.json", "vocab.txt", "tokenizer.json", "tokenizer_config.json",
        "special_tokens_map.json", "modules.json", "1_Pooling/config.json",
    ];

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

    /// <summary>Folder of the ONNX Runtime GenAI chat fixture, downloading it if needed. Null when model tests are disabled.</summary>
    public static Task<string?> GetOnnxChatModelAsync(CancellationToken cancellationToken = default) =>
        GetFolderAsync(OnnxChatFolder, OnnxChatBaseUrl, OnnxChatFiles, cancellationToken);

    /// <summary>Folder of the sentence-transformers ONNX embedding fixture. Null when model tests are disabled.</summary>
    public static Task<string?> GetOnnxEmbeddingModelAsync(CancellationToken cancellationToken = default) =>
        GetFolderAsync(OnnxEmbeddingFolder, OnnxEmbeddingBaseUrl, OnnxEmbeddingFiles, cancellationToken);

    /// <summary>Downloads every file of a multi-file model into one cached folder, skipping what is already there.</summary>
    private static async Task<string?> GetFolderAsync(string folder, string baseUrl, string[] files, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        var directory = Path.Combine(CacheDirectory, folder);
        if (files.All(f => File.Exists(Path.Combine(directory, f.Replace('/', Path.DirectorySeparatorChar)))))
        {
            return directory;
        }

        await DownloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in files)
            {
                var destination = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(destination))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAsync($"{baseUrl}/{file}", destination, cancellationToken).ConfigureAwait(false);
            }

            return directory;
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    /// <summary>Folder of the ONNX chat fixture, skipping the calling test when model tests are switched off.</summary>
    public static async Task<string> RequireOnnxChatModelAsync(CancellationToken cancellationToken = default)
    {
        Assert.SkipUnless(Enabled, "Model tests are off. Set NETCOREAI_TEST_MODELS=1 to download the fixture and run them.");
        var path = await GetOnnxChatModelAsync(cancellationToken).ConfigureAwait(false);
        Assert.SkipWhen(path is null, "The ONNX test model could not be downloaded.");
        return path!;
    }

    /// <summary>Folder of the ONNX embedding fixture, skipping the calling test when model tests are switched off.</summary>
    public static async Task<string> RequireOnnxEmbeddingModelAsync(CancellationToken cancellationToken = default)
    {
        Assert.SkipUnless(Enabled, "Model tests are off. Set NETCOREAI_TEST_MODELS=1 to download the fixture and run them.");
        var path = await GetOnnxEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);
        Assert.SkipWhen(path is null, "The ONNX embedding test model could not be downloaded.");
        return path!;
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
