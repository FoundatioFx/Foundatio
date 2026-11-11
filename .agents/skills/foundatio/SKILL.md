---
name: foundatio
description: >
  Use when working with Foundatio infrastructure abstractions for .NET -- caching,
  queuing, messaging, file storage, distributed locking, or background jobs. Apply
  when using ICacheClient, IQueue, IMessageBus, IMessageClient, IMessageHandler, IJobClient,
  IJob, IMessageTransport, IJobStore, IFileStorage, ILockProvider,
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
| `IJobClient` | Durable jobs (`IJob<TArgs>`, `IJob`): progress, cancel, retry, CRON | `UseInMemory()` (`InMemoryJobStore`) | Provider `IJobStore`s |
| `IJob` | Background job processing | N/A | Hosted services, or durable via `AddFoundatioJobs` |
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
    .UseInMemory()                          // provider: m.UseRedis(...) etc.
    .AddConsumer<SendReceiptHandler>()      // commands: SendAsync -> exactly one consumer
    .AddSubscriber<OrderPlacedHandler>()    // events: PublishAsync -> one copy per service
    .AddSubscriber<CacheBuster>(new SubscriptionOptions { Scope = SubscriptionScope.Instance }));

public class SendReceiptHandler(IEmail email) : IMessageHandler<SendReceipt>
{
    public Task HandleAsync(SendReceipt m, IMessageContext context, CancellationToken ct) => email.SendAsync(m, ct);
}

builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()
    .AddJob<GenerateReportJob>()            // IJob<GenerateReport>; enqueue by args: jobs.EnqueueAsync(new GenerateReport(...))
    .AddCronJob<CleanupJob>("0 3 * * *"));  // existing IJob/JobBase classes work; one run per occurrence cluster-wide
```

- Registered handlers/jobs start with the host; `AddFoundatio*` calls accumulate. No handlers registered = send/enqueue only.
- Throw to retry (`RetryPolicy`, default 5 attempts, 1s/4s/16s/64s), then dead-letter (messages) or `Failed` (jobs). `context.DeadLetter(reason)` / `context.Retry(delay)` decide without throwing; `RetryPolicy.Default.DoNotRetry<T>()` fails fast.
- At-least-once: handlers and jobs must be idempotent. Give contracts stable names: `[Message("orders.placed")]`, `[Job(Name = "reports.generate")]` (defaults are full type names).
- Delegate handlers get scoped services from `context.Services`. Unit test with `Foundatio.Testing.TestMessageContext` / `TestJobContext`.
- `m.AddLegacyMessageBus()` serves the original `IMessageBus` (HybridCache, CacheLockProvider) from the new transport.
- Dead letters: inject `IMessageAdmin` -> `PeekDeadLettersAsync(source)`, `ReplayDeadLettersAsync(source)` (back to that queue/subscription only), `PurgeDeadLettersAsync`, `DeleteAsync` (retired subscriptions). Sources: `MessageSource.Queue(name)` / `MessageSource.Subscription(topic, serviceName)`.
- Gotchas: `[Job(Name)]` for `IJob<TArgs>` goes on the args record (on the job class it throws). Publish delays beyond the transport's `MaxPublishDelay` throw. Oversized messages throw `MessageTooLargeException`. With `GroupKey` on an ordering transport, a retry holds the group's later messages (each counts an attempt).
- Writing a provider: implement `IMessageTransport` (send to queue/topic/one subscription, receive, delete) + `IMessageReceiver` or `IJobStore`, declare `TransportCapabilities` honestly (`MaxDelay`/`MaxPublishDelay`/`MaxRetryDelay`, `MaxMessageSize` after encoding overhead), and derive tests from `MessageTransportTestsBase` / `JobStoreTestsBase` (one override each).

Whole-app integration tests (`Foundatio.Testing`, no xUnit dependency):

```csharp
builder.Services.AddOrdersApp();              // real AddFoundatioMessaging/AddFoundatioJobs registrations
builder.Services.AddFoundatioTestHarness();   // LAST: replaces transport + job store with in-memory, records everything
await host.StartAsync();
var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

await messages.PublishAsync(new OrderPlaced(1));
await harness.WaitForIdleAsync();                                   // handlers idle, no queued/running jobs
Assert.Single(harness.Sent<SendReceipt>());                         // payloads; SentMessages/PublishedMessages for metadata
var dead = await harness.WaitForDeadLetteredAsync<SendReceipt>();   // also WaitForHandledAsync<T>(count)
var run = (await harness.WaitForJobAsync<GenerateReport>())[0];     // finished runs; check run.Status
await harness.TriggerCronJobAsync<CleanupJob>();                    // CRON never fires on its own under the harness
```

- `HandledMessages[i].Outcome`: `Completed`, `Retried`, `DeadLettered`, `Skipped` (no handler on a topic), `Expired`, `Released` (shutdown/lease lost). `JobRuns` includes retried runs (`Status == Queued`).
- Waits are real time (default 30s) and throw `TimeoutException` explaining what happened. Retries honor the policy delay: set `o.DefaultRetryPolicy` to a short `RetryPolicy.Fixed(...)` in tests. `WaitForIdleAsync` ignores queues the app sends to but does not consume.

Health checks (`Foundatio.Extensions.Hosting`):

```csharp
builder.Services.AddHealthChecks()
    .AddFoundatioMessaging(tags: ["live"])                          // per-source data: queue:x, subscription:t/s
    .AddFoundatioJobs(tags: ["live"], unhealthyAfter: TimeSpan.FromMinutes(5)); // worker (running per job name) + scheduler
```

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
- **JobContext.RenewLockAsync**: Call in long-running jobs (both `JobBase` and `QueueJobBase`) to prevent lock expiration mid-processing.
- **Register as singletons**: All infrastructure services (`ICacheClient`, `IMessageBus`, `IQueue<T>`, `IFileStorage`, `ILockProvider`) maintain internal state and connections -- always register as singletons.
- **CacheLockProvider + IMessageBus**: `IMessageBus` is optional but recommended. Without it, lock release falls back to polling. With it, locks are released instantly via pub/sub notification.
- **In-memory for tests**: In-memory implementations follow the same interfaces as production providers, but some behaviors differ (delivery delay limits, ordering, deduplication, group ids). Swap via DI for fast, isolated unit tests, and check [provider behavioral gaps](https://foundatio.dev/guide/provider-behavioral-gaps) before relying on provider-specific behavior.
- **Queue `GroupId` is a hint**: It only changes delivery on SQS (fair queues on standard queues, strict ordering on `.fifo` queues) and session-enabled Azure Service Bus queues (ordered per group, one message at a time per group; `RetryDelay` is not applied, so an abandoned message retries ahead of the rest of its group; a group id of at most 128 characters is required). The other built-in queues store it as metadata and log one debug message. SQS group ids are max 128 characters with no spaces. Empty values are stored as `null`. A non-empty explicit `QueueEntryOptions.GroupId` overrides the queue's `GroupId(...)` resolver; null or empty invokes the resolver. Resolvers should return `null` for no group.
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
| `Foundatio.AzureStorage` | Azure Blob storage, Azure Storage queues |
| `Foundatio.AzureServiceBus` | Azure Service Bus queues + messaging |
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
