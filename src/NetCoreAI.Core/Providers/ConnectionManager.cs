using Microsoft.Extensions.Logging;
using NetCoreAI.Security;

namespace NetCoreAI.Providers;

/// <summary>Manages remote provider connections: CRUD with secret protection, testing, and model discovery into the registry.</summary>
public interface IConnectionManager
{
    Task<IReadOnlyList<ProviderConnection>> ListAsync(CancellationToken cancellationToken = default);

    Task<ProviderConnection?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates. <paramref name="plainSecret"/> null keeps the stored secret; empty string clears it.</summary>
    Task<ProviderConnection> SaveAsync(ProviderConnection connection, string? plainSecret, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<ConnectionTestResult> TestAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists remote models and registers them (idempotently) in the model registry.</summary>
    Task<IReadOnlyList<ModelDescriptor>> SyncModelsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Decrypted secret for a connection, honouring the environment-variable override.</summary>
    string? ResolveSecret(ProviderConnection connection);
}

internal sealed class ConnectionManager(
    IMetadataStore store,
    IProviderRegistry providers,
    IModelRegistry registry,
    ISecretProtector protector,
    ISecretResolver secrets,
    ILogger<ConnectionManager> logger) : IConnectionManager
{
    public Task<IReadOnlyList<ProviderConnection>> ListAsync(CancellationToken cancellationToken = default) => store.Connections.ListAsync(cancellationToken);

    public Task<ProviderConnection?> GetAsync(string id, CancellationToken cancellationToken = default) => store.Connections.GetAsync(id, cancellationToken);

    public async Task<ProviderConnection> SaveAsync(ProviderConnection connection, string? plainSecret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var provider = providers.Get(connection.ProviderId) as IConnectionAwareProvider
            ?? throw new ProviderNotFoundException(connection.ProviderId);

        var existing = string.IsNullOrEmpty(connection.Id) ? null : await store.Connections.GetAsync(connection.Id, cancellationToken).ConfigureAwait(false);
        var id = string.IsNullOrEmpty(connection.Id) ? Slug(connection.Name) : connection.Id;

        var protectedSecret = plainSecret switch
        {
            null => existing?.ProtectedSecret,
            "" => null,
            _ => protector.Protect(plainSecret),
        };

        var preset = provider.Presets.FirstOrDefault(p => p.Id == connection.Preset);
        var saved = connection with
        {
            Id = id,
            ProtectedSecret = protectedSecret,
            BaseUrl = string.IsNullOrWhiteSpace(connection.BaseUrl) ? preset?.DefaultBaseUrl : connection.BaseUrl!.TrimEnd('/'),
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
        };

        await store.Connections.UpsertAsync(saved, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Saved provider connection {ConnectionId} ({ProviderId}/{Preset})", saved.Id, saved.ProviderId, saved.Preset);
        return saved;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        foreach (var model in (await registry.ListAsync(cancellationToken).ConfigureAwait(false)).Where(m => m.Descriptor.ConnectionId == id))
        {
            await registry.RemoveAsync(model.Descriptor.Id, deleteFiles: false, cancellationToken).ConfigureAwait(false);
        }

        await store.Connections.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectionTestResult> TestAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await store.Connections.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ConnectionNotFoundException(id);
        var provider = providers.Get(connection.ProviderId) as IConnectionAwareProvider ?? throw new ProviderNotFoundException(connection.ProviderId);

        ConnectionTestResult result;
        try
        {
            result = await provider.TestConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = ConnectionTestResult.Failed(ConnectionHealth.Unreachable, ex.Message, TimeSpan.Zero);
        }

        await store.Connections.UpsertAsync(connection with { Health = result.Health, HealthMessage = result.Message, LastHealthCheckAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<ModelDescriptor>> SyncModelsAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await store.Connections.GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ConnectionNotFoundException(id);
        var provider = providers.Get(connection.ProviderId) as IConnectionAwareProvider ?? throw new ProviderNotFoundException(connection.ProviderId);
        var models = await provider.ListModelsAsync(connection, cancellationToken).ConfigureAwait(false);

        var existing = (await registry.ListAsync(cancellationToken).ConfigureAwait(false)).Where(e => e.Descriptor.ConnectionId == id).ToDictionary(e => e.Descriptor.Id, e => e.Descriptor, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModelDescriptor>();
        foreach (var model in models)
        {
            var descriptor = existing.TryGetValue(model.Id, out var old)
                ? old with { Capabilities = model.Capabilities, ContextLength = model.ContextLength ?? old.ContextLength }
                : model;
            result.Add(existing.ContainsKey(model.Id)
                ? await registry.UpdateAsync(descriptor, cancellationToken).ConfigureAwait(false)
                : await registry.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public string? ResolveSecret(ProviderConnection connection) => secrets.Resolve(connection);

    internal static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(slug) ? Guid.NewGuid().ToString("N")[..8] : slug;
    }
}
