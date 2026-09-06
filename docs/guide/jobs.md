# Durable jobs

Use a message consumer for ordinary worker-queue processing. Use durable jobs when callers need a handle, progress, cancellation, persisted retries, or CRON scheduling. Ad hoc jobs and scheduled occurrences use the same execution state machine.

## Start a job worker

```csharp
using Foundatio;
using Foundatio.Jobs;

builder.Services.AddFoundatioWorker(foundatio => foundatio
    .ConfigureJobs(jobs => jobs.UseInMemory()
        .AddJobType<ResizeImageJob>("resize-image.v1")
        .AddCronJob<CleanupJob>("0 2 * * *")));
```

The in-memory store is for tests and local development. Use `.Jobs.UseRedis()` for persistence across processes, with Redis persistence and availability configured for your requirements.

`AddFoundatioWorker(..., jobConcurrency: 4)` runs up to four jobs concurrently. Its scheduler runs independently. For a producer-only API, use `AddFoundatio()` to register the same store and job types. For separate scheduler and worker processes, use the [individual hosting methods](dependency-injection.md#choose-host-roles-explicitly).

## Typed arguments and handles

```csharp
public sealed record ResizeArgs(string File, int Width);

public sealed class ResizeImageJob(ImageService images) : IJob<ResizeArgs>
{
    public async Task<JobResult> RunAsync(ResizeArgs arguments, JobExecutionContext context)
    {
        await context.ReportProgressAsync(10, "Reading image");
        await images.ResizeAsync(arguments.File, arguments.Width, context.CancellationToken);
        return JobResult.Success;
    }
}

var handle = await jobs.EnqueueAsync<ResizeImageJob, ResizeArgs>(new ResizeArgs("image.png", 640));
var state = await handle.WaitForCompletionAsync(TimeSpan.FromMinutes(2));
Console.WriteLine(state.ResultMessage);

var delayed = await jobs.EnqueueAsync<ResizeImageJob, ResizeArgs>(
    new ResizeArgs("later.png", 640), new JobRequestOptions { Delay = TimeSpan.FromHours(1) });
await delayed.RequestCancellationAsync();
```

`Delay` and `RunAt` are mutually exclusive. `WaitForCompletionAsync` defaults to a five-minute timeout and polls every 250 ms. Timeout or cancellation stops the wait; cancelling execution requires `RequestCancellationAsync`.

The argument type is part of `IJob<TArgs>` and is checked before persistence. Argument-free jobs implement `IJob.RunAsync(JobExecutionContext)`. A typed job cannot be submitted without its arguments. Register stable, versioned job names on both submitters and workers; only allowlisted job types execute. Keep the serialized argument contract compatible for as long as old jobs can remain queued or retained.

Each execution receives a dependency injection scope, its application job ID, attempt number, and cancellation token. Workers renew leases automatically and poll for cancellation. Progress updates, renewal, and completion require the current unexpired claim token. Restarting with the same node name does not confer ownership of a previous execution.

## Execution and retries

Jobs progress from queued to processing to completed, failed, or cancelled. A failed attempt returns to the queue with a persisted `JobRetryPolicy`: initially 10 seconds, exponential multiplier 2, a five-minute cap, and 20% jitter. Set `JobRequestOptions.RetryPolicy` or `CronJobOptions.RetryPolicy` to change it. The policy is stored with the job so different workers apply the same curve. Return a failed `JobResult` with `Retryable = false` for a terminal business failure. `JobRequestOptions.MaxAttempts` defaults to three total attempts, including crash recovery; CRON definitions snapshot the same budget into each occurrence. Exhausted jobs end in `Failed` with their error retained.

An expired processing lease can be claimed with a fresh token. Host shutdown returns unfinished work to the queue; explicit user cancellation is terminal. A worker that loses its lease cannot complete or report progress against the replacement claim.

These fences protect job state, not arbitrary external side effects. A process can crash after completing an external operation but before persisting completion. Jobs must tolerate repeated execution. A stable caller-supplied `JobRequestOptions.JobId` makes submission create-if-absent until its idempotency reservation expires; it does not make execution exactly once.

Workers atomically claim the oldest eligible due job from registered types and optional node affinity. Monitoring queries do not drive execution, so old or unknown job types cannot crowd runnable work out of a monitoring page.

## CRON schedules

```csharp
builder.Services.AddFoundatioWorker(foundatio => foundatio
    .ConfigureJobs(jobs => jobs.UseInMemory()
    .AddJobType<ResizeImageJob>("resize-image.v1")
    .AddCronJob<ResizeImageJob, ResizeArgs>("0 2 * * *", new ResizeArgs("banner.png", 640), o =>
    {
        o.Name = "resize-banner";
        o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        o.ConfigurationVersion = 1;
    })));
```

Five-field expressions use minute resolution; six-field expressions include seconds. Definitions persist a wire job name, serialized argument payload, time-zone ID, retry budget, enabled state, scope, overlap policy, and revision. They contain no CLR `Type`, delegates, or live argument objects.

Global schedules create one occurrence per tick across scheduler replicas. `PerNode` creates occurrences with affinity to each scheduler node; the worker on that node must use the same node identity and register the job type. `PerNode` requires an explicit stable `Jobs.ConfigureWorker(o => o with { NodeId = "worker-a" })` or `FOUNDATIO_NODE_ID`; startup fails if neither is configured. Use a unique stable identity per node. Global is the default and needs no node configuration. Unclaimed node-affine occurrences expire after `UnclaimedLifetime` (default one day), releasing backlog and overlap reservations when a node is retired. Once an attempt starts, ordinary retry and lease recovery apply.

`SkipIfRunning` prevents a new occurrence while an earlier occurrence remains queued, processing, or waiting for retry. Explicitly allowing overlap permits concurrent occurrences. Unique occurrence IDs prevent duplicate materialization across concurrent scheduler polls. Manual triggers also respect overlap and disabled state.

The misfire window defaults to one minute and is limited to one day. Only ticks inside that window are caught up; this is not a promise to replay every tick after an unlimited outage. Keep high-frequency catch-up windows small. A skipped overlapping tick can be considered again while it remains in the window.

### Persisted edits and deployment reconciliation

`IScheduledJobManager` lists, inspects, reschedules, enables/disables, removes, and manually triggers schedules. `TriggerAsync(name)` returns a durable job handle. `ScheduleAsync<TJob, TArgs>` creates a typed runtime schedule.

Updates to `ScheduledJobDefinition` use its `Revision`; a stale update fails rather than silently replacing another operator's edit. Declarative configuration has a separate `ConfigurationVersion`. Restarting the same deployment preserves runtime edits. Changing a declaration requires increasing that configuration version; older deployments cannot overwrite newer definitions. An intentional higher version applies the new declaration and advances the stored revision.

Disabling or removing a schedule stops future materialization; already queued occurrences remain independent jobs. Cancel those explicitly if needed.

## Monitoring, retention, and capacity

`IJobMonitor.GetAsync(id)` retrieves a job. `QueryAsync(JobQuery)` returns a `JobPage`, ordered by job ID, with optional name/status filters. Limits range from 1 to 1,000 and default to 100. Continue using the returned token and the same filters:

```csharp
string? cursor = null;
do
{
    var page = await monitor.QueryAsync(new JobQuery { Status = JobStatus.Failed, AfterJobId = cursor });
    foreach (var job in page)
        Console.WriteLine($"{job.JobId}: {job.Error}");
    cursor = page.ContinuationToken;
} while (cursor is not null);
```

Redis reads bounded index pages rather than loading every job. A filtered page can be empty and still have a continuation token. Pages are a live view; concurrent inserts or status changes are not a snapshot.

`JobRuntimeStoreOptions` separates admission, history and idempotency budgets:

| Option | Default |
| --- | --- |
| `MaxActiveJobs` | 100,000 queued, scheduled or processing jobs |
| `MaxHistoryJobs` / `HistoryRetention` | 100,000 terminal records / seven days |
| `MaxDeduplicationRecords` / `DeduplicationRetention` | 1,000,000 ID reservations / seven days after completion |
| `MaxScheduledDispatches` | 100,000 delayed messages |
| `MaxPayloadBytes` | 1 MiB per job payload or scheduled message body plus UTF-8 header keys/values |

History is evicted by age or count independently of active capacity. Eviction preserves the ID reservation until its deduplication deadline; active jobs reserve IDs until they become terminal. Deduplication retention must be at least history retention. Admission fails with an actionable `JobException` when an applicable budget is full. Neither eviction nor a supplied ID makes business side effects exactly once.

Pass these options to `Jobs.UseInMemory(options)` or `RedisJobRuntimeStoreOptions.Runtime`. The old `MaxJobs` setting now means active capacity. Hosted workers clean up automatically; manual hosts call `CleanupAsync`. `GetStatsAsync` exposes current budget usage.

`AddFoundatioWorker` registers a `foundatio` health check. Map it with ASP.NET Core's `app.MapHealthChecks("/health")` or query `HealthCheckService`. Worker, scheduler, dispatcher and listener recovery affect health. The `Foundatio.Runtime` meter exposes active jobs, retained history, ID reservations and scheduled dispatch counts. Job exceptions are logged with ID, wire type and attempt, with a bounded failure summary in `JobState.Error`; successful messages use `ResultMessage`.

## Testing and migration

`Foundatio.Testing.JobsTestHarness` runs the real worker/scheduler with in-memory state and a controlled clock. `RunAllQueuedAsync()` drains currently eligible work across batches; future delayed jobs remain queued. `RunToCompletionAsync(handle)` runs only that job. `RunDueAsync()` materializes due CRON occurrences and drains eligible jobs; it does not dispatch scheduled messages. Shared `JobRuntimeStoreConformanceTests` cover ownership fencing, eligibility, concurrency, retries, cancellation, schedule revisions, dispatch recovery, pagination, and retention against memory and Redis.

Old `JobBase`, `QueueJobBase<T>`, `JobWithLockBase`, `JobRunner`, and `WorkItemJob` implementations migrate to plain `IJob` or `IMessageHandler<T>`. Replace `RunAsync(CancellationToken)` with `RunAsync(JobExecutionContext)`, queue jobs with explicit message consumers, and work-item payloads with `IJob<TArgs>`. The old automatic runtime pump and generic state-patch store API are removed; host roles and ownership-specific store operations are explicit.
