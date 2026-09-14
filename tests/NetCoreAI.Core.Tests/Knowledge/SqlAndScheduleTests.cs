using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using NetCoreAI.Knowledge;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// The SQL data source against a real database, and the cron logic that decides when a source is due.
/// </summary>
public class SqlAndScheduleTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";
    private string _databasePath = "";

    static SqlAndScheduleTests()
    {
        // The host owns its database drivers; this is what a real application does in Program.cs.
        if (!DbProviderFactories.GetProviderInvariantNames().Contains("Microsoft.Data.Sqlite"))
        {
            DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        }
    }

    private async Task<IDataSource> StartAsync()
    {
        _host = await TestHost.StartAsync(options: o => _dataDirectory = o.DataDirectory);
        _databasePath = Path.Combine(_dataDirectory, "source.db");

        await using (var db = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE tickets (id TEXT PRIMARY KEY, subject TEXT, body TEXT, team TEXT, updated TEXT);
                INSERT INTO tickets VALUES ('T-1', 'Printer offline', 'The printer on floor two is offline.', 'facilities', '2026-01-05T10:00:00Z');
                INSERT INTO tickets VALUES ('T-2', 'VPN drops', 'The VPN disconnects every ten minutes.', 'it', '2026-02-11T09:30:00Z');
                INSERT INTO tickets VALUES ('T-3', 'Empty ticket', '', 'it', '2026-02-12T09:30:00Z');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return _host.Services.GetServices<IDataSource>().Single(s => s.Type == SqlDataSource.TypeName);
    }

    private DataSourceDefinition Definition(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SqlDataSource.ProviderSetting] = "Microsoft.Data.Sqlite",
            [SqlDataSource.ConnectionStringSetting] = $"Data Source={_databasePath}",
            [SqlDataSource.QuerySetting] = "SELECT id, subject, body, team, updated FROM tickets",
            [SqlDataSource.IdColumnSetting] = "id",
            [SqlDataSource.TitleColumnSetting] = "subject",
        };

        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return new DataSourceDefinition
        {
            Id = "sql1",
            KnowledgeBaseId = "kb1",
            Name = "Tickets",
            Type = SqlDataSource.TypeName,
            Settings = settings,
        };
    }

    private static async Task<List<SourceDocument>> CollectAsync(IDataSource source, DataSourceDefinition definition, CancellationToken cancellationToken)
    {
        var documents = new List<SourceDocument>();
        await foreach (var document in source.EnumerateAsync(definition, cancellationToken))
        {
            documents.Add(document);
        }

        return documents;
    }

    private static async Task<string> ReadAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        await using var stream = await document.OpenAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    [Fact]
    public async Task Each_row_becomes_a_document_labelled_with_its_columns()
    {
        var source = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var documents = await CollectAsync(source, Definition(), ct);

        // All three rows: the third has an empty body, but its subject and team are still worth indexing.
        Assert.Equal(3, documents.Count);
        Assert.Equal("T-1", documents[0].Id);
        Assert.Equal("Printer offline", documents[0].Title);

        // Column names travel into the text, so "which ticket is about the printer" can match.
        var text = await ReadAsync(documents[0], ct);
        Assert.Contains("body: The printer on floor two is offline.", text, StringComparison.Ordinal);
        Assert.Contains("team: facilities", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_id_column_is_left_out_of_the_indexed_text()
    {
        var source = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var text = await ReadAsync((await CollectAsync(source, Definition(), ct))[0], ct);

        // A primary key embeds into noise; it identifies the document rather than describing it.
        Assert.DoesNotContain("id: T-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_columns_can_be_chosen_explicitly()
    {
        var source = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var text = await ReadAsync((await CollectAsync(source, Definition((SqlDataSource.ContentColumnsSetting, "body")), ct))[0], ct);

        Assert.Contains("body:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("team:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_whose_indexed_columns_are_all_empty_is_skipped()
    {
        var source = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        // Indexing only the body leaves the third row with nothing to say, so it is not stored at all:
        // an empty chunk costs an embedding call and retrieves noise.
        var documents = await CollectAsync(source, Definition((SqlDataSource.ContentColumnsSetting, "body")), ct);

        Assert.Equal(2, documents.Count);
        Assert.DoesNotContain(documents, d => d.Id == "T-3");
    }

    [Fact]
    public async Task A_column_can_supply_the_access_tag()
    {
        var source = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var documents = await CollectAsync(source, Definition((SqlDataSource.AclColumnSetting, "team")), ct);

        Assert.Equal(["facilities"], documents[0].AclTags!);
        Assert.Equal(["it"], documents[1].AclTags!);
    }

    [Fact]
    public async Task A_modified_column_is_carried_through()
    {
        var source = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var documents = await CollectAsync(source, Definition((SqlDataSource.ModifiedColumnSetting, "updated")), ct);

        Assert.Equal(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero), documents[0].ModifiedAt);
    }

    [Fact]
    public async Task Testing_a_working_query_reports_what_it_found()
    {
        var source = await StartAsync();

        var result = await source.TestAsync(Definition(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(3, result.DocumentCount);
        Assert.Contains("Printer offline", result.SampleTitles);
    }

    [Fact]
    public async Task A_broken_query_is_reported_rather_than_thrown_at_the_user()
    {
        var source = await StartAsync();

        var result = await source.TestAsync(Definition((SqlDataSource.QuerySetting, "SELECT * FROM no_such_table")), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no_such_table", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unregistered_provider_says_how_to_register_it()
    {
        var source = await StartAsync();

        var result = await source.TestAsync(Definition((SqlDataSource.ProviderSetting, "Npgsql")), TestContext.Current.CancellationToken);

        Assert.False(result.Success);

        // NetCoreAI ships no driver for every database; the message says whose job it is.
        Assert.Contains("RegisterFactory", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_with_no_query_says_so()
    {
        var source = await StartAsync();
        var definition = Definition();
        var settings = definition.Settings.Where(s => s.Key != SqlDataSource.QuerySetting).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

        var result = await source.TestAsync(definition with { Settings = settings }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no query", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- scheduling ----------

    [Fact]
    public void A_schedule_is_due_once_its_slot_has_passed()
    {
        var created = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var lastSynced = new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero);

        // Hourly: at 02:30 the 03:00 slot has not arrived; at 03:01 it has.
        Assert.False(SyncScheduler.IsDue("0 * * * *", lastSynced, created, lastSynced.AddMinutes(30), out _));
        Assert.True(SyncScheduler.IsDue("0 * * * *", lastSynced, created, lastSynced.AddMinutes(61), out _));
    }

    [Fact]
    public void A_slot_missed_while_the_host_was_down_is_still_picked_up()
    {
        var created = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var lastSynced = new DateTimeOffset(2026, 3, 1, 1, 0, 0, TimeSpan.Zero);

        // Down for a day: due times are measured from the last run, so the work is not silently skipped.
        Assert.True(SyncScheduler.IsDue("0 2 * * *", lastSynced, created, lastSynced.AddDays(1), out _));
    }

    [Fact]
    public void A_source_that_has_never_synced_is_measured_from_when_it_was_created()
    {
        var created = DateTimeOffset.UtcNow.AddHours(-2);

        // Not "run everything on startup": a host that restarts often would re-crawl each time.
        Assert.True(SyncScheduler.IsDue("0 * * * *", null, created, DateTimeOffset.UtcNow, out _));
        Assert.False(SyncScheduler.IsDue("0 4 * * *", null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void An_unusable_cron_expression_is_reported_rather_than_throwing()
    {
        var due = SyncScheduler.IsDue("not a cron expression", null, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, out var error);

        // One bad schedule must not stop every other schedule in the host.
        Assert.False(due);
        Assert.NotNull(error);
    }

    [Fact]
    public void Six_field_expressions_with_seconds_are_understood()
    {
        var created = DateTimeOffset.UtcNow.AddMinutes(-5);

        Assert.True(SyncScheduler.IsDue("0 * * * * *", null, created, DateTimeOffset.UtcNow, out var error));
        Assert.Null(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
