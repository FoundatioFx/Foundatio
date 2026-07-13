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

- One messaging client: `IMessageBus` in `Foundatio.Messaging`. The caller's verb decides delivery -- `SendAsync` is a command processed by exactly one handler instance across the fleet (competing consumers); `PublishAsync` is an event received once per subscribing service (a scaled service's instances compete), or by every instance when the subscription sets `PerInstance`. `SendBatchAsync` / `PublishBatchAsync` batch both verbs. Per-operation options: `MessageSendOptions` / `MessagePublishOptions` (priority, `Delay`/`DeliverAt`, TTL, correlation id, headers, `Destination`/`Topic` override).
- Handlers are topology-free. Implement `IMessageHandler<T>` and register with `.Messaging.AddHandler<TMessage, THandler>(o => ...)`; a hosted service (`MessageHandlerHostedService`) starts them all and each message is dispatched in its own DI scope. `IMessageBus.SubscribeAsync<T>` is the dynamic path and returns an `IMessageSubscription` handle.
- `MessageSubscriptionOptions` declares delivery intent: `Deliveries` (`MessageDeliveries.Sent`/`Published`/`Both`, default `Both`), `Subscription` / `SubscriptionQualifier` / `PerInstance` for subscriber-group identity, `MaxConcurrency` (default 1, preserves per-handler ordering), `MaxAttempts` / `RedeliveryBackoff` / `DeadLetterWhen` (+ `DeadLetterOn<TException>()` shorthand) retry overrides, `AckMode` (`Auto` default / `Manual`), and `Key` (subscriptions sharing a key form one competing group; their backoff/dead-letter DELEGATES are compared by identity, so share delegate instances).
- Routing is central: `.Messaging.ConfigureRouting(r => r.UseDefaultQueue(...).UseDefaultTopic(...).MapQueue<T>(...).MapTopic(...).UseServiceIdentity(...).UseSubscriptionIdentity(...).UseConvention(...))`. Precedence: operation override > exact map > interface/base-type map > `MessageRouteAttribute` > configured default > convention > kebab-cased type name.
- Routing config doubles as topology declarations (`DestinationDeclaration` with a canonical `DestinationAddress` -- `ForQueue`/`ForTopic`/`ForSubscription`). `IMessageTopology` exposes `GetDeclarations()` / `EnsureAsync()` / `ValidateAsync()`. `.Messaging.ConfigureTopology(TopologyMode.Ensure | Validate | None)` picks whether the client creates missing destinations (default), only verifies they exist (throws at startup when missing), or never touches topology; the handler host applies the mode at startup.
- The CORE owns retry/dead-lettering identically on every transport: default `RetryPolicy` is `MaxAttempts` 5 with immediate-then-10s/20s/30s backoff (+/-20% jitter); configure via `.Messaging.ConfigureRetry(p => p with { ... })`. Dead-lettered messages go to the transport's native sink or a derived `"{source}.deadletter"` destination, stamped with `message.dead_letter.*` forensics headers (`KnownHeaders.DeadLetter*`). Never configure broker-native redrive policies.
- Message settlement: `IMessageContext` / `IMessageContext<T>` with `CompleteAsync()`, `RejectAsync(RejectOptions)` (non-terminal = retry, optionally with `RedeliveryDelay`; `Terminal = true` = dead-letter with `Reason`/`Exception`), and `RenewLockAsync()`. Auto-ack is the default.
- Transports advertise role-aware capabilities: `ITransportInfo.GetCapabilities(DestinationRole)` returns `TransportCapabilities` (e.g. the AWS transport's queue role has a native 15-minute `MaxDeliveryDelay`; its topic role has none). Delays beyond a ceiling and store-parked retries fall back to the durable runtime store (`IScheduledDispatchStore`, satisfied by any `IJobRuntimeStore`) and are drained by the job runtime pump -- never silently truncated.
- Durable jobs: implement `IJob` (`Task<JobResult> RunAsync(JobExecutionContext context)`). `IJobClient.EnqueueAsync<TJob>()` / `EnqueueAsync<TJob, TArgs>(args)` (typed payloads) returns a `JobHandle`; `IJobMonitor` queries state; `IJobWorker` executes with per-run DI scopes, bounded concurrency, and supervised lease renewal. `JobExecutionContext` gives `JobId`/`Attempt`/`CancellationToken`, `GetArguments<TArgs>()`, `ReportProgressAsync`, `RenewLeaseAsync`, `IsCancellationRequestedAsync`; its public constructor makes a detached context for tests.
- CRON: `.Jobs.AddCronJob<TJob>("0 */6 * * *", o => ...)` with `CronJobOptions` (`Scope` Global/PerNode, `Overlap`, `MisfireWindow`, `MaxRetries`, `TimeZone`, typed `Arguments`). Scheduled automatically when the runtime pump starts. Tune the pump with `.Jobs.ConfigureRuntimePump(o => ...)` (`JobRuntimePumpOptions`: `Enabled`, `PollInterval`, `BatchSize`, `MaxJobAttempts`, `WorkerConcurrency`).
- Runtime schedule management: `IScheduledJobManager` (DI-registered with the runtime) lists/inspects schedules, adds or replaces `ScheduledJobDefinition`s on the fly, `RescheduleAsync(name, cron)` changes just the schedule, `SetEnabledAsync(name, bool)` pauses/resumes materialization, and `TriggerAsync(name)` runs an immediate durable occurrence (definition's `Arguments` + retry budget) returning a `JobHandle`. Triggering a disabled schedule throws; manual occurrences never dedupe and bypass `Overlap` accounting. Generic overloads (`GetScheduleAsync<TJob>()`, `TriggerAsync<TJob>()`, `RescheduleAsync<TJob>(cron)`, `SetEnabledAsync<TJob>(bool)`, `UnscheduleAsync<TJob>()`) resolve the schedule name via `ScheduledJobDefinition.DefaultNameFor(type)` — the same default `AddCronJob<TJob>` uses when no explicit name is given.
- Stable wire names: `.Messaging.AddMessageType<T>("name")` and `.Jobs.AddJobType<TJob>("name")` so persisted discriminators survive assembly/namespace moves; unregistered types fall back to `Type.FullName`.
- Legacy implementations were removed. For migration, `Messaging.AddLegacyAdapter()` registers the old `IMessageBus`/`IMessagePublisher`/`IMessageSubscriber` interfaces as a thin adapter over the new bus (old handler code compiles unchanged; delete the call when migrated). Old jobs migrate mechanically: `RunAsync(CancellationToken)` becomes `RunAsync(JobExecutionContext)` (use `context.CancellationToken`), `QueueJobBase<T>`/`IQueue<T>` become `IMessageHandler<T>` + `SendAsync`, and `WorkItemJob` becomes `EnqueueAsync<TJob, TArgs>(args)` with `ReportProgressAsync`.

## Core Interfaces

| Interface | Purpose | In-Memory | Production |
| --------- | ------- | --------- | ---------- |
| `ICacheClient` | Key-value caching with TTL | `InMemoryCacheClient` | Redis, Hybrid |
| `IMessageBus` | Commands (`SendAsync`) + events (`PublishAsync`) over one client | `InMemoryMessageTransport` | Redis Streams, AWS SQS/SNS |
| `IJobClient` / `IJobMonitor` | Submit and observe durable background jobs | `InMemoryJobRuntimeStore` | `RedisJobRuntimeStore` |
| `IQueue<T>` | Work-item queue (classic API) | `InMemoryQueue<T>` | Redis, SQS, Azure |
| `IFileStorage` | File storage abstraction | `InMemoryFileStorage` | S3, Azure Blob, Minio |
| `ILockProvider` | Distributed locking | `CacheLockProvider` | Redis-backed |
| `ISerializer` / `ITextSerializer` | Binary and text serialization | `SystemTextJsonSerializer` | MessagePack, JsonNet |
| `IResiliencePolicy` | Retry, circuit breaker, timeout | `ResiliencePolicyBuilder` | N/A |

## DI Registration

Use the `AddFoundatio()` fluent builder; infrastructure services register as **singletons**. Handlers and jobs resolve in their own DI scope per message/run, so they can inject scoped dependencies.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoundatio()
    .Caching.UseInMemory()
    .Storage.UseFolder("data")
    .Locking.UseCache()
    .Messaging
        .ConfigureRouting(r => r
            .UseServiceIdentity("billing")
            .MapQueue<OrderSubmitted>("orders")
            .MapTopic("order-events", typeof(IOrderEvent)))
        .ConfigureRetry(p => p with { MaxAttempts = 5 })
        .UseInMemory()
    .Messaging.AddHandler<OrderSubmitted, SendConfirmationHandler>()
    .Jobs.UseInMemory()
    .Jobs.AddJobType<RebuildSearchIndexJob>("search.rebuild");
```

Swap to production by changing only the provider lines:

```csharp
builder.Services.AddFoundatio()
    .Messaging.UseRedis(connectionString: "localhost:6379")   // Redis Streams transport
    .Jobs.UseRedis();                                         // Redis job runtime store

// or AWS (SQS queues, SNS+SQS pub/sub; point ServiceUrl at LocalStack for local dev)
builder.Services.AddFoundatio()
    .Messaging.UseAws(o => o.ResourcePrefix = "myapp");
```

Custom providers plug in via `.Messaging.UseTransport(...)` (any `IMessageTransport`) and `.Jobs.UseRuntimeStore(...)` (any `IJobRuntimeStore`).

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
    .Messaging.AddHandler<OrderSubmitted, SendConfirmationHandler>(o =>
    {
        o.MaxConcurrency = 4;                 // default 1 preserves per-handler ordering
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
public class RebuildSearchIndexJob : IJob
{
    public async Task<JobResult> RunAsync(JobExecutionContext context)
    {
        var args = context.GetArguments<RebuildSearchIndexArgs>();

        await context.ReportProgressAsync(10, "starting");
        foreach (var batch in GetBatches(args.Index))
        {
            if (await context.IsCancellationRequestedAsync())
                return JobResult.Cancelled;

            await IndexBatchAsync(batch, context.CancellationToken);
            await context.RenewLeaseAsync(); // heartbeat for long runs
        }

        return JobResult.Success;
    }
}

JobHandle handle = await _jobs.EnqueueAsync<RebuildSearchIndexJob, RebuildSearchIndexArgs>(
    new RebuildSearchIndexArgs { Index = "orders" });
JobState? state = await handle.GetStateAsync();
await handle.RequestCancellationAsync();
```

The worker gives every run its own DI scope, claims jobs with compare-and-set transitions (no double-runs), and supervises the lease: a run is cancelled when the lease is lost to another node or renewal keeps failing past the lease window. Crashed runs are reclaimed and retried until the attempt budget (`JobRuntimePumpOptions.MaxJobAttempts`, default 3) is exhausted, then dead-lettered.

### CRON Job

```csharp
services.AddFoundatio()
    .Jobs.UseInMemory()
    .Jobs.AddCronJob<NightlyExportJob>("0 2 * * *", o =>
    {
        o.Scope = ScheduledJobScope.Global;   // one instance per tick (default); PerNode = every instance
        o.MaxRetries = 3;
        o.Arguments = new ExportArgs { Format = "csv" };
    });
```

Occurrences are materialized durably through the runtime store (deduplicated across nodes and misfire windows) and executed by the auto-registered `JobRuntimePumpService`.

### Migrating old jobs

`JobBase`/`QueueJobBase<T>`/`JobWithLockBase`/`JobRunner`/`WorkItemJob` and the hosted `AddJob`/`AddDistributedCronJob` infrastructure were removed. The mappings are mechanical: an old job's `RunAsync(CancellationToken)` becomes `RunAsync(JobExecutionContext)` (use `context.CancellationToken`; `JobResult` is unchanged); a `QueueJobBase<T>` becomes an `IMessageHandler<T>` fed by `SendAsync`; a `WorkItemJob` handler becomes a job enqueued with `EnqueueAsync<TJob, TArgs>(args)` reporting progress via `context.ReportProgressAsync`; distributed CRON is `.Jobs.AddCronJob<TJob>(cron)` on the durable runtime.

## Testing

### Messaging: Foundatio.Testing harness

`Foundatio.Testing` runs the real `IMessageBus` over a recording in-memory transport -- deterministic tests without sleeps, including the retry/dead-letter path:

```csharp
services.AddFoundatio()
    .Messaging.UseTestHarness()
    .Messaging.AddHandler<OrderPlaced, SendConfirmationHandler>();

// resolve MessagingTestHarness from the container; start hosted services, then:
await bus.PublishAsync(new OrderPlaced(42));
await harness.WaitForIdleAsync();  // blocks until queues and in-flight handlers drain

Assert.Single(harness.Published<OrderPlaced>());
Assert.Single(harness.Handled<OrderPlaced>());
Assert.Empty(harness.DeadLetteredMessages);
```

Recordings: `SentMessages` / `PublishedMessages` / `HandledMessages` / `AbandonedMessages` (retries) / `DeadLetteredMessages`, with typed accessors `Sent<T>()`, `Published<T>()`, `Handled<T>()`, `Abandoned<T>()`, `DeadLettered<T>()`.

For jobs, `new JobExecutionContext(cancellationToken, arguments: myArgs)` builds a detached context to run an `IJob` directly -- progress/lease helpers no-op and `GetArguments<T>()` returns the supplied object.

### Test logging via Foundatio.Xunit.v3

Two base classes:

- **`TestWithLoggingBase`** -- lightweight, no DI container. `_logger` (`ILogger`) for logging; `Log` (`ILoggerFactory`) for passing to Foundatio services.
- **`TestLoggerBase`** -- full DI via `TestLoggerFixture`. Override `ConfigureServices` to register services. `Log` (`ILogger`) for logging; `TestLogger` (`ILoggerFactory`) for passing to Foundatio services.

### Custom providers

Validate a custom transport or job store against the shared conformance suites in `Foundatio.TestHarness`: inherit `MessageTransportConformanceTests` (override `CreateTransport`) and `JobRuntimeStoreConformanceTests` (override `CreateStore`). Tests skip automatically for unimplemented optional interfaces or unavailable backends.

## Gotchas

- **Handlers registered per class get their own event copy**: `AddHandler<TMessage, THandler>` defaults the `SubscriptionQualifier` to the handler type name, so two handler classes on one event type EACH receive every published message. Set an explicit shared `Subscription` only when they should compete.
- **Shared subscription keys compare delegates by identity**: subscriptions sharing a `Key` must pass the SAME `RedeliveryBackoff`/`DeadLetterWhen` delegate instances -- a lambda recreated per subscription is rejected as a conflicting registration.
- **Do not configure broker redrive policies**: the core owns retry/dead-lettering (SQS `maxReceiveCount`, DLX, etc. would split authority and make behavior transport-specific).
- **A runtime store needs its pump**: the DI builder auto-registers `JobRuntimePumpService` with any runtime store, but in a non-hosted process (no generic host) nothing starts it -- drive `JobScheduleProcessor`/`IJobWorker` manually or nothing drains.
- **Delayed sends beyond transport ceilings need a runtime store**: e.g. > 15 min on SQS, or any delayed publish on SNS topics. Without a store the operation fails loudly rather than truncating the delay.
- **`WaitForIdleAsync` ignores store-parked work**: delayed sends/retries parked in the runtime store are not transport activity -- drain them via the job schedule processor before asserting.
- **Lock returns null**: `TryAcquireAsync` returns `null` when the lock cannot be acquired -- always guard with `is not null`. `AcquireAsync` throws `LockAcquisitionTimeoutException` instead of returning null.
- **Dispose streams and locks**: `ILock` is `IAsyncDisposable` -- use `await using`. Streams from `GetFileStreamAsync` are `IDisposable` -- use `using var`.
- **Cache `GetAsync` returns `CacheValue<T>`**: check `result.HasValue` before `result.Value`. A missing key returns `HasValue = false`, not an exception.
- **Cache stampede**: serialize regeneration of hot keys with `CacheLockProvider` (lock on the cache key, double-check after acquiring). See the [Cache Stampede Protection](https://foundatio.readthedocs.io/guide/caching.html#cache-stampede-protection) docs.
- **Register as singletons**: infrastructure services (`ICacheClient`, `IMessageBus`, `IFileStorage`, `ILockProvider`) maintain internal state and connections; the `AddFoundatio()` builder does this for you.
- **In-memory for tests**: in-memory implementations are functionally equivalent to production providers and run the same conformance suites -- swap via DI for fast, isolated tests.
- **Legacy name collision during migration**: with `AddLegacyAdapter()`, `Foundatio.Messaging.Legacy.IMessageBus` and `Foundatio.Messaging.IMessageBus` coexist. Disambiguate with a `using` alias in files that reference both namespaces.

## NuGet Packages

### Core

| Package | Provides |
| ------- | -------- |
| `Foundatio` | Core interfaces, in-memory implementations, messaging + durable job runtime, resilience, `SystemTextJsonSerializer` |
| `Foundatio.Extensions.Hosting` | `AddJobRuntimeService`, startup actions |

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
| `Foundatio.Redis` | `RedisStreamsMessageTransport` (messaging), `RedisJobRuntimeStore` (jobs), plus Redis cache/queue/lock/storage |
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
| `Foundatio.Testing` | `MessagingTestHarness` + `UseTestHarness()` recording transport for deterministic messaging tests |
| `Foundatio.TestHarness` | Conformance suites (`MessageTransportConformanceTests`, `JobRuntimeStoreConformanceTests`) for custom providers |
| `Foundatio.Xunit` | xUnit v2 test logging, retry attributes |
| `Foundatio.Xunit.v3` | xUnit v3 test logging, retry attributes |
| `Foundatio.DataProtection` | ASP.NET Core Data Protection key storage via `IFileStorage` |
