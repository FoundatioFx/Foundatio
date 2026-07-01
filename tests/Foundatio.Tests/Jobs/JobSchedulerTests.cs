using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public async Task EnqueueDueOccurrencesAsync_WithAllowConcurrent_MaterializesEveryMissedOccurrenceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 5, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "frequent",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob),
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
    public async Task JobRuntimeService_RunsQueuedJobsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = new JobSchedulerProbe();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var store = new InMemoryJobRuntimeStore();
        var scheduler = new InMemoryJobScheduler();
        var registry = new JobTypeRegistry([new JobTypeRegistration("probe", typeof(ScheduledProbeJob))]);
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a", jobTypes: registry);
        var processor = new JobScheduleProcessor(scheduler, store, worker, nodeId: "node-a", jobTypes: registry);
        var client = new JobClient(store, jobTypes: registry);

        var service = new Foundatio.Extensions.Hosting.Jobs.JobRuntimeService(processor, worker,
            options: new Foundatio.Extensions.Hosting.Jobs.JobRuntimeServiceOptions { PollInterval = TimeSpan.FromMilliseconds(50) });

        await ((Microsoft.Extensions.Hosting.IHostedService)service).StartAsync(cancellationToken);
        try
        {
            var handle = await client.EnqueueAsync<ScheduledProbeJob>(cancellationToken: cancellationToken);

            JobState? state = null;
            for (int i = 0; i < 100 && (state = await handle.GetStateAsync(cancellationToken))?.Status != JobStatus.Completed; i++)
                await Task.Delay(50, cancellationToken);

            Assert.Equal(JobStatus.Completed, state?.Status);
            Assert.Equal(1, probe.RunCount);
        }
        finally
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)service).StopAsync(cancellationToken);
        }
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
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        var processor = new JobScheduleProcessor(scheduler, store, worker, nodeId: "node-a");
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
    public async Task RunDueOccurrencesAsync_WhenDispatchIsQueueMessage_MaterializesItAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new InMemoryMessageTransport();
        var processor = CreateProcessor(scheduler, store, "node-a", transport);
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

        Assert.Equal(1, completed);
        var pull = Assert.IsAssignableFrom<ISupportsPull>(transport);
        var entries = await pull.ReceiveAsync("work", new ReceiveRequest { MaxMessages = 1, MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        var entry = Assert.Single(entries);
        Assert.Equal("delayed-message", entry.Id);
        Assert.Equal("hello"u8.ToArray(), entry.Body.ToArray());
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenJobFails_RetriesThenDeadLettersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        var processor = new JobScheduleProcessor(scheduler, store, worker, nodeId: "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(FailingScheduledJob),
            MaxRetries = 1
        }, cancellationToken);
        var scheduled = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var dispatch = Assert.Single(scheduled);

        Assert.Equal(0, await processor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken));
        var retried = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(retried);
        Assert.Equal(JobStatus.Scheduled, retried.Status);
        Assert.Equal(1, retried.Attempt);

        Assert.Equal(1, await processor.RunDueOccurrencesAsync(now.AddMinutes(2), cancellationToken: cancellationToken));
        var deadlettered = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(deadlettered);
        Assert.Equal(JobStatus.DeadLettered, deadlettered.Status);
        Assert.Equal(2, deadlettered.Attempt);
        Assert.Equal(2, probe.RunCount);
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenProcessingLeaseExpired_ReclaimsAndRunsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        var processor = new JobScheduleProcessor(scheduler, store, worker, nodeId: "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);
        const string jobId = "nightly:20260101000000:global";

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob),
            MaxRetries = 1
        }, cancellationToken);
        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = jobId,
            Name = "nightly",
            Status = JobStatus.Processing,
            Attempt = 1,
            NodeId = "node-b",
            LeaseExpiresUtc = now.AddMinutes(-1),
            ScheduledForUtc = now.AddSeconds(-30)
        }, cancellationToken);
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = jobId,
            Kind = ScheduledDispatchKind.JobOccurrence,
            Destination = "nightly",
            Body = Array.Empty<byte>(),
            DueUtc = now,
            JobId = jobId
        }, cancellationToken);

        Assert.Equal(1, await processor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken));

        var state = await store.GetAsync(jobId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(JobStatus.Completed, state.Status);
        Assert.Equal(2, state.Attempt);
        Assert.Equal(1, probe.RunCount);
    }

    [Fact]
    public async Task RunQueuedAsync_DoesNotClaimScheduledOccurrencesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobSchedulerProbe();
        await using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");

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

        Assert.Equal(0, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        Assert.Equal(0, probe.RunCount);
        Assert.Equal(JobStatus.Queued, (await store.GetAsync("nightly:20260101000000:global", cancellationToken))!.Status);
    }

    [Fact]
    public async Task RunDueOccurrencesAsync_WhenOccurrenceIsTerminal_RetiresDispatchInsteadOfReschedulingAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        var processor = CreateProcessor(scheduler, store, "node-a");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);
        const string jobId = "nightly:20260101000000:global";

        await scheduler.ScheduleAsync(new ScheduledJobDefinition { Name = "nightly", Cron = "* * * * *", JobType = typeof(ScheduledProbeJob) }, cancellationToken);
        // A worker completed the occurrence but crashed before retiring its dispatch: a terminal job with a live dispatch.
        await store.CreateIfAbsentAsync(new JobState { JobId = jobId, Name = "nightly", Status = JobStatus.Completed, ScheduledForUtc = now.AddSeconds(-30) }, cancellationToken);
        await store.ScheduleDispatchAsync(new ScheduledDispatchState { DispatchId = jobId, Kind = ScheduledDispatchKind.JobOccurrence, Destination = "nightly", Body = Array.Empty<byte>(), DueUtc = now, JobId = jobId }, cancellationToken);

        await processor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken);

        // The dispatch for a terminal occurrence must be retired, not rescheduled +1min and re-claimed forever.
        Assert.Empty(await store.ClaimDueDispatchesAsync(now.AddMinutes(5), 10, "node-b", TimeSpan.FromMinutes(5), cancellationToken));
    }

    [Fact]
    public async Task EnqueueDueOccurrencesAsync_PerNodeScope_WithDelimiterInNodeId_DoesNotCrossMatchAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scheduler = new InMemoryJobScheduler();
        var store = new InMemoryJobRuntimeStore();
        // Node ids that are suffix-confusable under a naive EndsWith(":{scope}") check — the default NodeIdentity contains ':'.
        var nodeXB = CreateProcessor(scheduler, store, "x:b");
        var nodeB = CreateProcessor(scheduler, store, "b");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "per-node",
            Cron = "* * * * *",
            JobType = typeof(ScheduledProbeJob),
            Scope = ScheduledJobScope.PerNode // default Overlap = SkipIfRunning, which runs the active-occurrence check
        }, cancellationToken);

        Assert.Single(await nodeXB.EnqueueDueOccurrencesAsync(now, cancellationToken)); // creates "per-node:...:x:b"
        // node "b" must still materialize its own occurrence; node "x:b"'s occurrence must not be mistaken for node "b"'s.
        Assert.Single(await nodeB.EnqueueDueOccurrencesAsync(now, cancellationToken));  // creates "per-node:...:b"

        var states = await store.QueryAsync(new JobQuery { Name = "per-node", Limit = 100 }, cancellationToken);
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public async Task AddFoundatio_WithRuntimeStore_AutoRegistersAndRunsPumpAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = new JobSchedulerProbe();
        var services = new ServiceCollection().AddSingleton(probe);
        var foundatio = services.AddFoundatio();
        foundatio.Jobs.UseInMemoryRuntime();
        foundatio.Jobs.Register<ScheduledProbeJob>("probe");
        await using var provider = services.BuildServiceProvider();

        // Configuring a runtime store auto-registers the pump — no separate AddJobRuntimeService — so a hosted process
        // runs IJobClient-submitted jobs (and drains delayed messaging) without extra wiring.
        var pump = Assert.Single(provider.GetServices<IHostedService>().OfType<JobRuntimePumpService>());
        await pump.StartAsync(cancellationToken);
        try
        {
            var handle = await provider.GetRequiredService<IJobClient>().EnqueueAsync<ScheduledProbeJob>(cancellationToken: cancellationToken);

            JobState? state = null;
            for (int i = 0; i < 100 && (state = await handle.GetStateAsync(cancellationToken))?.Status != JobStatus.Completed; i++)
                await Task.Delay(50, cancellationToken);

            Assert.Equal(JobStatus.Completed, state?.Status);
            Assert.Equal(1, probe.RunCount);
        }
        finally
        {
            await pump.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ConfigureRuntimePump_Disabled_DoesNotPumpAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var probe = new JobSchedulerProbe();
        var services = new ServiceCollection().AddSingleton(probe);
        var foundatio = services.AddFoundatio();
        foundatio.Jobs.UseInMemoryRuntime();
        foundatio.Jobs.Register<ScheduledProbeJob>("probe");
        foundatio.Jobs.ConfigureRuntimePump(o => o.Enabled = false); // opt out of automatic pumping
        await using var provider = services.BuildServiceProvider();

        var pump = Assert.Single(provider.GetServices<IHostedService>().OfType<JobRuntimePumpService>());
        await pump.StartAsync(cancellationToken);
        try
        {
            var handle = await provider.GetRequiredService<IJobClient>().EnqueueAsync<ScheduledProbeJob>(cancellationToken: cancellationToken);

            // With the pump disabled, the job is never claimed: it stays Queued and the job never runs.
            await Task.Delay(300, cancellationToken);
            Assert.Equal(JobStatus.Queued, (await handle.GetStateAsync(cancellationToken))!.Status);
            Assert.Equal(0, probe.RunCount);
        }
        finally
        {
            await pump.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task AddJobRuntimeService_BeforeUseRuntimeStore_RegistersExactlyOnePumpAsync()
    {
        var services = new ServiceCollection().AddSingleton(new JobSchedulerProbe());
        // Hosting-first ordering must not stack a second pump: AddJobRuntimeService only tunes the single core pump.
        Foundatio.Extensions.Hosting.Jobs.JobHostExtensions.AddJobRuntimeService(services, o => o.PollInterval = TimeSpan.FromMilliseconds(25));
        services.AddFoundatio().Jobs.UseInMemoryRuntime();
        await using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.Single(hostedServices.OfType<JobRuntimePumpService>());
        Assert.Empty(hostedServices.OfType<Foundatio.Extensions.Hosting.Jobs.JobRuntimeService>());
        // The options passed to AddJobRuntimeService are carried onto that single pump.
        Assert.Equal(TimeSpan.FromMilliseconds(25), provider.GetRequiredService<JobRuntimePumpOptions>().PollInterval);
    }

    private static JobScheduleProcessor CreateProcessor(IJobScheduler scheduler, IJobRuntimeStore store, string nodeId, IMessageTransport? transport = null)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(new JobSchedulerProbe())
            .BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: nodeId);
        return new JobScheduleProcessor(scheduler, store, worker, nodeId: nodeId, transport: transport);
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

    private sealed class FailingScheduledJob : IJob
    {
        private readonly JobSchedulerProbe _probe;

        public FailingScheduledJob(JobSchedulerProbe probe)
        {
            _probe = probe;
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _probe.RecordRun();
            return Task.FromResult(JobResult.Success);
        }
    }
}
