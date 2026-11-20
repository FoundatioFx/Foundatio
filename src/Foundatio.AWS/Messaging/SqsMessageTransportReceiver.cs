using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using SqsMessage = Amazon.SQS.Model.Message;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Receives from one SQS queue. Besides leasing deliveries it deletes messages sent before the queue's last purge and
/// purge markers, and hides messages that arrive before their delivery time; on a FIFO queue it then also releases the
/// later messages of that group it received, so none is handed out ahead of the hidden one. On a standard queue that
/// already has <see cref="MaxHiddenInFlight"/> messages in flight, an early message is sent again with a delay of up to
/// 15 minutes instead, since delayed messages do not count toward SQS's in-flight quota. Receives may run concurrently:
/// SQS hands a message, and a FIFO group with a message in flight, to one of them only, each receive handles the early
/// and held-back messages it got itself, with batch calls, and they share one reading of the in-flight count.
/// Completions, renewals and abandons are sent in batches too, as a group commit (<see cref="SqsSettlementBatcher{TEntry}"/>).
/// </summary>
internal sealed class SqsMessageReceiver : IMessageReceiver
{
    private const int MaxWaitSecondsPerPoll = 20;
    private const int MaxVisibilityTimeoutSeconds = SqsMessageTransport.MaxVisibilityTimeoutSeconds;
    private static readonly TimeSpan EarlyDeliveryTolerance = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LongPollRounding = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ShortPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A message that arrives early is hidden until the next multiple of this since its first receive (or until due),
    /// so it is received once per interval. It is short of the 12 hours SQS allows by <see cref="HideIntervalTolerance"/>.
    /// </summary>
    private static readonly TimeSpan HideInterval = TimeSpan.FromHours(11);

    /// <summary>How far the transport's clock may run ahead of SQS's without a hide ending in the interval it began in.</summary>
    private static readonly TimeSpan HideIntervalTolerance = TimeSpan.FromMinutes(30);

    private const string ReceiveCountAttribute = SqsMessageTransport.ReceiveCountAttribute;
    private const string SentTimestampAttribute = SqsMessageTransport.SentTimestampAttribute;
    private const string FirstReceiveTimestampAttribute = SqsMessageTransport.FirstReceiveTimestampAttribute;
    private const string MessageGroupIdAttribute = "MessageGroupId";
    private const int MaxNativeDelaySeconds = 15 * 60;

    /// <summary>
    /// How many messages may be in flight on a standard queue before early messages are sent again with a delay rather
    /// than hidden. SQS stops returning messages from a standard queue with about <see cref="InFlightQuota"/> in flight;
    /// the difference leaves room for the <see cref="InFlightSlice"/> each of about 70 receivers may hide before it
    /// reads the count again.
    /// </summary>
    private const long MaxHiddenInFlight = 50_000;

    /// <summary>About how many messages SQS lets a standard queue have in flight before it refuses receives with <c>OverLimit</c>.</summary>
    private const long InFlightQuota = 120_000;

    /// <summary>
    /// How many messages a receiver hides before it reads the queue's in-flight count again, so receivers hiding a
    /// burst together see each other's hides, and the quota is not overrun by many receivers' unread hides.
    /// </summary>
    private const int InFlightSlice = 1_000;

    /// <summary>How often the number of messages in flight is read from SQS while early messages arrive, at the latest.</summary>
    private static readonly TimeSpan InFlightCheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long after SQS reported the queue over its in-flight quota early messages are sent again rather than hidden.</summary>
    private static readonly TimeSpan OverLimitInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a receive SQS refused with <c>OverLimit</c> waits before it asks again, within its wait time.</summary>
    private static readonly TimeSpan OverLimitRetryInterval = TimeSpan.FromSeconds(1);

    private readonly SqsMessageTransport _transport;
    private readonly string _queueUrl;
    private readonly string? _deadLetterQueueName;
    private readonly SqsInstanceSubscription? _instance;
    private readonly long? _purgedBefore;
    private readonly bool _fifo;
    private readonly int _leaseSeconds;
    private readonly ConcurrentDictionary<string, SqsIncomingMessage> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposing = new();
    private readonly SemaphoreSlim _inFlightCheck = new(1, 1);
    private readonly SqsSettlementBatcher<SqsIncomingMessage> _completions;
    private readonly SqsSettlementBatcher<(SqsIncomingMessage Delivery, int Seconds)> _visibilityChanges;

    /// <summary>
    /// The receipt handles of the last <see cref="InFlightSlice"/> messages this receiver hid on a standard queue, with
    /// when they become visible, so it can release them to be sent again when SQS refuses receives with <c>OverLimit</c>.
    /// </summary>
    private readonly ConcurrentQueue<(string ReceiptHandle, long HiddenUntil)> _hidden = new();

    private Task _heartbeat = Task.CompletedTask;
    private long _inFlightCheckedAt;
    private long _measuredInFlight;
    private long _hiddenSinceCheck;
    private long _overLimitUntil;
    private int _disposed;

    public SqsMessageReceiver(SqsMessageTransport transport, MessageSource source, ReceiverOptions options, string queueUrl, string? deadLetterQueueName,
        SqsInstanceSubscription? instance, long? purgedBefore)
    {
        _transport = transport;
        Source = source;
        _queueUrl = queueUrl;
        _deadLetterQueueName = deadLetterQueueName;
        _instance = instance;
        _purgedBefore = purgedBefore;
        _fifo = transport.Options.Fifo;
        _leaseSeconds = ToSeconds(options.LeaseDuration, minimum: 1);
        _completions = new SqsSettlementBatcher<SqsIncomingMessage>(DeleteDeliveriesAsync);
        _visibilityChanges = new SqsSettlementBatcher<(SqsIncomingMessage Delivery, int Seconds)>(ChangeDeliveryVisibilityAsync);
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
                    MessageSystemAttributeNames = _fifo
                        ? [ReceiveCountAttribute, SentTimestampAttribute, FirstReceiveTimestampAttribute, MessageGroupIdAttribute]
                        : [ReceiveCountAttribute, SentTimestampAttribute, FirstReceiveTimestampAttribute],
                    MessageAttributeNames = [SqsMessageTransportCodec.AttributeName],
                    // Lets the SDK's retry of a failed FIFO receive return the same messages instead of leaving them locked.
                    ReceiveRequestAttemptId = _fifo ? Guid.NewGuid().ToString("N") : null
                }, receiveCancellation.Token).AnyContext();
            }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return [];
            }
            catch (AmazonServiceException ex) when (IsOverLimit(ex))
            {
                await OnOverLimitAsync(ex).AnyContext();
                remaining = maxWait - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero || !await TryDelayAsync(remaining < OverLimitRetryInterval ? remaining : OverLimitRetryInterval, cancellationToken, receiveCancellation.Token).AnyContext())
                    return [];

                continue;
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

            if (waitSeconds == 0 && !await TryDelayAsync(remaining < ShortPollInterval ? remaining : ShortPollInterval, cancellationToken, receiveCancellation.Token).AnyContext())
                return [];
        }
    }

    /// <remarks>
    /// Whether the delete succeeds or not, the receiver forgets the delivery: SQS redelivers a message it did not delete
    /// once the lease expires.
    /// </remarks>
    public async Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        try
        {
            await _completions.SendAsync(delivery, cancellationToken).AnyContext();
        }
        finally
        {
            _inFlight.TryRemove(delivery.ReceiptHandle, out _);
        }
    }

    /// <remarks>
    /// SQS keeps a message invisible for at most 12 hours from the receive, so a delay that would end later is shortened
    /// to that limit rather than rejected.
    /// </remarks>
    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        try
        {
            ThrowIfLeaseExpired(delivery);
            await _visibilityChanges.SendAsync((delivery, Math.Min(ToSeconds(delay, minimum: 0), RemainingVisibilitySeconds(delivery.ReceivedAt))), cancellationToken).AnyContext();
        }
        finally
        {
            _inFlight.TryRemove(delivery.ReceiptHandle, out _);
        }
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
            await _transport.SendDeadLetterAsync(_deadLetterQueueName, delivery, delivery.DeliveryId, reason, cancellationToken).AnyContext();
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
        int seconds = Math.Min(_leaseSeconds, RemainingVisibilitySeconds(delivery.ReceivedAt));
        if (seconds <= 0)
            throw LeaseLost(delivery, new InvalidOperationException("SQS does not keep a message invisible for more than 12 hours from its receive."));

        await _visibilityChanges.SendAsync((delivery, seconds), cancellationToken).AnyContext();
        delivery.LeaseDeadline = started + seconds * Stopwatch.Frequency;
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

    /// <summary>Waits between polls; returns false, so the receive returns nothing, when the receiver is disposed meanwhile.</summary>
    private async Task<bool> TryDelayAsync(TimeSpan delay, CancellationToken cancellationToken, CancellationToken receiveCancellation)
    {
        try
        {
            await Task.Delay(delay, receiveCancellation).AnyContext();
            return true;
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<IncomingMessage>> ToIncomingMessagesAsync(List<SqsMessage>? messages, long leaseStarted)
    {
        if (messages is null || messages.Count == 0)
            return [];

        var now = _transport.TimeProvider.GetUtcNow();
        long leaseDeadline = leaseStarted + _leaseSeconds * Stopwatch.Frequency;
        var result = new List<IncomingMessage>(messages.Count);
        SetAside? setAside = null;
        HashSet<string>? heldGroups = null;
        foreach (var message in messages)
        {
            var decoded = SqsMessageTransportCodec.Decode(message.Body, message.MessageAttributes?.GetValueOrDefault(SqsMessageTransportCodec.AttributeName), message.MessageId);
            long? sentTimestamp = SqsMessageTransport.GetNumber(message.Attributes, SentTimestampAttribute);
            if (decoded.IsPurgeMarker || sentTimestamp <= _purgedBefore)
            {
                (setAside ??= new SetAside()).Deletes.Add(new Deletion(message, decoded.MessageId, SentAgain: false));
                continue;
            }

            // FIFO returns a group's messages in order; while an earlier one is hidden, the later ones must wait too.
            string? groupId = _fifo ? message.Attributes?.GetValueOrDefault(MessageGroupIdAttribute) : null;
            if (groupId is not null && heldGroups?.Contains(groupId) == true)
            {
                (setAside ??= new SetAside()).HeldBack.Add((message, decoded.MessageId));
                continue;
            }

            long receiveCount = SqsMessageTransport.GetNumber(message.Attributes, ReceiveCountAttribute) ?? 1;
            int deliveryCount = (int)Math.Clamp(receiveCount, 1, Int32.MaxValue);
            if (decoded.DeliverAt is { } deliverAt)
            {
                var early = deliverAt - now;
                if (early > EarlyDeliveryTolerance)
                {
                    (setAside ??= new SetAside()).Early.Add(new EarlyMessage(message, decoded, early));
                    if (groupId is not null)
                        (heldGroups ??= new HashSet<string>(StringComparer.Ordinal)).Add(groupId);
                    continue;
                }

                deliveryCount = Math.Max(1, deliveryCount - GetEarlyReceives(message, deliverAt));
            }

            var incoming = new SqsIncomingMessage
            {
                Owner = this,
                ReceiptHandle = message.ReceiptHandle,
                DeliveryId = $"{message.MessageId}-{receiveCount.ToString(CultureInfo.InvariantCulture)}",
                ReceivedAt = leaseStarted,
                LeaseDeadline = leaseDeadline,
                MessageId = decoded.MessageId,
                Body = decoded.Body,
                Headers = decoded.Headers,
                GroupKey = decoded.GroupKey,
                DeliveryCount = deliveryCount,
                EnqueuedAt = sentTimestamp is { } sent ? DateTimeOffset.FromUnixTimeMilliseconds(sent) : null
            };

            _inFlight[incoming.ReceiptHandle] = incoming;
            result.Add(incoming);
        }

        if (setAside is not null)
            await SetAsideAsync(setAside, now, leaseStarted).AnyContext();

        return result;
    }

    /// <summary>
    /// Deals with what a receive got but does not deliver, in batch calls rather than one call per message: early
    /// messages are hidden until due, or on a crowded standard queue sent again with a delay; held-back FIFO messages
    /// are released; messages sent before the last purge, purge markers and the originals of messages sent again are
    /// deleted. An early message SQS refuses to hide because the queue is over its in-flight quota is sent again too.
    /// </summary>
    private async Task SetAsideAsync(SetAside setAside, DateTimeOffset now, long leaseStarted)
    {
        var hides = setAside.Early;
        if (hides.Count > 0 && !_fifo && await IsCrowdedAsync(hides.Count).AnyContext())
            hides = await SendAgainAsync(hides, setAside.Deletes).AnyContext();

        if (hides.Count > 0 || setAside.HeldBack.Count > 0)
        {
            var refused = await HideAsync(hides, setAside.HeldBack, now, leaseStarted).AnyContext();
            if (refused.Count > 0)
            {
                foreach (var early in await SendAgainAsync(refused, setAside.Deletes).AnyContext())
                    _transport.Logger.LogWarning("Could not hide or send again early message {MessageId} from {Source}; it will be received again when its lease expires", early.Decoded.MessageId, Source);
            }
        }

        if (setAside.Deletes.Count > 0)
            await DeleteSetAsideAsync(setAside.Deletes).AnyContext();
    }

    /// <summary>
    /// Hides early messages until due and releases held-back ones at once, in batches. Returns the early messages SQS
    /// refused to hide because the queue is over its in-flight quota, on a standard queue, so they can be sent again.
    /// </summary>
    private async Task<List<EarlyMessage>> HideAsync(List<EarlyMessage> hides, List<(SqsMessage Message, string MessageId)> heldBack, DateTimeOffset now, long leaseStarted)
    {
        var changes = new List<(string ReceiptHandle, int Seconds)>(hides.Count + heldBack.Count);
        foreach (var early in hides)
            changes.Add((early.Message.ReceiptHandle, GetHideSeconds(early.Message, early.Early, now, leaseStarted)));
        foreach (var (message, _) in heldBack)
            changes.Add((message.ReceiptHandle, 0));

        var errors = await ChangeVisibilityAsync(changes, CancellationToken.None).AnyContext();
        var refused = new List<EarlyMessage>();
        int hidden = 0;
        for (int i = 0; i < changes.Count; i++)
        {
            var (receiptHandle, seconds) = changes[i];
            bool isHide = i < hides.Count;
            string messageId = isHide ? hides[i].Decoded.MessageId : heldBack[i - hides.Count].MessageId;
            if (errors[i] is not { } error)
            {
                if (isHide)
                {
                    hidden++;
                    RememberHidden(receiptHandle, now, seconds);
                }

                continue;
            }

            if (isHide && IsOverLimit(error))
            {
                MarkOverLimit();
                if (!_fifo)
                {
                    refused.Add(hides[i]);
                    continue;
                }
            }

            _transport.Logger.Log(isHide ? LogLevel.Warning : LogLevel.Debug, error, "Could not change the visibility of message {MessageId} from {Source} to {Seconds} seconds; it will be received again when its lease expires", messageId, Source, seconds);
        }

        Interlocked.Add(ref _hiddenSinceCheck, hidden);
        return refused;
    }

    /// <summary>
    /// How long to hide a message that arrived <paramref name="early"/>: until it is due, but at most until the end of
    /// the current <see cref="HideInterval"/> since its first receive, and never past the 12 hours SQS allows from
    /// this receive (it rejects a longer visibility timeout).
    /// </summary>
    private static int GetHideSeconds(SqsMessage message, TimeSpan early, DateTimeOffset now, long receivedAt)
    {
        var hide = early;
        if (SqsMessageTransport.GetNumber(message.Attributes, FirstReceiveTimestampAttribute) is { } firstReceived)
        {
            var sinceFirstReceive = now - DateTimeOffset.FromUnixTimeMilliseconds(firstReceived);
            long interval = Math.Max(0, (long)Math.Floor((sinceFirstReceive + HideIntervalTolerance) / HideInterval));
            var intervalEnd = HideInterval * (interval + 1) - sinceFirstReceive;
            if (intervalEnd < hide)
                hide = intervalEnd;
        }

        return Math.Min(ToSeconds(hide, minimum: 1), RemainingVisibilitySeconds(receivedAt));
    }

    /// <summary>
    /// How many receives hid the message before it was due: one per <see cref="HideInterval"/> since its first receive,
    /// as <see cref="GetHideSeconds"/> schedules them. They are not deliveries, so they are not counted as attempts.
    /// </summary>
    private static int GetEarlyReceives(SqsMessage message, DateTimeOffset deliverAt)
    {
        if (SqsMessageTransport.GetNumber(message.Attributes, FirstReceiveTimestampAttribute) is not { } firstReceived)
            return 0;

        var hiddenFor = deliverAt - EarlyDeliveryTolerance - DateTimeOffset.FromUnixTimeMilliseconds(firstReceived);
        return hiddenFor > TimeSpan.Zero ? (int)Math.Ceiling(hiddenFor / HideInterval) : 0;
    }

    /// <summary>
    /// Whether the queue has so many messages in flight that hiding <paramref name="count"/> more early ones risks
    /// SQS's in-flight quota, past which no message of the queue can be received. The count is read from SQS once this
    /// receiver has hidden <see cref="InFlightSlice"/> messages since the last read, and at least every
    /// <see cref="InFlightCheckInterval"/>, adding what it has hidden since. Concurrent receives wait for a read in
    /// progress rather than judge by the count it is replacing. Once SQS reported the queue over its quota, it is
    /// crowded for <see cref="OverLimitInterval"/>.
    /// </summary>
    private async Task<bool> IsCrowdedAsync(int count)
    {
        if (IsOverLimitNow())
            return true;

        if (IsInFlightCountStale())
        {
            await _inFlightCheck.WaitAsync().AnyContext();
            try
            {
                if (IsInFlightCountStale())
                    await ReadInFlightCountAsync().AnyContext();
            }
            finally
            {
                _inFlightCheck.Release();
            }
        }

        return Interlocked.Read(ref _measuredInFlight) + Interlocked.Read(ref _hiddenSinceCheck) + count > MaxHiddenInFlight;
    }

    private bool IsInFlightCountStale()
    {
        long checkedAt = Interlocked.Read(ref _inFlightCheckedAt);
        if (checkedAt == 0 || Interlocked.Read(ref _hiddenSinceCheck) >= InFlightSlice)
            return true;

        // A clock set back counts as stale too, so the count is never trusted for longer than the interval.
        long age = _transport.TimeProvider.GetUtcNow().UtcTicks - checkedAt;
        return age < 0 || age > InFlightCheckInterval.Ticks;
    }

    private bool IsOverLimitNow()
    {
        long until = Interlocked.Read(ref _overLimitUntil);
        return until != 0 && _transport.TimeProvider.GetUtcNow().UtcTicks < until;
    }

    /// <summary>
    /// Reads the queue's in-flight count. Messages hidden while the read runs may be missing from it, so only those
    /// hidden before it started stop being counted separately.
    /// </summary>
    private async Task ReadInFlightCountAsync()
    {
        long hiddenBefore = Interlocked.Read(ref _hiddenSinceCheck);
        try
        {
            var response = await _transport.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = _queueUrl,
                AttributeNames = [QueueAttributeName.ApproximateNumberOfMessagesNotVisible]
            }).AnyContext();

            Interlocked.Exchange(ref _measuredInFlight, SqsMessageTransport.GetNumber(response.Attributes, QueueAttributeName.ApproximateNumberOfMessagesNotVisible) ?? 0);
            Interlocked.Add(ref _hiddenSinceCheck, -hiddenBefore);
        }
        catch (AmazonServiceException ex)
        {
            _transport.Logger.LogDebug(ex, "Could not read how many messages of {Source} are in flight", Source);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlightCheckedAt, _transport.TimeProvider.GetUtcNow().UtcTicks);
        }
    }

    /// <summary>
    /// SQS refused a receive because the queue is over its in-flight quota, so none of its messages can be received
    /// until some settle or become visible. Early messages are sent again rather than hidden for a while, and the ones
    /// this receiver hid most recently are released to be received and sent again, freeing their place in the quota.
    /// </summary>
    private async Task OnOverLimitAsync(AmazonServiceException ex)
    {
        bool alreadyOverLimit = IsOverLimitNow();
        MarkOverLimit();

        var now = _transport.TimeProvider.GetUtcNow().UtcTicks;
        var releases = new List<(string ReceiptHandle, int Seconds)>();
        while (_hidden.TryDequeue(out var hidden))
        {
            if (hidden.HiddenUntil > now)
                releases.Add((hidden.ReceiptHandle, 0));
        }

        _transport.Logger.Log(alreadyOverLimit ? LogLevel.Debug : LogLevel.Warning, ex,
            "SQS refused to receive from {Source} because the queue is over its quota of messages in flight. Early messages will be sent again with a delay instead of hidden, and {Count} this receiver hid are released to be sent again",
            Source, releases.Count);

        if (releases.Count == 0)
            return;

        var errors = await ChangeVisibilityAsync(releases, CancellationToken.None).AnyContext();
        int failed = errors.Count(e => e is not null);
        if (failed > 0)
            _transport.Logger.LogDebug("Could not release {Count} hidden messages of {Source}; they become visible when due", failed, Source);
    }

    private void MarkOverLimit() => Interlocked.Exchange(ref _overLimitUntil, (_transport.TimeProvider.GetUtcNow() + OverLimitInterval).UtcTicks);

    private void RememberHidden(string receiptHandle, DateTimeOffset now, int seconds)
    {
        if (_fifo)
            return;

        _hidden.Enqueue((receiptHandle, now.AddSeconds(seconds).UtcTicks));
        while (_hidden.Count > InFlightSlice)
            _hidden.TryDequeue(out _);
    }

    private static bool IsOverLimit(Exception ex) => ex is OverLimitException || ex is AmazonServiceException { ErrorCode: "OverLimit" or "AWS.SimpleQueueService.OverLimit" };

    /// <summary>
    /// Sends copies of early messages to the queue with a delay of up to 15 minutes, in batches, and adds the originals
    /// to <paramref name="deletes"/>. A copy is received for the first time, so the original's receives are not counted
    /// as attempts. Returns the messages whose copy could not be sent, to be hidden instead.
    /// </summary>
    private async Task<List<EarlyMessage>> SendAgainAsync(List<EarlyMessage> early, List<Deletion> deletes)
    {
        var notSent = new List<EarlyMessage>();
        var copies = new List<(EarlyMessage Early, SendMessageBatchRequestEntry Entry, int Size)>(early.Count);
        foreach (var message in early)
        {
            var envelope = message.Message.MessageAttributes?.GetValueOrDefault(SqsMessageTransportCodec.AttributeName);
            if (envelope?.BinaryValue is not { } envelopeBytes)
            {
                notSent.Add(message);
                continue;
            }

            byte[] envelopeCopy = envelopeBytes.ToArray();
            copies.Add((message, new SendMessageBatchRequestEntry
            {
                Id = copies.Count.ToString(CultureInfo.InvariantCulture),
                MessageBody = message.Message.Body,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    [SqsMessageTransportCodec.AttributeName] = new() { DataType = envelope.DataType, BinaryValue = new MemoryStream(envelopeCopy, writable: false) }
                },
                DelaySeconds = (int)Math.Min(Math.Ceiling(message.Early.TotalSeconds), MaxNativeDelaySeconds),
                MessageGroupId = message.Decoded.GroupKey is { } groupKey ? SqsMessageTransportNames.FifoId(groupKey) : null
            }, Encoding.UTF8.GetByteCount(message.Message.Body) + envelopeCopy.Length + SqsMessageTransportCodec.AttributeName.Length + (envelope.DataType?.Length ?? 0)));
        }

        foreach (var batch in BatchBySize(copies, c => c.Size))
        {
            var errors = await SendCopiesAsync(batch.Select(c => c.Entry).ToList()).AnyContext();
            for (int i = 0; i < batch.Count; i++)
            {
                var message = batch[i].Early;
                if (errors[i] is { } error)
                {
                    _transport.Logger.LogWarning(error, "Could not send early message {MessageId} from {Source} again with a delay; hiding it instead", message.Decoded.MessageId, Source);
                    notSent.Add(message);
                }
                else
                {
                    deletes.Add(new Deletion(message.Message, message.Decoded.MessageId, SentAgain: true));
                }
            }
        }

        return notSent;
    }

    /// <summary>Sends one copy with SendMessage, or several with SendMessageBatch, and returns each one's error, or null where it was sent.</summary>
    private async Task<Exception?[]> SendCopiesAsync(List<SendMessageBatchRequestEntry> entries)
    {
        var errors = new Exception?[entries.Count];
        try
        {
            if (entries.Count == 1)
            {
                var entry = entries[0];
                await _transport.Sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MessageBody = entry.MessageBody,
                    MessageAttributes = entry.MessageAttributes,
                    DelaySeconds = entry.DelaySeconds,
                    MessageGroupId = entry.MessageGroupId
                }).AnyContext();
                return errors;
            }

            for (int i = 0; i < entries.Count; i++)
                entries[i].Id = i.ToString(CultureInfo.InvariantCulture);

            var response = await _transport.Sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = _queueUrl, Entries = entries }).AnyContext();
            SetBatchErrors(errors, 0, response.Failed);
        }
        catch (AmazonServiceException ex)
        {
            Array.Fill(errors, ex);
        }

        return errors;
    }

    private async Task DeleteSetAsideAsync(List<Deletion> deletes)
    {
        var errors = await DeleteAsync(deletes.Select(d => d.Message.ReceiptHandle).ToList(), CancellationToken.None).AnyContext();
        for (int i = 0; i < deletes.Count; i++)
        {
            var (_, messageId, sentAgain) = deletes[i];
            if (errors[i] is { } error)
            {
                if (sentAgain)
                    _transport.Logger.LogWarning(error, "Could not delete early message {MessageId} from {Source} after sending it again with a delay; it may be delivered twice", messageId, Source);
                else
                    _transport.Logger.LogDebug(error, "Could not discard message {MessageId} from {Source}; it will be discarded when received again", messageId, Source);
            }
            else if (!sentAgain)
            {
                _transport.Logger.LogDebug("Discarded message {MessageId} from {Source}, which was sent before the source was deleted", messageId, Source);
            }
        }
    }

    /// <summary>The settlement batcher's call for completions: deletes up to 10 deliveries and returns each one's error.</summary>
    private async Task<Exception?[]> DeleteDeliveriesAsync(IReadOnlyList<SqsIncomingMessage> deliveries, CancellationToken cancellationToken)
    {
        var errors = await DeleteAsync(deliveries.Select(d => d.ReceiptHandle).ToList(), cancellationToken).AnyContext();
        for (int i = 0; i < errors.Length; i++)
        {
            if (errors[i] is AmazonServiceException ex)
                errors[i] = SqsMessageTransport.IsLeaseLost(ex) ? LeaseLost(deliveries[i], ex) : new MessageBusException($"Could not complete message {deliveries[i].MessageId} from {Source}: {ex.Message}", ex);
        }

        return errors;
    }

    /// <summary>The settlement batcher's call for renewals and abandons: changes the visibility of up to 10 deliveries and returns each one's error.</summary>
    private async Task<Exception?[]> ChangeDeliveryVisibilityAsync(IReadOnlyList<(SqsIncomingMessage Delivery, int Seconds)> changes, CancellationToken cancellationToken)
    {
        var errors = await ChangeVisibilityAsync(changes.Select(c => (c.Delivery.ReceiptHandle, c.Seconds)).ToList(), cancellationToken).AnyContext();
        for (int i = 0; i < errors.Length; i++)
        {
            if (errors[i] is not AmazonServiceException ex)
                continue;

            var delivery = changes[i].Delivery;
            if (IsOverLimit(ex))
                MarkOverLimit();

            errors[i] = SqsMessageTransport.IsLeaseLost(ex) ? LeaseLost(delivery, ex) : new MessageBusException($"Could not change the visibility of message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        return errors;
    }

    /// <summary>
    /// Deletes messages with DeleteMessage when there is one, otherwise with DeleteMessageBatch calls of up to 10, and
    /// returns each one's <see cref="AmazonServiceException"/>, or null where it was deleted.
    /// </summary>
    private async Task<Exception?[]> DeleteAsync(List<string> receiptHandles, CancellationToken cancellationToken)
    {
        var errors = new Exception?[receiptHandles.Count];
        for (int offset = 0; offset < receiptHandles.Count; offset += SqsMessageTransport.MaxBatchSize)
        {
            int count = Math.Min(SqsMessageTransport.MaxBatchSize, receiptHandles.Count - offset);
            try
            {
                if (count == 1)
                {
                    await _transport.Sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = _queueUrl, ReceiptHandle = receiptHandles[offset] }, cancellationToken).AnyContext();
                    continue;
                }

                var response = await _transport.Sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = _queueUrl,
                    Entries = receiptHandles.GetRange(offset, count).Select((receiptHandle, i) => new DeleteMessageBatchRequestEntry
                    {
                        Id = i.ToString(CultureInfo.InvariantCulture),
                        ReceiptHandle = receiptHandle
                    }).ToList()
                }, cancellationToken).AnyContext();

                SetBatchErrors(errors, offset, response.Failed);
            }
            catch (AmazonServiceException ex)
            {
                Array.Fill(errors, ex, offset, count);
            }
        }

        return errors;
    }

    /// <summary>
    /// Changes visibility timeouts with ChangeMessageVisibility when there is one, otherwise with
    /// ChangeMessageVisibilityBatch calls of up to 10, and returns each one's <see cref="AmazonServiceException"/>, or
    /// null where it was changed.
    /// </summary>
    private async Task<Exception?[]> ChangeVisibilityAsync(List<(string ReceiptHandle, int Seconds)> changes, CancellationToken cancellationToken)
    {
        var errors = new Exception?[changes.Count];
        for (int offset = 0; offset < changes.Count; offset += SqsMessageTransport.MaxBatchSize)
        {
            int count = Math.Min(SqsMessageTransport.MaxBatchSize, changes.Count - offset);
            try
            {
                if (count == 1)
                {
                    await _transport.Sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                    {
                        QueueUrl = _queueUrl,
                        ReceiptHandle = changes[offset].ReceiptHandle,
                        VisibilityTimeout = changes[offset].Seconds
                    }, cancellationToken).AnyContext();
                    continue;
                }

                var response = await _transport.Sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
                {
                    QueueUrl = _queueUrl,
                    Entries = changes.GetRange(offset, count).Select((change, i) => new ChangeMessageVisibilityBatchRequestEntry
                    {
                        Id = i.ToString(CultureInfo.InvariantCulture),
                        ReceiptHandle = change.ReceiptHandle,
                        VisibilityTimeout = change.Seconds
                    }).ToList()
                }, cancellationToken).AnyContext();

                SetBatchErrors(errors, offset, response.Failed);
            }
            catch (AmazonServiceException ex)
            {
                Array.Fill(errors, ex, offset, count);
            }
        }

        return errors;
    }

    /// <summary>Records each failed entry of a batch call, whose ids are the entries' positions from <paramref name="offset"/>, as an <see cref="AmazonSQSException"/> with its code.</summary>
    private static void SetBatchErrors(Exception?[] errors, int offset, List<BatchResultErrorEntry>? failed)
    {
        foreach (var entry in failed ?? [])
        {
            if (!Int32.TryParse(entry.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || offset + index >= errors.Length)
                continue;

            errors[offset + index] = new AmazonSQSException(entry.Message ?? entry.Code)
            {
                ErrorCode = entry.Code,
                ErrorType = entry.SenderFault == true ? ErrorType.Sender : ErrorType.Receiver
            };
        }
    }

    /// <summary>Splits items into batches SQS accepts: at most 10 entries and 256 KiB in total.</summary>
    private static IEnumerable<List<T>> BatchBySize<T>(List<T> items, Func<T, int> size)
    {
        var batch = new List<T>(Math.Min(items.Count, SqsMessageTransport.MaxBatchSize));
        int total = 0;
        foreach (var item in items)
        {
            int itemSize = size(item);
            if (batch.Count > 0 && (batch.Count == SqsMessageTransport.MaxBatchSize || total + itemSize > SqsMessageTransportCodec.MaxBrokerMessageSize))
            {
                yield return batch;
                batch = new List<T>(SqsMessageTransport.MaxBatchSize);
                total = 0;
            }

            batch.Add(item);
            total += itemSize;
        }

        if (batch.Count > 0)
            yield return batch;
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

    /// <summary>
    /// How much longer SQS lets a delivery stay invisible, measured conservatively from <paramref name="receivedAt"/>,
    /// a <see cref="Stopwatch"/> timestamp taken before the receive request was sent.
    /// </summary>
    private static int RemainingVisibilitySeconds(long receivedAt) =>
        Math.Max(0, MaxVisibilityTimeoutSeconds - (int)Math.Ceiling(Stopwatch.GetElapsedTime(receivedAt).TotalSeconds));

    private static int ToSeconds(TimeSpan value, int minimum) =>
        (int)Math.Clamp(Math.Ceiling(value.TotalSeconds), minimum, MaxVisibilityTimeoutSeconds);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1 || _transport.IsDisposed, this);

    /// <summary>A message a receive got before its delivery time, with how early it is.</summary>
    private readonly record struct EarlyMessage(SqsMessage Message, SqsDecodedMessage Decoded, TimeSpan Early);

    /// <summary>A message a receive deletes: a discarded one, or the original of an early message sent again.</summary>
    private readonly record struct Deletion(SqsMessage Message, string MessageId, bool SentAgain);

    /// <summary>What one receive got but does not deliver.</summary>
    private sealed class SetAside
    {
        public List<EarlyMessage> Early { get; } = [];
        public List<(SqsMessage Message, string MessageId)> HeldBack { get; } = [];
        public List<Deletion> Deletes { get; } = [];
    }

    private sealed class SqsIncomingMessage : IncomingMessage
    {
        private long _leaseDeadline;

        public required SqsMessageReceiver Owner { get; init; }
        public required string ReceiptHandle { get; init; }

        /// <summary>Identifies this delivery of the SQS message, so a retried dead-lettering is deduplicated on FIFO queues.</summary>
        public required string DeliveryId { get; init; }

        /// <summary>When the receive request was sent, as a <see cref="Stopwatch"/> timestamp.</summary>
        public required long ReceivedAt { get; init; }

        /// <summary>When the lease ends, as a <see cref="Stopwatch"/> timestamp.</summary>
        public required long LeaseDeadline
        {
            get => Volatile.Read(ref _leaseDeadline);
            set => Volatile.Write(ref _leaseDeadline, value);
        }
    }
}
