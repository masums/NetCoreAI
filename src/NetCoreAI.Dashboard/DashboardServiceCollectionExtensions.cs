using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetCoreAI.Dashboard.Rendering;

namespace NetCoreAI;

public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers dashboard services. Called automatically by <c>AddNetCoreAI()</c> when this package is referenced;
    /// call it explicitly only when you reference NetCoreAI.Dashboard without the meta-package and want to be sure.
    /// </summary>
    public static NetCoreAIBuilder AddNetCoreAIDashboard(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<PageRenderer>();
        builder.Services.TryAddSingleton<EmbeddedAssets>();
        builder.Services.AddHealthChecks();
        return builder;
    }
}
