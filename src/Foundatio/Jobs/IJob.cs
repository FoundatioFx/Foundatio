using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

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
/// A job that exposes configurable options for execution behavior.
/// </summary>
public interface IJobWithOptions : IJob
{
    /// <summary>
    /// Gets or sets the options controlling job execution (name, interval, iteration limit).
    /// </summary>
    JobOptions Options { get; set; }
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

    /// <summary>
    /// Runs the job continuously until the cancellation token is set or the iteration limit is reached.
    /// </summary>
    /// <returns>Returns the iteration count for normal jobs. For queue-based jobs this will be the number of items processed successfully.</returns>
    public static Task<int> RunContinuousAsync(this IJob job, TimeSpan? interval = null, int iterationLimit = -1,
        CancellationToken cancellationToken = default, Func<Task<bool>> continuationCallback = null)
    {
        var options = JobOptions.GetDefaults(job);
        options.Interval = interval;
        options.IterationLimit = iterationLimit;
        return RunContinuousAsync(job, options, cancellationToken, continuationCallback);
    }

    /// <summary>
    /// Runs the job continuously until the cancellation token is set or the iteration limit is reached.
    /// </summary>
    /// <returns>Returns the iteration count for normal jobs. For queue based jobs this will be the amount of items processed successfully.</returns>
    public static async Task<int> RunContinuousAsync(this IJob job, JobOptions options, CancellationToken cancellationToken = default, Func<Task<bool>> continuationCallback = null)
    {
        int iterations = 0;
        var logger = job.GetLogger();

        int queueItemsProcessed = 0;
        bool isQueueJob = job.GetType().GetInterfaces().Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IQueueJob<>));

        string jobId = Guid.NewGuid().ToString("N").Substring(0, 10);
        using var jobScope = logger.BeginScope(s => s.Property("job.name", options.Name).Property("job.id", jobId));
        logger.LogInformation("Starting continuous job type {JobName} on machine {MachineName}...", options.Name, Environment.MachineName);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var activity = FoundatioDiagnostics.ActivitySource.StartActivity($"Job: {options.Name}");

            string jobRunId = Guid.NewGuid().ToString("N").Substring(0, 10);
            using var _ = logger.BeginScope(s => s.Property("job.run_id", jobRunId));
            var result = await job.TryRunAsync(cancellationToken).AnyContext();
            logger.LogJobResult(result, options.Name);

            iterations++;
            if (isQueueJob && result.IsSuccess)
                queueItemsProcessed++;

            if (cancellationToken.IsCancellationRequested || (options.IterationLimit > -1 && options.IterationLimit <= iterations))
                break;

            if (result.Error != null)
            {
                await job.GetTimeProvider().SafeDelay(TimeSpan.FromMilliseconds(Math.Max((int)(options.Interval?.TotalMilliseconds ?? 0), 100)), cancellationToken).AnyContext();
            }
            else if (options.Interval.HasValue && options.Interval.Value > TimeSpan.Zero)
            {
                await job.GetTimeProvider().SafeDelay(options.Interval.Value, cancellationToken).AnyContext();
            }

            // needed to yield back a task for jobs that aren't async
            await Task.Yield();

            if (cancellationToken.IsCancellationRequested)
                break;

            if (continuationCallback == null)
                continue;

            try
            {
                if (!await continuationCallback().AnyContext())
                    break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in continuation callback: {Message}", ex.Message);
            }
        }

        if (cancellationToken.IsCancellationRequested)
            logger.LogTrace("Job cancellation requested");

        if (options.IterationLimit > 0)
        {
            logger.LogInformation(
                "Stopping continuous job type {JobName} on machine {MachineName}: Job ran {Iterations} times (Limit={IterationLimit})",
                options.Name, Environment.MachineName, iterations, options.IterationLimit);
        }
        else
        {
            logger.LogInformation(
                "Stopping continuous job type {JobName} on machine {MachineName}: Job ran {Iterations} times",
                options.Name, Environment.MachineName, iterations);
        }

        return isQueueJob ? queueItemsProcessed : iterations;
    }
}
