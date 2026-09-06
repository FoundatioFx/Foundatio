using System;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Jobs;

/// <summary>
/// Represents a unit of background work run by the durable job runtime. Every run is handed a
/// <see cref="JobExecutionContext"/> carrying its cancellation token, identity, attempt number, and store-backed
/// progress/heartbeat helpers — a job uses what it needs and ignores the rest.
/// </summary>
public interface IJob
{
    /// <summary>
    /// Executes the job's work.
    /// </summary>
    /// <param name="context">The execution context for this run (cancellation, identity, progress, heartbeat).</param>
    /// <returns>A result indicating success, failure, or cancellation.</returns>
    Task<JobResult> RunAsync(JobExecutionContext context);
}

/// <summary>A job whose required argument contract is checked when it is submitted.</summary>
public interface IJob<TArgs> : IJob where TArgs : class
{
    /// <summary>Executes with the deserialized arguments and the current execution context.</summary>
    Task<JobResult> RunAsync(TArgs arguments, JobExecutionContext context);

    Task<JobResult> IJob.RunAsync(JobExecutionContext context)
        => RunAsync(context.GetArguments<TArgs>(), context);
}

public static class JobExtensions
{
    /// <summary>
    /// Runs the job, converting cancellation and unhandled exceptions into a <see cref="JobResult"/> instead of throwing.
    /// </summary>
    public static async Task<JobResult> TryRunAsync(this IJob job, JobExecutionContext context)
    {
        try
        {
            return await job.RunAsync(context).AnyContext();
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return JobResult.Cancelled;
        }
        catch (Exception ex)
        {
            return JobResult.FromException(ex);
        }
    }
}
