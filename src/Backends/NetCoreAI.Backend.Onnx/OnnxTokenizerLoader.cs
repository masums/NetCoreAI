using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace NetCoreAI.Backends.Onnx;

/// <summary>
/// Builds a <see cref="Tokenizer"/> for a sentence-transformers ONNX export from whatever the folder ships:
/// a WordPiece vocab.txt, or the vocabulary embedded in a tokenizer.json.
/// </summary>
internal static class OnnxTokenizerLoader
{
    /// <summary>Loads the tokenizer for an encoder model folder.</summary>
    /// <exception cref="OnnxFormatException">The folder has no tokenizer this backend can build.</exception>
    public static BertTokenizer Load(string directory)
    {
        var options = ReadOptions(directory);

        var vocabTxt = Path.Combine(directory, "vocab.txt");
        if (File.Exists(vocabTxt))
        {
            return BertTokenizer.Create(vocabTxt, options);
        }

        var tokenizerJson = Path.Combine(directory, "tokenizer.json");
        if (File.Exists(tokenizerJson))
        {
            using var vocabulary = ReadVocabularyFromTokenizerJson(tokenizerJson, directory);
            return BertTokenizer.Create(vocabulary, options);
        }

        throw new OnnxFormatException(
            $"{directory} has no vocab.txt or tokenizer.json, so its text cannot be encoded. Re-export the model with its tokenizer files.");
    }

    /// <summary>Special tokens and casing come from tokenizer_config.json when it is present.</summary>
    private static BertOptions ReadOptions(string directory)
    {
        var options = new BertOptions();
        var configPath = Path.Combine(directory, "tokenizer_config.json");
        if (!File.Exists(configPath))
        {
            return options;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            if (root.TryGetProperty("do_lower_case", out var lower) && lower.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                options.LowerCaseBeforeTokenization = lower.GetBoolean();
            }

            Apply(root, "cls_token", value => options.ClassificationToken = value);
            Apply(root, "sep_token", value => options.SeparatorToken = value);
            Apply(root, "pad_token", value => options.PaddingToken = value);
            Apply(root, "mask_token", value => options.MaskingToken = value);
            Apply(root, "unk_token", value => options.UnknownToken = value);
        }
        catch (JsonException)
        {
            // A malformed tokenizer config falls back to the BERT defaults, which fit almost every export.
        }

        return options;
    }

    private static void Apply(JsonElement root, string name, Action<string> set)
    {
        // Special tokens are either a plain string or an AddedToken object with a "content" field.
        if (!root.TryGetProperty(name, out var element))
        {
            return;
        }

        var value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("content", out var content) => content.GetString(),
            _ => null,
        };

        if (value is { Length: > 0 })
        {
            set(value);
        }
    }

    /// <summary>
    /// Rewrites the vocabulary embedded in tokenizer.json as the one-token-per-line stream the WordPiece
    /// tokenizer expects, so a folder that ships only tokenizer.json still works.
    /// </summary>
    private static MemoryStream ReadVocabularyFromTokenizerJson(string path, string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("model", out var model)
            || !model.TryGetProperty("vocab", out var vocab)
            || vocab.ValueKind != JsonValueKind.Object)
        {
            throw new OnnxFormatException(
                $"The tokenizer.json in {directory} carries no WordPiece vocabulary. This backend supports BERT-family embedding models; use a GGUF embedding model for other architectures.");
        }

        var ordered = new SortedDictionary<int, string>();
        foreach (var token in vocab.EnumerateObject())
        {
            if (token.Value.ValueKind == JsonValueKind.Number && token.Value.TryGetInt32(out var id))
            {
                ordered[id] = token.Name;
            }
        }

        if (ordered.Count == 0)
        {
            throw new OnnxFormatException($"The tokenizer.json in {directory} has an empty vocabulary.");
        }

        var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            var next = 0;
            foreach (var (id, token) in ordered)
            {
                // Ids must line up with line numbers, so any gap is filled with an unused placeholder.
                for (; next < id; next++)
                {
                    writer.WriteLine($"[unused{next}]");
                }

                writer.WriteLine(token);
                next = id + 1;
            }
        }

        stream.Position = 0;
        return stream;
    }
}
