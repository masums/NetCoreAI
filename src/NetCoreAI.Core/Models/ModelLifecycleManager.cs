using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Providers;
using NetCoreAI.Telemetry;

namespace NetCoreAI.Models;

/// <summary>
/// Owns loaded model handles: load/unload with memory budget, per-model concurrency gates, idle unload,
/// warm-up on startup and graceful shutdown. The registry delegates to it.
/// </summary>
public interface IModelLifecycleManager
{
    IReadOnlyCollection<LoadedModel> Loaded { get; }

    bool TryGetLoaded(string modelId, out LoadedModel? model);

    Task<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions? options, CancellationToken cancellationToken = default);

    Task UnloadAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>Acquires a generation slot according to the model's concurrency policy. Dispose to release.</summary>
    Task<IDisposable> AcquireSlotAsync(LoadedModel model, CancellationToken cancellationToken = default);

    /// <summary>Bytes still available under the budget.</summary>
    long AvailableBudgetBytes { get; }

    /// <summary>True once startup warm-up has finished (used by the readiness health check).</summary>
    bool IsReady { get; }

    event EventHandler<LoadedModel>? ModelLoaded;

    event EventHandler<LoadedModel>? ModelUnloaded;

    event EventHandler<(ModelDescriptor Model, ModelStatus Status, string? Message)>? StatusChanged;
}

internal sealed class ModelLifecycleManager(
    IProviderRegistry providers,
    IFitEstimator fitEstimator,
    IHardwareProbe hardware,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<ModelLifecycleManager> logger) : IModelLifecycleManager, IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, LoadedModel> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<LoadedModel>> _pendingLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _budgetLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Timer? _idleTimer;
    private long _usedBytes;

    public IReadOnlyCollection<LoadedModel> Loaded => _loaded.Values.ToList();

    public bool IsReady { get; private set; }

    public event EventHandler<LoadedModel>? ModelLoaded;
    public event EventHandler<LoadedModel>? ModelUnloaded;
    public event EventHandler<(ModelDescriptor Model, ModelStatus Status, string? Message)>? StatusChanged;

    public long AvailableBudgetBytes => Math.Max(0, Budget() - Interlocked.Read(ref _usedBytes));

    public bool TryGetLoaded(string modelId, out LoadedModel? model) => _loaded.TryGetValue(modelId, out model);

    public Task<LoadedModel> LoadAsync(ModelDescriptor model, LoadOptions? options, CancellationToken cancellationToken = default)
    {
        if (_loaded.TryGetValue(model.Id, out var existing))
        {
            existing.LastUsedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(existing);
        }

        // Coalesce concurrent loads of the same model.
        return _pendingLoads.GetOrAdd(model.Id, _ => LoadCoreAsync(model, options ?? BuildDefaultOptions(model), cancellationToken))
            .ContinueWith(t => { _pendingLoads.TryRemove(model.Id, out _); return t.GetAwaiter().GetResult(); }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<LoadedModel> LoadCoreAsync(ModelDescriptor model, LoadOptions loadOptions, CancellationToken cancellationToken)
    {
        var provider = providers.Resolve(model);
        StatusChanged?.Invoke(this, (model, ModelStatus.Loading, null));
        using var activity = NetCoreAITelemetry.ActivitySource.StartActivity("netcoreai.model.load");
        activity?.SetTag("netcoreai.model.id", model.Id);
        activity?.SetTag("netcoreai.provider.id", provider.Id);

        long reserved = 0;
        try
        {
            if (provider.Kind == ProviderKind.Local)
            {
                if (options.CurrentValue.Models.MemoryBudgetBytes is not > 0)
                {
                    await RefreshAutoBudgetAsync(cancellationToken).ConfigureAwait(false);
                }

                var estimate = await fitEstimator.EstimateAsync(model, loadOptions, cancellationToken).ConfigureAwait(false);
                reserved = estimate.TotalBytes;
                await _budgetLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var available = AvailableBudgetBytes;
                    if (estimate.Verdict == FitVerdict.WontFit || (estimate.TotalBytes > 0 && estimate.TotalBytes > available))
                    {
                        throw new ModelWontFitException(model, estimate, available);
                    }

                    Interlocked.Add(ref _usedBytes, reserved);
                }
                finally
                {
                    _budgetLock.Release();
                }
            }

            var loaded = await provider.LoadAsync(model, loadOptions, cancellationToken).ConfigureAwait(false);

            // Providers may report a better number than the estimate.
            if (provider.Kind == ProviderKind.Local && loaded.MemoryBytes > 0 && loaded.MemoryBytes != reserved)
            {
                Interlocked.Add(ref _usedBytes, loaded.MemoryBytes - reserved);
                reserved = loaded.MemoryBytes;
            }

            _loaded[model.Id] = loaded;
            _slots[model.Id] = new SemaphoreSlim(SlotCount(loadOptions), SlotCount(loadOptions));
            NetCoreAITelemetry.LoadedModels.Add(1);
            logger.LogInformation("Loaded model {ModelId} via {ProviderId} ({MemoryMb} MB)", model.Id, provider.Id, loaded.MemoryBytes / 1_048_576);
            StatusChanged?.Invoke(this, (model, ModelStatus.Loaded, null));
            ModelLoaded?.Invoke(this, loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            if (reserved > 0)
            {
                Interlocked.Add(ref _usedBytes, -reserved);
            }

            logger.LogError(ex, "Failed to load model {ModelId}", model.Id);
            StatusChanged?.Invoke(this, (model, ModelStatus.Error, ex.Message));
            throw;
        }
    }

    public async Task UnloadAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!_loaded.TryRemove(modelId, out var loaded))
        {
            return;
        }

        StatusChanged?.Invoke(this, (loaded.Descriptor, ModelStatus.Unloading, null));
        var provider = providers.Get(loaded.ProviderId);
        try
        {
            if (provider is not null)
            {
                await provider.UnloadAsync(loaded, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_slots.TryRemove(modelId, out var slot))
            {
                slot.Dispose();
            }

            if (provider?.Kind == ProviderKind.Local)
            {
                Interlocked.Add(ref _usedBytes, -loaded.MemoryBytes);
            }

            NetCoreAITelemetry.LoadedModels.Add(-1);
            logger.LogInformation("Unloaded model {ModelId}", modelId);
            StatusChanged?.Invoke(this, (loaded.Descriptor, ModelStatus.Available, null));
            ModelUnloaded?.Invoke(this, loaded);
        }
    }

    public async Task<IDisposable> AcquireSlotAsync(LoadedModel model, CancellationToken cancellationToken = default)
    {
        model.LastUsedAt = DateTimeOffset.UtcNow;
        if (!_slots.TryGetValue(model.Descriptor.Id, out var slot))
        {
            return NoopDisposable.Instance; // remote or unmanaged
        }

        if (model.Options.Concurrency == ConcurrencyPolicy.RejectWhenBusy)
        {
            if (!await slot.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new ModelBusyException(model.Descriptor.Id);
            }
        }
        else
        {
            await slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        NetCoreAITelemetry.ActiveGenerations.Add(1);
        return new SlotRelease(slot, model);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var idle = options.CurrentValue.Models.IdleUnloadTimeout;
        if (idle is { } timeout && timeout > TimeSpan.Zero)
        {
            var period = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds / 4, 5, 60));
            _idleTimer = new Timer(_ => _ = UnloadIdleAsync(), null, period, period);
        }

        // Warm-up is triggered by the registry (it knows the descriptors); here we prime the budget and mark readiness.
        await RefreshAutoBudgetAsync(cancellationToken).ConfigureAwait(false);
        IsReady = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IsReady = false;
        _idleTimer?.Dispose();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var id in _loaded.Keys.ToList())
        {
            try
            {
                await UnloadAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error unloading {ModelId} during shutdown", id);
            }
        }
    }

    private async Task UnloadIdleAsync()
    {
        var timeout = options.CurrentValue.Models.IdleUnloadTimeout;
        if (timeout is null)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - timeout.Value;
        foreach (var loaded in _loaded.Values.Where(l => l.LastUsedAt < cutoff && !l.Descriptor.LoadOnStartup && !l.Descriptor.IsRemote).ToList())
        {
            if (_slots.TryGetValue(loaded.Descriptor.Id, out var slot) && slot.CurrentCount < SlotCount(loaded.Options))
            {
                continue; // in use
            }

            logger.LogInformation("Unloading idle model {ModelId} (idle since {LastUsed})", loaded.Descriptor.Id, loaded.LastUsedAt);
            try
            {
                await UnloadAsync(loaded.Descriptor.Id, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Idle unload of {ModelId} failed", loaded.Descriptor.Id);
            }
        }
    }

    private long _autoBudget;

    /// <summary>Configured budget, or 80 % of RAM + all VRAM from the last hardware probe (refreshed asynchronously; never blocks).</summary>
    private long Budget()
    {
        var configured = options.CurrentValue.Models.MemoryBudgetBytes;
        return configured is > 0 ? configured.Value : Interlocked.Read(ref _autoBudget);
    }

    private async Task RefreshAutoBudgetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await hardware.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _autoBudget, (long)(info.TotalRamBytes * 0.8) + info.TotalVramBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hardware probe failed; using GC memory info for the model memory budget.");
            Interlocked.Exchange(ref _autoBudget, (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.8));
        }
    }

    private LoadOptions BuildDefaultOptions(ModelDescriptor model)
    {
        var m = options.CurrentValue.Models;
        return new LoadOptions
        {
            ContextSize = Math.Min(model.ContextLength ?? m.DefaultContextSize, m.DefaultContextSize),
            ExecutionProvider = m.ExecutionProvider,
            Threads = m.Threads,
            Concurrency = m.DefaultConcurrency,
            PoolSize = m.DefaultPoolSize,
        };
    }

    private static int SlotCount(LoadOptions o) => o.Concurrency == ConcurrencyPolicy.Pool ? Math.Max(1, o.PoolSize) : 1;

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // registered under two service types; the container disposes both
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
        _budgetLock.Dispose();
    }

    private sealed class SlotRelease(SemaphoreSlim slot, LoadedModel model) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                model.LastUsedAt = DateTimeOffset.UtcNow;
                NetCoreAITelemetry.ActiveGenerations.Add(-1);
                slot.Release();
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
