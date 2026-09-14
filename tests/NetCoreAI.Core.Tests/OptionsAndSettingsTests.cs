using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Settings;
using Xunit;

namespace NetCoreAI.Core.Tests;

public class OptionsAndSettingsTests
{
    [Fact]
    public async Task Options_bind_from_configuration_section_then_code_and_data_directory_is_rooted()
    {
        using var host = await TestHost.StartAsync(config: new Dictionary<string, string?>
        {
            ["NetCoreAI:Dashboard:Path"] = "/ai",
            ["NetCoreAI:Models:DefaultContextSize"] = "1024",
            ["NetCoreAI:Network:HuggingFaceToken"] = "hf_env",
        });
        var o = host.Services.GetRequiredService<IOptions<NetCoreAIOptions>>().Value;

        Assert.Equal("/ai", o.Dashboard.Path);
        Assert.Equal(1024, o.Models.DefaultContextSize);
        Assert.True(Path.IsPathRooted(o.DataDirectory));
        Assert.True(Directory.Exists(o.DataDirectory));
        Assert.Equal("hf_env", await host.Services.GetRequiredService<ISettingsService>().GetHuggingFaceTokenAsync());
    }

    [Fact]
    public void AddNetCoreAI_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddNetCoreAI();
        var count = services.Count;
        services.AddNetCoreAI(o => o.Dashboard.Title = "x");
        Assert.True(services.Count <= count + 1, "second call must not duplicate registrations");
    }

    [Fact]
    public async Task Settings_updates_persist_and_override_options_immediately()
    {
        using var host = await TestHost.StartAsync();
        var settings = host.Services.GetRequiredService<ISettingsService>();
        var monitor = host.Services.GetRequiredService<IOptionsMonitor<NetCoreAIOptions>>();

        var updated = await settings.UpdateAsync(new NetCoreAISettingsUpdate { OfflineMode = true, DefaultContextSize = 2048, IdleUnloadMinutes = 5, HuggingFaceToken = "hf_secret", DisabledProviders = ["onnx"] });

        Assert.True(updated.OfflineMode);
        Assert.True(updated.HasHuggingFaceToken);
        Assert.True(monitor.CurrentValue.Network.OfflineMode);
        Assert.Equal(2048, monitor.CurrentValue.Models.DefaultContextSize);
        Assert.Equal(TimeSpan.FromMinutes(5), monitor.CurrentValue.Models.IdleUnloadTimeout);
        Assert.Contains("onnx", monitor.CurrentValue.Providers.Disabled);
        Assert.Equal("hf_secret", await settings.GetHuggingFaceTokenAsync());

        // Stored encrypted, never in clear text.
        var raw = await host.Services.GetRequiredService<IMetadataStore>().Settings.GetAsync("Network:HuggingFaceToken");
        Assert.NotNull(raw);
        Assert.DoesNotContain("hf_secret", raw);

        var cleared = await settings.UpdateAsync(new NetCoreAISettingsUpdate { HuggingFaceToken = "", ClearIdleUnload = true });
        Assert.False(cleared.HasHuggingFaceToken);
        Assert.Null(monitor.CurrentValue.Models.IdleUnloadTimeout);
    }

    [Fact]
    public async Task Secret_protector_roundtrips_and_passes_through_unprotected_values()
    {
        using var host = await TestHost.StartAsync();
        var protector = host.Services.GetRequiredService<ISecretProtector>();
        var protectedValue = protector.Protect("sk-123");
        Assert.StartsWith("dp:", protectedValue);
        Assert.NotEqual("sk-123", protectedValue);
        Assert.Equal("sk-123", protector.Unprotect(protectedValue));
        Assert.Equal("legacy", protector.Unprotect("legacy"));
    }
}
