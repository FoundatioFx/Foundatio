using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs {
    public interface IJob {
        Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken));
    }

    public static class JobExtensions {
        public static async Task<JobResult> TryRunAsync(this IJob job, CancellationToken cancellationToken = default(CancellationToken)) {
            try {
                return await job.RunAsync(cancellationToken).AnyContext();
            } catch (OperationCanceledException) {
                return JobResult.Cancelled;
            } catch (Exception ex) {
                return JobResult.FromException(ex);
            }
        }

        public static async Task RunContinuousAsync(this IJob job, TimeSpan? interval = null, int iterationLimit = -1, CancellationToken cancellationToken = default(CancellationToken), Func<Task<bool>> continuationCallback = null) {
            int iterations = 0;
            string jobName = job.GetType().Name;
            var logger = job.GetLogger() ?? NullLogger.Instance;

            using (logger.BeginScope(new Dictionary<string, object> {{ "job", jobName }})) {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Starting continuous job type {JobName} on machine {MachineName}...", jobName, Environment.MachineName);

                var sw = Stopwatch.StartNew();
                while (true) {
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();
                    LogResult(result, logger, jobName);
                    iterations++;

                    if (cancellationToken.IsCancellationRequested || (-1 < iterationLimit && iterationLimit <= iterations))
                       break;

                    // Maybe look into yeilding threads. task scheduler queue is starving.
                    if (result.Error != null) {
                        await SystemClock.SleepAsync(Math.Max((int)(interval?.TotalMilliseconds ?? 0), 100)).AnyContext();
                    } else if (interval.HasValue && interval.Value > TimeSpan.Zero) {
                        await SystemClock.SleepAsync(interval.Value).AnyContext();
                    } else if (sw.ElapsedMilliseconds > 5000) {
                        // allow for cancellation token to get set
                        Thread.Yield();
                        sw.Restart();
                    }

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    if (continuationCallback == null)
                        continue;

                    try {
                        if (!await continuationCallback().AnyContext())
                            break;
                    } catch (Exception ex) {
                        if (logger.IsEnabled(LogLevel.Error))
                            logger.LogError(ex, "Error in continuation callback: {Message}", ex.Message);
                    }
                }

                logger.LogInformation("Finished continuous job type {JobName}: {IterationLimit} {Iterations}", jobName, Environment.MachineName, iterationLimit, iterations);
                if (cancellationToken.IsCancellationRequested && logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("Job cancellation requested.");

                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Stopping continuous job type {JobName} on machine {MachineName}...", jobName, Environment.MachineName);
            }
        }

        internal static void LogResult(JobResult result, ILogger logger, string jobName) {
            if (result != null) {
                if (result.IsCancelled)
                    logger.LogWarning(result.Error, "Job run {JobName} cancelled: {Message}", jobName, result.Message);
                else if (!result.IsSuccess)
                    logger.LogError(result.Error, "Job run {JobName} failed: {Message}", jobName, result.Message);
                else if (!String.IsNullOrEmpty(result.Message))
                    logger.LogInformation("Job run {JobName} succeeded: {Message}", jobName, result.Message);
                else if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Job run {JobName} succeeded.", jobName);
            } else if (logger.IsEnabled(LogLevel.Error)) {
                logger.LogError("Null job run result for {JobName}.", jobName);
            }
        }
    }
}
