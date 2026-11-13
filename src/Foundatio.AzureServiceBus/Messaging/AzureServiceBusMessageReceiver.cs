using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Receives from a queue or subscription without sessions, or from a dead-letter queue, with peek-lock. Disposing it
/// abandons what it handed out and did not settle, so those messages go to the next receiver at once with a higher
/// delivery count (closing the link alone releases them without counting the delivery).
/// </summary>
internal sealed class AzureServiceBusMessageReceiver : IMessageReceiver
{
    private static readonly TimeSpan MinReceiveWait = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private readonly AzureServiceBusMessageTransport _transport;
    private readonly AzureServiceBusReceiverEntity _entity;
    private readonly ServiceBusReceiver _receiver;
    private readonly ConcurrentDictionary<string, AzureServiceBusIncomingMessage> _unsettled = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposing = new();
    private int _disposed;

    public AzureServiceBusMessageReceiver(AzureServiceBusMessageTransport transport, MessageSource source, AzureServiceBusReceiverEntity entity)
    {
        _transport = transport;
        _entity = entity;
        Source = source;
        _receiver = entity.CreateReceiver(transport.Client);
    }

    public MessageSource Source { get; }

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        var requestedAt = _transport.TimeProvider.GetUtcNow();
        IReadOnlyList<ServiceBusReceivedMessage> received;
        try
        {
            received = await _receiver.ReceiveMessagesAsync(Math.Min(maxMessages, AzureServiceBusMessageTransport.MaxBatchSize),
                maxWait > MinReceiveWait ? maxWait : MinReceiveWait, receiveCancellation.Token).AnyContext();
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (ObjectDisposedException) when (_disposing.IsCancellationRequested)
        {
            return [];
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsFatal(ex))
        {
            throw new MessageReceiverFaultedException($"{_entity} of {Source} can no longer be received from: {ex.Message}", ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not receive from {Source}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageReceiverFaultedException($"Could not receive from {Source}: {ex.Message}", ex);
        }

        if (received.Count == 0)
            return [];

        var messages = new List<IncomingMessage>(received.Count);
        foreach (var message in received)
        {
            var decoded = AzureServiceBusMessageTransportCodec.Decode(message, _entity.DeadLetter);
            var incoming = new AzureServiceBusIncomingMessage
            {
                Owner = this,
                Received = message,
                MessageId = decoded.MessageId,
                Body = decoded.Body,
                Headers = decoded.Headers,
                GroupKey = decoded.GroupKey,
                DeliveryCount = decoded.DeliveryCount,
                EnqueuedAt = message.EnqueuedTime,
                LeaseExpiresAt = _transport.GetLeaseEnd(requestedAt, message.LockedUntil, _entity.LockDuration)
            };

            _unsettled[message.LockToken] = incoming;
            messages.Add(incoming);
        }

        return messages;
    }

    public async Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        try
        {
            await _receiver.CompleteMessageAsync(delivery.Received, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not complete message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        _unsettled.TryRemove(delivery.Received.LockToken, out _);
    }

    /// <remarks>
    /// Service Bus cannot abandon with a delay. A delayed retry renews the lock, which proves this delivery still holds
    /// it, then schedules a copy carrying the delivery count and completes the original: in one transaction on a queue,
    /// one after the other on a subscription, where the copy goes to the topic for this subscription only. A dead letter
    /// is released at once whatever the delay.
    /// </remarks>
    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);

        // Service Bus may accept an abandon of an expired lock without an error, so a lease known to be over is refused here.
        ThrowIfLeaseExpired(delivery);
        if (delay <= TimeSpan.Zero || _entity.DeadLetter)
        {
            try
            {
                await _receiver.AbandonMessageAsync(delivery.Received, cancellationToken: cancellationToken).AnyContext();
            }
            catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
            {
                throw LeaseLost(delivery, ex);
            }
            catch (ServiceBusException ex)
            {
                throw new MessageBusException($"Could not abandon message {delivery.MessageId} from {Source}: {ex.Message}", ex);
            }

            _unsettled.TryRemove(delivery.Received.LockToken, out _);
            return;
        }

        await RenewAsync(delivery, cancellationToken).AnyContext();
        try
        {
            await _transport.ScheduleRetryAsync(_receiver, _entity, delivery, delay, Source, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not schedule the retry of message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        _unsettled.TryRemove(delivery.Received.LockToken, out _);
    }

    public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        if (_entity.DeadLetter)
            throw new InvalidOperationException("A dead letter cannot be dead-lettered again; complete it or abandon it.");

        var delivery = GetDelivery(message);
        ThrowIfLeaseExpired(delivery);
        try
        {
            await _receiver.DeadLetterMessageAsync(delivery.Received,
                AzureServiceBusMessageTransportCodec.TruncateUtf8(reason ?? String.Empty, AzureServiceBusMessageTransportCodec.MaxDeadLetterReasonBytes),
                deadLetterErrorDescription: null, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not dead-letter message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        _unsettled.TryRemove(delivery.Received.LockToken, out _);
    }

    public async Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        var requestedAt = _transport.TimeProvider.GetUtcNow();
        try
        {
            await _receiver.RenewMessageLockAsync(delivery.Received, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not renew the lock of message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        delivery.LeaseExpiresAt = _transport.GetLeaseEnd(requestedAt, delivery.Received.LockedUntil, _entity.LockDuration);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _transport.Untrack(this);
        await _disposing.CancelAsync().AnyContext();

        try
        {
            await ReleaseUnsettledAsync().AnyContext();
            await _receiver.DisposeAsync().AnyContext();
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not close the receiver for {Source}; its messages will be delivered again when their locks expire", Source);
        }

        if (_entity.Instance)
            await _transport.Entities.TryDeleteInstanceSubscriptionAsync(_entity.Topic!, _entity.Subscription!).AnyContext();
    }

    private async Task ReleaseUnsettledAsync()
    {
        var unsettled = _unsettled.Values.ToList();
        _unsettled.Clear();
        if (unsettled.Count == 0)
            return;

        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        var now = _transport.TimeProvider.GetUtcNow();
        await Task.WhenAll(unsettled.Where(m => m.LeaseExpiresAt > now).Select(async message =>
        {
            try
            {
                await _receiver.AbandonMessageAsync(message.Received, cancellationToken: timeout.Token).AnyContext();
            }
            catch (Exception ex)
            {
                _transport.Logger.LogDebug(ex, "Could not release message {MessageId} from {Source}; it will be delivered again when its lock expires", message.MessageId, Source);
            }
        })).AnyContext();
    }

    private AzureServiceBusIncomingMessage GetDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not AzureServiceBusIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        ThrowIfDisposed();
        return delivery;
    }

    private void ThrowIfLeaseExpired(AzureServiceBusIncomingMessage delivery)
    {
        if (delivery.LeaseExpiresAt is { } expiresAt && _transport.TimeProvider.GetUtcNow() >= expiresAt)
            throw LeaseLost(delivery, null);
    }

    private MessageLeaseLostException LeaseLost(AzureServiceBusIncomingMessage delivery, Exception? innerException)
    {
        _unsettled.TryRemove(delivery.Received.LockToken, out _);
        string message = $"The lock of message {delivery.MessageId} from {Source} was lost.";
        return innerException is null ? new MessageLeaseLostException(message) : new MessageLeaseLostException(message, innerException);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1 || _transport.IsDisposed, this);
}
