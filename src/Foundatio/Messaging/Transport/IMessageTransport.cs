using System;
using System.Collections.Generic;
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
public interface IMessageTransport : IAsyncDisposable
{
    /// <summary>What this transport supports natively. Foundatio adapts its behavior to these values.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Sends messages to a queue, or publishes them to a topic. Sending to a queue must not lose messages because
    /// the queue does not exist yet; publishing to a topic with no subscriptions may discard them.
    /// </summary>
    /// <remarks>Throw if any message could not be sent. Messages before the failure may already have been sent.</remarks>
    Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Opens a receiver for a queue, subscription or dead-letter queue, creating the source if needed.</summary>
    Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default);
}
