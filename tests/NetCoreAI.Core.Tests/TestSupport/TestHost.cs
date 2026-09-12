using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Core.Tests.TestSupport;

public sealed class FakeHardwareProbe : IHardwareProbe
{
    public HardwareInfo Info { get; set; } = new()
    {
        OperatingSystem = "TestOS",
        Architecture = "X64",
        LogicalCores = 8,
        TotalRamBytes = 16L * 1024 * 1024 * 1024,
        AvailableRamBytes = 12L * 1024 * 1024 * 1024,
    };

    public ValueTask<HardwareInfo> ProbeAsync(bool refresh = false, CancellationToken cancellationToken = default) => ValueTask.FromResult(Info);
}

/// <summary>Builds a minimal generic host with NetCoreAI registered; hosted services started so stores are initialised.</summary>
public static class TestHost
{
    public static async Task<IHost> StartAsync(Action<NetCoreAIBuilder>? configure = null, Action<NetCoreAIOptions>? options = null, IDictionary<string, string?>? config = null)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Test", ContentRootPath = dataDir });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(config ?? new Dictionary<string, string?>());
        var ai = builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = dataDir;
            o.Models.IdleUnloadTimeout = null;
            options?.Invoke(o);
        });
        // Deterministic, instant hardware for unit tests (the real probe shells out to nvidia-smi/PowerShell).
        builder.Services.AddSingleton<IHardwareProbe>(new FakeHardwareProbe());
        configure?.Invoke(ai);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }
}
