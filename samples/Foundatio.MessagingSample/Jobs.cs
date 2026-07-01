using Foundatio.Jobs;

namespace Foundatio.MessagingSample;

/// <summary>
/// A durable, on-demand job (submitted via <c>POST /reports</c>). It runs on whichever instance's runtime pump claims
/// it, and reports progress through its <see cref="JobExecutionContext"/> so <c>GET /reports/{id}</c> can observe it.
/// </summary>
public sealed class GenerateReportJob(InstanceInfo instance, ILogger<GenerateReportJob> logger) : IJobWithExecutionContext
{
    public JobExecutionContext? ExecutionContext { get; set; }

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[{Instance}] generating report {JobId}", instance.Id, ExecutionContext?.JobId);

        for (int percent = 25; percent <= 100; percent += 25)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            if (ExecutionContext is { } context)
                await context.ReportProgressAsync(percent, $"{percent}% complete", cancellationToken);
        }

        return JobResult.Success;
    }
}

/// <summary>
/// A recurring job (see the CRON schedule wired in Program.cs). Every instance registers the same schedule, but the
/// shared runtime store dedupes each occurrence, so exactly one instance runs each tick.
/// </summary>
public sealed class HeartbeatJob(InstanceInfo instance, ILogger<HeartbeatJob> logger) : IJob
{
    public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[{Instance}] heartbeat {Time:HH:mm:ss}", instance.Id, DateTimeOffset.UtcNow);
        return Task.FromResult(JobResult.Success);
    }
}
