using Microsoft.Extensions.Logging;

namespace NetCoreAI.Telemetry;

/// <summary>
/// Turns token counts into money using the price recorded on the model's provider connection.
/// Local models cost nothing per token, so they estimate to null rather than zero: "free" and "not priced"
/// are different answers, and the UI says so.
/// </summary>
public interface ICostEstimator
{
    /// <summary>Estimated cost of one call, or null when the model is local or its connection has no pricing.</summary>
    decimal? Estimate(ModelDescriptor model, long inputTokens, long outputTokens);
}

internal sealed class CostEstimator(IMetadataStore store, ILogger<CostEstimator> logger) : ICostEstimator
{
    // Connections change rarely and this runs on every generation, so prices are cached after first use.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (decimal? In, decimal? Out)> _prices = new(StringComparer.Ordinal);

    public decimal? Estimate(ModelDescriptor model, long inputTokens, long outputTokens)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.IsRemote || model.ConnectionId is not { Length: > 0 } connectionId)
        {
            // A local model's cost is electricity, which NetCoreAI is in no position to price.
            return null;
        }

        var (input, output) = _prices.GetOrAdd(connectionId, Load);
        if (input is null && output is null)
        {
            return null;
        }

        return ((inputTokens / 1000m) * (input ?? 0m)) + ((outputTokens / 1000m) * (output ?? 0m));
    }

    /// <summary>Drops the cached price for a connection whose pricing was edited.</summary>
    public void Invalidate(string connectionId) => _prices.TryRemove(connectionId, out _);

    private (decimal? In, decimal? Out) Load(string connectionId)
    {
        try
        {
            // The store is async; this runs inside a generation, so the wait is on a cache miss only.
            var connection = store.Connections.GetAsync(connectionId).GetAwaiter().GetResult();
            return (connection?.CostPer1KInputTokens, connection?.CostPer1KOutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read pricing for connection {ConnectionId}; costs will not be shown.", connectionId);
            return (null, null);
        }
    }
}
