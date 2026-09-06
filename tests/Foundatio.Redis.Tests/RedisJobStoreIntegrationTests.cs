using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Redis.Tests;

/// <summary>
/// End-to-end tests that wire the real messaging core / CRON scheduler on top of the Redis <see cref="IJobRuntimeStore"/>
/// and exercise the two paths the store exists to support but that the primitive-level conformance suite does not cover:
/// (1) a delayed send whose delay exceeds the transport's <see cref="TransportCapabilities.MaxDeliveryDelay"/> being
/// durably stored in Redis and drained by the dispatch pump when due, and (2) CRON occurrences being materialized, run,
/// retried/dead-lettered, and stale-reclaimed through Redis.
///
/// Gated on <c>FOUNDATIO_REDIS_CONNECTION_STRING</c>; skips when unset. Each test isolates under a unique key prefix.
/// </summary>
public class RedisJobStoreIntegrationTests
{
    private static JobTypeRegistry CreateJobRegistry() => new(typeof(RedisJobStoreIntegrationTests).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
        .Where(t => t.IsClass && !t.IsAbstract && typeof(IJob).IsAssignableFrom(t))
        .Select(t => new JobTypeRegistration(t.FullName!, t)));

    [Fact]
    public async Task CreateIfAbsentAsync_ConcurrentAdmission_EnforcesCapacityAtomicallyAsync()
    {
        var connection = RedisTestConnection.Multiplexer;
        Assert.SkipWhen(connection is null, "FOUNDATIO_REDIS_CONNECTION_STRING not set.");
        var token = TestContext.Current.CancellationToken;
        var store = new RedisJobRuntimeStore(new RedisJobRuntimeStoreOptions { ConnectionMultiplexer = connection, KeyPrefix = $"test:capacity:{Guid.NewGuid():N}:", MaxJobs = 1 });
        var accepted = await Task.WhenAll(Enumerable.Range(0, 10).Select(async index =>
        {
            try
            {
                await store.CreateIfAbsentAsync(new JobState { JobId = index.ToString(), Name = "work" }, token);
                return true;
            }
            catch (JobException) { return false; }
        }));
        Assert.Single(accepted, value => value);
        var existing = Assert.Single(await store.QueryAsync(new JobQuery(), token));
        await store.CreateIfAbsentAsync(existing, token);
    }

    [Fact]
    public async Task QueryAsync_SparseFilter_ContinuesPastEmptyBoundedPageAsync()
    {
        var connection = RedisTestConnection.Multiplexer;
        Assert.SkipWhen(connection is null, "FOUNDATIO_REDIS_CONNECTION_STRING not set.");
        var token = TestContext.Current.CancellationToken;
        var store = RedisTestConnection.CreateStore(connection);
        await Task.WhenAll(Enumerable.Range(0, 1001).Select(index => store.CreateIfAbsentAsync(new JobState
        {
            JobId = index.ToString("D4"),
            Name = "same",
            Status = index == 1000 ? JobStatus.Queued : JobStatus.Completed
        }, token)));
        var page = await store.QueryAsync(new JobQuery { Name = "same", Status = JobStatus.Queued }, token);
        Assert.Empty(page);
        Assert.NotNull(page.ContinuationToken);
        var last = await store.QueryAsync(new JobQuery { Name = "same", Status = JobStatus.Queued, AfterJobId = page.ContinuationToken }, token);
        Assert.Equal("1000", Assert.Single(last).JobId);
        Assert.Null(last.ContinuationToken);
    }

    [Fact]
    public async Task Schedules_FreshStoreInstanceReadsPersistedDefinitionAndArgumentsAsync()
    {
        var connection = RedisTestConnection.Multiplexer;
        Assert.SkipWhen(connection is null, "FOUNDATIO_REDIS_CONNECTION_STRING not set.");
        var token = TestContext.Current.CancellationToken;
        string prefix = $"test:schedules:{Guid.NewGuid():N}:";
        var first = new RedisJobRuntimeStore(connection, prefix);
        await first.ReconcileAsync(new ScheduledJobDefinition
        {
            Name = "persisted",
            Cron = "0 3 * * *",
            JobType = "report.v2",
            TimeZoneId = "America/Chicago",
            Payload = "hello"u8.ToArray(),
            PayloadType = "report-args.v2"
        }, token);

        var second = new RedisJobRuntimeStore(connection, prefix);
        var definition = await second.GetScheduleAsync("persisted", token);
        Assert.NotNull(definition);
        Assert.Equal("report.v2", definition.JobType);
        Assert.Equal("America/Chicago", definition.TimeZoneId);
        Assert.Equal("hello"u8.ToArray(), definition.Payload!.Value.ToArray());
        Assert.Equal("report-args.v2", definition.PayloadType);
        Assert.Equal(1, definition.Revision);
        await second.UnscheduleAsync("persisted", token);
        Assert.Null(await first.GetScheduleAsync("persisted", token));
    }

    [Fact]
    public async Task DelayedQueueSend_BeyondTransportLimit_StoresInRedisAndDrainsWhenDueAsync()
    {
        if (RedisTestConnection.Multiplexer is not { } connection)
        {
            Assert.Skip("FOUNDATIO_REDIS_CONNECTION_STRING not set.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        // Within the transport's advertised maximum: delivered natively, nothing is parked in Redis.
        var nativeStore = RedisTestConnection.CreateStore(connection);
        await using var nativeTransport = new CappedDelayTransport(TimeSpan.FromMinutes(15));
        await using var nativeQueue = new MessageBus(nativeTransport, new MessageBusOptions { RuntimeStore = nativeStore });
        var nativeProcessor = new ScheduledMessageDispatcher(nativeStore, nativeTransport);

        await nativeQueue.SendAsync(new PreviewWorkItem { Data = "soon" }, new MessageSendOptions { Delay = TimeSpan.FromMinutes(5) }, cancellationToken);
        Assert.Equal(1, nativeTransport.SendCount);
        Assert.NotNull(nativeTransport.LastSendOptions?.DeliverAt);
        Assert.Equal(0, await nativeProcessor.DispatchDueAsync(now.AddYears(1), cancellationToken: cancellationToken));

        // Beyond the transport's maximum: routed into the Redis store rather than truncated to the broker ceiling.
        var fallbackStore = RedisTestConnection.CreateStore(connection);
        await using var fallbackTransport = new CappedDelayTransport(TimeSpan.FromMinutes(15));
        await using var fallbackQueue = new MessageBus(fallbackTransport, new MessageBusOptions { RuntimeStore = fallbackStore });
        var fallbackProcessor = new ScheduledMessageDispatcher(fallbackStore, fallbackTransport);

        await fallbackQueue.SendAsync(new PreviewWorkItem { Data = "later" }, new MessageSendOptions { Delay = TimeSpan.FromHours(1) }, cancellationToken);
        Assert.Equal(0, fallbackTransport.SendCount);

        // Durably parked in Redis and time-gated: a drain before the due time claims nothing; only when due does the
        // pump pull it from Redis and hand it to the transport.
        Assert.Equal(0, await fallbackProcessor.DispatchDueAsync(now, cancellationToken: cancellationToken));
        Assert.Equal(0, fallbackTransport.SendCount);

        Assert.Equal(1, await fallbackProcessor.DispatchDueAsync(now.AddHours(2), cancellationToken: cancellationToken));
        Assert.Equal(1, fallbackTransport.SendCount);

        var deliveredContext = new TaskCompletionSource<IMessageContext<PreviewWorkItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await fallbackQueue.ConsumeAsync<PreviewWorkItem>((context, _) =>
        {
            deliveredContext.TrySetResult(context);
            return Task.CompletedTask;
        }, new MessageConsumerOptions { AckMode = AckMode.Manual }, cancellationToken);

        var delivered = await deliveredContext.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal("later", delivered.Message.Data);
        await delivered.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task CronOccurrence_MaterializesRunsAndDedupesThroughRedisAsync()
    {
        if (RedisTestConnection.Multiplexer is not { } connection)
        {
            Assert.Skip("FOUNDATIO_REDIS_CONNECTION_STRING not set.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var store = RedisTestConnection.CreateStore(connection);
        var scheduler = new InMemoryScheduledJobStore();
        var (processor, worker, probe) = CreateProcessor(store, scheduler);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ProbeJob).FullName!
        }, cancellationToken);

        // Materialize: one occurrence is written to Redis as a Scheduled JobState + a JobOccurrence dispatch.
        var first = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var dispatch = Assert.Single(first);
        Assert.Equal("nightly:20260101000000:global", dispatch.JobId);
        Assert.Equal("nightly", dispatch.ScheduleName);
        Assert.Equal("nightly", dispatch.Name);

        var scheduled = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(scheduled);
        Assert.Equal(JobStatus.Queued, scheduled.Status);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), scheduled.ScheduledForUtc);

        // Deterministic occurrence id dedupes against the Redis row: a second materialize pass at the same time is a no-op.
        Assert.Empty(await processor.EnqueueDueOccurrencesAsync(now, cancellationToken));

        // Claim from Redis and run: the occurrence completes and the run is recorded once.
        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        Assert.Equal(1, probe.RunCount);

        var completed = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Attempt);
        Assert.Equal(100, completed.Progress);

        // The dispatch was completed (removed) in Redis, so a later drain finds nothing.
        Assert.Equal(0, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task CronOccurrence_RetryDeadLetterAndStaleReclaimThroughRedisAsync()
    {
        if (RedisTestConnection.Multiplexer is not { } connection)
        {
            Assert.Skip("FOUNDATIO_REDIS_CONNECTION_STRING not set.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        var store = RedisTestConnection.CreateStore(connection, time);
        var scheduler = new InMemoryScheduledJobStore();
        var (processor, worker, probe) = CreateProcessor(store, scheduler, timeProvider: time);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        // (a) Retry-then-dead-letter: a failing occurrence is rescheduled in Redis until its retry budget is spent.
        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "flaky",
            Cron = "* * * * *",
            JobType = typeof(FailingJob).FullName!,
            MaxAttempts = 2
        }, cancellationToken);
        var flaky = Assert.Single(await processor.EnqueueDueOccurrencesAsync(now, cancellationToken));

        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        var retried = await store.GetAsync(flaky.JobId!, cancellationToken);
        Assert.NotNull(retried);
        Assert.Equal(JobStatus.Queued, retried.Status);
        Assert.Equal(1, retried.Attempt);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        var deadlettered = await store.GetAsync(flaky.JobId!, cancellationToken);
        Assert.NotNull(deadlettered);
        Assert.Equal(JobStatus.Failed, deadlettered.Status);
        Assert.Equal(2, deadlettered.Attempt);

        // (b) Stale reclaim: an occurrence stuck in Processing under a dead node with an expired lease is reclaimed
        //     (via the Redis CAS reclaim) and run to completion by the live node.
        const string jobId = "nightly:20260101000000:global";
        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ProbeJob).FullName!,
            MaxAttempts = 2
        }, cancellationToken);
        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = jobId,
            Name = "nightly",
            Status = JobStatus.Processing,
            JobType = typeof(ProbeJob).FullName,
            MaxAttempts = 2,
            Attempt = 1,
            NodeId = "node-b",
            LeaseExpiresUtc = now.AddMinutes(-1),
            ScheduledForUtc = now.AddSeconds(-30)
        }, cancellationToken);

        Assert.Equal(1, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        var reclaimed = await store.GetAsync(jobId, cancellationToken);
        Assert.NotNull(reclaimed);
        Assert.Equal(JobStatus.Completed, reclaimed.Status);
        Assert.Equal(2, reclaimed.Attempt);
        Assert.Equal(1, probe.RunCount);
    }

    private static (JobScheduleProcessor Processor, IJobWorker Worker, Probe Probe) CreateProcessor(IJobRuntimeStore store, IMessageTransport? transport = null)
        => CreateProcessor(store, new InMemoryScheduledJobStore(), transport);

    private static (JobScheduleProcessor Processor, IJobWorker Worker, Probe Probe) CreateProcessor(IJobRuntimeStore store, IScheduledJobStore scheduler, IMessageTransport? transport = null, TimeProvider? timeProvider = null)
    {
        var probe = new Probe();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry(), TimeProvider = timeProvider });
        return (new JobScheduleProcessor(scheduler, store, new JobScheduleProcessorOptions { NodeId = "node-a", TimeProvider = timeProvider }), worker, probe);
    }

    private sealed class Probe
    {
        private int _runCount;
        public int RunCount => Volatile.Read(ref _runCount);
        public void Record() => Interlocked.Increment(ref _runCount);
    }

    private sealed class ProbeJob(Probe probe) : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            probe.Record();
            return Task.FromResult(JobResult.Success);
        }
    }

    private sealed class FailingJob : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(JobResult.FromException(new InvalidOperationException("boom")));
        }
    }

    private sealed class PreviewWorkItem
    {
        public string? Data { get; set; }
    }

    // Minimal pull transport with a configurable native delayed-delivery ceiling, so a delay beyond the cap is forced
    // through the runtime store (mirrors the fixture used by the in-memory MessageBus tests).
    private sealed class CappedDelayTransport : IMessageTransport, ISupportsPull, ITransportInfo
    {
        private readonly Queue<TransportEntry> _entries = new();

        public CappedDelayTransport(TimeSpan? maxDeliveryDelay) => MaxDeliveryDelay = maxDeliveryDelay;

        public TimeSpan? MaxDeliveryDelay { get; }
        public int SendCount { get; private set; }
        public TransportSendOptions? LastSendOptions { get; private set; }

        public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
        public IReadOnlySet<DestinationRole> SupportedRoles => new HashSet<DestinationRole> { DestinationRole.Queue };

        public TransportCapabilities GetCapabilities(DestinationAddress destination) =>
            new() { DelayedDelivery = true, MaxDeliveryDelay = MaxDeliveryDelay };

        public Task<SendResult> SendAsync(DestinationAddress destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
        {
            SendCount += messages.Count;
            LastSendOptions = options;
            var items = new SendItemResult[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                string id = messages[i].MessageId ?? Guid.NewGuid().ToString("N");
                _entries.Enqueue(new TransportEntry { Id = id, Destination = destination, Body = messages[i].Body, Headers = messages[i].Headers, Receipt = new Receipt() });
                items[i] = new SendItemResult { MessageId = id };
            }

            return Task.FromResult(new SendResult { Items = items });
        }

        public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TransportEntry>>(_entries.Count > 0 ? [_entries.Dequeue()] : []);

        public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
