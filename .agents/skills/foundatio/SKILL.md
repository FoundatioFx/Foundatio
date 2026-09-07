---
name: foundatio
description: >
  Use when working with Foundatio infrastructure abstractions for .NET -- caching,
  messaging, background jobs, file storage, distributed locking, or queuing. Apply
  when using ICacheClient, IMessageBus, IJobClient, IJob, IFileStorage, ILockProvider,
  or resilience patterns like retry and circuit breakers. Covers in-memory and
  production implementations (Redis, AWS, Azure). Use context7 MCP to fetch current
  API docs and examples.
---

# Foundatio

Pluggable infrastructure abstractions for distributed .NET apps. Interface-first, testable, swappable between in-memory (dev/test) and production providers (Redis, AWS, Azure) with zero application code changes.

## Documentation via context7

Use context7 MCP for complete, up-to-date API docs and examples. The main library ID covers all abstractions and implementations:

```text
query-docs(libraryId="/foundatiofx/foundatio", query="How to configure messaging retry policies and dead letter handling")
```

Query with specific questions, not single keywords. All provider docs (Redis, Azure, AWS, Kafka, etc.) are included in the main library.

## Messaging and Jobs (current API)

- One messaging client: `IMessageBus` in `Foundatio.Messaging`. `SendAsync` targets competing queue consumers; `PublishAsync` fans out to existing event subscriptions. Delivery is at least once where supported, so handlers must tolerate duplicates. Both return application IDs, independently of broker IDs. Supply `MessageSendOptions.MessageId` / `MessagePublishOptions.MessageId` for retry correlation; this does not create exactly-once delivery. Batches return IDs in input order; `MessageSendException.Outcomes` distinguishes accepted, unknown, and unattempted inputs on failure.
- Publish has real pub/sub DROP semantics: a publish to a topic with no existing subscriptions is dropped (subscriptions are created when handlers subscribe or via topology provisioning -- subscribers must exist before the publish). A sent command waits durably on its queue instead. The in-memory transport warns once per topic on zero-subscription drops, and the core logs every produce at debug.
- Implement `IMessageHandler<T>` and explicitly register `.Messaging.AddConsumer<TMessage, THandler>()` for queued work or `.Messaging.AddSubscriber<TMessage, THandler>("billing")` for events. Each message uses its own DI scope. Dynamic equivalents are `ConsumeAsync<T>` and `SubscribeAsync<T>`, returning an `IMessageSubscription` with its structural `Source` address, Status, RecoveryVersion and WaitUntilReadyAsync. Transient temporary-lease renewal errors retry; definite loss recreates the listener. Derived local state must resynchronize after a recovery gap; HybridCacheClient clears its local cache automatically.
- `MessageConsumerOptions` sets an optional queue destination. MessageTypeName plus Destination/Topic on a handler binds its wire name and producer route; AddMessageType<T>(name, queue: ..., topic: ...) does the same for producers. GetRouteMaps() and startup logs expose mappings; startup validates duplicate wire names even when topology mode is None. `MessageSubscriptionOptions` sets an optional topic and optional durable subscription name (declarative default: UseServiceName, then hosting ApplicationName): replicas using the same name compete. In dynamic SubscribeAsync, null creates a temporary listener with a renewable expiration lease on in-memory/Redis; AWS requires a durable name. Shared `MessageHandlerOptions` controls endpoint concurrency (default 1), retries and acknowledgement. Duplicate concrete handlers and multiple interface/raw fallback handlers on one endpoint are rejected. Manual acknowledgement holds its concurrency slot until settlement.
- Routing is central: `.Messaging.ConfigureRouting(r => r.UseDefaultQueue(...).UseDefaultTopic(...).MapQueue<T>(...).MapTopic(...).UseConvention(...))`. Precedence: operation override > exact map > interface/base-type map > `MessageRouteAttribute` > configured default > convention > kebab-cased type name. Producer routing declares queues/topics, never phantom subscriber groups.
- Routing config doubles as topology declarations (`DestinationDeclaration` with a canonical `DestinationAddress` -- `ForQueue`/`ForTopic`/`ForSubscription`). `IMessageTopology` exposes `GetDeclarations()` / `EnsureAsync()` / `ValidateAsync()`. `.Messaging.ConfigureTopology(TopologyMode.Ensure | Validate | None)` picks whether the client creates missing destinations (default), only verifies they exist (throws at startup when missing), or never touches topology; AddMessageConsumers includes startup topology; producers can opt in with AddMessagingTopology. Registering a transport starts no hosted services.
- The CORE owns retry/dead-lettering identically on every transport: default `RetryPolicy` is `MaxAttempts` 5 with immediate-then-10s/20s/30s backoff (+/-20% jitter); configure via `.Messaging.ConfigureRetry(p => p with { ... })`. Dead-lettered messages go to the transport's native sink or a derived `"{source}.deadletter"` destination, stamped with `message.dead_letter.*` forensics headers (`KnownHeaders.DeadLetter*`). Never configure broker-native redrive policies.
- Settlement succeeds only after the broker operation succeeds. A failed DLQ write leaves the original unsettled. `IMessageContext` exposes application `Id`, diagnostic `BrokerMessageId`, `CompleteAsync`, `RejectAsync`, and cancellation. Expiring delivery leases are supervised and renewed while a handler runs; lease loss cancels the handler and prevents settlement. Direct loops use `await using var message = await bus.ReceiveAsync<T>(options, token)`; disposal returns unfinished work for redelivery. Raw receive requires an explicit destination.
- Transports advertise per-destination capabilities: `ITransportInfo.GetCapabilities(destination)` takes the `DestinationAddress` in question (most transports answer by its role) and returns `TransportCapabilities` (e.g. the AWS transport's queue role has a native 15-minute `MaxDeliveryDelay`; its topic role has none). Delays beyond a ceiling and store-parked retries fall back to the durable runtime store (`IScheduledDispatchStore`, satisfied by any `IJobRuntimeStore`) and are drained by an explicitly hosted ScheduledMessageDispatcher -- never silently truncated.
- Durable jobs: implement `IJob` (`Task<JobResult> RunAsync(JobExecutionContext context)`). `JobResult` is an immutable record -- return the shared `JobResult.Success`/`JobResult.Cancelled` statics or the `SuccessWithMessage`/`FailedWithMessage`/`CancelledWithMessage`/`FromException` factories (there is no `None`). `IJobClient.EnqueueAsync<TJob>()` / `EnqueueAsync<TJob, TArgs>(args)` (typed payloads) returns a `JobHandle`; `IJobMonitor` queries state; `IJobWorker` executes with per-run DI scopes, bounded concurrency, and supervised lease renewal. `JobExecutionContext` gives `JobId`/`Attempt`/`CancellationToken`, `GetArguments<TArgs>()`, `ReportProgressAsync`, `RenewLeaseAsync`, `IsCancellationRequestedAsync`; its public constructor makes a detached context for tests. `GetArguments<TArgs>` enforces the stored payload-type discriminator: requesting a different type than the job was enqueued with throws before deserialization. Hand-wiring outside DI: `JobWorker`/`JobScheduleProcessor` take `JobWorkerOptions`/`JobScheduleProcessorOptions` records for their optional dependencies.
- CRON: `.Jobs.AddCronJob<TJob>(cron)` or `.Jobs.AddCronJob<TJob,TArgs>(cron,args)`; typed jobs implement `IJob<TArgs>`. Schedules persist wire names, serialized payloads, time-zone IDs, retry budgets, and revisions. `ConfigurationVersion` must increase for a changed declaration; same-version restarts preserve runtime edits. `ScheduleAsync` uses revision checks. Global and per-node occurrences share the same job worker/state machine. Global is the default. PerNode requires Jobs.ConfigureWorker(o => o with { NodeId = ... }) or FOUNDATIO_NODE_ID; unclaimed occurrences expire after configurable UnclaimedLifetime (one day). Cache confirmed materializations only: OverlapBlocked must be retried within the misfire window.
- `AddFoundatioWorker` validates missing transports/stores during registration. Receiving options, durable names, concrete job types, and schedule options also fail at registration. With individually hosted roles, startup validation fails fast at boot with actionable messages: CRON jobs registered without a runtime store, or handlers registered without a transport, throw when the corresponding consumer/scheduler host starts (add `.Jobs.UseInMemory()` / `.Messaging.UseInMemory()` or the production `Use*`).
- Jobs exceptions on the trigger/resolve paths: `ScheduledJobNotFoundException` (unknown schedule name), `ScheduledJobDisabledException` (triggering a disabled schedule), and `JobException` (unresolvable job type); all derive from `JobException` : `InvalidOperationException`.
- Schedule management: `IScheduledJobManager` supports inspect, revision-checked updates, enable/disable, reschedule, remove, and manual trigger. The DI-configured manager rejects unregistered job types before saving a schedule. Manual triggers respect disabled/overlap policy. Removing a definition does not cancel already queued jobs.
- Stable wire names: `.Messaging.AddMessageType<T>("order-created.v1")` and `.Jobs.AddJobType<TJob>("name")` preserve persisted discriminators across refactors. Sends retain the runtime concrete type in the envelope while the declared type selects the route. Interface, abstract and object receivers resolve only explicitly registered names; it never scans loaded assemblies. Concrete handlers can use the default CLR full name. Producers and consumers must use the same serializer/content type. SystemTextJson defaults to application/json; other serializers default to byte-safe application/octet-stream unless ContentType is explicitly configured. Metadata and application IDs survive scheduling and dead-lettering. Dispatch IDs are independently generated; repeated application IDs do not deduplicate sends. Batch MessageBatchItem<T> supplies per-input IDs. Indexed outcomes distinguish Accepted, Rejected, Unknown and NotAttempted; retain error/retryability details. AWS uses native batches of ten; Redis pipelines bounded batches of 64 by default.
- Legacy implementations were removed. For migration, `Messaging.AddLegacyAdapter()` registers the old `IMessageBus`/`IMessagePublisher`/`IMessageSubscriber` interfaces as a thin adapter over the new bus (old handler code compiles unchanged; delete the call when migrated). Old jobs migrate mechanically: `RunAsync(CancellationToken)` becomes `RunAsync(JobExecutionContext)` (use `context.CancellationToken`), `QueueJobBase<T>`/`IQueue<T>` become `IMessageHandler<T>` + `SendAsync`, and `WorkItemJob` becomes `EnqueueAsync<TJob, TArgs>(args)` with `ReportProgressAsync`.

## Core Interfaces

| Interface | Purpose | In-Memory | Production |
| --------- | ------- | --------- | ---------- |
| `ICacheClient` | Key-value caching with TTL | `InMemoryCacheClient` | Redis, Hybrid |
| `IMessageBus` | Commands (`SendAsync`) + events (`PublishAsync`) over one client | `InMemoryMessageTransport` | Redis Streams, AWS SQS/SNS |
| `IJobClient` / `IJobMonitor` | Submit and observe durable background jobs | `InMemoryJobRuntimeStore` | `RedisJobRuntimeStore` |
| `IFileStorage` | File storage abstraction | `InMemoryFileStorage` | S3, Azure Blob, Minio |
| `ILockProvider` | Distributed locking | `CacheLockProvider` | Redis-backed |
| `ISerializer` / `ITextSerializer` | Binary and text serialization | `SystemTextJsonSerializer` | MessagePack, JsonNet |
| `IResiliencePolicy` | Retry, circuit breaker, timeout | `ResiliencePolicyBuilder` | N/A |

## DI Registration

Use `AddFoundatioWorker(configure)` from Foundatio.Extensions.Hosting (namespace Foundatio) for combined workers; put transport, store, handler, and job registrations in its callback. It hosts consumers, registered jobs and their scheduler, and delayed dispatch when a dispatch store is configured. Use the inert `AddFoundatio()` builder for producer-only apps and manual tests. Infrastructure services register as **singletons**. Handlers and jobs resolve in their own DI scope per message/run, so they can inject scoped dependencies.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoundatioWorker(foundatio => foundatio
    .Caching.UseInMemory()
    .Storage.UseFolder("data")
    .Locking.UseCache()
    .Messaging
        .ConfigureRouting(r => r
            .MapQueue<OrderSubmitted>("orders")
            .MapTopic("order-events", typeof(IOrderEvent)))
        .ConfigureRetry(p => p with { MaxAttempts = 5 })
        .UseInMemory()
    .AddConsumer<OrderSubmitted, SendConfirmationHandler>()
    .Builder.Jobs.UseInMemory()
    .AddJobType<RebuildSearchIndexJob>("search.rebuild"));
```

Swap to production by changing only the provider lines:

```csharp
builder.Services.AddFoundatio()
    .Messaging.UseRedis(connectionString: "localhost:6379")   // Redis Streams transport
    .Builder.Jobs.UseRedis();                                         // Redis job runtime store

// or AWS (SQS queues, SNS+SQS pub/sub; point ServiceUrl at LocalStack for local dev)
builder.Services.AddFoundatio()
    .Messaging.UseAws(o => o.ResourcePrefix = "myapp");
```

Custom providers plug in via `.Messaging.UseTransport(...)` (any `IMessageTransport`) and `.Jobs.UseRuntimeStore(...)` (any `IJobRuntimeStore`). The zero-dependency starting point is `samples/Foundatio.QuickstartSample` in the Foundatio repo -- a generic-host console app running messaging and jobs fully in-memory with plain `dotnet run`.

- Prefer ConfigureMessaging(m => ...) and ConfigureJobs(j => ...) blocks. Every messaging/job method returns its feature builder; .Builder returns to the root. AddSubscriber defaults to one durable subscription per service, while dynamic unnamed SubscribeAsync remains temporary.
- Messaging.UseInMemory/UseRedis supply a matching scheduled dispatch store without registering jobs. The automatic Redis store inherits transport connection, clock and KeyPrefix; configure its budgets with RedisStreamsMessageTransportOptions.Scheduling. AWS needs UseSchedulingStore for non-native delays. HybridCacheClient requires temporary subscriptions and fails immediately on AWS; CacheLockProvider falls back to polling.
- JobRequestOptions supports mutually exclusive Delay/RunAt, MaxAttempts and a persisted JobRetryPolicy (10s initial, multiplier 2, 5min cap, 20% jitter). A failed JobResult with Retryable=false is terminal. JobState.ResultMessage holds success text; Error is reserved for failures. JobHandle.WaitForCompletionAsync defaults to a five-minute wait; cancelling the wait does not cancel work. Context helpers inherit the execution cancellation token by default.
- Hosted job slots replenish independently; RunQueuedAsync remains a bounded drain. Jobs are scoped and disposed, including fallback activation. Shutdown returns owned unsettled messages with a bounded independent token; a lost lease cannot settle replacement work. In-memory transport uses finite visibility and shared pull concurrency.
- AWS automatically coalesces concurrent sends/publishes/deletes with bounded per-destination buffers; completion still requires each broker result. Caller cancellation never cancels a shared batch's other inputs and may leave an Unknown send outcome after dispatch. AWS collects partial operation batches for 2 ms, flushes acknowledgements once the observed receive capacity is filled, and uses up to four overlapping receives with a shared consumer capacity budget. Provider authors can advertise MaxReceiveBatchSize, MaxConcurrentReceives and ReceiveBatchDelay in TransportCapabilities; other providers default to one receive and no coalescing delay. Settled-handler cleanup is separately bounded and drained on shutdown. The versioned fnd.envelope AWS attribute retains readable bodies and all headers. NativeMessageHeaders optionally duplicates up to nine selected headers for SNS filters (empty by default); reserved/invalid names fail at construction and the native-name list is snapshotted; new readers accept legacy envelopes, but old experimental readers cannot read new sends.
- AddFoundatioWorker registers the foundatio health check and Foundatio.Runtime capacity gauges; subscriptions and infrastructure recovery affect health. Malformed AWS envelopes retain raw evidence and are quarantined per entry. Unmatched types back off five seconds with jitter instead of hot-looping.

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

### Messaging

The verb carries the delivery semantic; handlers never choose queue vs. topic:

```csharp
// Command: exactly one handler instance across the fleet processes it.
await _bus.SendAsync(new ResizeImage(imageId));

// Event: every subscribing service receives one copy.
await _bus.PublishAsync(new OrderSubmitted(orderId));
```

```csharp
public class SendConfirmationHandler : IMessageHandler<OrderSubmitted>
{
    public Task HandleAsync(IMessageContext<OrderSubmitted> context, CancellationToken cancellationToken)
        => _email.SendConfirmationAsync(context.Message.OrderId, cancellationToken);
}

services.AddFoundatio()
    .Messaging.AddConsumer<OrderSubmitted, SendConfirmationHandler>(o =>
    {
        o.MaxConcurrency = 4;                 // default 1; retries may still reorder work
        o.DeadLetterOn<ValidationException>(); // retries cannot fix validation failures
    });
```

Throwing from `HandleAsync` triggers the core retry/dead-letter policy. With `AckMode.Manual`, settle explicitly:

```csharp
await context.CompleteAsync();
await context.RejectAsync(new RejectOptions { RedeliveryDelay = TimeSpan.FromSeconds(30) });
await context.RejectAsync(new RejectOptions { Terminal = true, Reason = "malformed" });
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
// TryAcquireAsync returns null when the lock is unavailable; AcquireAsync throws instead.
await using var lck = await _locker.TryAcquireAsync(
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

### Durable Job

Implement `IJob`; enqueue through `IJobClient`. Arguments are typed and persisted with the job:

```csharp
public class RebuildSearchIndexJob : IJob<RebuildSearchIndexArgs>
{
    public async Task<JobResult> RunAsync(RebuildSearchIndexArgs args, JobExecutionContext context)
    {

        await context.ReportProgressAsync(10, "starting");
        foreach (var batch in GetBatches(args.Index))
        {
            if (await context.IsCancellationRequestedAsync())
                return JobResult.Cancelled;

            await IndexBatchAsync(batch, context.CancellationToken);
        }

        return JobResult.Success;
    }
}

JobHandle handle = await _jobs.EnqueueAsync<RebuildSearchIndexJob, RebuildSearchIndexArgs>(
    new RebuildSearchIndexArgs { Index = "orders" });
JobState? state = await handle.GetStateAsync();
await handle.RequestCancellationAsync();
```

Workers claim only registered job types, with a fresh ownership token and a DI scope per run. Leases are supervised; stale tokens cannot mutate a replacement execution. Host interruption returns work to the queue; explicit cancellation is terminal. Persisted MaxAttempts defaults to three; failures use bounded exponential backoff and end in Failed when exhausted. Execution is at least once: protect external side effects with application idempotency.

`IJobMonitor.QueryAsync` returns a bounded `JobPage` ordered by ID; pass ContinuationToken as JobQuery.AfterJobId until null, including after empty filtered pages. Hosted workers clean terminal history older than seven days; manual hosts call CleanupAsync. JobRuntimeStoreOptions separates 100,000 active jobs, 100,000 history records, 1,000,000 deduplication reservations, and 100,000 scheduled messages. History/deduplication default to seven days; history eviction preserves the separate ID reservation. Payload limit defaults to 1 MiB (scheduled messages include UTF-8 header keys/values). GetStatsAsync reports usage. Configure RedisJobRuntimeStoreOptions.Runtime or Jobs.UseInMemory(options).

### CRON Job

```csharp
services.AddFoundatio()
    .Jobs.UseInMemory()
    .AddCronJob<NightlyExportJob, ExportArgs>("0 2 * * *", new ExportArgs { Format = "csv" }, o =>
    {
        o.Scope = ScheduledJobScope.Global;   // one instance per tick (default); PerNode = every instance
        o.MaxAttempts = 3;                    // TOTAL run attempts per failed occurrence
        o.ConfigurationVersion = 1;
    });
```

Start `services.AddJobScheduler()` to reconcile definitions and materialize due occurrences; start `services.AddJobWorker()` to execute them. Registering the store starts neither.

### Migrating old jobs

`JobBase`/`QueueJobBase<T>`/`JobWithLockBase`/`JobRunner`/`WorkItemJob` and the hosted `AddJob`/`AddDistributedCronJob` infrastructure were removed. The mappings are mechanical: an old job's `RunAsync(CancellationToken)` becomes `RunAsync(JobExecutionContext)` (use `context.CancellationToken`; `JobResult` is unchanged); a `QueueJobBase<T>` becomes an `IMessageHandler<T>` fed by `SendAsync`; a `WorkItemJob` handler becomes a job enqueued with `EnqueueAsync<TJob, TArgs>(args)` reporting progress via `context.ReportProgressAsync`; distributed CRON is `.Jobs.AddCronJob<TJob>(cron)` on the durable runtime.

## Testing

### Messaging: Foundatio.Testing harness

`Foundatio.Testing` runs the real `IMessageBus` over a recording in-memory transport -- deterministic tests without sleeps, including the retry/dead-letter path:

```csharp
services.AddFoundatio()
    .Messaging.UseTestHarness()
    .AddSubscriber<OrderPlaced, SendConfirmationHandler>("confirmation");
services.AddMessageConsumers();

// resolve MessagingTestHarness from the container; start hosted services, then:
await bus.PublishAsync(new OrderPlaced(42));
await harness.WaitForIdleAsync();  // blocks until queues and in-flight handlers drain

Assert.Single(harness.Published<OrderPlaced>());
Assert.Single(harness.Handled<OrderPlaced>());
Assert.Empty(harness.DeadLetteredMessages);
```

Recordings: `SentMessages` / `PublishedMessages` / `HandledMessages` / `AbandonedMessages` (retries) / `DeadLetteredMessages`, with typed accessors `Sent<T>()`, `Published<T>()`, `Handled<T>()`, `Abandoned<T>()`, `DeadLettered<T>()`. To await one outcome without draining the whole bus: `WaitForHandledAsync<T>(count)` (returns the handled messages) and `WaitForDeadLetteredAsync<T>(count)` (returns raw `RecordedMessage`s -- assert `Reason`/`Attempts`). `DestinationsWithNoConsumer` lists destinations that received messages nothing consumed -- the usual reason a test is "idle immediately and Handled is empty".

The harness polls in REAL time (25ms cadence) regardless of any injected `TimeProvider`, while delayed redeliveries execute on the injected `TimeProvider` -- a faked clock must be advanced manually or waits time out. For sleep-free retry tests prefer `RedeliveryBackoff = _ => TimeSpan.Zero` on the subscription instead of faking the clock.

### Jobs: JobsTestHarness

`.Jobs.UseTestHarness()` registers `JobsTestHarness`: the real in-memory job runtime without hosted workers, so the test decides exactly when work runs.

```csharp
services.AddFoundatio().Jobs.UseTestHarness();
var harness = provider.GetRequiredService<JobsTestHarness>();

var handle = await harness.Client.EnqueueAsync<SendWelcomeEmailJob>();
await harness.RunAllQueuedAsync();                 // drains currently eligible jobs across batches; future retries remain queued
await harness.RunDueAsync(fixedNow);               // materializes due CRON occurrences, then drains eligible jobs
var state = await harness.RunToCompletionAsync(handle); // drives one job to its terminal state
```

`Client` (`IJobClient`), `Schedules` (`IScheduledJobManager`), and `Monitor` (`IJobMonitor`) expose the enqueue/manage/assert surface. For running an `IJob` directly without any runtime, `new JobExecutionContext(cancellationToken, arguments: myArgs)` builds a detached context -- progress/lease helpers no-op and `GetArguments<T>()` returns the supplied object.

### Test logging via Foundatio.Xunit.v3

Two base classes:

- **`TestWithLoggingBase`** -- lightweight, no DI container. `_logger` (`ILogger`) for logging; `Log` (`ILoggerFactory`) for passing to Foundatio services.
- **`TestLoggerBase`** -- full DI via `TestLoggerFixture`. Override `ConfigureServices` to register services. `Log` (`ILogger`) for logging; `TestLogger` (`ILoggerFactory`) for passing to Foundatio services.

### Custom providers

The transport contract is documented on the interfaces themselves (`IMessageTransport` + `ISupports*`): settle semantics (stale receipts SHOULD throw `ReceiptExpiredException`, but the signal is best-effort), the per-delivery `Receipt` token (never settle by entry identity alone), and the growth rule that contract changes only ever add optional init members.

Validate a custom transport or job store against the shared conformance suites in `Foundatio.TestHarness`: inherit `MessageTransportConformanceTests` (override `CreateTransport`) and `JobRuntimeStoreConformanceTests` (override `CreateStore`). Tests skip automatically for unimplemented optional interfaces or unavailable backends. The suites pin per-message ids (distinct, positionally aligned in batch results), content-type round-trip, and non-destructive dead-letter inspection with explicit deletion/replay.

## Gotchas

- **Redis lifetime**: UseRedis registers a shared container-owned multiplexer by default. Custom connections should use a singleton factory so DI disposes them after hosted work stops. An already-created singleton instance stays caller-owned; dispose the host before disposing that connection.
- **Shared Redis connection**: messaging and jobs share one multiplexer. Configure ConnectionStrings:Redis, provide one explicit UseRedis connection string, or register the multiplexer. Conflicting explicit strings fail at registration; omit connectionString when using an existing multiplexer.

- **Explicit receiving intent**: `AddConsumer` registers queued work; `AddSubscriber(..., "stable-group")` registers a durable event subscription. Replicas in the same group compete. DI AddSubscriber defaults to UseServiceName or IHostEnvironment.ApplicationName; set an explicit nonblank name to override; use AddTemporarySubscriber explicitly for temporary listeners. Dynamic unnamed subscriptions require expiring-subscription support (in-memory/Redis); AWS requires a durable name.
- **Do not configure broker redrive policies**: the core owns retry/dead-lettering (SQS `maxReceiveCount`, DLX, etc. would split authority and make behavior transport-specific).
- **Hosting is explicit**: AddFoundatioWorker(configure, jobConcurrency: 1) hosts the roles selected by its callback. Plain AddFoundatio client/storage registrations start no services. For split deployments, add `AddMessageConsumers`, `AddJobWorker(concurrency)`, `AddJobScheduler`, and/or `AddScheduledMessageDispatcher` only where each role should run. Workers, schedulers, and dispatchers are independent. `AddMessagingTopology` is available for producer-only startup checks.
- **Delayed sends beyond transport ceilings need a runtime store**: e.g. > 15 min on SQS, or any delayed publish on SNS topics. Without a store the operation fails loudly rather than truncating the delay.
- **`WaitForIdleAsync` ignores store-parked work**: delayed sends/retries parked in the runtime store are not transport activity -- drain them via ScheduledMessageDispatcher before asserting.
- **Lock returns null**: `TryAcquireAsync` returns `null` when the lock cannot be acquired -- always guard with `is not null`. `AcquireAsync` throws `LockAcquisitionTimeoutException` instead of returning null.
- **Dispose streams and locks**: `ILock` is `IAsyncDisposable` -- use `await using`. Streams from `GetFileStreamAsync` are `IDisposable` -- use `using var`.
- **Cache `GetAsync` returns `CacheValue<T>`**: check `result.HasValue` before `result.Value`. A missing key returns `HasValue = false`, not an exception.
- **Cache stampede**: serialize regeneration of hot keys with `CacheLockProvider` (lock on the cache key, double-check after acquiring). See the [Cache Stampede Protection](https://foundatio.readthedocs.io/guide/caching.html#cache-stampede-protection) docs.
- **Register as singletons**: infrastructure services (`ICacheClient`, `IMessageBus`, `IFileStorage`, `ILockProvider`) maintain internal state and connections; the `AddFoundatio()` builder does this for you.
- **In-memory for tests**: in-memory implementations run the same applicable conformance suites for fast, isolated tests. Their state is process-local, and optional provider capabilities differ.
- **In-memory visibility timing**: one shared timer reclaims expired deliveries at 50 ms intervals while messages are in flight, and pauses when idle. With a fake TimeProvider, advance past the lease expiry to wake blocked receivers; lock renewal uses the current lease, and completion does not retain one timer per delivery.
- **Legacy name collision during migration**: with `AddLegacyAdapter()`, `Foundatio.Messaging.Legacy.IMessageBus` and `Foundatio.Messaging.IMessageBus` coexist. Disambiguate with a `using` alias in files that reference both namespaces.

## NuGet Packages

### Core

| Package | Provides |
| ------- | -------- |
| `Foundatio` | Core interfaces, in-memory implementations, messaging + durable job runtime, resilience, `SystemTextJsonSerializer` |
| `Foundatio.Extensions.Hosting` | Explicit message consumers, workers, schedulers, dispatchers, startup actions |

### Serializers

`ITextSerializer` extends `ISerializer` for human-readable formats (JSON). `ISerializer` covers binary formats. Default is `SystemTextJsonSerializer` (included in core).

`IBufferSerializer` is optional: byte-array/memory extensions use it automatically, while stream-only serializers retain the existing fallback. The default JSON serializer supports it with identical options and primitive normalization. Implementations return owned output and never retain or modify input memory; callers need no configuration changes.

| Package | Provides |
| ------- | -------- |
| `Foundatio.JsonNet` | `JsonNetSerializer` : `ITextSerializer` (Newtonsoft.Json) |
| `Foundatio.MessagePack` | `MessagePackSerializer` : `ISerializer` (binary, high-throughput) |
| `Foundatio.Utf8Json` | `Utf8JsonSerializer` : `ITextSerializer` (fast JSON) |

### Providers

| Package | Provides |
| ------- | -------- |
| `Foundatio.Redis` | This revision: Redis Streams messaging and durable jobs. Earlier external packages also supply legacy Redis abstractions; check API compatibility before mixing versions. |
| `Foundatio.Aws` | `AwsMessageTransport` (SQS queues, SNS+SQS pub/sub), S3 storage |
| `Foundatio.AzureStorage` | Azure Blob storage, Azure Storage queues |
| `Foundatio.AzureServiceBus` | Azure Service Bus queues + messaging |
| `Foundatio.Kafka` | Kafka messaging |
| `Foundatio.RabbitMQ` | RabbitMQ messaging |
| `Foundatio.Minio` | MinIO S3-compatible storage |
| `Foundatio.Aliyun` | Aliyun OSS storage |
| `Foundatio.Storage.SshNet` | SFTP storage |

### Testing & Other

| Package | Provides |
| ------- | -------- |
| `Foundatio.Testing` | `MessagingTestHarness`, `JobsTestHarness`, and `UseTestHarness()` for explicit test-driven execution |
| `Foundatio.TestHarness` | Conformance suites (`MessageTransportConformanceTests`, `JobRuntimeStoreConformanceTests`) for custom providers |
| `Foundatio.Xunit` | xUnit v2 test logging, retry attributes |
| `Foundatio.Xunit.v3` | xUnit v3 test logging, retry attributes |
| `Foundatio.DataProtection` | ASP.NET Core Data Protection key storage via `IFileStorage` |

## Broker execution integration

- Optional broker execution history uses `IMessageExecutionStore` with `.Messaging.UseInMemoryExecutionTracking()` or `.UseRedisExecutionTracking()`. `MessageExecutionPipeline` and native `MessageProcessingContext` support progress, cancellation, and attempt-fenced state. Do not enqueue the same delivery through `IJobRuntimeStore`; the bus owns receiving and settlement.
- `ConsumeWithOutcomeAsync` handles returned Success/Retry/DeadLetter/Unsettled outcomes. Endpoint receive capacity, visibility, automatic renewal, and graceful drain are configured with MessageHandlerOptions.
- `SubscribeNodeAsync` supplies independent best-effort node broadcasts. AWS uses managed tagged resources and startup stale cleanup, not native TTL. Acknowledge-before-callback intentionally allows lost notifications.
- Native `MessageAdministration` owns bounded dead-letter inspection/replay. Send-before-delete is at least once, not atomic. Optional replay preparation can create fresh tracked execution IDs.
- `.Locking.UseRedis()` uses native RedisLockProvider with ownership-checked renewal/release. It coordinates live resource ownership, not persistent duplicate detection.
