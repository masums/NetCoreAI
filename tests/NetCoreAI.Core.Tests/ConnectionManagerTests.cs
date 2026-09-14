using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Providers;
using NetCoreAI.Security;
using Xunit;

namespace NetCoreAI.Core.Tests;

public class ConnectionManagerTests
{
    /// <summary>Remote provider built on the shared base class; lists two models and records the secret it saw.</summary>
    private sealed class FakeRemote(IMetadataStore store, ISecretResolver secrets, IOptionsMonitor<NetCoreAIOptions> options)
        : RemoteModelProviderBase(store, secrets, options)
    {
        public string? LastSecret { get; private set; }
        public override string Id => "cloud";
        public override string DisplayName => "Cloud";
        public override IReadOnlyList<ProviderPreset> Presets { get; } = [new("main", "Main", "https://cloud.example/v1", true, true, ["fallback-model"])];
        protected override ModelCapabilities DefaultCapabilities(string remoteModelId) => new(ModelCapability.Chat);
        protected override object CreateHandle(ProviderConnection connection, ModelDescriptor model) => LastSecret = Secret(connection) ?? "";
        public override IChatClient CreateChatClient(LoadedModel model) => throw new NotSupportedException();
        public override IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LoadedModel model) => throw new NotSupportedException();
        public override Task<ConnectionTestResult> TestConnectionAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(Secret(connection) == "sk-ok" ? ConnectionTestResult.Ok(["a", "b"], TimeSpan.Zero) : ConnectionTestResult.Failed(ConnectionHealth.AuthFailed, "bad key", TimeSpan.Zero));
        public override Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(ProviderConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModelDescriptor>>([MakeDescriptor(connection, "a"), MakeDescriptor(connection, "b")]);
    }

    [Fact]
    public async Task Save_protects_secret_and_keeps_it_when_updating_without_one()
    {
        using var host = await TestHost.StartAsync(b => b.AddProvider<FakeRemote>());
        var manager = host.Services.GetRequiredService<IConnectionManager>();

        var saved = await manager.SaveAsync(new ProviderConnection { Id = "", Name = "Cloud Prod", ProviderId = "cloud", Preset = "main" }, "sk-ok");

        Assert.Equal("cloud-prod", saved.Id);
        Assert.Equal("https://cloud.example/v1", saved.BaseUrl);
        Assert.NotNull(saved.ProtectedSecret);
        Assert.DoesNotContain("sk-ok", saved.ProtectedSecret);
        Assert.Equal("sk-ok", manager.ResolveSecret(saved));

        var renamed = await manager.SaveAsync(saved with { Name = "Renamed" }, plainSecret: null);
        Assert.Equal("sk-ok", manager.ResolveSecret(renamed));

        var cleared = await manager.SaveAsync(renamed, plainSecret: "");
        Assert.Null(cleared.ProtectedSecret);
    }

    [Fact]
    public async Task Environment_variable_overrides_stored_secret()
    {
        using var host = await TestHost.StartAsync(b => b.AddProvider<FakeRemote>(), config: new Dictionary<string, string?> { ["NetCoreAI:Connections:Cloud_Prod:Secret"] = "sk-from-env" });
        var manager = host.Services.GetRequiredService<IConnectionManager>();
        var saved = await manager.SaveAsync(new ProviderConnection { Id = "", Name = "Cloud Prod", ProviderId = "cloud", Preset = "main" }, "sk-stored");
        Assert.Equal("sk-from-env", manager.ResolveSecret(saved));
    }

    [Fact]
    public async Task Test_updates_health_and_sync_registers_models_idempotently()
    {
        using var host = await TestHost.StartAsync(b => b.AddProvider<FakeRemote>());
        var manager = host.Services.GetRequiredService<IConnectionManager>();
        var registry = host.Services.GetRequiredService<IModelRegistry>();
        var saved = await manager.SaveAsync(new ProviderConnection { Id = "", Name = "c", ProviderId = "cloud", Preset = "main" }, "sk-ok");

        var result = await manager.TestAsync(saved.Id);
        Assert.True(result.Success);
        Assert.Equal(ConnectionHealth.Healthy, (await manager.GetAsync(saved.Id))!.Health);

        await manager.SyncModelsAsync(saved.Id);
        await manager.SyncModelsAsync(saved.Id);
        var models = await registry.ListAsync();
        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.Equal(saved.Id, m.Descriptor.ConnectionId));
        Assert.All(models, m => Assert.Equal(ModelFormat.Remote, m.Descriptor.Format));

        await manager.DeleteAsync(saved.Id);
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Loading_a_remote_model_resolves_its_connection_and_costs_no_memory()
    {
        using var host = await TestHost.StartAsync(b => b.AddProvider<FakeRemote>());
        var manager = host.Services.GetRequiredService<IConnectionManager>();
        var registry = host.Services.GetRequiredService<IModelRegistry>();
        var saved = await manager.SaveAsync(new ProviderConnection { Id = "", Name = "c", ProviderId = "cloud", Preset = "main" }, "sk-ok");
        var models = await manager.SyncModelsAsync(saved.Id);

        var loaded = await registry.LoadAsync(models[0].Id);

        Assert.Equal(0, loaded.MemoryBytes);
        var provider = host.Services.GetServices<IModelProvider>().OfType<FakeRemote>().Single();
        Assert.Equal("sk-ok", provider.LastSecret);
    }
}
