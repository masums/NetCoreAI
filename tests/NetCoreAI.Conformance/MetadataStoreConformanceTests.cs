using Xunit;

namespace NetCoreAI.Conformance;

/// <summary>
/// Contract every <see cref="IMetadataStore"/> implementation must satisfy. Derive, implement <see cref="CreateStoreAsync"/>,
/// and the tests run against your store.
/// </summary>
public abstract class MetadataStoreConformanceTests : IAsyncLifetime
{
    protected IMetadataStore Store { get; private set; } = default!;

    protected abstract Task<IMetadataStore> CreateStoreAsync();

    public async ValueTask InitializeAsync()
    {
        Store = await CreateStoreAsync();
        await Store.InitializeAsync();
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected static ModelDescriptor Model(string id, ModelCapability caps = ModelCapability.Chat) => new()
    {
        Id = id,
        Name = id.ToUpperInvariant(),
        Format = ModelFormat.Gguf,
        ProviderId = "test",
        Path = $"models/{id}.gguf",
        SizeBytes = 1234,
        Quantization = "Q4_K_M",
        ContextLength = 4096,
        Capabilities = new ModelCapabilities(caps, 4096),
        Tags = ["a", "b"],
        DefaultParameters = new ModelParameters { Temperature = 0.7f, StopSequences = ["</s>"] },
    };

    [Fact]
    public async Task Store_is_healthy_after_initialize() => Assert.True(await Store.IsHealthyAsync());

    [Fact]
    public async Task Models_roundtrip_all_fields()
    {
        var model = Model("m1");
        await Store.Models.UpsertAsync(model);
        var read = await Store.Models.GetAsync("m1");
        Assert.NotNull(read);
        // Records with list members are not structurally equal after a roundtrip; compare the serialized shape.
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(model), System.Text.Json.JsonSerializer.Serialize(read));
        Assert.Equal(["a", "b"], read.Tags);
        Assert.Equal(["</s>"], read.DefaultParameters.StopSequences);
    }

    [Fact]
    public async Task Models_upsert_replaces_and_list_sorts_by_name()
    {
        await Store.Models.UpsertAsync(Model("zeta"));
        await Store.Models.UpsertAsync(Model("alpha"));
        await Store.Models.UpsertAsync(Model("zeta") with { Name = "AAA" });
        var list = await Store.Models.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("AAA", list[0].Name);
    }

    [Fact]
    public async Task Models_delete_is_idempotent()
    {
        await Store.Models.UpsertAsync(Model("d1"));
        await Store.Models.DeleteAsync("d1");
        await Store.Models.DeleteAsync("d1");
        Assert.Null(await Store.Models.GetAsync("d1"));
    }

    [Fact]
    public async Task Aliases_roundtrip_with_fallbacks()
    {
        await Store.Aliases.UpsertAsync(new ModelAlias("fast", "m1", ["m2", "m3"]));
        var list = await Store.Aliases.ListAsync();
        var alias = Assert.Single(list);
        Assert.Equal("fast", alias.Alias);
        Assert.Equal(["m2", "m3"], alias.FallbackModelIds);
        await Store.Aliases.DeleteAsync("fast");
        Assert.Empty(await Store.Aliases.ListAsync());
    }

    [Fact]
    public async Task Connections_roundtrip_including_settings_and_secret()
    {
        var c = new ProviderConnection
        {
            Id = "c1",
            Name = "OpenAI prod",
            ProviderId = "openai",
            Preset = "openai",
            BaseUrl = "https://api.openai.com/v1",
            ProtectedSecret = "dp:abc",
            Settings = new Dictionary<string, string> { ["api-version"] = "2024-10-21" },
            Timeout = TimeSpan.FromSeconds(30),
            CostPer1KInputTokens = 0.15m,
            Health = ConnectionHealth.Healthy,
        };
        await Store.Connections.UpsertAsync(c);
        var read = await Store.Connections.GetAsync("c1");
        Assert.NotNull(read);
        Assert.Equal("dp:abc", read.ProtectedSecret);
        Assert.Equal("2024-10-21", read.Settings["api-version"]);
        Assert.Equal(TimeSpan.FromSeconds(30), read.Timeout);
        Assert.Equal(0.15m, read.CostPer1KInputTokens);
        Assert.Equal(ConnectionHealth.Healthy, read.Health);
    }

    [Fact]
    public async Task Sessions_and_messages_roundtrip_in_order()
    {
        var session = new ChatSession { Id = "s1", UserId = "u1", Title = "Hello", ModelId = "m1" };
        await Store.Sessions.UpsertAsync(session);
        await Store.Sessions.UpsertAsync(new ChatSession { Id = "s2", UserId = "u2", Title = "Other" });

        for (var i = 0; i < 3; i++)
        {
            await Store.Sessions.AppendMessageAsync(new ChatMessageRecord { Id = $"msg{i}", SessionId = "s1", Role = i % 2 == 0 ? "user" : "assistant", Content = $"text {i}", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i) });
        }

        var messages = await Store.Sessions.GetMessagesAsync("s1");
        Assert.Equal(["msg0", "msg1", "msg2"], messages.Select(m => m.Id));

        var forUser = await Store.Sessions.ListAsync("u1");
        Assert.Single(forUser);
        Assert.Equal(2, (await Store.Sessions.ListAsync(null)).Count);

        await Store.Sessions.ReplaceMessagesAsync("s1", messages.Take(1).ToList());
        Assert.Single(await Store.Sessions.GetMessagesAsync("s1"));

        await Store.Sessions.DeleteAsync("s1");
        Assert.Null(await Store.Sessions.GetAsync("s1"));
        Assert.Empty(await Store.Sessions.GetMessagesAsync("s1"));
    }

    [Fact]
    public async Task Settings_set_get_and_remove()
    {
        await Store.Settings.SetAsync("Network:OfflineMode", "True");
        Assert.Equal("True", await Store.Settings.GetAsync("Network:OfflineMode"));
        await Store.Settings.SetAsync("Network:OfflineMode", "False");
        Assert.Equal("False", (await Store.Settings.GetAllAsync())["Network:OfflineMode"]);
        await Store.Settings.SetAsync("Network:OfflineMode", null);
        Assert.Null(await Store.Settings.GetAsync("Network:OfflineMode"));
    }

    [Fact]
    public async Task Downloads_roundtrip_and_list_newest_first()
    {
        var older = new DownloadJob { Id = "j1", Request = new DownloadRequest("huggingface", "org/repo", ["a.gguf"]), CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1), BytesTotal = 100, BytesDone = 50 };
        var newer = new DownloadJob { Id = "j2", Request = new DownloadRequest("huggingface", "org/repo2", ["b.gguf"]), State = DownloadState.Completed };
        await Store.Downloads.UpsertAsync(older);
        await Store.Downloads.UpsertAsync(newer);
        var list = await Store.Downloads.ListAsync();
        Assert.Equal(["j2", "j1"], list.Select(j => j.Id));
        Assert.Equal(0.5, (await Store.Downloads.GetAsync("j1"))!.Progress);
        await Store.Downloads.DeleteAsync("j1");
        Assert.Null(await Store.Downloads.GetAsync("j1"));
    }
}
