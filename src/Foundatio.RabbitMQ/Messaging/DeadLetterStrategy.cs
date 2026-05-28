using System.Runtime.Serialization;

namespace Foundatio.Messaging;

/// <summary>
/// Dead-letter strategy for quorum queues.
/// Controls how messages are transferred to the dead-letter exchange.
/// Set via the x-dead-letter-strategy queue argument.
/// See: https://www.rabbitmq.com/docs/quorum-queues#dead-lettering
/// </summary>
public enum DeadLetterStrategy
{
    /// <summary>
    /// Default. Messages may be lost in transit between queues during dead-lettering.
    /// Suitable when dead-lettered messages are informational and loss is acceptable.
    /// </summary>
    [EnumMember(Value = "at-most-once")]
    AtMostOnce,

    /// <summary>
    /// Guarantees message transfer to the dead-letter exchange using internal publisher confirms.
    /// Requires overflow to be set to reject-publish (not drop-head).
    /// Uses more memory and CPU. Only enable when dead-lettered messages must not be lost.
    /// </summary>
    [EnumMember(Value = "at-least-once")]
    AtLeastOnce
}

/// <summary>
/// Queue overflow behavior when a queue reaches its maximum length.
/// Set via the x-overflow queue argument.
/// See: https://www.rabbitmq.com/docs/maxlength#overflow-behaviour
/// </summary>
public enum QueueOverflowBehavior
{
    /// <summary>
    /// Default. Drop or dead-letter messages from the head (oldest) of the queue.
    /// </summary>
    [EnumMember(Value = "drop-head")]
    DropHead,

    /// <summary>
    /// Reject new publishes with basic.nack when the queue is full.
    /// Required for at-least-once dead-lettering on quorum queues.
    /// </summary>
    [EnumMember(Value = "reject-publish")]
    RejectPublish
}
