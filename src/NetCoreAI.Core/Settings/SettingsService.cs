using System.Globalization;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Settings;

/// <summary>Settings editable from the dashboard. Secrets are write-only: the DTO only says whether one is set.</summary>
public sealed record NetCoreAISettings
{
    public string DataDirectory { get; init; } = "";
    public string DashboardTitle { get; init; } = "NetCoreAI";
    public int DefaultContextSize { get; init; }
    public int? IdleUnloadMinutes { get; init; }
    public long? MemoryBudgetBytes { get; init; }
    public ExecutionProvider ExecutionProvider { get; init; }
    public int? Threads { get; init; }
    public ConcurrencyPolicy DefaultConcurrency { get; init; }
    public bool OfflineMode { get; init; }
    public string HuggingFaceEndpoint { get; init; } = "";
    public bool HasHuggingFaceToken { get; init; }
    public string? ProxyUrl { get; init; }
    public long? BandwidthLimitBytesPerSecond { get; init; }
    public bool RemoteProvidersEnabled { get; init; }
    public IReadOnlyList<string> DisabledProviders { get; init; } = [];
    public bool TelemetryEnabled { get; init; }
    public long? StorageQuotaWarningBytes { get; init; }
}

/// <summary>Patch for <see cref="NetCoreAISettings"/>; null properties are left unchanged.</summary>
public sealed record NetCoreAISettingsUpdate
{
    public string? DashboardTitle { get; init; }
    public int? DefaultContextSize { get; init; }
    public int? IdleUnloadMinutes { get; init; }
    public bool ClearIdleUnload { get; init; }
    public long? MemoryBudgetBytes { get; init; }
    public ExecutionProvider? ExecutionProvider { get; init; }
    public int? Threads { get; init; }
    public ConcurrencyPolicy? DefaultConcurrency { get; init; }
    public bool? OfflineMode { get; init; }
    public string? HuggingFaceEndpoint { get; init; }
    /// <summary>New Hugging Face token; empty string removes the stored token; null keeps it.</summary>
    public string? HuggingFaceToken { get; init; }
    public string? ProxyUrl { get; init; }
    public long? BandwidthLimitBytesPerSecond { get; init; }
    public bool? RemoteProvidersEnabled { get; init; }
    public IReadOnlyList<string>? DisabledProviders { get; init; }
    public bool? TelemetryEnabled { get; init; }
    public long? StorageQuotaWarningBytes { get; init; }
}

public interface ISettingsService
{
    Task<NetCoreAISettings> GetAsync(CancellationToken cancellationToken = default);

    Task<NetCoreAISettings> UpdateAsync(NetCoreAISettingsUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Decrypted Hugging Face token from the store or configuration, if any.</summary>
    Task<string?> GetHuggingFaceTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists UI overrides in <see cref="ISettingsStore"/> and layers them over configured options through
/// <see cref="IPostConfigureOptions{TOptions}"/>, invalidating <see cref="IOptionsMonitor{TOptions}"/> on change.
/// </summary>
/// <summary>Shared holder so the options post-configurer does not depend on IOptionsMonitor (which would be a DI cycle).</summary>
internal sealed class SettingsOverrides
{
    public volatile Dictionary<string, string>? Values;
}

/// <summary>Applies persisted UI overrides on top of configured options.</summary>
internal sealed class SettingsPostConfigure(SettingsOverrides overrides) : IPostConfigureOptions<NetCoreAIOptions>
{
    public void PostConfigure(string? name, NetCoreAIOptions options) => SettingsService.Apply(overrides.Values, options);
}

internal sealed class SettingsService(IMetadataStore store, ISecretProtector protector, IOptionsMonitor<NetCoreAIOptions> monitor, IOptionsMonitorCache<NetCoreAIOptions> cache, SettingsOverrides overrides)
    : ISettingsService
{
    internal const string HfTokenKey = "Network:HuggingFaceToken";
    private Dictionary<string, string>? _overrides { get => overrides.Values; set => overrides.Values = value; }

    public async Task<NetCoreAISettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var o = monitor.CurrentValue;
        return new NetCoreAISettings
        {
            DataDirectory = o.DataDirectory,
            DashboardTitle = o.Dashboard.Title,
            DefaultContextSize = o.Models.DefaultContextSize,
            IdleUnloadMinutes = o.Models.IdleUnloadTimeout is { } t ? (int)t.TotalMinutes : null,
            MemoryBudgetBytes = o.Models.MemoryBudgetBytes,
            ExecutionProvider = o.Models.ExecutionProvider,
            Threads = o.Models.Threads,
            DefaultConcurrency = o.Models.DefaultConcurrency,
            OfflineMode = o.Network.OfflineMode,
            HuggingFaceEndpoint = o.Network.HuggingFaceEndpoint,
            HasHuggingFaceToken = !string.IsNullOrEmpty(await GetHuggingFaceTokenAsync(cancellationToken).ConfigureAwait(false)),
            ProxyUrl = o.Network.ProxyUrl,
            BandwidthLimitBytesPerSecond = o.Network.BandwidthLimitBytesPerSecond,
            RemoteProvidersEnabled = o.Providers.RemoteEnabled,
            DisabledProviders = o.Providers.Disabled.ToList(),
            TelemetryEnabled = o.Telemetry.Enabled,
            StorageQuotaWarningBytes = o.Models.StorageQuotaWarningBytes,
        };
    }

    public async Task<NetCoreAISettings> UpdateAsync(NetCoreAISettingsUpdate u, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(u);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await Set("Dashboard:Title", u.DashboardTitle).ConfigureAwait(false);
        await Set("Models:DefaultContextSize", u.DefaultContextSize?.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (u.ClearIdleUnload)
        {
            await Set("Models:IdleUnloadMinutes", "0").ConfigureAwait(false);
        }
        else
        {
            await Set("Models:IdleUnloadMinutes", u.IdleUnloadMinutes?.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        await Set("Models:MemoryBudgetBytes", u.MemoryBudgetBytes?.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await Set("Models:ExecutionProvider", u.ExecutionProvider?.ToString()).ConfigureAwait(false);
        await Set("Models:Threads", u.Threads?.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await Set("Models:DefaultConcurrency", u.DefaultConcurrency?.ToString()).ConfigureAwait(false);
        await Set("Network:OfflineMode", u.OfflineMode?.ToString()).ConfigureAwait(false);
        await Set("Network:HuggingFaceEndpoint", u.HuggingFaceEndpoint).ConfigureAwait(false);
        await Set("Network:ProxyUrl", u.ProxyUrl).ConfigureAwait(false);
        await Set("Network:BandwidthLimitBytesPerSecond", u.BandwidthLimitBytesPerSecond?.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await Set("Providers:RemoteEnabled", u.RemoteProvidersEnabled?.ToString()).ConfigureAwait(false);
        await Set("Providers:Disabled", u.DisabledProviders is null ? null : string.Join(',', u.DisabledProviders)).ConfigureAwait(false);
        await Set("Telemetry:Enabled", u.TelemetryEnabled?.ToString()).ConfigureAwait(false);
        await Set("Models:StorageQuotaWarningBytes", u.StorageQuotaWarningBytes?.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

        if (u.HuggingFaceToken is not null)
        {
            var value = u.HuggingFaceToken.Length == 0 ? null : protector.Protect(u.HuggingFaceToken.Trim());
            await store.Settings.SetAsync(HfTokenKey, value, cancellationToken).ConfigureAwait(false);
        }

        _overrides = new Dictionary<string, string>(await store.Settings.GetAllAsync(cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        cache.TryRemove(Options.DefaultName);
        return await GetAsync(cancellationToken).ConfigureAwait(false);

        async Task Set(string key, string? value)
        {
            if (value is not null)
            {
                await store.Settings.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<string?> GetHuggingFaceTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_overrides!.TryGetValue(HfTokenKey, out var stored) && !string.IsNullOrEmpty(stored))
        {
            return protector.Unprotect(stored);
        }

        return string.IsNullOrEmpty(monitor.CurrentValue.Network.HuggingFaceToken) ? null : monitor.CurrentValue.Network.HuggingFaceToken;
    }

    internal static void Apply(Dictionary<string, string>? o, NetCoreAIOptions options)
    {
        if (o is null || o.Count == 0)
        {
            return;
        }

        if (o.TryGetValue("Dashboard:Title", out var title)) { options.Dashboard.Title = title; }
        if (o.TryGetValue("Models:DefaultContextSize", out var ctx) && int.TryParse(ctx, CultureInfo.InvariantCulture, out var ctxV)) { options.Models.DefaultContextSize = ctxV; }
        if (o.TryGetValue("Models:IdleUnloadMinutes", out var idle) && int.TryParse(idle, CultureInfo.InvariantCulture, out var idleV)) { options.Models.IdleUnloadTimeout = idleV <= 0 ? null : TimeSpan.FromMinutes(idleV); }
        if (o.TryGetValue("Models:MemoryBudgetBytes", out var mem) && long.TryParse(mem, CultureInfo.InvariantCulture, out var memV)) { options.Models.MemoryBudgetBytes = memV <= 0 ? null : memV; }
        if (o.TryGetValue("Models:ExecutionProvider", out var ep) && Enum.TryParse<ExecutionProvider>(ep, true, out var epV)) { options.Models.ExecutionProvider = epV; }
        if (o.TryGetValue("Models:Threads", out var th) && int.TryParse(th, CultureInfo.InvariantCulture, out var thV)) { options.Models.Threads = thV <= 0 ? null : thV; }
        if (o.TryGetValue("Models:DefaultConcurrency", out var cc) && Enum.TryParse<ConcurrencyPolicy>(cc, true, out var ccV)) { options.Models.DefaultConcurrency = ccV; }
        if (o.TryGetValue("Network:OfflineMode", out var off) && bool.TryParse(off, out var offV)) { options.Network.OfflineMode = offV; }
        if (o.TryGetValue("Network:HuggingFaceEndpoint", out var hf) && !string.IsNullOrWhiteSpace(hf)) { options.Network.HuggingFaceEndpoint = hf.TrimEnd('/'); }
        if (o.TryGetValue("Network:ProxyUrl", out var proxy)) { options.Network.ProxyUrl = string.IsNullOrWhiteSpace(proxy) ? null : proxy; }
        if (o.TryGetValue("Network:BandwidthLimitBytesPerSecond", out var bw) && long.TryParse(bw, CultureInfo.InvariantCulture, out var bwV)) { options.Network.BandwidthLimitBytesPerSecond = bwV <= 0 ? null : bwV; }
        if (o.TryGetValue("Providers:RemoteEnabled", out var re) && bool.TryParse(re, out var reV)) { options.Providers.RemoteEnabled = reV; }
        if (o.TryGetValue("Providers:Disabled", out var dis)) { options.Providers.Disabled = dis.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); }
        if (o.TryGetValue("Telemetry:Enabled", out var tel) && bool.TryParse(tel, out var telV)) { options.Telemetry.Enabled = telV; }
        if (o.TryGetValue("Models:StorageQuotaWarningBytes", out var quota) && long.TryParse(quota, CultureInfo.InvariantCulture, out var quotaV)) { options.Models.StorageQuotaWarningBytes = quotaV <= 0 ? null : quotaV; }
    }

    internal async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_overrides is not null)
        {
            return;
        }

        _overrides = new Dictionary<string, string>(await store.Settings.GetAllAsync(cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        cache.TryRemove(Options.DefaultName);
    }
}
