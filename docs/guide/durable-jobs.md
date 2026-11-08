# Durable Jobs

Background work you can track: arguments, progress, cancellation, retries, delays and CRON schedules. Jobs are stored, so they survive restarts and run on whichever instance is free.

Use a job when you need status, progress, cancellation or a schedule. Otherwise a [message](./messages) is simpler.

## Quick start

```csharp
// Arguments are the job's contract. Producers only need this record.
[Job(Name = "reports.generate")]
public record GenerateReport(string Format, int Days);

public class GenerateReportJob(IReportService reports) : IJob<GenerateReport>
{
    public async Task RunAsync(GenerateReport args, IJobContext context, CancellationToken ct)
    {
        for (int day = 1; day <= args.Days; day++)
        {
            await reports.ProcessDayAsync(day, ct);
            await context.ReportProgressAsync(day * 100 / args.Days, $"Day {day} of {args.Days}", ct);
        }
    }
}

builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()                            // or a provider, e.g. UseRedis(...)
    .AddJob<GenerateReportJob>()
    .AddCronJob<CleanupJob>("0 3 * * *"));    // 03:00 UTC daily

app.MapPost("/reports", async (IJobClient jobs) =>
{
    var job = await jobs.EnqueueAsync(new GenerateReport("pdf", 30));
    return Results.Accepted($"/reports/{job.Id}", new { job.Id });
});

app.MapGet("/reports/{id}", async (string id, IJobClient jobs) =>
    await jobs.GetAsync(id) is { } job ? Results.Ok(new { job.Status, job.Progress, job.Message }) : Results.NotFound());
```

The worker and scheduler start with the host in any process that registers jobs. A process that only enqueues just needs `UseInMemory()` (or a provider) and the argument records.

## Running a job

- **Return** to succeed. **Throw** to fail; the job is retried by its `RetryPolicy` (5 attempts with backoff by default).
- The cancellation token fires when someone calls `CancelAsync`; the job ends as `Cancelled`. If it finishes anyway, the result counts.
- A job interrupted by shutdown is run again later and the interrupted attempt is not counted.
- Jobs run at least once. Make them safe to repeat.

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
});

var handle = await jobs.EnqueueAsync<CleanupJob>();   // a job without arguments
JobInfo done = await handle.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
```

`QueryAsync` lists jobs newest first by status and name; `CountAsync` counts them. Finished jobs are kept for a day (failures for a week); see `JobsOptions`.

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

Existing `IJob` and `JobBase` classes work as CRON jobs and on-demand jobs without changes. `[Job(Name = "...")]` gives them a stable name.

## Startup

Workers and schedulers wait for Foundatio.Extensions.Hosting startup actions (such as migrations) to finish, and do not start if they fail. Implement `IStartupGate` to supply your own condition.

## Testing jobs

```csharp
var context = new TestJobContext();
await new GenerateReportJob(fakeReports).RunAsync(new GenerateReport("pdf", 2), context, CancellationToken.None);
Assert.Equal(100, context.Progress);
```
