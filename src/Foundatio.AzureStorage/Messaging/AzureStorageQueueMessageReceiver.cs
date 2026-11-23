using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Receives from one Storage queue. Storage cannot long-poll, so a receive polls, waiting from
/// <see cref="AzureStorageQueueMessageTransportOptions.MinPollInterval"/> after the first empty poll, twice as long
/// after each further one, up to <see cref="AzureStorageQueueMessageTransportOptions.MaxPollInterval"/>; a message
/// received, or one this transport sends or retries to the queue, ends the wait. The wait carries over to the next
/// receive, so an idle receiver keeps polling at the longest interval. Each delivery's settlements run one at a time,
/// since a renewal or abandon gives the message a new pop receipt that the next settlement needs.
/// </summary>
internal sealed class AzureStorageQueueMessageReceiver : IMessageReceiver
{
    /// <summary>How often, at most, an idle durable subscription receiver checks that its subscription was not deleted.</summary>
    private static readonly TimeSpan SubscriptionCheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long disposing waits for a settlement in flight before it releases the message anyway.</summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private readonly AzureStorageQueueMessageTransport _transport;
    private readonly QueueClient _queue;
    private readonly string _queueName;
    private readonly string? _deadLetterName;
    private readonly TimeSpan _lease;
    private readonly AzureStorageQueueSignal _signal;
    private readonly ConcurrentDictionary<AzureStorageQueueIncomingMessage, byte> _inFlight = new();
    private readonly CancellationTokenSource _disposing = new();
    private Task _heartbeat = Task.CompletedTask;
    private int _emptyPolls;
    private long _subscriptionCheckedAt = Stopwatch.GetTimestamp();
    private int _disposed;

    public AzureStorageQueueMessageReceiver(AzureStorageQueueMessageTransport transport, MessageSource source, ReceiverOptions options, string queueName, string? deadLetterName)
    {
        _transport = transport;
        Source = source;
        _queueName = queueName;
        _deadLetterName = deadLetterName;
        _queue = transport.GetQueue(queueName);
        _lease = AzureStorageQueueMessageTransport.ToVisibilityTimeout(options.LeaseDuration);
        _signal = transport.GetSignal(queueName);
    }

    public MessageSource Source { get; }

    public void Start()
    {
        if (Source.Kind == MessageSourceKind.InstanceSubscription)
            _heartbeat = Task.Run(HeartbeatLoopAsync, CancellationToken.None);
    }

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        int count = Math.Min(maxMessages, AzureStorageQueueMessageTransport.MaxReceiveBatchSize);
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            var wake = _signal.WaitAsync();
            Task<Response<QueueMessage[]>>? poll = null;
            QueueMessage[] messages;
            try
            {
                // Not cancelled with the receive: Storage may hand messages to a request the client gave up on, which
                // would then stay invisible for the lease, so a poll the receive stops waiting for runs out and its
                // messages are released.
                poll = _queue.ReceiveMessagesAsync(count, _lease, CancellationToken.None);
                messages = (await poll.WaitAsync(receiveCancellation.Token).AnyContext()).Value;
            }
            catch (OperationCanceledException) when (receiveCancellation.IsCancellationRequested && poll is not null)
            {
                _ = ReleaseAbandonedPollAsync(poll);
                if (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    return [];

                throw;
            }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return [];
            }
            catch (RequestFailedException ex) when (AzureStorageQueueMessageTransport.IsQueueNotFound(ex))
            {
                throw new MessageReceiverFaultedException($"The queue {_queueName} of {Source} no longer exists.", ex);
            }
            catch (RequestFailedException ex)
            {
                throw new MessageBusException($"Could not receive from {Source}: {ex.Message}", ex);
            }

            if (messages.Length > 0)
            {
                Volatile.Write(ref _emptyPolls, 0);
                return ToIncomingMessages(messages);
            }

            if (Source.Kind == MessageSourceKind.Subscription)
                await ThrowIfUnsubscribedAsync(receiveCancellation.Token).AnyContext();

            var remaining = maxWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return [];

            var interval = NextPollInterval();
            if (!await WaitAsync(interval < remaining ? interval : remaining, wake, cancellationToken, receiveCancellation.Token).AnyContext())
                return [];
        }
    }

    public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        return SettleAsync(delivery, "complete", settles: true, async token =>
        {
            await _queue.DeleteMessageAsync(delivery.QueueMessageId, delivery.PopReceipt, token).AnyContext();
        }, cancellationToken);
    }

    /// <remarks>Storage keeps a message invisible for at most 7 days, so a longer delay is shortened to that.</remarks>
    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        var visibility = AzureStorageQueueMessageTransport.ToVisibilityTimeout(delay);
        await SettleAsync(delivery, "abandon", settles: true, async token =>
        {
            await _queue.UpdateMessageAsync(delivery.QueueMessageId, delivery.PopReceipt, visibilityTimeout: visibility, cancellationToken: token).AnyContext();
        }, cancellationToken).AnyContext();

        if (visibility == TimeSpan.Zero)
            _transport.Signal(_queueName);
        else
            _transport.SignalAfter(_queueName, visibility);
    }

    public Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        if (_deadLetterName is null)
            throw new InvalidOperationException($"{Source.Kind} sources do not have a dead-letter queue.");

        var delivery = GetDelivery(message);
        return SettleAsync(delivery, "dead-letter", settles: true, async token =>
        {
            // Renewing first proves the lease is still held, so a stale delivery is not copied to the dead-letter queue.
            var renewed = await _queue.UpdateMessageAsync(delivery.QueueMessageId, delivery.PopReceipt, visibilityTimeout: _lease, cancellationToken: token).AnyContext();
            delivery.PopReceipt = renewed.Value.PopReceipt;

            string text = AzureStorageQueueMessageTransportCodec.EncodeDeadLetter(delivery, reason ?? String.Empty);
            await _transport.SendDeadLetterAsync(_deadLetterName, Source, text, token).AnyContext();
            await _queue.DeleteMessageAsync(delivery.QueueMessageId, delivery.PopReceipt, token).AnyContext();
        }, cancellationToken);
    }

    public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        return SettleAsync(delivery, "renew", settles: false, async token =>
        {
            var renewed = await _queue.UpdateMessageAsync(delivery.QueueMessageId, delivery.PopReceipt, visibilityTimeout: _lease, cancellationToken: token).AnyContext();
            delivery.PopReceipt = renewed.Value.PopReceipt;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _transport.Untrack(this);
        await _disposing.CancelAsync().AnyContext();
        await _heartbeat.AnyContext();

        try
        {
            if (Source.Kind == MessageSourceKind.InstanceSubscription)
                await _transport.DeleteQueueAsync(_queueName).AnyContext();
            else
                await ReleaseAsync(_inFlight.Keys.ToList()).AnyContext();
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not clean up the receiver for {Source}; its messages will be delivered again when their leases expire", Source);
        }

        _inFlight.Clear();
    }

    /// <summary>
    /// Runs one settlement of the delivery after any other in flight, with the pop receipt the last one left. A pop
    /// receipt Storage no longer accepts means the lease was lost. Whether a settlement that ends the delivery succeeds
    /// or the lease was lost, the receiver forgets the delivery; one that failed otherwise leaves it to be delivered
    /// again when its lease expires.
    /// </summary>
    private async Task SettleAsync(AzureStorageQueueIncomingMessage delivery, string operation, bool settles, Func<CancellationToken, Task> settle, CancellationToken cancellationToken)
    {
        await delivery.Gate.WaitAsync(cancellationToken).AnyContext();
        try
        {
            ThrowIfDisposed();
            if (delivery.Settled)
                throw new MessageLeaseLostException($"Message {delivery.MessageId} from {Source} was already settled.");

            await settle(cancellationToken).AnyContext();
            if (settles)
                delivery.Settled = true;
        }
        catch (RequestFailedException ex) when (AzureStorageQueueMessageTransport.IsLeaseLost(ex))
        {
            delivery.Settled = true;
            throw new MessageLeaseLostException($"The lease for message {delivery.MessageId} from {Source} was lost: {ex.ErrorCode}.", ex);
        }
        catch (RequestFailedException ex)
        {
            throw new MessageBusException($"Could not {operation} message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }
        finally
        {
            if (delivery.Settled)
                _inFlight.TryRemove(delivery, out _);

            delivery.Gate.Release();
        }
    }

    private IReadOnlyList<IncomingMessage> ToIncomingMessages(QueueMessage[] messages)
    {
        var result = new IncomingMessage[messages.Length];
        for (int i = 0; i < messages.Length; i++)
        {
            var message = messages[i];
            bool isEnvelope = AzureStorageQueueMessageTransportCodec.TryDecode(message.Body.ToMemory(), out var body, out string? messageId, out var headers, out string? groupKey);
            var delivery = new AzureStorageQueueIncomingMessage
            {
                Owner = this,
                QueueMessageId = message.MessageId,
                PopReceipt = message.PopReceipt,
                MessageId = isEnvelope ? messageId! : message.MessageId,
                Body = body,
                Headers = headers ?? EmptyHeaders,
                GroupKey = groupKey,
                DeliveryCount = (int)Math.Min(message.DequeueCount, Int32.MaxValue),
                EnqueuedAt = message.InsertedOn
            };

            _inFlight.TryAdd(delivery, 0);
            result[i] = delivery;
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    /// <summary>The wait after another empty poll: twice the last one, from the shortest to the longest interval.</summary>
    private TimeSpan NextPollInterval()
    {
        int polls = Interlocked.Increment(ref _emptyPolls);
        var options = _transport.Options;
        if (polls > 30)
            return options.MaxPollInterval;

        var interval = options.MinPollInterval * Math.Pow(2, polls - 1);
        return interval < options.MaxPollInterval ? interval : options.MaxPollInterval;
    }

    /// <summary>
    /// Waits between polls until the delay passes or the queue is signalled. Returns false, so the receive returns
    /// nothing, when the receiver is disposed meanwhile.
    /// </summary>
    private async Task<bool> WaitAsync(TimeSpan delay, Task wake, CancellationToken cancellationToken, CancellationToken receiveCancellation)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(receiveCancellation);
        var delayed = Task.Delay(delay, delayCancellation.Token);
        var completed = await Task.WhenAny(delayed, wake).AnyContext();
        await delayCancellation.CancelAsync().AnyContext();

        if (completed == wake)
        {
            Volatile.Write(ref _emptyPolls, 0);
            return true;
        }

        if (!receiveCancellation.IsCancellationRequested)
            return true;

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// Throws <see cref="MessageReceiverFaultedException"/> once the subscription was deleted, so the receiver does not
    /// silently wait on a queue publishes skip; its replacement subscribes again. Checked at most every few seconds,
    /// while the queue is empty.
    /// </summary>
    private async Task ThrowIfUnsubscribedAsync(CancellationToken cancellationToken)
    {
        long checkedAt = Volatile.Read(ref _subscriptionCheckedAt);
        if (Stopwatch.GetElapsedTime(checkedAt) < SubscriptionCheckInterval || Interlocked.CompareExchange(ref _subscriptionCheckedAt, Stopwatch.GetTimestamp(), checkedAt) != checkedAt)
            return;

        IDictionary<string, string> metadata;
        try
        {
            metadata = (await _queue.GetPropertiesAsync(cancellationToken).AnyContext()).Value.Metadata;
        }
        catch (RequestFailedException ex) when (AzureStorageQueueMessageTransport.IsQueueNotFound(ex))
        {
            throw new MessageReceiverFaultedException($"The queue {_queueName} of {Source} no longer exists.", ex);
        }
        catch (RequestFailedException ex)
        {
            _transport.Logger.LogDebug(ex, "Could not check that {Source} is still subscribed", Source);
            return;
        }

        bool subscribed = metadata.Any(m => String.Equals(m.Key, AzureStorageQueueMessageTransport.TopicMetadata, StringComparison.OrdinalIgnoreCase)
            && String.Equals(m.Value, Source.Topic, StringComparison.Ordinal));
        if (!subscribed)
            throw new MessageReceiverFaultedException($"{Source} was deleted, so publishes no longer reach it; a new receiver subscribes it again.");
    }

    private async Task HeartbeatLoopAsync()
    {
        var interval = _transport.Options.InstanceSubscriptionTimeout / 3;
        var token = _disposing.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token).AnyContext();
                await _transport.HeartbeatAsync(_queueName, Source.Topic!, token).AnyContext();
                await _transport.SweepInstancesAsync(Source.Topic!).AnyContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _transport.Logger.LogWarning(ex, "Could not renew instance subscription queue {QueueName} of topic {Topic}", _queueName, Source.Topic);
            }
        }
    }

    /// <summary>Makes the deliveries visible again at once, as many at a time as the transport allows, and wakes this process's receivers of the queue.</summary>
    private async Task ReleaseAsync(IReadOnlyList<AzureStorageQueueIncomingMessage> deliveries)
    {
        if (deliveries.Count == 0)
            return;

        await Parallel.ForEachAsync(deliveries, new ParallelOptions { MaxDegreeOfParallelism = AzureStorageQueueMessageTransport.MaxRequestsInFlight }, async (delivery, _) =>
        {
            bool entered = await delivery.Gate.WaitAsync(ReleaseTimeout).AnyContext();
            try
            {
                if (delivery.Settled)
                    return;

                delivery.Settled = true;
                await _queue.UpdateMessageAsync(delivery.QueueMessageId, delivery.PopReceipt, visibilityTimeout: TimeSpan.Zero).AnyContext();
            }
            catch (RequestFailedException ex)
            {
                _transport.Logger.LogDebug(ex, "Could not release message {MessageId} from {Source}", delivery.MessageId, Source);
            }
            finally
            {
                if (entered)
                    delivery.Gate.Release();
            }
        }).AnyContext();

        _transport.Signal(_queueName);
    }

    /// <summary>Releases the messages of a poll the receive stopped waiting for, once it returns.</summary>
    private async Task ReleaseAbandonedPollAsync(Task<Response<QueueMessage[]>> poll)
    {
        QueueMessage[] messages;
        try
        {
            messages = (await poll.AnyContext()).Value;
        }
        catch (Exception ex)
        {
            _transport.Logger.LogDebug(ex, "A poll of {Source} the receive stopped waiting for failed", Source);
            return;
        }

        if (messages.Length == 0)
            return;

        try
        {
            await Parallel.ForEachAsync(messages, new ParallelOptions { MaxDegreeOfParallelism = AzureStorageQueueMessageTransport.MaxRequestsInFlight },
                async (message, _) => await _queue.UpdateMessageAsync(message.MessageId, message.PopReceipt, visibilityTimeout: TimeSpan.Zero).AnyContext()).AnyContext();
            _transport.Signal(_queueName);
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not release {Count} messages of a cancelled receive from {Source}; they will be delivered again when their leases expire", messages.Length, Source);
        }
    }

    private AzureStorageQueueIncomingMessage GetDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not AzureStorageQueueIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        ThrowIfDisposed();
        return delivery;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}

/// <summary>A delivery from a Storage queue, with the pop receipt its next settlement uses.</summary>
internal sealed class AzureStorageQueueIncomingMessage : IncomingMessage
{
    public required AzureStorageQueueMessageReceiver Owner { get; init; }

    public required string QueueMessageId { get; init; }

    /// <summary>Changed by each renewal; read and written only while <see cref="Gate"/> is held.</summary>
    public required string PopReceipt { get; set; }

    /// <summary>Runs the delivery's settlements one at a time.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Completed, abandoned, dead-lettered, released or lost; read and written only while <see cref="Gate"/> is held.</summary>
    public bool Settled { get; set; }
}
