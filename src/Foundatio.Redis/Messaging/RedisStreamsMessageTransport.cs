using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Messaging;

/// <summary>
/// An <see cref="IMessageTransport"/> over Redis Streams + consumer groups (at-least-once). Temporary in-repo provider
/// used to validate the redesigned transport contract — and the core's retry/dead-letter machinery — against a real
/// broker. A stream is a queue/topic; a consumer group is a subscription (the default group for a plain queue gives
/// competing consumers; one group per named subscription gives topic fan-out).
/// </summary>
/// <remarks>
/// Streams has no per-message visible-until or per-message delay, so this transport keeps the lease explicitly: a
/// per-group sorted set (<c>member = stream entry id</c>, <c>score = visible-until unix-ms</c>) is the authoritative
/// in-flight lease and a per-group hash holds <c>token|delivery-count</c> per entry. Reclaim (abandon, redelivery
/// delay, lock expiry, crashed consumer) is driven by that sorted set — entries whose lease has lapsed are
/// <c>XCLAIM</c>ed and redelivered (same stream id, delivery count incremented). Because the lease lives in Redis, a
/// message held by a crashed instance is recovered by any other instance. A stale receipt (already settled, or the
/// entry was redelivered to someone else) is detected by an owner token and surfaced as <see cref="ReceiptExpiredException"/>.
/// </remarks>
public sealed partial class RedisStreamsMessageTransport : IMessageTransport, ISupportsPull, ISupportsVisibilityTimeout,
    ISupportsLockRenewal, ISupportsRedeliveryDelay, ISupportsDeadLetter, ISupportsEphemeralSubscriptions, ISupportsStats, ITransportInfo
{
    private readonly ConcurrentDictionary<string, int> _idlePolls = new(StringComparer.Ordinal);

    private static readonly IReadOnlySet<DestinationRole> _supportedRoles =
        new HashSet<DestinationRole> { DestinationRole.Queue, DestinationRole.Topic, DestinationRole.Subscription, DestinationRole.Binding };

    private readonly RedisStreamsMessageTransportOptions _options;
    private readonly IDatabase _db;
    private readonly TimeProvider _timeProvider;
    private readonly string _prefix;
    private readonly string _consumer;
    private readonly ConcurrentDictionary<string, byte> _ensuredGroups = new(StringComparer.Ordinal);
    private int _isDisposed;

    public RedisStreamsMessageTransport(RedisStreamsMessageTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPendingMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxBatchSize, 256);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxIdlePollInterval, options.PollInterval);
        ArgumentNullException.ThrowIfNull(options.ConnectionMultiplexer);
        _db = options.ConnectionMultiplexer.GetDatabase();
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _prefix = options.KeyPrefix ?? "";
        _consumer = !String.IsNullOrEmpty(options.ConsumerName) ? options.ConsumerName : $"c-{Guid.NewGuid():N}"[..16];
    }

    // Streams append FIFO; there is no native priority, per-message expiration, or delayed delivery (delays route
    // through the runtime-store fallback), and no broker-imposed size or batch limits.
    private static readonly TransportCapabilities _capabilities = new() { Ordering = OrderingGuarantee.Fifo };

    public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
    public IReadOnlySet<DestinationRole> SupportedRoles => _supportedRoles;
    public TransportCapabilities GetCapabilities(DestinationAddress destination) => _capabilities with { MaxBatchSize = _options.MaxBatchSize };
    public TimeSpan? MaxRedeliveryDelay => null; // lease is tracked in Redis, so any delay is honored
    public TimeSpan? MaxVisibilityTimeout => null;

    public async Task<SendResult> SendAsync(DestinationAddress destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);

        // Streams have no native delayed delivery; the core routes delayed sends through the runtime-store fallback
        // (no DelayedDelivery capability is advertised), so a future DeliverAt reaching here is a contract violation —
        // refuse loudly rather than deliver immediately and silently drop the delay.
        if (options.DeliverAt is { } deliverAt && deliverAt > _timeProvider.GetUtcNow())
            throw new NotSupportedException($"Transport \"{nameof(RedisStreamsMessageTransport)}\" does not support native delayed delivery. Register a job runtime store so delayed sends use the scheduled-dispatch fallback.");

        // The stream IS the queue/topic; subscriptions read it through their own group. The address role picks the
        // stream namespace so a queue and a topic sharing a route name never cross-deliver.
        RedisKey streamKey = destination.Role == DestinationRole.Topic ? TopicStreamKey(destination.Name) : QueueStreamKey(destination.Name);
        var items = new SendItemResult[messages.Count];
        for (int index = 0; index < items.Length; index++)
            items[index] = new SendItemResult { Index = index, Status = MessageSendStatus.NotAttempted };
        for (int offset = 0; offset < messages.Count; offset += _options.MaxBatchSize)
        {
            if (ct.IsCancellationRequested) break;
            int count = Math.Min(_options.MaxBatchSize, messages.Count - offset);
            var pending = new Task[count];
            for (int index = 0; index < count; index++)
                pending[index] = SendOneAsync(offset + index);
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        return new SendResult { Items = items };

        async Task SendOneAsync(int index)
        {
            items[index] = items[index] with { Status = MessageSendStatus.Unknown };
            try
            {
                var arguments = new List<RedisValue> { destination.Role == DestinationRole.Topic ? "1" : "0", _options.MaxPendingMessages, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() };
                foreach (var field in BuildFields(messages[index])) { arguments.Add(field.Name); arguments.Add(field.Value); }
                var id = await _db.ScriptEvaluateAsync(SendScript, [streamKey], arguments.ToArray()).WaitAsync(ct).ConfigureAwait(false);
                items[index] = new SendItemResult { Index = index, Status = MessageSendStatus.Accepted, MessageId = (string)id! };
            }
            catch (Exception ex)
            {
                bool rejected = ex is RedisServerException && ex.Message.Contains("The destination has reached its pending-message capacity.", StringComparison.Ordinal);
                items[index] = new SendItemResult
                {
                    Index = index,
                    Status = rejected ? MessageSendStatus.Rejected : MessageSendStatus.Unknown,
                    ErrorCode = rejected ? "CapacityExceeded" : ex.GetType().Name,
                    ErrorMessage = ex.Message,
                    Retryable = ex is not OperationCanceledException
                };
            }
        }
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, CancellationToken ct)
        => ReceiveAsync(source, request, _options.DefaultVisibilityTimeout, ct);

    public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        var resolved = Resolve(source);
        int max = Math.Max(1, request.MaxMessages);
        long visibilityMs = (long)Math.Max(0, visibility.TotalMilliseconds);
        var deadline = _timeProvider.GetUtcNow() + (request.MaxWaitTime ?? TimeSpan.Zero);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<TransportEntry> entries;
            try { entries = await PollOnceAsync(source, resolved, max, visibilityMs, ct).ConfigureAwait(false); }
            catch (RedisServerException ex) when (ex.Message.Contains("NOGROUP", StringComparison.Ordinal))
            {
                _ensuredGroups.TryRemove(GroupKey(resolved), out _);
                throw new MessageDestinationNotFoundException(source, ex);
            }
            if (entries.Count > 0)
            {
                _idlePolls.TryRemove(GroupKey(resolved), out _);
                return entries;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return [];

            int idle = _idlePolls.AddOrUpdate(GroupKey(resolved), 0, (_, current) => Math.Min(10, current + 1));
            var delay = TimeSpan.FromMilliseconds(Math.Min(_options.MaxIdlePollInterval.TotalMilliseconds, _options.PollInterval.TotalMilliseconds * (1 << idle)));
            await Task.Delay(remaining < delay ? remaining : delay, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private async Task<List<TransportEntry>> PollOnceAsync(DestinationAddress source, ResolvedSource resolved, int max, long visibilityMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var snapshot = await _db.ScriptEvaluateAsync(ReceiveScript,
            new RedisKey[] { resolved.StreamKey, LockKey(resolved), MetaKey(resolved) },
            new RedisValue[] { resolved.Group, _consumer, nowMs, visibilityMs, max, Guid.NewGuid().ToString("N"), (long)_options.DefaultVisibilityTimeout.TotalMilliseconds }).ConfigureAwait(false);
        var rows = (RedisResult[]?)snapshot ?? [];
        var result = new List<TransportEntry>(rows.Length);
        foreach (var row in rows)
        {
            var values = (RedisResult[])row!;
            var fields = (RedisResult[])values[1]!;
            var entries = new NameValueEntry[fields.Length / 2];
            for (int index = 0; index < entries.Length; index++)
                entries[index] = new NameValueEntry((string)fields[index * 2]!, (byte[])fields[index * 2 + 1]!);
            var entry = new StreamEntry((string)values[0]!, entries);
            result.Add(ToEntry(source, resolved, entry, (int)values[2], (string)values[3]!) with
            {
                LockExpiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs + visibilityMs)
            });
        }
        return result;
    }

    public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default)
        => SettleAsync(entry, "complete", null, null, ct);

    public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default)
        => AbandonAsync(entry, TimeSpan.Zero, ct);

    public Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct)
        => SettleAsync(entry, "abandon", redeliveryDelay, null, ct);

    public Task RenewLockAsync(TransportEntry entry, TimeSpan? duration, CancellationToken ct)
        => SettleAsync(entry, "renew", duration ?? _options.DefaultVisibilityTimeout, null, ct);

    public Task DeadLetterAsync(TransportEntry entry, string? reason, CancellationToken ct)
        => SettleAsync(entry, "deadletter", null, reason, ct);

    private async Task SettleAsync(TransportEntry entry, string operation, TimeSpan? duration, string? reason, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        if (entry.Receipt.TransportState is not StreamReceipt receipt)
            throw new ReceiptExpiredException("The entry does not carry a Redis Streams receipt.");
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var arguments = new List<RedisValue>
        {
            receipt.Group, receipt.EntryId, receipt.Token, nowMs, operation,
            nowMs + (long)Math.Max(0, duration?.TotalMilliseconds ?? 0), IsTopicStream(receipt.StreamKey) ? "0" : "1", _options.MaxPendingMessages
        };
        if (operation == "deadletter")
        {
            var headers = entry.Headers.ToBuilder();
            if (!String.IsNullOrEmpty(reason))
                headers.Set(KnownHeaders.DeadLetterReason, reason);
            foreach (var field in BuildFields(entry.ApplicationMessageId, entry.Body, headers.Build(), entry.ContentType))
            {
                arguments.Add(field.Name);
                arguments.Add(field.Value);
            }
        }
        var result = await _db.ScriptEvaluateAsync(SettleScript,
            new RedisKey[] { receipt.StreamKey, LockKey(receipt), MetaKey(receipt), DeadKey(receipt) }, arguments.ToArray()).ConfigureAwait(false);
        if ((long)result != 1)
            throw new ReceiptExpiredException();
    }

    public async Task<IReadOnlyList<TransportEntry>> PeekDeadLetteredAsync(DestinationAddress destination, DeadLetterQuery? query = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new DeadLetterQuery();
        query.Validate();
        var entries = await _db.StreamRangeAsync(DeadKey(Resolve(destination)), minId: query.AfterId is null ? "-" : "(" + query.AfterId, count: query.Limit).ConfigureAwait(false);
        var result = new List<TransportEntry>(entries.Length);
        foreach (var entry in entries)
            result.Add(ToEntry(destination, null, entry, 1, ""));
        return result;
    }

    public async Task<bool> DeleteDeadLetteredAsync(DestinationAddress destination, string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();
        return await _db.StreamDeleteAsync(DeadKey(Resolve(destination)), new RedisValue[] { id }).ConfigureAwait(false) > 0;
    }

    public async Task<bool> ReplayDeadLetteredAsync(DestinationAddress source, string id, DestinationAddress target, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Role is not (DestinationRole.Queue or DestinationRole.Topic))
            throw new ArgumentException("Replay targets must be a queue or topic.", nameof(target));
        var result = await _db.ScriptEvaluateAsync(ReplayScript, new RedisKey[] { DeadKey(Resolve(source)), Resolve(target).StreamKey },
            new RedisValue[] { id, target.Role == DestinationRole.Topic ? "1" : "0", _options.MaxPendingMessages, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() }).ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(declarations);

        foreach (var declaration in declarations)
        {
            if (declaration.AutoDeleteAfter is { } lease)
            {
                await EnsureTemporarySubscriptionAsync(declaration.Address, lease, ct).ConfigureAwait(false);
                continue;
            }
            switch (declaration.Address.Role)
            {
                case DestinationRole.Topic:
                    await _db.ScriptEvaluateAsync("""
                        if redis.call('EXISTS', KEYS[1]) == 0 then
                            local id = redis.call('XADD', KEYS[1], '*', 'init', '1')
                            redis.call('XDEL', KEYS[1], id)
                        end
                        return 1
                        """, new RedisKey[] { TopicStreamKey(declaration.Address.Name) }).ConfigureAwait(false);
                    break;
                default:
                    // Queue, subscription, and binding declarations all materialize as a consumer group on the stream
                    // the address resolves to.
                    await EnsureGroupAsync(Resolve(declaration.Address)).ConfigureAwait(false);
                    break;
            }
        }
    }

    public async Task DeleteAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);

        // A subscription deletes only that group's state (never the shared topic stream — other subscriptions still
        // read it); a queue or topic deletes its own stream and everything scoped to it.
        if (destination.Role is DestinationRole.Subscription or DestinationRole.Binding)
        {
            var sub = Resolve(destination);
            await _db.StreamDeleteConsumerGroupAsync(sub.StreamKey, sub.Group).ConfigureAwait(false);
            await _db.KeyDeleteAsync([LockKey(sub), MetaKey(sub), DeadKey(sub)]).ConfigureAwait(false);
            await _db.SortedSetRemoveAsync((RedisKey)$"{sub.StreamKey}:subscriptions", sub.Group).ConfigureAwait(false);
            _ensuredGroups.TryRemove(GroupKey(sub), out _);
            return;
        }

        var resolved = Resolve(destination);

        // Drop each consumer group's lease/meta state before the stream itself (topic streams can carry several).
        if (await _db.KeyExistsAsync(resolved.StreamKey).ConfigureAwait(false))
        {
            foreach (var group in await _db.StreamGroupInfoAsync(resolved.StreamKey).ConfigureAwait(false))
            {
                var groupSource = resolved with { Group = group.Name };
                await _db.KeyDeleteAsync([LockKey(groupSource), MetaKey(groupSource), DeadKey(groupSource)]).ConfigureAwait(false);
                _ensuredGroups.TryRemove(GroupKey(groupSource), out _);
            }
        }

        await _db.KeyDeleteAsync([resolved.StreamKey, DeadKey(resolved), (RedisKey)$"{resolved.StreamKey}:subscriptions"]).ConfigureAwait(false);
        _ensuredGroups.TryRemove(GroupKey(resolved), out _);
    }

    public async Task<bool> ExistsAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);

        var resolved = Resolve(destination);
        await _db.ScriptEvaluateAsync(TopicRetentionFunctions + "\ncleanupSubscriptions(KEYS[1], ARGV[1]); return 1",
            new RedisKey[] { resolved.StreamKey }, new RedisValue[] { _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() }).ConfigureAwait(false);
        if (!await _db.KeyExistsAsync(resolved.StreamKey).ConfigureAwait(false))
            return false;

        // A subscription exists when its consumer group exists on the topic stream; a queue/topic exists when its
        // stream key does.
        if (destination.Role is not (DestinationRole.Subscription or DestinationRole.Binding))
            return true;

        foreach (var group in await _db.StreamGroupInfoAsync(resolved.StreamKey).ConfigureAwait(false))
        {
            if (String.Equals(group.Name, resolved.Group, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public async Task<MessageDestinationStats> GetStatsAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ct.ThrowIfCancellationRequested();
        var resolved = Resolve(destination);
        var result = await _db.ScriptEvaluateAsync(TopicRetentionFunctions + """

            cleanupSubscriptions(KEYS[1], ARGV[4])
            local queued, working, found = 0, 0, false
            if redis.call('EXISTS', KEYS[1]) == 1 then
                for _, group in ipairs(redis.call('XINFO', 'GROUPS', KEYS[1])) do
                    local name, pending, lag
                    for index = 1, #group, 2 do
                        if group[index] == 'name' then name = group[index + 1] end
                        if group[index] == 'pending' then pending = group[index + 1] end
                        if group[index] == 'lag' then lag = group[index + 1] end
                    end
                    if ARGV[2] == '1' or name == ARGV[1] then
                        found = true
                        queued = queued + (tonumber(lag) or 0)
                        working = working + (tonumber(pending) or 0)
                    end
                end
                if not found and ARGV[3] == '1' then queued = redis.call('XLEN', KEYS[1]) end
            end
            return {queued, working, redis.call('XLEN', KEYS[2])}
            """, new RedisKey[] { resolved.StreamKey, DeadKey(resolved) },
            new RedisValue[] { resolved.Group, destination.Role == DestinationRole.Topic ? "1" : "0", destination.Role == DestinationRole.Queue ? "1" : "0", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() }).ConfigureAwait(false);
        var values = (RedisResult[])result!;
        return new MessageDestinationStats { Queued = (long)values[0], Working = (long)values[1], Deadletter = (long)values[2] };
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _isDisposed, 1);
        return ValueTask.CompletedTask; // the connection multiplexer is owned by the caller
    }

    private async Task EnsureGroupAsync(ResolvedSource resolved)
    {
        if (_ensuredGroups.ContainsKey(GroupKey(resolved)))
            return;

        try
        {
            await _db.StreamCreateConsumerGroupAsync(resolved.StreamKey, resolved.Group, resolved.Position, createStream: true).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            // Group already exists — creation is idempotent.
        }
        _ensuredGroups.TryAdd(GroupKey(resolved), 0);
    }

    // The address is structural, so the physical mapping is derived from it directly — topology declarations and the
    // runtime resolve the SAME address to the SAME stream/group, with no registration cache to drift. A subscription is
    // a consumer group on its owning topic's stream: the group is named by the bare subscription name and scoped by the
    // topic stream key. A queue is its own stream read through the shared default group (competing consumers).
    private ResolvedSource Resolve(DestinationAddress address) => address.Role switch
    {
        DestinationRole.Topic => new ResolvedSource(TopicStreamKey(address.Name), _options.DefaultConsumerGroup, "$"),
        DestinationRole.Subscription or DestinationRole.Binding => new ResolvedSource(TopicStreamKey(address.Topic ?? address.Name), address.Name, "$"),
        _ => new ResolvedSource(QueueStreamKey(address.Name), _options.DefaultConsumerGroup, "0")
    };

    private TransportEntry ToEntry(DestinationAddress destination, ResolvedSource? resolved, StreamEntry entry, int deliveries, string token)
    {
        var headers = MessageHeaders.DeserializeFromJson(GetField(entry, "h"));
        Receipt receipt = resolved is null
            ? default
            : new Receipt { TransportState = new StreamReceipt(resolved.StreamKey.ToString(), resolved.Group, entry.Id.ToString(), token) };

        return new TransportEntry
        {
            Id = entry.Id.ToString(),
            ApplicationMessageId = GetField(entry, "id"),
            ContentType = GetField(entry, "ct"),
            Destination = destination,
            Body = GetBody(entry),
            Headers = headers,
            DeliveryCount = deliveries,
            EnqueuedUtc = ParseStreamIdTime(entry.Id),
            Receipt = receipt
        };
    }

    private static NameValueEntry[] BuildFields(TransportMessage message)
        => BuildFields(message.MessageId, message.Body, message.Headers, message.ContentType);

    private static NameValueEntry[] BuildFields(string? messageId, ReadOnlyMemory<byte> body, MessageHeaders headers, string? contentType = null)
    {
        return
        [
            new NameValueEntry("id", messageId ?? ""),
            new NameValueEntry("ct", contentType ?? ""),
            new NameValueEntry("h", MessageHeaders.SerializeToJson(headers)),
            new NameValueEntry("b", body.ToArray())
        ];
    }

    private static string? GetField(StreamEntry entry, string name)
    {
        foreach (var value in entry.Values)
        {
            if (value.Name == name)
                return value.Value.IsNull ? null : value.Value.ToString();
        }

        return null;
    }

    private static ReadOnlyMemory<byte> GetBody(StreamEntry entry)
    {
        foreach (var value in entry.Values)
        {
            if (value.Name == "b")
                return value.Value.IsNullOrEmpty ? ReadOnlyMemory<byte>.Empty : (byte[])value.Value!;
        }

        return ReadOnlyMemory<byte>.Empty;
    }


    // Stream ids are "<unix-ms>-<seq>"; the timestamp half is the broker enqueue time.
    private static DateTimeOffset? ParseStreamIdTime(RedisValue id)
    {
        string s = id.ToString();
        int dash = s.IndexOf('-');
        string ms = dash > 0 ? s[..dash] : s;
        return Int64.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            : null;
    }

    // Streams are namespaced by role ("q:" queue, "t:" topic) because an XADD lands on whichever stream the key names:
    // without the split, a message type both sent and published would share one stream and cross-deliver (a publish
    // consumed as queue work and vice versa). Subscriptions are consumer groups on the topic stream.
    private static string EncodeKeyPart(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
    private RedisKey QueueStreamKey(string name) => $"{_prefix}q:{EncodeKeyPart(name)}";
    private RedisKey TopicStreamKey(string name) => $"{_prefix}t:{EncodeKeyPart(name)}";
    private bool IsTopicStream(string streamKey) => streamKey.StartsWith($"{_prefix}t:", StringComparison.Ordinal);
    private RedisKey DeadKey(ResolvedSource source)
        => IsTopicStream(source.StreamKey.ToString()) ? $"{source.StreamKey}:dead:{EncodeKeyPart(source.Group)}" : $"{source.StreamKey}:dead";
    private RedisKey DeadKey(StreamReceipt receipt)
        => IsTopicStream(receipt.StreamKey) ? $"{receipt.StreamKey}:dead:{EncodeKeyPart(receipt.Group)}" : $"{receipt.StreamKey}:dead";
    private static RedisKey LockKey(ResolvedSource r) => $"{r.StreamKey}:lock:{EncodeKeyPart(r.Group)}";
    private static RedisKey MetaKey(ResolvedSource r) => $"{r.StreamKey}:meta:{EncodeKeyPart(r.Group)}";
    private static RedisKey LockKey(StreamReceipt r) => $"{r.StreamKey}:lock:{EncodeKeyPart(r.Group)}";
    private static RedisKey MetaKey(StreamReceipt r) => $"{r.StreamKey}:meta:{EncodeKeyPart(r.Group)}";
    private static string GroupKey(ResolvedSource r) => $"{r.StreamKey}|{r.Group}";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);

    private sealed record ResolvedSource(RedisKey StreamKey, string Group, RedisValue Position);

    private sealed record StreamReceipt(string StreamKey, string Group, string EntryId, string Token);
}
