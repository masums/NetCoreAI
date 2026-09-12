using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreAI.Clients;
using NetCoreAI.Hardware;
using NetCoreAI.Models;
using NetCoreAI.Providers;
using NetCoreAI.Security;
using NetCoreAI.Settings;
using NetCoreAI.Chat;
using NetCoreAI.Storage;

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
