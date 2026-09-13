using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>
/// Rows from a SQL database as documents.
/// </summary>
/// <remarks>
/// The query is supplied by an administrator through the dashboard, not by a model or an end user, and it
/// is executed verbatim: this is a reporting-style read, so there is nothing to parameterise. The
/// connection string is stored encrypted, and the connection itself is opened through the host's own
/// ADO.NET provider factory — NetCoreAI ships no database driver of its own beyond SQLite.
/// </remarks>
internal sealed class SqlDataSource(ISecretProtector protector, ILogger<SqlDataSource> logger) : IDataSource
{
    public const string TypeName = "sql";

    /// <summary>Invariant name of a registered <see cref="DbProviderFactory"/>, e.g. "Microsoft.Data.Sqlite".</summary>
    public const string ProviderSetting = "provider";

    /// <summary>Connection string; stored with the protected prefix so credentials are not in plaintext.</summary>
    public const string ConnectionStringSetting = "connectionString";

    /// <summary>The SELECT to run. Every returned row becomes one document.</summary>
    public const string QuerySetting = "query";

    /// <summary>Column holding the stable row identity; without one, a re-sync would duplicate everything.</summary>
    public const string IdColumnSetting = "idColumn";

    public const string TitleColumnSetting = "titleColumn";

    /// <summary>Columns to index, comma separated. Empty means every column except the id.</summary>
    public const string ContentColumnsSetting = "contentColumns";

    /// <summary>Column whose value becomes an access tag on the document, e.g. a tenant or department.</summary>
    public const string AclColumnSetting = "aclColumn";

    /// <summary>Column carrying a last-modified timestamp, used to skip rows that have not changed.</summary>
    public const string ModifiedColumnSetting = "modifiedColumn";

    public string Type => TypeName;

    public async IAsyncEnumerable<SourceDocument> EnumerateAsync(DataSourceDefinition definition, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        await using var connection = await OpenAsync(definition, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = Query(definition);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = Columns(reader);
        var idColumn = definition.Settings.GetValueOrDefault(IdColumnSetting);
        var titleColumn = definition.Settings.GetValueOrDefault(TitleColumnSetting);
        var aclColumn = definition.Settings.GetValueOrDefault(AclColumnSetting);
        var modifiedColumn = definition.Settings.GetValueOrDefault(ModifiedColumnSetting);
        var contentColumns = ContentColumns(definition, columns, idColumn);

        var row = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            row++;
            var id = Value(reader, columns, idColumn) ?? row.ToString(CultureInfo.InvariantCulture);
            var title = Value(reader, columns, titleColumn) ?? $"Row {id}";

            // Each field is rendered with its column name, so a retrieved row reads as
            // "customer: Ada, status: open" rather than a tuple with no labels.
            var builder = new StringBuilder();
            foreach (var column in contentColumns)
            {
                if (Value(reader, columns, column) is { Length: > 0 } value)
                {
                    builder.Append(column).Append(": ").Append(value).AppendLine();
                }
            }

            var text = builder.ToString().Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            yield return new SourceDocument(id, title)
            {
                FileName = $"{id}.txt",
                ContentType = "text/plain",
                SizeBytes = bytes.Length,
                Source = $"{definition.Name} row {id}",
                ModifiedAt = Modified(reader, columns, modifiedColumn),
                AclTags = Value(reader, columns, aclColumn) is { Length: > 0 } tag ? [tag] : null,
                OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
            };
        }
    }

    public async Task<DataSourceTestResult> TestAsync(DataSourceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        try
        {
            var titles = new List<string>();
            var count = 0;
            await foreach (var document in EnumerateAsync(definition, cancellationToken).ConfigureAwait(false))
            {
                count++;
                if (titles.Count < 5)
                {
                    titles.Add(document.Title);
                }

                // Testing should not read a million-row table; enough to prove the query works.
                if (count >= 500)
                {
                    break;
                }
            }

            return new DataSourceTestResult(
                true,
                count == 0
                    ? "The query ran but returned no rows."
                    : $"The query returned {count}{(count >= 500 ? "+" : "")} row(s).",
                count)
            {
                SampleTitles = titles,
            };
        }
        catch (Exception ex) when (ex is NetCoreAIException or DbException or InvalidOperationException or ArgumentException)
        {
            logger.LogDebug(ex, "Testing SQL source {Source} failed.", definition.Name);
            return new DataSourceTestResult(false, ex.Message);
        }
    }

    private async Task<DbConnection> OpenAsync(DataSourceDefinition definition, CancellationToken cancellationToken)
    {
        var providerName = definition.Settings.GetValueOrDefault(ProviderSetting)
            ?? throw new NetCoreAIException($"'{definition.Name}' has no database provider set. Give the invariant name of a registered ADO.NET provider, such as Microsoft.Data.Sqlite.");

        DbProviderFactory factory;
        try
        {
            factory = DbProviderFactories.GetFactory(providerName);
        }
        catch (ArgumentException ex)
        {
            // The host owns its database drivers; NetCoreAI cannot reference every one of them.
            throw new NetCoreAIException(
                $"No ADO.NET provider named '{providerName}' is registered in this host. Call DbProviderFactories.RegisterFactory(\"{providerName}\", …) in Program.cs, referencing the driver package.",
                ex);
        }

        var connection = factory.CreateConnection()
            ?? throw new NetCoreAIException($"The provider '{providerName}' did not supply a connection.");

        connection.ConnectionString = Reveal(definition.Settings.GetValueOrDefault(ConnectionStringSetting))
            ?? throw new NetCoreAIException($"'{definition.Name}' has no connection string.");

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (DbException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new NetCoreAIException($"Could not connect for '{definition.Name}': {ex.Message}", ex);
        }
    }

    private static string Query(DataSourceDefinition definition) =>
        definition.Settings.GetValueOrDefault(QuerySetting) is { Length: > 0 } query
            ? query
            : throw new NetCoreAIException($"'{definition.Name}' has no query. Give a SELECT whose rows should become documents.");

    private static Dictionary<string, int> Columns(DbDataReader reader)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[reader.GetName(i)] = i;
        }

        return columns;
    }

    /// <summary>Columns to index: the configured ones, else everything but the id.</summary>
    private static List<string> ContentColumns(DataSourceDefinition definition, Dictionary<string, int> columns, string? idColumn)
    {
        if (definition.Settings.GetValueOrDefault(ContentColumnsSetting) is { Length: > 0 } configured)
        {
            return [.. configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(columns.ContainsKey)];
        }

        return [.. columns.Keys.Where(c => !string.Equals(c, idColumn, StringComparison.OrdinalIgnoreCase))];
    }

    private static string? Value(DbDataReader reader, Dictionary<string, int> columns, string? column)
    {
        if (column is not { Length: > 0 } || !columns.TryGetValue(column, out var index) || reader.IsDBNull(index))
        {
            return null;
        }

        return reader.GetValue(index)?.ToString();
    }

    private static DateTimeOffset? Modified(DbDataReader reader, Dictionary<string, int> columns, string? column) =>
        Value(reader, columns, column) is { Length: > 0 } value
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>Decrypts a setting stored with the protected prefix; anything else is used as written.</summary>
    private string? Reveal(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        if (!value.StartsWith(RestApiDataSource.ProtectedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            return protector.Unprotect(value[RestApiDataSource.ProtectedPrefix.Length..]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NetCoreAIException(
                "The stored connection string could not be decrypted. Data Protection keys are per-deployment, so a database restored onto a new host needs its sources re-entered.",
                ex);
        }
    }
}
