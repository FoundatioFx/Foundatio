using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

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
    /// <summary>
    /// Will run wait for the wait timeout to expire waiting if there are no queued items. It will then run until the queue is empty.
    /// NOTE: The wait timeout will not be reset until after the first job is processed.
    /// </summary>
    public static async Task RunUntilEmptyAsync<T>(this IQueueJob<T> job, TimeSpan waitTimeout,
        CancellationToken cancellationToken = default) where T : class
    {
        if (waitTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Acquire timeout must be greater than zero", nameof(waitTimeout));

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellationTokenSource.CancelAfter(waitTimeout);
        bool hasAcquireTimeout = true;

        var logger = job.GetLogger();

        // NOTE: This has to be awaited otherwise the linkedCancellationTokenSource cancel timer will not fire.
        await job.RunContinuousAsync(cancellationToken: linkedCancellationTokenSource.Token, continuationCallback: async () =>
        {
            // Stop the Cancel After
            if (hasAcquireTimeout)
            {
                linkedCancellationTokenSource.CancelAfter(Timeout.InfiniteTimeSpan);
                hasAcquireTimeout = false;
            }

            // Allow abandoned items to be added in a background task.
            Thread.Yield();

            var stats = await job.Queue.GetQueueStatsAsync().AnyContext();
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("RunUntilEmpty continuation: Queued={Queued}, Working={Working}, Abandoned={Abandoned}", stats.Queued, stats.Working, stats.Abandoned);

            return stats.Queued + stats.Working > 0;
        });
    }

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
