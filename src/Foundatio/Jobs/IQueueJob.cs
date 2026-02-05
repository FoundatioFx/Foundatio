using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>
/// A job that processes items from a queue. Each invocation of <see cref="IJob.RunAsync"/>
/// dequeues and processes a single item.
/// </summary>
/// <typeparam name="T">The type of message payload in the queue.</typeparam>
public interface IQueueJob<T> : IJob where T : class
{
    /// <summary>
    /// Processes a single queue entry. Called by <see cref="IJob.RunAsync"/> after dequeuing an item.
    /// Can also be called directly when the queue entry is obtained externally.
    /// </summary>
    /// <param name="queueEntry">The queue entry to process.</param>
    /// <param name="cancellationToken">Token to signal that processing should stop.</param>
    /// <returns>A result indicating success or failure of processing.</returns>
    Task<JobResult> ProcessAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the queue this job processes items from.
    /// </summary>
    IQueue<T> Queue { get; }
}

public static class QueueJobExtensions
{
    /// <summary>
    /// Will run until the queue is empty or the wait time is exceeded.
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
    /// <returns>The amount of queue items processed.</returns>
    public static Task<int> RunUntilEmptyAsync<T>(this IQueueJob<T> job, CancellationToken cancellationToken = default) where T : class
    {
        var logger = job.GetLogger();

        return job.RunContinuousAsync(cancellationToken: cancellationToken, continuationCallback: async () =>
        {
            // Allow abandoned items to be added in a background task.
            Thread.Yield();

            var stats = await job.Queue.GetQueueStatsAsync().AnyContext();
            logger.LogTrace("RunUntilEmpty continuation: Queued={Queued}, Working={Working}, Abandoned={Abandoned}", stats.Queued, stats.Working, stats.Abandoned);
            return stats.Queued + stats.Working > 0;
        });
    }
}
