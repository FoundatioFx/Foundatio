using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class JobSchedulerTests
{
    [Fact]
    public async Task EnqueueDueOccurrencesAsync_WhenOccurrenceIsDue_CreatesSingleGlobalOccurrenceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob)
        }, cancellationToken);

        var first = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var second = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);

        var dispatch = Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal("nightly:20260101000000:global", dispatch.DispatchId);
        Assert.Equal(ScheduledDispatchKind.JobOccurrence, dispatch.Kind);
        Assert.Equal("nightly", dispatch.Headers["job.name"]);

        var state = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Scheduled, state.Status);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), state.ScheduledForUtc);
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenOccurrenceIsDue_RunsConfiguredJobAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store, serviceProvider, nodeId: "node-a");
        var processor = new JobScheduleProcessor(scheduler, store, client, nodeId: "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob)
        }, cancellationToken);
        var scheduled = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);

        int completed = await processor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken);

        var dispatch = Assert.Single(scheduled);
        var state = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.Equal(1, completed);
        Assert.Equal(1, probe.RunCount);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Completed, state.Status);
        Assert.Equal(1, state.Attempt);
        Assert.Equal(100, state.Progress);
    }

    [Fact]
    public async Task EnqueueDueOccurrencesAsync_WithPerNodeScope_CreatesOccurrencePerNodeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var nodeA = CreateProcessor(scheduler, store, "node-a");
        var nodeB = CreateProcessor(scheduler, store, "node-b");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "per-node",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob),
            Scope = ScheduledJobScope.PerNode
        }, cancellationToken);

        var first = await nodeA.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var second = await nodeB.EnqueueDueOccurrencesAsync(now, cancellationToken);

        Assert.Equal("per-node:20260101000000:node-a", Assert.Single(first).DispatchId);
        Assert.Equal("per-node:20260101000000:node-b", Assert.Single(second).DispatchId);

        var states = await store.QueryAsync(new JobQuery { Name = "per-node" }, cancellationToken);
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public async Task EnqueueDueOccurrencesAsync_WithMisfireWindow_CatchesRecentMissedOccurrenceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "daily",
            Cron = "0 0 * * *",
            JobType = typeof(ScheduledProbeJob),
            MisfireWindow = TimeSpan.FromMinutes(10)
        }, cancellationToken);

        var scheduled = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);

        var dispatch = Assert.Single(scheduled);
        var state = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), state.ScheduledForUtc);
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenDispatchIsNotJobOccurrence_ReleasesItAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "delayed-message",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = "work",
            Body = "hello"u8.ToArray(),
            DueUtc = now
        }, cancellationToken);

        int completed = await processor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken);

        Assert.Equal(0, completed);
        var claimed = await store.ClaimDueDispatchesAsync(now, 10, "node-b", TimeSpan.FromMinutes(1), cancellationToken);
        var dispatch = Assert.Single(claimed);
        Assert.Equal("delayed-message", dispatch.DispatchId);
        Assert.Equal("node-b", dispatch.ClaimOwner);
    }
    private static JobScheduleProcessor CreateProcessor(IJobScheduler scheduler, IJobRuntimeStore store, string nodeId)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(new JobSchedulerProbe())
            .BuildServiceProvider();
        var client = new JobClient(store, serviceProvider, nodeId: nodeId);
        return new JobScheduleProcessor(scheduler, store, client, nodeId: nodeId);
    }

    private sealed class JobSchedulerProbe
    {
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);

        public void RecordRun()
        {
            Interlocked.Increment(ref _runCount);
        }
    }

    private sealed class ScheduledProbeJob : IJob
    {
        private readonly JobSchedulerProbe _probe;

        public ScheduledProbeJob(JobSchedulerProbe probe)
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
}
