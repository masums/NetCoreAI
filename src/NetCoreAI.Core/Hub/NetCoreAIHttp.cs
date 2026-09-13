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
/// Single enforcement point for offline mode: every NetCoreAI-initiated request is refused unless its host
/// is allow-listed, so an air-gapped or data-residency deployment cannot leak a call through a code path
/// that forgot to check the setting.
/// </summary>
internal sealed class OfflineModeHandler(IOptionsMonitor<NetCoreAIOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var network = options.CurrentValue.Network;
        if (network.OfflineMode && request.RequestUri is { } uri && !IsAllowed(uri, network))
        {
            throw new OfflineModeException(
                $"Offline mode is on, so NetCoreAI did not call {uri.Host}. Turn it off in settings, or add the host to Network.AllowedHosts if it is an internal mirror.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static bool IsAllowed(Uri uri, NetworkOptions network)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        foreach (var host in network.AllowedHosts)
        {
            if (host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)
                || (host.StartsWith("*.", StringComparison.Ordinal) && uri.Host.EndsWith(host[1..], StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
