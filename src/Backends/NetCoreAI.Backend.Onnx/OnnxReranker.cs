using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using NetCoreAI.Knowledge;

namespace NetCoreAI.Backends.Onnx;

/// <summary>
/// A cross-encoder reranker on ONNX Runtime — <c>bge-reranker-base</c> and its relatives.
/// </summary>
/// <remarks>
/// <para>
/// The model reads the question and one passage <em>together</em> and emits a single number: how well this
/// passage answers that question. That is why it is better than the retrieval score, which compared two
/// things embedded separately that never saw each other — and why it is too slow for a corpus and goes
/// second, over a few dozen candidates.
/// </para>
/// <para>
/// Loaded once and held. The session is thread-safe for inference; the work itself is synchronous and
/// CPU-bound, so it runs off the request thread like the embedding generator does.
/// </para>
/// </remarks>
public sealed class OnnxReranker : IReranker, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly ILogger<OnnxReranker> _logger;
    private readonly int _maxTokens;
    private readonly bool _wantsTokenTypes;

    /// <summary>Loads a cross-encoder from a folder holding <c>model.onnx</c> and its tokenizer files.</summary>
    public OnnxReranker(string modelDirectory, ILogger<OnnxReranker> logger, int maxTokens = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        var path = Directory.EnumerateFiles(modelDirectory, "*.onnx", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? throw new NetCoreAIException($"No .onnx file found in '{modelDirectory}'.");

        _session = new InferenceSession(path);
        _tokenizer = OnnxTokenizerLoader.Load(modelDirectory);
        _logger = logger;
        _maxTokens = maxTokens;

        // Some exports take token_type_ids and some do not, and feeding one that does not want it fails
        // the run. Asked once here rather than guessed at per call.
        _wantsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");

        Id = $"onnx:{Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";

        _logger.LogInformation(
            "Loaded cross-encoder reranker {Id} from {Path} ({TokenTypes} token type ids, {MaxTokens} tokens).",
            Id, path, _wantsTokenTypes ? "with" : "without", _maxTokens);
    }

    public string Id { get; }

    public async Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> chunks,
        int topN,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return chunks;
        }

        var scored = await Task.Run(() => Score(query, chunks, cancellationToken), cancellationToken).ConfigureAwait(false);

        return
        [
            .. scored
                .OrderByDescending(s => s.Score)

                // A stable tie-break on the retrieval order, so two passages the model cannot separate
                // come back in the order something else already had an opinion about.
                .ThenBy(s => s.Index)
                .Take(Math.Max(1, topN))
                .Select(s => s.Chunk with { Score = s.Score })
        ];
    }

    private List<(RetrievedChunk Chunk, int Index, float Score)> Score(
        string query,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken cancellationToken)
    {
        var scored = new List<(RetrievedChunk, int, float)>(chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = chunks[i].Chunk.Text;
            scored.Add((chunks[i], i, string.IsNullOrEmpty(text) ? float.MinValue : ScoreOne(query, text)));
        }

        return scored;
    }

    private float ScoreOne(string query, string passage)
    {
        // The pair, as the model was trained to see it: [CLS] question [SEP] passage [SEP]. Built by hand
        // because the tokenizer here encodes one sequence at a time, and a cross-encoder given the two
        // concatenated without a separator scores a different thing than it was trained to score.
        var questionIds = _tokenizer.EncodeToIds(query, addSpecialTokens: true);
        var passageIds = _tokenizer.EncodeToIds(passage, addSpecialTokens: false);

        var pair = new List<int>(questionIds.Count + passageIds.Count + 1);
        pair.AddRange(questionIds);
        pair.AddRange(passageIds);
        pair.Add(_tokenizer.SeparatorTokenId);

        // Truncated from the passage end rather than the question end: a question cut in half is a
        // different question, while a passage cut short is still about the same thing.
        var length = Math.Max(1, Math.Min(pair.Count, _maxTokens));

        var inputIds = new long[length];
        var attention = new long[length];
        for (var i = 0; i < length; i++)
        {
            inputIds[i] = pair[i];
            attention[i] = 1;
        }

        if (pair.Count > _maxTokens)
        {
            // Whatever was cut, the sequence still has to end the way the model expects.
            inputIds[length - 1] = _tokenizer.SeparatorTokenId;
        }

        var shape = new[] { 1, length };
        var feeds = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attention, shape)),
        };

        if (_wantsTokenTypes)
        {
            // Zeros throughout. The separator carries the boundary in these exports, and a wrong segment
            // id is worse than none: it moves the score without failing, which is the hardest kind of bug
            // to notice in a ranking.
            feeds.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[length], shape)));
        }

        using var outputs = _session.Run(feeds);
        var logits = outputs[0].AsEnumerable<float>().ToArray();

        // One logit is the usual shape for a reranker and is already a relevance score. Two means the
        // export kept a classification head, where the second class is "relevant".
        return logits.Length switch
        {
            0 => float.MinValue,
            1 => logits[0],
            _ => logits[1] - logits[0],
        };
    }

    public void Dispose() => _session.Dispose();
}
