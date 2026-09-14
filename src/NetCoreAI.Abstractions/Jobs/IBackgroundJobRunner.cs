namespace NetCoreAI;

/// <summary>Lifecycle of a background job.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<JobState>))]
public enum JobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// A unit of background work, persisted so progress survives a restart and a failure leaves a record
/// someone can read rather than a gap in the logs.
/// </summary>
public sealed record JobRecord
{
    public required string Id { get; init; }

    /// <summary>What kind of work this is, e.g. "kb.sync" or "kb.ingest".</summary>
    public required string Type { get; init; }

    /// <summary>What it operates on: a knowledge base id, a data source id.</summary>
    public string? TargetId { get; init; }

    public JobState State { get; init; } = JobState.Queued;

    /// <summary>What the job is doing right now, shown beside the progress bar.</summary>
    public string? Status { get; init; }

    public int ItemsDone { get; init; }

    /// <summary>Total items when known; 0 while the job is still counting.</summary>
    public int ItemsTotal { get; init; }

    /// <summary>Items that failed but did not stop the job; a bad PDF should not abandon the other 499.</summary>
    public int ItemsFailed { get; init; }

    public string? Error { get; init; }

    /// <summary>Per-item failures, so a partly successful run can be understood without trawling logs.</summary>
    public IReadOnlyList<JobFailure> Failures { get; init; } = [];

    /// <summary>Attempts so far; a retried job keeps its id and increments this.</summary>
    public int Attempt { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public double Progress => ItemsTotal <= 0 ? 0 : Math.Clamp((double)ItemsDone / ItemsTotal, 0, 1);

    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
}

/// <summary>One item that failed inside a job that otherwise carried on.</summary>
/// <param name="Item">What failed: a document title, a file name, a row key.</param>
/// <param name="Error">Why, in words the user can act on.</param>
public sealed record JobFailure(string Item, string Error)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Reports progress from inside a running job.</summary>
public interface IJobContext
{
    string JobId { get; }

    CancellationToken CancellationToken { get; }

    /// <summary>Sets the total once it is known, so the bar stops being indeterminate.</summary>
    Task SetTotalAsync(int total);

    /// <summary>Records one item done, optionally with what is happening next.</summary>
    Task AdvanceAsync(int done = 1, string? status = null);

    /// <summary>Records an item that failed without stopping the job.</summary>
    Task FailItemAsync(string item, string error);
}

/// <summary>
/// Runs queued work off the request thread: ingestion, sync, re-indexing. Jobs are persisted, so one that
/// was running when the host stopped is picked up again rather than lost.
/// </summary>
public interface IBackgroundJobRunner
{
    /// <summary>Queues work and returns the job record immediately.</summary>
    Task<JobRecord> EnqueueAsync(string type, string? targetId, Func<IJobContext, Task> work, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JobRecord>> ListAsync(string? targetId = null, CancellationToken cancellationToken = default);

    Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken = default);

    Task CancelAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Runs a failed job again, keeping its id and history.</summary>
    Task<JobRecord> RetryAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Raised whenever a job changes, so the dashboard can follow without polling.</summary>
    event EventHandler<JobRecord>? JobChanged;
}
