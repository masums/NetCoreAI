using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NetCoreAI.Providers;
using NetCoreAI.Security;
using OllamaSharp;

namespace NetCoreAI.Backends.Ollama;

/// <summary>Ollama server on the local machine or LAN. Lists models from /api/tags; chat, embeddings, tools and streaming via OllamaSharp.</summary>
public sealed class OllamaProvider(IMetadataStore store, ISecretResolver secrets, IOptionsMonitor<NetCoreAIOptions> options, IHttpClientFactory httpClientFactory)
    : RemoteModelProviderBase(store, secrets, options)
{
    public const string ProviderId = "ollama";

    public override string Id => ProviderId;

    public override string DisplayName => "Ollama";

    public override IReadOnlyList<ProviderPreset> Presets { get; } =
    [
        new("local", "Ollama (this machine)", "http://localhost:11434", false, true, []),
        new("remote", "Ollama server (LAN / remote)", null, false, true, []),
    ];

    protected override ModelCapabilities DefaultCapabilities(string remoteModelId)
        => LooksLikeEmbeddingModel(remoteModelId)
            ? new ModelCapabilities(ModelCapability.Embeddings)
            : new ModelCapabilities(ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCalling | ModelCapability.JsonMode | ModelCapability.StructuredOutput);

    protected override object CreateHandle(ProviderConnection connection, ModelDescriptor model) => CreateClient(connection, model.RemoteModelId);

    private OllamaApiClient CreateClient(ProviderConnection connection, string? defaultModel = null)
    {
        var http = httpClientFactory.CreateClient("NetCoreAI.Ollama");
        http.BaseAddress = new Uri((connection.BaseUrl ?? "http://localhost:11434").TrimEnd('/') + "/");
        http.Timeout = connection.Timeout;
        var secret = Secret(connection);
        if (!string.IsNullOrEmpty(secret))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        foreach (var (key, value) in connection.Settings.Where(kv => kv.Key.StartsWith("header:", StringComparison.OrdinalIgnoreCase)))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(key["header:".Length..], value);
        }

        return new OllamaApiClient(http, defaultModel ?? string.Empty);
    }

    public override IChatClient CreateChatClient(LoadedModel model) => (OllamaApiClient)model.Handle!;

    public override IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model) => (OllamaApiClient)model.Handle!;

    public override async Task<ConnectionTestResult> TestConnectionAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = CreateClient(connection);
            var models = await client.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            var names = models.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return ConnectionTestResult.Ok(names, sw.Elapsed, names.Count == 0 ? "Reachable, but no models are pulled yet. Run `ollama pull <model>` or use the Model Hub." : null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return ConnectionTestResult.Failed(ConnectionHealth.AuthFailed, "Authentication failed. Check the token.", sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConnectionTestResult.Failed(ConnectionHealth.Unreachable, ex.Message, sw.Elapsed);
        }
    }

    public override async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(connection);
        var models = await client.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ModelDescriptor>();
        foreach (var m in models)
        {
            var capabilities = DefaultCapabilities(m.Name);
            int? context = null;
            try
            {
                var info = await client.ShowModelAsync(m.Name, cancellationToken).ConfigureAwait(false);
                if (info.Capabilities is { Length: > 0 } caps)
                {
                    var flags = ModelCapability.None;
                    foreach (var c in caps)
                    {
                        flags |= c switch
                        {
                            "completion" => ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.JsonMode | ModelCapability.StructuredOutput,
                            "tools" => ModelCapability.ToolCalling,
                            "embedding" => ModelCapability.Embeddings,
                            "vision" => ModelCapability.Vision,
                            _ => ModelCapability.None,
                        };
                    }

                    if (flags != ModelCapability.None)
                    {
                        capabilities = new ModelCapabilities(flags);
                    }
                }

                if (info.Info is { } extra)
                {
                    var ctxKey = extra.ExtraInfo?.Keys.FirstOrDefault(k => k.EndsWith(".context_length", StringComparison.Ordinal));
                    if (ctxKey is not null && extra.ExtraInfo![ctxKey] is { } raw && int.TryParse(raw.ToString(), out var ctx))
                    {
                        context = ctx;
                    }
                }
            }
            catch (Exception)
            {
                // /api/show is optional; keep heuristics
            }

            result.Add(MakeDescriptor(connection, m.Name, capabilities with { MaxContext = context }, context) with
            {
                SizeBytes = m.Size,
                Quantization = m.Details?.QuantizationLevel,
                Family = m.Details?.Family,
            });
        }

        return result;
    }
}

