using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NetCoreAI.Storage.Sqlite;

/// <summary>
/// Brings an existing database up to the current model.
/// </summary>
/// <remarks>
/// <para>
/// <c>EnsureCreated</c> creates a schema only when the file is absent, and never touches one that already
/// exists. That was fine while nothing had shipped, and stopped being fine the moment a release added a
/// table: every host that upgraded crashed at startup with "no such table", at whichever query happened to
/// run first, which says nothing about the cause.
/// </para>
/// <para>
/// This handles the case the design actually produces. Rows are a JSON payload plus the few columns worth
/// filtering on, so a new feature adds a <em>table</em> far more often than it changes one — and a missing
/// table can be created without touching a byte of anyone's data. Those are created here.
/// </para>
/// <para>
/// A table whose <em>columns</em> have changed is a different thing, and this refuses it rather than
/// guessing: it names the table and the columns and stops. Pre-1.0 the answer is to delete the database
/// and let it be recreated; after 1.0 it will be a real migration. Either way the operator is told what is
/// wrong in one sentence, which is the part that was missing.
/// </para>
/// </remarks>
internal static partial class SchemaUpgrade
{
    [GeneratedRegex(@"CREATE\s+TABLE\s+""(?<name>[^""]+)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex CreateTable { get; }

    [GeneratedRegex(@"CREATE\s+(?:UNIQUE\s+)?INDEX\s+""(?<name>[^""]+)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex CreateIndex { get; }

    /// <summary>Creates whatever the model has and the database does not. Returns the objects created.</summary>
    /// <exception cref="NetCoreAIException">A table exists but is missing columns the model needs.</exception>
    public static async Task<IReadOnlyList<string>> ApplyAsync(NetCoreAIDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var existing = await NamesAsync(db, cancellationToken).ConfigureAwait(false);
        var created = new List<string>();

        // The script EF would run for a fresh database. Statements for objects that already exist are
        // skipped, so what runs is exactly the difference.
        foreach (var statement in Statements(db.Database.GenerateCreateScript()))
        {
            var name = CreateTable.Match(statement) is { Success: true } table ? table.Groups["name"].Value
                : CreateIndex.Match(statement) is { Success: true } index ? index.Groups["name"].Value
                : null;

            if (name is null || existing.Contains(name))
            {
                continue;
            }

            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken).ConfigureAwait(false);
            created.Add(name);
        }

        await VerifyColumnsAsync(db, created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>Every table and index the database already has.</summary>
    private static async Task<HashSet<string>> NamesAsync(NetCoreAIDbContext db, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        return names;
    }

    /// <summary>
    /// Checks that every table the model uses has the columns the model expects.
    /// </summary>
    /// <remarks>
    /// Tables created a moment ago are skipped: they came from this model, so they match it by
    /// construction, and reading them back would only be a test of SQLite.
    /// </remarks>
    private static async Task VerifyColumnsAsync(NetCoreAIDbContext db, List<string> created, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table is null || created.Contains(table))
                {
                    continue;
                }

                var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var command = connection.CreateCommand())
                {
                    // Quoted rather than parameterised: PRAGMA does not take parameters, and the name comes
                    // from our own EF model rather than from anything a caller supplied.
                    command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        actual.Add(reader.GetString(1));
                    }
                }

                var missing = entity.GetProperties()
                    .Select(p => p.GetColumnName())
                    .Where(c => c is { Length: > 0 } && !actual.Contains(c))
                    .ToList();

                if (missing.Count > 0)
                {
                    problems.Add($"{table} is missing {string.Join(", ", missing)}");
                }
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        if (problems.Count > 0)
        {
            throw new NetCoreAIException(
                "This NetCoreAI database was written by an older version whose schema differs from this one: "
                + string.Join("; ", problems)
                + ". Adding a table is handled automatically; a changed table is not, and guessing at it risks your data. "
                + "Before 1.0 the supported answer is to delete netcoreai.db (and its -wal and -shm files) and let it be "
                + "recreated, which loses saved settings, conversations and agents but no models or documents on disk.");
        }
    }

    /// <summary>Splits the generated script into statements, keeping the terminating semicolon.</summary>
    private static IEnumerable<string> Statements(string script)
    {
        foreach (var part in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var statement = part.Trim();
            if (statement.Length > 0)
            {
                yield return statement + ";";
            }
        }
    }
}
