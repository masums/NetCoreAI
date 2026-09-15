using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

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
/// This handles the two changes the row design actually produces. Rows are a JSON payload plus the few
/// columns worth filtering on, so a feature adds a <em>table</em>, or adds one more filterable
/// <em>column</em> beside the JSON. A missing table is created. A missing column is added when doing so
/// needs no decision about what the rows already there should say — it is nullable, or it has a default
/// that means what they always meant.
/// </para>
/// <para>
/// Anything else — a required column with no default, a type that changed, a column that went away — is
/// refused by name rather than guessed at. Pre-1.0 the answer is to delete the database and let it be
/// recreated; after 1.0 it will be a real migration. Either way the operator is told what is wrong in one
/// sentence, which is the part that was missing.
/// </para>
/// </remarks>
internal static partial class SchemaUpgrade
{
    [GeneratedRegex(@"CREATE\s+TABLE\s+""(?<name>[^""]+)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex CreateTable { get; }

    [GeneratedRegex(@"CREATE\s+(?:UNIQUE\s+)?INDEX\s+""(?<name>[^""]+)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex CreateIndex { get; }

    /// <summary>Creates whatever the model has and the database does not. Returns the objects created.</summary>
    /// <exception cref="NetCoreAIException">The database differs in a way that cannot be reconciled safely.</exception>
    public static async Task<IReadOnlyList<string>> ApplyAsync(NetCoreAIDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var existing = await NamesAsync(db, cancellationToken).ConfigureAwait(false);
        var created = new List<string>();
        var indexes = new List<(string Name, string Sql)>();

        // The script EF would run for a fresh database. Statements for objects that already exist are
        // skipped, so what runs is exactly the difference.
        foreach (var statement in Statements(db.Database.GenerateCreateScript()))
        {
            if (CreateTable.Match(statement) is { Success: true } table)
            {
                if (!existing.Contains(table.Groups["name"].Value))
                {
                    await db.Database.ExecuteSqlRawAsync(statement, cancellationToken).ConfigureAwait(false);
                    created.Add(table.Groups["name"].Value);
                }
            }
            else if (CreateIndex.Match(statement) is { Success: true } index
                && !existing.Contains(index.Groups["name"].Value))
            {
                // Held back. An index on a table that already exists may well be an index on a column
                // that does not yet — creating it here fails with "no such column", naming the column
                // rather than the cause.
                indexes.Add((index.Groups["name"].Value, statement));
            }
        }

        created.AddRange(await AddMissingColumnsAsync(db, created, cancellationToken).ConfigureAwait(false));

        foreach (var (name, sql) in indexes)
        {
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
            created.Add(name);
        }

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
    /// Adds columns the model has and a table does not, where that can be done without inventing data.
    /// </summary>
    /// <remarks>
    /// Tables created a moment ago are skipped: they came from this model, so they match it by
    /// construction, and reading them back would only be a test of SQLite.
    /// </remarks>
    private static async Task<List<string>> AddMissingColumnsAsync(NetCoreAIDbContext db, List<string> created, CancellationToken cancellationToken)
    {
        var added = new List<string>();
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

                var actual = await ColumnsAsync(connection, table, cancellationToken).ConfigureAwait(false);

                // A primary key cannot be altered in SQLite; changing one means rebuilding the table and
                // copying every row, which is a migration rather than an upgrade. Detected here so it is
                // reported once, by name, instead of surfacing later as a UNIQUE constraint failure in
                // front of whoever happened to save something.
                var wantedKey = entity.FindPrimaryKey()?.Properties
                    .Select(k => k.GetColumnName())
                    .Where(c => c is { Length: > 0 })
                    .ToList() ?? [];

                var actualKey = await PrimaryKeyAsync(connection, table, cancellationToken).ConfigureAwait(false);
                if (wantedKey.Count > 0 && actualKey.Count > 0 && !wantedKey.SequenceEqual(actualKey, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{table} is keyed on {string.Join(" + ", actualKey)} and this version keys it on {string.Join(" + ", wantedKey)}");
                    continue;
                }

                foreach (var property in entity.GetProperties())
                {
                    var column = property.GetColumnName();
                    if (column is not { Length: > 0 } || actual.Contains(column))
                    {
                        continue;
                    }

                    if (AddColumnSql(table, column, property) is { } sql)
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = sql;
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        added.Add($"{table}.{column}");
                    }
                    else
                    {
                        // Required, and nothing says what the rows already there should hold. Filling it in
                        // would be this code deciding something about somebody else's data.
                        problems.Add($"{table} is missing {column}, which is required and has no default");
                    }
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
                + ". Adding a table, and adding a column that can be filled in, are handled automatically; this cannot be, "
                + "and guessing at it risks your data. Before 1.0 the supported answer is to delete netcoreai.db (and its "
                + "-wal and -shm files) and let it be recreated, which loses saved settings, conversations and agents but "
                + "no models or documents on disk.");
        }

        return added;
    }

    /// <summary>The columns a table currently has.</summary>
    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();

        // Quoted rather than parameterised: PRAGMA takes no parameters, and the name comes from our own EF
        // model rather than from anything a caller supplied.
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>The primary key columns a table currently has, in order.</summary>
    private static async Task<List<string>> PrimaryKeyAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var key = new List<(int Position, string Column)>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Column 5 is the key's ordinal, 1-based, and 0 for a column outside it.
            var position = reader.GetInt32(5);
            if (position > 0)
            {
                key.Add((position, reader.GetString(1)));
            }
        }

        return [.. key.OrderBy(k => k.Position).Select(k => k.Column)];
    }

    /// <summary>
    /// The statement that adds this column, or null when adding it would mean inventing data.
    /// </summary>
    /// <remarks>
    /// SQLite's <c>ALTER TABLE ADD COLUMN</c> accepts a column that is nullable or has a default, and
    /// nothing else — which is the same line worth drawing anyway.
    /// </remarks>
    private static string? AddColumnSql(string table, string column, IProperty property)
    {
        var definition = $"ALTER TABLE {Quote(table)} ADD COLUMN {Quote(column)} {property.GetColumnType()}";

        if (property.IsNullable)
        {
            return definition + ";";
        }

        return Literal(property.GetDefaultValue()) is { } literal
            ? $"{definition} NOT NULL DEFAULT {literal};"
            : null;
    }

    /// <summary>A default value as SQL. Null when there is nothing usable to write.</summary>
    private static string? Literal(object? value) => value switch
    {
        null => null,
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool flag => flag ? "1" : "0",
        int or long or short or byte => Convert.ToString(value, CultureInfo.InvariantCulture),
        double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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
