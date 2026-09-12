using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Models;
using NetCoreAI.Providers;
using NetCoreAI.Telemetry;

namespace NetCoreAI.Clients;

/// <summary>
/// Builds the middleware pipeline around a provider's raw client. Order (outermost first):
/// logging → OpenTelemetry → function invocation → concurrency slot → provider.
/// </summary>
internal sealed class ChatClientFactory(
    IServiceProvider services,
    IProviderRegistry providers,
    IModelLifecycleManager lifecycle,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILoggerFactory loggerFactory) : IChatClientFactory
{
    public IChatClient Get(string idOrAlias = ModelAlias.Default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrAlias);
        return new AliasChatClient(idOrAlias, this, services, loggerFactory.CreateLogger<AliasChatClient>());
    }

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string idOrAlias = ModelAlias.Embed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrAlias);
        return new AliasEmbeddingGenerator(idOrAlias, this, services);
    }

    public bool TryGet(string idOrAlias, out IChatClient? client)
    {
        client = null;
        if (string.IsNullOrWhiteSpace(idOrAlias))
        {
            return false;
        }

        client = Get(idOrAlias);
        return true;
    }

    /// <summary>Resolves the descriptor for an alias/id, loads it if needed and returns the wrapped client.</summary>
    internal async Task<IChatClient> BuildChatClientAsync(ModelDescriptor model, CancellationToken cancellationToken)
    {
        var loaded = await lifecycle.LoadAsync(model, null, cancellationToken).ConfigureAwait(false);
        var provider = providers.Resolve(model);
        var raw = provider.CreateChatClient(loaded);

        var builder = new ChatClientBuilder(new SlotGuardChatClient(raw, loaded, lifecycle, model.DefaultParameters))
            .UseFunctionInvocation(loggerFactory);

        if (options.CurrentValue.Telemetry.Enabled)
        {
            builder.UseOpenTelemetry(loggerFactory, NetCoreAITelemetry.SourceName, o => o.EnableSensitiveData = options.CurrentValue.Telemetry.EnableSensitiveData);
        }

        builder.UseLogging(loggerFactory);
        return builder.Build(services);
    }

    internal async Task<IEmbeddingGenerator<string, Embedding<float>>> BuildEmbeddingGeneratorAsync(ModelDescriptor model, CancellationToken cancellationToken)
    {
        var loaded = await lifecycle.LoadAsync(model, null, cancellationToken).ConfigureAwait(false);
        var provider = providers.Resolve(model);
        var raw = provider.CreateEmbeddingGenerator(loaded);
        var builder = new EmbeddingGeneratorBuilder<string, Embedding<float>>(raw);
        if (options.CurrentValue.Telemetry.Enabled)
        {
            builder.UseOpenTelemetry(loggerFactory, NetCoreAITelemetry.SourceName);
        }

        builder.UseLogging(loggerFactory);
        return builder.Build(services);
    }
}

/// <summary>Applies per-model default parameters and holds a concurrency slot for the duration of a call.</summary>
internal sealed class SlotGuardChatClient(IChatClient inner, LoadedModel loaded, IModelLifecycleManager lifecycle, ModelParameters defaults) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var slot = await lifecycle.AcquireSlotAsync(loaded, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(messages, ApplyDefaults(options), cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var slot = await lifecycle.AcquireSlotAsync(loaded, cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(messages, ApplyDefaults(options), cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private ChatOptions ApplyDefaults(ChatOptions? options)
    {
        var o = options?.Clone() ?? new ChatOptions();
        o.ModelId ??= loaded.Descriptor.RemoteModelId ?? loaded.Descriptor.Id;
        o.Temperature ??= defaults.Temperature;
        o.TopP ??= defaults.TopP;
        o.TopK ??= defaults.TopK;
        o.MaxOutputTokens ??= defaults.MaxOutputTokens;
        o.FrequencyPenalty ??= defaults.FrequencyPenalty;
        o.PresencePenalty ??= defaults.PresencePenalty;
        o.Seed ??= defaults.Seed;
        if (o.StopSequences is null && defaults.StopSequences is { Count: > 0 })
        {
            o.StopSequences = [.. defaults.StopSequences];
        }

        return o;
    }
}

/// <summary>
/// The client handed to consumers. Resolves the alias on every call so alias changes, reloads and fallbacks apply
/// without the consumer holding stale references. Falls back through the alias' fallback list on provider errors.
/// </summary>
internal sealed class AliasChatClient(string idOrAlias, ChatClientFactory factory, IServiceProvider services, ILogger logger) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(messages, options, (c, m, o, ct) => c.GetResponseAsync(m, o, ct), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streaming cannot fall back mid-stream; we fall back only if the first update never arrives.
        var candidates = await ResolveCandidatesAsync(cancellationToken).ConfigureAwait(false);
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            var startedStreaming = false;
            try
            {
                var client = await factory.BuildChatClientAsync(candidate, cancellationToken).ConfigureAwait(false);
                enumerator = client.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                startedStreaming = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !startedStreaming)
            {
                last = ex;
                logger.LogWarning(ex, "Model {ModelId} failed for alias '{Alias}'; trying next fallback.", candidate.Id, idOrAlias);
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                continue;
            }

            try
            {
                yield return enumerator.Current;
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            yield break;
        }

        throw last ?? new ModelNotFoundException(idOrAlias);
    }

    private async Task<T> ExecuteAsync<T>(IEnumerable<ChatMessage> messages, ChatOptions? options, Func<IChatClient, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var candidates = await ResolveCandidatesAsync(cancellationToken).ConfigureAwait(false);
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var client = await factory.BuildChatClientAsync(candidate, cancellationToken).ConfigureAwait(false);
                return await call(client, messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not RemoteProvidersDisabledException)
            {
                last = ex;
                logger.LogWarning(ex, "Model {ModelId} failed for alias '{Alias}'; trying next fallback.", candidate.Id, idOrAlias);
            }
        }

        throw last ?? new ModelNotFoundException(idOrAlias);
    }

    private async Task<IReadOnlyList<ModelDescriptor>> ResolveCandidatesAsync(CancellationToken cancellationToken)
    {
        var registry = (ModelRegistry)services.GetRequiredService<IModelRegistry>();
        var aliases = await registry.GetAliasesAsync(cancellationToken).ConfigureAwait(false);
        var ids = aliases.TryGetValue(idOrAlias, out var alias) ? new[] { alias.ModelId }.Concat(alias.FallbackModelIds) : [idOrAlias];
        var list = new List<ModelDescriptor>();
        foreach (var id in ids)
        {
            var model = await registry.ResolveDescriptorAsync(id, cancellationToken).ConfigureAwait(false);
            if (model is not null)
            {
                list.Add(model);
            }
        }

        return list.Count == 0 ? throw new ModelNotFoundException(idOrAlias) : list;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("netcoreai", null, idOrAlias) : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}

internal sealed class AliasEmbeddingGenerator(string idOrAlias, ChatClientFactory factory, IServiceProvider services) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var registry = (ModelRegistry)services.GetRequiredService<IModelRegistry>();
        var model = await registry.ResolveDescriptorAsync(idOrAlias, cancellationToken).ConfigureAwait(false) ?? throw new ModelNotFoundException(idOrAlias);
        var generator = await factory.BuildEmbeddingGeneratorAsync(model, cancellationToken).ConfigureAwait(false);
        return await generator.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("netcoreai", null, idOrAlias) : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
