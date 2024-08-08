using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

public interface IJob
{
    Task<JobResult> RunAsync(CancellationToken cancellationToken = default);
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
    /// <returns>Returns the iteration count for normal jobs. For queue based jobs this will be the amount of items processed successfully.</returns>
    public static async Task<int> RunContinuousAsync(this IJob job, TimeSpan? interval = null, int iterationLimit = -1, CancellationToken cancellationToken = default, Func<Task<bool>> continuationCallback = null)
    {
        int iterations = 0;
        string jobName = job.GetType().Name;
        var logger = job.GetLogger();
        bool isInformationLogLevelEnabled = logger.IsEnabled(LogLevel.Information);

        int queueItemsProcessed = 0;
        bool isQueueJob = job.GetType().GetInterfaces().Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IQueueJob<>));

        using var _ = logger.BeginScope(new Dictionary<string, object> { { "job", jobName } });
        if (isInformationLogLevelEnabled)
            logger.LogInformation("Starting continuous job type {JobName} on machine {MachineName}...", jobName, Environment.MachineName);

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await job.TryRunAsync(cancellationToken).AnyContext();
            logger.LogJobResult(result, jobName);

            iterations++;
            if (isQueueJob && result.IsSuccess)
                queueItemsProcessed++;

            if (cancellationToken.IsCancellationRequested || (iterationLimit > -1 && iterationLimit <= iterations))
                break;

            if (result.Error != null)
            {
                await job.GetTimeProvider().SafeDelay(TimeSpan.FromMilliseconds(Math.Max((int)(interval?.TotalMilliseconds ?? 0), 100)), cancellationToken).AnyContext();
            }
            else if (interval.HasValue && interval.Value > TimeSpan.Zero)
            {
                await job.GetTimeProvider().SafeDelay(interval.Value, cancellationToken).AnyContext();
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
            catch (Exception ex) when (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "Error in continuation callback: {Message}", ex.Message);
            }
        }

        if (cancellationToken.IsCancellationRequested && logger.IsEnabled(LogLevel.Trace))
            logger.LogTrace("Job cancellation requested");

        if (isInformationLogLevelEnabled)
        {
            if (iterationLimit > 0)
            {
                logger.LogInformation(
                    "Stopping continuous job type {JobName} on machine {MachineName}: Job ran {Iterations} times (Limit={IterationLimit})",
                    jobName, Environment.MachineName, iterationLimit, iterations);
            }
            else
            {
                logger.LogInformation(
                    "Stopping continuous job type {JobName} on machine {MachineName}: Job ran {Iterations} times",
                    jobName, Environment.MachineName, iterations);
            }
        }

        return isQueueJob ? queueItemsProcessed : iterations;
    }
}
