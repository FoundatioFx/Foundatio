---
name: foundatio
description: >
  Use when working with Foundatio infrastructure abstractions for .NET -- caching,
  queuing, messaging, file storage, distributed locking, or background jobs. Apply
  when using ICacheClient, IQueue, IMessageBus, IFileStorage, ILockProvider, IJob,
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

RabbitMQ configuration, delivery safety, and contributor verification live in this repository's [provider guides](../../../docs/guide/implementations/rabbitmq.md), not a second documentation tree in Foundatio.RabbitMQ. When working from a companion documentation branch, check its implementation PR and package availability instead of treating staged APIs as released.

## Core Interfaces

| Interface | Purpose | In-Memory | Production |
| --------- | ------- | --------- | ---------- |
| `ICacheClient` | Key-value caching with TTL | `InMemoryCacheClient` | Redis, Hybrid |
| `IQueue<T>` | FIFO message queuing | `InMemoryQueue<T>` | Redis, SQS, Azure |
| `IMessageBus` | Pub/sub messaging | `InMemoryMessageBus` | Redis, Kafka, RabbitMQ, Azure |
| `IFileStorage` | File storage abstraction | `InMemoryFileStorage` | S3, Azure Blob, Minio |
| `ILockProvider` | Distributed locking | `CacheLockProvider` | Redis-backed |
| `IJob` | Background job processing | N/A | Hosted services |
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

- **RabbitMQ contracts are versioned**: The [4.2.5 delivery guide](../../../docs/guide/implementations/rabbitmq-delivery-safety.md) accompanies runtime PR Foundatio.RabbitMQ#105 and the linked provider review stack. Check package availability and land the provider dependencies before publishing these contracts. Strict dispatch is opt-in and requires Automatic acknowledgement, a nonempty typed terminal exchange, and no discard. Automatic exhaustion without a destination now retains by default: a breaking change with backlog/backpressure consequences. Default FireAndForget is unchanged.
- **RabbitMQ topology and retries**: Specify the actual queue type and a stable subscription queue. A normal policy cannot convert queue type. Both classic and quorum support strict processing and provider-confirmed terminal handoff; only quorum provides replication and at-least-once broker DLX. Handoff and source ACK are not atomic; preserve consumer-scoped identity. Use finite prefetch and bounded source/quarantine reject-publish policies with confirms; a full classic source can block retry. Classic retry needs default-exchange write permission. Quorum broker limits need a separate safe finite DLX policy.
- **RabbitMQ versions and scheduling**: The test baseline stays 4.2.5. Classic priorities use `x-max-priority`; quorum uses normal/high tiers on 4.2 and 32 built-in strict levels on 4.3+. `UseDelayedRetries()` and `ConsumerTimeout()` require 4.3+ quorum queues. Native retries apply to returned deliveries, not initial scheduling. The archived delayed-exchange plugin is unavailable on 4.3+; `DeliveryDelay` falls back to process memory unless `RequireBrokerDelayedDelivery` rejects it. Replacement publisher channels recheck scheduling capability and redeclare the topic. See the [version matrix](../../../docs/guide/implementations/rabbitmq.md#broker-baseline-and-priority).
- **RabbitMQ endpoint utility**: `RabbitMQEndpointResolver` preserves client-certificate settings from the supplied factory while forcing strict server identity/trust. Custom server-validation callbacks are rejected. Parsed ports outside 1–65535 throw `ArgumentOutOfRangeException`; malformed hosts or unparseable port text throw `ArgumentException`. This utility does not add client-certificate options to `RabbitMQMessageBusOptions`; configuration preservation is not live mutual-TLS proof.
- **RabbitMQ test ownership**: Keep provider tests on their Foundatio bases and shared Aspire collection; TLS runs in the normal Build and is required in CI. Optional local trust-store denial skips only TLS-dependent cases, not other brokers. Keep test methods alphabetical with Arrange/Act/Assert sections. Do not add independent verification workflows or silently replace the pinned 4.2.5 broker. See the [verification guide](../../../docs/guide/implementations/rabbitmq-verification.md).
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
- **In-memory for tests**: All in-memory implementations are functionally equivalent to production providers. Swap via DI for fast, isolated unit tests with no external dependencies.

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
| `Foundatio.Minio` | MinIO S3-compatible storage |
| `Foundatio.Aliyun` | Aliyun OSS storage |
| `Foundatio.Storage.SshNet` | SFTP storage |

### Testing & Other

| Package | Provides |
| ------- | -------- |
| `Foundatio.TestHarness` | Shared test base classes for validating custom implementations |
| `Foundatio.Xunit` | xUnit v2 test logging, retry attributes |
| `Foundatio.Xunit.v3` | xUnit v3 test logging, retry attributes |
| `Foundatio.DataProtection` | ASP.NET Core Data Protection key storage via `IFileStorage` |
