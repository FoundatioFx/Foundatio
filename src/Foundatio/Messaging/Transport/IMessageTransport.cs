using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// The contract a message broker implements to plug into Foundatio messaging.
/// </summary>
/// <remarks>
/// A transport only moves bytes: it sends to queues and topics, hands out leased deliveries, and settles them.
/// Serialization, routing, retries, backoff, dead-letter decisions, concurrency, lease renewal and graceful
/// shutdown are all handled by Foundatio. Implementations are verified by <c>MessageTransportTestsBase</c> in
/// Foundatio.TestHarness.
/// <para>
/// All members must be safe to call concurrently. Disposing the transport closes its receivers; their unsettled
/// messages are delivered again. Names are validated to letters, digits, '.', '_' and '-'; shorten or hash them
/// if the broker needs to.
/// </para>
/// </remarks>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public interface IMessageTransport : IAsyncDisposable
{
    /// <summary>What this transport supports natively. Foundatio adapts its behavior to these values.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Sends messages to a queue, publishes them to a topic, or sends them to one durable subscription of a topic.
    /// Sending to a queue or subscription must not lose messages because it does not exist yet; publishing to a
    /// topic with no subscriptions may discard them. A message sent to a subscription reaches only that
    /// subscription, not the topic's others.
    /// </summary>
    /// <remarks>
    /// Throw if any message could not be sent; messages before the failure may already have been sent. Remember a
    /// <see cref="OutgoingMessage.DeduplicationId"/> only for a message that was sent: when the call throws, forget the
    /// ids of the messages it did not send, or the caller's retry would be dropped as a duplicate and the message lost.
    /// Throw <see cref="MessageTooLargeException"/> for a message over <see cref="TransportCapabilities.MaxMessageSize"/>.
    /// </remarks>
    Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a receiver for a queue, subscription or dead-letter queue, creating the source if needed. Return once the
    /// receiver can deliver: its first <see cref="IMessageReceiver.ReceiveAsync"/> must return messages already in
    /// the source, so wait for the broker to assign it (for example a consumer group join) before returning. A new
    /// subscription gets copies of messages published after it is created, not earlier ones.
    /// </summary>
    Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a queue or durable subscription with its pending messages and dead letters, or, for a
    /// <see cref="MessageSourceKind.DeadLetter"/> source, only the dead letters. Succeeds when the source does not
    /// exist. Close its receivers first: a transport may refuse, throwing <see cref="MessageBusException"/> and keeping
    /// the source's messages, while a receiver is open on the source in any process. Receiving from the source again
    /// creates it anew; a deleted subscription does not get copies of messages published before then. Deduplication
    /// state may outlive the delete. When the delete goes ahead, a receiver still open on the source, in any process,
    /// may keep delivering its older messages, and must then keep receiving or fault (see <see cref="IMessageReceiver"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The source is an instance subscription; dispose its receiver instead.</exception>
    Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default);
}
