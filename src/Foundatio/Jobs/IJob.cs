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
                logger.LogInformation("Starting continuous job type \"{0}\" on machine \"{1}\"...", jobName, Environment.MachineName);

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
                        logger.LogError(ex, "Error in continuation callback: {0}", ex.Message);
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                    logger.LogTrace("Job cancellation requested.");

                logger.LogInformation("Stopping continuous job type \"{0}\" on machine \"{1}\"...", jobName, Environment.MachineName);
            }
        }

        internal static void LogResult(JobResult result, ILogger logger, string jobName) {
            if (result != null) {
                if (result.IsCancelled)
                    logger.LogWarning(result.Error, "Job run \"{0}\" cancelled: {1}", jobName, result.Message);
                else if (!result.IsSuccess)
                    logger.LogError(result.Error, "Job run \"{0}\" failed: {1}", jobName, result.Message);
                else if (!String.IsNullOrEmpty(result.Message))
                    logger.LogInformation("Job run \"{0}\" succeeded: {1}", jobName, result.Message);
                else
                    logger.LogTrace("Job run \"{0}\" succeeded.", jobName);
            } else {
                logger.LogError("Null job run result for \"{0}\".", jobName);
            }
        }
    }
}