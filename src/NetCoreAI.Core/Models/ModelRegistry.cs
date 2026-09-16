using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Providers;

namespace NetCoreAI.Models;

internal sealed class ModelRegistry : IModelRegistry, IHostedService
{
    private readonly IMetadataStore _store;
    private readonly IModelLifecycleManager _lifecycle;
    private readonly IProviderRegistry _providers;
    private readonly IOptionsMonitor<NetCoreAIOptions> _options;
    private readonly NetCoreAI.Security.IAuditLog _audit;
    private readonly ILogger<ModelRegistry> _logger;
    private readonly ConcurrentDictionary<string, (ModelStatus Status, string? Message)> _status = new(StringComparer.OrdinalIgnoreCase);

    public ModelRegistry(IMetadataStore store, IModelLifecycleManager lifecycle, IProviderRegistry providers, IOptionsMonitor<NetCoreAIOptions> options, NetCoreAI.Security.IAuditLog audit, ILogger<ModelRegistry> logger)
    {
        _store = store;
        _audit = audit;
        _lifecycle = lifecycle;
        _providers = providers;
        _options = options;
        _logger = logger;
        _lifecycle.StatusChanged += (_, e) =>
        {
            _status[e.Model.Id] = (e.Status, e.Message);
            Changed?.Invoke(this, new ModelEntry(e.Model, e.Status, e.Message, e.Status == ModelStatus.Loaded && _lifecycle.TryGetLoaded(e.Model.Id, out var l) ? l : null));
        };
    }

    public event EventHandler<ModelEntry>? Changed;

    /// <summary>
    /// Drops a loaded model whose settings now point somewhere else.
    /// </summary>
    /// <remarks>
    /// The lifecycle manager caches a loaded model by its id. Without this, repointing a model at a
    /// different connection leaves every call going to the old one until somebody restarts the host — and
    /// the dashboard shows the new setting the whole time, so it reads as a provider fault rather than a
    /// stale client.
    /// </remarks>
    private async Task UnloadIfRepointedAsync(ModelDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!_lifecycle.TryGetLoaded(descriptor.Id, out _))
        {
            return;
        }

        var previous = await _store.Models.GetAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
        if (previous is null || !LoadedFromChanged(previous, descriptor))
        {
            return;
        }

        _logger.LogInformation("{ModelId} now points somewhere else; unloading the copy loaded from the old settings.", descriptor.Id);
        await _lifecycle.UnloadAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the two descriptors would load differently.
    /// </summary>
    /// <remarks>
    /// Only what the loaded client is built from. A rename, a new tag or a changed default temperature all
    /// leave the same client serving the same model, and unloading for those would throw away a warm local
    /// model because somebody fixed a typo.
    /// </remarks>
    private static bool LoadedFromChanged(ModelDescriptor before, ModelDescriptor after) =>
        !string.Equals(before.ProviderId, after.ProviderId, StringComparison.Ordinal)
        || !string.Equals(before.ConnectionId, after.ConnectionId, StringComparison.Ordinal)
        || !string.Equals(before.RemoteModelId, after.RemoteModelId, StringComparison.Ordinal)
        || !string.Equals(before.Path, after.Path, StringComparison.Ordinal);

    public async Task<IReadOnlyList<ModelEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var models = await _store.Models.ListAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(ToEntry).ToList();
    }

    public async Task<ModelEntry?> GetAsync(string idOrAlias, CancellationToken cancellationToken = default)
    {
        var model = await ResolveDescriptorAsync(idOrAlias, cancellationToken).ConfigureAwait(false);
        return model is null ? null : ToEntry(model);
    }

    public async Task<ModelDescriptor> RegisterAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var descriptor = model;
        if (descriptor.Capabilities == ModelCapabilities.None)
        {
            try
            {
                descriptor = descriptor with { Capabilities = _providers.Resolve(descriptor).GetCapabilities(descriptor) };
            }
            catch (NetCoreAIException ex)
            {
                _logger.LogWarning("Registering {ModelId} without capabilities: {Reason}", descriptor.Id, ex.Message);
            }
        }

        var existed = await _store.Models.GetAsync(descriptor.Id, cancellationToken).ConfigureAwait(false) is not null;
        await UnloadIfRepointedAsync(descriptor, cancellationToken).ConfigureAwait(false);
        await _store.Models.UpsertAsync(descriptor, cancellationToken).ConfigureAwait(false);
        _status[descriptor.Id] = (ModelStatus.Available, null);

        await _audit.WriteAsync(
            existed ? NetCoreAI.Security.AuditAction.Updated : NetCoreAI.Security.AuditAction.Created,
            NetCoreAI.Security.AuditEntity.Model,
            descriptor.Id,
            descriptor.Name,
            descriptor.IsRemote ? $"{descriptor.ProviderId} via {descriptor.ConnectionId}, {descriptor.RemoteModelId}" : descriptor.Format.ToString(),
            cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, ToEntry(descriptor));

        // First chat model becomes "default", first embedding model becomes "embed", so code works without configuration.
        var aliases = await _store.Aliases.ListAsync(cancellationToken).ConfigureAwait(false);
        if (descriptor.Capabilities.Supports(ModelCapability.Chat) && !aliases.Any(a => a.Alias == ModelAlias.Default))
        {
            await SetAliasAsync(ModelAlias.Default, descriptor.Id, null, cancellationToken).ConfigureAwait(false);
        }

        if (descriptor.Capabilities.Supports(ModelCapability.Embeddings) && !aliases.Any(a => a.Alias == ModelAlias.Embed))
        {
            await SetAliasAsync(ModelAlias.Embed, descriptor.Id, null, cancellationToken).ConfigureAwait(false);
        }

        return descriptor;
    }

    public async Task<ModelDescriptor> UpdateAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        _ = await _store.Models.GetAsync(model.Id, cancellationToken).ConfigureAwait(false) ?? throw new ModelNotFoundException(model.Id);
        await UnloadIfRepointedAsync(model, cancellationToken).ConfigureAwait(false);
        await _store.Models.UpsertAsync(model, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, ToEntry(model));
        return model;
    }

    public async Task RemoveAsync(string id, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        var model = await _store.Models.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ModelNotFoundException(id);
        await _lifecycle.UnloadAsync(id, cancellationToken).ConfigureAwait(false);
        await _store.Models.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            NetCoreAI.Security.AuditAction.Deleted,
            NetCoreAI.Security.AuditEntity.Model,
            id,
            model.Name,
            deleteFiles ? "files deleted too" : null,
            cancellationToken).ConfigureAwait(false);
        foreach (var alias in (await _store.Aliases.ListAsync(cancellationToken).ConfigureAwait(false)).Where(a => a.ModelId == id))
        {
            await _store.Aliases.DeleteAsync(alias.Alias, cancellationToken).ConfigureAwait(false);
        }

        _status.TryRemove(id, out _);
        if (deleteFiles && model.Path is not null)
        {
            var full = ResolvePath(model.Path);
            try
            {
                if (Directory.Exists(full))
                {
                    Directory.Delete(full, recursive: true);
                }
                else if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete files for {ModelId} at {Path}", id, full);
            }
        }

        Changed?.Invoke(this, new ModelEntry(model, ModelStatus.Available, "removed"));
    }

    public async Task<LoadedModel> LoadAsync(string idOrAlias, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var model = await ResolveDescriptorAsync(idOrAlias, cancellationToken).ConfigureAwait(false) ?? throw new ModelNotFoundException(idOrAlias);
        var loaded = await _lifecycle.LoadAsync(model, options, cancellationToken).ConfigureAwait(false);
        if (model.LastUsedAt is null || model.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-5))
        {
            await _store.Models.UpsertAsync(model with { LastUsedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        }

        return loaded;
    }

    public async Task UnloadAsync(string idOrAlias, CancellationToken cancellationToken = default)
    {
        var model = await ResolveDescriptorAsync(idOrAlias, cancellationToken).ConfigureAwait(false) ?? throw new ModelNotFoundException(idOrAlias);
        await _lifecycle.UnloadAsync(model.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAliasAsync(string alias, string modelId, IReadOnlyList<string>? fallbackModelIds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        _ = await _store.Models.GetAsync(modelId, cancellationToken).ConfigureAwait(false) ?? throw new ModelNotFoundException(modelId);
        await _store.Aliases.UpsertAsync(new ModelAlias(alias.Trim(), modelId, fallbackModelIds ?? []), cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAliasAsync(string alias, CancellationToken cancellationToken = default) => _store.Aliases.DeleteAsync(alias, cancellationToken);

    public async Task<IReadOnlyDictionary<string, ModelAlias>> GetAliasesAsync(CancellationToken cancellationToken = default)
    {
        var list = await _store.Aliases.ListAsync(cancellationToken).ConfigureAwait(false);
        return list.ToDictionary(a => a.Alias, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves an id, or an alias to its primary model.</summary>
    internal async Task<ModelDescriptor?> ResolveDescriptorAsync(string idOrAlias, CancellationToken cancellationToken)
    {
        var byId = await _store.Models.GetAsync(idOrAlias, cancellationToken).ConfigureAwait(false);
        if (byId is not null)
        {
            return byId;
        }

        var aliases = await _store.Aliases.ListAsync(cancellationToken).ConfigureAwait(false);
        var alias = aliases.FirstOrDefault(a => string.Equals(a.Alias, idOrAlias, StringComparison.OrdinalIgnoreCase));
        return alias is null ? null : await _store.Models.GetAsync(alias.ModelId, cancellationToken).ConfigureAwait(false);
    }

    private ModelEntry ToEntry(ModelDescriptor model)
    {
        if (_lifecycle.TryGetLoaded(model.Id, out var loaded))
        {
            return new ModelEntry(model, ModelStatus.Loaded, null, loaded);
        }

        var (status, message) = _status.GetValueOrDefault(model.Id, (ModelStatus.Available, null));
        return new ModelEntry(model, status == ModelStatus.Loaded ? ModelStatus.Available : status, message);
    }

    private string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_options.CurrentValue.DataDirectory, path));

    // Warm-up: load models flagged LoadOnStartup after the store is initialised. Failures are logged, never thrown (host must start).
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelDescriptor> models;
        try
        {
            models = await _store.Models.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata store unavailable at startup; model registry is empty until it recovers.");
            return;
        }

        foreach (var model in models.Where(m => m.LoadOnStartup))
        {
            try
            {
                await _lifecycle.LoadAsync(model, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Warm-up load of {ModelId} failed", model.Id);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
