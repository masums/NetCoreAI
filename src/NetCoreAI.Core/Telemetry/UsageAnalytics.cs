using System.Globalization;
using System.Text;

namespace NetCoreAI.Telemetry;

/// <summary>Reading what agents have actually been used for, and what it cost.</summary>
public interface IUsageAnalytics
{
    /// <summary>What the matching runs used, in total and broken down by agent, model, person and day.</summary>
    Task<UsageSummary> SummariseAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>The matching runs themselves, newest first, with the total for paging.</summary>
    Task<(IReadOnlyList<RunTrace> Runs, int Total)> BrowseAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>The matching runs as CSV, one row each, for a spreadsheet or a finance system.</summary>
    Task<string> ExportCsvAsync(RunQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Usage, read from the run traces that were being written anyway.
/// </summary>
/// <remarks>
/// No separate accounting table. Every run already records its model, its tokens, its cost and who asked,
/// and a second copy kept for reporting is a second thing to be wrong — it disagrees with the traces the
/// first time a run is written by a path that forgot to update it. The figures here are the same ones the
/// run page shows, because they are the same rows.
/// </remarks>
internal sealed class UsageAnalytics(IMetadataStore store) : IUsageAnalytics
{
    public Task<UsageSummary> SummariseAsync(RunQuery query, CancellationToken cancellationToken = default) =>
        store.Runs.SummariseAsync(query, cancellationToken);

    public async Task<(IReadOnlyList<RunTrace> Runs, int Total)> BrowseAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        var runs = await store.Runs.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        var total = await store.Runs.CountAsync(query, cancellationToken).ConfigureAwait(false);
        return (runs, total);
    }

    public async Task<string> ExportCsvAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Capped, and deliberately not streamed. An export somebody waits on is better than one that
        // starts arriving and then falls over halfway through a year of runs.
        var runs = await store.Runs.QueryAsync(query with { Limit = 500, Offset = 0 }, cancellationToken).ConfigureAwait(false);

        var csv = new StringBuilder();
        csv.AppendLine("started_at,run_id,agent_id,model_id,user_id,success,elapsed_ms,input_tokens,output_tokens,estimated_cost");

        foreach (var run in runs)
        {
            csv.Append(run.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(run.Id)).Append(',')
                .Append(Escape(run.AgentId)).Append(',')
                .Append(Escape(run.ModelId)).Append(',')
                .Append(Escape(run.UserId)).Append(',')
                .Append(run.Success ? "true" : "false").Append(',')
                .Append(run.ElapsedMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(run.InputTokens?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(run.OutputTokens?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(run.EstimatedCost?.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        return csv.ToString();
    }

    /// <summary>
    /// A CSV field.
    /// </summary>
    /// <remarks>
    /// A leading =, +, - or @ is prefixed with a quote. Those characters make a spreadsheet treat the cell
    /// as a formula, and an agent id is text somebody chose — which is enough for a run history to become
    /// a way to run something on the machine of whoever opens the export.
    /// </remarks>
    private static string Escape(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return "";
        }

        var text = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return text.Contains(',', StringComparison.Ordinal)
            || text.Contains('"', StringComparison.Ordinal)
            || text.Contains('\n', StringComparison.Ordinal)
                ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : text;
    }
}
