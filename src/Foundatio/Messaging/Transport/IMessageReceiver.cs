using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging.Transport;

/// <summary>Receives and settles messages from a single source.</summary>
/// <remarks>
/// <para>
/// Each received message is leased to this receiver for <see cref="ReceiverOptions.LeaseDuration"/>. A message that
/// is not settled before its lease expires, or before the receiver is disposed, must be delivered again.
/// Disposing a receiver for an <see cref="MessageSourceKind.InstanceSubscription"/> removes that subscription; one
/// left behind by a crashed process must be removed by the transport (for example with a heartbeat and expiry).
/// Settlement and <see cref="RenewAsync"/> are called concurrently with <see cref="ReceiveAsync"/> and with each other
/// for different messages. Foundatio stops renewing a message, and waits for a renewal in flight, before it settles it.
/// </para>
/// <para>
/// A delivery's lease is lost once its lease expired and the message was delivered again, once this receiver was
/// disposed, or, without <see cref="TransportCapabilities.LeaseExpiration"/>, once the delivery moved to another
/// consumer. <see cref="RenewAsync"/>, <see cref="AbandonAsync"/> and <see cref="DeadLetterAsync"/> of a lost delivery
/// must throw <see cref="MessageLeaseLostException"/> (or <see cref="ObjectDisposedException"/> after the receiver or
/// transport was disposed) and leave any newer delivery of the message alone. <see cref="CompleteAsync"/> should do the
/// same; where the broker cannot tell a stale receipt from the current one (an SQS receipt handle), it may remove the
/// message instead.
/// </para>
/// <para>
/// A settlement call that throws for any other reason must leave the message to be delivered again within a bounded
/// time: once its lease expires, or, without <see cref="TransportCapabilities.LeaseExpiration"/>, after a short delay
/// the transport chooses (the Kafka transport waits five seconds). It must not wait for the receiver to be disposed:
/// Foundatio may not settle that delivery again, and a healthy receiver is never disposed.
/// </para>
/// <para>
/// When the source is deleted while this receiver is open, the receiver may still deliver messages it already held.
/// It must then either keep receiving what is sent to the source afterwards, or throw from <see cref="ReceiveAsync"/>,
/// preferably <see cref="MessageReceiverFaultedException"/> so Foundatio replaces it at once. It must not silently stop
/// delivering.
/// </para>
/// </remarks>
public interface IMessageReceiver : IAsyncDisposable
{
    MessageSource Source { get; }

    /// <summary>
    /// Returns up to <paramref name="maxMessages"/> messages, waiting at most <paramref name="maxWait"/> for the first
    /// one. Returns an empty list when nothing arrives in time.
    /// </summary>
    /// <remarks>
    /// Throw <see cref="OperationCanceledException"/> promptly when <paramref name="cancellationToken"/> is cancelled;
    /// a message the broker handed out to the cancelled call must still be delivered again, at the latest when its lease
    /// expires. Throw <see cref="MessageReceiverFaultedException"/> when this receiver can never deliver again, so
    /// Foundatio replaces it; other exceptions are retried with backoff, and after five in a row Foundatio replaces it too.
    /// When the broker, not <see cref="ReceiverOptions.LeaseDuration"/>, decides how long a delivery is leased (a Service
    /// Bus lock), set each message's <see cref="IncomingMessage.LeaseExpiresAt"/>, so Foundatio renews before it ends.
    /// </remarks>
    Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the message permanently. For a delivery whose lease was lost, throw <see cref="MessageLeaseLostException"/>
    /// and leave the newer delivery alone; only where the broker cannot tell a stale receipt (SQS) may it remove the
    /// message instead.
    /// </summary>
    Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the message so it is delivered again after <paramref name="delay"/> with an incremented
    /// <see cref="IncomingMessage.DeliveryCount"/>. Foundatio never passes a delay above <see cref="TransportCapabilities.MaxRetryDelay"/>.
    /// A transport may shorten the delay to what the broker still allows for this delivery (SQS caps visibility at
    /// 12 hours from the receive, including time spent renewing). For a delivery whose lease was lost, throw
    /// <see cref="MessageLeaseLostException"/>: releasing a message another consumer is handling would process it twice.
    /// </summary>
    Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the message to the dead-letter queue of this receiver's source, preserving its id, body and headers and
    /// adding a <see cref="KnownHeaders.DeadLetterReason"/> header. The message must then be receivable from
    /// <see cref="MessageSource.DeadLetter(MessageSource)"/>. Never called for instance subscriptions. When the broker
    /// cannot move a message atomically, copy then remove; a duplicate dead letter is acceptable, a lost one is not.
    /// Dead-letter any message within <see cref="TransportCapabilities.MaxMessageSize"/> whatever the length of
    /// <paramref name="reason"/>: a transport may shorten the reason to fit, but keeps at least its first 1,024 bytes of
    /// UTF-8. Dead-lettering the same message id again, for example after a replay, must keep both dead letters. For a
    /// delivery whose lease was lost, throw <see cref="MessageLeaseLostException"/> without copying the message.
    /// </summary>
    Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease by <see cref="ReceiverOptions.LeaseDuration"/> from now, or by the broker's own lease duration;
    /// in that case set the message's <see cref="IncomingMessage.LeaseExpiresAt"/> to the new expiry before returning.
    /// Throw <see cref="MessageLeaseLostException"/> when the lease is known to be lost; Foundatio then cancels the
    /// handler and does not settle the message.
    /// </summary>
    /// <remarks>
    /// After the lease has expired but before the message is delivered again, a transport may either take the lease
    /// back or throw <see cref="MessageLeaseLostException"/>; Foundatio handles both. Once the message has been
    /// delivered again it must throw. It may also throw when the broker caps how long one delivery can be held in total
    /// (SQS: 12 hours from the receive), which <see cref="TransportCapabilities.MaxLeaseDuration"/> cannot express.
    /// Without <see cref="TransportCapabilities.LeaseExpiration"/>, Foundatio calls it about once a second while
    /// handling: do nothing, or throw <see cref="MessageLeaseLostException"/> once the delivery has gone to another
    /// consumer (for example after a Kafka partition was revoked), so the handler stops instead of doing the work twice.
    /// </remarks>
    Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default);
}
