# Queues

Queues offer First In, First Out (FIFO) message delivery with reliable processing semantics. Foundatio provides multiple queue implementations through the `IQueue<T>` interface.

## The IQueue Interface

```csharp
public interface IQueue<T> : IQueue where T : class
{
    AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; }
    AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; }
    AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; }
    AsyncEvent<LockRenewedEventArgs<T>> LockRenewed { get; }
    AsyncEvent<CompletedEventArgs<T>> Completed { get; }
    AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; }

    void AttachBehavior(IQueueBehavior<T> behavior);
    Task<string> EnqueueAsync(T data, QueueEntryOptions options = null);
    Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken);
    Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null);
    Task RenewLockAsync(IQueueEntry<T> queueEntry);
    Task CompleteAsync(IQueueEntry<T> queueEntry);
    Task AbandonAsync(IQueueEntry<T> queueEntry);
    Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default);
    Task StartWorkingAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler,
                           bool autoComplete = false,
                           CancellationToken cancellationToken = default);
}

public interface IQueue : IHaveSerializer, IDisposable
{
    Task<QueueStats> GetQueueStatsAsync();
    Task DeleteQueueAsync();
    string QueueId { get; }
}
```

## Implementations

### InMemoryQueue

An in-memory queue implementation for development and testing:

```csharp
using Foundatio.Queues;

var queue = new InMemoryQueue<WorkItem>();

// Enqueue work
await queue.EnqueueAsync(new WorkItem { Id = 1, Data = "Hello" });

// Dequeue and process
var entry = await queue.DequeueAsync();
Console.WriteLine($"Processing: {entry.Value.Data}");
await entry.CompleteAsync();
```

### RedisQueue

Distributed queue using Redis (separate package):

```csharp
// dotnet add package Foundatio.Redis

using Foundatio.Redis.Queues;

var queue = new RedisQueue<WorkItem>(o => {
    o.ConnectionMultiplexer = redis;
    o.Name = "work-items";
    o.WorkItemTimeout = TimeSpan.FromMinutes(5);
});
```

### AzureServiceBusQueue

Queue using Azure Service Bus (separate package):

```csharp
// dotnet add package Foundatio.AzureServiceBus

using Foundatio.AzureServiceBus.Queues;

var queue = new AzureServiceBusQueue<WorkItem>(o => {
    o.ConnectionString = "...";
    o.Name = "work-items";
});
```

### AzureStorageQueue

Queue using Azure Storage Queues (separate package):

```csharp
// dotnet add package Foundatio.AzureStorage

using Foundatio.AzureStorage.Queues;

var queue = new AzureStorageQueue<WorkItem>(o => {
    o.ConnectionString = "...";
    o.Name = "work-items";
});
```

### SQSQueue

Queue using AWS SQS (separate package):

```csharp
// dotnet add package Foundatio.AWS

using Foundatio.AWS.Queues;

var queue = new SQSQueue<WorkItem>(o => {
    o.Region = RegionEndpoint.USEast1;
    o.QueueName = "work-items";
});
```

## Queue Entry Lifecycle

Each dequeued message goes through a lifecycle:

```txt
                    ┌─────────┐
                    │ Queued  │
                    └────┬────┘
                         │
                         ▼
              ┌──────────────────┐
              │ Dequeued/Working │
              └────┬─────────────┘
                   │
                   ▼
            ┌──────────────┐
            │  Processing  │
            └──┬────────┬──┘
               │        │
        Success│        │Failure
               │        │
               ▼        ▼
          ┌────────┐  ┌───────────┐
          │Complete│  │ Abandoned │
          └────────┘  └─────┬─────┘
                            │
                            ▼
                       ┌─────────┐
                       │ Retry?  │
                       └──┬───┬──┘
                   Yes    │   │    No
                          │   │
                          ▼   ▼
                    ┌─────────┐  ┌──────────────┐
                    │ Queued  │  │ Dead Letter  │
                    └─────────┘  └──────────────┘
```

### Completing Entries

Mark an entry as successfully processed:

```csharp
var entry = await queue.DequeueAsync();
try
{
    await ProcessAsync(entry.Value);
    await entry.CompleteAsync();
}
catch
{
    await entry.AbandonAsync();
    throw;
}
```

### Abandoning Entries

Return an entry to the queue for retry:

```csharp
var entry = await queue.DequeueAsync();
if (!CanProcess(entry.Value))
{
    // Return to queue for later processing
    await entry.AbandonAsync();
    return;
}
```

### Lock Renewal

When processing takes longer than the `WorkItemTimeout`, the queue entry's lock may expire, causing another worker to pick up the same item. Use `RenewLockAsync` to extend the lock duration.

**Why lock renewal matters:**

- Prevents duplicate processing when work takes longer than expected
- Avoids entries being re-queued while still being processed
- Essential for variable-duration workloads

::: tip Recommended Approach
Use `QueueJobBase<T>` for queue processing (see [Jobs - Queue Processor Jobs](/guide/jobs#queue-processor-jobs)). For manual processing, call `RenewLockAsync()` periodically within your processing logic.
:::

**Best practices for `WorkItemTimeout`:**

- Set `WorkItemTimeout` to your typical processing time plus padding (e.g., 2x normal duration)
- Call `RenewLockAsync()` before the timeout expires if processing takes longer than expected
- Monitor your processing times to adjust the timeout appropriately

#### Manual Renewal in Queue Jobs

For long-running operations in a `QueueJobBase<T>`, renew the lock during processing:

```csharp
public class VideoProcessorJob : QueueJobBase<VideoWorkItem>
{
    private readonly IVideoService _videoService;

    public VideoProcessorJob(IQueue<VideoWorkItem> queue, IVideoService videoService)
        : base(queue) => _videoService = videoService;

    protected override async Task<JobResult> ProcessQueueEntryAsync(
        QueueEntryContext<VideoWorkItem> context)
    {
        var workItem = context.QueueEntry.Value;
        var startTime = DateTime.UtcNow;

        try
        {
            // Start processing
            await _videoService.StartProcessingAsync(workItem.VideoId);

            // Renew lock if processing is taking longer than expected
            if (DateTime.UtcNow - startTime > TimeSpan.FromMinutes(3))
            {
                await context.QueueEntry.RenewLockAsync();
            }

            await _videoService.CompleteProcessingAsync(workItem.VideoId);
            return JobResult.Success;
        }
        catch (Exception ex)
        {
            return JobResult.FromException(ex);
        }
    }
}
```

::: warning Manual Lock Renewal
Most processing should complete within the `WorkItemTimeout`. If you regularly need lock renewal, increase the `WorkItemTimeout` instead. Manual renewal should only be used for truly variable-duration workloads where you cannot predict processing time accurately.
:::

#### Ensuring Single Processing with GetQueueEntryLockAsync

Override `GetQueueEntryLockAsync` to acquire a distributed lock based on a unique value from the work item. This guarantees that even if the same item is enqueued multiple times (e.g., due to retries or system failures), only one instance will process it at a time.

**When to use this:**

- Processing must be guaranteed to occur only once per unique identifier
- Work items can be re-queued due to failures, but duplicate processing would cause issues
- You need to lock on a business key (e.g., user ID, order ID) rather than the queue entry ID

```csharp
public class OrderProcessorJob : QueueJobBase<OrderWorkItem>
{
    private readonly ILockProvider _lockProvider;
    private readonly IOrderService _orderService;

    public OrderProcessorJob(
        IQueue<OrderWorkItem> queue,
        ILockProvider lockProvider,
        IOrderService orderService) : base(queue)
    {
        _lockProvider = lockProvider;
        _orderService = orderService;
    }

    // Override to lock on the order ID instead of the queue entry ID
    protected override Task<ILock> GetQueueEntryLockAsync(
        IQueueEntry<OrderWorkItem> queueEntry,
        CancellationToken cancellationToken = default)
    {
        // Lock on the business key (order ID) to prevent concurrent processing
        // of the same order across all queue entries
        string lockKey = $"order:{queueEntry.Value.OrderId}";
        return _lockProvider.AcquireAsync(lockKey, TimeSpan.FromMinutes(5), cancellationToken);
    }

    protected override async Task<JobResult> ProcessQueueEntryAsync(
        QueueEntryContext<OrderWorkItem> context)
    {
        // This will only execute if we successfully acquired the lock
        // Multiple queue entries for the same order will be serialized
        var orderId = context.QueueEntry.Value.OrderId;

        await _orderService.ProcessAsync(orderId, context.CancellationToken);
        return JobResult.Success;
    }
}
```

**How it works:**

1. When `QueueJobBase` dequeues an entry, it calls `GetQueueEntryLockAsync` before processing
2. If the lock cannot be acquired (returns `null`), the entry is abandoned and returned to the queue
3. If the lock is acquired, processing continues and the lock is automatically released after completion
4. The lock is also used for manual renewal within `ProcessQueueEntryAsync` via `await context.QueueEntry.RenewLockAsync()`

::: tip Lock Provider Selection
Use a distributed lock provider (e.g., `CacheLockProvider` with Redis) in production to coordinate across multiple instances. For single-instance scenarios, `CacheLockProvider` with `InMemoryCacheClient` is sufficient.
:::

## Processing Patterns

::: tip Recommended Approach
For production applications, use `QueueJobBase<T>` with `Foundatio.Extensions.Hosting` for reliable, automatic background processing. See [Jobs - Queue Processor Jobs](/guide/jobs#queue-processor-jobs) for details. The patterns below are for advanced scenarios or custom integrations.
:::

### Simple Processing Loop

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    var entry = await queue.DequeueAsync(cancellationToken);
    if (entry == null)
        continue;

    try
    {
        await ProcessAsync(entry.Value);
        await entry.CompleteAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to process {Id}", entry.Value.Id);
        await entry.AbandonAsync();
    }
}
```

### Using StartWorkingAsync

Simplified background processing:

```csharp
// Start processing in background
await queue.StartWorkingAsync(
    async (entry, ct) =>
    {
        await ProcessAsync(entry.Value);
    },
    autoComplete: true,  // Automatically complete on success
    cancellationToken
);
```

## Queue Entry Options

Configure enqueue behavior:

```csharp
await queue.EnqueueAsync(new WorkItem { Id = 1 }, new QueueEntryOptions
{
    UniqueId = "unique-id",           // Dedupe by ID
    CorrelationId = "request-123",    // For tracing
    DeliveryDelay = TimeSpan.FromMinutes(5),  // Delayed delivery
    Properties = new Dictionary<string, string>
    {
        ["priority"] = "high"
    }
});
```

## Queue Events

Subscribe to queue lifecycle events:

```csharp
var queue = new InMemoryQueue<WorkItem>();

queue.Enqueuing.AddHandler(async (sender, args) =>
{
    _logger.LogInformation("Enqueuing: {Data}", args.Entry.Value);
});

queue.Enqueued.AddHandler(async (sender, args) =>
{
    _logger.LogInformation("Enqueued: {Id}", args.Entry.Id);
});

queue.Dequeued.AddHandler(async (sender, args) =>
{
    _logger.LogInformation("Dequeued: {Id}", args.Entry.Id);
});

queue.Completed.AddHandler(async (sender, args) =>
{
    _logger.LogInformation("Completed: {Id}", args.Entry.Id);
});

queue.Abandoned.AddHandler(async (sender, args) =>
{
    _logger.LogWarning("Abandoned: {Id}", args.Entry.Id);
});
```

## Queue Behaviors

Extend queue functionality with behaviors. Behaviors hook into queue events to add cross-cutting concerns like logging, metrics, or deduplication.

### Creating Custom Behaviors

```csharp
public class LoggingQueueBehavior<T> : QueueBehaviorBase<T> where T : class
{
    private readonly ILogger _logger;

    public LoggingQueueBehavior(ILogger logger) => _logger = logger;

    protected override Task OnEnqueued(object sender, EnqueuedEventArgs<T> args)
    {
        _logger.LogInformation("Enqueued {Id}", args.Entry.Id);
        return Task.CompletedTask;
    }

    protected override Task OnDequeued(object sender, DequeuedEventArgs<T> args)
    {
        _logger.LogInformation("Dequeued {Id}", args.Entry.Id);
        return Task.CompletedTask;
    }

    protected override Task OnCompleted(object sender, CompletedEventArgs<T> args)
    {
        _logger.LogInformation("Completed {Id} in {Duration}ms",
            args.Entry.Id, args.Entry.ProcessingTime.TotalMilliseconds);
        return Task.CompletedTask;
    }

    protected override Task OnAbandoned(object sender, AbandonedEventArgs<T> args)
    {
        _logger.LogWarning("Abandoned {Id}, attempt {Attempt}",
            args.Entry.Id, args.Entry.Attempts);
        return Task.CompletedTask;
    }
}

// Attach to queue
queue.AttachBehavior(new LoggingQueueBehavior<WorkItem>(logger));
```

### Built-in: Duplicate Detection Behavior

Foundatio includes `DuplicateDetectionQueueBehavior<T>` to prevent duplicate messages from being enqueued. This is useful for scenarios where the same work item might be submitted multiple times.

```csharp
// Your message must implement IHaveUniqueIdentifier
public class OrderWorkItem : IHaveUniqueIdentifier
{
    public int OrderId { get; set; }
    public string UniqueIdentifier => $"order:{OrderId}";
}

// Attach the behavior
var cache = new InMemoryCacheClient();
queue.AttachBehavior(new DuplicateDetectionQueueBehavior<OrderWorkItem>(
    cache,
    loggerFactory,
    detectionWindow: TimeSpan.FromMinutes(10)  // How long to remember seen IDs
));

// Duplicates are automatically discarded
await queue.EnqueueAsync(new OrderWorkItem { OrderId = 123 }); // ✅ Enqueued
await queue.EnqueueAsync(new OrderWorkItem { OrderId = 123 }); // ❌ Discarded (duplicate)
await queue.EnqueueAsync(new OrderWorkItem { OrderId = 456 }); // ✅ Enqueued
```

**How it works:**

1. On enqueue, the behavior checks if the `UniqueIdentifier` exists in the cache
2. If found, the message is discarded (not enqueued)
3. If not found, the identifier is cached with the specified TTL
4. On dequeue, the identifier is removed from the cache (allowing re-submission)

### Attaching Multiple Behaviors

```csharp
var queue = new InMemoryQueue<WorkItem>(o => o
    .Behaviors(
        new LoggingQueueBehavior<WorkItem>(logger),
        new DuplicateDetectionQueueBehavior<WorkItem>(cache, loggerFactory),
        new MetricsQueueBehavior<WorkItem>(metrics)
    ));
```

## Queue Statistics

Monitor queue health:

```csharp
var stats = await queue.GetQueueStatsAsync();

Console.WriteLine($"Queued: {stats.Queued}");
Console.WriteLine($"Working: {stats.Working}");
Console.WriteLine($"Dead Letter: {stats.Deadletter}");
Console.WriteLine($"Enqueued: {stats.Enqueued}");
Console.WriteLine($"Dequeued: {stats.Dequeued}");
Console.WriteLine($"Completed: {stats.Completed}");
Console.WriteLine($"Abandoned: {stats.Abandoned}");
Console.WriteLine($"Errors: {stats.Errors}");
Console.WriteLine($"Timeouts: {stats.Timeouts}");
```

## Dead Letter Queue

Handle failed messages that have exceeded the retry limit:

```csharp
// Get dead letter items
var deadLetters = await queue.GetDeadletterItemsAsync();

foreach (var item in deadLetters)
{
    _logger.LogWarning("Dead letter: {Id}", item.Id);

    // Optionally re-queue for retry
    await queue.EnqueueAsync(item);
}
```

### When Messages Go to Dead Letter

Messages are moved to the dead letter queue when:

1. The message has been abandoned more times than the configured `Retries` count
2. Processing repeatedly fails and the retry limit is exhausted

### Monitoring Dead Letters

```csharp
var stats = await queue.GetQueueStatsAsync();
if (stats.Deadletter > 0)
{
    _logger.LogWarning("Dead letter queue has {Count} items", stats.Deadletter);
    // Alert operations team, trigger investigation
}
```

## Retry Policies

All Foundatio queue implementations share common retry behavior configured via `SharedQueueOptions`:

| Option | Default | Description |
|--------|---------|-------------|
| `Retries` | 2 | Maximum number of retry attempts before dead-lettering |
| `WorkItemTimeout` | 5 minutes | How long a worker can hold a message before it's considered abandoned |

### WorkItemTimeout Best Practices

The `WorkItemTimeout` determines how long a dequeued entry stays locked before being considered abandoned and returned to the queue for retry. Setting this value correctly is critical for reliable queue processing.

**Guidelines for setting `WorkItemTimeout`:**

```csharp
var queue = new RedisQueue<WorkItem>(o =>
{
    // For predictable workloads: typical duration + padding
    // Example: If processing takes 2 minutes, set to 4-5 minutes
    o.WorkItemTimeout = TimeSpan.FromMinutes(5);

    // For variable workloads: maximum expected duration + buffer
    // Example: If processing can take up to 10 minutes, set to 15 minutes
    o.WorkItemTimeout = TimeSpan.FromMinutes(15);
});
```

**Sizing recommendations:**

- **Fast operations (< 30 seconds)**: Set to 1-2 minutes to allow for retries without long delays
- **Standard operations (1-5 minutes)**: Set to 2x your average processing time (e.g., 3 minutes avg → 6 minute timeout)
- **Long operations (> 5 minutes)**: Set to 1.5x your maximum expected time, but consider using manual lock renewal if highly variable
- **Always include padding**: Account for network latency, temporary slowdowns, and system load

**What happens when timeout expires:**

1. The queue entry lock is released
2. Another worker can pick up the same entry
3. The original worker may still be processing (potentially duplicate work)
4. Entry's `Attempts` counter increments
5. After `Retries` attempts, the entry moves to the dead letter queue

::: warning Timeout Too Short
If `WorkItemTimeout` is too short, entries will be re-queued before processing completes, leading to duplicate processing attempts and wasted resources.
:::

::: tip Monitoring and Adjustment
Monitor your queue processing times and adjust `WorkItemTimeout` based on actual metrics. Use Application Insights, logging, or custom telemetry to track processing duration over time.
:::

### InMemoryQueue Retry Options

The in-memory queue provides additional retry configuration:

```csharp
var queue = new InMemoryQueue<WorkItem>(o =>
{
    o.Retries = 3;                              // Max retry attempts
    o.RetryDelay = TimeSpan.FromMinutes(1);     // Base delay between retries
    o.RetryMultipliers = new[] { 1, 3, 5, 10 }; // Exponential backoff multipliers
});
```

**Retry delay calculation:** `RetryDelay × RetryMultipliers[attempt - 1]`

For example, with defaults:

- 1st retry: 1 minute × 1 = 1 minute
- 2nd retry: 1 minute × 3 = 3 minutes
- 3rd retry: 1 minute × 5 = 5 minutes
- 4th+ retry: 1 minute × 10 = 10 minutes

### Provider-Specific Retry Behavior

| Provider | Retry Mechanism | Dead Letter Support |
|----------|-----------------|---------------------|
| InMemoryQueue | Built-in with configurable backoff | In-memory dead letter queue |
| RedisQueue | Built-in with configurable backoff | Redis-backed dead letter queue |
| AzureServiceBusQueue | Native Service Bus retries | Native DLQ with message metadata |
| AzureStorageQueue | Built-in retries | Poison message queue |
| SQSQueue | Native SQS retries | Native DLQ (requires configuration) |

## Message Size Limits

Different queue providers have different message size limits. Understanding these limits is crucial for designing your message contracts.

| Provider | Max Message Size | Notes |
|----------|------------------|-------|
| InMemoryQueue | Limited by available memory | No practical limit |
| RedisQueue | 512 MB (Redis limit) | Recommended: < 1 MB for performance |
| AzureServiceBusQueue | 256 KB (Standard) / 100 MB (Premium) | Use claim check pattern for large payloads |
| AzureStorageQueue | 64 KB | Base64 encoded, effective ~48 KB |
| SQSQueue | 256 KB | Use S3 for larger messages |

### Best Practice: Keep Messages Small

```csharp
// ✅ Good: Small message with reference
public record ProcessImageWorkItem
{
    public required string ImageBlobPath { get; init; }  // Reference to storage
    public required string OutputPath { get; init; }
    public required ImageProcessingOptions Options { get; init; }
}

// ❌ Bad: Large payload in message
public record ProcessImageWorkItem
{
    public required byte[] ImageData { get; init; }  // Could be megabytes!
    public required ImageProcessingOptions Options { get; init; }
}
```

### Claim Check Pattern for Large Payloads

When you need to process large data, store it externally and pass a reference:

```csharp
// Store large data in blob storage
var blobPath = $"work-items/{Guid.NewGuid()}.json";
await fileStorage.SaveObjectAsync(blobPath, largePayload);

// Enqueue reference only
await queue.EnqueueAsync(new WorkItem
{
    PayloadPath = blobPath,
    PayloadSize = largePayload.Length
});

// In worker: retrieve the payload
var entry = await queue.DequeueAsync();
var payload = await fileStorage.GetObjectAsync<LargePayload>(entry.Value.PayloadPath);
await ProcessAsync(payload);
await entry.CompleteAsync();

// Clean up blob after processing
await fileStorage.DeleteFileAsync(entry.Value.PayloadPath);
```

## Dependency Injection

### Basic Registration

```csharp
// In-memory (development)
services.AddSingleton<IQueue<WorkItem>>(sp =>
    new InMemoryQueue<WorkItem>());

// Redis (production)
services.AddSingleton<IQueue<WorkItem>>(sp =>
    new RedisQueue<WorkItem>(o => {
        o.ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
        o.Name = "work-items";
    }));
```

::: tip Automatic Queue Processing
For automatic background processing of queue items, use `QueueJobBase<T>` with `Foundatio.Extensions.Hosting`. See [Jobs - Queue Processor Jobs](/guide/jobs#queue-processor-jobs) for details.

```csharp
// Register queue and processor job
services.AddSingleton<IQueue<OrderWorkItem>>(sp => new InMemoryQueue<OrderWorkItem>());
services.AddJob<OrderProcessorJob>();  // Automatically processes queue items
```

### Multiple Queues

```csharp
services.AddSingleton<IQueue<OrderWorkItem>>(sp =>
    new InMemoryQueue<OrderWorkItem>(o => o.Name = "orders"));

services.AddSingleton<IQueue<EmailWorkItem>>(sp =>
    new InMemoryQueue<EmailWorkItem>(o => o.Name = "emails"));
```

## Best Practices

### 1. Proper Resource Disposal

Queues implement `IDisposable` and should be properly disposed:

```csharp
// ✅ Good: Using statement for short-lived queues
await using var queue = new InMemoryQueue<WorkItem>();
await queue.EnqueueAsync(new WorkItem { Id = 1 });

// ✅ Good: DI container manages lifetime
services.AddSingleton<IQueue<WorkItem>>(sp =>
    new InMemoryQueue<WorkItem>());

// ❌ Bad: Not disposing
var queue = new InMemoryQueue<WorkItem>();
// ... use queue
// Queue is never disposed, resources leak
```

### 2. Use Typed Messages

```csharp
// ✅ Good: Typed, versioned messages
public record OrderWorkItem
{
    public int Version { get; init; } = 1;
    public required int OrderId { get; init; }
    public required DateTime CreatedAt { get; init; }
}

// ❌ Bad: Generic, untyped
public class WorkItem
{
    public object Data { get; set; }
}
```

### 2. Handle Idempotency

```csharp
var entry = await queue.DequeueAsync();

// Check if already processed
if (await _processedIds.ContainsAsync(entry.Value.Id))
{
    await entry.CompleteAsync();
    return;
}

// Process
await ProcessAsync(entry.Value);

// Mark as processed
await _processedIds.AddAsync(entry.Value.Id);
await entry.CompleteAsync();
```

### 3. Set Appropriate Timeouts

```csharp
var queue = new RedisQueue<WorkItem>(o => {
    o.WorkItemTimeout = TimeSpan.FromMinutes(5);  // How long to process
    o.RetryDelay = TimeSpan.FromSeconds(30);      // Delay before retry
    o.Retries = 3;                                 // Max retries
});
```

### 4. Monitor Queue Depth

```csharp
var stats = await queue.GetQueueStatsAsync();
if (stats.Queued > 1000)
{
    _logger.LogWarning("Queue depth is high: {Depth}", stats.Queued);
    // Consider scaling workers
}
```

### 5. Use Delayed Delivery for Scheduling

```csharp
// Schedule for later
await queue.EnqueueAsync(reminder, new QueueEntryOptions
{
    DeliveryDelay = TimeSpan.FromHours(24)
});
```

## Next Steps

- [Jobs](./jobs) - Queue processor jobs for automatic background processing with `QueueJobBase<T>`
- [Messaging](./messaging) - Pub/sub for event-driven patterns
- [Locks](./locks) - Coordinate queue processing across instances
- [Serialization](./serialization) - Serializer configuration and performance
