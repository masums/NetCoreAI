using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreAI.Backends.OpenAICompatible;
using NetCoreAI.Models;
using NetCoreAI.Providers;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Pointing a model somewhere else.
/// </summary>
/// <remarks>
/// The lifecycle manager caches a loaded model by its id, so re-registering the same id with a different
/// connection used to leave every call going to the old one until the host restarted — while the dashboard
/// showed the new setting the whole time, which reads as a provider fault rather than a stale client.
/// </remarks>
public sealed class ModelReRegistrationTests : IAsyncLifetime
{
    private FakeOpenAIServer _first = default!;
    private FakeOpenAIServer _second = default!;
    private WebApplication _app = default!;
    private string _dataDir = "";

    public async ValueTask InitializeAsync()
    {
        _first = await FakeOpenAIServer.StartAsync();
        _second = await FakeOpenAIServer.StartAsync();
        _dataDir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = _dataDir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(_dataDir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();

        await ConnectAsync("one", _first);
        await ConnectAsync("two", _second);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _first.DisposeAsync();
        await _second.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ConnectAsync(string id, FakeOpenAIServer server) =>
        _ = await _app.Services.GetRequiredService<IConnectionManager>().SaveAsync(
            new ProviderConnection
            {
                Id = id,
                Name = id,
                ProviderId = OpenAICompatibleProvider.ProviderId,
                BaseUrl = server.BaseUrl,
            },
            "sk-test",
            Ct);

    private async Task RegisterAsync(string connectionId, string remoteModelId = "fake-chat") =>
        _ = await _app.Services.GetRequiredService<IModelRegistry>().RegisterAsync(new ModelDescriptor
        {
            Id = "default",
            Name = "Chat",
            Format = ModelFormat.Remote,
            ProviderId = OpenAICompatibleProvider.ProviderId,
            ConnectionId = connectionId,
            RemoteModelId = remoteModelId,
            Capabilities = new ModelCapabilities(ModelCapability.Chat),
        }, Ct);

    private async Task AskAsync() =>
        _ = await _app.Services.GetRequiredService<IChatClientFactory>().Get("default")
            .GetResponseAsync("hello", cancellationToken: Ct);

    [Fact]
    public async Task Repointing_a_model_at_another_connection_takes_effect_without_a_restart()
    {
        await RegisterAsync("one");
        await AskAsync();
        Assert.Single(_first.Requests);

        await RegisterAsync("two");
        await AskAsync();

        // The second call went to the new connection, and the old one saw nothing more.
        Assert.Single(_second.Requests);
        Assert.Single(_first.Requests);
    }

    [Fact]
    public async Task Changing_only_the_remote_model_name_also_takes_effect()
    {
        await RegisterAsync("one");
        await AskAsync();

        await RegisterAsync("one", "a-different-model");
        await AskAsync();

        Assert.Equal("a-different-model", _first.Requests[^1]["model"]!.ToString());
    }

    [Fact]
    public async Task Renaming_a_model_does_not_throw_away_the_loaded_one()
    {
        // The other half: unloading on every re-registration would drop a warm local model because
        // somebody fixed a typo in its display name.
        await RegisterAsync("one");
        await AskAsync();

        var lifecycle = _app.Services.GetRequiredService<IModelLifecycleManager>();
        Assert.True(lifecycle.TryGetLoaded("default", out var before));

        var registry = _app.Services.GetRequiredService<IModelRegistry>();
        var descriptor = (await registry.GetAsync("default", Ct))!.Descriptor;
        await registry.UpdateAsync(descriptor with { Name = "Renamed", Notes = "a note" }, Ct);

        Assert.True(lifecycle.TryGetLoaded("default", out var after));
        Assert.Same(before, after);
    }
}
