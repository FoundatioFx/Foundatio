using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public interface IQueueJob<T> : IJob where T : class
{
    /// <summary>
    /// Processes a queue entry and returns the result. This method is typically called from RunAsync()
    /// but can also be called from a function passing in the queue entry.
    /// </summary>
    Task<JobResult> ProcessAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken);
    IQueue<T> Queue { get; }
}

public static class QueueJobExtensions
{
    public static Task RunUntilEmptyAsync<T>(this IQueueJob<T> job, CancellationToken cancellationToken = default) where T : class
    {
        var logger = job.GetLogger();
        return job.RunContinuousAsync(cancellationToken: cancellationToken, continuationCallback: async () =>
        {
            // Allow abandoned items to be added in a background task.
            Thread.Yield();

            var stats = await job.Queue.GetQueueStatsAsync().AnyContext();
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("RunUntilEmpty continuation: Queued={Queued}, Working={Working}, Abandoned={Abandoned}", stats.Queued, stats.Working, stats.Abandoned);

            return stats.Queued + stats.Working > 0;
        });
    }
}
