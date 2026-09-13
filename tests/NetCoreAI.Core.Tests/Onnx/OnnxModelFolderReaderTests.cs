using NetCoreAI.Backends.Onnx;
using Xunit;

namespace NetCoreAI.Core.Tests.Onnx;

/// <summary>
/// The folder reader answers format, shape and capability questions from the JSON configs alone, which is
/// what import, the Hub and the fit estimator rely on before anything is loaded.
/// </summary>
public class OnnxModelFolderReaderTests : IDisposable
{
    private readonly OnnxFolderBuilder _builder = new();

    [Fact]
    public void Generative_folder_is_read_from_genai_config()
    {
        var folder = OnnxModelFolderReader.Read(_builder.GenerativeFolder());

        Assert.Equal(OnnxModelKind.Generative, folder.Kind);
        Assert.Equal("qwen2", folder.ModelType);
        Assert.Equal(32768, folder.ContextLength);
        Assert.Equal(24, folder.LayerCount);
        Assert.Equal(14, folder.HeadCount);
        Assert.Equal(2, folder.HeadCountKv);
        Assert.Equal(64, folder.HeadSize);
        Assert.Equal(151936, folder.VocabSize);
        Assert.Equal("int4", folder.Quantization);
        Assert.EndsWith("model.onnx", folder.GraphPath, StringComparison.Ordinal);

        // The graph and its external-data file both count towards what has to be mapped into memory.
        Assert.Equal(4096 + 1_048_576, folder.GraphBytes);
    }

    [Fact]
    public void Generative_search_defaults_come_from_the_config()
    {
        var folder = OnnxModelFolderReader.Read(_builder.GenerativeFolder());

        Assert.Equal(0.7f, folder.SearchDefaults.Temperature);
        Assert.Equal(0.95f, folder.SearchDefaults.TopP);
        Assert.Equal(40, folder.SearchDefaults.TopK);
        Assert.Equal(1.1f, folder.SearchDefaults.RepetitionPenalty!.Value, 3);
        Assert.Equal(32768, folder.SearchDefaults.MaxLength);
    }

    [Fact]
    public void Chat_template_is_found_in_tokenizer_config_or_beside_it()
    {
        var inConfig = OnnxModelFolderReader.Read(_builder.GenerativeFolder(templateInTokenizerConfig: true));
        var inOwnFile = OnnxModelFolderReader.Read(_builder.GenerativeFolder(templateInTokenizerConfig: false));
        var missing = OnnxModelFolderReader.Read(_builder.GenerativeFolder(chatTemplate: null));

        Assert.True(inConfig.HasChatTemplate);
        Assert.Contains("im_start", inConfig.ChatTemplate, StringComparison.Ordinal);

        // The runtime only reads tokenizer_config.json, so a standalone chat_template.jinja must be picked up here.
        Assert.True(inOwnFile.HasChatTemplate);
        Assert.Contains("im_start", inOwnFile.ChatTemplate, StringComparison.Ordinal);

        Assert.False(missing.HasChatTemplate);
        Assert.Null(missing.ChatTemplate);
    }

    [Fact]
    public void Generative_capabilities_are_chat_and_streaming()
    {
        var capabilities = OnnxModelFolderReader.Read(_builder.GenerativeFolder()).ToCapabilities();

        Assert.True(capabilities.Supports(ModelCapability.Chat));
        Assert.True(capabilities.Supports(ModelCapability.Streaming));
        Assert.Equal(32768, capabilities.MaxContext);

        // ONNX Runtime GenAI has no native tool calling and guidance is a build-time option, so neither is claimed.
        Assert.False(capabilities.Supports(ModelCapability.ToolCalling));
        Assert.False(capabilities.Supports(ModelCapability.StructuredOutput));
        Assert.False(capabilities.Supports(ModelCapability.Embeddings));
    }

    [Fact]
    public void Sentence_transformers_export_is_read_as_an_embedding_model()
    {
        var folder = OnnxModelFolderReader.Read(_builder.EmbeddingFolder());

        Assert.Equal(OnnxModelKind.Embedding, folder.Kind);
        Assert.Equal("bert", folder.ModelType);
        Assert.Equal(384, folder.HiddenSize);
        Assert.Equal(512, folder.ContextLength);
        Assert.True(folder.MeanPooling);
        Assert.True(folder.NormalizeEmbeddings);

        var capabilities = folder.ToCapabilities();
        Assert.True(capabilities.Supports(ModelCapability.Embeddings));
        Assert.False(capabilities.Supports(ModelCapability.Chat));
        Assert.Equal(384, capabilities.EmbeddingDimensions);
    }

    [Fact]
    public void Pooling_module_decides_mean_versus_cls_and_normalization()
    {
        var cls = OnnxModelFolderReader.Read(_builder.EmbeddingFolder(clsPooling: true, normalize: false));

        Assert.False(cls.MeanPooling);
        Assert.False(cls.NormalizeEmbeddings);
    }

    [Fact]
    public void Normalization_declared_in_modules_json_is_honoured()
    {
        // all-MiniLM-L6-v2 and friends ship no 2_Normalize folder, only the module list.
        var folder = OnnxModelFolderReader.Read(_builder.EmbeddingFolderWithModulesJson());

        Assert.True(folder.NormalizeEmbeddings);
    }

    [Fact]
    public void A_graph_file_path_resolves_to_its_folder()
    {
        var directory = _builder.EmbeddingFolder();

        var folder = OnnxModelFolderReader.Read(Path.Combine(directory, "onnx", "model.onnx"));

        // Pointing at the graph rather than the folder is a normal import mistake; it must still work.
        Assert.Equal(OnnxModelKind.Embedding, folder.Kind);
    }

    [Fact]
    public void A_folder_with_no_model_is_rejected_with_a_clear_message()
    {
        var empty = _builder.EmptyFolder();

        var ex = Assert.Throws<OnnxFormatException>(() => OnnxModelFolderReader.Read(empty));

        Assert.Contains("genai_config.json", ex.Message, StringComparison.Ordinal);
        Assert.Null(OnnxModelFolderReader.TryRead(empty));
        Assert.False(OnnxModelFolderReader.IsOnnxModel(empty));
    }

    [Fact]
    public void A_graph_without_a_tokenizer_is_rejected()
    {
        var directory = _builder.EmptyFolder();
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), new byte[128]);

        var ex = Assert.Throws<OnnxFormatException>(() => OnnxModelFolderReader.Read(directory));

        Assert.Contains("tokenizer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cpu-int4-rtn-block-32", "model.onnx", "int4")]
    [InlineData("model-dir", "model_fp16.onnx", "fp16")]
    [InlineData("model-dir", "model_quantized.onnx", "int8")]
    [InlineData("model-dir", "model.onnx", null)]
    public void Quantization_is_inferred_from_the_folder_or_file_name(string directory, string file, string? expected)
    {
        Assert.Equal(expected, OnnxModelFolderReader.DetectQuantization(directory, file));
    }

    public void Dispose()
    {
        _builder.Dispose();
        GC.SuppressFinalize(this);
    }
}
