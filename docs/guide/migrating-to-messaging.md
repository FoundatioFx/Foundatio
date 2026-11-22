# Migrating to Messages and Durable Jobs

The original queue, message bus and job APIs keep working alongside the new ones, so you can move one queue or job at a time.


## Before you start: not in v2 yet

Check these first; work that depends on them stays on the original APIs for now:

- **One transport per app.** Every queue and topic uses the transport you choose with `Use...()`, and `AddLegacyMessageBus()` runs on it too. You can't keep queues on one broker and pub/sub on another.
- **Run once and exit.** There is no "run this job, then stop the process" mode for a Kubernetes CronJob. Host the schedule with `AddCronJob` in a long-running process instead.

## What moves where

| You have | Move to |
|---|---|
| `IQueue<T>.EnqueueAsync(item)` | `IMessageClient.SendAsync(item)` |
| `AzureStorageQueue<T>` (Foundatio.AzureStorage) | `UseAzureStorageQueues()`. It uses its own queues and message format, so drain the legacy queue before you move its consumers, or run both until it is empty |
| `QueueJobBase<T>` / `IQueueJob<T>` | `IMessageHandler<T>` + `AddConsumer<THandler>()` |
| `entry.CompleteAsync()` / `AbandonAsync()` | Return, or throw to retry; see [queue job outcomes](#queue-job-outcomes) |
| `entry.RenewLockAsync()` | Automatic while the handler runs |
| Queue options (`Retries`, `RetryDelay`, `WorkItemTimeout`) | `HandlerOptions`; see [queue options](#queue-options) |
| `IMessagePublisher.PublishAsync(msg)` | `IMessageClient.PublishAsync(msg)`. Both reach each other's subscribers of types messaging knows; see [mixing the two](#mixing-imessagebus-and-imessageclient) |
| `IMessageSubscriber.SubscribeAsync<T>(...)` (every instance) | `AddSubscriber<THandler>(new SubscriptionOptions { Scope = SubscriptionScope.Instance })`, or `IMessageClient.SubscribeAsync` |
| A queue processed by one service while others publish to it | `AddSubscriber<THandler>()`; each service, named with `UseServiceName`, gets its own copy |
| `WorkItemJob` + `WorkItemHandlerBase` with progress | `IJobHandler<TArgs>` + `IJobClient.EnqueueAsync(args)`; progress via `IJobContext` |
| `JobWithLockBase` CRON jobs, `services.AddCronJob<T>(...)` | An `IJobHandler` + `AddFoundatioJobs(j => j.AddCronJob<T>("..."))`; one run per occurrence, no lock needed |
| `IJob` / `JobBase` run on demand or on a schedule | `IJobHandler` ([see below](#converting-an-ijob-or-jobbase)), or unchanged through `j.AddLegacyJob<T>()` and `j.AddCronJob<LegacyJob<T>>("...")` |
| `IJob` / `JobBase` run continuously with `services.AddJob<T>()` | Unchanged: keep `services.AddJob<T>()`. Durable jobs only run when enqueued or scheduled |
| A hosted job on a short `Interval` (every few seconds) | `j.AddCronJob<T>("*/5 * * * * *", new CronJobOptions { RetainSucceededFor = TimeSpan.Zero })`; see [work items and interval jobs](#work-items-and-interval-jobs) |
| `WorkItemHandlerBase.GetWorkItemLockAsync` (one per project at a time) | `EnqueueOptions.ConcurrencyKey`; see [work items and interval jobs](#work-items-and-interval-jobs) |
| Queue behaviors, duplicate detection | For jobs, `EnqueueOptions.DeduplicationKey`, the same contract as `DuplicateDetectionQueueBehavior`. For messages, `SendOptions.DeduplicationId` where the transport supports it (elsewhere a warning is logged), which drops repeats within a time window instead; otherwise idempotent handlers |
| `QueueEntryOptions.GroupId` | `SendOptions.GroupKey` |
| Tests that call `job.RunAsync()` / `RunUntilEmptyAsync()` and assert queue stats between steps | `AddFoundatioTestHarness(o => o.StartPaused = true)`, then `harness.ProcessPendingAsync<T>()`; see [tests](#tests) |
| `services.AddJobLifetimeService()` | Unchanged. It also counts message handlers, the job worker and the CRON scheduler as running work, so a process that runs only those keeps running |

## Step by step

1. Add `AddFoundatioMessaging(m => m.UseRedis())` next to your existing registrations. It uses the `IConnectionMultiplexer` you already register for Redis; for SQS or Kafka, see [Transports](./messaging#transports).
2. Keep your existing `IMessageBus` registration, such as `RedisMessageBus`, while you migrate. It runs side by side with the new messaging, and `HybridCacheClient`, `CacheLockProvider` and your subscribers keep using it unchanged. See [Moving IMessageBus to the new transport](#moving-imessagebus-to-the-new-transport) for the last step.
3. Move a queue: change its producers to `SendAsync`, turn its `QueueJobBase<T>` into an `IMessageHandler<T>`, and register it with `AddConsumer`. Drain the old queue before removing its job.
4. Move scheduled jobs: convert each to `IJobHandler` and replace `services.AddCronJob<T>(...)` with `j.AddCronJob<T>(...)`, or schedule it unchanged with `j.AddCronJob<LegacyJob<T>>(...)`. Give each job a stable `[Job(Name = "...")]`. Leave jobs that run continuously on `services.AddJob<T>()`.
5. Move work items: make each work item record the arguments of an `IJobHandler<TArgs>`, and enqueue with `IJobClient`.

Check concurrency as you move each queue. A `QueueJobBase<T>` handled one entry at a time per process; a handler handles up to `Environment.ProcessorCount` messages at once unless you set `HandlerOptions.MaxConcurrency`, and durable jobs share `JobWorkerOptions.MaxConcurrency` (also the processor count) unless the job type sets its own `MaxConcurrency`. Set `MaxConcurrency = 1` where the old job relied on running alone.

Message and job names default to the type's full name. Set stable names before your first deployment so later renames are safe.

Name each service with `m.UseServiceName("orders")`. Its durable subscriptions are kept under that name, so every service needs a name of its own, and on a real broker the host fails to start without one when the service subscribes to events.

## Work items and interval jobs

A work item becomes the arguments of an `IJobHandler<TArgs>`. What its handler or queue did around the work maps to enqueue and schedule options:

```csharp
// Before: one reset per project, held with a lock the work item job takes before it runs the handler
public override Task<ILock?> GetWorkItemLockAsync(object workItem, CancellationToken ct) =>
    _lockProvider.TryAcquireAsync($"{nameof(ResetProjectDataWorkItemHandler)}:{((ResetProjectDataWorkItem)workItem).ProjectId}", TimeSpan.FromMinutes(15), ct);

// After: the store runs one job of this name per key at a time; no lock, and a job that waits doesn't use an attempt
await jobs.EnqueueAsync(new ResetProjectDataWorkItem { OrganizationId = organizationId, ProjectId = projectId }, new EnqueueOptions { ConcurrencyKey = projectId });
```

- **`GetWorkItemLockAsync`** becomes `EnqueueOptions.ConcurrencyKey`: the key the lock was named after, without the handler's name, since keys are per job name already. A lock named only after the handler (one at a time across the cluster) becomes a constant key, such as `"all"`. A job that waits for its key stays queued without using an attempt, where a held lock abandoned the work item and spent a retry. Keys don't span job names; to keep two different jobs apart, keep the lock.
- **`DuplicateDetectionQueueBehavior`** becomes `EnqueueOptions.DeduplicationKey`, with the item's `UniqueIdentifier` as the key. As with the behavior, an enqueue while a job with the key is waiting returns that job's handle (the behavior discarded the item), and once the job starts, the next enqueue creates a new one. There is no detection window: the key is held until the job starts, however long it waits.
- **Interval jobs** (`[Job(Interval = "5s")]` or `services.AddJob<T>(o => o.Interval(...))`) become CRON jobs with a seconds field, with `CronJobOptions.RetainSucceededFor = TimeSpan.Zero` so each run's record is kept only as long as running each occurrence once needs. They then run once per occurrence across every instance instead of on every instance, and a run that overlaps the next occurrence makes it skip unless `AllowConcurrent` is set. Keep a job that should run continuously or on every instance on `services.AddJob<T>()`.

## Queue job outcomes

A `QueueJobBase<T>` returned a `JobResult`, and with `AutoComplete` (the default) completed the entry on success and abandoned it otherwise. A handler decides by returning or throwing:

| Before | After |
|---|---|
| `return JobResult.Success` / `SuccessWithMessage(...)` | Return. Report a message with `context.ReportProgressAsync(message: ...)` if you need one |
| `return JobResult.FailedWithMessage(...)` / `FromException(ex)` | Throw. The handler's `RetryPolicy` retries it, then dead-letters it |
| `return JobResult.Cancelled` / `CancelledWithMessage(...)` (abandoned, so retried) | Throw, or call `context.Retry()` and return |
| An exception thrown from the job | Unchanged: retried, then dead-lettered |
| `context.QueueEntry.MarkCompleted()` (completed without a result) | Return |
| `AutoComplete = false` with `entry.CompleteAsync()` | Return |
| `AutoComplete = false` with `entry.AbandonAsync()` | `context.Retry(delay)` and return, or throw |
| Completing or abandoning early, then doing more work | Do the work, then return; see [settlement follows the handler](#settlement-follows-the-handler) |
| A poison entry you want to drop | `context.DeadLetter("reason")` and return; it goes to the dead-letter queue without more attempts |
| `entry.RenewLockAsync()` | Remove it; the lease is renewed while the handler runs |
| `if (token.IsCancellationRequested) return JobResult.Cancelled;` | `token.ThrowIfCancellationRequested();`. **Returning completes the message**, so a handler that returns early at shutdown loses it |

`context.Retry()` and `context.DeadLetter()` count as an attempt, like a throw.

### Settlement follows the handler

The message is completed only after the handler returns, never while it runs. Code that used to follow `entry.CompleteAsync()` now runs before the message is settled, so a crash or a lost lease between your last side effect and the completion delivers the message again. Make each side effect safe to repeat: check whether it has happened (an archive file already moved, a row already written) before doing it, and treat "already done" as success rather than throwing, or the redelivery is retried and dead-lettered.

## Queue options

Queue options become `HandlerOptions` on the consumer, or the app-wide `MessagingOptions.DefaultRetryPolicy`:

```csharp
// Before: new RedisQueue<WorkItem>(o => o.Retries(2).RetryDelay(TimeSpan.FromMinutes(1)).WorkItemTimeout(TimeSpan.FromMinutes(5)))
m.AddConsumer<WorkItemHandler>(new HandlerOptions
{
    RetryPolicy = new RetryPolicy
    {
        MaxAttempts = 3,                                                // Retries = 2 plus the first attempt
        Delay = attempt => TimeSpan.FromMinutes(attempt switch { 1 => 1, 2 => 3, 3 => 5, _ => 10 })   // RetryDelay × RetryMultipliers
    },
    LeaseDuration = TimeSpan.FromMinutes(5),                            // WorkItemTimeout
    MaxConcurrency = 1                                                  // one entry at a time, like QueueJobBase
});
```

| Legacy option | v2 | Note |
|---|---|---|
| `Retries` (default 2) | `RetryPolicy.MaxAttempts` | **Off by one**: `Retries` counts retries, `MaxAttempts` counts every attempt, so `Retries = 2` is `MaxAttempts = 3`. The default goes from 3 attempts to 5 |
| `RetryDelay` (1 min) × `RetryMultipliers` (`{ 1, 3, 5, 10 }`) | `RetryPolicy.Delay` | The default waits about 1, 4, 16 and 64 s (×4, ±20% jitter, at most 10 min), so a poison message is dead-lettered after about 85 s instead of about 4 min. Use `RetryPolicy.Exponential(initial, max)`, `RetryPolicy.Fixed(delay)` or your own `Func<int, TimeSpan>` |
| `WorkItemTimeout` (5 min) | `HandlerOptions.LeaseDuration` (1 min) | Renewed while the handler runs, so it only bounds how long a crashed instance holds a message, not how long a handler may run |
| `Behaviors` such as `DuplicateDetectionQueueBehavior` | `SendOptions.DeduplicationId` | A time window, and only on transports that deduplicate. Work moved to a durable job gets the behavior's own contract from `EnqueueOptions.DeduplicationKey`; see [work items and interval jobs](#work-items-and-interval-jobs) |
| `GetDeadletterItemsAsync()` | `IMessageAdmin.PeekDeadLettersAsync(source)` | Entries already in the legacy dead-letter list stay there; drain or inspect them with the old queue |
| `GetQueueStatsAsync()` | `IMessageAdmin.GetStatsAsync(source)` | Use the old queue's stats to know when it is drained before you remove its job |

On SQS, an empty transport `Prefix` shares queue names with `SQSQueue<T>` and `SQSMessageBus`, so set a prefix while they run side by side.

## Mixing IMessageBus and IMessageClient

While you migrate, `IMessagePublisher`/`IMessageSubscriber` code you can't change yet, such as Foundatio.Repositories' `EntityChanged` notifications, can talk to `IMessageClient` code, when the bus is `m.AddLegacyMessageBus()` on the same transport. For a message type messaging knows, `IMessageBus.PublishAsync` also reaches `AddSubscriber` handlers and `IMessageClient.SubscribeAsync` subscribers, and `IMessageClient.PublishAsync` also reaches `IMessageBus.SubscribeAsync` subscribers. Each subscriber on either API receives a publish once.

Messaging knows a type when it is mapped with `m.MapMessage<T>("name")`, marked with `[Message("name")]`, or handled by a handler you register with `AddSubscriber`/`AddConsumer`. Other types, such as `HybridCacheClient`'s invalidations, stay on the legacy bus and cost nothing extra.

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseRedis()
    .UseServiceName("api")
    .MapMessage<EntityChanged>("entity-changed")   // a type you can't add [Message] to
    .AddSubscriber<EntityChangedHandler>()        // receives what Foundatio.Repositories publishes through IMessagePublisher
    .AddLegacyMessageBus());
```

- **Every service must know the type.** A service decides on its own: one that publishes `EntityChanged` through `IMessageBus` without knowing the type sends it only to the legacy topic, so another service's `AddSubscriber<EntityChangedHandler>` never sees it. Put the `MapMessage` calls in a registration every service shares.
- **Use the same name everywhere.** The `MapMessage` or `[Message]` name chooses the topic, so a service that maps the type to another name, or not at all, uses another topic.
- Only `AddLegacyMessageBus()` takes part. An original bus such as `RedisMessageBus` uses its own channels, and neither side receives the other's messages.
- A legacy subscriber of a base type, or of `IMessage`, receives what `IMessageClient` publishes only for types it subscribes to by name: it subscribes to the topic of the type it names.
- A legacy publish of a type messaging knows is sent twice, once to each side, so it costs a second send, and every process with `AddLegacyMessageBus()` receives the legacy copy.

Once the publishers you can't change are the last legacy code for a type, list it in `MigratedMessageTypes`. Its legacy publishes are then sent once, to its topic, as if published with `IMessageClient.PublishAsync`:

```csharp
m.MapMessage<EntityChanged>("entity-changed")
 .AddLegacyMessageBus(o => o.MigratedMessageTypes.Add(typeof(EntityChanged)));
```

- Legacy subscribers keep working: a subscriber of the type, of a base type or of `IMessage` receives it once, from its topic.
- Set it the same way in every process that shares the bus. A process that doesn't list the type still delivers it to its legacy subscribers of that exact type, when it knows the type, but not to its subscribers of a base type or of `IMessage`.
- It applies to the listed types only, not to types derived from them.

## Moving IMessageBus to the new transport

`m.AddLegacyMessageBus()` replaces your `IMessageBus` with one that runs on the new transport, so you can retire the original bus. It is not wire compatible with the original buses:
- it publishes to a transport topic with the new message headers;
- `RedisMessageBus`, `SQSMessageBus`, `KafkaMessageBus`, `AzureServiceBusMessageBus` and `RabbitMQMessageBus` use their own channels and formats;
- so a process on one never receives what a process on the other publishes.

Switch every process that shares the bus in the same deployment. That means every instance of each service, and every service that publishes or subscribes to the same messages. Use a recreate deployment rather than a rolling one, or accept a short window during the deployment:
- `HybridCacheClient` instances on different buses don't invalidate each other's local caches, so old and new instances can serve stale entries until those entries expire;
- `CacheLockProvider` waiters on different buses don't get each other's release signals, so they fall back to polling;
- your own subscribers miss messages published from the other side.

If you can't switch everything at once, keep the original bus. It is still supported.

## Converting an IJob or JobBase

A durable job without arguments implements `IJobHandler`. It gets an `IJobContext`, so a CRON job can see the occurrence it runs for, its attempt and its id, and report progress.

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
public class ArchiveOrdersJob(IOrderArchive archive) : IJobHandler
{
    public async Task HandleAsync(IJobContext context, CancellationToken cancellationToken)
    {
        int archived = await archive.ArchiveOlderThanAsync(context.ScheduledFor.AddDays(-30), cancellationToken);
        await context.ReportProgressAsync(100, $"Archived {archived} orders", cancellationToken);
    }
}
```

- Return to succeed, and report a result message with `ReportProgressAsync`. Throw instead of returning `JobResult.FromException` or `FailedWithMessage`; the job's `RetryPolicy` retries it.
- **Throw on cancellation, don't return.** A job that returns succeeds, even when its token fired: `if (ct.IsCancellationRequested) return JobResult.Cancelled;` becomes `ct.ThrowIfCancellationRequested();`. Thrown, a run cancelled with `CancelAsync` ends as `Cancelled`, and one interrupted by shutdown runs again later; returned, both are marked `Succeeded`, and a CRON occurrence interrupted by shutdown never runs.
- A legacy job's `[Job(Name = "...")]` with spaces, such as `"Cleanup Data"`, is rejected: job names allow only letters, digits, `.`, `_` and `-`. Without the attribute, a durable job is named after the type's full name, not the hosted runner's display name.
- Use `context.ScheduledFor` rather than the clock, so a late run or a retry processes the window it was scheduled for.
- Keep the `[Job(Name = "...")]`, so stored jobs and schedules keep their names.

To move a job before converting it, register the class unchanged with `j.AddLegacyJob<ArchiveOrdersJob>()`, enqueue it with `EnqueueAsync<LegacyJob<ArchiveOrdersJob>>()` and schedule it with `j.AddCronJob<LegacyJob<ArchiveOrdersJob>>("0 3 * * *")`. It keeps its name; a failed `JobResult` fails the attempt and is retried, and a successful result's message becomes the job's message.

## Tests

The test harness replaces stepping a job by hand. With `AddFoundatioTestHarness(o => o.StartPaused = true)`, nothing is handled until the test asks, and `ProcessNextAsync` and `ProcessPendingAsync` run what is pending through the real dispatcher and worker, so retries, dead letters and settlement behave as in production:

| Before | After |
|---|---|
| `await job.RunAsync()` (one queue entry) | `await harness.ProcessNextAsync<WorkItem>()`. What the handler sends back to that queue, and a retry, wait for the next call |
| `await job.RunUntilEmptyAsync()` | `await harness.ProcessPendingAsync<WorkItem>()`, or `ProcessPendingAsync()` for everything due, including what the handlers send |
| `Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued)` | `Assert.Single(harness.Sent<WorkItem>())` |
| `Assert.Equal(0, stats.Completed)` before running the job | `Assert.Empty(harness.HandledMessages)` while paused |
| `Assert.Equal(1, stats.Deadletter)` | `Assert.Single(harness.HandledMessages, m => m.Outcome == MessageOutcome.DeadLettered)`, with `o.RetryDelay = TimeSpan.Zero` so the retries run within one `ProcessPendingAsync` call, or one per `ProcessNextAsync` call |
| Resetting queues between tests | `harness.Reset()` |
| Calling a job's `RunAsync()` for a CRON job | `await harness.TriggerCronJobAsync("schedule-name")` |

Without `StartPaused`, handlers run on their own; wait for them with `harness.WaitForHandledAsync<T>()` or `harness.WaitForIdleAsync()`. `WaitForIdleAsync()` also works while paused: it runs everything and pauses again, so a base class shared by paused and running tests needs no branch. See [Testing](./messaging#testing) and [Running work step by step](./messaging#running-work-step-by-step).
