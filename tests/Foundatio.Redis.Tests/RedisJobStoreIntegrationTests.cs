using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Redis.Tests;

/// <summary>
/// End-to-end tests that wire the real messaging core / CRON scheduler on top of the Redis <see cref="IJobRuntimeStore"/>
/// and exercise the two paths the store exists to support but that the primitive-level conformance suite does not cover:
/// (1) a delayed send whose delay exceeds the transport's <see cref="ISupportsDelayedDelivery.MaxDeliveryDelay"/> being
/// durably stored in Redis and drained by the dispatch pump when due, and (2) CRON occurrences being materialized, run,
/// retried/dead-lettered, and stale-reclaimed through Redis.
///
/// Gated on <c>FOUNDATIO_REDIS_CONNECTION_STRING</c>; skips when unset. Each test isolates under a unique key prefix.
/// </summary>
public class RedisJobStoreIntegrationTests
{
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
        await using var nativeQueue = new MessageQueue(nativeTransport, new QueueOptions { RuntimeStore = nativeStore });
        var nativeProcessor = CreateProcessor(nativeStore, nativeTransport).Processor;

        await nativeQueue.EnqueueAsync(new PreviewWorkItem { Data = "soon" }, new QueueMessageOptions { Delay = TimeSpan.FromMinutes(5) }, cancellationToken);
        Assert.Equal(1, nativeTransport.SendCount);
        Assert.NotNull(nativeTransport.LastSendOptions?.DeliverAt);
        Assert.Equal(0, await nativeProcessor.RunDueOccurrencesAsync(now.AddYears(1), cancellationToken: cancellationToken));

        // Beyond the transport's maximum: routed into the Redis store rather than truncated to the broker ceiling.
        var fallbackStore = RedisTestConnection.CreateStore(connection);
        await using var fallbackTransport = new CappedDelayTransport(TimeSpan.FromMinutes(15));
        await using var fallbackQueue = new MessageQueue(fallbackTransport, new QueueOptions { RuntimeStore = fallbackStore });
        var fallbackProcessor = CreateProcessor(fallbackStore, fallbackTransport).Processor;

        await fallbackQueue.EnqueueAsync(new PreviewWorkItem { Data = "later" }, new QueueMessageOptions { Delay = TimeSpan.FromHours(1) }, cancellationToken);
        Assert.Equal(0, fallbackTransport.SendCount);

        // Durably parked in Redis and time-gated: a drain before the due time claims nothing; only when due does the
        // pump pull it from Redis and hand it to the transport.
        Assert.Equal(0, await fallbackProcessor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken));
        Assert.Equal(0, fallbackTransport.SendCount);

        Assert.Equal(1, await fallbackProcessor.RunDueOccurrencesAsync(now.AddHours(2), cancellationToken: cancellationToken));
        Assert.Equal(1, fallbackTransport.SendCount);

        var delivered = await fallbackQueue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(2) }, cancellationToken);
        Assert.NotNull(delivered);
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
        var scheduler = new InMemoryJobScheduler();
        var (processor, probe) = CreateProcessor(store, scheduler);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ProbeJob)
        }, cancellationToken);

        // Materialize: one occurrence is written to Redis as a Scheduled JobState + a JobOccurrence dispatch.
        var first = await processor.EnqueueDueOccurrencesAsync(now, cancellationToken);
        var dispatch = Assert.Single(first);
        Assert.Equal("nightly:20260101000000:global", dispatch.DispatchId);
        Assert.Equal(ScheduledDispatchKind.JobOccurrence, dispatch.Kind);
        Assert.Equal("nightly", dispatch.Headers["job.name"]);

        var scheduled = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(scheduled);
        Assert.Equal(JobStatus.Scheduled, scheduled.Status);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), scheduled.ScheduledForUtc);

        // Deterministic occurrence id dedupes against the Redis row: a second materialize pass at the same time is a no-op.
        Assert.Empty(await processor.EnqueueDueOccurrencesAsync(now, cancellationToken));

        // Claim from Redis and run: the occurrence completes and the run is recorded once.
        Assert.Equal(1, await processor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken));
        Assert.Equal(1, probe.RunCount);

        var completed = await store.GetAsync(dispatch.JobId!, cancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Attempt);
        Assert.Equal(100, completed.Progress);

        // The dispatch was completed (removed) in Redis, so a later drain finds nothing.
        Assert.Equal(0, await processor.RunDueOccurrencesAsync(now.AddMinutes(1), cancellationToken: cancellationToken));
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
        var store = RedisTestConnection.CreateStore(connection);
        var scheduler = new InMemoryJobScheduler();
        var (processor, probe) = CreateProcessor(store, scheduler);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        // (a) Retry-then-dead-letter: a failing occurrence is rescheduled in Redis until its retry budget is spent.
        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "flaky",
            Cron = "* * * * *",
            JobType = typeof(FailingJob),
            MaxRetries = 1
        }, cancellationToken);
        var flaky = Assert.Single(await processor.EnqueueDueOccurrencesAsync(now, cancellationToken));

        Assert.Equal(0, await processor.RunDueOccurrencesAsync(now, cancellationToken: cancellationToken));
        var retried = await store.GetAsync(flaky.JobId!, cancellationToken);
        Assert.NotNull(retried);
        Assert.Equal(JobStatus.Scheduled, retried.Status);
        Assert.Equal(1, retried.Attempt);

        Assert.Equal(1, await processor.RunDueOccurrencesAsync(now.AddMinutes(2), cancellationToken: cancellationToken));
        var deadlettered = await store.GetAsync(flaky.JobId!, cancellationToken);
        Assert.NotNull(deadlettered);
        Assert.Equal(JobStatus.DeadLettered, deadlettered.Status);
        Assert.Equal(2, deadlettered.Attempt);

        // (b) Stale reclaim: an occurrence stuck in Processing under a dead node with an expired lease is reclaimed
        //     (via the Redis CAS reclaim) and run to completion by the live node.
        const string jobId = "nightly:20260101000000:global";
        await scheduler.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "nightly",
            Cron = "* * * * *",
            JobType = typeof(ProbeJob),
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
        var reclaimed = await store.GetAsync(jobId, cancellationToken);
        Assert.NotNull(reclaimed);
        Assert.Equal(JobStatus.Completed, reclaimed.Status);
        Assert.Equal(2, reclaimed.Attempt);
        Assert.Equal(1, probe.RunCount);
    }

    private static (JobScheduleProcessor Processor, Probe Probe) CreateProcessor(IJobRuntimeStore store, IMessageTransport? transport = null)
        => CreateProcessor(store, new InMemoryJobScheduler(), transport);

    private static (JobScheduleProcessor Processor, Probe Probe) CreateProcessor(IJobRuntimeStore store, IJobScheduler scheduler, IMessageTransport? transport = null)
    {
        var probe = new Probe();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        return (new JobScheduleProcessor(scheduler, store, worker, nodeId: "node-a", transport: transport), probe);
    }

    private sealed class Probe
    {
        private int _runCount;
        public int RunCount => Volatile.Read(ref _runCount);
        public void Record() => Interlocked.Increment(ref _runCount);
    }

    private sealed class ProbeJob(Probe probe) : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.Record();
            return Task.FromResult(JobResult.Success);
        }
    }

    private sealed class FailingJob : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(JobResult.FromException(new InvalidOperationException("boom")));
        }
    }

    private sealed class PreviewWorkItem
    {
        public string? Data { get; set; }
    }

    // Minimal pull transport with a configurable native delayed-delivery ceiling, so a delay beyond the cap is forced
    // through the runtime store (mirrors the fixture used by the in-memory MessageQueue tests).
    private sealed class CappedDelayTransport : IMessageTransport, ISupportsPull, ISupportsDelayedDelivery
    {
        private readonly Queue<TransportEntry> _entries = new();

        public CappedDelayTransport(TimeSpan? maxDeliveryDelay) => MaxDeliveryDelay = maxDeliveryDelay;

        public TimeSpan? MaxDeliveryDelay { get; }
        public int SendCount { get; private set; }
        public TransportSendOptions? LastSendOptions { get; private set; }

        public Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
        {
            SendCount += messages.Count;
            LastSendOptions = options;
            var items = new SendItemResult[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                string id = messages[i].MessageId ?? Guid.NewGuid().ToString("N");
                _entries.Enqueue(new TransportEntry { Id = id, Destination = destination, Body = messages[i].Body, Headers = messages[i].Headers, Receipt = new Receipt() });
                items[i] = new SendItemResult { MessageId = id, Success = true };
            }

            return Task.FromResult(new SendResult { Items = items });
        }

        public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TransportEntry>>(_entries.Count > 0 ? [_entries.Dequeue()] : []);

        public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
