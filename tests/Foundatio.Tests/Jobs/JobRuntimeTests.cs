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

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            _probe.Started.TrySetResult();

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                return JobResult.Success;
            }
            catch (OperationCanceledException)
            {
                _probe.Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
