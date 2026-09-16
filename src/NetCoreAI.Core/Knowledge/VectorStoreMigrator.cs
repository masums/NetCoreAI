using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Knowledge;

/// <summary>What a migration did, or would do.</summary>
/// <param name="Collections">Collections copied.</param>
/// <param name="Chunks">Chunks copied.</param>
public sealed record VectorMigrationResult(int Collections, long Chunks)
{
    /// <summary>Collections skipped because the target already had them, and was not told to replace.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public long ElapsedMs { get; init; }

    /// <summary>True when nothing was written, because the caller only asked what would happen.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Moving indexed vectors from one store to another.</summary>
public interface IVectorStoreMigrator
{
    /// <summary>
    /// Copies collections from one store to another.
    /// </summary>
    /// <param name="from">Store id to read from, as <see cref="IVectorStore.Id"/> reports it.</param>
    /// <param name="to">Store id to write to.</param>
    /// <param name="collections">Collections to copy; empty copies all of them.</param>
    /// <param name="replace">Replace a collection the target already has, rather than skipping it.</param>
    /// <param name="dryRun">Report what would be copied and write nothing.</param>
    /// <param name="cancellationToken">Stops the copy. What was already written stays.</param>
    Task<VectorMigrationResult> MigrateAsync(
        string from,
        string to,
        IReadOnlyList<string>? collections = null,
        bool replace = false,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Copies vectors between stores, whichever stores they are.
/// </summary>
/// <remarks>
/// <para>
/// Copies rather than re-embeds. The vectors already exist and re-generating them would cost an embedding
/// call per chunk and, worse, produce different numbers if the model has moved on since — turning a
/// change of database into a silent change of what the base retrieves.
/// </para>
/// <para>
/// Nothing is deleted from the source. A migration that emptied the old store as it went would leave a
/// host with no way back from a half-finished one.
/// </para>
/// </remarks>
internal sealed class VectorStoreMigrator(
    IEnumerable<IVectorStore> stores,
    ILogger<VectorStoreMigrator> logger) : IVectorStoreMigrator
{
    /// <summary>
    /// Chunks read and written at a time.
    /// </summary>
    /// <remarks>
    /// A collection is not something to hold in memory: a modest knowledge base is hundreds of thousands
    /// of chunks, each carrying a vector of a thousand floats.
    /// </remarks>
    private const int BatchSize = 500;

    public async Task<VectorMigrationResult> MigrateAsync(
        string from,
        string to,
        IReadOnlyList<string>? collections = null,
        bool replace = false,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var source = Store(from);
        var target = Store(to);

        if (ReferenceEquals(source, target))
        {
            throw new NetCoreAIException($"'{from}' and '{to}' are the same store.");
        }

        var started = Stopwatch.GetTimestamp();
        var wanted = collections is { Count: > 0 }
            ? collections
            : await source.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);

        var existing = (await target.ListCollectionsAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var copied = 0;
        var chunks = 0L;
        var skipped = new List<string>();

        foreach (var collection in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.Contains(collection) && !replace)
            {
                // Refusing beats merging. Two stores holding overlapping chunk ids from different indexing
                // runs produce a collection that is neither, and nobody would know which.
                skipped.Add(collection);
                continue;
            }

            chunks += await CopyAsync(source, target, collection, dryRun, cancellationToken).ConfigureAwait(false);
            copied++;
        }

        var result = new VectorMigrationResult(copied, chunks)
        {
            Skipped = skipped,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            DryRun = dryRun,
        };

        logger.Log(
            dryRun ? LogLevel.Debug : LogLevel.Information,
            "{Verb} {Collections} collection(s) and {Chunks} chunk(s) from {From} to {To}{Skipped}.",
            dryRun ? "Would copy" : "Copied",
            result.Collections,
            result.Chunks,
            from,
            to,
            skipped.Count == 0 ? "" : $", skipping {string.Join(", ", skipped)}");

        return result;
    }

    private static async Task<long> CopyAsync(
        IVectorStore source,
        IVectorStore target,
        string collection,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (source is not IVectorEnumerable readable)
        {
            throw new NetCoreAIException(
                $"'{source.Id}' cannot list its chunks, so they cannot be copied out of it. Re-index into the new store instead.");
        }

        var total = 0L;
        var batch = new List<VectorRecord>(BatchSize);
        var dimensions = 0;

        await foreach (var record in readable.ReadAllAsync(collection, cancellationToken).ConfigureAwait(false))
        {
            if (dimensions == 0)
            {
                // Taken from the first record rather than asked for. The target has to be told the width,
                // and the source's own answer is the only one that cannot disagree with the data.
                dimensions = record.Embedding.Length;
                if (!dryRun)
                {
                    await target.DeleteCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
                    await target.EnsureCollectionAsync(collection, dimensions, VectorDistance.Cosine, cancellationToken).ConfigureAwait(false);
                }
            }

            batch.Add(record);
            total++;

            if (batch.Count >= BatchSize)
            {
                if (!dryRun)
                {
                    await target.UpsertAsync(collection, batch, cancellationToken).ConfigureAwait(false);
                }

                batch.Clear();
            }
        }

        if (batch.Count > 0 && !dryRun)
        {
            await target.UpsertAsync(collection, batch, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private IVectorStore Store(string id) =>
        stores.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new NetCoreAIException(
            $"No vector store with id '{id}'. This host has: {string.Join(", ", stores.Select(s => s.Id))}.");
}
