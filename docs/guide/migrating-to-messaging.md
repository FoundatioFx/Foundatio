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
| A queue processed by one service while others publish to it | `AddSubscriber<THandler>()`; each service gets its own copy |
| `WorkItemJob` + `WorkItemHandlerBase` with progress | `IJob<TArgs>` + `IJobClient.EnqueueAsync(args)`; progress via `IJobContext` |
| `JobWithLockBase` CRON jobs, `services.AddCronJob<T>(...)` | `AddFoundatioJobs(j => j.AddCronJob<T>("..."))`; one run per occurrence, no lock needed |
| `IJob` / `JobBase` | Unchanged; register with `AddJob<T>()` or `AddCronJob<T>()` |
| Queue behaviors, duplicate detection | `SendOptions.DeduplicationId` where the transport supports it; otherwise idempotent handlers |
| `QueueEntryOptions.GroupId` | `SendOptions.GroupKey` |

## Step by step

1. Add `AddFoundatioMessaging(m => m.UseRedis(...))` (or another provider) next to your existing registrations.
2. Add `m.AddLegacyMessageBus()` so code that injects `IMessageBus`, including `HybridCacheClient` and `CacheLockProvider`, uses the same transport.
3. Move a queue: change its producers to `SendAsync`, turn its `QueueJobBase<T>` into an `IMessageHandler<T>`, and register it with `AddConsumer`. Drain the old queue before removing its job.
4. Move scheduled jobs: replace `services.AddCronJob<T>(...)` with `j.AddCronJob<T>(...)`. Give each job a stable `[Job(Name = "...")]`.
5. Move work items: make each work item record the arguments of an `IJob<TArgs>`, and enqueue with `IJobClient`.

Message and job names default to the type's full name. Set stable names before your first deployment so later renames are safe.
