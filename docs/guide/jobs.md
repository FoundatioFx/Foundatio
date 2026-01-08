# Jobs

Jobs allow you to run long-running processes without worrying about them being terminated prematurely. Foundatio provides several patterns for defining and running jobs.

## The IJob Interface

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Jobs/IJob.cs)

```csharp
public interface IJob
{
    Task<JobResult> RunAsync(CancellationToken cancellationToken = default);
}
```

## Job Types

Foundatio provides three main patterns for defining jobs:

1. **Standard Jobs** - Simple jobs that run independently
2. **Queue Processor Jobs** - Jobs that process items from a queue
3. **Work Item Jobs** - Jobs that handle work items from a shared pool

## Standard Jobs

### Basic Job

Create a job by implementing `IJob` or deriving from `JobBase` ([view source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Jobs/JobBase.cs)):

```csharp
using Foundatio.Jobs;

public class CleanupJob : JobBase
{
    private readonly ILogger<CleanupJob> _logger;

    public CleanupJob(ILogger<CleanupJob> logger)
    {
        _logger = logger;
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        _logger.LogInformation("Starting cleanup...");

        // Do cleanup work
        var deletedCount = await CleanupOldRecordsAsync(context.CancellationToken);

        _logger.LogInformation("Cleaned up {Count} records", deletedCount);

        return JobResult.Success;
    }
}
```

### Running Jobs

```csharp
var job = new CleanupJob(logger);

// Run once
await job.RunAsync();

// Run continuously with interval
await job.RunContinuousAsync(
    interval: TimeSpan.FromMinutes(5),
    cancellationToken: stoppingToken
);

// Run with iteration limit
await job.RunContinuousAsync(
    iterationLimit: 100,
    cancellationToken: stoppingToken
);
```

### Job Results

```csharp
protected override Task<JobResult> RunInternalAsync(JobContext context)
{
    try
    {
        // Success
        return Task.FromResult(JobResult.Success);

        // Success with message
        return Task.FromResult(JobResult.SuccessWithMessage("Processed 100 items"));

        // Failed
        return Task.FromResult(JobResult.Failed);

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

## Queue Processor Jobs

Process items from a queue automatically:

```csharp
using Foundatio.Jobs;
using Foundatio.Queues;

public class OrderProcessorJob : QueueJobBase<OrderWorkItem>
{
    private readonly IOrderService _orderService;
    private readonly ILogger<OrderProcessorJob> _logger;

    public OrderProcessorJob(
        IQueue<OrderWorkItem> queue,
        IOrderService orderService,
        ILogger<OrderProcessorJob> logger) : base(queue)
    {
        _orderService = orderService;
        _logger = logger;
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

### Running Queue Jobs

```csharp
// Setup
var queue = new InMemoryQueue<OrderWorkItem>();
var job = new OrderProcessorJob(queue, orderService, logger);

// Enqueue work
await queue.EnqueueAsync(new OrderWorkItem { OrderId = 123 });
await queue.EnqueueAsync(new OrderWorkItem { OrderId = 456 });

// Process all items
await job.RunUntilEmptyAsync();

// Or run continuously
await job.RunContinuousAsync(cancellationToken: stoppingToken);
```

## Work Item Jobs

Work item jobs run in a shared pool and are triggered by messages on the message bus:

### Define a Work Item Handler

```csharp
using Foundatio.Jobs;

public class DeleteEntityWorkItemHandler : WorkItemHandlerBase
{
    private readonly IEntityService _entityService;
    private readonly ILogger<DeleteEntityWorkItemHandler> _logger;

    public DeleteEntityWorkItemHandler(
        IEntityService entityService,
        ILogger<DeleteEntityWorkItemHandler> logger)
    {
        _entityService = entityService;
        _logger = logger;
    }

    public override async Task HandleItemAsync(WorkItemContext ctx)
    {
        var workItem = ctx.GetData<DeleteEntityWorkItem>();

        await ctx.ReportProgressAsync(0, "Starting deletion...");

        // Delete entity and all children
        var children = await _entityService.GetChildrenAsync(workItem.EntityId);
        var total = children.Count;
        var current = 0;

        foreach (var child in children)
        {
            await _entityService.DeleteAsync(child.Id);
            current++;
            await ctx.ReportProgressAsync(
                (current * 100) / total,
                $"Deleted {current} of {total} children"
            );
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

### Register and Run Work Item Jobs

```csharp
// Register handlers
var handlers = new WorkItemHandlers();
handlers.Register<DeleteEntityWorkItem, DeleteEntityWorkItemHandler>();

// Register with DI
services.AddSingleton(handlers);
services.AddSingleton<IQueue<WorkItemData>>(sp => new InMemoryQueue<WorkItemData>());
services.AddScoped<DeleteEntityWorkItemHandler>();

// Run the job pool
var job = serviceProvider.GetRequiredService<WorkItemJob>();
await new JobRunner(job, instanceCount: 2).RunAsync(stoppingToken);
```

### Trigger Work Items

```csharp
// Enqueue work item
var queue = serviceProvider.GetRequiredService<IQueue<WorkItemData>>();
await queue.EnqueueAsync(new DeleteEntityWorkItem { EntityId = 123 });

// Subscribe to progress updates
var messageBus = serviceProvider.GetRequiredService<IMessageBus>();
await messageBus.SubscribeAsync<WorkItemStatus>(status =>
{
    Console.WriteLine($"Progress: {status.Progress}% - {status.Message}");
});
```

## Job Runner

Run jobs with various configurations:

```csharp
using Foundatio.Jobs;

var job = new CleanupJob(logger);
var runner = new JobRunner(job);

// Run until cancelled
await runner.RunAsync(stoppingToken);

// Run in background
runner.RunInBackground();

// Multiple instances
var multiRunner = new JobRunner(job, instanceCount: 4);
await multiRunner.RunAsync(stoppingToken);
```

## Job Options

Configure job behavior:

```csharp
public class MyJob : JobBase
{
    protected override JobOptions GetDefaultOptions()
    {
        return new JobOptions
        {
            Name = "MyJob",
            Interval = TimeSpan.FromMinutes(5),
            IterationLimit = -1  // No limit
        };
    }
}
```

### Using Job Options

```csharp
var options = new JobOptions
{
    Name = "CleanupJob",
    Interval = TimeSpan.FromHours(1),
    IterationLimit = 100
};

await job.RunContinuousAsync(options, stoppingToken);
```

## Hosted Service Integration

Foundatio provides `Foundatio.Extensions.Hosting` for seamless ASP.NET Core integration with hosted services.

### Installation

```bash
dotnet add package Foundatio.Extensions.Hosting
```

### AddJob Extension

Register jobs as hosted services:

```csharp
using Foundatio.Extensions.Hosting.Jobs;

// Simple job registration
services.AddJob<CleanupJob>();

// With configuration
services.AddJob<CleanupJob>(o => o
    .Interval(TimeSpan.FromHours(1))
    .WaitForStartupActions()
    .InitialDelay(TimeSpan.FromSeconds(30)));

// Multiple instances
services.AddJob<OrderProcessorJob>(o => o.InstanceCount(4));
```

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

// Inline action
services.AddCronJob("health-check", "*/5 * * * *", async (sp, ct) =>
{
    var healthService = sp.GetRequiredService<IHealthService>();
    await healthService.CheckAsync(ct);
});
```

### Distributed Cron Jobs

Ensure only one instance runs a scheduled job across all servers:

```csharp
using Foundatio.Extensions.Hosting.Jobs;

// Only one server runs this job at the scheduled time
services.AddDistributedCronJob<ReportJob>("0 0 * * *");

// Requires ILockProvider to be registered
services.AddSingleton<ILockProvider>(sp =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetRequiredService<IMessageBus>()));
```

### Manual BackgroundService

For custom control, implement `BackgroundService` directly:

```csharp
public class CleanupJobHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CleanupJobHostedService> _logger;

    public CleanupJobHostedService(
        IServiceProvider services,
        ILogger<CleanupJobHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cleanup job starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _services.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<CleanupJob>();

            try
            {
                await job.RunAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup job failed");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

// Register
services.AddScoped<CleanupJob>();
services.AddHostedService<CleanupJobHostedService>();
```

## Common Patterns

### Job with Locking

Ensure only one instance runs:

```csharp
public class SingletonJob : JobBase
{
    private readonly ILockProvider _locker;

    public SingletonJob(ILockProvider locker)
    {
        _locker = locker;
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        await using var lck = await _locker.AcquireAsync("singleton-job");

        if (lck is null)
        {
            _logger.LogDebug("Another instance is running");
            return JobResult.Success;
        }

        // Only one instance runs this
        return await DoWorkAsync(context.CancellationToken);
    }
}
```

### Job with Progress Reporting

```csharp
public class ImportJob : JobBase
{
    private readonly IMessageBus _messageBus;

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        var items = await GetItemsToImportAsync();
        var total = items.Count;
        var processed = 0;

        foreach (var item in items)
        {
            if (context.CancellationToken.IsCancellationRequested)
                return JobResult.Cancelled;

            await ImportItemAsync(item);
            processed++;

            await _messageBus.PublishAsync(new ImportProgress
            {
                ProcessedCount = processed,
                TotalCount = total,
                PercentComplete = (processed * 100) / total
            });
        }

        return JobResult.Success;
    }
}
```

### Retry Failed Jobs

```csharp
public class RetryableJob : JobBase
{
    private readonly ResiliencePolicy _policy;

    public RetryableJob()
    {
        _policy = new ResiliencePolicyBuilder()
            .WithMaxAttempts(3)
            .WithExponentialDelay(TimeSpan.FromSeconds(1))
            .Build();
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        try
        {
            await _policy.ExecuteAsync(async ct =>
            {
                await DoUnreliableWorkAsync(ct);
            }, context.CancellationToken);

            return JobResult.Success;
        }
        catch (Exception ex)
        {
            return JobResult.FromException(ex);
        }
    }
}
```

## Dependency Injection

### Register Jobs

```csharp
services.AddScoped<CleanupJob>();
services.AddScoped<OrderProcessorJob>();
services.AddSingleton<IQueue<OrderWorkItem>>(sp => new InMemoryQueue<OrderWorkItem>());
```

### Queue Jobs with DI

```csharp
services.AddSingleton<IQueue<OrderWorkItem>>(sp =>
    new InMemoryQueue<OrderWorkItem>()
);

services.AddScoped<OrderProcessorJob>();

services.AddHostedService<QueueJobHostedService<OrderWorkItem>>();
```

## Best Practices

### 1. Use Cancellation Tokens

```csharp
protected override async Task<JobResult> RunInternalAsync(JobContext context)
{
    foreach (var item in items)
    {
        // Check for cancellation
        context.CancellationToken.ThrowIfCancellationRequested();

        await ProcessItemAsync(item, context.CancellationToken);
    }

    return JobResult.Success;
}
```

### 2. Log Job Progress

```csharp
protected override async Task<JobResult> RunInternalAsync(JobContext context)
{
    using var _ = _logger.BeginScope(new { JobRunId = Guid.NewGuid() });

    _logger.LogInformation("Starting job");

    try
    {
        await DoWorkAsync();
        _logger.LogInformation("Job completed successfully");
        return JobResult.Success;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Job failed");
        return JobResult.FromException(ex);
    }
}
```

### 3. Keep Jobs Idempotent

```csharp
protected override async Task<JobResult> RunInternalAsync(JobContext context)
{
    // Track what was processed
    var lastProcessedId = await _state.GetLastProcessedIdAsync();

    var items = await _db.GetItemsAfterAsync(lastProcessedId);

    foreach (var item in items)
    {
        await ProcessItemAsync(item);
        await _state.SetLastProcessedIdAsync(item.Id);
    }

    return JobResult.Success;
}
```

### 4. Handle Transient Failures

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
        // Fail and allow retry
        return JobResult.FailedWithMessage(ex.Message);
    }
    catch (PermanentException ex)
    {
        // Log and succeed to prevent retries
        _logger.LogError(ex, "Permanent failure - not retrying");
        return JobResult.Success;
    }
}
```

### 5. Use Appropriate Job Type

| Use Case | Job Type |
|----------|----------|
| Scheduled maintenance | Standard Job |
| Process queue items | Queue Processor Job |
| On-demand heavy tasks | Work Item Job |
| Event-driven processing | Work Item Job |

## Next Steps

- [Queues](./queues) - Queue implementations for job processing
- [Locks](./locks) - Distributed locking for singleton jobs
- [Resilience](./resilience) - Retry policies for job reliability
- [Serialization](./serialization) - Serializer configuration and performance
