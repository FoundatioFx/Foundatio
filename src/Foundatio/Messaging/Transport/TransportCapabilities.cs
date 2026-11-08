using System;

namespace Foundatio.Messaging.Transport;

/// <summary>What a transport supports. Foundatio emulates or rejects anything a transport cannot do.</summary>
public sealed record TransportCapabilities
{
    /// <summary>
    /// The longest delay for <see cref="OutgoingMessage.DeliverAt"/> (to queues and topics) and for
    /// <see cref="IMessageReceiver.AbandonAsync"/>. <c>null</c> means unlimited and <see cref="TimeSpan.Zero"/> means
    /// delayed delivery is not supported. Longer queue sends are emulated by re-sending the message when it arrives
    /// early; longer publishes are rejected. A broker that can only delay some of these (SNS cannot delay publishes)
    /// must emulate the rest, for example by hiding an early delivery until it is due.
    /// </summary>
    public TimeSpan? MaxDelay { get; init; }

    /// <summary>The most messages accepted by one <see cref="IMessageTransport.SendAsync"/> call.</summary>
    public int MaxSendBatchSize { get; init; } = 100;

    /// <summary>The most messages returned by one <see cref="IMessageReceiver.ReceiveAsync"/> call.</summary>
    public int MaxReceiveBatchSize { get; init; } = 100;

    /// <summary>
    /// The largest body plus headers in bytes, or <c>null</c> when there is no practical limit. Foundatio checks an
    /// approximation before sending; transports may still reject a message that encodes larger.
    /// </summary>
    public long? MaxMessageSize { get; init; }

    /// <summary>The longest lease the broker allows, or <c>null</c> when unlimited.</summary>
    public TimeSpan? MaxLeaseDuration { get; init; }

    /// <summary>
    /// True when an unsettled message is delivered again after its lease expires. False for brokers that hold a
    /// delivery until the connection closes (RabbitMQ) or that track progress by offset (Kafka).
    /// </summary>
    public bool LeaseExpiration { get; init; } = true;

    /// <summary>True when messages with the same <see cref="OutgoingMessage.GroupKey"/> are delivered in order, one at a time.</summary>
    public bool OrderedGroups { get; init; }

    /// <summary>True when the broker drops messages whose <see cref="OutgoingMessage.DeduplicationId"/> was already seen.</summary>
    public bool Deduplication { get; init; }
}
