using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetCoreAI.Storage;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Moving what somebody built from one host to another, and taking a copy of a running one.
/// </summary>
public sealed class BundleTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = [];
    private readonly List<string> _dirs = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<WebApplication> HostAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = dir;
            o.Dashboard.AllowAnonymous = true;
        })
            .AddSqliteStorage($"Data Source={Path.Combine(dir, "netcoreai.db")};Pooling=False")
            .AddOpenAICompatibleBackend();

        var app = builder.Build();
        _apps.Add(app);
        app.MapNetCoreAI();
        await app.StartAsync();
        return app;
    }

    private static IBundleService Bundles(WebApplication app) => app.Services.GetRequiredService<IBundleService>();

    private static IMetadataStore Store(WebApplication app) => app.Services.GetRequiredService<IMetadataStore>();

    /// <summary>A host with one of everything, wired together the way a real one would be.</summary>
    private static async Task FillAsync(WebApplication app)
    {
        var store = Store(app);

        await store.Models.UpsertAsync(
            new ModelDescriptor { Id = "gpt", Name = "GPT", Format = ModelFormat.Remote, ProviderId = "openai", ConnectionId = "main" },
            Ct);

        await store.Knowledge.UpsertAsync(new KnowledgeBase { Id = "docs", Name = "Docs", EmbeddingModel = "gpt" }, Ct);
        await store.Knowledge.UpsertSourceAsync(new DataSourceDefinition { Id = "files", Name = "Uploads", KnowledgeBaseId = "docs", Type = "files" }, Ct);
        await store.Tools.UpsertAsync(
            new ToolDefinition { Id = "t1", Name = "lookup_order", Description = "Look one up.", Route = "/api/orders/{id}" },
            Ct);

        await store.Agents.UpsertAsync(
            new AgentDefinition
            {
                Id = "support",
                Name = "Support",
                Model = "gpt",
                ToolIds = ["t1"],
                Knowledge = [new AgentKnowledge("docs")],
            },
            Ct);
    }

    // ---------- what travels ----------

    [Fact]
    public async Task An_export_carries_what_a_person_built()
    {
        var app = await HostAsync();
        await FillAsync(app);

        var bundle = await Bundles(app).ExportAsync(cancellationToken: Ct);

        Assert.Single(bundle.Agents);
        Assert.Single(bundle.Tools);
        Assert.Single(bundle.KnowledgeBases);
        Assert.Single(bundle.DataSources);
    }

    [Fact]
    public async Task An_export_of_one_agent_brings_only_what_that_agent_uses()
    {
        var app = await HostAsync();
        await FillAsync(app);
        await Store(app).Agents.UpsertAsync(new AgentDefinition { Id = "other", Name = "Other", Model = "gpt" }, Ct);
        await Store(app).Tools.UpsertAsync(new ToolDefinition { Id = "t2", Name = "unused", Route = "/x" }, Ct);

        var bundle = await Bundles(app).ExportAsync(["support"], cancellationToken: Ct);

        // Exporting the whole host when somebody asked for one agent hands them a file full of things
        // they did not mean to move.
        Assert.Equal("support", Assert.Single(bundle.Agents).Id);
        Assert.Equal("lookup_order", Assert.Single(bundle.Tools).Name);
    }

    [Fact]
    public async Task Documents_conversations_and_runs_do_not_travel()
    {
        var app = await HostAsync();
        await FillAsync(app);

        var bundle = await Bundles(app).ExportAsync(cancellationToken: Ct);
        var json = JsonSerializer.Serialize(bundle);

        // They belong to the environment they happened in. Carrying a staging server's conversations into
        // production is not a promotion, it is a leak.
        Assert.DoesNotContain("\"runs\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sessions\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"documents\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connection_is_named_but_its_secret_never_leaves()
    {
        var app = await HostAsync();
        await app.Services.GetRequiredService<NetCoreAI.Providers.IConnectionManager>().SaveAsync(
            new ProviderConnection { Id = "main", Name = "Main", ProviderId = "openai", BaseUrl = "https://api.example.com" },
            "sk-super-secret-value",
            Ct);

        var json = JsonSerializer.Serialize(await Bundles(app).ExportAsync(cancellationToken: Ct));

        // A bundle is a file people email each other and commit to repositories, and it is designed on
        // the assumption that they will.
        Assert.Contains("main", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-super-secret-value", json, StringComparison.Ordinal);
    }

    // ---------- arriving ----------

    [Fact]
    public async Task A_bundle_imports_into_an_empty_host()
    {
        var source = await HostAsync();
        await FillAsync(source);
        var bundle = await Bundles(source).ExportAsync(cancellationToken: Ct);

        var target = await HostAsync();
        var result = await Bundles(target).ImportAsync(bundle, ImportMode.SkipExisting, Ct);

        Assert.Contains("agent support", result.Created);
        Assert.Equal("Support", (await Store(target).Agents.GetAsync("support", Ct))!.Name);
        Assert.NotNull(await Store(target).Knowledge.GetAsync("docs", Ct));
    }

    [Fact]
    public async Task Validating_writes_nothing()
    {
        var source = await HostAsync();
        await FillAsync(source);
        var bundle = await Bundles(source).ExportAsync(cancellationToken: Ct);

        var target = await HostAsync();
        var result = await Bundles(target).ImportAsync(bundle, ImportMode.Validate, Ct);

        // The sensible first run: say what would happen, touch nothing.
        Assert.Contains("agent support", result.Created);
        Assert.Null(await Store(target).Agents.GetAsync("support", Ct));
    }

    [Fact]
    public async Task What_is_already_there_is_left_alone_unless_asked()
    {
        var source = await HostAsync();
        await FillAsync(source);
        var bundle = await Bundles(source).ExportAsync(cancellationToken: Ct);

        var target = await HostAsync();
        await Store(target).Agents.UpsertAsync(new AgentDefinition { Id = "support", Name = "Local version" }, Ct);

        var skipped = await Bundles(target).ImportAsync(bundle, ImportMode.SkipExisting, Ct);
        Assert.Contains("agent support", skipped.Skipped);
        Assert.Equal("Local version", (await Store(target).Agents.GetAsync("support", Ct))!.Name);

        var overwritten = await Bundles(target).ImportAsync(bundle, ImportMode.Overwrite, Ct);
        Assert.Contains("agent support", overwritten.Replaced);
        Assert.Equal("Support", (await Store(target).Agents.GetAsync("support", Ct))!.Name);
    }

    // ---------- being told what is wrong ----------

    [Fact]
    public async Task An_agent_arriving_without_its_tool_is_reported_rather_than_left_broken()
    {
        var target = await HostAsync();
        var bundle = new Bundle
        {
            Agents = [new AgentDefinition { Id = "support", Name = "Support", Model = "gpt", ToolIds = ["lookup_order"] }],
        };

        var result = await Bundles(target).ImportAsync(bundle, ImportMode.Validate, Ct);

        // The failure this exists to prevent: an agent without its tools runs, answers, and is quietly
        // wrong for a fortnight.
        Assert.False(result.Clean);
        Assert.Contains(result.Missing, m => m.Contains("lookup_order", StringComparison.Ordinal));
        Assert.Contains(result.Missing, m => m.Contains("gpt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_knowledge_base_whose_embedding_model_is_absent_is_reported()
    {
        var target = await HostAsync();
        var bundle = new Bundle
        {
            KnowledgeBases = [new KnowledgeBase { Id = "docs", Name = "Docs", EmbeddingModel = "bge-small" }],
        };

        var result = await Bundles(target).ImportAsync(bundle, ImportMode.Validate, Ct);

        // It cannot be re-indexed, and its vectors cannot be compared with what a different model makes.
        Assert.Contains(result.Missing, m => m.Contains("bge-small", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_connection_is_reported_with_what_to_do_about_it()
    {
        var target = await HostAsync();
        var bundle = new Bundle { Connections = [new ConnectionReference("main", "openai") { Name = "Main" }] };

        var result = await Bundles(target).ImportAsync(bundle, ImportMode.Validate, Ct);

        Assert.Contains(result.Missing, m => m.Contains("own secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clean_import_says_so()
    {
        var source = await HostAsync();
        await FillAsync(source);
        var bundle = await Bundles(source).ExportAsync(cancellationToken: Ct);

        var target = await HostAsync();
        await Store(target).Models.UpsertAsync(
            new ModelDescriptor { Id = "gpt", Name = "GPT", Format = ModelFormat.Remote, ProviderId = "openai", ConnectionId = "main" },
            Ct);
        await Store(target).Connections.UpsertAsync(
            new ProviderConnection { Id = "main", Name = "Main", ProviderId = "openai" },
            Ct);

        Assert.True((await Bundles(target).ImportAsync(bundle, ImportMode.SkipExisting, Ct)).Clean);
    }

    [Fact]
    public async Task A_tool_arrives_without_permission_to_run_in_process()
    {
        var source = await HostAsync();
        await Store(source).Tools.UpsertAsync(
            new ToolDefinition
            {
                Id = "t1",
                Name = "danger",
                Route = "/x",
                Kind = ToolKind.Endpoint,
                InvocationMode = ToolInvocationMode.InProcess,
                InProcessAllowed = true,
                InProcessAllowedBy = "alice",
            },
            Ct);

        var target = await HostAsync();
        await Bundles(target).ImportAsync(await Bundles(source).ExportAsync(cancellationToken: Ct), ImportMode.SkipExisting, Ct);

        // Running inside this host's process is an act by a named administrator here, not something a
        // file can carry across from somewhere else.
        var tool = await Store(target).Tools.GetAsync("t1", Ct);
        Assert.False(tool!.InProcessAllowed);
        Assert.Null(tool.InProcessAllowedBy);
    }

    [Fact]
    public async Task A_bundle_from_a_newer_version_is_refused_with_a_sentence()
    {
        var target = await HostAsync();

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() =>
            Bundles(target).ImportAsync(new Bundle { FormatVersion = 99 }, ImportMode.Validate, Ct));

        Assert.Contains("version 99", error.Message, StringComparison.Ordinal);
    }

    // ---------- snapshots ----------

    [Fact]
    public async Task A_snapshot_is_a_readable_database_taken_while_the_host_runs()
    {
        var app = await HostAsync();
        await FillAsync(app);

        var path = Path.Combine(_dirs[^1], "backups", "snap.db");
        await ((ISnapshotSource)Store(app)).SnapshotAsync(path, Ct);

        Assert.True(File.Exists(path));

        // Opened as a database rather than compared as bytes: the point of VACUUM INTO over a file copy
        // is that what comes out is consistent, and only reading it proves that.
        var store = new NetCoreAI.Storage.Sqlite.SqliteMetadataStore(
            new Factory($"Data Source={path};Pooling=False"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NetCoreAI.Storage.Sqlite.SqliteMetadataStore>.Instance);

        Assert.Equal("Support", (await store.Agents.GetAsync("support", Ct))!.Name);
    }

    [Fact]
    public async Task A_snapshot_refuses_to_write_over_an_existing_file()
    {
        var app = await HostAsync();
        var path = Path.Combine(_dirs[^1], "backups", "snap.db");
        await ((ISnapshotSource)Store(app)).SnapshotAsync(path, Ct);

        // Silently replacing the previous backup with a new one is how somebody ends up with exactly one
        // backup, taken after the thing they needed to recover from.
        await Assert.ThrowsAsync<NetCoreAIException>(() => ((ISnapshotSource)Store(app)).SnapshotAsync(path, Ct));
    }

    private sealed class Factory(string connectionString)
        : Microsoft.EntityFrameworkCore.IDbContextFactory<NetCoreAI.Storage.Sqlite.NetCoreAIDbContext>
    {
        public NetCoreAI.Storage.Sqlite.NetCoreAIDbContext CreateDbContext() =>
            new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<NetCoreAI.Storage.Sqlite.NetCoreAIDbContext>()
                .UseSqlite(connectionString).Options);
    }
}
