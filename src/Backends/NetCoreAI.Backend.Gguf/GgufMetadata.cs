using System.Globalization;

namespace NetCoreAI.Backends.Gguf;

/// <summary>
/// What the GGUF header says about a model. Everything here is read from the file header alone,
/// without loading weights, so the Hub, the importer and the fit estimator can use it cheaply.
/// </summary>
public sealed record GgufMetadata
{
    /// <summary>Architecture family: llama, qwen2, phi3, gemma2, bert, nomic-bert...</summary>
    public required string Architecture { get; init; }

    public string? Name { get; init; }

    /// <summary>Training context length in tokens.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Hidden size; also the embedding dimension for embedding models.</summary>
    public int? EmbeddingLength { get; init; }

    /// <summary>Transformer layers, needed to size the KV cache.</summary>
    public int? BlockCount { get; init; }

    public int? HeadCount { get; init; }

    /// <summary>Key/value heads. Fewer than <see cref="HeadCount"/> means grouped-query attention and a much smaller KV cache.</summary>
    public int? HeadCountKv { get; init; }

    /// <summary>Quantization label derived from general.file_type, e.g. Q4_K_M.</summary>
    public string? Quantization { get; init; }

    /// <summary>Jinja chat template embedded in the file, when present.</summary>
    public string? ChatTemplate { get; init; }

    public string? License { get; init; }

    /// <summary>Parameter count when the file records it (general.parameter_count or a size label such as "1.5B").</summary>
    public long? ParameterCount { get; init; }

    /// <summary>True when the file looks like an embedding model rather than a generative one.</summary>
    public bool IsEmbeddingModel { get; init; }

    public int TensorCount { get; init; }

    public uint Version { get; init; }

    /// <summary>Every key/value pair read from the header, values rendered as strings.</summary>
    public IReadOnlyDictionary<string, string> Raw { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Head dimension used for KV-cache sizing; falls back to embedding/heads.</summary>
    public int? HeadDimension =>
        EmbeddingLength is { } e && HeadCount is > 0 ? e / HeadCount!.Value : null;

    /// <summary>Capabilities implied by the header.</summary>
    public ModelCapabilities ToCapabilities()
    {
        if (IsEmbeddingModel)
        {
            return new ModelCapabilities(ModelCapability.Embeddings, ContextLength, EmbeddingLength);
        }

        // llama.cpp constrains output with GBNF, so structured output and JSON mode are always available.
        var flags = ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.JsonMode | ModelCapability.StructuredOutput;

        // Tool calling needs a template that renders tool definitions; that is what the "tools" placeholder means.
        if (ChatTemplate is { } t && t.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            flags |= ModelCapability.ToolCalling;
        }

        return new ModelCapabilities(flags, ContextLength);
    }
}

/// <summary>Thrown when a file is not a readable GGUF container.</summary>
public sealed class GgufFormatException(string message) : NetCoreAIException(message);

/// <summary>
/// Streaming reader for the GGUF header (magic, version, tensor count, key/value block).
/// Reads only as far as the metadata requires, so it is cheap even on a 40 GB file.
/// Format reference: https://github.com/ggml-org/ggml/blob/master/docs/gguf.md
/// </summary>
public static class GgufMetadataReader
{
    private const uint Magic = 0x46554747; // "GGUF" little-endian
    private const int MaxStringBytes = 16 * 1024 * 1024;

    /// <summary>Architectures that produce embeddings rather than text.</summary>
    private static readonly HashSet<string> EmbeddingArchitectures = new(StringComparer.OrdinalIgnoreCase)
    {
        "bert", "nomic-bert", "nomic-bert-moe", "jina-bert-v2", "jina-bert-v3", "gte", "t5encoder", "roberta-bert", "modern-bert",
    };

    /// <summary>general.file_type values, from llama.cpp's llama_ftype enum.</summary>
    private static readonly Dictionary<uint, string> FileTypes = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [4] = "Q4_1_F16", [7] = "Q8_0", [8] = "Q5_0", [9] = "Q5_1",
        [10] = "Q2_K", [11] = "Q3_K_S", [12] = "Q3_K_M", [13] = "Q3_K_L", [14] = "Q4_K_S", [15] = "Q4_K_M",
        [16] = "Q5_K_S", [17] = "Q5_K_M", [18] = "Q6_K", [19] = "IQ2_XXS", [20] = "IQ2_XS", [21] = "Q2_K_S",
        [22] = "IQ3_XS", [23] = "IQ3_XXS", [24] = "IQ1_S", [25] = "IQ4_NL", [26] = "IQ3_S", [27] = "IQ3_M",
        [28] = "IQ2_S", [29] = "IQ2_M", [30] = "IQ4_XS", [31] = "IQ1_M", [32] = "BF16", [36] = "TQ1_0", [37] = "TQ2_0",
    };

    public static bool IsGgufFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 && BitConverter.ToUInt32(magic) == Magic;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static GgufMetadata Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, FileOptions.SequentialScan);
        var metadata = Read(stream);
        // The filename is often the only place the quantization appears (older converters omit general.file_type).
        return metadata.Quantization is null
            ? metadata with { Quantization = QuantizationFromFileName(Path.GetFileNameWithoutExtension(path)) }
            : metadata;
    }

    public static GgufMetadata Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
        {
            throw new GgufFormatException("Not a GGUF file: the magic bytes are missing. Re-download the file or check it is not an LFS pointer.");
        }

        var version = reader.ReadUInt32();
        if (version is < 1 or > 3)
        {
            throw new GgufFormatException($"GGUF version {version} is not supported (this build reads versions 1 to 3).");
        }

        // Version 1 stored counts and lengths as 32-bit; 2 and 3 use 64-bit.
        var wide = version >= 2;
        var tensorCount = wide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
        var kvCount = wide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
        if (kvCount is < 0 or > 100_000)
        {
            throw new GgufFormatException($"GGUF header claims {kvCount} metadata entries, which is implausible; the file is likely truncated.");
        }

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0L; i < kvCount; i++)
        {
            try
            {
                var key = ReadString(reader, wide);
                raw[key] = ReadValue(reader, wide);
            }
            catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or OverflowException)
            {
                // Any read running off the end means the declared entry count does not match the bytes present.
                throw new GgufFormatException($"GGUF header ended after {i} of {kvCount} metadata entries; the file is truncated.");
            }
        }

        return Build(raw, tensorCount, version);
    }

    private static GgufMetadata Build(Dictionary<string, string> raw, long tensorCount, uint version)
    {
        var architecture = raw.GetValueOrDefault("general.architecture") ?? "unknown";

        int? Arch(string suffix) => Int(raw.GetValueOrDefault($"{architecture}.{suffix}"));

        var pooling = raw.ContainsKey($"{architecture}.pooling_type");
        var chatTemplate = raw.GetValueOrDefault("tokenizer.chat_template");
        var isEmbedding = pooling || EmbeddingArchitectures.Contains(architecture) || (chatTemplate is null && raw.ContainsKey($"{architecture}.attention.causal") && raw[$"{architecture}.attention.causal"] == "False");

        string? quantization = null;
        if (Int(raw.GetValueOrDefault("general.file_type")) is { } ft && FileTypes.TryGetValue((uint)ft, out var label))
        {
            quantization = label;
        }

        return new GgufMetadata
        {
            Architecture = architecture,
            Name = raw.GetValueOrDefault("general.name"),
            ContextLength = Arch("context_length"),
            EmbeddingLength = Arch("embedding_length"),
            BlockCount = Arch("block_count"),
            HeadCount = Arch("attention.head_count"),
            HeadCountKv = Arch("attention.head_count_kv") ?? Arch("attention.head_count"),
            Quantization = quantization,
            ChatTemplate = chatTemplate,
            License = raw.GetValueOrDefault("general.license"),
            ParameterCount = Long(raw.GetValueOrDefault("general.parameter_count")) ?? ParseSizeLabel(raw.GetValueOrDefault("general.size_label")),
            IsEmbeddingModel = isEmbedding,
            TensorCount = (int)Math.Min(tensorCount, int.MaxValue),
            Version = version,
            Raw = raw,
        };
    }

    private static string ReadValue(BinaryReader reader, bool wide)
    {
        var type = reader.ReadUInt32();
        return type switch
        {
            0 => reader.ReadByte().ToString(CultureInfo.InvariantCulture),
            1 => reader.ReadSByte().ToString(CultureInfo.InvariantCulture),
            2 => reader.ReadUInt16().ToString(CultureInfo.InvariantCulture),
            3 => reader.ReadInt16().ToString(CultureInfo.InvariantCulture),
            4 => reader.ReadUInt32().ToString(CultureInfo.InvariantCulture),
            5 => reader.ReadInt32().ToString(CultureInfo.InvariantCulture),
            6 => reader.ReadSingle().ToString(CultureInfo.InvariantCulture),
            7 => (reader.ReadByte() != 0).ToString(),
            8 => ReadString(reader, wide),
            9 => ReadArray(reader, wide),
            10 => reader.ReadUInt64().ToString(CultureInfo.InvariantCulture),
            11 => reader.ReadInt64().ToString(CultureInfo.InvariantCulture),
            12 => reader.ReadDouble().ToString(CultureInfo.InvariantCulture),
            _ => throw new GgufFormatException($"Unknown GGUF value type {type}."),
        };
    }

    /// <summary>
    /// Arrays are skipped rather than materialised: the only large ones are the tokenizer vocabulary
    /// (hundreds of thousands of strings) and nothing in the header summary needs them. Short arrays are
    /// rendered so callers can still read things like rope scaling factors.
    /// </summary>
    private static string ReadArray(BinaryReader reader, bool wide)
    {
        var itemType = reader.ReadUInt32();
        var length = wide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
        const int renderLimit = 16;

        if (itemType is 8 or 9)
        {
            // Strings (and nested arrays) have to be walked one by one to find the end.
            var rendered = new List<string>();
            for (var i = 0L; i < length; i++)
            {
                var value = itemType == 8 ? ReadString(reader, wide) : ReadArray(reader, wide);
                if (i < renderLimit)
                {
                    rendered.Add(value);
                }
            }

            return length <= renderLimit ? string.Join(',', rendered) : $"[{length} items]";
        }

        var itemSize = FixedSize(itemType);
        if (length <= renderLimit)
        {
            var rendered = new List<string>((int)length);
            for (var i = 0L; i < length; i++)
            {
                rendered.Add(ReadFixed(reader, itemType));
            }

            return string.Join(',', rendered);
        }

        Skip(reader, checked(length * itemSize));
        return $"[{length} items]";
    }

    private static int FixedSize(uint type) => type switch
    {
        0 or 1 or 7 => 1,
        2 or 3 => 2,
        4 or 5 or 6 => 4,
        10 or 11 or 12 => 8,
        _ => throw new GgufFormatException($"Unknown GGUF array item type {type}."),
    };

    private static string ReadFixed(BinaryReader reader, uint type) => type switch
    {
        0 => reader.ReadByte().ToString(CultureInfo.InvariantCulture),
        1 => reader.ReadSByte().ToString(CultureInfo.InvariantCulture),
        2 => reader.ReadUInt16().ToString(CultureInfo.InvariantCulture),
        3 => reader.ReadInt16().ToString(CultureInfo.InvariantCulture),
        4 => reader.ReadUInt32().ToString(CultureInfo.InvariantCulture),
        5 => reader.ReadInt32().ToString(CultureInfo.InvariantCulture),
        6 => reader.ReadSingle().ToString(CultureInfo.InvariantCulture),
        7 => (reader.ReadByte() != 0).ToString(),
        10 => reader.ReadUInt64().ToString(CultureInfo.InvariantCulture),
        11 => reader.ReadInt64().ToString(CultureInfo.InvariantCulture),
        12 => reader.ReadDouble().ToString(CultureInfo.InvariantCulture),
        _ => throw new GgufFormatException($"Unknown GGUF array item type {type}."),
    };

    private static string ReadString(BinaryReader reader, bool wide)
    {
        var length = wide ? (long)reader.ReadUInt64() : reader.ReadUInt32();
        if (length is < 0 or > MaxStringBytes)
        {
            throw new GgufFormatException($"GGUF string length {length} is out of range; the file is corrupt.");
        }

        var bytes = reader.ReadBytes((int)length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static void Skip(BinaryReader reader, long count)
    {
        if (reader.BaseStream.CanSeek)
        {
            reader.BaseStream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> scratch = stackalloc byte[4096];
        while (count > 0)
        {
            var take = (int)Math.Min(count, scratch.Length);
            var read = reader.BaseStream.Read(scratch[..take]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            count -= read;
        }
    }

    private static int? Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Turns a size label such as "1.5B" or "270M" into a parameter count.</summary>
    internal static long? ParseSizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var text = label.Trim();
        var multiplier = char.ToUpperInvariant(text[^1]) switch
        {
            'B' => 1_000_000_000L,
            'M' => 1_000_000L,
            'K' => 1_000L,
            _ => 0L,
        };

        return multiplier > 0 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (long)(number * multiplier)
            : null;
    }

    /// <summary>Recovers a quantization label from a filename such as qwen2.5-0.5b-instruct-q4_k_m.gguf.</summary>
    internal static string? QuantizationFromFileName(string fileName)
    {
        var upper = fileName.ToUpperInvariant();
        // Longest first so Q4_K_M wins over Q4_K and Q4.
        foreach (var candidate in FileTypes.Values.Concat(["IQ4_XS", "IQ4_NL"]).Distinct().OrderByDescending(v => v.Length))
        {
            if (upper.Contains(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }
}
