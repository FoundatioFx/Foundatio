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

    private readonly RedisMessageTransport _transport;
    private readonly RedisSourceKeys _keys;
    private readonly ReceiverOptions _options;
    private readonly RedisInstanceSubscription? _instance;
    private readonly string _consumer = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _disposing = new();
    private TaskCompletionSource _released = NewReleasedSignal();
    private Task _heartbeat = Task.CompletedTask;
    private int _disposed;

    public RedisMessageReceiver(RedisMessageTransport transport, MessageSource source, RedisSourceKeys keys, ReceiverOptions options, RedisInstanceSubscription? instance)
    {
        _transport = transport;
        Source = source;
        _keys = keys;
        _options = options;
        _instance = instance;
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
        int count = Math.Min(maxMessages, _transport.Capabilities.MaxReceiveBatchSize);
        long started = Stopwatch.GetTimestamp();
        var poll = _transport.Options.MinPollInterval;

        while (true)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var released = Volatile.Read(ref _released).Task;
            var (messages, nextChange) = await ReceiveOnceAsync(count).AnyContext();
            if (messages.Count > 0)
                return messages;

            var remaining = maxWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return [];

            var wait = poll < remaining ? poll : remaining;
            if (nextChange is { } change && change < wait)
                wait = change < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : change;

            // Settling a group's message here releases its next one, which is worth polling for at once.
            if (await Task.WhenAny(Task.Delay(wait, cancellationToken), released).AnyContext() == released)
                poll = _transport.Options.MinPollInterval;
            else
                poll = TimeSpan.FromTicks(Math.Min(poll.Ticks * 2, _transport.Options.MaxPollInterval.Ticks));
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

        try
        {
            if (_instance is not null)
                await _transport.RemoveInstanceAsync(_instance.Topic, _instance.Name, _keys).AnyContext();
            else
                await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.ReleaseScript, [_keys.Stream, _keys.Leases],
                    [RedisMessageTransport.GroupName, _consumer, RedisMessageTransport.ReleasedConsumer]).AnyContext();
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not release receiver {Consumer} of {Source}; its messages will be delivered again when their leases expire", _consumer, Source);
        }
    }

    private async Task<(IReadOnlyList<IncomingMessage> Messages, TimeSpan? NextChange)> ReceiveOnceAsync(int count)
    {
        var result = (RedisResult[])(await _transport.Database.ScriptEvaluateAsync(RedisMessageTransport.ReceiveScript,
            [_keys.Stream, _keys.Leases, _keys.Delayed, _keys.DelayedData, _keys.Groups],
            [RedisMessageTransport.GroupName, _consumer, count, (long)Math.Ceiling(_options.LeaseDuration.TotalMilliseconds), MaxPromotedPerReceive]).AnyContext())!;

        var entries = (RedisResult[])result[0]!;
        long wait = (long)result[1];
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

        int separator = entryId.IndexOf('-');
        DateTimeOffset? enqueuedAt = Int64.TryParse(separator > 0 ? entryId.AsSpan(0, separator) : entryId, out long milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

        return new RedisIncomingMessage
        {
            Owner = this,
            EntryId = entryId,
            Deliveries = deliveries,
            MessageId = messageId,
            Body = body,
            Headers = headers,
            GroupKey = groupKey,
            EnqueuedAt = enqueuedAt,
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
                RedisMessageTransport.DeadLetterReasonField, ordered ? 1 : 0, ordered ? delivery.GroupKey : String.Empty]).AnyContext();

        if (result == 0)
            throw new MessageLeaseLostException($"The lease for message {message.MessageId} was lost.");

        if (result == 2)
            Interlocked.Exchange(ref _released, NewReleasedSignal()).TrySetResult();
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
                await Task.Delay(interval, token).AnyContext();
                await _transport.HeartbeatAsync(instance.Topic, instance.Name, _keys).AnyContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _transport.Logger.LogWarning(ex, "Could not renew instance subscription {Instance} of topic {Topic}", instance.Name, instance.Topic.Topic);
            }
        }
    }

    private static TaskCompletionSource NewReleasedSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1 || _transport.IsDisposed, this);

    private sealed class RedisIncomingMessage : IncomingMessage
    {
        public required RedisMessageReceiver Owner { get; init; }
        public required string EntryId { get; init; }
        public required long Deliveries { get; init; }
    }
}
