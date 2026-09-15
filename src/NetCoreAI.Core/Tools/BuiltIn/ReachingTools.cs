using System.ComponentModel;
using System.Data.Common;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCoreAI.Knowledge;

namespace NetCoreAI.Tools.BuiltIn;

/// <summary>
/// Searching the host's own documents.
/// </summary>
/// <remarks>
/// Retrieval filters by the caller's access tags, and this passes the current request's user through, so a
/// model cannot read a document on behalf of somebody who could not read it themselves. With no request
/// behind the run the caller is nobody, which means public documents only.
/// </remarks>
public sealed class KnowledgeSearchTool(IRetriever retriever, IKnowledgeService knowledge, IServiceProvider services)
{
    [AITool("search_documents", Description = "Search the organisation's documents for passages relevant to a question, and return them with the document they came from.")]
    public async Task<string> SearchAsync(
        [Description("What to look for, in the words a person would use.")] string query,
        [Description("Knowledge base to search. Leave empty to search them all.")] string? knowledgeBase = null,
        CancellationToken cancellationToken = default)
    {
        var user = services.GetService<IHttpContextAccessor>()?.HttpContext?.User;
        var callerTags = NetCoreAI.Agents.AgentEngine.CallerTags(user);

        var bases = knowledgeBase is { Length: > 0 }
            ? [knowledgeBase]
            : (await knowledge.ListAsync(cancellationToken).ConfigureAwait(false)).Select(k => k.Id).ToList();

        if (bases.Count == 0)
        {
            return "This host has no knowledge bases to search.";
        }

        var hits = await retriever.SearchManyAsync(bases, query, options: null, callerTags, cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            // Said plainly, so the model reports it rather than filling the gap from memory.
            return "Nothing in the documents matched that. Do not answer from your own knowledge; say that it was not found.";
        }

        var builder = new StringBuilder();
        foreach (var hit in hits)
        {
            builder.Append("From \"").Append(hit.Citation.Title).Append('"');
            if (hit.Citation.Page is { } page)
            {
                builder.Append(", page ").Append(page);
            }

            builder.AppendLine(":").AppendLine(hit.Chunk.Text).AppendLine();
        }

        return builder.ToString();
    }
}

/// <summary>
/// Fetching a page from an allow-listed host.
/// </summary>
/// <remarks>
/// The allow-list is the whole of the safety here. A model that can fetch an arbitrary URL is a
/// request-forgery hole with a friendly name: it sits inside the network, and the addresses worth reaching
/// from there are exactly the ones a block-list forgets — link-local metadata services that hand out cloud
/// credentials, admin panels bound to localhost, anything on the private ranges.
/// </remarks>
public sealed class FetchTool(IHttpClientFactory factory, IOptionsMonitor<BuiltInToolOptions> options)
{
    /// <summary>The named client, so a host can give outbound fetches their own proxy or handler.</summary>
    public const string HttpClientName = "NetCoreAI.Tools.Fetch";

    [AITool("fetch_url", Description = "Fetch the text of a web page from an approved site. Only a small list of sites is allowed.")]
    public async Task<string> FetchAsync(
        [Description("The full URL to fetch.")] string url,
        CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            return "That is not a URL I can fetch. Give an http or https address.";
        }

        if (!Allowed(address, settings.FetchAllowedHosts))
        {
            // Names what is allowed rather than only refusing: the model can then say which sites it could
            // have used, instead of trying variations of the one it was refused.
            return settings.FetchAllowedHosts.Count == 0
                ? "Fetching is not configured on this host."
                : $"{address.Host} is not an approved site. Approved sites: {string.Join(", ", settings.FetchAllowedHosts)}.";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.ParseAdd("text/plain, text/html;q=0.9, application/json;q=0.8");

        try
        {
            using var response = await factory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return $"{address} returned HTTP {(int)response.StatusCode}.";
            }

            // Read to a cap rather than trusting Content-Length, which a server may understate or omit.
            var text = await ReadCappedAsync(response, settings.FetchMaxBytes, cancellationToken).ConfigureAwait(false);
            return text.Length == 0 ? $"{address} returned nothing readable." : text;
        }
        catch (NetCoreAI.Hub.OfflineModeException ex)
        {
            // Handed back rather than thrown. A tool that throws ends the turn, and this is not a fault:
            // the host has said where it may talk, and the model should be able to say so.
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            return $"{address} could not be reached: {ex.Message}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"{address} took too long to answer.";
        }
    }

    /// <summary>
    /// Whether a URL may be fetched.
    /// </summary>
    /// <remarks>
    /// Exact host matches only. A suffix match would let <c>evil-example.com</c> through an allow-list
    /// naming <c>example.com</c>, and an address literal is refused outright — an allow-list of names says
    /// nothing about what an IP address currently resolves to.
    /// </remarks>
    internal static bool Allowed(Uri address, IEnumerable<string> allowedHosts)
    {
        if (IPAddress.TryParse(address.Host, out _))
        {
            return false;
        }

        return allowedHosts.Any(host => string.Equals(host, address.Host, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Clamp(maxBytes, 1024, 1024 * 1024)];
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        return response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
            ? StripTags(text)
            : text;
    }

    /// <summary>Crude tag removal, so a model reads the words rather than the markup.</summary>
    private static string StripTags(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            html, "<(script|style)[^>]*>.*?</\\1>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
        return System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2))).Trim();
    }
}

/// <summary>
/// Reading from a database, and only reading.
/// </summary>
/// <remarks>
/// Three separate limits, because any one of them alone is thin. The statement must be a single SELECT;
/// the rows are capped; and the guide says to point it at an account that can only read. The string check
/// is the weakest of the three and is treated as such — a permission the account does not have is the one
/// that holds when the check is wrong.
/// </remarks>
public sealed class SqlQueryTool(IOptionsMonitor<BuiltInToolOptions> options)
{
    [AITool("query_database", Description = "Run a read-only SQL SELECT against the reporting database and return the rows.")]
    public async Task<string> QueryAsync(
        [Description("A single SQL SELECT statement.")] string sql,
        CancellationToken cancellationToken = default)
    {
        if (options.CurrentValue.Sql is not { } settings)
        {
            return "No database is configured for this tool.";
        }

        if (Refuse(sql, settings) is { } refusal)
        {
            return refusal;
        }

        try
        {
            var factory = DbProviderFactories.GetFactory(settings.ProviderName);
            await using var connection = factory.CreateConnection()
                ?? throw new NetCoreAIException($"The provider '{settings.ProviderName}' produced no connection.");

            connection.ConnectionString = settings.ConnectionString;
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = settings.TimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await FormatAsync(reader, settings.MaxRows, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // Handed back rather than thrown: a model that is told its SQL was wrong can write different
            // SQL, which is the entire point of giving it this tool.
            return $"The query failed: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"The database could not be reached: {ex.Message}";
        }
    }

    /// <summary>The reason to refuse this statement, or null to run it.</summary>
    internal static string? Refuse(string sql, SqlToolConnection settings)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "Give a SELECT statement to run.";
        }

        var trimmed = sql.Trim().TrimEnd(';').Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return "Only SELECT statements are allowed here.";
        }

        // One statement. Otherwise "SELECT 1; DELETE FROM orders" passes a check that only reads the start.
        if (trimmed.Contains(';', StringComparison.Ordinal))
        {
            return "Send one statement at a time.";
        }

        foreach (var word in (string[])["INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "TRUNCATE", "GRANT", "REVOKE", "EXEC", "MERGE", "CALL"])
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, $@"\b{word}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                return $"Only SELECT statements are allowed here; this one contains {word}.";
            }
        }

        if (settings.AllowedTables.Count > 0
            && !settings.AllowedTables.Any(table => System.Text.RegularExpressions.Regex.IsMatch(
                trimmed, $@"\b{System.Text.RegularExpressions.Regex.Escape(table)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))))
        {
            return $"This query may only read: {string.Join(", ", settings.AllowedTables)}.";
        }

        return null;
    }

    private static async Task<string> FormatAsync(DbDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        builder.AppendLine(string.Join(" | ", columns));

        var rows = 0;
        while (rows < maxRows && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new List<string>(columns.Count);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values.Add(await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                    ? ""
                    : (reader.GetValue(i)?.ToString() ?? ""));
            }

            builder.AppendLine(string.Join(" | ", values));
            rows++;
        }

        if (rows == 0)
        {
            return "No rows matched.";
        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Said explicitly: a model reading a cut-off result as the whole answer will state a total that
            // is simply wrong.
            builder.AppendLine($"[Stopped at {maxRows} rows; there were more. Narrow the query or use a COUNT.]");
        }

        return builder.ToString();
    }
}
