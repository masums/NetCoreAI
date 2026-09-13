using System.Text.RegularExpressions;

namespace NetCoreAI.Hub;

/// <summary>
/// Reads what a repository file name says about it: which runtime can serve it and at what quantization.
/// Used by hub browsing and by import, both of which have only a name to go on before anything is fetched.
/// </summary>
public static partial class HubFormats
{
    /// <summary>Marks an ONNX Runtime GenAI folder; its presence makes every graph beside it loadable.</summary>
    public const string GenAiConfigFile = "genai_config.json";

    /// <summary>The weight format a file belongs to, or null when it is a config, tokenizer or doc file.</summary>
    public static ModelFormat? DetectFormat(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = Path.GetFileName(path);
        if (name.Equals(GenAiConfigFile, StringComparison.OrdinalIgnoreCase))
        {
            return ModelFormat.Onnx;
        }

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".gguf" => ModelFormat.Gguf,
            ".onnx" => ModelFormat.Onnx,
            ".safetensors" => ModelFormat.Safetensors,
            _ => null,
        };
    }

    /// <summary>
    /// The quantization label a file name advertises: llama.cpp spellings such as Q4_K_M or IQ3_XXS, and the
    /// int4/int8/fp16 spellings ONNX exports use. Null when the name says nothing.
    /// </summary>
    public static string? DetectQuantization(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = Path.GetFileNameWithoutExtension(path);
        if (GgufQuantization().Match(name) is { Success: true } gguf)
        {
            return gguf.Value.ToUpperInvariant();
        }

        var lower = $"{path}".ToLowerInvariant();
        foreach (var (needle, label) in QuantizationNames)
        {
            if (lower.Contains(needle, StringComparison.Ordinal))
            {
                return label;
            }
        }

        return null;
    }

    private static readonly (string Needle, string Label)[] QuantizationNames =
    [
        ("int4", "int4"), ("uint4", "int4"), ("int8", "int8"), ("uint8", "int8"), ("qint8", "int8"),
        ("quantized", "int8"), ("bf16", "bf16"), ("fp16", "fp16"), ("float16", "fp16"), ("fp32", "fp32"), ("float32", "fp32"),
    ];

    /// <summary>llama.cpp quantization suffixes: Q4_K_M, Q5_1, IQ2_XXS, F16...</summary>
    [GeneratedRegex(@"\b(?:I?Q\d(?:_[A-Z0-9]+)*|F(?:16|32)|BF16)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GgufQuantization();

    /// <summary>
    /// Groups a repository's files into the variants a user actually picks between: one entry per GGUF file,
    /// and one per ONNX Runtime GenAI folder (the folder is the unit, not its graph).
    /// </summary>
    public static IReadOnlyList<HubVariant> GroupVariants(IEnumerable<HubFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var all = files.ToList();
        var variants = new List<HubVariant>();

        foreach (var file in all.Where(f => f.Format == ModelFormat.Gguf))
        {
            // A multi-part GGUF (…-00001-of-00003.gguf) is downloaded whole, so only the first part is offered.
            if (GgufPart().Match(file.Path) is { Success: true } part && part.Groups[1].Value != "00001")
            {
                continue;
            }

            var siblings = GgufPart().IsMatch(file.Path)
                ? all.Where(f => SameGgufSeries(f.Path, file.Path)).ToList()
                : [file];

            variants.Add(new HubVariant(
                Path.GetFileNameWithoutExtension(file.Path),
                ModelFormat.Gguf,
                [.. siblings.Select(s => s.Path)],
                siblings.Sum(s => s.SizeBytes ?? 0),
                file.Quantization));
        }

        foreach (var config in all.Where(f => Path.GetFileName(f.Path).Equals(GenAiConfigFile, StringComparison.OrdinalIgnoreCase)))
        {
            // Everything beside the config belongs to the same export: graph, external data and tokenizer.
            var folder = Folder(config.Path);
            var members = all.Where(f => Folder(f.Path) == folder).ToList();
            variants.Add(new HubVariant(
                folder.Length == 0 ? "onnx" : folder,
                ModelFormat.Onnx,
                [.. members.Select(m => m.Path)],
                members.Sum(m => m.SizeBytes ?? 0),
                DetectQuantization(folder)));
        }

        return variants;
    }

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static bool SameGgufSeries(string candidate, string first) =>
        GgufPart().Replace(candidate, string.Empty).Equals(GgufPart().Replace(first, string.Empty), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"-(\d{5})-of-\d{5}", RegexOptions.CultureInvariant)]
    private static partial Regex GgufPart();
}

/// <summary>
/// One downloadable variant of a repository: a GGUF quantization (with its split parts) or an ONNX
/// Runtime GenAI folder. This is the unit the Hub UI offers and the download manager queues.
/// </summary>
/// <param name="Name">Display name, taken from the file or folder.</param>
/// <param name="Format">Runtime format the variant needs.</param>
/// <param name="Files">Repository-relative paths that make up the variant.</param>
/// <param name="SizeBytes">Total download size, when the hub reported sizes.</param>
/// <param name="Quantization">Quantization label inferred from the name.</param>
public sealed record HubVariant(string Name, ModelFormat Format, IReadOnlyList<string> Files, long SizeBytes, string? Quantization)
{
    /// <summary>Whether this variant fits the current machine; filled in by the hub service.</summary>
    public MemoryEstimate? Fit { get; init; }
}
