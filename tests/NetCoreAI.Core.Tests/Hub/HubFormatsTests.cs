using NetCoreAI.Hub;
using Xunit;

namespace NetCoreAI.Core.Tests.Hub;

/// <summary>
/// Turning a repository file list into the variants a user picks between. This is what the Hub page shows
/// and what the download queue receives, so the grouping has to match how each runtime consumes files.
/// </summary>
public class HubFormatsTests
{
    private static HubFile File(string path, long size = 1024) =>
        new(path, size, null, HubFormats.DetectFormat(path), HubFormats.DetectQuantization(path));

    [Theory]
    [InlineData("qwen2.5-0.5b-instruct-q4_k_m.gguf", ModelFormat.Gguf)]
    [InlineData("onnx/model.onnx", ModelFormat.Onnx)]
    [InlineData("cpu-int4/genai_config.json", ModelFormat.Onnx)]
    [InlineData("model.safetensors", ModelFormat.Safetensors)]
    [InlineData("README.md", null)]
    [InlineData("tokenizer.json", null)]
    [InlineData("config.json", null)]
    public void Format_is_read_from_the_file_name(string path, ModelFormat? expected)
    {
        Assert.Equal(expected, HubFormats.DetectFormat(path));
    }

    [Theory]
    [InlineData("Llama-3.2-3B-Instruct-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("model-IQ3_XXS.gguf", "IQ3_XXS")]
    [InlineData("mistral-7b.Q8_0.gguf", "Q8_0")]
    [InlineData("model-f16.gguf", "F16")]
    [InlineData("cpu-int4-rtn-block-32/model.onnx", "int4")]
    [InlineData("onnx/model_quantized.onnx", "int8")]
    [InlineData("model.safetensors", null)]
    public void Quantization_is_read_from_the_file_name(string path, string? expected)
    {
        Assert.Equal(expected, HubFormats.DetectQuantization(path));
    }

    [Fact]
    public void Each_gguf_quantization_is_its_own_variant()
    {
        var variants = HubFormats.GroupVariants(
        [
            File("README.md"),
            File("model-Q4_K_M.gguf", 2_000_000),
            File("model-Q8_0.gguf", 4_000_000),
        ]);

        Assert.Equal(2, variants.Count);
        Assert.All(variants, v => Assert.Equal(ModelFormat.Gguf, v.Format));
        Assert.Contains(variants, v => v.Quantization == "Q4_K_M" && v.SizeBytes == 2_000_000);

        // A README is not something anyone downloads on its own.
        Assert.DoesNotContain(variants, v => v.Files.Contains("README.md"));
    }

    [Fact]
    public void A_split_gguf_is_one_variant_carrying_every_part()
    {
        var variants = HubFormats.GroupVariants(
        [
            File("big-model-Q4_K_M-00001-of-00003.gguf", 1_000),
            File("big-model-Q4_K_M-00002-of-00003.gguf", 2_000),
            File("big-model-Q4_K_M-00003-of-00003.gguf", 3_000),
        ]);

        // llama.cpp loads a split model from its first part, but every part has to be on disk.
        var variant = Assert.Single(variants);
        Assert.Equal(3, variant.Files.Count);
        Assert.Equal(6_000, variant.SizeBytes);
    }

    [Fact]
    public void An_onnx_folder_is_one_variant_including_its_tokenizer()
    {
        var variants = HubFormats.GroupVariants(
        [
            File("README.md"),
            File("cpu_and_mobile/cpu-int4-rtn-block-32/genai_config.json", 10),
            File("cpu_and_mobile/cpu-int4-rtn-block-32/model.onnx", 100),
            File("cpu_and_mobile/cpu-int4-rtn-block-32/model.onnx.data", 400_000),
            File("cpu_and_mobile/cpu-int4-rtn-block-32/tokenizer.json", 20),
            File("cuda/cuda-fp16/genai_config.json", 10),
            File("cuda/cuda-fp16/model.onnx", 900_000),
        ]);

        Assert.Equal(2, variants.Count);

        var cpu = variants.Single(v => v.Name.Contains("cpu-int4", StringComparison.Ordinal));
        Assert.Equal(ModelFormat.Onnx, cpu.Format);
        Assert.Equal("int4", cpu.Quantization);

        // The whole folder travels together: config, graph, external data and tokenizer.
        Assert.Equal(4, cpu.Files.Count);
        Assert.Equal(400_130, cpu.SizeBytes);
        Assert.Contains(variants, v => v.Quantization == "fp16");
    }

    [Fact]
    public void A_repository_with_no_weights_has_no_variants()
    {
        Assert.Empty(HubFormats.GroupVariants([File("README.md"), File("config.json"), File("tokenizer.json")]));
    }
}
