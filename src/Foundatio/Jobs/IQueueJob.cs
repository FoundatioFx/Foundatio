using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs {
    public interface IQueueJob<T> : IJob where T : class {
        /// <summary>
        /// Processes a queue entry and returns the result. This method is typically called from RunAsync() 
        /// but can also be called from a function passing in the queue entry.
        /// </summary>
        Task<JobResult> ProcessAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken);
        IQueue<T> Queue { get; }
    }

    public static class QueueJobExtensions {
        public static void RunUntilEmpty<T>(this IQueueJob<T> job, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            var logger = job.GetLogger() ?? NullLogger.Instance;
            job.RunContinuous(cancellationToken: cancellationToken, continuationCallback: async () => {
                // Allow abandoned items to be added in a background task.
                await Task.Yield();

                var stats = await job.Queue.GetQueueStatsAsync().AnyContext();
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("RunUntilEmpty continuation: Queued={Queued}, Working={Working}, Abandoned={Abandoned}", stats.Queued, stats.Working, stats.Abandoned);
                return stats.Queued + stats.Working > 0;
            });
        }
    }
}