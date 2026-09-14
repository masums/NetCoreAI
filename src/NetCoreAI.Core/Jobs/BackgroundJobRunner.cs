using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Jobs;

/// <summary>
/// Runs queued work off the request thread, one job at a time, persisting progress as it goes.
/// </summary>
/// <remarks>
/// Work is a delegate held in memory, so a job that was running when the host stopped cannot simply be
/// resumed: it is marked for retry and re-queued by whatever owns that job type at startup. Progress and
/// failures persist either way, so a restart never loses the record of what happened.
/// </remarks>
internal sealed class BackgroundJobRunner(IMetadataStore store, ILogger<BackgroundJobRunner> logger)
    : IBackgroundJobRunner, IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, Func<IJobContext, Task>> _work = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _stopping;
    private Task? _worker;

    public event EventHandler<JobRecord>? JobChanged;

    /// <summary>Exposed so the nested progress context can log without capturing the parameter twice.</summary>
    private ILogger Log => logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A job that was mid-flight when the process stopped cannot run again by itself: its delegate died
        // with the process. Mark it failed with a message that says so, rather than leaving it "Running"
        // for ever and looking like a hang.
        foreach (var job in await store.Jobs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (job.State is JobState.Running or JobState.Queued)
            {
                await SaveAsync(job with
                {
                    State = JobState.Failed,
                    Error = "The host stopped while this job was running. Run it again to continue from where it left off; work already done is not repeated.",
                    CompletedAt = DateTimeOffset.UtcNow,
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        _stopping = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        if (_stopping is { } stopping)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        foreach (var cts in _running.Values)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (_worker is { } worker)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Ingestion is resumable: what has been embedded is already stored.
            }
        }
    }

    public async Task<JobRecord> EnqueueAsync(string type, string? targetId, Func<IJobContext, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(work);

        var job = new JobRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Type = type,
            TargetId = targetId,
            State = JobState.Queued,
        };

        _work[job.Id] = work;
        await SaveAsync(job, cancellationToken).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(job.Id, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Queued {JobType} job {JobId} for {TargetId}.", type, job.Id, targetId ?? "-");
        return job;
    }

    public Task<IReadOnlyList<JobRecord>> ListAsync(string? targetId = null, CancellationToken cancellationToken = default) =>
        store.Jobs.ListAsync(targetId, cancellationToken);

    public Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
        store.Jobs.GetAsync(jobId, cancellationToken);

    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
            return;
        }

        // Queued but not started: mark it so the worker skips it when it comes round.
        if (await store.Jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false) is { State: JobState.Queued } job)
        {
            await SaveAsync(job with { State = JobState.Cancelled, CompletedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<JobRecord> RetryAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await store.Jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new NetCoreAIException($"Job '{jobId}' no longer exists.");

        if (!_work.ContainsKey(jobId))
        {
            throw new NetCoreAIException(
                "This job cannot be retried in place because the host restarted since it ran. Start the operation again from its own page.");
        }

        var retried = job with
        {
            State = JobState.Queued,
            Attempt = job.Attempt + 1,
            Error = null,
            Failures = [],
            ItemsDone = 0,
            ItemsFailed = 0,
            StartedAt = null,
            CompletedAt = null,
        };

        await SaveAsync(retried, cancellationToken).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(jobId, cancellationToken).ConfigureAwait(false);
        return retried;
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                var job = await store.Jobs.GetAsync(id, stopping).ConfigureAwait(false);
                if (job is null || job.State == JobState.Cancelled || !_work.TryGetValue(id, out var work))
                {
                    continue;
                }

                await RunJobAsync(job, work, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            // Without this the queue would stall silently for the rest of the process lifetime.
            logger.LogError(ex, "The background job worker stopped unexpectedly. Restart the host to run queued jobs.");
        }
    }

    private async Task RunJobAsync(JobRecord job, Func<IJobContext, Task> work, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[job.Id] = cts;

        var context = new JobContext(job, this, cts.Token);
        try
        {
            await SaveAsync(job with { State = JobState.Running, StartedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
            await work(context).ConfigureAwait(false);

            var current = context.Current;
            await SaveAsync(current with
            {
                State = JobState.Completed,
                Status = null,
                CompletedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation(
                "Job {JobId} completed: {Done} item(s), {Failed} failed.", job.Id, current.ItemsDone, current.ItemsFailed);
        }
        catch (OperationCanceledException)
        {
            await SaveAsync(context.Current with { State = JobState.Cancelled, CompletedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation("Job {JobId} was cancelled.", job.Id);
        }
        catch (Exception ex)
        {
            await SaveAsync(context.Current with
            {
                State = JobState.Failed,
                Error = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);

            logger.LogError(ex, "Job {JobId} ({JobType}) failed.", job.Id, job.Type);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
        }
    }

    private async Task SaveAsync(JobRecord job, CancellationToken cancellationToken)
    {
        try
        {
            await store.Jobs.UpsertAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not persist job {JobId}; its progress will not survive a restart.", job.Id);
        }

        JobChanged?.Invoke(this, job);
    }

    public void Dispose()
    {
        _stopping?.Dispose();
        foreach (var cts in _running.Values)
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Progress reporting for one running job. Updates are throttled: an ingest of 10,000 chunks should not
    /// write 10,000 rows, but a job that stalls must still show where it stopped.
    /// </summary>
    private sealed class JobContext(JobRecord job, BackgroundJobRunner runner, CancellationToken cancellationToken) : IJobContext
    {
        private readonly Lock _gate = new();
        private long _lastSaveTicks = Environment.TickCount64;

        public JobRecord Current { get; private set; } = job;

        public string JobId => Current.Id;

        public CancellationToken CancellationToken => cancellationToken;

        public Task SetTotalAsync(int total)
        {
            lock (_gate)
            {
                Current = Current with { ItemsTotal = total };
            }

            return runner.SaveAsync(Current, CancellationToken.None);
        }

        public Task AdvanceAsync(int done = 1, string? status = null)
        {
            bool save;
            lock (_gate)
            {
                Current = Current with { ItemsDone = Current.ItemsDone + done, Status = status ?? Current.Status };
                var now = Environment.TickCount64;
                save = now - _lastSaveTicks > 500 || Current.ItemsDone >= Current.ItemsTotal;
                if (save)
                {
                    _lastSaveTicks = now;
                }
            }

            return save ? runner.SaveAsync(Current, CancellationToken.None) : Task.CompletedTask;
        }

        public Task FailItemAsync(string item, string error)
        {
            lock (_gate)
            {
                // Cap the list: a source that fails on every one of 50,000 rows should not produce a row
                // in the database that cannot be loaded. The counter still tells the whole story.
                var failures = Current.Failures.Count >= 100
                    ? Current.Failures
                    : (IReadOnlyList<JobFailure>)[.. Current.Failures, new JobFailure(item, error)];

                Current = Current with { ItemsFailed = Current.ItemsFailed + 1, Failures = failures };
            }

            runner.Log.LogWarning("Job {JobId}: {Item} failed: {Error}", Current.Id, item, error);
            return runner.SaveAsync(Current, CancellationToken.None);
        }
    }
}
