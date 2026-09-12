using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NetCoreAI.Providers;
using NetCoreAI.Security;
using OpenAI;

namespace NetCoreAI.Backends.OpenAICompatible;

/// <summary>OpenAI, Azure OpenAI and any server speaking the OpenAI chat/embeddings API.</summary>
public sealed class OpenAICompatibleProvider(IMetadataStore store, ISecretResolver secrets, IOptionsMonitor<NetCoreAIOptions> options)
    : RemoteModelProviderBase(store, secrets, options)
{
    public const string ProviderId = "openai";

    public override string Id => ProviderId;

    public override string DisplayName => "OpenAI-compatible";

    public override IReadOnlyList<ProviderPreset> Presets { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", true, true, ["gpt-4.1", "gpt-4.1-mini", "gpt-4o", "gpt-4o-mini", "o4-mini", "text-embedding-3-small", "text-embedding-3-large"]),
        new("azure", "Azure OpenAI", null, true, false, ["gpt-4.1", "gpt-4o", "gpt-4o-mini", "text-embedding-3-small"]) { RequiredSettings = ["api-version"] },
        new("vllm", "vLLM", "http://localhost:8000/v1", false, true, []),
        new("lmstudio", "LM Studio", "http://localhost:1234/v1", false, true, []),
        new("groq", "Groq", "https://api.groq.com/openai/v1", true, true, ["llama-3.3-70b-versatile", "llama-3.1-8b-instant", "qwen-qwq-32b"], SupportsEmbeddings: false),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", true, true, ["deepseek-chat", "deepseek-reasoner"], SupportsEmbeddings: false),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", true, true, ["openai/gpt-4o-mini", "anthropic/claude-sonnet-4", "meta-llama/llama-3.3-70b-instruct"], SupportsEmbeddings: false),
        new("together", "Together AI", "https://api.together.xyz/v1", true, true, ["meta-llama/Llama-3.3-70B-Instruct-Turbo", "Qwen/Qwen2.5-72B-Instruct-Turbo"]),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", true, true, ["mistral-large-latest", "mistral-small-latest", "mistral-embed"]),
        new("custom", "Custom OpenAI-compatible server", null, false, true, []),
    ];

    protected override ModelCapabilities DefaultCapabilities(string remoteModelId)
    {
        if (LooksLikeEmbeddingModel(remoteModelId))
        {
            return new ModelCapabilities(ModelCapability.Embeddings, EmbeddingDimensions: remoteModelId.Contains("3-large", StringComparison.OrdinalIgnoreCase) ? 3072 : 1536);
        }

        var flags = ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCalling | ModelCapability.JsonMode | ModelCapability.StructuredOutput;
        if (remoteModelId.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) || remoteModelId.Contains("gpt-4.1", StringComparison.OrdinalIgnoreCase) || remoteModelId.Contains("vision", StringComparison.OrdinalIgnoreCase) || remoteModelId.Contains("vl", StringComparison.OrdinalIgnoreCase))
        {
            flags |= ModelCapability.Vision;
        }

        return new ModelCapabilities(flags, MaxContext: 128_000);
    }

    protected override object CreateHandle(ProviderConnection connection, ModelDescriptor model) => CreateClient(connection);

    private OpenAIClient CreateClient(ProviderConnection connection)
    {
        var secret = Secret(connection) ?? string.Empty;
        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = connection.Timeout,
            RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
        };
        if (!string.IsNullOrWhiteSpace(connection.BaseUrl))
        {
            clientOptions.Endpoint = new Uri(connection.BaseUrl);
        }

        if (connection.Preset == "azure")
        {
            // Azure OpenAI v1 endpoint accepts the key in the api-key header; the SDK sends a bearer token which Azure ignores for keys.
            clientOptions.AddPolicy(new HeaderPolicy("api-key", secret), PipelinePosition.PerCall);
            if (connection.Settings.TryGetValue("api-version", out var apiVersion) && !string.IsNullOrEmpty(apiVersion))
            {
                clientOptions.AddPolicy(new QueryPolicy("api-version", apiVersion), PipelinePosition.PerCall);
            }
        }

        foreach (var (key, value) in connection.Settings.Where(kv => kv.Key.StartsWith("header:", StringComparison.OrdinalIgnoreCase)))
        {
            clientOptions.AddPolicy(new HeaderPolicy(key["header:".Length..], value), PipelinePosition.PerCall);
        }

        // Servers without auth (vLLM, LM Studio) still need a non-empty credential for the SDK.
        return new OpenAIClient(new ApiKeyCredential(string.IsNullOrEmpty(secret) ? "no-key" : secret), clientOptions);
    }

    public override IChatClient CreateChatClient(LoadedModel model)
        => ((OpenAIClient)model.Handle!).GetChatClient(model.Descriptor.RemoteModelId!).AsIChatClient();

    public override IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model)
        => ((OpenAIClient)model.Handle!).GetEmbeddingClient(model.Descriptor.RemoteModelId!).AsIEmbeddingGenerator();

    public override async Task<ConnectionTestResult> TestConnectionAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var models = await ListRemoteModelIdsAsync(connection, cancellationToken).ConfigureAwait(false);
            return ConnectionTestResult.Ok(models, sw.Elapsed);
        }
        catch (ClientResultException ex) when (ex.Status is 401 or 403)
        {
            return ConnectionTestResult.Failed(ConnectionHealth.AuthFailed, $"Authentication failed ({ex.Status}). Check the API key.", sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConnectionTestResult.Failed(ConnectionHealth.Unreachable, ex.Message, sw.Elapsed);
        }
    }

    public override async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
    {
        var ids = await ListRemoteModelIdsAsync(connection, cancellationToken).ConfigureAwait(false);
        return ids.Select(id => MakeDescriptor(connection, id)).ToList();
    }

    private async Task<IReadOnlyList<string>> ListRemoteModelIdsAsync(ProviderConnection connection, CancellationToken cancellationToken)
    {
        var preset = PresetOf(connection);
        if (preset is { SupportsModelListing: false })
        {
            return preset.CuratedModels;
        }

        var client = CreateClient(connection);
        var result = await client.GetOpenAIModelClient().GetModelsAsync(cancellationToken).ConfigureAwait(false);
        var ids = result.Value.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        return ids.Count > 0 ? ids : preset?.CuratedModels ?? [];
    }

    private sealed class HeaderPolicy(string name, string value) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set(name, value);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set(name, value);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }
    }

    private sealed class QueryPolicy(string name, string value) : PipelinePolicy
    {
        private void Apply(PipelineMessage message)
        {
            if (message.Request.Uri is { } uri && !uri.Query.Contains(name + "=", StringComparison.Ordinal))
            {
                var builder = new UriBuilder(uri);
                var q = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
                builder.Query = string.IsNullOrEmpty(builder.Query) ? q : builder.Query.TrimStart('?') + "&" + q;
                message.Request.Uri = builder.Uri;
            }
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }
    }
}

