using Microsoft.Extensions.AI;

namespace NetCoreAI;

/// <summary>
/// A runtime that executes a model format locally (GGUF, ONNX...) or a connector to a remote model service
/// (Ollama, OpenAI-compatible, Anthropic...). Providers are discovered through DI; the registry picks one per model.
/// All consumers talk to the resulting <see cref="IChatClient"/> / <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> only.
/// </summary>
public interface IModelProvider
{
    /// <summary>Stable id used in <see cref="ModelDescriptor.ProviderId"/>, e.g. "gguf", "onnx", "ollama", "openai", "anthropic".</summary>
    string Id { get; }

    string DisplayName { get; }

    ProviderKind Kind { get; }

    IReadOnlyList<ModelFormat> SupportedFormats { get; }

    /// <summary>Cheap check: can this provider serve the descriptor on the current machine / configuration?</summary>
    bool CanLoad(ModelDescriptor model);

    /// <summary>Capabilities without loading (from file metadata, preset tables or the remote model list).</summary>
    ModelCapabilities GetCapabilities(ModelDescriptor model);

    /// <summary>Memory needed to load with these options; providers may return <see cref="MemoryEstimate.Unknown"/>.</summary>
    ValueTask<MemoryEstimate> EstimateMemoryAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default);

    ValueTask<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default);

    ValueTask UnloadAsync(LoadedModel model, CancellationToken cancellationToken = default);

    /// <summary>Creates the raw chat client for a loaded model. The core wraps it with the middleware pipeline.</summary>
    IChatClient CreateChatClient(LoadedModel model);

    /// <summary>Creates the raw embedding generator; throws <see cref="NotSupportedException"/> when the model has no embeddings capability.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model);
}

/// <summary>
/// Implemented by remote providers that expose their models through a configured <see cref="ProviderConnection"/>.
/// </summary>
public interface IConnectionAwareProvider : IModelProvider
{
    /// <summary>Presets the UI offers for this provider (e.g. OpenAI, Azure OpenAI, Groq).</summary>
    IReadOnlyList<ProviderPreset> Presets { get; }

    /// <summary>Verifies auth + reachability and returns the models the connection can serve.</summary>
    Task<ConnectionTestResult> TestConnectionAsync(ProviderConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Lists models for a connection; remote-provided where supported, otherwise the preset's curated list.</summary>
    Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderConnection connection, CancellationToken cancellationToken = default);
}
