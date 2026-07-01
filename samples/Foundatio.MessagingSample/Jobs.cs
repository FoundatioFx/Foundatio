using Foundatio.Jobs;

namespace Foundatio.MessagingSample;

/// <summary>
/// A durable, on-demand job (submitted via <c>POST /reports</c>). It runs on whichever instance's runtime pump claims
/// it, and reports progress through its <see cref="JobExecutionContext"/> so <c>GET /reports/{id}</c> can observe it.
/// </summary>
public sealed class GenerateReportJob(InstanceInfo instance, ILogger<GenerateReportJob> logger) : IJob
{
    public async Task<JobResult> RunAsync(JobExecutionContext context)
    {
        logger.LogInformation("[{Instance}] generating report {JobId}", instance.Id, context.JobId);

        for (int percent = 25; percent <= 100; percent += 25)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), context.CancellationToken);
            await context.ReportProgressAsync(percent, $"{percent}% complete", context.CancellationToken);
        }

        return JobResult.Success;
    }
}

// The recurring (CRON) jobs below are scheduled in Program.cs. Every instance registers the same schedules, but each
// occurrence is materialized once into the shared runtime store, so scope decides how many instances run it:
//   * Global  (default) -> exactly ONE instance runs each tick (a leader/singleton task).
//   * PerNode           -> EVERY instance runs its own occurrence each tick (per-instance maintenance).
// A job uses its JobExecutionContext when it wants progress/heartbeat/identity, or ignores it (as these do).

/// <summary>Global, every minute: a simple liveness heartbeat that runs on a single instance per tick.</summary>
public sealed class HeartbeatJob(InstanceInfo instance, ILogger<HeartbeatJob> logger) : IJob
{
    public Task<JobResult> RunAsync(JobExecutionContext context)
    {
        logger.LogInformation("[{Instance}] heartbeat {Time:HH:mm:ss} (one instance per tick)", instance.Id, DateTimeOffset.UtcNow);
        return Task.FromResult(JobResult.Success);
    }
}

/// <summary>PerNode, every minute: each instance refreshes its own local state — so every instance runs this each tick.</summary>
public sealed class RefreshCacheJob(InstanceInfo instance, ILogger<RefreshCacheJob> logger) : IJob
{
    public Task<JobResult> RunAsync(JobExecutionContext context)
    {
        logger.LogInformation("[{Instance}] refreshed local cache (every instance per tick)", instance.Id);
        return Task.FromResult(JobResult.Success);
    }
}

/// <summary>Global, every 2 minutes: a periodic maintenance sweep that runs on a single instance per tick.</summary>
public sealed class SweepStaleOrdersJob(InstanceInfo instance, ILogger<SweepStaleOrdersJob> logger) : IJob
{
    public Task<JobResult> RunAsync(JobExecutionContext context)
    {
        logger.LogInformation("[{Instance}] swept stale orders (one instance per tick)", instance.Id);
        return Task.FromResult(JobResult.Success);
    }
}
