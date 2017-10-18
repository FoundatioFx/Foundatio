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
        public static JobResult TryRun(this IJob job, CancellationToken cancellationToken = default(CancellationToken)) {
            try {
                return job.RunAsync(cancellationToken).GetAwaiter().GetResult();
            } catch (Exception ex) {
                return JobResult.FromException(ex);
            }
        }

        public static void RunContinuous(this IJob job, TimeSpan? interval = null, int iterationLimit = -1, CancellationToken cancellationToken = default(CancellationToken), Func<Task<bool>> continuationCallback = null) {
            int iterations = 0;
            string jobName = job.GetType().Name;
            var logger = job.GetLogger() ?? NullLogger.Instance;

            using (logger.BeginScope(new Dictionary<string, object> {{ "job", jobName }})) {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Starting continuous job type {JobName} on machine {MachineName}...", jobName, Environment.MachineName);

                var sw = Stopwatch.StartNew();
                while (!cancellationToken.IsCancellationRequested && (iterationLimit < 0 || iterations < iterationLimit)) {
                    var result = job.TryRun(cancellationToken);
                    LogResult(result, logger, jobName);
                    iterations++;

                    // Maybe look into yeilding threads. task scheduler queue is starving.
                    if (result.Error != null) {
                        SystemClock.Sleep(Math.Max(interval?.Milliseconds ?? 0, 100));
                    } else if (interval.HasValue) {
                        SystemClock.Sleep(interval.Value);
                    } else if (sw.ElapsedMilliseconds > 5000) {
                        // allow for cancellation token to get set
                        Thread.Yield();
                        sw.Restart();
                    }

                    if (continuationCallback == null || cancellationToken.IsCancellationRequested)
                        continue;

                    try {
                        if (!continuationCallback().GetAwaiter().GetResult())
                            break;
                    } catch (Exception ex) {
                        if (logger.IsEnabled(LogLevel.Error))
                            logger.LogError(ex, "Error in continuation callback: {Message}", ex.Message);
                    }
                }

                if (cancellationToken.IsCancellationRequested && logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("Job cancellation requested.");

                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Stopping continuous job type {JobName} on machine {MachineName}...", jobName, Environment.MachineName);
            }
        }

        internal static void LogResult(JobResult result, ILogger logger, string jobName) {
            if (result != null) {
                if (result.IsCancelled)
                    if (logger.IsEnabled(LogLevel.Warning))
                        logger.LogWarning(result.Error, "Job run {JobName} cancelled: {Message}", jobName, result.Message);
                else if (!result.IsSuccess)
                    if (logger.IsEnabled(LogLevel.Error))
                        logger.LogError(result.Error, "Job run {JobName} failed: {Message}", jobName, result.Message);
                else if (!String.IsNullOrEmpty(result.Message))
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Job run {JobName} succeeded: {Message}", jobName, result.Message);
                else if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("Job run {JobName} succeeded.", jobName);
            } else if (logger.IsEnabled(LogLevel.Error)) {
                    logger.LogError("Null job run result for {JobName}.", jobName);
            }
        }
    }
}