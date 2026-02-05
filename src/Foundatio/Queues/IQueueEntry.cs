using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Foundatio.Queues;

/// <summary>
/// Represents a dequeued item with its processing state and lifecycle methods.
/// Each entry holds a lock that must be renewed for long-running operations.
/// </summary>
public interface IQueueEntry
{
    /// <summary>
    /// Gets the unique identifier for this queue entry.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the correlation identifier for distributed tracing.
    /// </summary>
    string CorrelationId { get; }

    /// <summary>
    /// Gets custom properties attached to this entry.
    /// </summary>
    IDictionary<string, string> Properties { get; }

    /// <summary>
    /// Gets the CLR type of the message payload.
    /// </summary>
    Type EntryType { get; }

    /// <summary>
    /// Gets the message payload as an untyped object.
    /// </summary>
    object GetValue();

    /// <summary>
    /// Gets whether this entry has been marked as completed.
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    /// Gets whether this entry has been marked as abandoned.
    /// </summary>
    bool IsAbandoned { get; }

    /// <summary>
    /// Gets the number of times this entry has been dequeued, including the current attempt.
    /// Useful for implementing retry limits or exponential backoff.
    /// </summary>
    int Attempts { get; }

    /// <summary>
    /// Marks this entry as abandoned locally without notifying the queue.
    /// Use <see cref="AbandonAsync"/> to return the entry to the queue for reprocessing.
    /// </summary>
    void MarkAbandoned();

    /// <summary>
    /// Marks this entry as completed locally without notifying the queue.
    /// Use <see cref="CompleteAsync"/> to remove the entry from the queue.
    /// </summary>
    void MarkCompleted();

    /// <summary>
    /// Extends the processing lock to prevent the entry from being redelivered.
    /// Call periodically for long-running work items.
    /// </summary>
    Task RenewLockAsync();

    /// <summary>
    /// Returns this entry to the queue for reprocessing.
    /// The entry will be redelivered after a delay, up to the maximum retry limit.
    /// </summary>
    Task AbandonAsync();

    /// <summary>
    /// Marks this entry as successfully processed and removes it from the queue.
    /// </summary>
    Task CompleteAsync();

    /// <summary>
    /// Releases resources associated with this entry.
    /// If not completed or abandoned, the entry will be automatically abandoned.
    /// </summary>
    ValueTask DisposeAsync();
}

/// <summary>
/// A typed queue entry providing strongly-typed access to the message payload.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public interface IQueueEntry<T> : IQueueEntry where T : class
{
    /// <summary>
    /// Gets the deserialized message payload.
    /// </summary>
    T Value { get; }
}
