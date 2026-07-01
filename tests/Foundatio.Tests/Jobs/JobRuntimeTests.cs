using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class JobRuntimeTests
{
    [Fact]
    public async Task RunAsync_WithExecutionContext_ReportsProgressAndIdentityAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "ctx-node");

        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "ctx-job",
            Name = "ctx",
            JobType = typeof(ProgressJob).FullName,
            Status = JobStatus.Queued
        }, cancellationToken);

        Assert.True(await worker.RunAsync("ctx-job", cancellationToken));

        var state = await store.GetAsync("ctx-job", cancellationToken);
        Assert.Equal(JobStatus.Completed, state!.Status);
        Assert.Equal(100, state.Progress); // a completed job is 100%; the worker sets this on success
        // The job wrote its context identity + attempt into the progress message (preserved through completion),
        // proving the store-backed context is wired through to job code.
        Assert.Equal("ctx-job:1", state.ProgressMessage);
    }

    [Fact]
    public async Task RecoverStaleAsync_ReclaimsExpiredProcessingJobsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "recovery-node");

        var expired = DateTimeOffset.UtcNow.AddMinutes(-5);

        // A crashed job with attempts remaining -> re-queued.
        await store.CreateIfAbsentAsync(new JobState { JobId = "retry-me", Name = "j", Status = JobStatus.Processing, NodeId = "dead-node", LeaseExpiresUtc = expired, Attempt = 1 }, cancellationToken);
        // A crashed job that exhausted its attempts -> dead-lettered.
        await store.CreateIfAbsentAsync(new JobState { JobId = "give-up", Name = "j", Status = JobStatus.Processing, NodeId = "dead-node", LeaseExpiresUtc = expired, Attempt = 3 }, cancellationToken);
        // A healthy job whose lease is still valid -> untouched.
        await store.CreateIfAbsentAsync(new JobState { JobId = "alive", Name = "j", Status = JobStatus.Processing, NodeId = "live-node", LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5), Attempt = 1 }, cancellationToken);
        // A CRON occurrence (ScheduledForUtc set) with an expired lease -> NOT reclaimed here; the scheduler owns it.
        await store.CreateIfAbsentAsync(new JobState { JobId = "occurrence", Name = "j", Status = JobStatus.Processing, NodeId = "dead-node", LeaseExpiresUtc = expired, Attempt = 1, ScheduledForUtc = expired }, cancellationToken);

        int recovered = await worker.RecoverStaleAsync(maxAttempts: 3, cancellationToken: cancellationToken);

        Assert.Equal(2, recovered);

        var retried = await store.GetAsync("retry-me", cancellationToken);
        Assert.Equal(JobStatus.Queued, retried!.Status);
        Assert.Null(retried.NodeId);
        Assert.Null(retried.LeaseExpiresUtc);

        Assert.Equal(JobStatus.DeadLettered, (await store.GetAsync("give-up", cancellationToken))!.Status);
        Assert.Equal(JobStatus.Processing, (await store.GetAsync("alive", cancellationToken))!.Status);
        // The CRON occurrence is left for the scheduler's own recovery, not reclaimed as a plain job.
        Assert.Equal(JobStatus.Processing, (await store.GetAsync("occurrence", cancellationToken))!.Status);
    }

    [Fact]
    public async Task TryReclaimExpiredAsync_GuardsAgainstOwnerRenewAndForeignNodeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var now = DateTimeOffset.UtcNow;

        await store.CreateIfAbsentAsync(new JobState { JobId = "expired", Name = "j", Status = JobStatus.Processing, NodeId = "owner", LeaseExpiresUtc = now.AddMinutes(-1), Attempt = 1 }, cancellationToken);
        await store.CreateIfAbsentAsync(new JobState { JobId = "renewed", Name = "j", Status = JobStatus.Processing, NodeId = "owner", LeaseExpiresUtc = now.AddMinutes(5), Attempt = 1 }, cancellationToken);

        // Wrong owner -> rejected (another node already reclaimed/re-ran it).
        Assert.False(await store.TryReclaimExpiredAsync("expired", now, "different-node", JobStatus.Queued, cancellationToken: cancellationToken));
        // Owner renewed its lease (no longer expired) -> rejected, so a live worker is never yanked out from under itself.
        Assert.False(await store.TryReclaimExpiredAsync("renewed", now, "owner", JobStatus.Queued, cancellationToken: cancellationToken));
        // Still owned by the presumed-dead node and still expired -> reclaimed.
        Assert.True(await store.TryReclaimExpiredAsync("expired", now, "owner", JobStatus.Queued, cancellationToken: cancellationToken));

        Assert.Equal(JobStatus.Queued, (await store.GetAsync("expired", cancellationToken))!.Status);
        Assert.Equal(JobStatus.Processing, (await store.GetAsync("renewed", cancellationToken))!.Status);
    }

    [Fact]
    public async Task CreateIfAbsentAsync_WithExistingJob_DoesNotOverwriteStateAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();

        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "job-1",
            Name = "first",
            Status = JobStatus.Queued
        }, cancellationToken);

        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "job-1",
            Name = "second",
            Status = JobStatus.Failed,
            Error = "should not overwrite"
        }, cancellationToken);

        var state = await store.GetAsync("job-1", cancellationToken);

        Assert.NotNull(state);
        Assert.Equal("first", state.Name);
        Assert.Equal(JobStatus.Queued, state.Status);
        Assert.Null(state.Error);
    }

    [Fact]
    public async Task TryTransitionAsync_WithExpectedNodeId_RejectsStaleOwnerAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();

        await store.CreateIfAbsentAsync(new JobState { JobId = "job-1", Name = "test", Status = JobStatus.Queued }, cancellationToken);

        // node-a claims and moves to Processing.
        Assert.True(await store.TryTransitionAsync("job-1", JobStatus.Queued, JobStatus.Processing, new JobStatePatch { NodeId = "node-a" }, cancellationToken: cancellationToken));

        // Its lease lapses and node-b reclaims (re-queue, then claim); node-a is no longer the owner.
        Assert.True(await store.TryTransitionAsync("job-1", JobStatus.Processing, JobStatus.Queued, new JobStatePatch { ClearNodeId = true }, cancellationToken: cancellationToken));
        Assert.True(await store.TryTransitionAsync("job-1", JobStatus.Queued, JobStatus.Processing, new JobStatePatch { NodeId = "node-b" }, cancellationToken: cancellationToken));

        // Stale node-a must NOT be able to complete the job it no longer owns (would otherwise stomp node-b's run).
        Assert.False(await store.TryTransitionAsync("job-1", JobStatus.Processing, JobStatus.Completed, patch: null, expectedNodeId: "node-a", cancellationToken: cancellationToken));

        // The current owner (node-b) can.
        Assert.True(await store.TryTransitionAsync("job-1", JobStatus.Processing, JobStatus.Completed, patch: null, expectedNodeId: "node-b", cancellationToken: cancellationToken));

        var state = await store.GetAsync("job-1", cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Completed, state.Status);
    }

    [Fact]
    public async Task TryClaimAsync_WhenLeaseIsHeldByAnotherNode_ReturnsFalseUntilLeaseExpiresAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();

        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "job-1",
            Name = "test",
            Status = JobStatus.Queued
        }, cancellationToken);

        Assert.True(await store.TryClaimAsync("job-1", "node-a", TimeSpan.FromMinutes(1), cancellationToken));
        Assert.False(await store.TryClaimAsync("job-1", "node-b", TimeSpan.FromMinutes(1), cancellationToken));
        Assert.True(await store.ReleaseClaimAsync("job-1", "node-a", cancellationToken));
        Assert.True(await store.TryClaimAsync("job-1", "node-b", TimeSpan.FromMinutes(1), cancellationToken));

        var state = await store.GetAsync("job-1", cancellationToken);
        Assert.NotNull(state);
        Assert.Equal("node-b", state.NodeId);
        Assert.NotNull(state.LeaseExpiresUtc);
    }

    [Fact]
    public async Task ClaimDueDispatchesAsync_ClaimsReleasesAndCompletesDueDispatchesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var now = DateTimeOffset.UtcNow;

        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "dispatch-1",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = "work",
            Body = "hello"u8.ToArray(),
            DueUtc = now.AddSeconds(-1)
        }, cancellationToken);

        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "dispatch-2",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = "work",
            Body = "later"u8.ToArray(),
            DueUtc = now.AddHours(1)
        }, cancellationToken);

        var claimed = await store.ClaimDueDispatchesAsync(now, 10, "node-a", TimeSpan.FromMinutes(1), cancellationToken);

        var dispatch = Assert.Single(claimed);
        Assert.Equal("dispatch-1", dispatch.DispatchId);
        Assert.Equal("node-a", dispatch.ClaimOwner);
        Assert.Equal(1, dispatch.Attempts);

        var claimedAgain = await store.ClaimDueDispatchesAsync(now, 10, "node-b", TimeSpan.FromMinutes(1), cancellationToken);
        Assert.Empty(claimedAgain);

        await store.ReleaseDispatchAsync("dispatch-1", "node-a", now.AddSeconds(-1), cancellationToken);

        var reclaimed = await store.ClaimDueDispatchesAsync(now, 10, "node-b", TimeSpan.FromMinutes(1), cancellationToken);
        dispatch = Assert.Single(reclaimed);
        Assert.Equal("node-b", dispatch.ClaimOwner);
        Assert.Equal(2, dispatch.Attempts);

        await store.CompleteDispatchAsync("dispatch-1", "node-b", cancellationToken);

        var afterComplete = await store.ClaimDueDispatchesAsync(now, 10, "node-c", TimeSpan.FromMinutes(1), cancellationToken);
        Assert.Empty(afterComplete);
    }

    [Fact]
    public async Task RunAsync_WhenJobSucceeds_TracksCompletedStateAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");

        JobHandle handle = await client.EnqueueAsync<SuccessfulTrackedJob>(new JobRequestOptions { JobId = "job-1" }, cancellationToken);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        var state = await handle.GetStateAsync(cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(1, probe.RunCount);
        Assert.Equal(JobStatus.Completed, state.Status);
        Assert.Equal(1, state.Attempt);
        Assert.Equal(100, state.Progress);
        Assert.NotNull(state.StartedUtc);
        Assert.NotNull(state.CompletedUtc);
        Assert.Null(state.NodeId);
        Assert.Null(state.LeaseExpiresUtc);
    }

    [Fact]
    public async Task EnqueueAsync_WithRegisteredJobType_PersistsStableNameAndWorkerResolvesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        var registry = new JobTypeRegistry([new JobTypeRegistration("search.rebuild", typeof(SuccessfulTrackedJob))]);
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store, jobTypes: registry);
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a", jobTypes: registry);

        JobHandle handle = await client.EnqueueAsync<SuccessfulTrackedJob>(new JobRequestOptions { JobId = "job-registered" }, cancellationToken);
        var queued = await handle.GetStateAsync(cancellationToken);

        Assert.NotNull(queued);
        Assert.Equal("search.rebuild", queued.JobType);
        Assert.DoesNotContain(",", queued.JobType);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        var completed = await handle.GetStateAsync(cancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(1, probe.RunCount);
    }

    [Fact]
    public async Task RequestCancellationAsync_WhenJobIsRunning_CancelsAndTracksStateAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");

        JobHandle handle = await client.EnqueueAsync<CancellableTrackedJob>(new JobRequestOptions { JobId = "job-1" }, cancellationToken);
        var runTask = worker.RunAsync(handle.JobId, cancellationToken);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.True(await handle.RequestCancellationAsync(cancellationToken));

        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(await runTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        var state = await handle.GetStateAsync(cancellationToken);

        Assert.NotNull(state);
        Assert.Equal(JobStatus.Cancelled, state.Status);
        Assert.True(state.CancellationRequested);
        Assert.NotNull(state.CompletedUtc);
        Assert.Null(state.NodeId);
        Assert.Null(state.LeaseExpiresUtc);
    }

    private sealed class JobRuntimeProbe
    {
        private int _runCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount => Volatile.Read(ref _runCount);

        public void RecordRun()
        {
            Interlocked.Increment(ref _runCount);
        }
    }

    private sealed class SuccessfulTrackedJob : IJob
    {
        private readonly JobRuntimeProbe _probe;

        public SuccessfulTrackedJob(JobRuntimeProbe probe)
        {
            _probe = probe;
        }

        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            _probe.RecordRun();
            return Task.FromResult(JobResult.Success);
        }
    }

    private sealed class CancellableTrackedJob : IJob
    {
        private readonly JobRuntimeProbe _probe;

        public CancellableTrackedJob(JobRuntimeProbe probe)
        {
            _probe = probe;
        }

        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            _probe.Started.TrySetResult();

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
                return JobResult.Success;
            }
            catch (OperationCanceledException)
            {
                _probe.Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ProgressJob : IJob
    {
        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            await context.ReportProgressAsync(75, $"{context.JobId}:{context.Attempt}", context.CancellationToken);
            return JobResult.Success;
        }
    }
}
