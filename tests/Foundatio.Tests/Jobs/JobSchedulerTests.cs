using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class JobSchedulerTests
{
    private static JobTypeRegistry CreateJobRegistry() => new(typeof(JobSchedulerTests).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
        .Where(t => t.IsClass && !t.IsAbstract && typeof(IJob).IsAssignableFrom(t))
        .Select(t => new JobTypeRegistration(t.FullName!, t)));

    [Fact]
    public async Task EnqueueDueOccurrencesAsync_WhenOccurrenceIsDue_CreatesSingleGlobalOccurrenceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob).FullName!
        }, cancellationToken);

        var first = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var second = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);

        var dispatch = Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal("nightly:20260101000000:global", dispatch.JobId);
        Assert.Equal("nightly", dispatch.Name);
        Assert.Empty(await store.ClaimDueDispatchesAsync(now, 100, "other-node", TimeSpan.FromMinutes(1), cancellationToken));

        var state = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Queued, state.Status);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), state.ScheduledForUtc);
    }

    [Fact]
    public async Task EnqueueDueOccurrencesAsync_WithAllowConcurrent_MaterializesEveryMissedOccurrenceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 5, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "frequent",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob).FullName!,
            Overlap = OverlapPolicy.AllowConcurrent,
            MisfireWindow = TimeSpan.FromMinutes(10)
        }, cancellationToken);

        // A scheduler that lagged behind a per-minute cadence must materialize every missed occurrence in the window,
        // not just the most recent one.
        var first = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        Assert.True(first.Count >= 5, $"Expected multiple missed occurrences, got {first.Count}");

        // Deterministic occurrence ids dedupe across overlapping windows: a second pass at the same time adds nothing.
        var second = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        Assert.Empty(second);
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenOccurrenceIsDue_RunsConfiguredJobAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry() });
        var processor = new JobScheduleProcessor(scheduler, store, new JobScheduleProcessorOptions { NodeId = "node-a" });
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob).FullName!
        }, cancellationToken);
        var scheduled = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);

        int completed = await worker.RunQueuedAsync(cancellationToken: cancellationToken);

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
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        var nodeA = CreateProcessor(scheduler, store, "node-a");
        var nodeB = CreateProcessor(scheduler, store, "node-b");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "per-node",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob).FullName!,
            Scope = ScheduledJobScope.PerNode
        }, cancellationToken);

        var first = await nodeA.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var second = await nodeB.EnqueueDueOccurrencesAsync(now, cancellationToken);

        Assert.Equal("per-node:20260101000000:node-a", Assert.Single(first).JobId);
        Assert.Equal("per-node:20260101000000:node-b", Assert.Single(second).JobId);

        var states = await store.QueryAsync(new JobQuery { Name = "per-node" }, cancellationToken);
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public async Task EnqueueDueOccurrencesAsync_WithMisfireWindow_CatchesRecentMissedOccurrenceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "daily",
            Cron = "0 0 * * *",
            JobType = typeof(ScheduledProbeJob).FullName!,
            MisfireWindow = TimeSpan.FromMinutes(10)
        }, cancellationToken);

        var scheduled = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);

        var dispatch = Assert.Single(scheduled);
        var state = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), state.ScheduledForUtc);
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenDispatchIsQueueMessage_MaterializesItAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new InMemoryMessageTransport();
        var dispatcher = new ScheduledMessageDispatcher(store, transport);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "delayed-message",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = DestinationAddress.ForQueue("work"),
            Body = "hello"u8.ToArray(),
            DueUtc = now
        }, cancellationToken);

        int completed = await dispatcher.DispatchDueAsync(now, cancellationToken: cancellationToken);

        Assert.Equal(1, completed);
        var pull = Assert.IsAssignableFrom<ISupportsPull>(transport);
        var entries = await pull.ReceiveAsync(DestinationAddress.ForQueue("work"), new ReceiveRequest { MaxMessages = 1, MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        var entry = Assert.Single(entries);
        Assert.Equal("delayed-message", entry.ApplicationMessageId);
        Assert.Equal("hello"u8.ToArray(), entry.Body.ToArray());
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenJobFails_RetriesThenDeadLettersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        var store = new InMemoryJobRuntimeStore(time);
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry(), TimeProvider = time });
        var processor = new JobScheduleProcessor(scheduler, store, new JobScheduleProcessorOptions { NodeId = "node-a" });
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(FailingScheduledJob).FullName!,
            MaxAttempts = 2
        }, cancellationToken);
        var scheduled = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var dispatch = Assert.Single(scheduled);

        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        var retried = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(retried);
        Assert.Equal(JobStatus.Queued, retried.Status);
        Assert.Equal(1, retried.Attempt);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        var deadlettered = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(deadlettered);
        Assert.Equal(JobStatus.Failed, deadlettered.Status);
        Assert.Equal(2, deadlettered.Attempt);
        Assert.Equal(2, probe.RunCount);
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenProcessingLeaseExpired_ReclaimsAndRunsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry() });
        var processor = new JobScheduleProcessor(scheduler, store, new JobScheduleProcessorOptions { NodeId = "node-a" });
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);
        const string jobId = "nightly:20260101000000:global";

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob).FullName!,
            MaxAttempts = 2
        }, cancellationToken);
        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = jobId,
            Name = "nightly",
            Status = JobStatus.Processing,
            JobType = typeof(ScheduledProbeJob).FullName,
            MaxAttempts = 2,
            Attempt = 1,
            NodeId = "node-b",
            LeaseExpiresUtc = now.AddMinutes(-1),
            ScheduledForUtc = now.AddSeconds(-30)
        }, cancellationToken);

        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));

        var state = await store.GetAsync(jobId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Completed, state.Status);
        Assert.Equal(2, state.Attempt);
        Assert.Equal(1, probe.RunCount);
    }

    [Fact]
    public async Task RunQueuedAsync_ClaimsScheduledOccurrencesThroughTheSameWorkerAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry() });

        // A CRON occurrence sitting in Queued (the scheduler transitioned it Scheduled->Queued) must NOT be claimed by
        // the generic worker — only the scheduler runs occurrences, with its own retry/dead-letter accounting.
        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "nightly:20260101000000:global",
            Name = "nightly",
            JobType = typeof(ScheduledProbeJob).FullName,
            Status = JobStatus.Queued,
            ScheduledForUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        }, cancellationToken);

        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        Assert.Equal(1, probe.RunCount);
        Assert.Equal(JobStatus.Completed, (await store.GetAsync("nightly:20260101000000:global", cancellationToken))!.Status);
    }

    [Fact]
    public async Task EnqueueDueOccurrencesAsync_PerNodeScope_WithDelimiterInNodeId_DoesNotCrossMatchAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryScheduledJobStore();
        var store = new InMemoryJobRuntimeStore();
        // Node ids that are suffix-confusable under a naive EndsWith(":{scope}") check — the default NodeIdentity contains ':'.
        var nodeXB = CreateProcessor(scheduler, store, "x:b");
        var nodeB = CreateProcessor(scheduler, store, "b");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "per-node",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob).FullName!,
            Scope = ScheduledJobScope.PerNode // default Overlap = SkipIfRunning, which runs the active-occurrence check
        }, cancellationToken);

        Assert.Single(await nodeXB.EnqueueDueOccurrencesAsync(now, cancellationToken)); // creates "per-node:...:x:b"
        // node "b" must still materialize its own occurrence; node "x:b"'s occurrence must not be mistaken for node "b"'s.
        Assert.Single(await nodeB.EnqueueDueOccurrencesAsync(now, cancellationToken));  // creates "per-node:...:b"

        var states = await store.QueryAsync(new JobQuery { Name = "per-node", Limit = 100 }, cancellationToken);
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public async Task UseRuntimeStore_RegistersClientsWithoutStartingWorkersAsync()
    {
        var services = new ServiceCollection();
        services.AddFoundatio().Jobs.UseInMemory();
        await using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.NotNull(provider.GetRequiredService<IJobClient>());
    }

    [Fact]
    public async Task AddJobWorker_ExplicitlyRunsQueuedJobsAndRegistersOnceAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var probe = new JobSchedulerProbe();
        var services = new ServiceCollection().AddLogging().AddSingleton(probe);
        services.AddJobWorker();
        services.AddFoundatio().Jobs.UseInMemory().Jobs.AddJobType<ScheduledProbeJob>("probe");
        services.AddJobWorker();
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(token);
        try
        {
            var handle = await provider.GetRequiredService<IJobClient>().EnqueueAsync<ScheduledProbeJob>(cancellationToken: token);
            JobState? state = null;
            for (int i = 0; i < 100 && (state = await handle.GetStateAsync(token))?.Status != JobStatus.Completed; i++)
                await Task.Delay(50, token);
            Assert.Equal(JobStatus.Completed, state?.Status);
            Assert.Equal(1, probe.RunCount);
        }
        finally
        {
            await hosted.StopAsync(token);
        }
    }

    private static JobScheduleProcessor CreateProcessor(IScheduledJobStore scheduler, IJobRuntimeStore store, string nodeId)
        => new(scheduler, store, new JobScheduleProcessorOptions { NodeId = nodeId });

    private sealed class JobSchedulerProbe
    {
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);

        public void RecordRun()
        {
            Interlocked.Increment(ref _runCount);
        }
    }

    private sealed class FailingScheduledJob : IJob
    {
        private readonly JobSchedulerProbe _probe;

        public FailingScheduledJob(JobSchedulerProbe probe)
        {
            _probe = probe;
        }

        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            _probe.RecordRun();
            return Task.FromResult(JobResult.FromException(new InvalidOperationException("failed")));
        }
    }

    private sealed class ScheduledProbeJob : IJob
    {
        private readonly JobSchedulerProbe _probe;

        public ScheduledProbeJob(JobSchedulerProbe probe)
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
}
