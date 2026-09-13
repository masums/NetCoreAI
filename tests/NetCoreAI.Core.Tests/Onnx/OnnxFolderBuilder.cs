using System.Text;
using System.Text.Json;

namespace NetCoreAI.Core.Tests.Onnx;

/// <summary>
/// Writes throwaway ONNX model folders — a genai_config.json generative layout or a sentence-transformers
/// encoder layout — so the folder reader and the provider can be tested without downloading weights.
/// </summary>
internal sealed class OnnxFolderBuilder : IDisposable
{
    private readonly List<string> _roots = [];

    /// <summary>An ONNX Runtime GenAI folder shaped like a Qwen2.5 0.5B int4 export.</summary>
    public string GenerativeFolder(
        int contextLength = 32768,
        int layers = 24,
        int heads = 14,
        int kvHeads = 2,
        int headSize = 64,
        string? chatTemplate = "{% for m in messages %}<|im_start|>{{ m.role }}\n{{ m.content }}<|im_end|>\n{% endfor %}<|im_start|>assistant\n",
        bool templateInTokenizerConfig = true,
        string folderName = "cpu-int4-rtn-block-32")
    {
        var directory = CreateRoot(folderName);
        var config = new
        {
            model = new
            {
                bos_token_id = 151643,
                context_length = contextLength,
                eos_token_id = new[] { 151645, 151643 },
                pad_token_id = 151643,
                type = "qwen2",
                vocab_size = 151936,
                decoder = new
                {
                    filename = "model.onnx",
                    head_size = headSize,
                    hidden_size = heads * headSize,
                    num_attention_heads = heads,
                    num_hidden_layers = layers,
                    num_key_value_heads = kvHeads,
                },
            },
            search = new
            {
                do_sample = true,
                max_length = contextLength,
                repetition_penalty = 1.1,
                temperature = 0.7,
                top_k = 40,
                top_p = 0.95,
            },
        };

        File.WriteAllText(Path.Combine(directory, "genai_config.json"), JsonSerializer.Serialize(config));

        // A stand-in graph: the reader only measures it, and nothing in these tests loads the runtime.
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(directory, "model.onnx.data"), new byte[1_048_576]);

        if (chatTemplate is null)
        {
            File.WriteAllText(Path.Combine(directory, "tokenizer_config.json"), "{}");
        }
        else if (templateInTokenizerConfig)
        {
            File.WriteAllText(
                Path.Combine(directory, "tokenizer_config.json"),
                JsonSerializer.Serialize(new { chat_template = chatTemplate }));
        }
        else
        {
            // Newer Hugging Face exports keep the template in its own file, which the runtime does not read.
            File.WriteAllText(Path.Combine(directory, "tokenizer_config.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "chat_template.jinja"), chatTemplate);
        }

        return directory;
    }

    /// <summary>A sentence-transformers ONNX export: graph under onnx/, WordPiece vocab and pooling module.</summary>
    public string EmbeddingFolder(int hiddenSize = 384, int maxPositions = 512, bool normalize = true, bool clsPooling = false)
    {
        var directory = CreateRoot("all-MiniLM-L6-v2");
        Directory.CreateDirectory(Path.Combine(directory, "onnx"));
        File.WriteAllBytes(Path.Combine(directory, "onnx", "model.onnx"), new byte[90_000]);

        File.WriteAllText(
            Path.Combine(directory, "config.json"),
            JsonSerializer.Serialize(new { hidden_size = hiddenSize, max_position_embeddings = maxPositions, model_type = "bert" }));

        File.WriteAllText(Path.Combine(directory, "vocab.txt"), Vocabulary());
        File.WriteAllText(
            Path.Combine(directory, "tokenizer_config.json"),
            JsonSerializer.Serialize(new { do_lower_case = true, cls_token = "[CLS]", sep_token = "[SEP]", pad_token = "[PAD]", unk_token = "[UNK]" }));

        Directory.CreateDirectory(Path.Combine(directory, "1_Pooling"));
        File.WriteAllText(
            Path.Combine(directory, "1_Pooling", "config.json"),
            JsonSerializer.Serialize(new { pooling_mode_cls_token = clsPooling, pooling_mode_mean_tokens = !clsPooling }));

        if (normalize)
        {
            Directory.CreateDirectory(Path.Combine(directory, "2_Normalize"));
        }

        return directory;
    }

    /// <summary>An encoder export that declares its pipeline in modules.json instead of numbered folders.</summary>
    public string EmbeddingFolderWithModulesJson()
    {
        var directory = EmbeddingFolder(normalize: false);
        File.WriteAllText(
            Path.Combine(directory, "modules.json"),
            JsonSerializer.Serialize(new[]
            {
                new { idx = 0, name = "0", path = string.Empty, type = "sentence_transformers.models.Transformer" },
                new { idx = 1, name = "1", path = "1_Pooling", type = "sentence_transformers.models.Pooling" },
                new { idx = 2, name = "2", path = "2_Normalize", type = "sentence_transformers.models.Normalize" },
            }));

        return directory;
    }

    /// <summary>A folder with neither a GenAI config nor a graph.</summary>
    public string EmptyFolder() => CreateRoot("empty");

    private static string Vocabulary()
    {
        var sb = new StringBuilder();
        foreach (var token in (string[])["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "hello", "world", "net", "##core"])
        {
            sb.AppendLine(token);
        }

        return sb.ToString();
    }

    private string CreateRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _roots.Clear();
    }
}
