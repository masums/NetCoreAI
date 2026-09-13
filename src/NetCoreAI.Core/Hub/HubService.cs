using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>A repository with its files grouped into the variants a user picks between, each with a fit verdict.</summary>
/// <param name="Detail">Repository metadata, README and file list.</param>
/// <param name="Variants">Downloadable variants, largest-fitting first.</param>
public sealed record HubModelView(HubModelDetail Detail, IReadOnlyList<HubVariant> Variants);

/// <summary>
/// Browsing across every registered <see cref="IModelSource"/>, plus the curated list. Answers the question
/// the Hub UI actually asks: what can I download, and will it run on this machine?
/// </summary>
public interface IHubService
{
    IReadOnlyList<IModelSource> Sources { get; }

    Task<IReadOnlyList<HubModelSummary>> SearchAsync(ModelSearchQuery query, string? sourceId = null, CancellationToken cancellationToken = default);

    Task<HubModelView?> GetAsync(string repoId, string? sourceId = null, string? revision = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecommendedModel>> GetRecommendedAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

internal sealed class HubService(
    IEnumerable<IModelSource> sources,
    CuratedManifestService manifest,
    IFitEstimator fitEstimator,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<HubService> logger) : IHubService
{
    private readonly List<IModelSource> _sources = [.. sources];

    public IReadOnlyList<IModelSource> Sources => _sources;

    public async Task<IReadOnlyList<HubModelSummary>> SearchAsync(ModelSearchQuery query, string? sourceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureOnline();

        var targets = sourceId is { Length: > 0 } ? [Source(sourceId)] : _sources;
        if (targets.Count == 1)
        {
            return await targets[0].SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }

        // Several sources: query them together and interleave by popularity so no source dominates the page.
        var results = await Task.WhenAll(targets.Select(async source =>
        {
            try
            {
                return await source.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (NetCoreAIException ex)
            {
                logger.LogWarning(ex, "Model source {SourceId} could not be searched; its results are missing from this page.", source.Id);
                return [];
            }
        })).ConfigureAwait(false);

        return [.. results.SelectMany(r => r).OrderByDescending(r => r.Downloads).Take(query.Limit)];
    }

    public async Task<HubModelView?> GetAsync(string repoId, string? sourceId = null, string? revision = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        EnsureOnline();

        var source = sourceId is { Length: > 0 } ? Source(sourceId) : _sources.FirstOrDefault()
            ?? throw new NetCoreAIException("No model source is registered.");

        var detail = await source.GetAsync(repoId, revision, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        var variants = HubFormats.GroupVariants(detail.Files);
        var withFit = new List<HubVariant>(variants.Count);
        var fitByFile = new Dictionary<string, MemoryEstimate>(StringComparer.Ordinal);

        foreach (var variant in variants)
        {
            var fit = await EstimateAsync(detail.Summary, variant, cancellationToken).ConfigureAwait(false);
            withFit.Add(variant with { Fit = fit });
            foreach (var file in variant.Files)
            {
                fitByFile[file] = fit;
            }
        }

        // Biggest variant that still fits first: that is the one a user most often wants.
        withFit.Sort((a, b) => Rank(b).CompareTo(Rank(a)));
        return new HubModelView(detail with { FitByFile = fitByFile }, withFit);
    }

    public async Task<IReadOnlyList<RecommendedModel>> GetRecommendedAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var list = await manifest.GetAsync(refresh, cancellationToken).ConfigureAwait(false);
        var results = new List<RecommendedModel>(list.Models.Count);
        foreach (var model in list.Models)
        {
            results.Add(model with { Fit = await EstimateAsync(model, cancellationToken).ConfigureAwait(false) });
        }

        return results;
    }

    /// <summary>Ranks a variant for display: fitting variants first, larger before smaller within a verdict.</summary>
    private static (int Verdict, long Size) Rank(HubVariant variant) =>
        (variant.Fit?.Verdict switch { FitVerdict.Fits => 3, FitVerdict.Tight => 2, FitVerdict.Unknown => 1, _ => 0 }, variant.SizeBytes);

    /// <summary>
    /// A fit verdict before anything is downloaded. There is no file to read yet, so the estimate comes from
    /// the size and quantization the hub reports, through the same estimator the registry uses after download.
    /// </summary>
    private async Task<MemoryEstimate> EstimateAsync(HubModelSummary summary, HubVariant variant, CancellationToken cancellationToken)
    {
        var descriptor = new ModelDescriptor
        {
            Id = $"hub:{summary.RepoId}:{variant.Name}",
            Name = variant.Name,
            Format = variant.Format,
            ProviderId = string.Empty,
            SizeBytes = variant.SizeBytes,
            Quantization = variant.Quantization,
        };

        return await EstimateAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryEstimate> EstimateAsync(RecommendedModel model, CancellationToken cancellationToken)
    {
        var descriptor = new ModelDescriptor
        {
            Id = $"hub:{model.RepoId}",
            Name = model.Name,
            Format = model.Format,
            ProviderId = string.Empty,
            SizeBytes = model.SizeBytes,
            Quantization = model.Quantization,
            ParameterCount = model.ParameterCount,
            ContextLength = model.ContextLength,
        };

        return await EstimateAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryEstimate> EstimateAsync(ModelDescriptor descriptor, CancellationToken cancellationToken)
    {
        try
        {
            var context = Math.Min(descriptor.ContextLength ?? int.MaxValue, options.CurrentValue.Models.DefaultContextSize);
            return await fitEstimator.EstimateAsync(descriptor, new LoadOptions { ContextSize = context }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NetCoreAIException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not estimate the fit of {ModelId}.", descriptor.Id);
            return MemoryEstimate.Unknown;
        }
    }

    private IModelSource Source(string id) =>
        _sources.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new NetCoreAIException($"No model source with id '{id}' is registered. Registered sources: {(_sources.Count == 0 ? "none" : string.Join(", ", _sources.Select(s => s.Id)))}.");

    private void EnsureOnline()
    {
        if (options.CurrentValue.Network.OfflineMode)
        {
            throw new OfflineModeException(
                "Offline mode is on, so the model hub cannot be browsed. Turn it off in Settings → Network, or import a model from disk instead.");
        }
    }
}
