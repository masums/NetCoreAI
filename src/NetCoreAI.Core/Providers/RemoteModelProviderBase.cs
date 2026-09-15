using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NetCoreAI.Security;

namespace NetCoreAI.Providers;

/// <summary>
/// Base class for providers that call a remote service through a <see cref="ProviderConnection"/>.
/// "Loading" creates SDK clients; nothing counts against the memory budget.
/// </summary>
public abstract class RemoteModelProviderBase(IMetadataStore store, ISecretResolver secrets, IOptionsMonitor<NetCoreAIOptions> options) : IConnectionAwareProvider
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public ProviderKind Kind => ProviderKind.Remote;

    public IReadOnlyList<ModelFormat> SupportedFormats { get; } = [ModelFormat.Remote];

    public abstract IReadOnlyList<ProviderPreset> Presets { get; }

    protected IMetadataStore Store { get; } = store;

    protected ISecretResolver Secrets { get; } = secrets;

    protected NetCoreAIOptions Options => options.CurrentValue;

    public virtual bool CanLoad(ModelDescriptor model) => model.Format == ModelFormat.Remote && string.Equals(model.ProviderId, Id, StringComparison.OrdinalIgnoreCase) && model.ConnectionId is not null;

    public virtual ModelCapabilities GetCapabilities(ModelDescriptor model)
        => model.Capabilities != ModelCapabilities.None ? model.Capabilities : DefaultCapabilities(model.RemoteModelId ?? model.Id);

    /// <summary>Capabilities guessed from the remote model id when the API does not expose them.</summary>
    protected abstract ModelCapabilities DefaultCapabilities(string remoteModelId);

    public ValueTask<MemoryEstimate> EstimateMemoryAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new MemoryEstimate(0, 0, FitVerdict.Fits, "Remote model."));

    public async ValueTask<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions options, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(model.ConnectionId!, cancellationToken).ConfigureAwait(false);
        var handle = CreateHandle(connection, model);
        return new LoadedModel(model, Id, handle, 0, options);
    }

    public ValueTask UnloadAsync(LoadedModel model, CancellationToken cancellationToken = default)
    {
        (model.Handle as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates the provider-specific client bundle for the connection; stored as <see cref="LoadedModel.Handle"/>.</summary>
    protected abstract object CreateHandle(ProviderConnection connection, ModelDescriptor model);

    public abstract IChatClient CreateChatClient(LoadedModel model);

    public abstract IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model);

    public abstract Task<ConnectionTestResult> TestConnectionAsync(ProviderConnection connection, CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderConnection connection, CancellationToken cancellationToken = default);

    protected async Task<ProviderConnection> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        var connection = await Store.Connections.GetAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null || !connection.Enabled)
        {
            throw new ConnectionNotFoundException(connectionId);
        }

        if (!NetCoreAI.Hub.EgressPolicy.IsAllowed(connection.BaseUrl, Options.Network))
        {
            throw new RemoteProvidersDisabledException($"{connection.Name} (offline mode)");
        }

        return connection;
    }

    protected string? Secret(ProviderConnection connection) => Secrets.Resolve(connection);

    protected ProviderPreset? PresetOf(ProviderConnection connection) => Presets.FirstOrDefault(p => p.Id == connection.Preset) ?? Presets.FirstOrDefault(p => p.Id == "custom");

    protected static string MakeModelId(ProviderConnection connection, string remoteModelId)
        => $"{connection.Id}/{remoteModelId}".Replace(':', '_');

    protected ModelDescriptor MakeDescriptor(ProviderConnection connection, string remoteModelId, ModelCapabilities? capabilities = null, int? contextLength = null)
        => new()
        {
            Id = MakeModelId(connection, remoteModelId),
            Name = $"{remoteModelId} ({connection.Name})",
            Format = ModelFormat.Remote,
            ProviderId = Id,
            ConnectionId = connection.Id,
            RemoteModelId = remoteModelId,
            Source = $"connection:{connection.Id}",
            ContextLength = contextLength,
            Capabilities = capabilities ?? DefaultCapabilities(remoteModelId),
            DefaultParameters = connection.DefaultParameters,
        };

    protected static bool LooksLikeEmbeddingModel(string id)
        => id.Contains("embed", StringComparison.OrdinalIgnoreCase) || id.Contains("bge", StringComparison.OrdinalIgnoreCase) || id.Contains("e5-", StringComparison.OrdinalIgnoreCase) || id.Contains("minilm", StringComparison.OrdinalIgnoreCase);
}
