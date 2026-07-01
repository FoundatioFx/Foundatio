using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
public sealed class RedisStreamsMessageTransport : IMessageTransport, ISupportsPull, ISupportsVisibilityTimeout,
    ISupportsLockRenewal, ISupportsRedeliveryDelay, ISupportsDeadLetter, ISupportsProvisioning, ISupportsStats, ITransportInfo
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly IReadOnlySet<DestinationRole> _supportedRoles =
        new HashSet<DestinationRole> { DestinationRole.Queue, DestinationRole.Topic, DestinationRole.Subscription, DestinationRole.Binding };

    private readonly RedisStreamsMessageTransportOptions _options;
    private readonly IDatabase _db;
    private readonly TimeProvider _timeProvider;
    private readonly string _prefix;
    private readonly string _consumer;
    // Logical destination name -> resolved (stream key, consumer group, group-create position). Populated by EnsureAsync.
    private readonly ConcurrentDictionary<string, ResolvedSource> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _ensuredGroups = new(StringComparer.Ordinal);
    private int _isDisposed;

    public RedisStreamsMessageTransport(RedisStreamsMessageTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.ConnectionMultiplexer);
        _db = options.ConnectionMultiplexer.GetDatabase();
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _prefix = options.KeyPrefix ?? "";
        _consumer = !String.IsNullOrEmpty(options.ConsumerName) ? options.ConsumerName : $"c-{Guid.NewGuid():N}"[..16];
    }

    public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
    public OrderingGuarantee Ordering => OrderingGuarantee.Fifo;
    public IReadOnlySet<DestinationRole> SupportedRoles => _supportedRoles;
    public int? MaxBatchSize => null;
    public long? MaxMessageBytes => null;
    public TimeSpan? MaxRedeliveryDelay => null; // lease is tracked in Redis, so any delay is honored
    public TimeSpan? MaxVisibilityTimeout => null;

    public async Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(messages);

        // The stream IS the queue/topic; subscriptions read it through their own group, so a send is always an XADD to
        // the destination's stream regardless of role.
        RedisKey streamKey = StreamKey(destination);
        var items = new List<SendItemResult>(messages.Count);
        foreach (var message in messages)
        {
            RedisValue id = await _db.StreamAddAsync(streamKey, BuildFields(message), messageId: null,
                maxLength: _options.MaxStreamLength, useApproximateMaxLength: true).ConfigureAwait(false);
            items.Add(new SendItemResult { MessageId = id.ToString() });
        }

        return new SendResult { Items = items };
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct)
        => ReceiveAsync(source, request, _options.DefaultVisibilityTimeout, ct);

    public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(request);

        var resolved = Resolve(source);
        await EnsureGroupAsync(resolved).ConfigureAwait(false);

        int max = Math.Max(1, request.MaxMessages);
        long visibilityMs = (long)Math.Max(0, visibility.TotalMilliseconds);
        var deadline = _timeProvider.GetUtcNow() + (request.MaxWaitTime ?? TimeSpan.Zero);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var entries = await PollOnceAsync(source, resolved, max, visibilityMs, ct).ConfigureAwait(false);
            if (entries.Count > 0)
                return entries;

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return [];

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task<List<TransportEntry>> PollOnceAsync(string source, ResolvedSource resolved, int max, long visibilityMs, CancellationToken ct)
    {
        var result = new List<TransportEntry>(max);
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        RedisKey lockKey = LockKey(resolved);
        RedisKey metaKey = MetaKey(resolved);

        // 1. Reclaim entries whose lease has lapsed (abandoned, redelivery-delay due, lock expired, crashed consumer).
        // The lease score is updated only after the claim, never removed first, so a crash mid-reclaim can't orphan an
        // entry (it stays reclaimable); the cost is that two instances racing the same lapsed entry may both deliver it
        // — acceptable under at-least-once.
        var dueIds = await _db.SortedSetRangeByScoreAsync(lockKey, Double.NegativeInfinity, nowMs, take: max).ConfigureAwait(false);
        if (dueIds.Length > 0)
        {
            var claimed = await _db.StreamClaimAsync(resolved.StreamKey, resolved.Group, _consumer, 0, dueIds).ConfigureAwait(false);
            foreach (var entry in claimed)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsNull || entry.Values is not { Length: > 0 })
                {
                    // The entry was settled/trimmed since we read the lease; drop our bookkeeping for it.
                    await _db.SortedSetRemoveAsync(lockKey, entry.Id).ConfigureAwait(false);
                    await _db.HashDeleteAsync(metaKey, entry.Id).ConfigureAwait(false);
                    continue;
                }

                int deliveries = ParseDeliveries(await _db.HashGetAsync(metaKey, entry.Id).ConfigureAwait(false)) + 1;
                result.Add(await TrackAsync(source, resolved, entry, deliveries, nowMs, visibilityMs).ConfigureAwait(false));
                if (result.Count >= max)
                    return result;
            }
        }

        // 2. New, never-delivered entries.
        var fresh = await _db.StreamReadGroupAsync(resolved.StreamKey, resolved.Group, _consumer, StreamPosition.NewMessages, max - result.Count).ConfigureAwait(false);
        foreach (var entry in fresh)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(await TrackAsync(source, resolved, entry, 1, nowMs, visibilityMs).ConfigureAwait(false));
        }

        return result;
    }

    // Records the lease (sorted set) + owner token & delivery count (hash) for a just-delivered entry and projects it
    // into a TransportEntry whose Receipt carries everything needed to settle it.
    private async Task<TransportEntry> TrackAsync(string source, ResolvedSource resolved, StreamEntry entry, int deliveries, long nowMs, long visibilityMs)
    {
        string token = Guid.NewGuid().ToString("N");
        await _db.HashSetAsync(MetaKey(resolved), entry.Id, $"{token}|{deliveries}").ConfigureAwait(false);
        await _db.SortedSetAddAsync(LockKey(resolved), entry.Id, nowMs + visibilityMs).ConfigureAwait(false);
        return ToEntry(source, resolved, entry, deliveries, token);
    }

    public async Task CompleteAsync(TransportEntry entry, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var r = await ValidateReceiptAsync(entry).ConfigureAwait(false);

        long acked = await _db.StreamAcknowledgeAsync(r.StreamKey, r.Group, r.EntryId).ConfigureAwait(false);
        await _db.StreamDeleteAsync(r.StreamKey, [r.EntryId]).ConfigureAwait(false);
        await ClearTrackingAsync(r).ConfigureAwait(false);

        if (acked == 0)
            throw new ReceiptExpiredException();
    }

    public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default) => AbandonAsync(entry, TimeSpan.Zero, ct);

    public async Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct)
    {
        ThrowIfDisposed();
        var r = await ValidateReceiptAsync(entry).ConfigureAwait(false);

        // Make the (still-pending) entry reclaimable when the delay lapses; the reclaim pass redelivers the same stream
        // id with an incremented delivery count. delay <= 0 => immediately due.
        long dueMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + (long)Math.Max(0, redeliveryDelay.TotalMilliseconds);
        await _db.SortedSetAddAsync(LockKey(r), r.EntryId, dueMs).ConfigureAwait(false);
    }

    public async Task RenewLockAsync(TransportEntry entry, TimeSpan? duration, CancellationToken ct)
    {
        ThrowIfDisposed();
        var r = await ValidateReceiptAsync(entry).ConfigureAwait(false);
        long until = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + (long)(duration ?? _options.DefaultVisibilityTimeout).TotalMilliseconds;
        await _db.SortedSetAddAsync(LockKey(r), r.EntryId, until).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(TransportEntry entry, string? reason, CancellationToken ct)
    {
        ThrowIfDisposed();
        var r = await ValidateReceiptAsync(entry).ConfigureAwait(false);

        // Match the in-memory reference: record the reason header only when there's a reason (never an empty value).
        var headerBuilder = entry.Headers.ToBuilder();
        if (!String.IsNullOrEmpty(reason))
            headerBuilder.Set(KnownHeaders.DeadLetterReason, reason);
        var headers = headerBuilder.Build();
        await _db.StreamAddAsync(DeadKey(r.StreamKey), BuildFields(entry.Id, entry.Body, headers), messageId: null,
            maxLength: _options.MaxStreamLength, useApproximateMaxLength: true).ConfigureAwait(false);

        await _db.StreamAcknowledgeAsync(r.StreamKey, r.Group, r.EntryId).ConfigureAwait(false);
        await _db.StreamDeleteAsync(r.StreamKey, [r.EntryId]).ConfigureAwait(false);
        await ClearTrackingAsync(r).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TransportEntry>> ReceiveDeadLetteredAsync(string destination, ReceiveRequest request, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(request);

        RedisKey deadKey = DeadKey(StreamKey(destination));
        var entries = await _db.StreamRangeAsync(deadKey, count: Math.Max(1, request.MaxMessages)).ConfigureAwait(false);
        if (entries.Length == 0)
            return [];

        var result = new List<TransportEntry>(entries.Length);
        var ids = new RedisValue[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            ids[i] = entries[i].Id;
            result.Add(ToEntry(destination, resolved: null, entries[i], deliveries: 1, token: ""));
        }

        // Inspecting the dead-letter backlog consumes it.
        await _db.StreamDeleteAsync(deadKey, ids).ConfigureAwait(false);
        return result;
    }

    public async Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(declarations);

        foreach (var declaration in declarations)
        {
            switch (declaration.Role)
            {
                case DestinationRole.Topic:
                    // Topics are read through subscription groups; nothing to create until a subscription appears.
                    break;
                case DestinationRole.Subscription:
                case DestinationRole.Binding:
                    string topic = declaration.Source ?? declaration.Name;
                    var sub = new ResolvedSource(StreamKey(topic), declaration.Name, "$");
                    _sources[declaration.Name] = sub;
                    await EnsureGroupAsync(sub).ConfigureAwait(false);
                    break;
                default:
                    var queue = new ResolvedSource(StreamKey(declaration.Name), _options.DefaultConsumerGroup, "0");
                    _sources[declaration.Name] = queue;
                    await EnsureGroupAsync(queue).ConfigureAwait(false);
                    break;
            }
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resolved = Resolve(name);
        await _db.KeyDeleteAsync([resolved.StreamKey, DeadKey(resolved.StreamKey), LockKey(resolved), MetaKey(resolved)]).ConfigureAwait(false);
        _sources.TryRemove(name, out _);
        _ensuredGroups.TryRemove(GroupKey(resolved), out _);
    }

    public Task<bool> ExistsAsync(string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _db.KeyExistsAsync(Resolve(name).StreamKey);
    }

    public async Task<MessageDestinationStats> GetStatsAsync(string destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        var resolved = Resolve(destination);
        await EnsureGroupAsync(resolved).ConfigureAwait(false);

        long length = await _db.StreamLengthAsync(resolved.StreamKey).ConfigureAwait(false);
        long working = (await _db.StreamPendingAsync(resolved.StreamKey, resolved.Group).ConfigureAwait(false)).PendingMessageCount;
        RedisKey deadKey = DeadKey(resolved.StreamKey);
        long dead = await _db.KeyExistsAsync(deadKey).ConfigureAwait(false) ? await _db.StreamLengthAsync(deadKey).ConfigureAwait(false) : 0;

        return new MessageDestinationStats
        {
            Queued = Math.Max(0, length - working),
            Working = working,
            Deadletter = dead
        };
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _isDisposed, 1);
        return ValueTask.CompletedTask; // the connection multiplexer is owned by the caller
    }

    private async Task<StreamReceipt> ValidateReceiptAsync(TransportEntry entry)
    {
        if (entry.Receipt.TransportState is not StreamReceipt r)
            throw new ReceiptExpiredException("The transport entry does not carry a Redis Streams receipt.");

        // The owner token guards stale receipts: once the entry is redelivered (reclaimed) or settled, the token in the
        // meta hash no longer matches, so a late Complete/Abandon from the previous holder is rejected.
        var current = await _db.HashGetAsync(MetaKey(r), r.EntryId).ConfigureAwait(false);
        if (current.IsNull || ParseToken(current) != r.Token)
            throw new ReceiptExpiredException();

        return r;
    }

    private async Task ClearTrackingAsync(StreamReceipt r)
    {
        await _db.SortedSetRemoveAsync(LockKey(r), r.EntryId).ConfigureAwait(false);
        await _db.HashDeleteAsync(MetaKey(r), r.EntryId).ConfigureAwait(false);
    }

    private async Task EnsureGroupAsync(ResolvedSource resolved)
    {
        if (!_ensuredGroups.TryAdd(GroupKey(resolved), 0))
            return;

        try
        {
            await _db.StreamCreateConsumerGroupAsync(resolved.StreamKey, resolved.Group, resolved.Position, createStream: true).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            // Group already exists — creation is idempotent.
        }
    }

    private ResolvedSource Resolve(string source)
    {
        if (_sources.TryGetValue(source, out var registered))
            return registered;

        // PubSub facade sources are "topic/subscription" (a consumer group on the topic stream); a bare name is a queue
        // on the default group. Parse via the shared convention rather than re-deriving the split.
        return SubscriptionAddress.TryParse(source, out string topic, out string subscription)
            ? new ResolvedSource(StreamKey(topic), subscription, "$")
            : new ResolvedSource(StreamKey(source), _options.DefaultConsumerGroup, "0");
    }

    private TransportEntry ToEntry(string destination, ResolvedSource? resolved, StreamEntry entry, int deliveries, string token)
    {
        string? messageId = GetField(entry, "id");
        var headers = MessageHeaders.DeserializeFromJson(GetField(entry, "h"));
        Receipt receipt = resolved is null
            ? default
            : new Receipt { TransportState = new StreamReceipt(resolved.StreamKey.ToString(), resolved.Group, entry.Id.ToString(), token) };

        return new TransportEntry
        {
            Id = String.IsNullOrEmpty(messageId) ? entry.Id.ToString() : messageId,
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

    private static int ParseDeliveries(RedisValue meta)
    {
        if (meta.IsNullOrEmpty)
            return 0;
        string s = meta.ToString();
        int bar = s.IndexOf('|');
        return bar >= 0 && Int32.TryParse(s.AsSpan(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    private static string ParseToken(RedisValue meta)
    {
        string s = meta.ToString();
        int bar = s.IndexOf('|');
        return bar >= 0 ? s[..bar] : s;
    }

    private RedisKey StreamKey(string name) => $"{_prefix}{name}";
    private static RedisKey DeadKey(RedisKey streamKey) => streamKey.ToString() + ":dead";
    private static RedisKey LockKey(ResolvedSource r) => $"{r.StreamKey}:lock:{r.Group}";
    private static RedisKey MetaKey(ResolvedSource r) => $"{r.StreamKey}:meta:{r.Group}";
    private static RedisKey LockKey(StreamReceipt r) => $"{r.StreamKey}:lock:{r.Group}";
    private static RedisKey MetaKey(StreamReceipt r) => $"{r.StreamKey}:meta:{r.Group}";
    private static string GroupKey(ResolvedSource r) => $"{r.StreamKey}|{r.Group}";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);

    private sealed record ResolvedSource(RedisKey StreamKey, string Group, RedisValue Position);

    private sealed record StreamReceipt(string StreamKey, string Group, string EntryId, string Token);
}
