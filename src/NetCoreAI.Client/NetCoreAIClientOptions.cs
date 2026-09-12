using Microsoft.Extensions.DependencyInjection;

namespace NetCoreAI.Client;

/// <summary>Settings for talking to a NetCoreAI host over HTTP (agents, knowledge, chat).</summary>
public sealed class NetCoreAIClientOptions
{
    /// <summary>Base URL including the dashboard path, e.g. https://myapp.example.com/netcoreai</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>API key created in the dashboard (Phase 3). Sent as a bearer token.</summary>
    public string? ApiKey { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

public static class NetCoreAIClientServiceCollectionExtensions
{
    public const string HttpClientName = "NetCoreAI.Client";

    /// <summary>
    /// Registers a named <see cref="HttpClient"/> for a remote NetCoreAI host. <c>IAgentClient</c> and
    /// <c>IKnowledgeClient</c> implementations are added in Phase 2/3; in-process hosts get them from <c>AddNetCoreAI()</c>.
    /// </summary>
    public static IServiceCollection AddNetCoreAIClient(this IServiceCollection services, Action<NetCoreAIClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new NetCoreAIClientOptions();
        configure(options);
        if (options.BaseUrl is null)
        {
            throw new ArgumentException("NetCoreAIClientOptions.BaseUrl is required.", nameof(configure));
        }

        services.AddSingleton(options);
        services.AddHttpClient(HttpClientName, http =>
        {
            http.BaseAddress = new Uri(options.BaseUrl.ToString().TrimEnd('/') + "/");
            http.Timeout = options.Timeout;
            if (!string.IsNullOrEmpty(options.ApiKey))
            {
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            }
        });
        return services;
    }
}
