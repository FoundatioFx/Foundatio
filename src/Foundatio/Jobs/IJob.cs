using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Jobs;

/// <summary>
/// Represents a unit of background work that can be executed once or continuously.
/// Implement this interface to create custom jobs for scheduled tasks, queue processing, or maintenance operations.
/// </summary>
public interface IJob
{
    /// <summary>
    /// Executes the job's work.
    /// </summary>
    /// <param name="cancellationToken">Token to signal that the job should stop.</param>
    /// <returns>A result indicating success, failure, or cancellation.</returns>
    Task<JobResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A durable job that wants its <see cref="JobExecutionContext"/> — job id, attempt number, and store-backed progress,
/// lease heartbeat, and cooperative cancellation checks. The durable runtime sets <see cref="ExecutionContext"/> on the
/// job instance before invoking it. Jobs that use the context should be registered as transient (a fresh instance per
/// run), since the context is per-run state, matching <see cref="Foundatio.Jobs.Legacy.IJobWithOptions"/>.
/// </summary>
public interface IJobWithExecutionContext : IJob
{
    JobExecutionContext? ExecutionContext { get; set; }
}

public static class JobExtensions
{
    public static async Task<JobResult> TryRunAsync(this IJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            return await job.RunAsync(cancellationToken).AnyContext();
        }
        catch (OperationCanceledException)
        {
            return JobResult.Cancelled;
        }
        catch (Exception ex)
        {
            return JobResult.FromException(ex);
        }
    }
}
