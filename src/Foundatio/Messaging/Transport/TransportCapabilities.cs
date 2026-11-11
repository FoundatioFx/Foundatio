using System;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// What a transport supports natively. Foundatio emulates or rejects anything a transport cannot do, and the
/// conformance suite skips tests for capabilities a transport does not declare.
/// </summary>
/// <remarks>
/// Delay limits use the same convention: <c>null</c> means unlimited and <see cref="TimeSpan.Zero"/> means not
/// supported. <see cref="MaxPublishDelay"/> and <see cref="MaxRetryDelay"/> default to <see cref="MaxDelay"/> when
/// they are not set.
/// </remarks>
public sealed record TransportCapabilities
{
    private readonly TimeSpan? _maxPublishDelay;
    private readonly bool _maxPublishDelaySet;
    private readonly TimeSpan? _maxRetryDelay;
    private readonly bool _maxRetryDelaySet;

    /// <summary>
    /// The longest <see cref="OutgoingMessage.DeliverAt"/> delay for messages sent to a queue or to one subscription.
    /// Foundatio emulates longer delays by re-sending a message that arrives early. With <see cref="TimeSpan.Zero"/>,
    /// delayed sends are rejected.
    /// </summary>
    public TimeSpan? MaxDelay { get; init; }

    /// <summary>
    /// The longest <see cref="OutgoingMessage.DeliverAt"/> delay for messages published to a topic; longer publishes
    /// are rejected. A broker that cannot delay publishes natively (SNS) may still declare a limit if the transport
    /// emulates it, for example by hiding an early delivery until it is due. Defaults to <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan? MaxPublishDelay
    {
        get => _maxPublishDelaySet ? _maxPublishDelay : MaxDelay;
        init
        {
            _maxPublishDelay = value;
            _maxPublishDelaySet = true;
        }
    }

    /// <summary>
    /// The longest delay <see cref="IMessageReceiver.AbandonAsync"/> honours. Longer retry delays are shortened; with
    /// <see cref="TimeSpan.Zero"/>, Foundatio holds the message (and keeps renewing it) for the backoff and then
    /// abandons it without a delay. Defaults to <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan? MaxRetryDelay
    {
        get => _maxRetryDelaySet ? _maxRetryDelay : MaxDelay;
        init
        {
            _maxRetryDelay = value;
            _maxRetryDelaySet = true;
        }
    }

    /// <summary>The most messages accepted by one <see cref="IMessageTransport.SendAsync"/> call.</summary>
    public int MaxSendBatchSize { get; init; } = 100;

    /// <summary>The most messages returned by one <see cref="IMessageReceiver.ReceiveAsync"/> call.</summary>
    public int MaxReceiveBatchSize { get; init; } = 100;

    /// <summary>
    /// The largest message the transport accepts, measured as body bytes plus the UTF-8 bytes of every header name and
    /// value, the message id, the group key and the deduplication id (see <see cref="MessageSize"/>), or <c>null</c>
    /// when there is no practical limit. Declare it after the transport's own encoding
    /// overhead: any message within the limit must be accepted, including binary bodies, and a larger one must be
    /// rejected with <see cref="MessageTooLargeException"/>.
    /// </summary>
    public long? MaxMessageSize { get; init; }

    /// <summary>The longest lease the broker allows, or <c>null</c> when unlimited.</summary>
    public TimeSpan? MaxLeaseDuration { get; init; }

    /// <summary>
    /// True when an unsettled message is delivered again after its lease expires. False for brokers that hold a
    /// delivery until the connection closes (RabbitMQ) or that track progress by offset (Kafka).
    /// </summary>
    public bool LeaseExpiration { get; init; } = true;

    /// <summary>
    /// True when messages with the same <see cref="OutgoingMessage.GroupKey"/> are delivered in send order and no
    /// message of a group is delivered while an earlier one of that group is unsettled. Send order includes delayed
    /// messages: a delayed message holds back its group's later messages until it has been delivered and settled.
    /// A single receive may return several messages of one group, in order; Foundatio then handles them one after
    /// another, and if one is not completed or dead-lettered, it abandons the rest of that group's messages in the
    /// batch so they are delivered again in order. Dead-letter sources are not ordered: every dead letter of a group
    /// can be received at once.
    /// </summary>
    public bool OrderedGroups { get; init; }

    /// <summary>True when the broker drops messages whose <see cref="OutgoingMessage.DeduplicationId"/> was already seen.</summary>
    public bool Deduplication { get; init; }
}
