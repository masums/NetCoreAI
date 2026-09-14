using NetCoreAI.Backends.Gguf;
using Xunit;

namespace NetCoreAI.Core.Tests.Gguf;

public class GgufMetadataReaderTests
{
    [Fact]
    public void Reads_architecture_shape_and_template_from_a_chat_model_header()
    {
        var metadata = GgufMetadataReader.Read(GgufHeaderWriter.ChatModel().ToStream());

        Assert.Equal("qwen2", metadata.Architecture);
        Assert.Equal("Qwen2.5 0.5B Instruct", metadata.Name);
        Assert.Equal(32768, metadata.ContextLength);
        Assert.Equal(896, metadata.EmbeddingLength);
        Assert.Equal(24, metadata.BlockCount);
        Assert.Equal(14, metadata.HeadCount);
        Assert.Equal(2, metadata.HeadCountKv);
        Assert.Equal(64, metadata.HeadDimension);       // 896 / 14
        Assert.Equal("Q4_K_M", metadata.Quantization);
        Assert.Equal("apache-2.0", metadata.License);
        Assert.Equal(500_000_000, metadata.ParameterCount);
        Assert.Contains("im_start", metadata.ChatTemplate);
        Assert.False(metadata.IsEmbeddingModel);
        Assert.Equal(3u, metadata.Version);
        Assert.Equal(291, metadata.TensorCount);
    }

    [Fact]
    public void Chat_model_capabilities_cover_streaming_and_structured_output()
    {
        var capabilities = GgufMetadataReader.Read(GgufHeaderWriter.ChatModel().ToStream()).ToCapabilities();

        Assert.True(capabilities.Supports(ModelCapability.Chat));
        Assert.True(capabilities.Supports(ModelCapability.Streaming));
        Assert.True(capabilities.Supports(ModelCapability.StructuredOutput));
        Assert.True(capabilities.Supports(ModelCapability.JsonMode));
        Assert.False(capabilities.Supports(ModelCapability.Embeddings));
        Assert.Equal(32768, capabilities.MaxContext);
    }

    [Fact]
    public void Detects_an_embedding_model_from_its_pooling_type()
    {
        var metadata = GgufMetadataReader.Read(GgufHeaderWriter.EmbeddingModel().ToStream());

        Assert.True(metadata.IsEmbeddingModel);
        Assert.Equal("Q8_0", metadata.Quantization);
        var capabilities = metadata.ToCapabilities();
        Assert.True(capabilities.Supports(ModelCapability.Embeddings));
        Assert.False(capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(768, capabilities.EmbeddingDimensions);
    }

    [Fact]
    public void Tool_calling_is_reported_only_when_the_template_mentions_tools()
    {
        var plain = GgufMetadataReader.Read(GgufHeaderWriter.ChatModel().ToStream()).ToCapabilities();
        Assert.False(plain.Supports(ModelCapability.ToolCalling));

        var withTools = new GgufHeaderWriter()
            .String("general.architecture", "llama")
            .String("tokenizer.chat_template", "{% if tools %}{{ tools | tojson }}{% endif %}{{ messages }}");
        Assert.True(GgufMetadataReader.Read(withTools.ToStream()).ToCapabilities().Supports(ModelCapability.ToolCalling));
    }

    [Fact]
    public void Skips_large_arrays_without_materialising_them()
    {
        var writer = new GgufHeaderWriter()
            .String("general.architecture", "llama")
            .StringArray("tokenizer.ggml.tokens", Enumerable.Range(0, 5000).Select(i => "token" + i).ToArray())
            .Int32Array("tokenizer.ggml.token_type", Enumerable.Repeat(1, 5000).ToArray())
            .UInt32("llama.block_count", 32);

        var metadata = GgufMetadataReader.Read(writer.ToStream());

        Assert.Equal(32, metadata.BlockCount);
        Assert.Equal("[5000 items]", metadata.Raw["tokenizer.ggml.tokens"]);
        Assert.Equal("[5000 items]", metadata.Raw["tokenizer.ggml.token_type"]);
    }

    [Fact]
    public void Renders_short_arrays_so_small_settings_stay_readable()
    {
        var writer = new GgufHeaderWriter()
            .String("general.architecture", "llama")
            .Int32Array("llama.rope.scaling.factors", 1, 2, 3);

        Assert.Equal("1,2,3", GgufMetadataReader.Read(writer.ToStream()).Raw["llama.rope.scaling.factors"]);
    }

    [Fact]
    public void Reads_every_scalar_value_type()
    {
        var writer = new GgufHeaderWriter()
            .String("general.architecture", "llama")
            .UInt64("big", 9_000_000_000)
            .Float("temp", 0.5f)
            .Bool("flag", true);

        var raw = GgufMetadataReader.Read(writer.ToStream()).Raw;

        Assert.Equal("9000000000", raw["big"]);
        Assert.Equal("0.5", raw["temp"]);
        Assert.Equal("True", raw["flag"]);
    }

    [Fact]
    public void Supports_the_older_32_bit_header_layout()
    {
        var bytes = new GgufHeaderWriter { Version = 3 }.String("general.architecture", "llama").ToBytes();
        Assert.Equal("llama", GgufMetadataReader.Read(new MemoryStream(bytes)).Architecture);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_gguf()
    {
        var bytes = "this is a Git LFS pointer, not a model"u8.ToArray();
        var ex = Assert.Throws<GgufFormatException>(() => GgufMetadataReader.Read(new MemoryStream(bytes)));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_unsupported_container_version()
    {
        var bytes = new GgufHeaderWriter { Version = 99 }.String("general.architecture", "llama").ToBytes();
        Assert.Throws<GgufFormatException>(() => GgufMetadataReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void Reports_a_truncated_header_clearly()
    {
        var writer = new GgufHeaderWriter { OverstateKvCount = 50 }.String("general.architecture", "llama");
        var ex = Assert.Throws<GgufFormatException>(() => GgufMetadataReader.Read(new MemoryStream(writer.ToBytes())));
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Falls_back_to_the_filename_when_the_header_omits_the_quantization()
    {
        var writer = new GgufHeaderWriter().String("general.architecture", "llama");   // no general.file_type
        var path = writer.ToFile("qwen2.5-0.5b-instruct-q4_k_m");
        try
        {
            Assert.Equal("Q4_K_M", GgufMetadataReader.Read(path).Quantization);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("model-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("model.q8_0.gguf", "Q8_0")]
    [InlineData("llama-3-8b-IQ4_XS", "IQ4_XS")]
    [InlineData("model-f16", "F16")]
    [InlineData("no-quant-here", null)]
    public void Recovers_quantization_from_common_filename_shapes(string fileName, string? expected)
        => Assert.Equal(expected, GgufMetadataReader.QuantizationFromFileName(fileName));

    [Theory]
    [InlineData("1.5B", 1_500_000_000L)]
    [InlineData("270M", 270_000_000L)]
    [InlineData("7B", 7_000_000_000L)]
    [InlineData("", null)]
    [InlineData("large", null)]
    public void Parses_size_labels(string label, long? expected)
        => Assert.Equal(expected, GgufMetadataReader.ParseSizeLabel(label));

    [Fact]
    public void IsGgufFile_recognises_the_container_and_rejects_other_files()
    {
        var gguf = GgufHeaderWriter.ChatModel().ToFile();
        var notGguf = Path.Combine(Path.GetDirectoryName(gguf)!, "readme.txt");
        File.WriteAllText(notGguf, "hello");
        try
        {
            Assert.True(GgufMetadataReader.IsGgufFile(gguf));
            Assert.False(GgufMetadataReader.IsGgufFile(notGguf));
            Assert.False(GgufMetadataReader.IsGgufFile(Path.Combine(Path.GetDirectoryName(gguf)!, "missing.gguf")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(gguf)!, recursive: true);
        }
    }
}
