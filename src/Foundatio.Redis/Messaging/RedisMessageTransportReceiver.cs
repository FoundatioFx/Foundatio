using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging.Transport;

internal sealed class RedisMessageReceiver : IMessageReceiver
{
    private const int MaxPromotedPerReceive = 100;
    private const int HeartbeatsPerTimeout = 6;
    private static readonly TimeSpan IdleRegistrationRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SubscriptionRegistrationRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxHeartbeatRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WakePassOnDelay = TimeSpan.FromMilliseconds(5);

    private readonly RedisMessageTransport _transport;
    private readonly RedisSourceKeys _keys;
    private readonly ReceiverOptions _options;
    private readonly RedisInstanceSubscription? _instance;
    private readonly RedisKey? _topicSubscriptions;
    private readonly string _consumer = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _disposing = new();
    private readonly RedisChannel _wakeChannel;
    private readonly Action<RedisChannel, RedisValue> _onSignal;
    private readonly SettlementBatcher<RedisIncomingMessage> _completions;
    private TaskCompletionSource _wake = NewWakeSignal();
    private IdleRegistration? _idle;
    private TimeSpan _pollInterval;
    private bool _subscribed;
    private bool _mayBeRegistered;
    private bool _registrationCurrent;
    private long _registeredAt;
    private long _subscriptionRegisteredAt = Stopwatch.GetTimestamp();
    private int _receiving;
    private long _receiveStarts;
    private long _signals;
    private long _signalsAtLastPoll;
    private int _passingOnWake;
    private Task _heartbeat = Task.CompletedTask;
    private int _disposed;

    public RedisMessageReceiver(RedisMessageTransport transport, MessageSource source, RedisSourceKeys keys, ReceiverOptions options, RedisInstanceSubscription? instance)
    {
        _transport = transport;
        Source = source;
        _keys = keys;
        _options = options;
        _instance = instance;
        if (source.Kind == MessageSourceKind.Subscription)
            _topicSubscriptions = new RedisTopicKeys(transport.Options.Prefix, source.Topic!).Subscriptions;
        _completions = new SettlementBatcher<RedisIncomingMessage>(RedisMessageTransport.ScriptBatchSize, CompleteBatchAsync);
        _wakeChannel = transport.WakeChannel(keys.WakeChannelPrefix + _consumer);
        _onSignal = (_, _) =>
        {
            // A wake-up removed this receiver from the idle set.
            Volatile.Write(ref _registrationCurrent, false);
            Interlocked.Increment(ref _signals);
            Wake();
            if (Volatile.Read(ref _receiving) == 0)
                _ = PassOnWakeAsync();
        };
        _pollInterval = transport.Options.MinPollInterval;
    }

    public MessageSource Source { get; }

    /// <summary>
    /// Subscribes to this receiver's wake-up channel before the first receive, which is the first that can register it
    /// as idle, so no message made available after that is missed.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await _transport.Subscriber.SubscribeAsync(_wakeChannel, _onSignal).AnyContext();
            _subscribed = true;
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not subscribe to the wake-up channel of a receiver of {Source}; it waits up to {MaxPollInterval} for new messages instead",
                Source, _transport.Options.MaxPollInterval);
        }

        if (_instance is not null)
            _heartbeat = Task.Run(HeartbeatLoopAsync, CancellationToken.None);
    }

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        Interlocked.Increment(ref _receiveStarts);
        Interlocked.Increment(ref _receiving);
        try
        {
            return await ReceiveUntilAnyAsync(maxMessages, maxWait, cancellationToken).AnyContext();
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw new MessageBusException($"Could not receive from {Source}: {ex.Message}", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _receiving);
            // A wake-up that came after the last poll was for messages this receive did not take.
            if (Volatile.Read(ref _signals) != Volatile.Read(ref _signalsAtLastPoll))
                _ = PassOnWakeAsync();
        }
    }

    private async Task<IReadOnlyList<IncomingMessage>> ReceiveUntilAnyAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        int count = Math.Min(maxMessages, _transport.Capabilities.MaxReceiveBatchSize);
        long started = Stopwatch.GetTimestamp();
        bool lastPoll = false;
        // The receive before found the source drained and left this receiver registered as idle. Until it is woken, or a
        // delayed or leased message falls due, a poll would find nothing, so it starts by waiting.
        var idle = TakeIdleRegistration();

        while (true)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            Task wake;
            TimeSpan? nextChange;
            IdleRegistration? registration = null;
            bool polled = idle is null;
            if (idle is not null)
            {
                wake = idle.Wake;
                nextChange = idle.TimeUntilDue();
                idle = null;
            }
            else
            {
                Volatile.Write(ref _signalsAtLastPoll, Volatile.Read(ref _signals));
                wake = Volatile.Read(ref _wake).Task;
                (var messages, nextChange, registration) = await ReceiveOnceAsync(count, wake).AnyContext();
                if (messages.Count > 0)
                {
                    _pollInterval = _transport.Options.MinPollInterval;
                    Volatile.Write(ref _idle, registration);
                    return messages;
                }
            }

            var remaining = maxWait - Stopwatch.GetElapsedTime(started);
            if (lastPoll || remaining <= TimeSpan.Zero)
            {
                // Every receive polls at least once.
                if (!polled)
                    continue;

                Volatile.Write(ref _idle, registration);
                return [];
            }

            var poll = _pollInterval;
            var wait = poll < remaining ? poll : remaining;
            if (nextChange is { } change && change < wait)
                wait = change < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : change;

            // Polling is the fallback: a message made available while this receiver is registered as idle wakes it.
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var woken = await Task.WhenAny(Task.Delay(wait, delay.Token), wake).AnyContext() == wake;
            await delay.CancelAsync().AnyContext();
            if (woken)
                continue;

            // A timer can fire a little early, which must not cost more than one last poll.
            if (wait == remaining)
                lastPoll = true;
            else if (wait == poll)
                _pollInterval = TimeSpan.FromTicks(Math.Min(poll.Ticks * 2, _transport.Options.MaxPollInterval.Ticks));
        }
    }

    /// <summary>
    /// Completes the message. Completes called while another one runs are sent together, as one script call, once it
    /// ends (see <see cref="SettlementBatcher{TEntry}"/>), so concurrent completes take fewer round trips. Each message's
    /// lease is checked on its own; a call that fails completes none of its messages, so they are delivered again.
    /// </summary>
    public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        => _completions.SendAsync(GetDelivery(message), cancellationToken);

    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        long milliseconds = delay > TimeSpan.Zero ? (long)Math.Ceiling(delay.TotalMilliseconds) : 0;
        await SettleAsync(message, "abandon", milliseconds).AnyContext();

        // A retry is not available yet, so it wakes no idle receiver. This one is woken to learn when it is due, as is
        // one that cannot be woken over its channel.
        if (milliseconds > 0 || !_subscribed)
            Wake();
    }

    public Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        if (_instance is not null)
            throw new InvalidOperationException("Instance subscriptions do not have a dead-letter queue.");

        return SettleAsync(message, "deadletter", reason ?? String.Empty);
    }

    public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default) =>
        SettleAsync(message, "renew", (long)Math.Ceiling(_options.LeaseDuration.TotalMilliseconds));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _transport.Untrack(this);
        await _disposing.CancelAsync().AnyContext();
        await _heartbeat.AnyContext();
        _disposing.Dispose();

        if (_subscribed)
        {
            try
            {
                await _transport.Subscriber.UnsubscribeAsync(_wakeChannel, _onSignal).AnyContext();
            }
            catch (Exception ex)
            {
                _transport.Logger.LogDebug(ex, "Could not unsubscribe receiver {Consumer} of {Source} from its wake-up channel", _consumer, Source);
            }
        }

        try
        {
            if (_instance is not null)
            {
                await _transport.RemoveInstanceAsync(_instance.Topic, _instance.Name, _keys).AnyContext();
            }
            else
            {
                await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.ReleaseScript, [_keys.Stream, _keys.Leases, _keys.Idle],
                    [RedisMessageTransport.GroupName, _consumer, RedisMessageTransport.ReleasedConsumer, _keys.WakeChannelPrefix, _transport.PublishCommand]).AnyContext();
            }
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not release receiver {Consumer} of {Source}; its messages will be delivered again when their leases expire", _consumer, Source);
        }
    }

    /// <summary>
    /// Polls the source once. When the poll drains it, with room for more messages, the script registers this receiver
    /// as idle, so the next message made available wakes it; the registration returned records that, with
    /// <paramref name="wake"/>, which completes when it is woken.
    /// </summary>
    private async Task<(IReadOnlyList<IncomingMessage> Messages, TimeSpan? NextChange, IdleRegistration? Idle)> ReceiveOnceAsync(int count, Task wake)
    {
        bool deadLetters = Source.Kind == MessageSourceKind.DeadLetter;
        bool register = _subscribed;
        // A receiver still registered need not register again when it takes nothing, but it does now and then, in case
        // it was woken by a wake-up that was lost.
        long now = Stopwatch.GetTimestamp();
        bool current = Volatile.Read(ref _registrationCurrent) && Stopwatch.GetElapsedTime(_registeredAt, now) < IdleRegistrationRefreshInterval;
        var result = (RedisResult[])(await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.ReceiveScript,
            [_keys.Stream, _keys.Leases, _keys.Delayed, _keys.DelayedData, _keys.Groups, _keys.Held, _keys.Retries, _keys.Idle],
            [RedisMessageTransport.GroupName, _consumer, count, (long)Math.Ceiling(_options.LeaseDuration.TotalMilliseconds), MaxPromotedPerReceive,
                RedisMessageTransport.ReceiveBatchBytes, _transport.Options.MaxMessageSize,
                deadLetters ? _transport.DeadLetterMaxLength : 0, deadLetters ? _transport.DeadLetterRetentionMilliseconds : 0,
                !register ? 0 : current ? 2 : 1, _mayBeRegistered ? 1 : 0]).AnyContext())!;

        var entries = (RedisResult[])result[0]!;
        long wait = (long)result[1];
        _transport.LogReleasedGroups(_keys, (long)result[2]);
        bool recreated = (long)result[4] == 1;
        if (recreated && _topicSubscriptions is null)
            _transport.Logger.Log(deadLetters ? LogLevel.Debug : LogLevel.Warning, "{Source} was missing in Redis and is created again: it was deleted, or Redis lost its keys " +
                "(a restart without persistence, a failover before replication, or a flush), and with them the messages it held", Source);
        if (_topicSubscriptions is { } subscriptions && (recreated || Stopwatch.GetElapsedTime(_subscriptionRegisteredAt, now) >= SubscriptionRegistrationRefreshInterval))
            await RegisterSubscriptionAsync(subscriptions, recreated, now).AnyContext();
        bool registered = register && (long)result[3] == 1;
        if (registered)
        {
            if (!current || entries.Length > 0)
                _registeredAt = now;

            _mayBeRegistered = true;
            // A wake-up that came before the reply already removed it again.
            Volatile.Write(ref _registrationCurrent, !wake.IsCompleted);
        }
        else
        {
            _mayBeRegistered = false;
            Volatile.Write(ref _registrationCurrent, false);
        }

        TimeSpan? nextChange = wait >= 0 ? TimeSpan.FromMilliseconds(wait) : null;
        var idle = registered ? new IdleRegistration(wake, nextChange is { } due ? now + (long)(due.TotalSeconds * Stopwatch.Frequency) : null) : null;
        if (entries.Length == 0)
            return ([], nextChange, idle);

        var messages = new List<IncomingMessage>(entries.Length);
        foreach (var entry in entries)
        {
            var parts = (RedisResult[])entry!;
            messages.Add(ToMessage((string)parts[0]!, (RedisValue[])parts[1]!, (long)parts[2]));
        }

        return (messages, nextChange, idle);
    }

    /// <summary>
    /// Adds this durable subscription to its topic's subscriptions again, which publishes read to know where to copy a
    /// message. Done when the receive found the subscription's keys missing, and every few seconds, since the topic's
    /// set lives on another cluster slot and can be lost while the subscription's keys survive.
    /// </summary>
    private async Task RegisterSubscriptionAsync(RedisKey subscriptions, bool recreated, long now)
    {
        try
        {
            bool added = await _transport.Database.SetAddAsync(subscriptions, Source.Name).AnyContext();
            _subscriptionRegisteredAt = now;
            if (recreated)
                _transport.Logger.LogWarning("{Source} was missing in Redis and is created and registered with topic {Topic} again: it was deleted, or Redis lost its keys " +
                    "(a restart without persistence, a failover before replication, or a flush). The messages it held were lost, and so were those published to the topic meanwhile", Source, Source.Topic);
            else if (added)
                _transport.Logger.LogWarning("{Source} was missing from the subscriptions of topic {Topic} in Redis and is registered again: it was deleted, or Redis lost the topic's keys " +
                    "(a restart without persistence, a failover before replication, or a flush). Messages published to the topic meanwhile were not delivered to it", Source, Source.Topic);
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            // Retried on the next receive.
            _subscriptionRegisteredAt = 0;
            _transport.Logger.LogDebug(ex, "Could not register {Source} with topic {Topic}", Source, Source.Topic);
        }
    }

    private IdleRegistration? TakeIdleRegistration()
    {
        var idle = Interlocked.Exchange(ref _idle, null);
        return idle is not null && idle.IsCurrent() ? idle : null;
    }

    private RedisIncomingMessage ToMessage(string entryId, RedisValue[] fields, long deliveries)
    {
        string messageId = String.Empty;
        ReadOnlyMemory<byte> body = ReadOnlyMemory<byte>.Empty;
        string? groupKey = null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i + 1 < fields.Length; i += 2)
        {
            string name = fields[i]!;
            var value = fields[i + 1];
            switch (name)
            {
                case RedisMessageTransport.IdField:
                    messageId = value!;
                    break;
                case RedisMessageTransport.BodyField:
                    body = (byte[]?)value ?? [];
                    break;
                case RedisMessageTransport.GroupKeyField:
                    groupKey = value;
                    break;
                default:
                    if (name.StartsWith(RedisMessageTransport.HeaderFieldPrefix, StringComparison.Ordinal))
                        headers[name[RedisMessageTransport.HeaderFieldPrefix.Length..]] = value!;
                    break;
            }
        }

        return new RedisIncomingMessage
        {
            Owner = this,
            EntryId = entryId,
            Deliveries = deliveries,
            MessageId = messageId,
            Body = body,
            Headers = headers,
            GroupKey = groupKey,
            EnqueuedAt = RedisMessageTransport.EntryTime(entryId),
            DeliveryCount = (int)Math.Min(deliveries, Int32.MaxValue)
        };
    }

    // The script wakes an idle receiver for a message it makes available: one abandoned without a delay, the next
    // message of a group, or a dead letter.
    private async Task SettleAsync(IncomingMessage message, string operation, RedisValue value)
    {
        var delivery = GetDelivery(message);
        bool ordered = _keys.OrderedGroups && delivery.GroupKey is not null;
        long result;
        try
        {
            result = (long)await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.SettleScript,
                [_keys.Stream, _keys.Leases, _keys.DeadLetterStream, _keys.Groups, _keys.Held, _keys.Delayed, _keys.DelayedData, _keys.Retries, _keys.Idle, _keys.DeadLetterIdle],
                [operation, RedisMessageTransport.GroupName, _consumer, delivery.EntryId, delivery.Deliveries, value, RedisMessageTransport.ReleasedConsumer,
                    RedisMessageTransport.DeadLetterReasonField, ordered ? 1 : 0, ordered ? delivery.GroupKey : String.Empty,
                    _transport.DeadLetterMaxLength, _transport.DeadLetterRetentionMilliseconds,
                    _keys.WakeChannelPrefix, _keys.DeadLetterWakeChannelPrefix, _transport.PublishCommand]).AnyContext();
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw new MessageBusException($"Could not {(operation == "deadletter" ? "dead-letter" : operation)} message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        if (result == 0)
            throw new MessageLeaseLostException($"The lease for message {message.MessageId} was lost.");

        if (result == 2 && !_subscribed)
            Wake();
    }

    private RedisIncomingMessage GetDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not RedisIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        return delivery;
    }

    /// <summary>The settlement batcher's call: completes up to a script batch of deliveries and returns the lease-lost ones' errors.</summary>
    private async Task<IReadOnlyList<Exception?>?> CompleteBatchAsync(IReadOnlyList<RedisIncomingMessage> batch, CancellationToken cancellationToken)
    {
        var values = new List<RedisValue>(4 + batch.Count * 4) { RedisMessageTransport.GroupName, _consumer, _keys.WakeChannelPrefix, _transport.PublishCommand };
        foreach (var delivery in batch)
        {
            bool ordered = _keys.OrderedGroups && delivery.GroupKey is not null;
            values.Add(delivery.EntryId);
            values.Add(delivery.Deliveries);
            values.Add(ordered ? 1 : 0);
            values.Add(ordered ? delivery.GroupKey : String.Empty);
        }

        long[] results;
        try
        {
            results = (long[])(await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.CompleteScript,
                [_keys.Stream, _keys.Leases, _keys.Groups, _keys.Held, _keys.Delayed, _keys.DelayedData, _keys.Idle], values.ToArray()).AnyContext())!;
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw new MessageBusException($"Could not complete {batch.Count} message(s) from {Source}: {ex.Message}", ex);
        }

        Exception?[]? errors = null;
        bool released = false;
        for (int i = 0; i < batch.Count; i++)
        {
            if (results[i] == 0)
                (errors ??= new Exception?[batch.Count])[i] = new MessageLeaseLostException($"The lease for message {batch[i].MessageId} was lost.");
            else
                released |= results[i] == 2;
        }

        if (released && !_subscribed)
            Wake();

        return errors;
    }

    private void Wake() => Interlocked.Exchange(ref _wake, NewWakeSignal()).TrySetResult();

    /// <summary>
    /// Passes on a wake-up that reached this receiver while no receive was running, or after the last poll of the one
    /// that was, to the idle receiver busy most recently, unless a receive starts within <see cref="WakePassOnDelay"/>.
    /// A processor that reads ahead may not receive again until its handlers free room, and the message would wait for it.
    /// </summary>
    private async Task PassOnWakeAsync()
    {
        if (Interlocked.Exchange(ref _passingOnWake, 1) == 1)
            return;

        try
        {
            long starts = Volatile.Read(ref _receiveStarts);
            await Task.Delay(WakePassOnDelay, _disposing.Token).AnyContext();
            if (Volatile.Read(ref _receiving) > 0 || Volatile.Read(ref _receiveStarts) != starts)
                return;

            await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.WakeScript, [_keys.Idle], [_keys.WakeChannelPrefix, _transport.PublishCommand]).AnyContext();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && Volatile.Read(ref _disposed) == 1)
        {
        }
        catch (Exception ex)
        {
            _transport.Logger.LogDebug(ex, "Could not pass on a wake-up of receiver {Consumer} of {Source}; the message waits for the next receive or poll", _consumer, Source);
        }
        finally
        {
            Volatile.Write(ref _passingOnWake, 0);
        }
    }

    // Renews often enough to survive several missed or failed heartbeats, and retries a failed one sooner.
    private async Task HeartbeatLoopAsync()
    {
        var instance = _instance!;
        var timeout = _transport.Options.InstanceSubscriptionTimeout;
        var interval = timeout / HeartbeatsPerTimeout;
        var retry = interval < MaxHeartbeatRetryDelay ? interval : MaxHeartbeatRetryDelay;
        var token = _disposing.Token;
        bool failed = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(failed ? retry : interval, token).AnyContext();
                if (await _transport.HeartbeatAsync(instance.Topic, instance.Name, _keys).AnyContext())
                    _transport.Logger.LogWarning("Instance subscription {Instance} of topic {Topic} was removed because it missed its heartbeats for {Timeout}, and is now restored. " +
                        "Messages published to the topic meanwhile were not delivered to it, and messages it had received could not be settled", instance.Name, instance.Topic.Topic, timeout);

                failed = false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _transport.Logger.Log(failed ? LogLevel.Debug : LogLevel.Warning, ex, "Could not renew instance subscription {Instance} of topic {Topic}", instance.Name, instance.Topic.Topic);
                failed = true;
            }
        }
    }

    private static TaskCompletionSource NewWakeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1 || _transport.IsDisposed, this);

    /// <summary>
    /// This receiver is registered as idle since a poll that drained the source. <paramref name="Wake"/> completes when
    /// it is woken; <paramref name="DueAt"/> is the <see cref="Stopwatch"/> timestamp when the next delayed or leased
    /// message the poll saw falls due.
    /// </summary>
    private sealed record IdleRegistration(Task Wake, long? DueAt)
    {
        public bool IsCurrent() => !Wake.IsCompleted && (DueAt is not { } due || Stopwatch.GetTimestamp() < due);

        public TimeSpan? TimeUntilDue() => DueAt is { } due ? Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due) : null;
    }

    private sealed class RedisIncomingMessage : IncomingMessage
    {
        public required RedisMessageReceiver Owner { get; init; }
        public required string EntryId { get; init; }
        public required long Deliveries { get; init; }
    }
}
