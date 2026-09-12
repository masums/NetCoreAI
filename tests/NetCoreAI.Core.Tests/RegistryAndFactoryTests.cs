using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Models;
using NetCoreAI.Providers;
using Xunit;

namespace NetCoreAI.Core.Tests;

public class RegistryAndFactoryTests
{
    private static ModelDescriptor Model(string id, string provider = "fake", ModelFormat format = ModelFormat.Gguf) => new()
    {
        Id = id,
        Name = id,
        Format = format,
        ProviderId = provider,
        Path = $"models/{id}.gguf",
        SizeBytes = 1000,
    };

    [Fact]
    public async Task First_registered_chat_model_becomes_default_alias_and_is_usable_through_IChatClient()
    {
        var provider = new FakeProvider();
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(provider));
        var registry = host.Services.GetRequiredService<IModelRegistry>();

        await registry.RegisterAsync(Model("m1"));

        var chat = host.Services.GetRequiredService<IChatClient>();
        var response = await chat.GetResponseAsync("hello");

        Assert.Equal("fake: hello", response.Text);
        Assert.Equal(1, provider.Loads);
        var aliases = await registry.GetAliasesAsync();
        Assert.Equal("m1", aliases[ModelAlias.Default].ModelId);
        Assert.Equal("m1", aliases[ModelAlias.Embed].ModelId);
    }

    [Fact]
    public async Task Model_is_loaded_once_and_status_reflects_lifecycle()
    {
        var provider = new FakeProvider();
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(provider));
        var registry = host.Services.GetRequiredService<IModelRegistry>();
        await registry.RegisterAsync(Model("m1"));
        var chat = host.Services.GetRequiredService<IChatClientFactory>().Get("m1");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => chat.GetResponseAsync("x")));

        Assert.Equal(1, provider.Loads);
        Assert.Equal(ModelStatus.Loaded, (await registry.GetAsync("m1"))!.Status);
        await registry.UnloadAsync("m1");
        Assert.Equal(1, provider.Unloads);
        Assert.Equal(ModelStatus.Available, (await registry.GetAsync("m1"))!.Status);
    }

    [Fact]
    public async Task Alias_fallback_is_used_when_primary_fails()
    {
        var broken = new FakeProvider("broken") { FailWith = new InvalidOperationException("boom") };
        var good = new FakeProvider("good");
        using var host = await TestHost.StartAsync(b =>
        {
            b.Services.AddSingleton<IModelProvider>(broken);
            b.Services.AddSingleton<IModelProvider>(good);
        });
        var registry = host.Services.GetRequiredService<IModelRegistry>();
        await registry.RegisterAsync(Model("primary", "broken"));
        await registry.RegisterAsync(Model("backup", "good"));
        await registry.SetAliasAsync("quality", "primary", ["backup"]);

        var chat = host.Services.GetRequiredService<IChatClientFactory>().Get("quality");
        var response = await chat.GetResponseAsync("hi");

        Assert.Equal("good: hi", response.Text);
        Assert.Contains("hi", broken.Calls);
    }

    [Fact]
    public async Task Streaming_works_through_the_pipeline_and_reports_usage()
    {
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(new FakeProvider()));
        await host.Services.GetRequiredService<IModelRegistry>().RegisterAsync(Model("m1"));
        var chat = host.Services.GetRequiredService<IChatClientFactory>().Get("m1");

        var updates = await chat.GetStreamingResponseAsync("a b").ToListAsync();
        var text = string.Concat(updates.Select(u => u.Text));
        Assert.Equal("fake: a b ", text);
        Assert.Contains(updates, u => u.Contents.OfType<UsageContent>().Any());
    }

    [Fact]
    public async Task Unknown_alias_throws_ModelNotFound()
    {
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(new FakeProvider()));
        var chat = host.Services.GetRequiredService<IChatClientFactory>().Get("nope");
        await Assert.ThrowsAsync<ModelNotFoundException>(() => chat.GetResponseAsync("x"));
    }

    [Fact]
    public async Task Memory_budget_refuses_load_without_crashing()
    {
        var provider = new FakeProvider { MemoryPerLoad = 10_000 };
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(provider), o => o.Models.MemoryBudgetBytes = 5_000);
        var registry = host.Services.GetRequiredService<IModelRegistry>();
        await registry.RegisterAsync(Model("big"));

        var ex = await Assert.ThrowsAsync<ModelWontFitException>(() => registry.LoadAsync("big"));
        Assert.Equal(0, provider.Loads);
        Assert.Contains("MB", ex.Message);
        Assert.Equal(ModelStatus.Error, (await registry.GetAsync("big"))!.Status);
    }

    [Fact]
    public async Task Provider_resolution_prefers_explicit_id_then_registration_order_and_CanLoad()
    {
        var first = new FakeProvider("first") { CanLoadResult = false };
        var second = new FakeProvider("second");
        using var host = await TestHost.StartAsync(b =>
        {
            b.Services.AddSingleton<IModelProvider>(first);
            b.Services.AddSingleton<IModelProvider>(second);
        });
        var providers = host.Services.GetRequiredService<IProviderRegistry>();

        Assert.Equal("second", providers.Resolve(Model("x", provider: "")).Id);
        Assert.Equal("first", providers.Resolve(Model("x", provider: "first")).Id);
        Assert.Throws<ProviderNotFoundException>(() => providers.Resolve(Model("x", provider: "", format: ModelFormat.Onnx)));
    }

    [Fact]
    public async Task Disabling_remote_providers_blocks_them_with_a_clear_error()
    {
        var remote = new FakeProvider("cloud", ProviderKind.Remote, ModelFormat.Remote);
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(remote), o => o.Providers.RemoteEnabled = false);
        var providers = host.Services.GetRequiredService<IProviderRegistry>();

        Assert.False(providers.IsEnabled(remote));
        Assert.Throws<RemoteProvidersDisabledException>(() => providers.Resolve(Model("gpt", "cloud", ModelFormat.Remote)));
    }

    [Fact]
    public async Task Removing_a_model_unloads_it_and_drops_its_aliases()
    {
        var provider = new FakeProvider();
        using var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IModelProvider>(provider));
        var registry = host.Services.GetRequiredService<IModelRegistry>();
        await registry.RegisterAsync(Model("m1"));
        await registry.LoadAsync("m1");

        await registry.RemoveAsync("m1", deleteFiles: false);

        Assert.Equal(1, provider.Unloads);
        Assert.Null(await registry.GetAsync("m1"));
        Assert.Empty(await registry.GetAliasesAsync());
        Assert.Empty(host.Services.GetRequiredService<IModelLifecycleManager>().Loaded);
    }
}
