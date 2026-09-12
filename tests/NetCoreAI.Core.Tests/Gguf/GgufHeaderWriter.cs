using System.Text;

namespace NetCoreAI.Core.Tests.Gguf;

/// <summary>
/// Writes synthetic GGUF headers so the metadata reader can be tested without shipping model weights.
/// Mirrors the container format: magic, version, counts, then typed key/value pairs.
/// </summary>
internal sealed class GgufHeaderWriter
{
    private readonly List<(string Key, Action<BinaryWriter> Write)> _entries = [];

    public uint Version { get; set; } = 3;

    public long TensorCount { get; set; } = 291;

    /// <summary>Corrupts the header by claiming more entries than are written, to test truncation handling.</summary>
    public long? OverstateKvCount { get; set; }

    public GgufHeaderWriter String(string key, string value) => Add(key, w =>
    {
        w.Write(8u);
        WriteString(w, value);
    });

    public GgufHeaderWriter UInt32(string key, uint value) => Add(key, w =>
    {
        w.Write(4u);
        w.Write(value);
    });

    public GgufHeaderWriter UInt64(string key, ulong value) => Add(key, w =>
    {
        w.Write(10u);
        w.Write(value);
    });

    public GgufHeaderWriter Float(string key, float value) => Add(key, w =>
    {
        w.Write(6u);
        w.Write(value);
    });

    public GgufHeaderWriter Bool(string key, bool value) => Add(key, w =>
    {
        w.Write(7u);
        w.Write((byte)(value ? 1 : 0));
    });

    /// <summary>A string array, like a tokenizer vocabulary: the reader must skip it without materialising it.</summary>
    public GgufHeaderWriter StringArray(string key, params string[] values) => Add(key, w =>
    {
        w.Write(9u);
        w.Write(8u);
        w.Write((ulong)values.Length);
        foreach (var value in values)
        {
            WriteString(w, value);
        }
    });

    public GgufHeaderWriter Int32Array(string key, params int[] values) => Add(key, w =>
    {
        w.Write(9u);
        w.Write(5u);
        w.Write((ulong)values.Length);
        foreach (var value in values)
        {
            w.Write(value);
        }
    });

    private GgufHeaderWriter Add(string key, Action<BinaryWriter> write)
    {
        _entries.Add((key, write));
        return this;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46554747u);          // "GGUF"
            writer.Write(Version);
            writer.Write((ulong)TensorCount);
            writer.Write((ulong)(OverstateKvCount ?? _entries.Count));
            foreach (var (key, write) in _entries)
            {
                WriteString(writer, key);
                write(writer);
            }

            // Trailing bytes stand in for the tensor-info block; the reader must stop before them.
            writer.Write(new byte[64]);
        }

        return stream.ToArray();
    }

    public Stream ToStream() => new MemoryStream(ToBytes());

    /// <summary>Writes the header to a temporary .gguf file and returns its path.</summary>
    public string ToFile(string? name = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, (name ?? "model") + ".gguf");
        File.WriteAllBytes(path, ToBytes());
        return path;
    }

    /// <summary>A realistic small chat model header (Qwen2-shaped).</summary>
    public static GgufHeaderWriter ChatModel() => new GgufHeaderWriter()
        .String("general.architecture", "qwen2")
        .String("general.name", "Qwen2.5 0.5B Instruct")
        .String("general.size_label", "0.5B")
        .String("general.license", "apache-2.0")
        .UInt32("general.file_type", 15)              // Q4_K_M
        .UInt32("qwen2.context_length", 32768)
        .UInt32("qwen2.embedding_length", 896)
        .UInt32("qwen2.block_count", 24)
        .UInt32("qwen2.attention.head_count", 14)
        .UInt32("qwen2.attention.head_count_kv", 2)
        .Float("qwen2.rope.freq_base", 1000000f)
        .String("tokenizer.ggml.model", "gpt2")
        .StringArray("tokenizer.ggml.tokens", Enumerable.Range(0, 64).Select(i => "tok" + i).ToArray())
        .String("tokenizer.chat_template", "{% for message in messages %}<|im_start|>{{ message.role }}\n{{ message.content }}<|im_end|>\n{% endfor %}");

    /// <summary>A realistic embedding model header (BERT-shaped, with a pooling type).</summary>
    public static GgufHeaderWriter EmbeddingModel() => new GgufHeaderWriter()
        .String("general.architecture", "nomic-bert")
        .String("general.name", "nomic-embed-text-v1.5")
        .UInt32("general.file_type", 7)               // Q8_0
        .UInt32("nomic-bert.context_length", 2048)
        .UInt32("nomic-bert.embedding_length", 768)
        .UInt32("nomic-bert.block_count", 12)
        .UInt32("nomic-bert.attention.head_count", 12)
        .UInt32("nomic-bert.pooling_type", 1);
}
