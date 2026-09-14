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
    /// Registers <see cref="IKnowledgeClient"/> and <see cref="IAgentClient"/> against a remote NetCoreAI
    /// host, over a named <see cref="HttpClient"/>. In-process hosts get the same interfaces from
    /// <c>AddNetCoreAI()</c> instead, so application code is identical either way.
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

        // Registered against the interfaces, so swapping a client app between "talks to a remote host" and
        // "hosts NetCoreAI itself" is a change of registration and nothing else.
        services.AddSingleton<IKnowledgeClient, HttpKnowledgeClient>();
        services.AddSingleton<IAgentClient, HttpAgentClient>();
        return services;
    }
}
