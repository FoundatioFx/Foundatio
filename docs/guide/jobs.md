# Jobs

Jobs allow you to run long-running processes without worrying about them being terminated prematurely. Foundatio provides several base classes that handle the boilerplate of continuous execution, cancellation, locking, queue processing, and hosting integration — so you focus on your business logic.

## The IJob Interface

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Jobs/IJob.cs)

Every job implements a single method:

```csharp
public interface IJob
{
    Task<JobResult> RunAsync(CancellationToken cancellationToken = default);
}
```

You can implement `IJob` directly, but in practice you'll derive from one of the base classes below.

## Choosing a Job Type

| Scenario | Base Class | When to Use |
|----------|-----------|-------------|
| Scheduled or periodic work | `JobBase` | Maintenance tasks, report generation, data sync |
| Singleton / leader-elected work | `JobWithLockBase` | Only one instance should run across all servers |
| Processing queue items | `QueueJobBase<T>` | Each unit of work arrives as a queue message |
| On-demand heterogeneous tasks | `WorkItemJob` + handlers | User-triggered operations, bulk operations with progress |

### Architectural Tradeoffs

**`JobBase` vs `QueueJobBase<T>`:** A `JobBase` that polls a database on an interval is simpler to reason about but wastes cycles when there's no work. A `QueueJobBase<T>` reacts instantly to new messages and naturally distributes load across instances, but adds a queue dependency. Use `QueueJobBase<T>` when work arrives unpredictably and latency matters; use `JobBase` when work is periodic or the polling interval is acceptable.

**`QueueJobBase<T>` vs `WorkItemJob`:** `QueueJobBase<T>` creates one strongly-typed queue per job — ideal when you have a steady stream of homogeneous work (order processing, email sending, image resizing). `WorkItemJob` uses a single shared `IQueue<WorkItemData>` to multiplex many task types through one queue and job pool. Prefer `WorkItemJob` when tasks are sporadic, one-off, or varied (user-triggered deletes, bulk exports, cache rebuilds) — it avoids creating a dedicated queue and job class for each operation. `WorkItemJob` also supports built-in progress reporting, making it natural for operations that a user is waiting on.

**Lock timeouts and self-healing:** Locks acquired via `JobWithLockBase` or `ILockProvider.AcquireAsync` have a `timeUntilExpires` parameter (default: 20 minutes). If a server crashes while holding a lock, the lock *automatically releases* after this timeout — no manual intervention needed. Set `timeUntilExpires` to a duration comfortably longer than your expected job duration so the lock doesn't expire mid-run, but short enough that a crash doesn't block the next run for too long. For jobs where you can measure average duration, set the timeout to roughly 2-3x that average. For long or unpredictable jobs, use a shorter timeout and call `context.RenewLockAsync()` periodically to extend the lease. When acquiring a lock in `GetLockAsync`, pass `new CancellationToken(true)` to make the attempt non-blocking — `AcquireAsync` checks `cancellationToken.IsCancellationRequested` to decide whether to wait; an already-cancelled token means "try once and return `null` if the lock is held." This lets interval-based jobs gracefully skip a run rather than pile up waiting for a busy lock. The queue's `WorkItemTimeout` serves the same self-healing purpose for queue entries: entries that aren't completed or renewed within the timeout are redelivered to another consumer.

## Standard Jobs

### JobBase

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Jobs/JobBase.cs)

`JobBase` provides structured logging (`_logger`), a `TimeProvider`, and a `ResiliencePolicyProvider`. All base classes accept optional `TimeProvider` and `IResiliencePolicyProvider` constructor parameters (defaulting to `TimeProvider.System` and `DefaultResiliencePolicyProvider.Instance`). You override `RunInternalAsync` and receive a `JobContext`:

```csharp
using Foundatio.Jobs;

public class CleanupJob : JobBase
{
    public CleanupJob(
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory)
        : base(timeProvider, resiliencePolicyProvider, loggerFactory) { }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        var deletedCount = await CleanupOldRecordsAsync(context.CancellationToken);
        _logger.LogInformation("Cleaned up {Count} records", deletedCount);
        return JobResult.Success;
    }
}
```

### JobContext

`JobContext` is passed to `RunInternalAsync` and carries everything your job needs at runtime:

| Member | Description |
|--------|-------------|
| `CancellationToken` | Signals that the job should stop gracefully |
| `Lock` | The distributed lock held by the job (`null` unless using `JobWithLockBase`) |
| `RenewLockAsync()` | Extends the lock lease — call this in long-running loops to prevent expiration. In `QueueEntryContext`, also renews the queue entry's visibility timeout so the message isn't redelivered to another consumer. |

```csharp
protected override async Task<JobResult> RunInternalAsync(JobContext context)
{
    foreach (var batch in GetBatches())
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        await ProcessBatchAsync(batch);
        await context.RenewLockAsync(); // keep the lock alive between batches
    }

    return JobResult.Success;
}
```

### JobWithLockBase

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Jobs/JobWithLockBase.cs)

`JobWithLockBase` automatically acquires a distributed lock before each run and releases it afterward. If the lock cannot be acquired, the run is cancelled — your code is never called. This makes it ideal for leader-election scenarios where exactly one instance should execute across a cluster.

Override two methods:

- **`GetLockAsync`** — return the lock to acquire, or `null` to skip the run.
- **`RunInternalAsync`** — your job logic, called only while the lock is held.

```csharp
using Foundatio.Jobs;
using Foundatio.Lock;

[Job(Description = "Singleton maintenance job", Interval = "5s")]
public class MaintenanceJob : JobWithLockBase
{
    private readonly ILockProvider _lockProvider;

    public MaintenanceJob(
        ICacheClient cache,
        IMessageBus messageBus,
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory) : base(timeProvider, resiliencePolicyProvider, loggerFactory)
    {
        _lockProvider = new CacheLockProvider(cache, messageBus, loggerFactory);
    }

    protected override Task<ILock?> GetLockAsync(CancellationToken cancellationToken)
    {
        // Pass an already-cancelled token so AcquireAsync attempts the lock
        // exactly once without waiting. If the lock is held by another instance,
        // it returns null immediately and this run is skipped.
        return _lockProvider.AcquireAsync(
            nameof(MaintenanceJob),
            timeUntilExpires: TimeSpan.FromMinutes(15),
            cancellationToken: new CancellationToken(true));
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        _logger.LogInformation("Running maintenance (lock held)...");
        await DoMaintenanceAsync(context.CancellationToken);
        return JobResult.Success;
    }
}
```

> **Why `new CancellationToken(true)`?** `ILockProvider.AcquireAsync` uses the cancellation token to decide whether to wait for a busy lock. A token that is already cancelled tells the provider "try once — if the lock is held, return `null` immediately." This is the standard pattern for jobs that run on an interval and should simply skip the current iteration if another instance is already running.

**`JobWithLockBase` vs manual locking in `JobBase`:**

- Use **`JobWithLockBase`** when the *entire run* must be single-instance. The lock wraps the full execution and is released automatically — even on exceptions. Set `timeUntilExpires` in `GetLockAsync` to at least 2-3x your expected run duration so the lock self-heals after a crash but doesn't expire during normal operation.
- Use **manual `ILockProvider.AcquireAsync`** inside `JobBase` when you need finer-grained control — for example, locking individual resources while allowing the job itself to run on multiple servers:

```csharp
public class ResourceSyncJob : JobBase
{
    private readonly ILockProvider _locker;
    private readonly IResourceRepository _repository;

    public ResourceSyncJob(
        ILockProvider locker,
        IResourceRepository repository,
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory)
        : base(timeProvider, resiliencePolicyProvider, loggerFactory)
    {
        _locker = locker;
        _repository = repository;
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        var pendingResources = await _repository.GetPendingSyncAsync(context.CancellationToken);
        if (pendingResources.Count == 0)
            return JobResult.Success;

        _logger.LogInformation("Found {Count} resources to sync", pendingResources.Count);

        foreach (var resource in pendingResources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            await using var lck = await _locker.AcquireAsync(
                $"resource-sync:{resource.Id}",
                cancellationToken: new CancellationToken(true));

            if (lck is null)
            {
                _logger.LogDebug("Skipping resource {ResourceId}, another instance is syncing it", resource.Id);
                continue;
            }

            await _repository.SyncAsync(resource, context.CancellationToken);
        }

        return JobResult.Success;
    }
}
```

### IJobWithOptions

`IJobWithOptions` extends `IJob` with a `JobOptions` property. `JobWithLockBase` implements this interface, and `JobRunner` uses it to pass runtime configuration (name, interval, iteration limit) to job instances. You rarely need to implement it directly.

```csharp
public interface IJobWithOptions : IJob
{
    JobOptions? Options { get; set; }
}
```

### Running Jobs

```csharp
var job = serviceProvider.GetRequiredService<CleanupJob>();

// Run once
await job.RunAsync();

// Run continuously with a 5-minute pause between iterations
await job.RunContinuousAsync(
    interval: TimeSpan.FromMinutes(5),
    cancellationToken: stoppingToken);

// Run exactly 100 iterations then stop
await job.RunContinuousAsync(
    iterationLimit: 100,
    cancellationToken: stoppingToken);
```

`RunContinuousAsync` handles the loop, error delays, and cancellation for you. For queue-based jobs, the return value is the number of items processed successfully; for standard jobs, it's the iteration count.

### Job Results

`JobResult` communicates the outcome of each run to the framework. When running continuously, a failed result triggers an automatic delay before the next iteration to avoid tight error loops:

```csharp
protected override Task<JobResult> RunInternalAsync(JobContext context)
{
    try
    {
        // Success
        return Task.FromResult(JobResult.Success);

        // Success with message
        return Task.FromResult(JobResult.SuccessWithMessage("Processed 100 items"));

        // Failed with message
        return Task.FromResult(JobResult.FailedWithMessage("Database connection failed"));

        // Cancelled
        return Task.FromResult(JobResult.Cancelled);
    }
    catch (Exception ex)
    {
        // From exception
        return Task.FromResult(JobResult.FromException(ex));
    }
}
```

| Factory | `IsSuccess` | Behavior in continuous mode |
|---------|------------|---------------------------|
| `Success` / `SuccessWithMessage` | `true` | Waits `Interval` then runs again |
| `FailedWithMessage` / `FromException` | `false` | Waits at least 100ms (or `Interval`, whichever is longer) |
| `Cancelled` / `CancelledWithMessage` | N/A | Logged as warning; loop continues |

## Queue Processor Jobs

### QueueJobBase\<T\>

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Jobs/QueueJobBase.cs)

`QueueJobBase<T>` processes items from an `IQueue<T>`. Each call to `RunAsync` dequeues one item and calls your `ProcessQueueEntryAsync` method. It handles dequeue timeouts, cancellation, poison messages (null values), and optional per-entry locking automatically.

**Key behaviors:**

- **AutoComplete (default: `true`)** — entries are completed when `ProcessQueueEntryAsync` returns success, or abandoned on failure/exception. Set `AutoComplete = false` when you need to call `CompleteAsync()` / `AbandonAsync()` yourself.
- **Entry-level locking** — override `GetQueueEntryLockAsync` to acquire a distributed lock per queue entry before processing. The default returns an empty (no-op) lock.
- **Poison message safety** — entries with `null` values (deserialization failures) are automatically abandoned without calling your code.

```csharp
using Foundatio.Jobs;
using Foundatio.Queues;

public class OrderProcessorJob : QueueJobBase<OrderWorkItem>
{
    private readonly IOrderService _orderService;

    public OrderProcessorJob(
        IQueue<OrderWorkItem> queue,
        IOrderService orderService,
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory)
        : base(queue, timeProvider, resiliencePolicyProvider, loggerFactory)
    {
        _orderService = orderService;
    }

    protected override async Task<JobResult> ProcessQueueEntryAsync(
        QueueEntryContext<OrderWorkItem> context)
    {
        var workItem = context.QueueEntry.Value;

        _logger.LogInformation("Processing order {OrderId}", workItem.OrderId);

        try
        {
            await _orderService.ProcessAsync(workItem.OrderId, context.CancellationToken);
            return JobResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process order {OrderId}", workItem.OrderId);
            return JobResult.FromException(ex);
        }
    }
}

public record OrderWorkItem
{
    public int OrderId { get; init; }
}
```

### QueueEntryContext\<T\>

`QueueEntryContext<T>` extends `JobContext` and is passed to `ProcessQueueEntryAsync`:

| Member | Description |
|--------|-------------|
| `QueueEntry` | The `IQueueEntry<T>` — access `Value`, `Id`, `Attempts`, `CompleteAsync()`, `AbandonAsync()` |
| `CancellationToken` | Inherited from `JobContext` |
| `Lock` | The per-entry lock from `GetQueueEntryLockAsync` |
| `RenewLockAsync()` | Renews the queue entry's visibility timeout (preventing redelivery) *and* the per-entry distributed lock |

### IQueueJob\<T\>

`IQueueJob<T>` extends `IJob` and exposes the queue and a direct processing method:

- **`ProcessAsync(IQueueEntry<T>, CancellationToken)`** — process a single entry obtained externally (e.g., from a test or a different dequeue source).
- **`Queue`** — the underlying `IQueue<T>`.

### Running Queue Jobs

```csharp
var queue = new InMemoryQueue<OrderWorkItem>();
var job = serviceProvider.GetRequiredService<OrderProcessorJob>();

// Enqueue work
await queue.EnqueueAsync(new OrderWorkItem { OrderId = 123 });
await queue.EnqueueAsync(new OrderWorkItem { OrderId = 456 });

// Process all queued items, then stop (waits up to 30s for an empty queue)
await job.RunUntilEmptyAsync();

// Process with an explicit timeout for the empty-queue wait
await job.RunUntilEmptyAsync(TimeSpan.FromSeconds(10));

// Run continuously — processes items as they arrive
await job.RunContinuousAsync(cancellationToken: stoppingToken);
```

### Queue Processing Behaviors

Behaviors hook into queue lifecycle events to add cross-cutting concerns without modifying your job. Attach them when creating the queue:

```csharp
var cache = new InMemoryCacheClient();
var queue = new InMemoryQueue<OrderWorkItem>(o => o
    .AddBehavior(new DuplicateDetectionQueueBehavior<OrderWorkItem>(
        cache, loggerFactory, detectionWindow: TimeSpan.FromMinutes(10))));
```

`DuplicateDetectionQueueBehavior<T>` discards duplicate entries based on `IHaveUniqueIdentifier.UniqueIdentifier`. Implement the interface on your work item type:

```csharp
public record OrderWorkItem : IHaveUniqueIdentifier
{
    public int OrderId { get; init; }
    public string? UniqueIdentifier => $"order:{OrderId}";
}
```

You can create custom behaviors by extending `QueueBehaviorBase<T>` and overriding any combination of `OnEnqueuing`, `OnEnqueued`, `OnDequeued`, `OnCompleted`, `OnAbandoned`, `OnLockRenewed`, and `OnQueueDeleted`.

## Work Item Jobs

Work item jobs solve a different problem than queue jobs: they process **heterogeneous** tasks from a single shared queue. A `WorkItemJob` dequeues `WorkItemData` messages and dispatches each one to a type-specific handler. This is ideal for user-triggered operations (bulk deletes, imports, exports) where you want progress reporting and don't want to create a separate queue per task type.

### Define a Work Item Handler

Create handlers by extending `WorkItemHandlerBase`:

```csharp
using Foundatio.Jobs;

public class DeleteEntityWorkItemHandler : WorkItemHandlerBase
{
    private readonly IEntityService _entityService;

    public DeleteEntityWorkItemHandler(
        IEntityService entityService,
        ILogger<DeleteEntityWorkItemHandler> logger) : base(logger)
    {
        _entityService = entityService;
    }

    public override async Task HandleItemAsync(WorkItemContext ctx)
    {
        var workItem = ctx.GetData<DeleteEntityWorkItem>();

        await ctx.ReportProgressAsync(0, "Starting deletion...");

        // Delete children with progress reporting
        var children = await _entityService.GetChildrenAsync(workItem.EntityId);
        var total = children.Count;
        var current = 0;

        foreach (var child in children)
        {
            await _entityService.DeleteAsync(child.Id);
            current++;
            await ctx.ReportProgressAsync(
                (current * 100) / total,
                $"Deleted {current} of {total} children");
        }

        await _entityService.DeleteAsync(workItem.EntityId);
        await ctx.ReportProgressAsync(100, "Deletion complete");
    }
}

public record DeleteEntityWorkItem
{
    public int EntityId { get; init; }
}
```

### WorkItemContext

`WorkItemContext` is passed to `HandleItemAsync` and provides everything a handler needs:

| Member | Description |
|--------|-------------|
| `GetData<T>()` | Deserializes the raw payload to your work item type |
| `Data` | The raw work item payload (use `GetData<T>()` instead) |
| `JobId` | Unique identifier for this job run |
| `WorkItemLock` | Optional distributed lock for the work item |
| `CancellationToken` | Signals that processing should stop |
| `Result` | Set to `JobResult.FailedWithMessage(...)` to indicate failure without throwing |
| `ReportProgressAsync(progress, message)` | Publishes `WorkItemStatus` updates via `IMessageBus` |
| `RenewLockAsync()` | Extends the work item lock lease |

### WorkItemHandlers

`WorkItemHandlers` is a registry mapping work item data types to their handlers. You can register handlers in several ways:

```csharp
var handlers = new WorkItemHandlers();

// Instance registration
handlers.Register<DeleteEntityWorkItem>(
    new DeleteEntityWorkItemHandler(entityService, logger));

// Factory registration (lazy — creates a new handler per invocation)
handlers.Register<DeleteEntityWorkItem>(
    () => sp.GetRequiredService<DeleteEntityWorkItemHandler>());

// Inline delegate (for simple tasks that don't need a full handler class)
handlers.Register<SimpleWorkItem>(async ctx =>
{
    var data = ctx.GetData<SimpleWorkItem>();
    await ProcessAsync(data);
});
```

### Register and Run Work Item Jobs

```csharp
// DI registration
services.AddSingleton<IQueue<WorkItemData>>(sp => new InMemoryQueue<WorkItemData>());
services.AddSingleton<IMessageBus>(sp => new InMemoryMessageBus());
services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<IMessageBus>());
services.AddScoped<DeleteEntityWorkItemHandler>();
services.AddSingleton(sp =>
{
    var handlers = new WorkItemHandlers();
    handlers.Register<DeleteEntityWorkItem>(
        () => sp.GetRequiredService<DeleteEntityWorkItemHandler>());
    return handlers;
});

// Run with multiple instances for parallel processing
var job = serviceProvider.GetRequiredService<WorkItemJob>();
await new JobRunner(job, serviceProvider, instanceCount: 2).RunAsync(stoppingToken);
```

### Trigger Work Items

Use the `EnqueueAsync<T>` extension method to enqueue strongly-typed work items:

```csharp
var queue = serviceProvider.GetRequiredService<IQueue<WorkItemData>>();

// Enqueue a work item (returns a job ID for tracking)
string jobId = await queue.EnqueueAsync(new DeleteEntityWorkItem { EntityId = 123 });

// With progress reporting enabled
string jobId = await queue.EnqueueAsync(
    new DeleteEntityWorkItem { EntityId = 123 },
    includeProgressReporting: true);

// Subscribe to progress updates
var messageBus = serviceProvider.GetRequiredService<IMessageBus>();
await messageBus.SubscribeAsync<WorkItemStatus>(status =>
{
    Console.WriteLine($"[{status.WorkItemId}] {status.Progress}% - {status.Message}");
});
```

## Job Runner

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Jobs/JobRunner.cs)

`JobRunner` orchestrates job execution with support for continuous running, multiple parallel instances, initial delays, and console hosting:

```csharp
using Foundatio.Jobs;

var job = serviceProvider.GetRequiredService<CleanupJob>();
var runner = new JobRunner(job, serviceProvider);

// Run until cancelled
await runner.RunAsync(stoppingToken);

// Run in background (fire-and-forget)
runner.RunInBackground();

// Multiple parallel instances
var multiRunner = new JobRunner(job, serviceProvider, instanceCount: 4);
await multiRunner.RunAsync(stoppingToken);
```

### Console App Hosting

`RunInConsoleAsync` sets up `Ctrl+C` and Azure WebJobs shutdown file handling, runs the job, and returns a process exit code:

```csharp
var exitCode = await new JobRunner(job, serviceProvider).RunInConsoleAsync();
Environment.Exit(exitCode);
// Returns: 0 = success, -1 = failure, 1 = unhandled exception
```

## Job Options

### Job Attribute

Configure job behavior declaratively with the `[Job]` attribute. These values become the defaults that `JobRunner` and the hosting infrastructure use:

```csharp
[Job(
    Name = "MyJob",
    Description = "Processes pending items",
    Interval = "5m",
    InitialDelay = "10s",
    IsContinuous = true,
    IterationLimit = -1,
    InstanceCount = 1
)]
public class MyJob : JobBase
{
    public MyJob(
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory)
        : base(timeProvider, resiliencePolicyProvider, loggerFactory) { }

    protected override Task<JobResult> RunInternalAsync(JobContext context)
    {
        return Task.FromResult(JobResult.Success);
    }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | `string?` | Type name minus "Job" suffix | Display name used in logging and status APIs |
| `Description` | `string?` | `null` | Human-readable description |
| `IsContinuous` | `bool` | `true` | Whether the job runs in a loop |
| `Interval` | `string?` | `null` | Delay between iterations (e.g., `"5m"`, `"30s"`) |
| `InitialDelay` | `string?` | `null` | Delay before first execution |
| `IterationLimit` | `int` | `-1` | Maximum iterations (`-1` = unlimited) |
| `InstanceCount` | `int` | `1` | Number of parallel instances |

### JobOptions Class

`JobOptions` holds the same settings programmatically. Values from `[Job]` are applied as defaults, and can be overridden at runtime:

```csharp
var options = new JobOptions
{
    Name = "CleanupJob",
    Interval = TimeSpan.FromHours(1),
    IterationLimit = 100,
    RunContinuous = true,
    InstanceCount = 2,
    InitialDelay = TimeSpan.FromSeconds(30)
};

await job.RunContinuousAsync(options, stoppingToken);
```

## Hosted Service Integration

`Foundatio.Extensions.Hosting` integrates Foundatio jobs with ASP.NET Core's `IHostedService` pipeline. Jobs are registered as managed background services that start with the host and shut down gracefully.

### Installation

```bash
dotnet add package Foundatio.Extensions.Hosting
```

### AddJob Extension

Register jobs as hosted services with a fluent builder:

```csharp
using Foundatio.Extensions.Hosting.Jobs;

// Simple registration — runs continuously
services.AddJob<CleanupJob>();

// With configuration
services.AddJob<CleanupJob>(o => o
    .Interval(TimeSpan.FromHours(1))
    .WaitForStartupActions()
    .InitialDelay(TimeSpan.FromSeconds(30)));

// Parallel queue processing
services.AddJob<OrderProcessorJob>(o => o.InstanceCount(4));
```

The builder exposes: `Name`, `Description`, `JobFactory`, `RunContinuous`, `Interval`, `InitialDelay`, `IterationLimit`, `InstanceCount`, and `WaitForStartupActions`.

### Cron Job Scheduling

Schedule jobs using cron expressions:

```csharp
using Foundatio.Extensions.Hosting.Jobs;

// Every 6 hours
services.AddCronJob<CleanupJob>("0 */6 * * *");

// Every Monday at midnight
services.AddCronJob<ReportJob>("0 0 * * MON");

// With configuration
services.AddCronJob<MaintenanceJob>("0 2 * * *", o => o
    .Name("nightly-maintenance")
    .WaitForStartupActions());

// Inline action — no job class needed
services.AddCronJob("health-check", "*/5 * * * *", async (sp, ct) =>
{
    var healthService = sp.GetRequiredService<IHealthService>();
    await healthService.CheckAsync(ct);
});
```

#### Cron Helper Class

Use the `Cron` helper to generate common cron expressions without memorizing the syntax:

```csharp
using Foundatio.Extensions.Hosting.Jobs;

services.AddCronJob<CleanupJob>(Cron.Hourly());                                // every hour at :00
services.AddCronJob<ReportJob>(Cron.Daily(hour: 2));                            // daily at 2:00 AM
services.AddCronJob<WeeklyJob>(Cron.Weekly(DayOfWeek.Monday, hour: 9));         // Monday at 9 AM
services.AddCronJob<MonthlyJob>(Cron.Monthly(day: 1));                          // 1st of each month
services.AddCronJob<FrequentJob>(Cron.Minutely(5));                             // every 5 minutes
services.AddCronJob<YearlyJob>(Cron.Yearly(month: 1));                          // January 1st
services.AddCronJob<DisabledJob>(Cron.Never());                                 // never (disabled)
```

#### Scheduled Job Options

Cron jobs support additional configuration through `ScheduledJobOptionsBuilder`:

```csharp
services.AddCronJob<ReportJob>("0 0 * * *", o => o
    .Name("daily-report")
    .Description("Generates the daily summary report")
    .WaitForStartupActions()
    .CronTimeZone("America/New_York")
    .Enabled(true));
```

### Distributed Cron Jobs

Ensure only one instance runs a scheduled job across all servers. This requires an `ICacheClient` registration for distributed lock coordination:

```csharp
using Foundatio.Extensions.Hosting.Jobs;

services.AddDistributedCronJob<ReportJob>("0 0 * * *");

// Requires ICacheClient for distributed locking
services.AddSingleton<ICacheClient>(sp => new RedisCacheClient(...));
```

### Job Manager

`IJobManager` provides a runtime API for inspecting, triggering, and managing scheduled jobs. It is automatically registered when you use `AddCronJob` or `AddJobScheduler`:

```csharp
var jobManager = serviceProvider.GetRequiredService<IJobManager>();

// View all job statuses
JobStatus[] statuses = jobManager.GetJobStatus();
foreach (var status in statuses)
    Console.WriteLine($"{status.Name}: NextRun={status.NextRun}, LastRun={status.LastRun}");

// Trigger a job on-demand (runs immediately regardless of schedule)
await jobManager.RunJobAsync<CleanupJob>();

// Add or update a scheduled job at runtime
jobManager.AddOrUpdate<CleanupJob>(o => o.CronSchedule(Cron.Hourly()));

// Disable a job without removing it
jobManager.Update<CleanupJob>(o => o.Disabled());

// Remove a job entirely
jobManager.Remove<CleanupJob>();

// Release a stuck distributed lock (e.g., after a server crash)
await jobManager.ReleaseLockAsync("Cleanup");
```

### Manual BackgroundService

When the `AddJob` extensions don't fit your needs, you can integrate any Foundatio job with `BackgroundService` directly:

```csharp
public class CleanupJobHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CleanupJobHostedService> _logger;

    public CleanupJobHostedService(
        IServiceProvider services, ILogger<CleanupJobHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _services.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<CleanupJob>();

            try { await job.RunAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Cleanup job failed"); }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

services.AddScoped<CleanupJob>();
services.AddHostedService<CleanupJobHostedService>();
```

## Common Patterns

### Job with Progress Reporting

Use `IMessageBus` to publish progress from standard jobs:

```csharp
public class ImportJob : JobBase
{
    private readonly IMessageBus _messageBus;

    public ImportJob(
        IMessageBus messageBus,
        TimeProvider timeProvider,
        IResiliencePolicyProvider resiliencePolicyProvider,
        ILoggerFactory loggerFactory)
        : base(timeProvider, resiliencePolicyProvider, loggerFactory)
    {
        _messageBus = messageBus;
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        var items = await GetItemsToImportAsync();
        var total = items.Count;

        for (int i = 0; i < total; i++)
        {
            if (context.CancellationToken.IsCancellationRequested)
                return JobResult.Cancelled;

            await ImportItemAsync(items[i]);
            await _messageBus.PublishAsync(new ImportProgress
            {
                ProcessedCount = i + 1,
                TotalCount = total,
                PercentComplete = ((i + 1) * 100) / total
            });
        }

        return JobResult.Success;
    }
}
```

### Retry vs Permanent Failure

Distinguish between transient errors (retry is useful) and permanent errors (retry would loop forever):

```csharp
protected override async Task<JobResult> RunInternalAsync(JobContext context)
{
    try
    {
        await DoWorkAsync(context.CancellationToken);
        return JobResult.Success;
    }
    catch (TransientException ex)
    {
        return JobResult.FailedWithMessage(ex.Message); // framework retries
    }
    catch (PermanentException ex)
    {
        _logger.LogError(ex, "Permanent failure — not retrying");
        return JobResult.Success; // return success to prevent retry loop
    }
}
```

### Idempotent Jobs

Track progress externally so the job can safely resume after a crash:

```csharp
protected override async Task<JobResult> RunInternalAsync(JobContext context)
{
    var lastProcessedId = await _state.GetLastProcessedIdAsync();
    var items = await _db.GetItemsAfterAsync(lastProcessedId);

    foreach (var item in items)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        await ProcessItemAsync(item);
        await _state.SetLastProcessedIdAsync(item.Id);
    }

    return JobResult.Success;
}
```

## Best Practices

1. **Always propagate cancellation tokens.** Pass `context.CancellationToken` to every async call and check it in loops. This ensures your job shuts down promptly during host shutdown.

2. **Renew locks in long-running jobs.** If your job holds a distributed lock (via `JobWithLockBase` or queue entry locking), call `context.RenewLockAsync()` periodically — especially between batches. Lock expiration mid-run causes correctness issues.

3. **Keep jobs idempotent.** Jobs may be killed at any point (process recycle, deployment, crash). Track progress so they can pick up where they left off rather than re-processing everything.

4. **Log with structured context.** Use `BeginScope` to correlate all log entries for a unit of work:

```csharp
using var _ = _logger.BeginScope(s => s.Property("OrderId", workItem.OrderId));
_logger.LogInformation("Processing order...");
// every log inside this scope automatically includes OrderId
```

5. **Match job type to workload.** Don't force a `QueueJobBase` when a simple `JobBase` with `RunContinuousAsync` suffices. Don't create separate queues for every task type — use `WorkItemJob` for heterogeneous on-demand work.

6. **Use distributed cron for cluster-wide scheduling.** If you have multiple servers running the same host, use `AddDistributedCronJob` to ensure only one server executes the scheduled run.

## Dependency Injection

### Register Standard Jobs

```csharp
services.AddScoped<CleanupJob>();
services.AddScoped<OrderProcessorJob>();
services.AddSingleton<IQueue<OrderWorkItem>>(sp => new InMemoryQueue<OrderWorkItem>());
```

### Register Queue Jobs with Parallel Processing

```csharp
services.AddSingleton<IQueue<OrderWorkItem>>(sp => new InMemoryQueue<OrderWorkItem>());
services.AddJob<OrderProcessorJob>(o => o.InstanceCount(4));
```

## Next Steps

- [Queues](./queues) — Queue implementations for job processing
- [Locks](./locks) — Distributed locking for singleton jobs
- [Resilience](./resilience) — Retry policies for job reliability
- [Serialization](./serialization) — Serializer configuration and performance

