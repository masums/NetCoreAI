using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Security;

/// <summary>Why a presented key was refused, so the caller is told something they can act on.</summary>
public enum ApiKeyFailure
{
    None,
    Unknown,
    Disabled,
    Expired,
    AddressNotAllowed,
    RateLimited,
}

/// <summary>The result of presenting a key.</summary>
/// <param name="Key">The key, when it was accepted.</param>
/// <param name="Failure">Why it was not.</param>
public sealed record ApiKeyResult(ApiKey? Key, ApiKeyFailure Failure = ApiKeyFailure.None)
{
    public bool Succeeded => Key is not null && Failure == ApiKeyFailure.None;

    /// <summary>Seconds until the caller may try again, when it was rate-limited.</summary>
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>Issuing, checking and revoking API keys.</summary>
public interface IApiKeyService
{
    Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a key and returns its secret. The secret is not stored and cannot be shown again.</summary>
    Task<CreatedApiKey> CreateAsync(ApiKey key, CancellationToken cancellationToken = default);

    Task<ApiKey> UpdateAsync(ApiKey key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Checks a presented secret, the address it came from, and the key's rate limit.</summary>
    Task<ApiKeyResult> AuthenticateAsync(string secret, IPAddress? address, CancellationToken cancellationToken = default);
}

/// <summary>
/// API keys, hashed at rest and rate-limited in memory.
/// </summary>
/// <remarks>
/// The rate limiter counts per key in this process. On a load-balanced deployment each instance therefore
/// allows the configured rate, so the effective limit is the limit times the number of instances. That is
/// a deliberate simplification — a shared counter needs a shared store, which NetCoreAI does not require —
/// and it is written down rather than left for someone to discover from a bill.
/// </remarks>
internal sealed class ApiKeyService(IMetadataStore store, IAuditLog audit, ILogger<ApiKeyService> logger) : IApiKeyService
{
    /// <summary>Marks a NetCoreAI key at a glance, in a log or a pasted configuration file.</summary>
    private const string Prefix = "ncai_";

    private readonly RateLimiter _limiter = new();

    public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ApiKeys.ListAsync(cancellationToken);

    public async Task<CreatedApiKey> CreateAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key.Name))
        {
            throw new NetCoreAIException("A key needs a name, so that whoever finds it in use can tell what it is for.");
        }

        // 256 bits from the cryptographic generator: the secret is the whole of the security here, so it
        // is not something anyone chooses or can guess.
        var secret = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "", StringComparison.Ordinal).Replace("/", "", StringComparison.Ordinal).TrimEnd('=');

        var saved = key with
        {
            Id = string.IsNullOrWhiteSpace(key.Id) ? Guid.NewGuid().ToString("N")[..12] : key.Id,
            Hash = HashOf(secret),
            Prefix = secret[..Math.Min(12, secret.Length)],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.ApiKeys.UpsertAsync(saved, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Issued API key {Name} ({Prefix}…) scoped to {Agents} agent(s) and {Bases} base(s).",
            saved.Name, saved.Prefix, saved.AgentIds.Count, saved.KnowledgeBaseIds.Count);

        await audit.WriteAsync(
            AuditAction.Created,
            AuditEntity.ApiKey,
            saved.Id,
            saved.Name,
            $"{saved.Prefix}…, {saved.AgentIds.Count} agent(s), {saved.KnowledgeBaseIds.Count} knowledge base(s)",
            cancellationToken).ConfigureAwait(false);

        return new CreatedApiKey(saved, secret);
    }

    public async Task<ApiKey> UpdateAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var existing = await store.ApiKeys.GetAsync(key.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException($"No API key with id '{key.Id}'.");

        // The hash and prefix come from the stored key, never from the request: an update that could set
        // them would let anyone replace a key's secret with one they chose.
        var saved = key with { Hash = existing.Hash, Prefix = existing.Prefix, CreatedAt = existing.CreatedAt };
        await store.ApiKeys.UpsertAsync(saved, cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            AuditAction.Updated,
            AuditEntity.ApiKey,
            saved.Id,
            saved.Name,
            saved.Enabled == existing.Enabled ? null : saved.Enabled ? "re-enabled" : "disabled",
            cancellationToken).ConfigureAwait(false);

        return saved;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var key = await store.ApiKeys.GetAsync(id, cancellationToken).ConfigureAwait(false);
        await store.ApiKeys.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        // Revoking a credential is the entry somebody comes looking for after an incident.
        await audit.WriteAsync(AuditAction.Deleted, AuditEntity.ApiKey, id, key?.Name, key is null ? null : $"{key.Prefix}…", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiKeyResult> AuthenticateAsync(string secret, IPAddress? address, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new ApiKeyResult(null, ApiKeyFailure.Unknown);
        }

        var key = await store.ApiKeys.FindByHashAsync(HashOf(secret), cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return new ApiKeyResult(null, ApiKeyFailure.Unknown);
        }

        if (!key.Enabled)
        {
            return new ApiKeyResult(null, ApiKeyFailure.Disabled);
        }

        if (key.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            return new ApiKeyResult(null, ApiKeyFailure.Expired);
        }

        if (!AddressAllowed(key, address))
        {
            logger.LogWarning("API key {Name} was presented from {Address}, which is not in its allow-list.", key.Name, address);
            return new ApiKeyResult(null, ApiKeyFailure.AddressNotAllowed);
        }

        if (key.RateLimitPerMinute is { } limit && !_limiter.Allow(key.Id, limit, out var retryAfter))
        {
            return new ApiKeyResult(null, ApiKeyFailure.RateLimited) { RetryAfterSeconds = retryAfter };
        }

        // Written without awaiting the result: recording when a key was last used must not slow down or
        // fail the call it is authenticating.
        _ = Task.Run(
            () => store.ApiKeys.UpsertAsync(key with { LastUsedAt = DateTimeOffset.UtcNow }, CancellationToken.None),
            CancellationToken.None);

        return new ApiKeyResult(key);
    }

    /// <summary>
    /// Whether the address a key was presented from is allowed.
    /// </summary>
    /// <remarks>
    /// An empty list means anywhere. A non-empty list with no address to check against fails closed: an
    /// allow-list that silently stops applying when the address cannot be read is not an allow-list.
    /// </remarks>
    internal static bool AddressAllowed(ApiKey key, IPAddress? address)
    {
        if (key.IpAllowList.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        foreach (var entry in key.IpAllowList)
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (System.Net.IPNetwork.TryParse(entry, out var network) && network.Contains(address))
                {
                    return true;
                }
            }
            else if (IPAddress.TryParse(entry, out var allowed) && allowed.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static string HashOf(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>A per-key sliding window, counted in this process.</summary>
    private sealed class RateLimiter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Queue<long>> _calls = new(StringComparer.Ordinal);

        public bool Allow(string keyId, int perMinute, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (perMinute <= 0)
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var window = _calls.GetOrAdd(keyId, _ => new Queue<long>());

            lock (window)
            {
                while (window.Count > 0 && now - window.Peek() >= 60_000)
                {
                    window.Dequeue();
                }

                if (window.Count >= perMinute)
                {
                    // When the oldest call falls out of the window, there is room again.
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((60_000 - (now - window.Peek())) / 1000.0));
                    return false;
                }

                window.Enqueue(now);
                return true;
            }
        }
    }
}
