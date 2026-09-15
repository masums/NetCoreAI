using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>Thrown when offline mode (or data residency) blocks an outbound call.</summary>
public sealed class OfflineModeException(string message) : NetCoreAIException(message);

/// <summary>
/// The named HTTP clients NetCoreAI uses for its own outbound calls. Both honour the proxy setting and
/// offline mode; provider packages keep their own clients because their connections carry their own settings.
/// </summary>
internal static class NetCoreAIHttp
{
    /// <summary>Hub browsing: short timeout, small responses.</summary>
    public const string HubClient = "NetCoreAI.Hub";

    /// <summary>Model downloads: no overall timeout, because a single file can take an hour.</summary>
    public const string DownloadClient = "NetCoreAI.Download";

    /// <summary>
    /// Adds offline-mode enforcement to a client NetCoreAI owns.
    /// </summary>
    /// <remarks>
    /// Every outbound client goes through this. A residency switch that covers some of the ways out of
    /// the process is not a residency switch.
    /// </remarks>
    public static IHttpClientBuilder EnforceOfflineMode(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddTransient<OfflineModeHandler>();
        return builder.AddHttpMessageHandler<OfflineModeHandler>();
    }

    public static void AddNetCoreAIHttpClients(this IServiceCollection services)
    {
        services.AddTransient<OfflineModeHandler>();

        services.AddHttpClient(HubClient, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetCoreAI");
        })
            .ConfigurePrimaryHttpMessageHandler(ConfigureProxy)
            .AddHttpMessageHandler<OfflineModeHandler>();

        services.AddHttpClient(DownloadClient, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetCoreAI");
        })
            .ConfigurePrimaryHttpMessageHandler(ConfigureProxy)
            .AddHttpMessageHandler<OfflineModeHandler>();
    }

    private static HttpMessageHandler ConfigureProxy(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptionsMonitor<NetCoreAIOptions>>().CurrentValue;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (options.Network.ProxyUrl is { Length: > 0 } proxy)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }
}

/// <summary>
/// Single enforcement point for offline mode.
/// </summary>
/// <remarks>
/// Attached to every HTTP client NetCoreAI owns — hub browsing, model downloads, tool invocation, the
/// built-in fetch tool — so an air-gapped or data-residency deployment cannot leak a call through a code
/// path that forgot to check the setting. Provider packages bring their own clients and are checked
/// against the same policy when their connection is resolved.
/// </remarks>
internal sealed class OfflineModeHandler(IOptionsMonitor<NetCoreAIOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var network = options.CurrentValue.Network;
        if (!EgressPolicy.IsAllowed(request.RequestUri, network))
        {
            throw new OfflineModeException(EgressPolicy.Refusal(request.RequestUri?.Host ?? "an unnamed host"));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
