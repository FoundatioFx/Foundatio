using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Queues;

/// <summary>
/// A typed message queue that supports enqueue, dequeue, and work item lifecycle management.
/// Entries are processed with at-least-once delivery semantics and must be explicitly completed or abandoned.
/// </summary>
/// <typeparam name="T">The type of message payload stored in the queue.</typeparam>
public interface IQueue<T> : IQueue where T : class
{
    /// <summary>
    /// Raised before an item is enqueued. Set <see cref="CancelEventArgs.Cancel"/> to prevent enqueueing.
    /// </summary>
    AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; }

    /// <summary>
    /// Raised after an item has been successfully enqueued.
    /// </summary>
    AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; }

    /// <summary>
    /// Raised after an item has been dequeued and is ready for processing.
    /// </summary>
    AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; }

    /// <summary>
    /// Raised after a queue entry's lock has been renewed.
    /// </summary>
    AsyncEvent<LockRenewedEventArgs<T>> LockRenewed { get; }

    /// <summary>
    /// Raised after a queue entry has been marked as completed.
    /// </summary>
    AsyncEvent<CompletedEventArgs<T>> Completed { get; }

    /// <summary>
    /// Raised after a queue entry has been abandoned and returned to the queue.
    /// </summary>
    AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; }

    /// <summary>
    /// Raised after the queue has been deleted.
    /// </summary>
    AsyncEvent<QueueDeletedEventArgs<T>> QueueDeleted { get; }

    /// <summary>
    /// Attaches a behavior that can intercept and modify queue operations.
    /// </summary>
    /// <param name="behavior">The behavior to attach.</param>
    void AttachBehavior(IQueueBehavior<T> behavior);

    /// <summary>
    /// Adds an item to the queue for processing.
    /// </summary>
    /// <param name="data">The message payload to enqueue.</param>
    /// <param name="options">Optional settings for delivery delay, correlation ID, and custom properties.</param>
    /// <returns>The unique identifier assigned to the queued entry.</returns>
    Task<string> EnqueueAsync(T data, QueueEntryOptions options = null);

    /// <summary>
    /// Retrieves and locks the next available item from the queue.
    /// Blocks until an item is available or the cancellation token is triggered.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the wait for an item.</param>
    /// <returns>The dequeued entry, or null if cancelled before an item became available.</returns>
    Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves and locks the next available item from the queue.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for an item. Defaults to 30 seconds.</param>
    /// <returns>The dequeued entry, or null if no item was available within the timeout.</returns>
    Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null);

    /// <summary>
    /// Extends the processing lock on a queue entry to prevent it from being redelivered.
    /// Call periodically for long-running work items.
    /// </summary>
    /// <param name="queueEntry">The entry whose lock should be renewed.</param>
    Task RenewLockAsync(IQueueEntry<T> queueEntry);

    /// <summary>
    /// Marks a queue entry as successfully processed and removes it from the queue.
    /// </summary>
    /// <param name="queueEntry">The entry to complete.</param>
    Task CompleteAsync(IQueueEntry<T> queueEntry);

    /// <summary>
    /// Returns a queue entry to the queue for reprocessing.
    /// The entry will be redelivered after a delay, up to the maximum retry limit.
    /// </summary>
    /// <param name="queueEntry">The entry to abandon.</param>
    Task AbandonAsync(IQueueEntry<T> queueEntry);

    /// <summary>
    /// Retrieves items that have exceeded the maximum retry attempts.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The collection of dead-lettered message payloads.</returns>
    Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a background worker that continuously dequeues and processes items.
    /// </summary>
    /// <param name="handler">The async function invoked for each dequeued entry.</param>
    /// <param name="autoComplete">
    /// If true, automatically calls <see cref="CompleteAsync"/> after the handler completes successfully.
    /// If false (default), the handler must explicitly complete or abandon the entry.
    /// </param>
    /// <param name="cancellationToken">Token to stop the background worker.</param>
    Task StartWorkingAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Base interface for queue operations that are not type-specific.
/// </summary>
public interface IQueue : IHaveSerializer, IDisposable
{
    /// <summary>
    /// Gets current queue statistics including counts for queued, working, and dead-lettered items.
    /// </summary>
    Task<QueueStats> GetQueueStatsAsync();

    /// <summary>
    /// Permanently deletes the queue and all its contents.
    /// </summary>
    Task DeleteQueueAsync();

    /// <summary>
    /// Gets the unique identifier for this queue instance.
    /// </summary>
    string QueueId { get; }
}

public static class QueueExtensions
{
    public static Task StartWorkingAsync<T>(this IQueue<T> queue, Func<IQueueEntry<T>, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default) where T : class
        => queue.StartWorkingAsync((entry, token) => handler(entry), autoComplete, cancellationToken);
}

/// <summary>
/// Provides statistics about queue state and processing activity.
/// </summary>
[DebuggerDisplay("Queued={Queued}, Working={Working}, Deadletter={Deadletter}, Enqueued={Enqueued}, Dequeued={Dequeued}, Completed={Completed}, Abandoned={Abandoned}, Errors={Errors}, Timeouts={Timeouts}")]
public record QueueStats
{
    /// <summary>
    /// Number of items waiting to be processed.
    /// </summary>
    public long Queued { get; set; }

    /// <summary>
    /// Number of items currently being processed (dequeued but not yet completed or abandoned).
    /// </summary>
    public long Working { get; set; }

    /// <summary>
    /// Number of items that exceeded retry limits and were moved to the dead-letter queue.
    /// </summary>
    public long Deadletter { get; set; }

    /// <summary>
    /// Total number of items that have been enqueued since queue creation.
    /// </summary>
    public long Enqueued { get; set; }

    /// <summary>
    /// Total number of items that have been dequeued since queue creation.
    /// </summary>
    public long Dequeued { get; set; }

    /// <summary>
    /// Total number of items that have been successfully completed since queue creation.
    /// </summary>
    public long Completed { get; set; }

    /// <summary>
    /// Total number of times items have been abandoned since queue creation.
    /// </summary>
    public long Abandoned { get; set; }

    /// <summary>
    /// Total number of processing errors since queue creation.
    /// </summary>
    public long Errors { get; set; }

    /// <summary>
    /// Total number of items that timed out during processing since queue creation.
    /// </summary>
    public long Timeouts { get; set; }
}

/// <summary>
/// Options for customizing how a message is enqueued.
/// </summary>
public record QueueEntryOptions
{
    /// <summary>
    /// A unique identifier for the message. If not specified, one will be generated.
    /// Can be used for deduplication or idempotency checks.
    /// </summary>
    public string UniqueId { get; set; }

    /// <summary>
    /// A correlation identifier for distributed tracing across services.
    /// </summary>
    public string CorrelationId { get; set; }

    /// <summary>
    /// Delay before the message becomes visible for processing.
    /// </summary>
    public TimeSpan? DeliveryDelay { get; set; }

    /// <summary>
    /// Custom properties to attach to the message.
    /// </summary>
    public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// Event arguments for the <see cref="IQueue{T}.Enqueuing"/> event.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public class EnqueuingEventArgs<T> : CancelEventArgs where T : class
{
    /// <summary>
    /// The queue raising the event.
    /// </summary>
    public IQueue<T> Queue { get; set; }

    /// <summary>
    /// The message payload being enqueued.
    /// </summary>
    public T Data { get; set; }

    /// <summary>
    /// The options for the enqueue operation.
    /// </summary>
    public QueueEntryOptions Options { get; set; }
}

/// <summary>
/// Event arguments for the <see cref="IQueue{T}.Enqueued"/> event.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public class EnqueuedEventArgs<T> : EventArgs where T : class
{
    /// <summary>
    /// The queue raising the event.
    /// </summary>
    public IQueue<T> Queue { get; set; }

    /// <summary>
    /// The queue entry that was enqueued.
    /// </summary>
    public IQueueEntry<T> Entry { get; set; }
}

/// <summary>
/// Event arguments for the <see cref="IQueue{T}.Dequeued"/> event.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public class DequeuedEventArgs<T> : EventArgs where T : class
{
    /// <summary>
    /// The queue raising the event.
    /// </summary>
    public IQueue<T> Queue { get; set; }

    /// <summary>
    /// The queue entry that was dequeued.
    /// </summary>
    public IQueueEntry<T> Entry { get; set; }
}

/// <summary>
/// Event arguments for the <see cref="IQueue{T}.LockRenewed"/> event.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public class LockRenewedEventArgs<T> : EventArgs where T : class
{
    /// <summary>
    /// The queue raising the event.
    /// </summary>
    public IQueue<T> Queue { get; set; }

    /// <summary>
    /// The queue entry whose lock was renewed.
    /// </summary>
    public IQueueEntry<T> Entry { get; set; }
}

/// <summary>
/// Event arguments for the <see cref="IQueue{T}.Completed"/> event.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public class CompletedEventArgs<T> : EventArgs where T : class
{
    /// <summary>
    /// The queue raising the event.
    /// </summary>
    public IQueue<T> Queue { get; set; }

    /// <summary>
    /// The queue entry that was completed.
    /// </summary>
    public IQueueEntry<T> Entry { get; set; }
}

/// <summary>
/// Event arguments for the <see cref="IQueue{T}.Abandoned"/> event.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public class AbandonedEventArgs<T> : EventArgs where T : class
{
    /// <summary>
    /// The queue raising the event.
    /// </summary>
    public IQueue<T> Queue { get; set; }

    /// <summary>
    /// The queue entry that was abandoned.
    /// </summary>
    public IQueueEntry<T> Entry { get; set; }
}

/// <summary>
/// Event arguments for the <see cref="IQueue{T}.QueueDeleted"/> event.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public class QueueDeletedEventArgs<T> : EventArgs where T : class
{
    /// <summary>
    /// The queue raising the event.
    /// </summary>
    public IQueue<T> Queue { get; set; }
}
