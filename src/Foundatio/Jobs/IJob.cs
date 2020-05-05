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
        Task<JobResult> RunAsync(CancellationToken cancellationToken = default);
    }

    public static class JobExtensions {
        public static async Task<JobResult> TryRunAsync(this IJob job, CancellationToken cancellationToken = default) {
            try {
                return await job.RunAsync(cancellationToken).AnyContext();
            } catch (OperationCanceledException) {
                return JobResult.Cancelled;
            } catch (Exception ex) {
                return JobResult.FromException(ex);
            }
        }

        public static async Task RunContinuousAsync(this IJob job, TimeSpan? interval = null, int iterationLimit = -1, CancellationToken cancellationToken = default, Func<Task<bool>> continuationCallback = null) {
            int iterations = 0;
            string jobName = job.GetType().Name;
            var logger = job.GetLogger();

            using (logger.BeginScope(new Dictionary<string, object> {{ "job", jobName }})) {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Starting continuous job type {JobName} on machine {MachineName}...", jobName, Environment.MachineName);

                var sw = Stopwatch.StartNew();
                while (!cancellationToken.IsCancellationRequested) {
                    var result = await job.TryRunAsync(cancellationToken).AnyContext();
                    logger.LogJobResult(result, jobName);
                    iterations++;

                    if (cancellationToken.IsCancellationRequested || (iterationLimit > -1 && iterationLimit <= iterations))
                       break;

                    // Maybe look into yielding threads. Task scheduler queue is starving.
                    if (result.Error != null) {
                        await SystemClock.SleepSafeAsync(Math.Max((int)(interval?.TotalMilliseconds ?? 0), 100), cancellationToken).AnyContext();
                    } else if (interval.HasValue && interval.Value > TimeSpan.Zero) {
                        await SystemClock.SleepSafeAsync(interval.Value, cancellationToken).AnyContext();
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
    }
}
