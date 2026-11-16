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
    /// <see cref="TimeSpan.Zero"/>, Foundatio holds the message (and keeps renewing it) for the backoff, giving its
    /// handler slot to other messages, and then abandons it without a delay. Defaults to <see cref="MaxDelay"/>.
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

    /// <summary>
    /// The shortest delay <see cref="IMessageReceiver.AbandonAsync"/> honours accurately, for brokers whose delays are
    /// coarse (SQS visibility timeouts are whole seconds). Foundatio lengthens a shorter retry delay to this one when
    /// it is within a fifth of it, and holds shorter ones in-process, keeping the delivery leased while its handler
    /// slot goes to other messages, then abandons it without a delay. A crash during the hold delivers the message
    /// again sooner; it is never lost. Defaults to <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan MinRetryDelay { get; init; }

    /// <summary>The most messages accepted by one <see cref="IMessageTransport.SendAsync"/> call.</summary>
    public int MaxSendBatchSize { get; init; } = 100;

    /// <summary>The most messages returned by one <see cref="IMessageReceiver.ReceiveAsync"/> call.</summary>
    public int MaxReceiveBatchSize { get; init; } = 100;

    /// <summary>
    /// The most <see cref="IMessageReceiver.ReceiveAsync"/> calls Foundatio may have in flight at once on one receiver,
    /// each of which must hand out different messages. More than one keeps handlers busy on a broker whose receives
    /// are slow next to the handlers, such as SQS, which returns at most 10 messages per call. Defaults to 1:
    /// receives are never called concurrently.
    /// </summary>
    /// <remarks>
    /// Foundatio runs up to as many receives as it takes to fill the handlers' <c>MaxConcurrency</c> with batches of
    /// <see cref="MaxReceiveBatchSize"/>, within this limit, and only one of them at a time asks for less than a full
    /// batch. With <see cref="ReceiveAhead"/> it runs one more, to read ahead, and parks the extra ones while the source
    /// is drained. With <see cref="OrderedGroups"/>, no call may return a message of a group while an earlier one
    /// returned by another call is unsettled. Failures of receives in flight together count as one toward replacing
    /// the receiver.
    /// </remarks>
    public int MaxConcurrentReceives { get; init; } = 1;

    /// <summary>
    /// True when Foundatio may receive ahead of its free handler slots: receives then ask for full batches while the
    /// handlers are busy, and the messages wait in a small in-process buffer, leased and renewed, until a slot frees.
    /// Declare it when a receive is a round trip to the broker that returns the messages it has at once (SQS, Redis,
    /// Service Bus without sessions). Defaults to false: each receive asks only for the handler slots free at that
    /// moment, which suits receivers that already buffer (Kafka, RabbitMQ), receive in the background (Service Bus
    /// sessions) or serve receives from memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How far Foundatio reads ahead, its depth, adapts to how long a message keeps its handler slot and how long a
    /// receive takes: what the handlers get through during one receive, plus a batch. It is capped so that a buffered
    /// message normally waits at most a second, or a quarter of the lease (the shortest
    /// <see cref="IncomingMessage.LeaseExpiresAt"/> seen, when the transport sets it), and it is zero until both times
    /// have been measured, while handlers take longer than that, and while the buffered bodies exceed 16 MiB. At depth
    /// zero, receives ask only for free slots, as without this capability. Up to 13 × <c>MaxConcurrency</c> plus the
    /// depth deliveries can then be unsettled at once.
    /// </para>
    /// <para>
    /// A receive that returns fewer messages than it asked for parks the other receives, each once its current call
    /// returns (a call is never cancelled to park it), so a drained source keeps one long poll open. Each receive that
    /// returns as many as it asked for lets one more poll, up to one more than it takes to fill the slots, within
    /// <see cref="MaxConcurrentReceives"/>.
    /// </para>
    /// <para>
    /// A buffered message is an unsettled delivery like any other: the broker counts it in flight, and with
    /// <see cref="OrderedGroups"/> it holds back its group's later messages. Buffered messages are handled within the
    /// shutdown drain. Those still buffered when the drain times out, or when their receiver is replaced, are released
    /// without running their handlers, and are delivered again with a higher <see cref="IncomingMessage.DeliveryCount"/>.
    /// Foundatio never reads ahead for a handler that processes messages one at a time in order.
    /// </para>
    /// </remarks>
    public bool ReceiveAhead { get; init; }

    /// <summary>
    /// The largest message the transport accepts, measured as body bytes plus the UTF-8 bytes of every header name and
    /// value, the message id, the group key and the deduplication id (see <see cref="MessageSize"/>), or <c>null</c>
    /// when there is no practical limit. Declare it after the transport's own encoding
    /// overhead: any message within the limit must be accepted, including binary bodies, and a larger one must be
    /// rejected with <see cref="MessageTooLargeException"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="MessageSize"/> counts no overhead per header. A transport whose encoding frames each header reserves
    /// room for that framing within the limit it declares, for at least 100 headers, plus room for the
    /// <see cref="KnownHeaders.DeadLetterReason"/> it adds when dead-lettering. It may reject a message whose
    /// header framing goes beyond what it reserved with <see cref="MessageTooLargeException"/>.
    /// </remarks>
    public long? MaxMessageSize { get; init; }

    /// <summary>
    /// The longest single lease the broker allows, or <c>null</c> when unlimited. A broker may also cap how long one
    /// delivery can be held across renewals (SQS: 12 hours from the receive); <see cref="IMessageReceiver.RenewAsync"/>
    /// then throws <see cref="MessageLeaseLostException"/> when that cap is reached.
    /// </summary>
    public TimeSpan? MaxLeaseDuration { get; init; }

    /// <summary>
    /// True when an unsettled message is delivered again after its lease expires. False for brokers that hold a
    /// delivery until the connection closes (RabbitMQ) or that track progress by offset (Kafka). Foundatio still calls
    /// <see cref="IMessageReceiver.RenewAsync"/> while handling, so such a transport can report a delivery that has
    /// moved to another consumer.
    /// </summary>
    public bool LeaseExpiration { get; init; } = true;

    /// <summary>
    /// True when messages with the same <see cref="OutgoingMessage.GroupKey"/> are delivered in send order and no
    /// message of a group is delivered to any receiver of the source, in any process, while an earlier one of that
    /// group is unsettled. Send order is the order within one <see cref="IMessageTransport.SendAsync"/> call and of
    /// calls that complete one after another; messages sent concurrently, for example by different processes, may be
    /// ordered either way. Send order includes delayed and retried messages: a delayed message, or one abandoned with a
    /// delay, holds back its group's later messages until it has been delivered again and settled.
    /// A single receive may return several messages of one group, in order; Foundatio then handles them one after
    /// another, and if one is not completed or dead-lettered, it abandons the rest of that group's messages in the
    /// batch so they are delivered again in order. Dead-letter sources are not ordered: every dead letter of a group
    /// can be received at once.
    /// </summary>
    public bool OrderedGroups { get; init; }

    /// <summary>
    /// True when the broker drops messages whose <see cref="OutgoingMessage.DeduplicationId"/> was already sent to the
    /// same destination, from any process, within its deduplication window.
    /// </summary>
    public bool Deduplication { get; init; }
}
