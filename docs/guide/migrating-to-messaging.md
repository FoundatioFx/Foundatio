# Migrating to Messages and Durable Jobs

The original queue, message bus and job APIs keep working alongside the new ones, so you can move one queue or job at a time.

| You have | Move to |
|---|---|
| `IQueue<T>.EnqueueAsync(item)` | `IMessageClient.SendAsync(item)` |
| `QueueJobBase<T>` / `IQueueJob<T>` | `IMessageHandler<T>` + `AddConsumer<THandler>()` |
| `entry.CompleteAsync()` / `AbandonAsync()` | Return, or throw to retry |
| `entry.RenewLockAsync()` | Automatic while the handler runs |
| `IMessagePublisher.PublishAsync(msg)` | `IMessageClient.PublishAsync(msg)` |
| `IMessageSubscriber.SubscribeAsync<T>(...)` (every instance) | `AddSubscriber<THandler>(new SubscriptionOptions { Scope = SubscriptionScope.Instance })`, or `IMessageClient.SubscribeAsync` |
| A queue processed by one service while others publish to it | `AddSubscriber<THandler>()`; each service, named with `UseServiceName`, gets its own copy |
| `WorkItemJob` + `WorkItemHandlerBase` with progress | `IDurableJob<TArgs>` + `IJobClient.EnqueueAsync(args)`; progress via `IJobContext` |
| `JobWithLockBase` CRON jobs, `services.AddCronJob<T>(...)` | An `IDurableJob` + `AddFoundatioJobs(j => j.AddCronJob<T>("..."))`; one run per occurrence, no lock needed |
| `IJob` / `JobBase` run on demand or on a schedule | `IDurableJob` ([see below](#converting-an-ijob-or-jobbase)), or unchanged through `j.AddLegacyJob<T>()` and `j.AddCronJob<LegacyJob<T>>("...")` |
| `IJob` / `JobBase` run continuously with `services.AddJob<T>()` | Unchanged: keep `services.AddJob<T>()`. Durable jobs only run when enqueued or scheduled |
| Queue behaviors, duplicate detection | `SendOptions.DeduplicationId` where the transport supports it (elsewhere a warning is logged); otherwise idempotent handlers |
| `QueueEntryOptions.GroupId` | `SendOptions.GroupKey` |

## Step by step

1. Add `AddFoundatioMessaging(m => m.UseRedis(...))` (or another provider) next to your existing registrations.
2. Add `m.AddLegacyMessageBus()` so code that injects `IMessageBus`, including `HybridCacheClient` and `CacheLockProvider`, uses the same transport.
3. Move a queue: change its producers to `SendAsync`, turn its `QueueJobBase<T>` into an `IMessageHandler<T>`, and register it with `AddConsumer`. Drain the old queue before removing its job.
4. Move scheduled jobs: convert each to `IDurableJob` and replace `services.AddCronJob<T>(...)` with `j.AddCronJob<T>(...)`, or schedule it unchanged with `j.AddCronJob<LegacyJob<T>>(...)`. Give each job a stable `[Job(Name = "...")]`. Leave jobs that run continuously on `services.AddJob<T>()`.
5. Move work items: make each work item record the arguments of an `IDurableJob<TArgs>`, and enqueue with `IJobClient`.

Message and job names default to the type's full name. Set stable names before your first deployment so later renames are safe.

Name each service with `m.UseServiceName("orders")`. Its durable subscriptions are kept under that name, so every service needs a name of its own, and on a real broker the host fails to start without one when the service subscribes to events.

## Converting an IJob or JobBase

A durable job without arguments implements `IDurableJob`. It gets an `IJobContext`, so a CRON job can see the occurrence it runs for, its attempt and its id, and report progress.

```csharp
// Before
[Job(Name = "orders.archive")]
public class ArchiveOrdersJob(IOrderArchive archive, ILoggerFactory loggerFactory) : JobBase(loggerFactory)
{
    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        int archived = await archive.ArchiveOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-30), context.CancellationToken);
        return JobResult.SuccessWithMessage($"Archived {archived} orders");
    }
}

// After
[Job(Name = "orders.archive")]
public class ArchiveOrdersJob(IOrderArchive archive) : IDurableJob
{
    public async Task RunAsync(IJobContext context, CancellationToken cancellationToken)
    {
        int archived = await archive.ArchiveOlderThanAsync(context.ScheduledFor.AddDays(-30), cancellationToken);
        await context.ReportProgressAsync(100, $"Archived {archived} orders", cancellationToken);
    }
}
```

- Return to succeed, and report a result message with `ReportProgressAsync`. Throw instead of returning `JobResult.FromException` or `FailedWithMessage`; the job's `RetryPolicy` retries it.
- Use `context.ScheduledFor` rather than the clock, so a late run or a retry processes the window it was scheduled for.
- Keep the `[Job(Name = "...")]`, so stored jobs and schedules keep their names.

To move a job before converting it, register the class unchanged with `j.AddLegacyJob<ArchiveOrdersJob>()`, enqueue it with `EnqueueAsync<LegacyJob<ArchiveOrdersJob>>()` and schedule it with `j.AddCronJob<LegacyJob<ArchiveOrdersJob>>("0 3 * * *")`. It keeps its name; a failed `JobResult` fails the attempt and is retried, and a successful result's message becomes the job's message.
