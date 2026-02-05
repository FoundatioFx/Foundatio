using System;

namespace Foundatio.Queues;

/// <summary>
/// Provides activity timestamps for monitoring queue health and detecting idle queues.
/// </summary>
public interface IQueueActivity
{
    /// <summary>
    /// Gets the timestamp of the last enqueue operation, or null if no items have been enqueued.
    /// </summary>
    DateTimeOffset? LastEnqueueActivity { get; }

    /// <summary>
    /// Gets the timestamp of the last dequeue operation, or null if no items have been dequeued.
    /// </summary>
    DateTimeOffset? LastDequeueActivity { get; }
}
