---
name: foundatio
description: >
  Use when working with Foundatio infrastructure abstractions for .NET -- caching,
  queuing, messaging, file storage, distributed locking, or background jobs. Apply
  when using ICacheClient, IQueue, IMessageBus, IMessageClient, IMessageHandler, IJobClient,
  IJobHandler, IJob, IMessageTransport, IJobStore, IFileStorage, ILockProvider,
  or resilience patterns like retry and circuit breakers. Covers in-memory and
  production implementations (Redis, Azure, AWS, Kafka, RabbitMQ). Use context7
  MCP to fetch current API docs and examples.
---

# Foundatio

Pluggable infrastructure abstractions for distributed .NET apps. Interface-first, testable, swappable between in-memory (dev/test) and production providers (Redis, Azure, AWS) with zero application code changes.

## Documentation via context7

Use context7 MCP for complete, up-to-date API docs and examples. The main library ID covers all abstractions and implementations:

```text
query-docs(libraryId="/foundatiofx/foundatio", query="How to configure queue retry policies and dead letter handling")
```

Query with specific questions, not single keywords. All provider docs (Redis, Azure, AWS, Kafka, etc.) are included in the main library.

## Core Interfaces

| Interface | Purpose | In-Memory | Production |
| --------- | ------- | --------- | ---------- |
| `ICacheClient` | Key-value caching with TTL | `InMemoryCacheClient` | Redis, Hybrid |
| `IQueue<T>` | FIFO message queuing | `InMemoryQueue<T>` | Redis, SQS, Azure |
| `IMessageBus` | Pub/sub messaging | `InMemoryMessageBus` | Redis, Kafka, RabbitMQ, Azure |
| `IFileStorage` | File storage abstraction | `InMemoryFileStorage` | S3, Azure Blob |
| `ILockProvider` | Distributed locking | `CacheLockProvider` | Redis-backed |
| `IMessageClient` | Send commands / publish events to `IMessageHandler<T>` | `UseInMemory()` (`InMemoryMessageTransport`) | Provider `IMessageTransport`s |
| `IJobClient` | Durable jobs (`IJobHandler<TArgs>`, `IJobHandler`): progress, cancel, retry, CRON | `UseInMemory()` (`InMemoryJobStore`) | Provider `IJobStore`s |
| `IWorkMonitor` | Active/recent work, stats, backlog and job counts across instances | `AddFoundatioMonitoring()` (this process only) | `AddFoundatioMonitoring(m => m.UseCache())` with a shared `ICacheClient` (Redis) |
| `IJob` | Background job processing | N/A | Hosted services, or durable via `j.AddLegacyJob<T>()` |
| `ISerializer` / `ITextSerializer` | Binary and text serialization | `SystemTextJsonSerializer` | MessagePack, JsonNet |
| `IResiliencePolicy` | Retry, circuit breaker, timeout | `ResiliencePolicyBuilder` | N/A |

## DI Registration

All services are **singletons** (maintain internal state/connections). Jobs are scoped.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Quick start -- all in-memory defaults
builder.Services.AddFoundatio();

// Or register individually with options
builder.Services.AddSingleton<ICacheClient>(sp =>
    new InMemoryCacheClient(o => o.MaxItems(1000)
        .LoggerFactory(sp.GetRequiredService<ILoggerFactory>())));
builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
builder.Services.AddSingleton<IFileStorage, InMemoryFileStorage>();
builder.Services.AddSingleton<IQueue<OrderWorkItem>>(sp =>
    new InMemoryQueue<OrderWorkItem>());

// Lock provider (message bus optional but enables faster lock release via pub/sub)
builder.Services.AddSingleton<ILockProvider>(sp =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetService<IMessageBus>(),
        sp.GetService<TimeProvider>(),
        sp.GetService<ILoggerFactory>()));
```

Swap to production by changing only DI registration:

```csharp
// DI owns and disposes the shared multiplexer during host shutdown.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect("localhost:6379"));

builder.Services.AddSingleton<ICacheClient>(sp =>
    new RedisCacheClient(o =>
    {
        o.ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
        o.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
builder.Services.AddSingleton<IMessageBus>(sp =>
    new RedisMessageBus(o =>
    {
        o.Subscriber = sp.GetRequiredService<IConnectionMultiplexer>().GetSubscriber();
        o.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
builder.Services.AddSingleton<IQueue<OrderWorkItem>>(sp =>
    new RedisQueue<OrderWorkItem>(o =>
    {
        o.ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
        o.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## Usage Patterns

### Caching

```csharp
await _cache.SetAsync("user:123", user, TimeSpan.FromHours(1));

var result = await _cache.GetAsync<User>("user:123");
if (result.HasValue)
    return result.Value;

await _cache.IncrementAsync("requests:today", 1);
await _cache.RemoveByPrefixAsync("user:");
```

### Queues

```csharp
await _queue.EnqueueAsync(new OrderWorkItem { OrderId = orderId });

// Multi-tenant: tag messages with a group id (SQS fair queues / FIFO ordering)
await _queue.EnqueueAsync(workItem, new QueueEntryOptions { GroupId = tenantId });
// or derive it from the payload once: new SQSQueue<T>(o => o.GroupId(x => x.TenantId))

var entry = await _queue.DequeueAsync(TimeSpan.FromSeconds(5));
if (entry is not null)
{
    await ProcessAsync(entry.Value);
    await entry.CompleteAsync();   // success
    // or: await entry.AbandonAsync();  // retry later
}
```

### Messaging (Pub/Sub)

```csharp
await _messageBus.SubscribeAsync<OrderCreated>(async (msg, ct) =>
{
    await HandleOrderCreatedAsync(msg, ct);
});

await _messageBus.PublishAsync(new OrderCreated { OrderId = orderId });
```

### File Storage

```csharp
await _storage.SaveFileAsync("reports/monthly.pdf", pdfStream);

using var stream = await _storage.GetFileStreamAsync("reports/monthly.pdf", StreamMode.Read);
var exists = await _storage.ExistsAsync("reports/monthly.pdf");
await _storage.DeleteFilesAsync("reports/old-*");
```

### Distributed Locks

```csharp
await using var lck = await _locker.AcquireAsync(
    "resource:order-123",
    timeUntilExpires: TimeSpan.FromMinutes(1));

if (lck is not null)
{
    await DoExclusiveWorkAsync();
}
// lock auto-released via IAsyncDisposable
```

### Resilience

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))
    .WithJitter()
    .Build();

await policy.ExecuteAsync(async ct =>
{
    await unreliableService.CallAsync(ct);
});
```

## Jobs

### Standard Job

```csharp
public class CleanupJob : JobBase
{
    public CleanupJob(
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory = null)
        : base(timeProvider, resiliencePolicyProvider, loggerFactory) { }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        await CleanupOldRecordsAsync(context.CancellationToken);
        return JobResult.Success;
    }
}
```

### Job with Lock (Singleton / Leader Election)

`JobWithLockBase` acquires a distributed lock before each run. If the lock isn't available the run is cancelled. Implements `IJobWithOptions`.

```csharp
[Job(Description = "Singleton maintenance", Interval = "5s")]
public class MaintenanceJob : JobWithLockBase
{
    private readonly ILockProvider _lockProvider;

    public MaintenanceJob(
        ICacheClient cache, IMessageBus messageBus,
        TimeProvider timeProvider, IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory) : base(timeProvider, resiliencePolicyProvider, loggerFactory)
    {
        _lockProvider = new CacheLockProvider(cache, messageBus, loggerFactory);
    }

    // new CancellationToken(true) = try once, skip if lock is held
    protected override Task<ILock?> GetLockAsync(CancellationToken cancellationToken) =>
        _lockProvider.AcquireAsync(nameof(MaintenanceJob), TimeSpan.FromMinutes(15),
            cancellationToken: new CancellationToken(true));

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        await DoMaintenanceAsync(context.CancellationToken);
        return JobResult.Success;
    }
}
```

### Queue Processor Job

```csharp
public class OrderProcessorJob : QueueJobBase<OrderWorkItem>
{
    public OrderProcessorJob(
        IQueue<OrderWorkItem> queue,
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory = null)
        : base(queue, timeProvider, resiliencePolicyProvider, loggerFactory) { }

    protected override async Task<JobResult> ProcessQueueEntryAsync(
        QueueEntryContext<OrderWorkItem> context)
    {
        var item = context.QueueEntry.Value;
        await ProcessOrderAsync(item.OrderId, context.CancellationToken);
        return JobResult.Success;
    }
}
```

### Hosting Integration

Requires `Foundatio.Extensions.Hosting` package:

```csharp
builder.Services.AddJob<CleanupJob>(o => o.WaitForStartupActions());
builder.Services.AddCronJob<CleanupJob>("0 */6 * * *");
builder.Services.AddDistributedCronJob<CleanupJob>("0 */6 * * *");
```

## Messages and Durable Jobs (new in v14, alongside IQueue/IMessageBus)

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseServiceName("orders")               // names durable subscriptions; required on every transport but in-memory
    .UseInMemory()                          // or UseRedis() / UseSqs(options) / UseKafka("host:9092") / UseAzureServiceBus(connectionString) / UseRabbitMQ("amqp://...") / UseAzureStorageQueues(connectionString)
    .AddConsumer<SendReceiptHandler>()      // commands: SendAsync -> exactly one consumer
    .AddSubscriber<OrderPlacedHandler>()    // events: PublishAsync -> one copy per service
    .AddSubscriber<CacheBuster>(new SubscriptionOptions { Scope = SubscriptionScope.Instance }));

public class SendReceiptHandler(IEmail email) : IMessageHandler<SendReceipt>
{
    public Task HandleAsync(SendReceipt m, IMessageContext context, CancellationToken ct) => email.SendAsync(m, ct);
}

builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()                          // or UseRedis()
    .AddJob<GenerateReportJob>()            // IJobHandler<GenerateReport>; enqueue by args: jobs.EnqueueAsync(new GenerateReport(...))
    .AddCronJob<CleanupJob>("0 3 * * *"));  // IJobHandler (no args, gets IJobContext); one run per occurrence cluster-wide
```

- Registered handlers/jobs start with the host; `AddFoundatio*` calls accumulate. No handlers registered = send/enqueue only. Handler and job classes are registered in DI, so `ValidateOnBuild` names a missing dependency at startup. `MessagingOptions`/`JobWorkerOptions` are standard options (`services.Configure<T>`, config binding); `AddConsumer`/`AddSubscriber`/`AddJob` also take `Func<IServiceProvider, TOptions>` to read options from DI. Sending a type this process only subscribes to (or publishing one it only consumes) warns once. `AddJobLifetimeService()` counts v2 handlers, the job worker and CRON as running work.
- Shutdown: messaging and jobs stop taking work as soon as the host begins stopping, then drain together for `ShutdownTimeout` (20 s) + 5 s; keep that under the host's `ShutdownTimeout` (30 s) and the orchestrator's grace period. `CancelAsync` reaches a job in this process at once, in another within ~5 s. A custom `IJobStore` without `ClaimBatchAsync` claims one job per round trip.
- Providers: `UseRedis` (Foundatio.Redis, messaging and jobs), `UseSqs` (Foundatio.AWS), `UseKafka` (Foundatio.Kafka), `UseAzureServiceBus` (Foundatio.AzureServiceBus), `UseRabbitMQ` (Foundatio.RabbitMQ, RabbitMQ 4), `UseAzureStorageQueues` (Foundatio.AzureStorage; connection string, `ServiceUri` + `TokenCredential`, or a `QueueServiceClient` from DI), in namespace `Microsoft.Extensions.DependencyInjection`. Each takes an options record or a `Func<IServiceProvider, TOptions>`; `UseRedis()` resolves `IConnectionMultiplexer` from DI (host fails to start without one) or takes one; `UseAzureServiceBus()` without a connection in its options resolves `ServiceBusClient` (+ `ServiceBusAdministrationClient` unless `CreateEntities = false`) from DI or takes them; `UseRabbitMQ()` without a `ConnectionString` resolves `IConnection` from DI (Aspire's `AddRabbitMQClient`) or takes one, and with one opens its own connections. DI creates and disposes the transport; unset `LoggerFactory`/`TimeProvider` come from DI. Options types live in `Foundatio.Messaging.Transport` / `Foundatio.Jobs.Store`. An enqueue-only process needs the workers' store, not `UseInMemory()`. `UseRedis("host:6379")` registers one shared multiplexer (caches, locks and jobs can use it) that logs only SE.Redis warnings and errors (for its full log, register a multiplexer with `ConfigurationOptions.LoggerFactory` and call `UseRedis()`); Redis is the only shared `IJobStore`, so apps on any other transport store jobs with `j.UseRedis(...)`; `UseSqs()` uses `IAmazonSQS`/`IAmazonSimpleNotificationService` from DI when registered; `UseSqs("Region=...")` takes a connection string. Option names match across providers: `MaxMessageSize`, `CreateEntities` (Kafka, was `CreateTopics`), `DeduplicationWindow`, `InstanceSubscriptionTimeout`. `UseRedis(null)` is ambiguous. Job stores throw `JobStoreException`.
- Throw to retry (`RetryPolicy`, default 5 attempts, 1s/4s/16s/64s), then dead-letter (messages) or `Failed` (jobs). `context.DeadLetter(reason)` / `context.Retry(delay)` decide without throwing; `RetryPolicy.Default.DoNotRetry<T>()` fails fast.
- `SendAsync`/`PublishAsync` return the message id; batch methods return ids in order, and `SendBatchAsync(messages, m => new MessageKeys { MessageId = ..., DeduplicationId = ..., GroupKey = ... })` keys each message so a failed batch can be retried safely. `IMessageClient` has 5 members (no-key batch overloads are extensions); derive a decorator from `MessageClientDecorator` and override only what it changes. `Delay` with `DeliverAt` throws; application headers can't reuse `KnownHeaders` names. For one class that consumes a command and subscribes to an event, register per type: `AddConsumer<THandler, TCommand>()` + `AddSubscriber<THandler, TEvent>()` (the one-argument forms register every handled type both ways).
- At-least-once: handlers and jobs must be idempotent. Give contracts stable names: `[Message("orders.placed")]`, `[Job(Name = "reports.generate")]` (defaults are full type names).
- Delegate handlers get scoped services from `context.Services`. Unit test with `Foundatio.Testing.TestMessageContext` / `TestJobContext` (both record `ProgressReports`; last values in `Progress` and `ProgressMessage` / `Message`).
- Jobs: `IJobHandler` (no args; `jobs.EnqueueAsync<CleanupJob>()`, `[Job(Name)]` on the class) or `IJobHandler<TArgs>`; both get `IJobContext` (`Attempt`, `ScheduledFor` = the CRON occurrence, `Headers`, `ReportProgressAsync`). `j.AddJob<T>()` throws for a legacy `IJob`: wrap it with `j.AddLegacyJob<T>()` + `EnqueueAsync<LegacyJob<T>>()` / `j.AddCronJob<LegacyJob<T>>(...)` (keeps its name), or keep continuous jobs on `services.AddJob<T>()`. `GetAsync`/`QueryAsync` return `JobDetails` (no claim token or payload; `GetArguments<T>()` is null once the job succeeded, since succeeded jobs drop their payload). Enqueue stores `Activity.Current` as `traceparent`/`tracestate` headers and defaults `CorrelationId` to its id; the job's activity continues the trace. A retained job costs ~1.6 KB in Redis: size for rate x `RetainSucceededFor` (1 day; also settable per `JobTypeOptions` and per `CronJobOptions`, zero allowed, though `WaitForCompletionAsync` then sees nothing). Seconds-level CRON (`*/5 * * * * *`) is fine with zero retention: the worker keeps each run until it can't be created again. Register a schedule in every process that runs its job. `EnqueueOptions.ConcurrencyKey` = one running job per name and key (blocked jobs wait without using attempts; replaces `GetWorkItemLockAsync`); `EnqueueOptions.DeduplicationKey` = enqueue returns the waiting job until it starts (replaces `DuplicateDetectionQueueBehavior`). Both keys are per job name. Prefer arguments that implement `IHaveJobKeys` (`string? IHaveJobKeys.ConcurrencyKey => ProjectId;`, explicit so it isn't serialized) over passing the keys at each enqueue site: they apply in every process and to CRON occurrences; `EnqueueOptions` wins. A job no process registers waits forever; a producer logs a warning when the first job of a name is still queued a minute after it was due and nothing of that name runs. Custom stores: `IJobStore.CreateAsync` returns `string?` (the id).
- Progress: `IMessageContext` and `IJobContext` both derive from `Foundatio.IHandlerContext` (`Attempt`, `CorrelationId`, `Headers`, `ReportProgressAsync(percent, message)`). Percent 0-100 (else `ArgumentOutOfRangeException`); null keeps the last value; throttled, so call it in loops. Messages: updates the monitor, adds a `progress` span event, logs Debug, never touches the transport (leases renew anyway). Jobs: also saved to the store (`JobDetails.Progress`/`Message`).
- Monitoring (`Foundatio.Monitoring`): every process tracks its messages and jobs; `services.AddFoundatioMonitoring(m => m.UseCache())` adds `IWorkMonitor` and writes a snapshot per instance to the shared `ICacheClient` every 5s (expires after 3 intervals; removed on graceful shutdown). `GetOverviewAsync()` returns instances, active work (running > 1s or reported progress), recent work (visible items + every retry/failure/dead letter), rolling 5-min stats for all work, backlog (`IMessageAdmin.GetStatsAsync` per durable source) and job counts. Without `UseCache` it shows only this process. `IMessageAdmin.GetStatsAsync(source)` counts one queue/subscription (`Queued`/`Delayed`/`InFlight`/`DeadLettered`, each nullable).
- Keep derived events in the same assembly as their base type: Foundatio registers the concrete types of each handled type's assembly at startup. An abstract or interface base whose derived type the subscriber can't load is skipped with a warning.
- `ScheduledFor` stays the occurrence across retries and shutdown releases; `JobDetails.AvailableAt` moves.
- `m.AddLegacyMessageBus()` serves the original `IMessageBus` (HybridCache, CacheLockProvider) from the new transport; it handles messages in order, receiving batches of up to 100. It is not wire compatible with `RedisMessageBus`/`SQSMessageBus`/etc., so every process sharing the bus must switch in the same deployment (rolling deploys split cache invalidation); until then keep the original bus. Pub/sub between `IMessageBus` and `IMessageClient` works only through `AddLegacyMessageBus()`, and only for types every service maps (`MapMessage<T>`, `[Message]`) or handles; each subscriber gets a publish once. Such a legacy publish is sent twice (legacy topic + v2 topic) until the type is listed in `AddLegacyMessageBus(o => o.MigratedMessageTypes.Add(typeof(T)))` in every process; then it goes once to the v2 topic and remaining legacy subscribers read it there.
- One subscription per service and topic, shared by all its subscribers: one delivery, so if any handler throws all of them run again, and they must set equal `RetryPolicy`/`LeaseDuration`/`MaxConcurrency` (unset != set; policies from `RetryPolicy.Exponential`/`Fixed`/`DoNotRetry` compare equal, hand-written delegates must share the instance) or startup throws. Same for consumers of types routed to one queue. An instance-scoped handler of N types opens N instance subscriptions (one per topic); route the types to one topic (`MapMessage<T>(name, topic: "notifications")`) to open one. Isolate a handler with `new SubscriptionOptions { Name = "billing-crm" }`; a new name starts at "now" and orphans the old subscription's backlog.
- `GroupKey` without `Capabilities.OrderedGroups` (SQS standard, Service Bus without `RequiresSession`, RabbitMQ) or `DeduplicationId` without `Capabilities.Deduplication` (SQS standard, Kafka, Service Bus without `RequiresDuplicateDetection`, RabbitMQ) is sent anyway with a once-per-type warning; `o.RequireTransportCapabilities = true` throws `MessageBusException` instead.
- Dead letters: inject `IMessageAdmin` -> `PeekDeadLettersAsync(source, skip, max)`, `ReplayDeadLettersAsync(source)` (back to that queue/subscription only), `ReplayDeadLetterAsync`/`DeleteDeadLetterAsync(source, messageId)` (one by id, looks through up to 10,000; throws on Kafka), `PurgeDeadLettersAsync`, `DeleteAsync` (retired subscriptions). Name sources from registrations: `admin.GetQueue<T>()`, `admin.GetSubscription<T>(name?)`, `admin.GetSources()`; `MessageSource` is in `Foundatio.Messaging`.
- Telemetry follows the OpenTelemetry messaging conventions: spans `send {destination}`/`process {destination}`, instruments `messaging.client.sent.messages`, `messaging.process.duration` (seconds), Foundatio tags under `foundatio.*` (`foundatio.outcome`, `foundatio.job.*`); see the Monitoring guide for the full list. Docs pages: `/guide/messaging` is v2, `/guide/message-bus` the legacy `IMessageBus`, `/guide/migrating-to-messaging` the migration guide.
- Monitoring gotchas: use a cache every instance shares (Redis); `HybridCacheClient`/`InMemoryCacheClient` show stale or partial data. A process with no handlers and no jobs (a dashboard) publishes nothing but reads all; for backlog and job counts it needs messaging/jobs on the same transport/store (no handlers). Fast handlers that don't report progress never show as active, only in stats. Errors are the exception type + message (1,024 chars max); payloads and headers are never recorded. Handlers using `IHandlerContext` need `using Foundatio;`.
- Service Bus transport gotchas: entities are `{Prefix}queue.{name}` / `{Prefix}topic.{name}` (queues and topics share a namespace; names are case-insensitive); the lease is the entity's `LockDuration` (default 1 min, max 5), not the handler's `LeaseDuration`; `RequiresSession`/`RequiresDuplicateDetection` must match existing entities (a session mismatch throws); in session mode a delayed grouped message or retry keeps its session locked by one receiver until due; `CreateEntities = false` disables instance subscriptions and queue/subscription deletion; on the emulator `GetStatsAsync` returns null and `MaxDeliveryCount` must be at most 10. More than 256 parked sessions (delayed or retrying group heads) per receiver slows how fast ready groups are found.
- RabbitMQ transport gotchas: leases never expire, but the broker closes a channel that holds a delivery past `consumer_timeout` (30 min default), redelivering everything on it, so raise it (a `consumer-timeout` policy) for long handlers or use jobs; delays are durable delay queues on the broker in whole seconds (no plugin); `GetStatsAsync` needs `ManagementEndpoint` to count in-flight messages; a `max-length` + `overflow: reject-publish` policy on a queue that gets delayed messages holds up that queue's own delays (and a subscription's topic's delayed publishes) for 3–6 min after it has room again (shorten with `dead_letter_worker_publisher_confirm_timeout`); a refusing instance subscription, a target whose landing queue was deleted (until it is declared again) and a landing queue under a `max-length` policy each hold a level for every queue under the prefix (warned about with `ManagementEndpoint`); one target's delays pass through its landing queue at about 7–8k/s, so a burst to one target arrives a few hundred ms late; to upgrade a broker from a pre-landing-queue preview, copy `<prefix>.delay`'s bindings to `<prefix>.delay.final` before deleting it (README). Run clusters with `cluster_partition_handling = pause_minority`; sends fail with `MessageBusException` after `PublishTimeout` (30 s) without confirms, and the messages may or may not have arrived. Dead-lettering is copy-then-ack, so a lost channel can leave a duplicate dead letter (also SQS, Kafka): make replays idempotent.
- Kafka transport gotchas: sends use `linger.ms=0` (every send is awaited); override via `ConfigureProducer` only to trade latency for fewer broker requests. A full producer queue makes a send wait instead of throwing. Delayed messages to a deleted queue are dropped when due. Groups are isolated per partition and `Partitions` defaults to the broker's `num.partitions` (often 1; warned on the first grouped send), so set it to at least instances and busy groups before topics exist. A busy group's later records are skipped and re-read by a groupless consumer; past 10,000 records or 100,000 offsets per partition, or 100,000 records per receiver, it pauses and logs the groups; a crash re-reads from the partition's oldest unfinished record (usually the busy group's unsettled head), which is older than its skipped ones. A killed consumer's partitions wait for the session timeout (45 s default; the README shows how to shorten it).
- Gotchas: `[Job(Name)]` for `IJobHandler<TArgs>` goes on the args record (on the job class it throws). Publish delays beyond the transport's `MaxPublishDelay` throw. Oversized messages throw `MessageTooLargeException`. With `GroupKey` on an ordering transport, a retry holds the group's later messages (each counts an attempt). Retries the transport can't delay are held in-process outside their handler slot (up to 10 × `MaxConcurrency`); delays within 20% under `MinRetryDelay` are lengthened to it. Settlements waiting on the broker run off the handler slot (up to `max(2 × MaxConcurrency, MaxReceiveBatchSize)`). A `RetryPolicy` that throws dead-letters the message. On SQS, Redis and Service Bus a handler is cancelled (and its message not settled) when renewals can't reach the broker before the lease ends; on RabbitMQ and Kafka a redelivery can overlap the old handler by up to about 1 s after the broker releases it. On transports declaring `ReceiveAhead` (SQS, Redis, Service Bus without sessions) receives run ahead of free handler slots into a small buffer (leased, renewed, counted `Buffered` not `Active`; up to 13 × `MaxConcurrency` + depth unsettled), and an idle source keeps one long poll.
- Wrap a job store with `JobStoreDecorator`; a hand-written wrapper loses `ClaimBatchAsync` and the store-clock `CreateAsync`. Stores keep a finished job until `JobInfo.RetainUntil` when later than its retention (CRON uses it), and `ClaimBatchAsync` honours `JobClaimRequest.MaxPerName`. `CreateTransportWithClock` opts a transport into the frozen-clock conformance test. Status enums (`JobState`, `MessageSourceKind`, `WorkOutcome`, `MessageOutcome`, `ProcessingState`) serialize as names.
- Writing a provider: implement `IMessageTransport` (send to queue/topic/one subscription, receive, delete, `GetStatsAsync` counts: null counts you can't read, null result for instance subscriptions, empty for unknown sources, `ArgumentException` for dead-letter sources) + `IMessageReceiver` or `IJobStore`, declare `TransportCapabilities` honestly (`MaxDelay`/`MaxPublishDelay`/`MaxRetryDelay`, `MinRetryDelay` for coarse delays (core may keep up to 13 × `MaxConcurrency` deliveries unsettled: handling, held retries and settlements off the slot), `MaxMessageSize` after encoding overhead, `ReceiveAhead` (opt-in) when a receive is a broker round trip returning what it has, not for receivers that buffer or receive in the background); throw `MessageReceiverFaultedException` from `ReceiveAsync` when the client is dead so core replaces the receiver; when the broker sets the lease (Service Bus lock duration), set `IncomingMessage.LeaseExpiresAt` in `ReceiveAsync` and `RenewAsync` so core renews by it (count it from when the receive returned, not when it started: a long poll can outlast the lock before the message arrives); without lease expiration, `RenewAsync` is still called about once a second, so throw `MessageLeaseLostException` there when a delivery moved, and derive tests from `MessageTransportTestsBase` / `JobStoreTestsBase` (override `CreateTransport`/`CreateStore`; override `CreatePeerTransport` when a second instance over the same backend needs different setup, since the suite uses it to act as another process). Implement the SPI explicitly on a public transport class (each in-repo test project checks no public unmarked type exposes it); use `SettlementBatcher<TEntry>` to coalesce settlements into batch calls; declare `InOrderCompletion` when you can't remove one message from the middle of a source (Kafka), and `OneOutstandingPerGroup` when the broker hands out one message of a group at a time.
- FOUNDATIO0001 (an error, not a warning): the provider SPI (`IMessageTransport`, `IMessageReceiver`, `IncomingMessage`/`OutgoingMessage`, `IJobStore`, `JobInfo` and its request types, `SettlementBatcher<TEntry>`, the conformance suites) is `[Experimental]` and may change in a minor release, so a provider project adds `<NoWarn>$(NoWarn);FOUNDATIO0001</NoWarn>`; app code never needs it (use the `Use*` helpers, or pass a concrete transport/store to `UseTransport`/`UseStore`).

Whole-app integration tests (`Foundatio.Testing`, no xUnit dependency):

```csharp
builder.Services.AddOrdersApp();              // real AddFoundatioMessaging/AddFoundatioJobs registrations
builder.Services.AddFoundatioTestHarness();   // any order: owns transport + job store (Use* calls are ignored) and the clock, records everything
// emulate production: AddFoundatioTestHarness(o => o.TransportCapabilities = new TransportCapabilities { MaxDelay = TimeSpan.FromMinutes(15) })
await host.StartAsync();
var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

await messages.PublishAsync(new OrderPlaced(1));
await harness.WaitForIdleAsync();                                   // handlers idle, no queued/running jobs
Assert.Single(harness.Sent<SendReceipt>());                         // payloads; SentMessages/PublishedMessages for metadata
var dead = await harness.WaitForDeadLetteredAsync<SendReceipt>();   // also WaitForHandledAsync<T>(count)
var run = (await harness.WaitForJobAsync<GenerateReport>())[0];     // finished runs; check run.State
await harness.TriggerCronJobAsync<CleanupJob>();                    // CRON never fires on its own under the harness
```

- `HandledMessages[i].Outcome`: `Completed`, `Retried`, `DeadLettered`, `Skipped` (no handler on a topic), `Expired`, `Released` (shutdown/lease lost); `Progress`/`ProgressMessage` hold the handler's last report. `JobRuns` includes retried runs (`State == Queued`); `Progress` is the job's last percentage.
- Waits are real time (default 30s) and throw `TimeoutException` explaining what happened (and, for a type nobody handled, asking whether you meant `PublishAsync`/`SendAsync`). `o.RetryDelay = TimeSpan.Zero` overrides every retry delay, handlers' own policies included. The harness runs on the system clock unless `o.TimeProvider` is set, even if the app registers a fake one.
- Step by step instead of `job.RunAsync()`: `AddFoundatioTestHarness(o => o.StartPaused = true)`, then `await harness.ProcessNextAsync<T>()` runs one due delivery or job run through the real dispatcher/worker, like one `RunAsync()` (what the handler sends back and each retry wait for the next call; `ProcessNextAsync<T>(count)` for N), and `ProcessPendingAsync<T>()` drains everything, both returning what they did; `WaitForIdleAsync()` while paused runs all work and pauses again; `PauseProcessingAsync()`/`ResumeProcessing()` switch modes. `harness.Reset()` clears records between tests sharing a host. Pausing holds the app's handlers and jobs only: `IMessageAdmin` dead-letter reads and `AddLegacyMessageBus()` subscribers keep running (the waits include those subscribers). To fail a publish in a test, `m.DecorateClient<FailFirstPublish>()` (a `MessageClientDecorator`), not rewriting DI descriptors. `HandledBy<THandler>()`/`WaitForHandledByAsync<THandler>()` count per handler (`WaitForHandledAsync<T>` counts per queue/subscription). `o.Transport`/`o.JobStore` share one in-memory transport/store across hosts (multi-pod tests; pausing is rejected then). `WaitForIdleAsync` ignores queues the app sends to but does not consume; with its own store it waits for (and its timeout names) queued jobs nothing in the app runs, while on a shared `JobStore` it ignores jobs this host doesn't run, so producer-only hosts can wait. To wait for every host, use `transport.WaitUntilIdleAsync(timeout)`. `PublishedMessages` records a legacy `IMessageBus` publish once per topic (`topic:<type>`, plus `topic:messages` unless the type is in `MigratedMessageTypes`): assert on `Address` to check a migration took effect; `Published<T>()` returns it once.

Health checks (`Foundatio.Extensions.Hosting`):

```csharp
builder.Services.AddHealthChecks()
    .AddFoundatioMessaging(tags: ["ready"])                         // per-source data: queue:x, subscription:t/s
    .AddFoundatioJobs(tags: ["ready"], unhealthyAfter: TimeSpan.FromMinutes(5)) // worker (running per job name) + scheduler
    .AddFoundatioCronJob("cleanup", succeededWithin: TimeSpan.FromHours(2));  // a schedule succeeded recently, on any instance (read from the store)
```

- `AddFoundatioCronJob<TJob>(succeededWithin)` names the schedule for you; a legacy job's schedule is named after `TJob` (`JobNames.Of<TJob>()`). A new schedule is Healthy until it is overdue. The check reads kept jobs: with `RetainSucceededFor = 0`, a success is kept only until the next occurrence or `MisfireThreshold`, so the host fails to start when `succeededWithin` is longer than both the retention and `MisfireThreshold` (set `RetainSucceededFor` to at least the window), or when this process doesn't register the schedule. Test a `ForTopics` split with `IMessageAdmin.GetTransportName(source)`.

- Use these for readiness, not liveness: a broker outage would restart every pod. On a liveness probe pass `failureStatus: HealthStatus.Degraded`. The host fails to start if a check's feature isn't registered.

- Starting (waiting for startup actions) = Healthy; retried receive/claim/schedule errors = Degraded with last error; Unhealthy (or `failureStatus`) only if a loop stopped, startup failed, or errors lasted `unhealthyAfter` (default 5 min). Handler/job exceptions never affect health. `MessagingStatus` / `JobsStatus` expose the raw `ProcessingStatus` snapshots.

## Testing

Use `Foundatio.Xunit.v3` for test logging and DI integration. Two base classes:

- **`TestWithLoggingBase`** -- lightweight, no DI container. `_logger` (`ILogger`) for logging; `Log` (`ILoggerFactory`) for passing to Foundatio services.
- **`TestLoggerBase`** -- full DI via `TestLoggerFixture`. Override `ConfigureServices` to register services. `Log` (`ILogger`) for logging; `TestLogger` (`ILoggerFactory`) for passing to Foundatio services.

```csharp
using Foundatio.Caching;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

public class OrderServiceTests : TestLoggerBase
{
    public OrderServiceTests(ITestOutputHelper output, TestLoggerFixture fixture)
        : base(output, fixture) { }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ICacheClient>(sp =>
            new InMemoryCacheClient(o => o.LoggerFactory(TestLogger)));
        services.AddSingleton<OrderService>();
    }

    [Fact]
    public async Task GetStatusAsync_WithCachedOrder_ReturnsCachedStatus()
    {
        // Arrange
        var cache = Services.GetRequiredService<ICacheClient>();
        await cache.SetAsync("order:123", "shipped");
        Log.LogInformation("Seeded cache with order status");

        // Act
        var status = await Services.GetRequiredService<OrderService>()
            .GetStatusAsync("123");

        // Assert
        Assert.Equal("shipped", status);
    }
}
```

## Gotchas

- **Lock returns null**: `TryAcquireAsync` returns `null` when the lock cannot be acquired -- always guard with `is not null` before doing work. `AcquireAsync` throws `LockAcquisitionTimeoutException` instead of returning null.
- **Dispose streams and locks**: `ILock` is `IAsyncDisposable` -- use `await using`. Streams from `GetFileStreamAsync` are `IDisposable` -- use `using var`.
- **Cache TTL floor**: Expiration values below 5ms are treated as already-expired and the key is silently removed. If you compute TTL dynamically (e.g., `expiresAt - now`), guard against near-zero values.
- **Cache `GetAsync` returns `CacheValue<T>`**: Check `result.HasValue` before accessing `result.Value`. A missing key returns `HasValue = false`, not an exception.
- **Cache stampede (thundering herd)**: The cache-aside pattern (`Get` -> miss -> load -> `Set`) is vulnerable to stampedes when a popular key expires and many callers regenerate simultaneously. Use `CacheLockProvider` to serialize regeneration: acquire a lock keyed on the cache key, double-check the cache after acquiring, and only then call the backing store. See the [Cache Stampede Protection](https://foundatio.readthedocs.io/guide/caching.html#cache-stampede-protection) docs for the full pattern.
- **Queue auto-complete**: `QueueJobBase<T>` auto-completes entries based on `JobResult` by default. Set `AutoComplete = false` only when you need manual `CompleteAsync()`/`AbandonAsync()` control. Manual `DequeueAsync` does NOT auto-complete.
- **GetQueueEntryLockAsync error handling**: If `GetQueueEntryLockAsync` returns `null`, the queue entry is abandoned. If it throws, the entry is also abandoned and a `JobResult.FromException` is returned. Use `TryAcquireAsync` (not `AcquireAsync`) in your override since the return type is `Task<ILock?>`.
- **Failure semantics depend on job type**: `JobResult` only has `IsSuccess` -- there is no separate "failed but don't retry" status. For **queue-processed jobs** (`QueueJobBase<T>.ProcessQueueEntryAsync`, or setting `context.Result` in a `WorkItemJob` handler), a non-success result triggers `AbandonAsync`, which re-queues the entry and eventually dead-letters it after `Retries` is exhausted -- reserve `FailedWithMessage`/`FromException` for transient errors you want retried, and log + return `JobResult.Success`/`SuccessWithMessage(...)` for permanent errors to avoid a pointless retry loop. For **standalone/manual jobs** (`JobBase`, a one-off `RunAsync()`/`RunInConsoleAsync()` run, or scheduled/cron jobs via `Foundatio.Extensions.Hosting`), there is no built-in retry or dead letter queue -- a failed result just produces an error-level log, a non-zero exit code from `RunInConsoleAsync`, or a failed entry in the job run history. Returning `FailedWithMessage`/`FromException` there is correct even for permanent errors, since nothing inside Foundatio will retry it.
- **JobWithLockBase vs manual locking**: Use `JobWithLockBase` when the entire run must be single-instance (leader election). Use manual `ILockProvider.AcquireAsync` inside `JobBase` for finer-grained locking within a job.
- **Legacy job constructors**: nothing registers `TimeProvider` or `IResiliencePolicyProvider`; take them as `= null` constructor parameters. `AcquireAsync` throws on a held lock; use `TryAcquireAsync` to skip.
- **JobContext.RenewLockAsync**: Call in long-running jobs (both `JobBase` and `QueueJobBase`) to prevent lock expiration mid-processing.
- **Register as singletons**: All infrastructure services (`ICacheClient`, `IMessageBus`, `IQueue<T>`, `IFileStorage`, `ILockProvider`) maintain internal state and connections -- always register as singletons.
- **CacheLockProvider + IMessageBus**: `IMessageBus` is optional but recommended. Without it, lock release falls back to polling. With it, locks are released instantly via pub/sub notification.
- **In-memory for tests**: In-memory implementations follow the same interfaces as production providers, but some behaviors differ (delivery delay limits, ordering, deduplication, group ids). Swap via DI for fast, isolated unit tests, and check [provider behavioral gaps](https://foundatio.dev/guide/provider-behavioral-gaps) before relying on provider-specific behavior.
- **Queue `GroupId` is a hint**: It only changes delivery on SQS (fair queues on standard queues, strict ordering on `.fifo` queues) and session-enabled Azure Service Bus queues (ordered per group, one message at a time per group; `RetryDelay` is not applied, so an abandoned message retries ahead of the rest of its group; a group id of at most 128 characters is required). The other built-in queues store it as metadata and log one debug message. SQS group ids are max 128 characters with no spaces. Empty values are stored as `null`. A non-empty explicit `QueueEntryOptions.GroupId` overrides the queue's `GroupId(...)` resolver; null or empty invokes the resolver. Resolvers should return `null` for no group.
- **Storage Queues dead letters and names**: dead letters never expire; retries and renewals stop 1 s before a message's TTL. Headers that leave no room for a 1,024-byte reason are refused at send; another producer's messages up to 64 KiB dead-letter whole. A publish skips instance queues that vanish mid-fan-out and throws one `MessageBusException` for other failures after all sends finish. `GetQueueName(source)` gives queue names for IaC; the transport needs Storage Queue Data Contributor.
- **Storage Queues can't long-poll**: with `UseAzureStorageQueues()`, a message from another process waits up to `MaxPollInterval` (2 s) after a quiet spell, and each idle receiver polls about every 2 s. With Redis available, use `UseAzureStorageQueues(...).ForTopics(t => t.UseRedis())` so publishes, subscribers and the legacy bus (`CacheLockProvider`) are pushed; every process sharing a topic must split the same way, and the test harness ignores `ForTopics`. Messages are limited to 46 KiB with headers (`MessageTooLargeException`; put large payloads in `IFileStorage`). No ordered groups or deduplication. v2 doesn't read legacy `AzureStorageQueue<T>` queues, so drain those first.
- **`EnqueueAsync` copies `QueueEntryOptions`**: The caller's instance is never modified and can be reused. `Enqueuing` handlers get the per-message copy, with the resolved `GroupId` and the `Activity.Current` `CorrelationId`/`TraceState` already filled in. `Properties` is copied into a writable `Dictionary` per enqueue, keeping the key comparer of built-in dictionary types (`Dictionary`, `ConcurrentDictionary`, sorted and immutable dictionaries); other implementations use the default comparer.

## NuGet Packages

### Core

| Package | Provides |
| ------- | -------- |
| `Foundatio` | Core interfaces, in-memory implementations, resilience, `SystemTextJsonSerializer` |
| `Foundatio.Extensions.Hosting` | `AddJob`, `AddCronJob`, `AddDistributedCronJob`, startup actions, hosted services |

### Serializers

`ITextSerializer` extends `ISerializer` for human-readable formats (JSON). `ISerializer` covers binary formats. Default is `SystemTextJsonSerializer` (included in core).

| Package | Provides |
| ------- | -------- |
| `Foundatio.JsonNet` | `JsonNetSerializer` : `ITextSerializer` (Newtonsoft.Json) |
| `Foundatio.MessagePack` | `MessagePackSerializer` : `ISerializer` (binary, high-throughput) |
| `Foundatio.Utf8Json` | `Utf8JsonSerializer` : `ITextSerializer` (fast JSON) |

### Providers

| Package | Provides |
| ------- | -------- |
| `Foundatio.Redis` | Redis cache, queue, messaging, locks, storage |
| `Foundatio.AzureStorage` | Azure Blob storage, Storage queues (legacy `AzureStorageQueue<T>` and v2 `UseAzureStorageQueues()`) |
| `Foundatio.AzureServiceBus` | Azure Service Bus queues, messaging, `AzureServiceBusMessageTransport` (`UseAzureServiceBus`) |
| `Foundatio.AWS` | SQS queues, SQS messaging, S3 storage |
| `Foundatio.Kafka` | Kafka messaging |
| `Foundatio.RabbitMQ` | RabbitMQ messaging |
| `Foundatio.Aliyun` | Aliyun OSS storage |
| `Foundatio.Storage.SshNet` | SFTP storage |

### Testing & Other

| Package | Provides |
| ------- | -------- |
| `Foundatio.TestHarness` | Shared test base classes for validating custom implementations, including `MessageTransportTestsBase` and `JobStoreTestsBase` conformance suites |
| `Foundatio.Xunit` | xUnit v2 test logging, retry attributes |
| `Foundatio.Xunit.v3` | xUnit v3 test logging, retry attributes |
| `Foundatio.DataProtection` | ASP.NET Core Data Protection key storage via `IFileStorage` |
