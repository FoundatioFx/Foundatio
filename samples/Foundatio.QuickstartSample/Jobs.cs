using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Foundatio.QuickstartSample;

/// <summary>Typed arguments for <see cref="ResizeImageJob"/>, serialized into the durable job payload.</summary>
public sealed record ResizeArgs(string FileName, int Width, int Height);

/// <summary>
/// A durable, on-demand job enqueued with <c>jobs.EnqueueAsync&lt;ResizeImageJob, ResizeArgs&gt;(args)</c>. It reads
/// its typed arguments back with <c>context.GetArguments&lt;ResizeArgs&gt;()</c> and reports progress through the
/// runtime store as it works.
/// </summary>
public sealed class ResizeImageJob(ILogger<ResizeImageJob> logger) : IJob
{
    public async Task<JobResult> RunAsync(JobExecutionContext context)
    {
        var args = context.GetArguments<ResizeArgs>();
        logger.LogInformation("JOB {JobId} started: resizing {FileName} to {Width}x{Height}", context.JobId, args.FileName, args.Width, args.Height);

        for (int percent = 25; percent <= 100; percent += 25)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), context.CancellationToken);
            await context.ReportProgressAsync(percent, $"{percent}% complete", context.CancellationToken);
            logger.LogInformation("JOB {JobId} progress: {Percent}%", context.JobId, percent);
        }

        return JobResult.SuccessWithMessage($"{args.FileName} resized to {args.Width}x{args.Height}");
    }
}

/// <summary>
/// A recurring (CRON) job registered with <c>AddCronJob&lt;CleanupJob&gt;("*/1 * * * *")</c> in Program.cs — the
/// scheduler materializes a durable occurrence every minute and the runtime pump executes it.
/// </summary>
public sealed class CleanupJob(ILogger<CleanupJob> logger) : IJob
{
    public Task<JobResult> RunAsync(JobExecutionContext context)
    {
        logger.LogInformation("CRON tick: cleanup ran at {Time:HH:mm:ss}", DateTimeOffset.Now);
        return Task.FromResult(JobResult.Success);
    }
}
