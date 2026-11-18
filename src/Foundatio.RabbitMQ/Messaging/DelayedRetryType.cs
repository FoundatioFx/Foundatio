using System.Runtime.Serialization;

namespace Foundatio.Messaging;

/// <summary>
/// Specifies the native delayed retry behavior for quorum queues (RabbitMQ 4.3+).
/// Maps to the x-delayed-retry-type queue argument.
/// See: https://www.rabbitmq.com/docs/quorum-queues#delayed-retries
/// </summary>
public enum DelayedRetryType
{
    /// <summary>
    /// All returned messages (nacks, rejects, and timeouts) are delayed before redelivery.
    /// </summary>
    [EnumMember(Value = "all")]
    All,

    /// <summary>
    /// Messages returned without marking the delivery as failed are delayed.
    /// Includes: basic.nack, AMQP 1.0 released, modified with delivery-failed=false.
    /// These do NOT increment delivery-count, supporting unlimited returns.
    /// </summary>
    [EnumMember(Value = "returned")]
    Returned,

    /// <summary>
    /// Only messages where delivery actually failed are delayed.
    /// Includes: basic.reject, client crash, AMQP 1.0 rejected, modified with delivery-failed=true.
    /// These increment delivery-count toward the delivery-limit.
    /// </summary>
    [EnumMember(Value = "failed")]
    Failed,

    /// <summary>
    /// Delayed retry is explicitly disabled.
    /// </summary>
    [EnumMember(Value = "disabled")]
    Disabled
}
