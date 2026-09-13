using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Core.Tests.TestSupport;
using Xunit;

namespace NetCoreAI.Core.Tests.Knowledge;

/// <summary>
/// The background job runner: progress that persists, failures that are recorded rather than swallowed,
/// and a partly successful run that still finishes.
/// </summary>
public class BackgroundJobRunnerTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private string _dataDirectory = "";

    private async Task<(IBackgroundJobRunner Runner, IMetadataStore Store)> StartAsync()
    {
        _host = await TestHost.StartAsync(options: o => _dataDirectory = o.DataDirectory);
        return (_host.Services.GetRequiredService<IBackgroundJobRunner>(), _host.Services.GetRequiredService<IMetadataStore>());
    }

    private static async Task<JobRecord> WaitAsync(IBackgroundJobRunner runner, string id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var job = await runner.GetAsync(id, timeout.Token) ?? throw new InvalidOperationException($"Job {id} vanished.");
            if (job.IsTerminal)
            {
                return job;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task A_job_runs_reports_progress_and_completes()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await runner.EnqueueAsync("test.work", "kb1", async context =>
        {
            await context.SetTotalAsync(3);
            for (var i = 0; i < 3; i++)
            {
                await context.AdvanceAsync(1, $"item {i}");
            }
        }, ct);

        var finished = await WaitAsync(runner, job.Id, ct);

        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(3, finished.ItemsDone);
        Assert.Equal(3, finished.ItemsTotal);
        Assert.Equal(1, finished.Progress);
        Assert.NotNull(finished.CompletedAt);
    }

    [Fact]
    public async Task Progress_is_persisted_so_it_survives_a_restart()
    {
        var (runner, store) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await runner.EnqueueAsync("test.work", null, async context =>
        {
            await context.SetTotalAsync(2);
            await context.AdvanceAsync(2);
        }, ct);

        await WaitAsync(runner, job.Id, ct);

        // Read through the store rather than the runner: this is what a restart would see.
        var persisted = await store.Jobs.GetAsync(job.Id, ct);
        Assert.Equal(JobState.Completed, persisted!.State);
        Assert.Equal(2, persisted.ItemsDone);
    }

    [Fact]
    public async Task A_failing_job_records_why_rather_than_disappearing()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await runner.EnqueueAsync("test.work", null, _ => throw new InvalidOperationException("the source was unreachable"), ct);
        var finished = await WaitAsync(runner, job.Id, ct);

        Assert.Equal(JobState.Failed, finished.State);
        Assert.Contains("unreachable", finished.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_bad_item_does_not_abandon_the_rest()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await runner.EnqueueAsync("test.work", null, async context =>
        {
            await context.SetTotalAsync(3);
            await context.AdvanceAsync();
            await context.FailItemAsync("broken.pdf", "the file is not a PDF");
            await context.AdvanceAsync(2);
        }, ct);

        var finished = await WaitAsync(runner, job.Id, ct);

        // A corrupt file in a folder of 500 must not cost the other 499.
        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(1, finished.ItemsFailed);
        Assert.Equal("broken.pdf", Assert.Single(finished.Failures).Item);
    }

    [Fact]
    public async Task The_failure_list_is_capped_but_the_count_is_not()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var job = await runner.EnqueueAsync("test.work", null, async context =>
        {
            for (var i = 0; i < 150; i++)
            {
                await context.FailItemAsync($"row-{i}", "bad row");
            }
        }, ct);

        var finished = await WaitAsync(runner, job.Id, ct);

        // A source failing on every row must not produce a record too large to load back.
        Assert.Equal(150, finished.ItemsFailed);
        Assert.Equal(100, finished.Failures.Count);
    }

    [Fact]
    public async Task A_cancelled_job_stops_and_says_so()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource();

        var job = await runner.EnqueueAsync("test.work", null, async context =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }, ct);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await runner.CancelAsync(job.Id, ct);

        Assert.Equal(JobState.Cancelled, (await WaitAsync(runner, job.Id, ct)).State);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_stops_it_before_it_starts()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var blocker = new TaskCompletionSource();

        // Jobs run one at a time, so this one holds the worker while the second waits behind it.
        var first = await runner.EnqueueAsync("test.work", null, async _ => await blocker.Task, ct);
        var second = await runner.EnqueueAsync("test.work", null, _ => Task.CompletedTask, ct);

        await runner.CancelAsync(second.Id, ct);
        blocker.TrySetResult();

        await WaitAsync(runner, first.Id, ct);
        Assert.Equal(JobState.Cancelled, (await runner.GetAsync(second.Id, ct))!.State);
    }

    [Fact]
    public async Task A_failed_job_can_be_retried_and_keeps_its_history()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;

        var job = await runner.EnqueueAsync("test.work", null, async context =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("first attempt fails");
            }

            await context.AdvanceAsync();
        }, ct);

        Assert.Equal(JobState.Failed, (await WaitAsync(runner, job.Id, ct)).State);

        await runner.RetryAsync(job.Id, ct);
        var retried = await WaitAsync(runner, job.Id, ct);

        // Same id, attempt counter incremented, error cleared.
        Assert.Equal(JobState.Completed, retried.State);
        Assert.Equal(2, retried.Attempt);
        Assert.Null(retried.Error);
    }

    [Fact]
    public async Task Jobs_can_be_listed_by_what_they_operate_on()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var mine = await runner.EnqueueAsync("kb.sync", "kb1", _ => Task.CompletedTask, ct);
        await runner.EnqueueAsync("kb.sync", "kb2", _ => Task.CompletedTask, ct);
        await WaitAsync(runner, mine.Id, ct);

        var forKb1 = await runner.ListAsync("kb1", ct);
        Assert.Equal(mine.Id, Assert.Single(forKb1).Id);
        Assert.Equal(2, (await runner.ListAsync(cancellationToken: ct)).Count);
    }

    [Fact]
    public async Task Changes_are_raised_so_the_dashboard_can_follow_without_polling()
    {
        var (runner, _) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var states = new List<JobState>();
        runner.JobChanged += (_, job) => { lock (states) { states.Add(job.State); } };

        var job = await runner.EnqueueAsync("test.work", null, c => c.AdvanceAsync(), ct);
        await WaitAsync(runner, job.Id, ct);

        lock (states)
        {
            Assert.Contains(JobState.Queued, states);
            Assert.Contains(JobState.Running, states);
            Assert.Contains(JobState.Completed, states);
        }
    }

    [Fact]
    public async Task Terminal_jobs_are_pruned_and_running_ones_are_left_alone()
    {
        var (_, store) = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await store.Jobs.UpsertAsync(new JobRecord { Id = "old", Type = "t", State = JobState.Completed, CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) }, ct);
        await store.Jobs.UpsertAsync(new JobRecord { Id = "old-running", Type = "t", State = JobState.Running, CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) }, ct);
        await store.Jobs.UpsertAsync(new JobRecord { Id = "recent", Type = "t", State = JobState.Completed }, ct);

        var pruned = await store.Jobs.PruneAsync(DateTimeOffset.UtcNow.AddDays(-7), ct);

        Assert.Equal(1, pruned);
        Assert.Null(await store.Jobs.GetAsync("old", ct));

        // A long ingest must survive housekeeping no matter how long it has been running.
        Assert.NotNull(await store.Jobs.GetAsync("old-running", ct));
        Assert.NotNull(await store.Jobs.GetAsync("recent", ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
