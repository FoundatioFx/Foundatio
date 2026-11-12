using Foundatio.Jobs;

namespace Foundatio.QuickstartSample;

/// <summary>A durable job with arguments, progress and cancellation. Throwing retries it with backoff.</summary>
public sealed class GenerateReportJob(ILogger<GenerateReportJob> logger) : IDurableJob<GenerateReport>
{
    public async Task RunAsync(GenerateReport report, IJobContext context, CancellationToken cancellationToken)
    {
        for (int day = 1; day <= report.Days; day++)
        {
            await Task.Delay(100, cancellationToken);
            await context.ReportProgressAsync(day * 100 / report.Days, $"Processed day {day} of {report.Days}", cancellationToken);
        }

        logger.LogInformation("Report {JobId} generated as {Format}", context.JobId, report.Format);
        await context.ReportProgressAsync(100, $"/reports/{context.JobId}.{report.Format}", cancellationToken);
    }
}

/// <summary>A recurring job. It runs once per CRON occurrence across all instances.</summary>
[Job(Name = "maintenance.cleanup")]
public sealed class CleanupJob(SampleActivity activity, ILogger<CleanupJob> logger) : IJob
{
    public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Cleanup ran");
        activity.CleanupRan.TrySetResult();
        return Task.FromResult(JobResult.Success);
    }
}

public sealed class SampleActivity
{
    public TaskCompletionSource ReceiptSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupRan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
