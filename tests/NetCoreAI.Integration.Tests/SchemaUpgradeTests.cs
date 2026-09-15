using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Storage.Sqlite;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// Opening a database written by an older version of NetCoreAI.
/// </summary>
/// <remarks>
/// This is the upgrade path, and it was broken: <c>EnsureCreated</c> builds a schema only when the file is
/// absent, so a release that added a table left every existing host crashing at startup with "no such
/// table" — at whichever query ran first, naming nothing useful.
/// </remarks>
public sealed class SchemaUpgradeTests : IAsyncLifetime
{
    private string _dir = "";

    public ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConnectionString => $"Data Source={Path.Combine(_dir, "netcoreai.db")};Pooling=False";

    private NetCoreAIDbContext Context() =>
        new(new DbContextOptionsBuilder<NetCoreAIDbContext>().UseSqlite(ConnectionString).Options);

    private SqliteMetadataStore Store() =>
        new(new Factory(ConnectionString), NullLogger<SqliteMetadataStore>.Instance);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<bool> ExistsAsync(string table)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    [Fact]
    public async Task A_database_missing_a_table_this_version_needs_gains_it()
    {
        await Store().InitializeAsync(Ct);

        // An older release, reproduced: the database has everything except a table added since.
        await ExecuteAsync("DROP TABLE \"Runs\";");
        Assert.False(await ExistsAsync("Runs"));

        await Store().InitializeAsync(Ct);

        Assert.True(await ExistsAsync("Runs"));

        // And it is a working table, not just a name in sqlite_master.
        var store = Store();
        await store.Runs.UpsertAsync(new RunTrace { Id = "r1", AgentId = "a1", Input = "hello" }, Ct);
        Assert.Equal("hello", (await store.Runs.GetAsync("r1", Ct))!.Input);
    }

    [Fact]
    public async Task What_is_already_there_is_left_alone()
    {
        var store = Store();
        await store.InitializeAsync(Ct);
        await store.Agents.UpsertAsync(new AgentDefinition { Id = "keep", Name = "Keep me" }, Ct);

        await ExecuteAsync("DROP TABLE \"Runs\";");
        await Store().InitializeAsync(Ct);

        // The upgrade creates what is missing and touches nothing else. A schema step that quietly
        // recreated the database would pass a "does the table exist" test and lose everything.
        Assert.Equal("Keep me", (await Store().Agents.GetAsync("keep", Ct))!.Name);
    }

    [Fact]
    public async Task A_second_start_with_nothing_missing_changes_nothing()
    {
        await Store().InitializeAsync(Ct);
        await Store().InitializeAsync(Ct);
        await Store().InitializeAsync(Ct);

        Assert.True(await ExistsAsync("Runs"));
    }

    [Fact]
    public async Task A_table_whose_columns_have_changed_is_refused_by_name()
    {
        await Store().InitializeAsync(Ct);

        // A table from a version whose columns differ. Creating the missing ones is not safe to guess at,
        // so the run stops — but it stops saying which table and which columns, which is the whole point.
        await ExecuteAsync("DROP TABLE \"Agents\"; CREATE TABLE \"Agents\" (\"Id\" TEXT NOT NULL CONSTRAINT \"PK_Agents\" PRIMARY KEY);");

        var error = await Assert.ThrowsAsync<NetCoreAIException>(() => Store().InitializeAsync(Ct));

        Assert.Contains("Agents is missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("netcoreai.db", error.Message, StringComparison.Ordinal);
    }

    private sealed class Factory(string connectionString) : IDbContextFactory<NetCoreAIDbContext>
    {
        public NetCoreAIDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NetCoreAIDbContext>().UseSqlite(connectionString).Options);
    }
}
