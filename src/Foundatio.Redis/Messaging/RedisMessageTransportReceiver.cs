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
    private static readonly TimeSpan MaxHeartbeatRetryDelay = TimeSpan.FromSeconds(1);

    private readonly RedisMessageTransport _transport;
    private readonly RedisSourceKeys _keys;
    private readonly ReceiverOptions _options;
    private readonly RedisInstanceSubscription? _instance;
    private readonly string _consumer = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _disposing = new();
    private readonly Action<RedisChannel, RedisValue> _onSignal;
    private TaskCompletionSource _wake = NewWakeSignal();
    private TimeSpan _pollInterval;
    private bool _subscribed;
    private Task _heartbeat = Task.CompletedTask;
    private int _disposed;

    public RedisMessageReceiver(RedisMessageTransport transport, MessageSource source, RedisSourceKeys keys, ReceiverOptions options, RedisInstanceSubscription? instance)
    {
        _transport = transport;
        Source = source;
        _keys = keys;
        _options = options;
        _instance = instance;
        _onSignal = (_, _) => Wake();
        _pollInterval = transport.Options.MinPollInterval;
    }

    public MessageSource Source { get; }

    /// <summary>Subscribes to the source's signal before the first receive, so no message sent after it is missed.</summary>
    public async Task StartAsync()
    {
        try
        {
            await _transport.Subscriber.SubscribeAsync(_keys.Signal, _onSignal).AnyContext();
            _subscribed = true;
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not subscribe to the new-message signal of {Source}; it waits up to {MaxPollInterval} for new messages instead",
                Source, _transport.Options.MaxPollInterval);
        }

        if (_instance is not null)
            _heartbeat = Task.Run(HeartbeatLoopAsync, CancellationToken.None);
    }

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        int count = Math.Min(maxMessages, _transport.Capabilities.MaxReceiveBatchSize);
        long started = Stopwatch.GetTimestamp();
        bool lastPoll = false;

        while (true)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var wake = Volatile.Read(ref _wake).Task;
            var (messages, nextChange) = await ReceiveOnceAsync(count).AnyContext();
            if (messages.Count > 0)
            {
                _pollInterval = _transport.Options.MinPollInterval;
                return messages;
            }

            var remaining = maxWait - Stopwatch.GetElapsedTime(started);
            if (lastPoll || remaining <= TimeSpan.Zero)
                return [];

            var poll = _pollInterval;
            var wait = poll < remaining ? poll : remaining;
            if (nextChange is { } change && change < wait)
                wait = change < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : change;

            // Polling is the fallback: a message sent to the source, or released by a settle, rings its signal.
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

    public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default) =>
        SettleAsync(message, "complete", RedisValue.EmptyString);

    public Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default) =>
        SettleAsync(message, "abandon", delay > TimeSpan.Zero ? (long)Math.Ceiling(delay.TotalMilliseconds) : 0);

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
                await _transport.Subscriber.UnsubscribeAsync(_keys.Signal, _onSignal).AnyContext();
            }
            catch (Exception ex)
            {
                _transport.Logger.LogDebug(ex, "Could not unsubscribe receiver {Consumer} of {Source} from its new-message signal", _consumer, Source);
            }
        }

        try
        {
            if (_instance is not null)
            {
                await _transport.RemoveInstanceAsync(_instance.Topic, _instance.Name, _keys).AnyContext();
            }
            else if ((long)await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.ReleaseScript, [_keys.Stream, _keys.Leases],
                [RedisMessageTransport.GroupName, _consumer, RedisMessageTransport.ReleasedConsumer]).AnyContext() > 0)
            {
                _transport.Signal(_keys.Signal);
            }
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not release receiver {Consumer} of {Source}; its messages will be delivered again when their leases expire", _consumer, Source);
        }
    }

    private async Task<(IReadOnlyList<IncomingMessage> Messages, TimeSpan? NextChange)> ReceiveOnceAsync(int count)
    {
        bool deadLetters = Source.Kind == MessageSourceKind.DeadLetter;
        var result = (RedisResult[])(await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.ReceiveScript,
            [_keys.Stream, _keys.Leases, _keys.Delayed, _keys.DelayedData, _keys.Groups, _keys.Held],
            [RedisMessageTransport.GroupName, _consumer, count, (long)Math.Ceiling(_options.LeaseDuration.TotalMilliseconds), MaxPromotedPerReceive,
                RedisMessageTransport.ReceiveBatchBytes, _transport.Options.MaxMessageSize,
                deadLetters ? _transport.DeadLetterMaxLength : 0, deadLetters ? _transport.DeadLetterRetentionMilliseconds : 0]).AnyContext())!;

        var entries = (RedisResult[])result[0]!;
        long wait = (long)result[1];
        _transport.LogReleasedGroups(_keys, (long)result[2]);
        if (entries.Length == 0)
            return ([], wait >= 0 ? TimeSpan.FromMilliseconds(wait) : null);

        var messages = new List<IncomingMessage>(entries.Length);
        foreach (var entry in entries)
        {
            var parts = (RedisResult[])entry!;
            messages.Add(ToMessage((string)parts[0]!, (RedisValue[])parts[1]!, (long)parts[2]));
        }

        return (messages, null);
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

    private async Task SettleAsync(IncomingMessage message, string operation, RedisValue value)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not RedisIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        bool ordered = _keys.OrderedGroups && delivery.GroupKey is not null;
        long result = (long)await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.SettleScript,
            [_keys.Stream, _keys.Leases, _keys.DeadLetterStream, _keys.Groups, _keys.Held, _keys.Delayed, _keys.DelayedData],
            [operation, RedisMessageTransport.GroupName, _consumer, delivery.EntryId, delivery.Deliveries, value, RedisMessageTransport.ReleasedConsumer,
                RedisMessageTransport.DeadLetterReasonField, ordered ? 1 : 0, ordered ? delivery.GroupKey : String.Empty,
                _transport.DeadLetterMaxLength, _transport.DeadLetterRetentionMilliseconds]).AnyContext();

        if (result == 0)
            throw new MessageLeaseLostException($"The lease for message {message.MessageId} was lost.");

        // An abandoned message, and the next message of a group released by this settle, can be received at once.
        if (result == 2 || operation == "abandon")
        {
            _transport.Signal(_keys.Signal);
            if (!_subscribed)
                Wake();
        }
        else if (operation == "deadletter")
        {
            _transport.Signal(_keys.DeadLetterSignal);
        }
    }

    private void Wake() => Interlocked.Exchange(ref _wake, NewWakeSignal()).TrySetResult();

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

    private sealed class RedisIncomingMessage : IncomingMessage
    {
        public required RedisMessageReceiver Owner { get; init; }
        public required string EntryId { get; init; }
        public required long Deliveries { get; init; }
    }
}
