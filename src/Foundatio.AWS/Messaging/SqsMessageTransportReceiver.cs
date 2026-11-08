using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS.Model;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using SqsMessage = Amazon.SQS.Model.Message;

namespace Foundatio.Messaging.Transport;

internal sealed class SqsMessageReceiver : IMessageReceiver
{
    private const int MaxWaitSecondsPerPoll = 20;
    private const int MaxVisibilityTimeoutSeconds = 12 * 60 * 60;
    private static readonly TimeSpan EarlyDeliveryTolerance = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LongPollRounding = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);
    private const string ReceiveCountAttribute = "ApproximateReceiveCount";
    private const string SentTimestampAttribute = "SentTimestamp";
    private const string FirstReceiveTimestampAttribute = "ApproximateFirstReceiveTimestamp";

    private readonly SqsMessageTransport _transport;
    private readonly string _queueUrl;
    private readonly string? _deadLetterQueueName;
    private readonly SqsInstanceSubscription? _instance;
    private readonly int _leaseSeconds;
    private readonly ConcurrentDictionary<string, SqsIncomingMessage> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposing = new();
    private Task _heartbeat = Task.CompletedTask;
    private int _disposed;

    public SqsMessageReceiver(SqsMessageTransport transport, MessageSource source, ReceiverOptions options, string queueUrl, string? deadLetterQueueName, SqsInstanceSubscription? instance)
    {
        _transport = transport;
        Source = source;
        _queueUrl = queueUrl;
        _deadLetterQueueName = deadLetterQueueName;
        _instance = instance;
        _leaseSeconds = ToSeconds(options.LeaseDuration, minimum: 1);
    }

    public MessageSource Source { get; }

    public void Start()
    {
        if (_instance is not null)
            _heartbeat = Task.Run(HeartbeatLoopAsync, CancellationToken.None);
    }

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        int count = Math.Min(maxMessages, SqsMessageTransport.MaxBatchSize);
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            // SQS long-polls in whole seconds. Wait the whole seconds that fit in maxWait, then short-poll the rest,
            // so a receive never waits noticeably longer than asked.
            var remaining = maxWait - Stopwatch.GetElapsedTime(started);
            int waitSeconds = remaining <= TimeSpan.Zero ? 0 : (int)Math.Min(MaxWaitSecondsPerPoll, Math.Floor((remaining + LongPollRounding).TotalSeconds));
            long leaseStarted = Stopwatch.GetTimestamp();

            ReceiveMessageResponse response;
            try
            {
                response = await _transport.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MaxNumberOfMessages = count,
                    WaitTimeSeconds = waitSeconds,
                    VisibilityTimeout = _leaseSeconds,
                    MessageSystemAttributeNames = [ReceiveCountAttribute, SentTimestampAttribute, FirstReceiveTimestampAttribute],
                    MessageAttributeNames = [SqsMessageTransportCodec.AttributeName]
                }, receiveCancellation.Token).AnyContext();
            }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return [];
            }
            catch (AmazonServiceException ex)
            {
                throw new MessageBusException($"Could not receive from {Source}: {ex.Message}", ex);
            }

            var messages = await ToIncomingMessagesAsync(response.Messages, leaseStarted).AnyContext();
            if (messages.Count > 0)
                return messages;

            remaining = maxWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return [];

            if (waitSeconds == 0)
            {
                try
                {
                    await Task.Delay(remaining < ShortPollInterval ? remaining : ShortPollInterval, receiveCancellation.Token).AnyContext();
                }
                catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return [];
                }
            }
        }
    }

    public async Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        try
        {
            await _transport.Sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = _queueUrl, ReceiptHandle = delivery.ReceiptHandle }, cancellationToken).AnyContext();
        }
        catch (AmazonServiceException ex) when (SqsMessageTransport.IsLeaseLost(ex))
        {
            throw LeaseLost(delivery, ex);
        }
        catch (AmazonServiceException ex)
        {
            throw new MessageBusException($"Could not complete message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        _inFlight.TryRemove(delivery.ReceiptHandle, out _);
    }

    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        ThrowIfLeaseExpired(delivery);
        await ChangeVisibilityAsync(delivery, ToSeconds(delay, minimum: 0), cancellationToken).AnyContext();
        _inFlight.TryRemove(delivery.ReceiptHandle, out _);
    }

    public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        if (_deadLetterQueueName is null)
            throw new InvalidOperationException("Instance subscriptions do not have a dead-letter queue.");

        var delivery = GetDelivery(message);

        // Renewing first proves the lease is still held, so a stale delivery is not copied to the dead-letter queue.
        await RenewAsync(delivery, cancellationToken).AnyContext();
        try
        {
            await _transport.SendDeadLetterAsync(_deadLetterQueueName, delivery, reason, cancellationToken).AnyContext();
        }
        catch (AmazonServiceException ex)
        {
            throw new MessageBusException($"Could not dead-letter message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        await CompleteAsync(delivery, cancellationToken).AnyContext();
    }

    public async Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        ThrowIfLeaseExpired(delivery);
        long started = Stopwatch.GetTimestamp();
        await ChangeVisibilityAsync(delivery, _leaseSeconds, cancellationToken).AnyContext();
        delivery.LeaseDeadline = started + _leaseSeconds * Stopwatch.Frequency;
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
            if (_instance is not null)
                await _transport.RemoveInstanceAsync(_instance).AnyContext();
            else
                await ReleaseInFlightAsync().AnyContext();
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not clean up the receiver for {Source}; its messages will be delivered again when their leases expire", Source);
        }
    }

    private async Task<IReadOnlyList<IncomingMessage>> ToIncomingMessagesAsync(List<SqsMessage>? messages, long leaseStarted)
    {
        if (messages is null || messages.Count == 0)
            return [];

        var now = _transport.TimeProvider.GetUtcNow();
        long leaseDeadline = leaseStarted + _leaseSeconds * Stopwatch.Frequency;
        var result = new List<IncomingMessage>(messages.Count);
        foreach (var message in messages)
        {
            var decoded = SqsMessageTransportCodec.Decode(message.Body, message.MessageAttributes?.GetValueOrDefault(SqsMessageTransportCodec.AttributeName)?.StringValue, message.MessageId);
            int deliveryCount = (int)Math.Clamp(GetNumber(message.Attributes, ReceiveCountAttribute) ?? 1, 1, Int32.MaxValue);

            if (decoded.DeliverAt is { } deliverAt)
            {
                var early = deliverAt - now;
                if (early > EarlyDeliveryTolerance)
                {
                    await HideUntilDueAsync(message, decoded.MessageId, early).AnyContext();
                    continue;
                }

                // The receive that hid an early publish counted as a delivery.
                if (GetNumber(message.Attributes, FirstReceiveTimestampAttribute) is { } firstReceived && DateTimeOffset.FromUnixTimeMilliseconds(firstReceived) < deliverAt - EarlyDeliveryTolerance)
                    deliveryCount = Math.Max(1, deliveryCount - 1);
            }

            var incoming = new SqsIncomingMessage
            {
                Owner = this,
                ReceiptHandle = message.ReceiptHandle,
                LeaseDeadline = leaseDeadline,
                MessageId = decoded.MessageId,
                Body = decoded.Body,
                Headers = decoded.Headers,
                GroupKey = decoded.GroupKey,
                DeliveryCount = deliveryCount,
                EnqueuedAt = GetNumber(message.Attributes, SentTimestampAttribute) is { } sent ? DateTimeOffset.FromUnixTimeMilliseconds(sent) : null
            };

            _inFlight[incoming.ReceiptHandle] = incoming;
            result.Add(incoming);
        }

        return result;
    }

    private async Task HideUntilDueAsync(SqsMessage message, string messageId, TimeSpan early)
    {
        try
        {
            await _transport.Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = _queueUrl,
                ReceiptHandle = message.ReceiptHandle,
                VisibilityTimeout = ToSeconds(early, minimum: 1)
            }).AnyContext();
        }
        catch (AmazonServiceException ex)
        {
            _transport.Logger.LogDebug(ex, "Could not hide early message {MessageId} from {Source}; it will be received again when its lease expires", messageId, Source);
        }
    }

    private async Task ChangeVisibilityAsync(SqsIncomingMessage delivery, int seconds, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = _queueUrl,
                ReceiptHandle = delivery.ReceiptHandle,
                VisibilityTimeout = seconds
            }, cancellationToken).AnyContext();
        }
        catch (AmazonServiceException ex) when (SqsMessageTransport.IsLeaseLost(ex))
        {
            throw LeaseLost(delivery, ex);
        }
        catch (AmazonServiceException ex)
        {
            throw new MessageBusException($"Could not change the visibility of message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }
    }

    private async Task ReleaseInFlightAsync()
    {
        var unsettled = _inFlight.Values.Where(m => Stopwatch.GetTimestamp() < m.LeaseDeadline).ToList();
        _inFlight.Clear();
        if (unsettled.Count == 0)
            return;

        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        foreach (var chunk in unsettled.Chunk(SqsMessageTransport.MaxBatchSize))
        {
            await _transport.Sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
            {
                QueueUrl = _queueUrl,
                Entries = chunk.Select((m, i) => new ChangeMessageVisibilityBatchRequestEntry
                {
                    Id = i.ToString(CultureInfo.InvariantCulture),
                    ReceiptHandle = m.ReceiptHandle,
                    VisibilityTimeout = 0
                }).ToList()
            }, timeout.Token).AnyContext();
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        var instance = _instance!;
        var interval = _transport.Options.InstanceSubscriptionTimeout / 3;
        var token = _disposing.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _transport.TimeProvider, token).AnyContext();
                await _transport.HeartbeatAsync(instance.QueueUrl, token).AnyContext();
                await _transport.SweepInstancesAsync(instance).AnyContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _transport.Logger.LogWarning(ex, "Could not renew instance subscription queue {QueueName} of topic {Topic}", instance.QueueName, instance.TopicName);
            }
        }
    }

    private SqsIncomingMessage GetDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not SqsIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        return delivery;
    }

    /// <summary>
    /// A lease past its deadline may already belong to another receiver; SQS would apply a visibility change to that
    /// newer delivery, so it is refused locally. The deadline is measured from before the receive request was sent.
    /// </summary>
    private void ThrowIfLeaseExpired(SqsIncomingMessage delivery)
    {
        if (Stopwatch.GetTimestamp() < delivery.LeaseDeadline)
            return;

        _inFlight.TryRemove(delivery.ReceiptHandle, out _);
        throw new MessageLeaseLostException($"The lease for message {delivery.MessageId} from {Source} expired.");
    }

    private MessageLeaseLostException LeaseLost(SqsIncomingMessage delivery, Exception innerException)
    {
        _inFlight.TryRemove(delivery.ReceiptHandle, out _);
        return new MessageLeaseLostException($"The lease for message {delivery.MessageId} from {Source} was lost.", innerException);
    }

    private static long? GetNumber(Dictionary<string, string>? attributes, string name) =>
        attributes?.GetValueOrDefault(name) is { } value && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number : null;

    private static int ToSeconds(TimeSpan value, int minimum) =>
        (int)Math.Clamp(Math.Ceiling(value.TotalSeconds), minimum, MaxVisibilityTimeoutSeconds);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1 || _transport.IsDisposed, this);

    private sealed class SqsIncomingMessage : IncomingMessage
    {
        private long _leaseDeadline;

        public required SqsMessageReceiver Owner { get; init; }
        public required string ReceiptHandle { get; init; }

        /// <summary>When the lease ends, as a <see cref="Stopwatch"/> timestamp.</summary>
        public required long LeaseDeadline
        {
            get => Volatile.Read(ref _leaseDeadline);
            set => Volatile.Write(ref _leaseDeadline, value);
        }
    }
}
