using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Clients;
using NetCoreAI.Hardware;
using NetCoreAI.Hub;
using NetCoreAI.Jobs;
using NetCoreAI.Knowledge;
using NetCoreAI.Models;
using NetCoreAI.Providers;
using NetCoreAI.Security;
using NetCoreAI.Settings;
using NetCoreAI.Chat;
using NetCoreAI.Storage;
using NetCoreAI.Telemetry;

namespace NetCoreAI;

public static class NetCoreAIServiceCollectionExtensions
{
    /// <summary>
    /// Registers NetCoreAI core services. Idempotent: calling it twice returns a builder over the same registrations.
    /// Options are bound from the "NetCoreAI" configuration section first, then from <paramref name="configure"/>.
    /// </summary>
    public static NetCoreAIBuilder AddNetCoreAI(this IServiceCollection services, Action<NetCoreAIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new NetCoreAIBuilder(services);
        if (services.Any(d => d.ServiceType == typeof(NetCoreAIMarker)))
        {
            if (configure is not null)
            {
                services.Configure(configure);
            }

            return builder;
        }

        services.AddSingleton<NetCoreAIMarker>();
        services.AddOptions<NetCoreAIOptions>()
            .Configure<IConfiguration>((o, config) => config.GetSection(NetCoreAIOptions.SectionName).Bind(o))
            .PostConfigure<IHostEnvironment>((o, env) =>
            {
                if (!Path.IsPathRooted(o.DataDirectory))
                {
                    o.DataDirectory = Path.GetFullPath(Path.Combine(env.ContentRootPath, o.DataDirectory));
                }
            });
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddLogging();
        services.AddDataProtection();
        services.TryAddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.TryAddSingleton<ISecretResolver, SecretResolver>();

        // Storage: in-memory unless a storage package replaces it (NetCoreAI meta-package registers SQLite).
        services.TryAddSingleton<IMetadataStore, InMemoryMetadataStore>();
        services.AddHostedService<MetadataStoreInitializer>();

        services.TryAddSingleton<IProviderRegistry, ProviderRegistry>();
        services.TryAddSingleton<IHardwareProbe, HardwareProbe>();
        services.TryAddSingleton<IFitEstimator, FitEstimator>();

        services.TryAddSingleton<ModelLifecycleManager>();
        services.TryAddSingleton<IModelLifecycleManager>(sp => sp.GetRequiredService<ModelLifecycleManager>());
        services.AddHostedService(sp => sp.GetRequiredService<ModelLifecycleManager>());

        services.TryAddSingleton<ModelRegistry>();
        services.TryAddSingleton<IModelRegistry>(sp => sp.GetRequiredService<ModelRegistry>());
        services.AddHostedService(sp => sp.GetRequiredService<ModelRegistry>());

        // Hub: outbound HTTP (proxy + offline enforcement), Hugging Face as the default source, curated list.
        services.AddNetCoreAIHttpClients();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelSource, HuggingFaceClient>());
        services.TryAddSingleton<CuratedManifestService>();
        services.TryAddSingleton<IHubService, HubService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelSource, UrlModelSource>());

        // Downloads: one worker, resumable and verified, registering what it fetches in the model registry.
        services.TryAddSingleton<BandwidthLimiter>();
        services.TryAddSingleton<FileDownloader>();
        services.TryAddSingleton<DownloadManager>();
        services.TryAddSingleton<IDownloadManager>(sp => sp.GetRequiredService<DownloadManager>());
        services.AddHostedService(sp => sp.GetRequiredService<DownloadManager>());
        services.TryAddSingleton<IModelImporter, ModelImporter>();
        services.TryAddSingleton<IModelUploadService, ModelUploadService>();
        services.TryAddSingleton<IStorageService, StorageService>();

        // Knowledge: background jobs, the chunkers, and the extract-chunk-embed-store pipeline.
        services.TryAddSingleton<BackgroundJobRunner>();
        services.TryAddSingleton<IBackgroundJobRunner>(sp => sp.GetRequiredService<BackgroundJobRunner>());
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundJobRunner>());
        // Formats that need no third-party dependency; NetCoreAI.Documents adds PDF, Office and HTML.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, PlainTextExtractor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, CsvExtractor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, JsonExtractor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChunker, RecursiveStructureChunker>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChunker, FixedSizeChunker>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChunker, SentenceChunker>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChunker, RowChunker>());
        services.TryAddSingleton<IIngestionPipeline, IngestionPipeline>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSource, FileDataSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSource, RestApiDataSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataSource, SqlDataSource>());
        services.AddHostedService<SyncScheduler>();
        services.TryAddSingleton<IKnowledgeService, KnowledgeService>();
        services.TryAddSingleton<IRetriever, Retriever>();
        services.TryAddSingleton<IRagChatClientFactory, RagChatClientFactory>();
        services.TryAddSingleton<IKnowledgeClient, KnowledgeClient>();

        // Tools: discovery only reads the route table, so it costs nothing until something asks.
        services.TryAddSingleton<NetCoreAI.Tools.IEndpointDiscovery, NetCoreAI.Tools.EndpointDiscoveryService>();
        services.TryAddSingleton<NetCoreAI.Tools.IToolService, NetCoreAI.Tools.ToolService>();
        services.TryAddSingleton<NetCoreAI.Tools.IInProcessToolTransport, NetCoreAI.Tools.InProcessToolTransport>();
        services.TryAddSingleton<NetCoreAI.Tools.IToolInvoker, NetCoreAI.Tools.ToolInvoker>();
        services.TryAddSingleton<NetCoreAI.Tools.IToolRegistry, NetCoreAI.Tools.ToolRegistry>();
        services.TryAddSingleton<NetCoreAI.Tools.ICodeToolSource, NetCoreAI.Tools.CodeToolSource>();
        services.TryAddSingleton<NetCoreAI.Tools.IToolTester, NetCoreAI.Tools.ToolTester>();

        // Agents: model, retrieval and tools assembled per run, with a trace written for each.
        services.TryAddSingleton<NetCoreAI.Agents.IAgentEngine, NetCoreAI.Agents.AgentEngine>();
        services.TryAddSingleton<NetCoreAI.Agents.IAgentService, NetCoreAI.Agents.AgentService>();
        services.TryAddSingleton<IAgentClient, NetCoreAI.Agents.AgentClient>();
        services.TryAddSingleton<NetCoreAI.Security.IApiKeyService, NetCoreAI.Security.ApiKeyService>();

        // Registered here rather than left to the host: without it the in-process IAgentClient cannot see
        // the current request, so an agent run from inside one would silently act as nobody — no
        // ACL-restricted document, no agent with access tags, and no loopback address for its tools. The
        // accessor is a singleton holding an AsyncLocal, so a host that never uses it pays nothing.
        services.AddHttpContextAccessor();

        // Named, so a host can give tool traffic its own handlers — a proxy, a client certificate, a
        // retry policy — without touching the clients the model providers use.
        services.AddHttpClient(NetCoreAI.Tools.ToolInvoker.HttpClientName);

        // Live traffic for the overview page, and per-call cost from connection pricing.
        services.TryAddSingleton<IUsageTracker, UsageTracker>();
        services.TryAddSingleton<ICostEstimator, CostEstimator>();

        services.TryAddSingleton<IConnectionManager, ConnectionManager>();
        services.TryAddSingleton<SettingsService>();
        services.TryAddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.TryAddSingleton<SettingsOverrides>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<NetCoreAIOptions>, SettingsPostConfigure>());
        services.TryAddSingleton<IChatService, ChatService>();
        services.TryAddSingleton<ChatClientFactory>();
        services.TryAddSingleton<IChatClientFactory>(sp => sp.GetRequiredService<ChatClientFactory>());

        // Convenience: IChatClient / IEmbeddingGenerator resolve to the "default" / "embed" aliases.
        services.TryAddSingleton(sp => sp.GetRequiredService<IChatClientFactory>().Get());
        services.TryAddSingleton(sp => sp.GetRequiredService<IChatClientFactory>().GetEmbeddingGenerator());

        AutoRegistration.Apply(builder);
        return builder;
    }

    /// <summary>Marker so <see cref="AddNetCoreAI"/> is idempotent and <c>MapNetCoreAI()</c> can verify registration.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public sealed class NetCoreAIMarker;

    /// <summary>Creates the data directory and initialises the metadata store before any other NetCoreAI hosted service runs.</summary>
    internal sealed class MetadataStoreInitializer(IMetadataStore store, SettingsService settings, IOptions<NetCoreAIOptions> options, ILogger<MetadataStoreInitializer> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var dir = options.Value.DataDirectory;
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await settings.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (store is InMemoryMetadataStore)
            {
                logger.LogWarning("NetCoreAI is using the in-memory metadata store: models, connections and sessions will not survive a restart. Reference the NetCoreAI meta-package or a NetCoreAI.Storage.* package.");
            }

            logger.LogInformation("NetCoreAI data directory: {DataDirectory}", dir);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
