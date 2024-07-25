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
    /// Will run until the wait timeout expires. If there is still data the job will be cancelled. and then will cancel the job.
    /// </summary>
    /// <returns>The amount of queue items processed.</returns>
    public static async Task<int> RunUntilEmptyAsync<T>(this IQueueJob<T> job, TimeSpan waitTimeout,
        CancellationToken cancellationToken = default) where T : class
    {
        if (waitTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Acquire timeout must be greater than zero", nameof(waitTimeout));

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellationTokenSource.CancelAfter(waitTimeout);

        // NOTE: This has to be awaited otherwise the linkedCancellationTokenSource cancel timer will not fire.
        return await job.RunUntilEmptyAsync(linkedCancellationTokenSource.Token).AnyContext();
    }

    /// <summary>
    /// Will wait up to thirty seconds if queue is empty, otherwise will run until the queue is empty or cancelled.
    /// </summary>
    /// <returns>The amount of queue items processed. This count will not be accurate if the job is cancelled.</returns>
    public static async Task<int> RunUntilEmptyAsync<T>(this IQueueJob<T> job, CancellationToken cancellationToken = default) where T : class
    {
        var logger = job.GetLogger();

        // NOTE: processed count is not accurate if the continuation callback is skipped due to cancellation.
        int processed = 0;
        await job.RunContinuousAsync(cancellationToken: cancellationToken, continuationCallback: async () =>
        {
            processed++;

            // Allow abandoned items to be added in a background task.
            Thread.Yield();

            var stats = await job.Queue.GetQueueStatsAsync().AnyContext();
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("RunUntilEmpty continuation: Queued={Queued}, Working={Working}, Abandoned={Abandoned}", stats.Queued, stats.Working, stats.Abandoned);

            return stats.Queued + stats.Working > 0;
        }).AnyContext();

        return processed;
    }
}
