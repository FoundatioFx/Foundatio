using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging.Transport;

/// <summary>Receives and settles messages from a single source.</summary>
/// <remarks>
/// Each received message is leased to this receiver for <see cref="ReceiverOptions.LeaseDuration"/>. A message that
/// is not settled before its lease expires, or before the receiver is disposed, must be delivered again.
/// Disposing a receiver for an <see cref="MessageSourceKind.InstanceSubscription"/> removes that subscription; one
/// left behind by a crashed process must be removed by the transport (for example with a heartbeat and expiry).
/// Settlement and <see cref="RenewAsync"/> are called concurrently with <see cref="ReceiveAsync"/> and with each other.
/// </remarks>
public interface IMessageReceiver : IAsyncDisposable
{
    MessageSource Source { get; }

    /// <summary>
    /// Returns up to <paramref name="maxMessages"/> messages, waiting at most <paramref name="maxWait"/> for the first
    /// one. Returns an empty list when nothing arrives in time.
    /// </summary>
    Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the message permanently. Settling a delivery that has since been delivered to another receiver should
    /// throw <see cref="MessageLeaseLostException"/> and leave the newer delivery alone, where the broker can tell
    /// (some, such as SQS, may accept a stale receipt).
    /// </summary>
    Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the message so it is delivered again after <paramref name="delay"/> with an incremented
    /// <see cref="IncomingMessage.DeliveryCount"/>. Foundatio never passes a delay above <see cref="TransportCapabilities.MaxRetryDelay"/>.
    /// </summary>
    Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the message to the dead-letter queue of this receiver's source, preserving its id, body and headers and
    /// adding a <see cref="KnownHeaders.DeadLetterReason"/> header. The message must then be receivable from
    /// <see cref="MessageSource.DeadLetter(MessageSource)"/>. Never called for instance subscriptions. When the broker
    /// cannot move a message atomically, copy then remove; a duplicate dead letter is acceptable, a lost one is not.
    /// </summary>
    Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease by <see cref="ReceiverOptions.LeaseDuration"/> from now. Throw <see cref="MessageLeaseLostException"/>
    /// when the lease is known to be lost. May be a no-op when <see cref="TransportCapabilities.LeaseExpiration"/> is false.
    /// </summary>
    Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default);
}
