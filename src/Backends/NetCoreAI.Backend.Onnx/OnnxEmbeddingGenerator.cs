using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace NetCoreAI.Backends.Onnx;

/// <summary>
/// Embeddings from a sentence-transformers ONNX export: tokenize, run the encoder, pool the token vectors
/// with the attention mask and (by default) L2-normalize, which is what the Python pipeline does.
/// </summary>
internal sealed class OnnxEmbeddingGenerator(OnnxEmbeddingModel model, ILogger logger)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingGeneratorMetadata _metadata =
        new("onnx", null, model.Descriptor.Id, model.Dimensions);

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var inputs = values.ToList();
        // ONNX Runtime inference is synchronous and CPU-bound, so it runs off the request thread.
        var embeddings = await Task.Run(() => Embed(inputs, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    private List<Embedding<float>> Embed(List<string> inputs, CancellationToken cancellationToken)
    {
        var results = new List<Embedding<float>>(inputs.Count);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new Embedding<float>(EmbedOne(input)) { ModelId = model.Descriptor.Id });
        }

        return results;
    }

    private float[] EmbedOne(string text)
    {
        var ids = model.Tokenizer.EncodeToIds(text ?? string.Empty, addSpecialTokens: true, considerPreTokenization: true, considerNormalization: true);
        var length = Math.Max(1, Math.Min(ids.Count, model.MaxTokens));

        var inputIds = new long[length];
        var attention = new long[length];
        for (var i = 0; i < length; i++)
        {
            inputIds[i] = i < ids.Count ? ids[i] : model.Tokenizer.PaddingTokenId;
            attention[i] = 1;
        }

        var shape = new[] { 1, length };
        var feeds = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, shape)),
        };

        if (model.InputNames.Contains("attention_mask"))
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attention, shape)));
        }

        if (model.InputNames.Contains("token_type_ids"))
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[length], shape)));
        }

        using var outputs = model.Session.Run(feeds);
        return Pool(outputs, attention);
    }

    /// <summary>Uses the model's own pooled output when it has one, else pools the token vectors here.</summary>
    private float[] Pool(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, long[] attention)
    {
        var pooled = outputs.FirstOrDefault(o => o.Name.Equals("sentence_embedding", StringComparison.OrdinalIgnoreCase));
        if (pooled is not null)
        {
            return Normalize([.. pooled.AsTensor<float>()]);
        }

        var hidden = outputs.FirstOrDefault(o => o.Name.Equals("last_hidden_state", StringComparison.OrdinalIgnoreCase))
            ?? outputs[0];
        var tensor = hidden.AsTensor<float>();
        var dimensions = tensor.Dimensions;
        if (dimensions.Length != 3)
        {
            throw new NetCoreAIException(
                $"'{model.Descriptor.Name}' produced a {dimensions.Length}-dimensional output, which is not a token-embedding tensor. Check that the folder is a sentence-transformers export.");
        }

        var tokens = dimensions[1];
        var width = dimensions[2];
        var vector = new float[width];
        long counted = 0;

        for (var token = 0; token < tokens; token++)
        {
            if (!model.MeanPooling && token > 0)
            {
                // CLS pooling: the first token is the sentence vector.
                break;
            }

            if (token < attention.Length && attention[token] == 0)
            {
                continue;
            }

            counted++;
            for (var i = 0; i < width; i++)
            {
                vector[i] += tensor[0, token, i];
            }
        }

        if (counted > 1)
        {
            for (var i = 0; i < width; i++)
            {
                vector[i] /= counted;
            }
        }

        return Normalize(vector);
    }

    private float[] Normalize(float[] vector)
    {
        if (!model.Normalize)
        {
            return vector;
        }

        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        var magnitude = Math.Sqrt(sum);
        if (magnitude < 1e-12)
        {
            logger.LogDebug("Embedding from {ModelId} had zero magnitude; returning it unnormalized.", model.Descriptor.Id);
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / magnitude);
        }

        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
            : serviceType == typeof(InferenceSession) ? model.Session
            : serviceType == typeof(Tokenizer) ? model.Tokenizer
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <summary>The session is owned by the lifecycle manager, so disposing a generator releases nothing.</summary>
    public void Dispose()
    {
    }
}
