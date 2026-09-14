using System.Globalization;
using System.Text.Json;

namespace NetCoreAI.Backends.Onnx;

/// <summary>How an ONNX folder on disk is meant to be run.</summary>
public enum OnnxModelKind
{
    /// <summary>ONNX Runtime GenAI folder: a genai_config.json next to the decoder and its tokenizer.</summary>
    Generative,

    /// <summary>A plain ONNX encoder (sentence-transformers export) served through ONNX Runtime with pooling.</summary>
    Embedding,
}

/// <summary>
/// What an ONNX model folder says about itself, read from genai_config.json / config.json alone.
/// Nothing here loads weights, so the Hub, the importer and the fit estimator can use it cheaply.
/// </summary>
public sealed record OnnxModelFolder
{
    /// <summary>Absolute path of the folder holding the model.</summary>
    public required string Directory { get; init; }

    public required OnnxModelKind Kind { get; init; }

    /// <summary>Architecture family as the config records it: qwen2, phi3, llama, bert...</summary>
    public string? ModelType { get; init; }

    /// <summary>Maximum context in tokens.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Hidden size; also the embedding dimension for encoder models.</summary>
    public int? HiddenSize { get; init; }

    public int? LayerCount { get; init; }

    public int? HeadCount { get; init; }

    /// <summary>Key/value heads. Fewer than <see cref="HeadCount"/> means grouped-query attention and a much smaller KV cache.</summary>
    public int? HeadCountKv { get; init; }

    /// <summary>Attention head dimension as recorded, else hidden size / heads.</summary>
    public int? HeadSize { get; init; }

    public int? VocabSize { get; init; }

    /// <summary>Quantization label inferred from the folder or graph file name, e.g. int4, fp16.</summary>
    public string? Quantization { get; init; }

    /// <summary>The decoder (or encoder) graph file, absolute.</summary>
    public string? GraphPath { get; init; }

    /// <summary>Bytes of every .onnx graph and external-data file in the folder.</summary>
    public long GraphBytes { get; init; }

    /// <summary>Generation defaults the model ships in the "search" block of genai_config.json.</summary>
    public OnnxSearchDefaults SearchDefaults { get; init; } = new();

    /// <summary>
    /// The Jinja chat template shipped with the folder, from tokenizer_config.json or a chat_template.jinja
    /// beside it. ONNX Runtime GenAI only reads the former, so the latter is passed in explicitly.
    /// </summary>
    public string? ChatTemplate { get; init; }

    /// <summary>True when a chat template is available for rendering conversations.</summary>
    public bool HasChatTemplate => ChatTemplate is { Length: > 0 };

    /// <summary>Sentence-transformers exports declare their pooling and normalization; defaults are mean + normalize.</summary>
    public bool MeanPooling { get; init; } = true;

    public bool NormalizeEmbeddings { get; init; } = true;

    /// <summary>Capabilities implied by the folder contents.</summary>
    public ModelCapabilities ToCapabilities() => Kind switch
    {
        OnnxModelKind.Embedding => new ModelCapabilities(ModelCapability.Embeddings, ContextLength, HiddenSize),

        // ONNX Runtime GenAI streams tokens but has no native tool calling, and JSON guidance depends on
        // how the runtime was built, so neither is advertised here.
        _ => new ModelCapabilities(ModelCapability.Chat | ModelCapability.Streaming, ContextLength),
    };
}

/// <summary>Sampling defaults recorded in genai_config.json.</summary>
public sealed record OnnxSearchDefaults
{
    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

    public float? RepetitionPenalty { get; init; }

    /// <summary>Total prompt + generated tokens the model config allows.</summary>
    public int? MaxLength { get; init; }
}

/// <summary>Thrown when a folder is not a usable ONNX model.</summary>
public sealed class OnnxFormatException(string message) : NetCoreAIException(message);

/// <summary>
/// Reads an ONNX model folder. A genai_config.json makes it a generative model for ONNX Runtime GenAI;
/// otherwise an .onnx graph plus a tokenizer makes it a sentence-transformers style embedding model.
/// </summary>
public static class OnnxModelFolderReader
{
    /// <summary>Graph file names tried in order when no config names one.</summary>
    private static readonly string[] EncoderCandidates =
    [
        "onnx/model.onnx",
        "model.onnx",
        "onnx/model_quantized.onnx",
        "onnx/model_int8.onnx",
        "onnx/model_fp16.onnx",
        "model_quantized.onnx",
    ];

    private static readonly string[] TokenizerFiles = ["vocab.txt", "tokenizer.json"];

    private static readonly (string Needle, string Label)[] QuantizationLabels =
    [
        ("int4", "int4"), ("q4", "int4"), ("int8", "int8"), ("uint8", "int8"), ("qint8", "int8"),
        ("quantized", "int8"), ("fp16", "fp16"), ("float16", "fp16"), ("fp32", "fp32"), ("float32", "fp32"),
    ];

    /// <summary>True when the path looks like an ONNX model folder (or a graph file inside one).</summary>
    public static bool IsOnnxModel(string path) => TryRead(path) is not null;

    /// <summary>Reads the folder, returning null when it holds no ONNX model.</summary>
    public static OnnxModelFolder? TryRead(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception ex) when (ex is OnnxFormatException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Reads the folder, or the folder containing the given .onnx file.</summary>
    /// <exception cref="OnnxFormatException">The path holds no ONNX model.</exception>
    public static OnnxModelFolder Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = System.IO.Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (directory is null || !System.IO.Directory.Exists(directory))
        {
            throw new OnnxFormatException($"{path} is not an ONNX model folder.");
        }

        directory = ModelRoot(Path.GetFullPath(directory));
        var genaiConfig = Path.Combine(directory, "genai_config.json");
        return File.Exists(genaiConfig)
            ? ReadGenerative(directory, genaiConfig)
            : ReadEmbedding(directory);
    }

    /// <summary>
    /// The folder that actually describes the model. Exports keep the graph in an "onnx" subfolder while the
    /// config and tokenizer stay in the parent, so a path pointing at the graph walks up to find them.
    /// </summary>
    private static string ModelRoot(string directory)
    {
        var current = directory;
        for (var level = 0; level < 2; level++)
        {
            if (File.Exists(Path.Combine(current, "genai_config.json")) || FindTokenizerFile(current) is not null)
            {
                return current;
            }

            if (Path.GetDirectoryName(current) is not { Length: > 0 } parent || !System.IO.Directory.Exists(parent))
            {
                break;
            }

            current = parent;
        }

        return directory;
    }

    private static OnnxModelFolder ReadGenerative(string directory, string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var model = Property(root, "model");
        var decoder = model is { } m ? Property(m, "decoder") : null;
        var search = Property(root, "search");

        var filename = decoder is { } d ? String(d, "filename") : null;
        var declaredGraph = filename is { Length: > 0 } ? Path.Combine(directory, filename) : null;
        var graph = declaredGraph is not null && File.Exists(declaredGraph) ? declaredGraph : FindGraph(directory);

        var heads = decoder is { } dh ? Int(dh, "num_attention_heads") : null;
        var hidden = decoder is { } dhi ? Int(dhi, "hidden_size") : null;

        return new OnnxModelFolder
        {
            Directory = directory,
            Kind = OnnxModelKind.Generative,
            ModelType = model is { } mt ? String(mt, "type") : null,
            ContextLength = model is { } mc ? Int(mc, "context_length") : null,
            HiddenSize = hidden,
            LayerCount = decoder is { } dl ? Int(dl, "num_hidden_layers") : null,
            HeadCount = heads,
            HeadCountKv = (decoder is { } dk ? Int(dk, "num_key_value_heads") : null) ?? heads,
            HeadSize = (decoder is { } ds ? Int(ds, "head_size") : null)
                ?? (hidden is { } h && heads is > 0 ? h / heads.Value : null),
            VocabSize = model is { } mv ? Int(mv, "vocab_size") : null,
            Quantization = DetectQuantization(directory, graph),
            GraphPath = graph,
            GraphBytes = MeasureGraphBytes(directory),
            ChatTemplate = ReadChatTemplate(directory),
            SearchDefaults = new OnnxSearchDefaults
            {
                Temperature = search is { } s ? Float(s, "temperature") : null,
                TopP = search is { } sp ? Float(sp, "top_p") : null,
                TopK = search is { } sk ? Int(sk, "top_k") : null,
                RepetitionPenalty = search is { } sr ? Float(sr, "repetition_penalty") : null,
                MaxLength = search is { } sm ? Int(sm, "max_length") : null,
            },
        };
    }

    private static OnnxModelFolder ReadEmbedding(string directory)
    {
        var graph = FindGraph(directory)
            ?? throw new OnnxFormatException(
                $"{directory} holds no genai_config.json and no .onnx graph. Import a folder produced by ONNX Runtime GenAI model builder or a sentence-transformers ONNX export.");

        if (FindTokenizerFile(directory) is null)
        {
            throw new OnnxFormatException(
                $"{directory} has an ONNX graph but no tokenizer (vocab.txt or tokenizer.json), so text cannot be encoded for it.");
        }

        int? hidden = null;
        int? context = null;
        string? modelType = null;
        var configPath = Path.Combine(directory, "config.json");
        if (File.Exists(configPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            hidden = Int(document.RootElement, "hidden_size");
            context = Int(document.RootElement, "max_position_embeddings");
            modelType = String(document.RootElement, "model_type");
        }

        var (mean, normalize) = ReadPooling(directory);
        return new OnnxModelFolder
        {
            Directory = directory,
            Kind = OnnxModelKind.Embedding,
            ModelType = modelType,
            ContextLength = context,
            HiddenSize = hidden,
            Quantization = DetectQuantization(directory, graph),
            GraphPath = graph,
            GraphBytes = MeasureGraphBytes(directory),
            MeanPooling = mean,
            NormalizeEmbeddings = normalize,
        };
    }

    /// <summary>Reads the sentence-transformers pooling module, defaulting to mean pooling + L2 normalization.</summary>
    private static (bool Mean, bool Normalize) ReadPooling(string directory)
    {
        var pooling = Path.Combine(directory, "1_Pooling", "config.json");
        if (!File.Exists(pooling))
        {
            return (true, true);
        }

        var mean = true;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pooling));
            if (Bool(document.RootElement, "pooling_mode_cls_token") == true)
            {
                mean = false;
            }
        }
        catch (JsonException)
        {
            // A malformed pooling module is not worth failing the import over; mean pooling is the common case.
        }

        return (mean, NormalizesEmbeddings(directory));
    }

    /// <summary>
    /// Sentence-transformers declares normalization as a pipeline module: either a "2_Normalize" folder or a
    /// Normalize entry in modules.json. Without either, the encoder output is used as it comes out.
    /// </summary>
    private static bool NormalizesEmbeddings(string directory)
    {
        if (System.IO.Directory.Exists(Path.Combine(directory, "2_Normalize")))
        {
            return true;
        }

        var modules = Path.Combine(directory, "modules.json");
        if (!File.Exists(modules))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modules));
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().Any(m =>
                    String(m, "type")?.EndsWith("Normalize", StringComparison.Ordinal) == true);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FindGraph(string directory)
    {
        foreach (var candidate in EncoderCandidates)
        {
            var path = Path.Combine(directory, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return path;
            }
        }

        return System.IO.Directory
            .EnumerateFiles(directory, "*.onnx", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
    }

    /// <summary>The tokenizer file an encoder model needs; null when the folder has none.</summary>
    internal static string? FindTokenizerFile(string directory)
    {
        foreach (var name in TokenizerFiles)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Graph plus external-data bytes: what the runtime will have to map into memory.</summary>
    private static long MeasureGraphBytes(string directory)
    {
        long total = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".onnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".data", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
            }
        }

        return total;
    }

    /// <summary>
    /// The chat template, preferring tokenizer_config.json (which the runtime reads itself) and falling back
    /// to the chat_template.jinja file that newer Hugging Face exports ship alongside it.
    /// </summary>
    private static string? ReadChatTemplate(string directory)
    {
        var config = Path.Combine(directory, "tokenizer_config.json");
        if (File.Exists(config))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(config));
                if (String(document.RootElement, "chat_template") is { Length: > 0 } template)
                {
                    return template;
                }
            }
            catch (JsonException)
            {
                // Fall through to the standalone template file.
            }
        }

        var jinja = Path.Combine(directory, "chat_template.jinja");
        try
        {
            return File.Exists(jinja) ? File.ReadAllText(jinja) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Quantization label from the folder or graph file name; ONNX folders encode it there by convention.</summary>
    internal static string? DetectQuantization(string directory, string? graphPath)
    {
        var haystack = string.Concat(directory, "/", graphPath is null ? string.Empty : Path.GetFileName(graphPath))
            .ToLowerInvariant();

        foreach (var (needle, label) in QuantizationLabels)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return label;
            }
        }

        return null;
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static int? Int(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var result) ? result : null;

    private static float? Float(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var result)
            ? (float)result
            : null;

    private static bool? Bool(JsonElement element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Formats a byte count for the memory-estimate explanation.</summary>
    internal static string Megabytes(long bytes) =>
        (bytes / 1_048_576).ToString(CultureInfo.InvariantCulture);
}
