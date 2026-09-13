using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Hub;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// The Hugging Face client against the real API. Gated behind NETCOREAI_TEST_MODELS=1 like the model
/// fixtures: the shape of a third-party API is exactly the thing unit tests cannot keep honest.
/// </summary>
[Trait("Category", "Model")]
public class HuggingFaceLiveTests
{
    private static HuggingFaceClient CreateClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(NetCoreAIHttp.HubClient);
        var provider = services.BuildServiceProvider();

        return new HuggingFaceClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            new GgufConformanceTests.StaticOptions(new NetCoreAIOptions()),
            NullLogger<HuggingFaceClient>.Instance);
    }

    private static void RequireNetwork() =>
        Assert.SkipUnless(ModelFixtures.Enabled, "Network tests are off. Set NETCOREAI_TEST_MODELS=1 to run them.");

    [Fact]
    public async Task Search_returns_gguf_repositories_with_their_metadata()
    {
        RequireNetwork();
        var client = CreateClient();

        var results = await client.SearchAsync(
            new ModelSearchQuery { Text = "qwen2.5 instruct", Format = ModelFormat.Gguf, Limit = 10 },
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.RepoId));
            Assert.Contains("/", r.RepoId, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(r.Author));
            Assert.True(r.Downloads >= 0);
        });

        // The gguf filter is applied by the API, so every row should carry the tag.
        Assert.Contains(results, r => r.Formats.Contains(ModelFormat.Gguf));
    }

    [Fact]
    public async Task Repository_detail_carries_file_sizes_and_lfs_hashes()
    {
        RequireNetwork();
        var client = CreateClient();

        var detail = await client.GetAsync("Qwen/Qwen2.5-0.5B-Instruct-GGUF", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal("apache-2.0", detail!.Summary.License);
        Assert.False(detail.Summary.Gated);
        Assert.NotNull(detail.ReadmeMarkdown);

        var weights = detail.Files.Where(f => f.Format == ModelFormat.Gguf).ToList();
        Assert.NotEmpty(weights);
        Assert.All(weights, f =>
        {
            Assert.True(f.SizeBytes > 0, $"{f.Path} reported no size");

            // The LFS oid is the SHA-256 the downloader verifies against; without it a corrupt file is silent.
            Assert.NotNull(f.Sha256);
            Assert.Equal(64, f.Sha256!.Length);
        });

        var q4 = weights.FirstOrDefault(f => f.Path.Contains("q4_k_m", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(q4);
        Assert.Equal("Q4_K_M", q4!.Quantization);
    }

    [Fact]
    public async Task Variants_group_an_onnx_repository_by_export_folder()
    {
        RequireNetwork();
        var client = CreateClient();

        var detail = await client.GetAsync("xiaoyao9184/Qwen2.5-0.5B-Instruct-onnx-genai", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(detail);

        var variants = HubFormats.GroupVariants(detail!.Files);

        // This repository ships several builds; each is a self-contained folder a user picks between.
        Assert.True(variants.Count >= 2, $"expected several ONNX variants, got {variants.Count}");
        Assert.All(variants, v =>
        {
            Assert.Equal(ModelFormat.Onnx, v.Format);
            Assert.Contains(v.Files, f => f.EndsWith("genai_config.json", StringComparison.Ordinal));
            Assert.Contains(v.Files, f => f.EndsWith(".onnx", StringComparison.Ordinal));
        });

        Assert.Contains(variants, v => v.Quantization == "int4");
    }

    [Fact]
    public async Task A_download_location_resolves_to_the_file_itself()
    {
        RequireNetwork();
        var client = CreateClient();

        var location = await client.GetDownloadLocationAsync(
            "Qwen/Qwen2.5-0.5B-Instruct-GGUF",
            "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("/resolve/main/", location.Url.AbsoluteUri, StringComparison.Ordinal);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, location.Url);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.True(response.Content.Headers.ContentLength > 100_000_000, "the resolved URL should point at the weights, not a pointer file");
        Assert.Contains("bytes", response.Headers.AcceptRanges, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_repository_that_does_not_exist_names_both_possible_causes()
    {
        RequireNetwork();
        var client = CreateClient();

        // Hugging Face answers 401, not 404, for an unknown repository, so that it never reveals whether a
        // private one exists. The message therefore has to offer both explanations rather than pick one.
        var ex = await Assert.ThrowsAsync<NetCoreAIException>(async () =>
            await client.GetAsync("netcoreai/definitely-not-a-real-repository-9f3a", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gated", ex.Message, StringComparison.Ordinal);
        Assert.Contains("netcoreai/definitely-not-a-real-repository-9f3a", ex.Message, StringComparison.Ordinal);
    }
}
