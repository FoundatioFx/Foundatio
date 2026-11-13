# Durable Jobs

Background work you can track: arguments, progress, cancellation, retries, delays and CRON schedules. Jobs are stored, so they survive restarts and run on whichever instance is free.

Use a job when you need status, progress, cancellation or a schedule. Otherwise a [message](./messages) is simpler.

## Quick start

```csharp
// Arguments are the job's contract. Producers only need this record.
[Job(Name = "reports.generate")]
public record GenerateReport(string Format, int Days);

public class GenerateReportJob(IReportService reports) : IJobHandler<GenerateReport>
{
    public async Task HandleAsync(GenerateReport args, IJobContext context, CancellationToken ct)
    {
        for (int day = 1; day <= args.Days; day++)
        {
            await reports.ProcessDayAsync(day, ct);
            await context.ReportProgressAsync(day * 100 / args.Days, $"Day {day} of {args.Days}", ct);
        }
    }
}

// A job without arguments, enqueued by its type or run on a CRON schedule.
[Job(Name = "reports.cleanup")]
public class CleanupJob(IReportService reports) : IJobHandler
{
    public Task HandleAsync(IJobContext context, CancellationToken ct) =>
        reports.DeleteOlderThanAsync(context.ScheduledFor.AddDays(-30), ct);
}

builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()                            // or UseRedis(); see Using Redis
    .AddJob<GenerateReportJob>()
    .AddCronJob<CleanupJob>("0 3 * * *"));    // 03:00 UTC daily

app.MapPost("/reports", async (IJobClient jobs) =>
{
    var job = await jobs.EnqueueAsync(new GenerateReport("pdf", 30));
    return Results.Accepted($"/reports/{job.Id}", new { job.Id });
});

app.MapGet("/reports/{id}", async (string id, IJobClient jobs) =>
    await jobs.GetAsync(id) is { } job ? Results.Ok(new { job.State, job.Progress, job.Message }) : Results.NotFound());
```

The worker and scheduler start with the host in any process that registers jobs. A process that only enqueues needs the argument records and the same store as the workers, such as `UseRedis()`. An in-memory store is private to its process, so jobs enqueued into it run only there.

A job with arguments is named after its arguments record, which is why `[Job(Name = ...)]` goes on `GenerateReport` rather than on the job class; putting it on the job class is rejected at startup. A job without arguments is named after its class, so its attribute goes on the class.

## Using Redis

`UseInMemory()` keeps jobs in the process, so they are lost when it stops and other instances never see them. To share jobs between instances and keep them across restarts, store them in Redis with `dotnet add package Foundatio.Redis`:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(await ConnectionMultiplexer.ConnectAsync("localhost"));
builder.Services.AddFoundatioJobs(j => j
    .UseRedis(new RedisJobStoreOptions { Prefix = "myapp:jobs" })   // or UseRedis() for the defaults
    .AddJob<GenerateReportJob>());
```

- `UseRedis()` uses the `IConnectionMultiplexer` registered in DI, and the host fails to start without one. To use another connection, call `UseRedis(connection, options)`; to build the options from configuration, pass a `Func<IServiceProvider, RedisJobStoreOptions>`. The connection stays yours.
- `RedisJobStoreOptions` is in `Foundatio.Jobs.Store`. Every process that enqueues or runs the same jobs must use the same `Prefix` and `Database`.
- The prefix is the store's hash tag, so on a cluster the whole store lives on one shard. Give each application its own prefix.

## Running a job

- **Return** to succeed. **Throw** to fail; the job is retried by its `RetryPolicy` (5 attempts with backoff by default).
- `IJobContext` gives the job its `JobId`, `Attempt`, `CorrelationId`, `Headers` and `ScheduledFor`, and `ReportProgressAsync` to report progress and a result message.
- The cancellation token fires when someone calls `CancelAsync`; the job ends as `Cancelled`. If it finishes anyway, the result counts.
- A job interrupted by shutdown is run again later and the interrupted attempt is not counted.
- The token also fires when the worker can no longer renew the job's lease, shortly before another worker could take it. That run's result is not recorded, and the job runs again.
- A store error while reporting progress doesn't fail the job; the report is retried with the next one.
- Jobs run at least once. Make them safe to repeat.
- A job is identified by the runtime type of its arguments, so arguments passed as `object` or a base type run the job for their actual type.

```csharp
j.AddJob<GenerateReportJob>(new JobTypeOptions
{
    MaxConcurrency = 2,                                         // per instance
    RetryPolicy = new RetryPolicy { MaxAttempts = 3 }.DoNotRetry<InvalidReportException>()
});
```

## Enqueue options

```csharp
await jobs.EnqueueAsync(new GenerateReport("csv", 7), new EnqueueOptions
{
    JobId = "report-2026-11",       // idempotent: enqueuing again returns the existing job
    RunAt = DateTimeOffset.UtcNow.AddHours(1),
    Headers = new Dictionary<string, string> { ["tenant"] = "acme" },   // the job reads them from context.Headers
});

var handle = await jobs.EnqueueAsync<CleanupJob>();   // a job without arguments
JobDetails done = await handle.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
```

`GetAsync`, `QueryAsync` and `WaitForCompletionAsync` return `JobDetails`: the job's state, attempt, progress, message, error, timestamps, correlation id and headers. It leaves out the claim token that fences workers and the serialized arguments, so it is safe to return from an API. `GetArguments<TArgs>()` reads the arguments back; it returns null for a job without arguments and for a succeeded job, whose arguments are dropped (see [Retention and memory](#retention-and-memory)).

```csharp
var job = await jobs.GetAsync(id);
var report = job?.GetArguments<GenerateReport>();   // null once the job has succeeded
```

`QueryAsync` lists jobs newest first by state and name; `CountAsync` counts them.

## Tracing and correlation

When an activity is current at enqueue, for example an ASP.NET Core request, a message handler or another job, its W3C trace context is stored with the job as the `traceparent` and `tracestate` headers. The job's `job <name>` activity continues that trace, however long the job waited, so a request and the report job it started show up as one trace. Set `traceparent` in `EnqueueOptions.Headers` to choose the parent yourself.

`CorrelationId` defaults to the correlation id of the message or job being handled, then to `Activity.Current.Id`, as the original queues do.

## CRON jobs

```csharp
j.AddCronJob<CleanupJob>("0 * * * *");                                  // hourly
j.AddCronJob("0 8 * * MON", new SendDigest("weekly"));                  // with arguments
j.AddCronJob<SyncJob>("*/30 * * * * *", new CronJobOptions              // six fields: seconds first
{
    TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"),
    AllowConcurrent = false,                       // default: skip while the previous run is unfinished
    MisfireThreshold = TimeSpan.FromMinutes(10)    // run a missed occurrence if an instance starts within this
});
```

Every instance runs the scheduler, and each occurrence becomes exactly one job, so there is no leader to elect. Schedules live in code: change or remove a line and redeploy.

A CRON job's `context.ScheduledFor` is the occurrence it runs for, even when it runs late after a misfire or a retry. Use it, not the clock, to choose the window to process, and as the key that makes a repeated run safe.

## Existing IJob and JobBase jobs

`AddJob<T>()` and `AddCronJob<T>()` take `IJobHandler` and `IJobHandler<TArgs>` classes, and reject a legacy `IJob`. To run an existing `IJob` or `JobBase` class as a durable job without changing it, wrap it in `LegacyJob<T>`:

```csharp
j.AddLegacyJob<QueueStatsJob>();                          // runs when enqueued
j.AddCronJob<LegacyJob<NightlyReportJob>>("0 2 * * *");   // runs on a schedule

await jobs.EnqueueAsync<LegacyJob<QueueStatsJob>>();
```

`LegacyJob<T>` keeps the job's name (its `[Job(Name = "...")]` or type name), so stored jobs and schedules keep theirs. A successful `JobResult`'s message becomes the job's message; a failed result fails the attempt, which the retry policy may retry; a cancelled result ends the job as cancelled when it was cancelled. A legacy job can't reach `IJobContext`, so [convert it](./migrating-to-messaging#converting-an-ijob-or-jobbase) when you can. A job that should run continuously rather than when enqueued stays on `services.AddJob<T>()` from Foundatio.Extensions.Hosting.

## Retention and memory

Finished jobs stay in the store so you can read their results: succeeded jobs for `JobWorkerOptions.RetainSucceededFor` (1 day by default), failed and cancelled jobs for `RetainFailedFor` (7 days). A succeeded job's arguments are dropped when it finishes; failed and cancelled jobs keep theirs so you can see what they were given.

Every retained job takes memory in the store. With a 55-character correlation id, a `traceparent` header and a short message, a succeeded job measured about 1.6 KB in Redis, a failed job with 200 bytes of arguments about 2 KB, and a job in the in-memory store about 1 KB. Longer messages, errors and headers add to that. Memory grows with rate × retention: at 10 jobs a second, a day of succeeded jobs is 864,000 jobs, about 1.4 GB in Redis; at 100 a second, about 14 GB. In Redis, all of a store's jobs live on one cluster shard, so size that shard for it. Shorten retention for high-volume jobs:

```csharp
j.Configure(o =>
{
    o.RetainSucceededFor = TimeSpan.FromHours(1);
    o.RetainFailedFor = TimeSpan.FromDays(3);
});
```

A CRON schedule's `MisfireThreshold` must stay shorter than both, or an occurrence could run twice; startup checks it.

## Startup

Workers and schedulers wait for Foundatio.Extensions.Hosting startup actions (such as migrations) to finish, and do not start if they fail. Implement `IStartupGate` to supply your own condition.

## Health checks

With Foundatio.Extensions.Hosting, report message processors, the job worker and the CRON scheduler to ASP.NET Core health checks. Use them for readiness: errors that last minutes are almost always a broker or store outage, and restarting the process does not fix one.

```csharp
builder.Services.AddHealthChecks()
    .AddFoundatioMessaging(tags: ["ready"])
    .AddFoundatioJobs(tags: ["ready"]);

app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
```

The host fails to start if you add a check without the matching `AddFoundatioMessaging` or `AddFoundatioJobs`.

| What is happening | Reported |
|---|---|
| Waiting for startup actions | Healthy, described as Starting |
| Receiving, claiming and scheduling | Healthy |
| Receive, claim or scheduling errors being retried with backoff | Degraded, with the last error in the description |
| Errors without a success for `unhealthyAfter` (default 5 minutes) | Unhealthy |
| A processor or the worker stopped unexpectedly, or startup failed | Unhealthy |

A handler or job that throws is not a health problem: it is retried, then dead-lettered or failed. The data has an entry per message source (`queue:orders`, `subscription:orders/billing`), for the `worker` (with running jobs per job name) and for the `scheduler`. Pass `failureStatus: HealthStatus.Degraded` to report problems without failing the probe (do this if a liveness probe uses the checks), and `name` to rename a check. Your own code can read the same information from `MessagingStatus` and `JobsStatus`.

## Testing

Unit test a job with `TestJobContext`:

```csharp
var context = new TestJobContext();
await new GenerateReportJob(fakeReports).HandleAsync(new GenerateReport("pdf", 2), context, CancellationToken.None);
Assert.Equal(100, context.Progress);
```

With [`AddFoundatioTestHarness()`](./messages#testing), jobs your app enqueues run on an in-memory store, and every finished run is recorded with its arguments, status, attempt and error:

```csharp
await jobs.EnqueueAsync(new GenerateReport("pdf", 2));
var run = Assert.Single(await harness.WaitForJobAsync<GenerateReport>());
Assert.Equal(JobState.Succeeded, run.State);

// CRON schedules only run when a test triggers them
await harness.TriggerCronJobAsync<CleanupJob>();
Assert.Equal(JobState.Succeeded, Assert.Single(await harness.WaitForJobAsync<CleanupJob>()).State);
```

`WaitForJobAsync` returns runs that finished (succeeded, failed or cancelled), so check `State`. It takes the arguments type, the job class or, for a legacy job, `LegacyJob<T>` or `T`. `WaitForIdleAsync` also waits for queued jobs that nothing in the app runs, and its timeout names them, so a forgotten `AddJob` fails the test instead of passing it. `JobRuns` also lists runs that will be retried, with `State` `Queued`. Schedules do not fire on their own under the harness: at startup the scheduler runs the latest occurrence within the misfire threshold, which would make tests depend on the time of day.
