using System.Diagnostics;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NetCoreAI.Providers;
using NetCoreAI.Security;

namespace NetCoreAI.Backends.Anthropic;

/// <summary>Anthropic Messages API (Claude) and Anthropic-compatible proxies. No embeddings: pair with a local or OpenAI-compatible embedding model.</summary>
public sealed class AnthropicProvider(IMetadataStore store, ISecretResolver secrets, IOptionsMonitor<NetCoreAIOptions> options)
    : RemoteModelProviderBase(store, secrets, options)
{
    public const string ProviderId = "anthropic";

    private const int DefaultMaxOutputTokens = 4096;

    public override string Id => ProviderId;

    public override string DisplayName => "Anthropic";

    public override IReadOnlyList<ProviderPreset> Presets { get; } =
    [
        new("anthropic", "Anthropic", "https://api.anthropic.com", true, true, ["claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5"], SupportsEmbeddings: false),
        new("custom", "Anthropic-compatible proxy", null, false, true, [], SupportsEmbeddings: false),
    ];

    protected override ModelCapabilities DefaultCapabilities(string remoteModelId)
        => new(ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCalling | ModelCapability.Vision | ModelCapability.StructuredOutput, MaxContext: 200_000);

    protected override object CreateHandle(ProviderConnection connection, ModelDescriptor model) => CreateClient(connection);

    private AnthropicClient CreateClient(ProviderConnection connection)
    {
        var clientOptions = new ClientOptions
        {
            ApiKey = Secret(connection) ?? "no-key",
            MaxRetries = connection.MaxRetries,
            Timeout = connection.Timeout,
        };
        if (!string.IsNullOrWhiteSpace(connection.BaseUrl))
        {
            clientOptions = clientOptions with { BaseUrl = connection.BaseUrl };
        }

        var headers = connection.Settings.Where(kv => kv.Key.StartsWith("header:", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key["header:".Length..], kv => kv.Value);
        if (headers.Count > 0)
        {
            clientOptions = clientOptions with { ExtraHeaders = headers };
        }

        return new AnthropicClient(clientOptions);
    }

    public override IChatClient CreateChatClient(LoadedModel model)
        => ((AnthropicClient)model.Handle!).AsIChatClient(model.Descriptor.RemoteModelId, model.Descriptor.DefaultParameters.MaxOutputTokens ?? DefaultMaxOutputTokens);

    public override IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model)
        => throw new NotSupportedException("The Anthropic API does not provide embeddings. Configure a local or OpenAI-compatible embedding model and set the 'embed' alias to it.");

    public override async Task<ConnectionTestResult> TestConnectionAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ids = await ListRemoteModelIdsAsync(connection, cancellationToken).ConfigureAwait(false);
            return ConnectionTestResult.Ok(ids, sw.Elapsed);
        }
        catch (AnthropicApiException ex) when ((int)ex.StatusCode is 401 or 403)
        {
            return ConnectionTestResult.Failed(ConnectionHealth.AuthFailed, $"Authentication failed ({ex.StatusCode}). Check the API key.", sw.Elapsed);
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
        var client = CreateClient(connection);
        var page = await client.Models.List(new ModelListParams { Limit = 100 }, cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        await foreach (ModelInfo model in page.Paginate(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(model.ID);
        }

        return ids.Count > 0 ? ids : PresetOf(connection)?.CuratedModels ?? [];
    }
}

