using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Tools;
using NetCoreAI.Tools.BuiltIn;

namespace NetCoreAI;

/// <summary>Registering the tools NetCoreAI ships with.</summary>
public static class BuiltInToolExtensions
{
    /// <summary>
    /// Adds the built-in tools this host has configured.
    /// </summary>
    /// <remarks>
    /// The harmless ones — the date, arithmetic, searching your own documents — are on by default. The two
    /// with reach outside the process are registered only once somebody has said where they may point:
    /// without an allow-list there is no fetch tool, and without a connection there is no SQL tool. A tool
    /// that exists but refuses everything still appears in the designer and still gets tried by a model,
    /// which wastes a call and reads as a fault; not existing is clearer.
    /// </remarks>
    public static NetCoreAIBuilder AddBuiltInTools(this NetCoreAIBuilder builder, Action<BuiltInToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new BuiltInToolOptions();
        configure?.Invoke(options);

        builder.Services.Configure<BuiltInToolOptions>(o =>
        {
            o.DateTime = options.DateTime;
            o.Calculator = options.Calculator;
            o.KnowledgeSearch = options.KnowledgeSearch;
            o.FetchMaxBytes = options.FetchMaxBytes;
            o.Sql = options.Sql;
            foreach (var host in options.FetchAllowedHosts)
            {
                o.FetchAllowedHosts.Add(host);
            }
        });

        if (options.DateTime)
        {
            builder.AddAITool<DateTimeTool>();
        }

        if (options.Calculator)
        {
            builder.AddAITool<CalculatorTool>();
        }

        if (options.KnowledgeSearch)
        {
            builder.AddAITool<KnowledgeSearchTool>();
        }

        if (options.FetchAllowedHosts.Count > 0)
        {
            builder.Services.AddHttpClient(FetchTool.HttpClientName, http =>
            {
                // A model waiting on a slow page is a turn nobody gets an answer from.
                http.Timeout = TimeSpan.FromSeconds(20);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NetCoreAI/1.0 (+https://github.com/masums/NetCoreAI)");
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // A redirect is a second URL, and the allow-list was only asked about the first. Following
                // one would let an approved host hand a model an address nobody approved.
                AllowAutoRedirect = false,
            });

            builder.AddAITool<FetchTool>();
        }

        if (options.Sql is not null)
        {
            builder.AddAITool<SqlQueryTool>();
        }

        return builder;
    }
}
