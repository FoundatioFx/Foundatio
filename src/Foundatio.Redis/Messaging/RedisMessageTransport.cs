using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Redis.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A message transport on Redis Streams. Each queue, subscription, instance subscription and dead-letter queue is a
/// stream with one consumer group; publishing copies a message into the stream of every subscription of the topic.
/// Leases, delays and redelivery are tracked in sorted sets scored by the Redis clock.
/// </summary>
/// <remarks>
/// Groups are ordered by keeping one message of each <see cref="OutgoingMessage.GroupKey"/> outstanding per source:
/// the others wait in Redis, in order, until it is completed or dead-lettered. A receive returns at most one message
/// of a group, so a single group is handled one message at a time; different groups are handled in parallel.
/// Dead-letter queues do not hold groups back, so they can be peeked and replayed in batches.
/// Receivers poll their source with a backoff, and every send to a source also publishes a pub/sub signal on a channel
/// named after it, which wakes its idle receivers in every process. Redis must not evict the transport's keys: see
/// <see href="https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Redis#redis-message-transport"/>.
/// </remarks>
public sealed class RedisMessageTransport : IMessageTransport
{
    internal const string GroupName = "foundatio";
    internal const string ReleasedConsumer = "foundatio:released";
    internal const string IdField = "id";
    internal const string BodyField = "body";
    internal const string GroupKeyField = "group";
    internal const string HeaderFieldPrefix = "h:";
    internal const int ScriptBatchSize = 100;
    private const long MaxMessageSizeLimit = 256 * 1024 * 1024;
    private const int ScriptBatchBytes = 8 * 1024 * 1024;
    internal const int ReceiveBatchBytes = 8 * 1024 * 1024;
    internal const int OrphanedSubscriptionBacklog = 1000;
    private static readonly TimeSpan OrphanedSubscriptionAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrphanedSubscriptionCheckInterval = TimeSpan.FromMinutes(10);
    internal static readonly string DeadLetterReasonField = HeaderFieldPrefix + KnownHeaders.DeadLetterReason;

    private static readonly string Common = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.MessageTransport.Common.lua");
    private static readonly string EnqueueScript = LoadScript("Enqueue");
    private static readonly string RouteScript = LoadScript("Route");
    private static readonly string HeartbeatScript = LoadScript("Heartbeat");
    private static readonly string PruneScript = LoadScript("Prune");
    private static readonly string DeleteScript = LoadScript("Delete");
    private static readonly string CreateScript = LoadScript("Create");
    private static readonly string StatsScript = LoadScript("Stats");
    internal static readonly string ReceiveScript = LoadScript("Receive");
    internal static readonly string SettleScript = LoadScript("Settle");
    internal static readonly string ReleaseScript = LoadScript("Release");
    private static readonly TimeSpan ConsumerPruneIdleTime = TimeSpan.FromHours(1);

    private readonly IConnectionMultiplexer _connection;
    private readonly ConcurrentDictionary<RedisMessageReceiver, byte> _receivers = new();
    private readonly ConcurrentDictionary<RedisKey, long> _orphanChecks = new();
    private int _evictionPolicyChecked;
    private int _disposed;

    /// <summary>Creates a transport on <paramref name="connection"/>, which the caller owns and disposes.</summary>
    /// <exception cref="ArgumentException"><see cref="RedisMessageTransportOptions.Prefix"/> is empty or contains '{' or '}'.</exception>
    public RedisMessageTransport(IConnectionMultiplexer connection, RedisMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Options = options ?? new RedisMessageTransportOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Prefix, nameof(options));
        if (Options.Prefix.AsSpan().IndexOfAny('{', '}') >= 0)
            throw new ArgumentException("The key prefix cannot contain '{' or '}'; the transport adds its own hash tags.", nameof(options));
        ThrowIfShorterThanAMillisecond(Options.InstanceSubscriptionTimeout, nameof(RedisMessageTransportOptions.InstanceSubscriptionTimeout));
        ThrowIfShorterThanAMillisecond(Options.DeduplicationWindow, nameof(RedisMessageTransportOptions.DeduplicationWindow));
        ThrowIfShorterThanAMillisecond(Options.MinPollInterval, nameof(RedisMessageTransportOptions.MinPollInterval));
        if (Options.MaxPollInterval < Options.MinPollInterval)
            throw new ArgumentOutOfRangeException(nameof(options), Options.MaxPollInterval, "MaxPollInterval cannot be shorter than MinPollInterval.");
        if (Options.MaxMessageSize is <= 0 or > MaxMessageSizeLimit)
            throw new ArgumentOutOfRangeException(nameof(options), Options.MaxMessageSize, $"MaxMessageSize must be between 1 and {MaxMessageSizeLimit} bytes.");
        if (Options.DeadLetterMaxLength is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), Options.DeadLetterMaxLength, "DeadLetterMaxLength must be at least 1.");
        if (Options.DeadLetterRetention is { } retention)
            ThrowIfShorterThanAMillisecond(retention, nameof(RedisMessageTransportOptions.DeadLetterRetention));

        _connection = connection;
        TimeProvider = Options.TimeProvider ?? TimeProvider.System;
        Logger = (Options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RedisMessageTransport>();
        Capabilities = new TransportCapabilities
        {
            MaxDelay = null,
            MaxPublishDelay = null,
            MaxRetryDelay = null,
            MaxSendBatchSize = 1000,
            MaxReceiveBatchSize = ScriptBatchSize,
            MaxMessageSize = Options.MaxMessageSize,
            MaxLeaseDuration = null,
            LeaseExpiration = true,
            OrderedGroups = true,
            Deduplication = true
        };
    }

    /// <summary>
    /// Every delay is unlimited (delayed and retried messages wait in sorted sets scored by the Redis clock), groups
    /// are ordered, and <see cref="OutgoingMessage.DeduplicationId"/> is honoured for
    /// <see cref="RedisMessageTransportOptions.DeduplicationWindow"/>. <see cref="TransportCapabilities.MaxMessageSize"/>
    /// comes from <see cref="RedisMessageTransportOptions.MaxMessageSize"/>.
    /// </summary>
    public TransportCapabilities Capabilities { get; }

    internal RedisMessageTransportOptions Options { get; }
    internal TimeProvider TimeProvider { get; }
    internal ILogger Logger { get; }
    internal long DeadLetterMaxLength => Options.DeadLetterMaxLength ?? 0;
    internal long DeadLetterRetentionMilliseconds => Options.DeadLetterRetention is { } retention ? (long)retention.TotalMilliseconds : 0;
    internal IDatabase Database => _connection.GetDatabase(Options.Database);
    internal ISubscriber Subscriber => _connection.GetSubscriber();

    /// <inheritdoc />
    public async Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return;

        CheckEvictionPolicy();
        var now = TimeProvider.GetUtcNow();
        var encoded = messages.Select(m => new EncodedMessage(m, now, Options.MaxMessageSize)).ToList();

        if (destination.Kind == MessageDestinationKind.Queue)
        {
            await EnqueueAsync(RedisSourceKeys.Queue(Options.Prefix, destination.Name), encoded, deduplicate: true, create: true, keyTtl: TimeSpan.Zero).AnyContext();
            return;
        }

        var topic = new RedisTopicKeys(Options.Prefix, destination.Name);
        if (destination.Kind == MessageDestinationKind.Subscription)
        {
            string subscription = destination.SubscriptionName!;
            var keys = RedisSourceKeys.Subscription(Options.Prefix, topic.Topic, subscription);
            long backlog = await EnqueueAsync(keys, encoded, deduplicate: true, create: true, keyTtl: TimeSpan.Zero).AnyContext();
            await Database.SetAddAsync(topic.Subscriptions, subscription).AnyContext();
            await WarnIfOrphanedAsync(keys, backlog).AnyContext();
            return;
        }

        var route = await RouteAsync(topic, encoded).AnyContext();
        if (route.Stale.Count > 0)
            await DeleteInstancesAsync(topic, route.Stale).AnyContext();

        if (route.Accepted.Count == 0 || route.Subscriptions.Count + route.Instances.Count == 0)
            return;

        try
        {
            var instanceKeyTtl = Options.InstanceSubscriptionTimeout * 2;
            await Task.WhenAll(
                route.Subscriptions.Select(s => CopyToSubscriptionAsync(RedisSourceKeys.Subscription(Options.Prefix, topic.Topic, s), route.Accepted))
                    .Concat(route.Instances.Select(i => EnqueueAsync(RedisSourceKeys.Instance(Options.Prefix, topic.Topic, i), route.Accepted, deduplicate: false, create: false, keyTtl: instanceKeyTtl)))).AnyContext();
        }
        catch
        {
            var deduplicationKeys = route.Accepted.Where(m => m.DeduplicationId is not null).Select(m => topic.DeduplicationKey(m.DeduplicationId!)).ToArray();
            if (deduplicationKeys.Length > 0)
                await Database.KeyDeleteAsync(deduplicationKeys, CommandFlags.FireAndForget).AnyContext();

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseDuration, TimeSpan.Zero, nameof(options));
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        CheckEvictionPolicy();

        RedisMessageReceiver receiver;
        if (source.Kind == MessageSourceKind.InstanceSubscription)
        {
            var topic = new RedisTopicKeys(Options.Prefix, source.Topic!);
            string instance = RedisTransportNames.NewInstanceName(source.Name);
            var keys = RedisSourceKeys.Instance(Options.Prefix, topic.Topic, instance);
            await HeartbeatAsync(topic, instance, keys).AnyContext();
            await SweepInstancesAsync(topic).AnyContext();
            receiver = new RedisMessageReceiver(this, source, keys, options, new RedisInstanceSubscription(topic, instance));
        }
        else
        {
            var keys = GetKeys(source);
            await CreateGroupAsync(keys).AnyContext();
            if (source.Kind == MessageSourceKind.Subscription)
                await Database.SetAddAsync(new RedisTopicKeys(Options.Prefix, source.Topic!).Subscriptions, source.Name).AnyContext();

            await Database.ScriptEvaluateAsync(PruneScript, [keys.Stream], [GroupName, ReleasedConsumer, (long)ConsumerPruneIdleTime.TotalMilliseconds]).AnyContext();
            receiver = new RedisMessageReceiver(this, source, keys, options, null);
        }

        _receivers.TryAdd(receiver, 0);
        await receiver.StartAsync().AnyContext();
        return receiver;
    }

    /// <summary>
    /// Counts the messages in a queue or durable subscription with one script, so the counts are read at a single moment
    /// and do not lag. <see cref="MessageSourceStats.Queued"/> includes the messages held behind an unsettled message of
    /// their group, abandoned messages and those whose lease lapsed; <see cref="MessageSourceStats.Delayed"/> includes
    /// retries waiting out their delay. Dead letters older than <see cref="RedisMessageTransportOptions.DeadLetterRetention"/>
    /// are removed first, so they are not counted. Instance subscriptions are not counted (null): their keys are named
    /// for the receiver that owns them.
    /// </summary>
    /// <exception cref="ArgumentException">The source is a dead-letter source.</exception>
    public async Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == MessageSourceKind.DeadLetter)
            throw new ArgumentException("A dead-letter queue is counted as the DeadLettered count of the source it belongs to.", nameof(source));

        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            return null;

        var keys = GetKeys(source);
        var counts = (long[])(await Database.ScriptEvaluateAsync(StatsScript, [keys.Stream, keys.Leases, keys.Retries, keys.Delayed, keys.Held, keys.DeadLetterStream],
            [GroupName, DeadLetterMaxLength, DeadLetterRetentionMilliseconds]).AnyContext())!;

        return new MessageSourceStats { Source = source, Queued = counts[0], Delayed = counts[1], InFlight = counts[2], DeadLettered = counts[3] };
    }

    /// <summary>
    /// Deletes a queue or durable subscription with its messages and dead letters, or only the dead letters of a
    /// <see cref="MessageSourceKind.DeadLetter"/> source. A deleted subscription is first removed from its topic, so it
    /// gets no copies of messages published until it is received from (or sent to) again.
    /// </summary>
    /// <remarks>
    /// Deduplication ids sent to the source are still remembered for <see cref="RedisMessageTransportOptions.DeduplicationWindow"/>.
    /// </remarks>
    public async Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source));

        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var keys = GetKeys(source);
        if (source.Kind == MessageSourceKind.Subscription)
            await Database.SetRemoveAsync(new RedisTopicKeys(Options.Prefix, source.Topic!).Subscriptions, source.Name).AnyContext();

        RedisKey[] deleted = source.Kind == MessageSourceKind.DeadLetter ? keys.All : [.. keys.All, .. keys.DeadLetter.All];
        await Database.ScriptEvaluateAsync(DeleteScript, deleted).AnyContext();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (var receiver in _receivers.Keys)
            await receiver.DisposeAsync().AnyContext();
    }

    internal void Untrack(RedisMessageReceiver receiver) => _receivers.TryRemove(receiver, out _);

    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// Tells idle receivers of a source, in every process, that it may have a message for them. Best effort: a lost
    /// signal only delays delivery until the receivers' next poll.
    /// </summary>
    internal void Signal(RedisChannel channel)
    {
        try
        {
            Subscriber.Publish(channel, RedisValue.EmptyString, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not signal the receivers of {Channel}", channel);
        }
    }

    internal void LogReleasedGroups(RedisSourceKeys keys, long released)
    {
        if (released > 0)
            Logger.LogWarning("Released {Count} message groups of {Source} whose outstanding message Redis no longer has, so their later messages are delivered; " +
                "it was evicted or deleted. Redis must not evict messaging keys: use maxmemory-policy noeviction", released, keys.Description);
    }

    /// <summary>
    /// Adds or renews an instance subscription, creating its stream when it is missing. Returns true when either was
    /// missing: the subscription is new, or it was removed as stale with its keys.
    /// </summary>
    internal async Task<bool> HeartbeatAsync(RedisTopicKeys topic, string instance, RedisSourceKeys keys)
    {
        var timeout = Options.InstanceSubscriptionTimeout;
        bool added = (long)await Database.ScriptEvaluateAsync(HeartbeatScript, [topic.Instances], [instance, (long)timeout.TotalMilliseconds]).AnyContext() == 1;
        var stream = Database.KeyExpireAsync(keys.Stream, timeout * 2);
        await Task.WhenAll(keys.All.Where(k => k != keys.Stream).Select(k => Database.KeyExpireAsync(k, timeout * 2)).Append(stream)).AnyContext();
        bool streamExists = await stream.AnyContext();
        if (!streamExists)
        {
            await CreateGroupAsync(keys).AnyContext();
            await Database.KeyExpireAsync(keys.Stream, timeout * 2).AnyContext();
        }

        return added || !streamExists;
    }

    internal async Task RemoveInstanceAsync(RedisTopicKeys topic, string instance, RedisSourceKeys keys)
    {
        await Database.SortedSetRemoveAsync(topic.Instances, instance).AnyContext();
        await Database.KeyDeleteAsync(keys.All).AnyContext();
    }

    private RedisSourceKeys GetKeys(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => RedisSourceKeys.Queue(Options.Prefix, source.Name),
        MessageSourceKind.Subscription => RedisSourceKeys.Subscription(Options.Prefix, source.Topic!, source.Name),
        MessageSourceKind.DeadLetter => GetKeys(source.DeadLetterOf!).DeadLetter,
        _ => throw new ArgumentException($"{source.Kind} sources are created per receiver.", nameof(source))
    };

    private async Task CreateGroupAsync(RedisSourceKeys keys)
    {
        long released = (long)await Database.ScriptEvaluateAsync(CreateScript, [keys.Stream, keys.Delayed, keys.DelayedData, keys.Groups, keys.Held], [GroupName]).AnyContext();
        LogReleasedGroups(keys, released);
    }

    private void CheckEvictionPolicy()
    {
        if (Interlocked.Exchange(ref _evictionPolicyChecked, 1) == 0)
            _ = Task.Run(WarnIfKeysCanBeEvictedAsync);
    }

    private async Task WarnIfKeysCanBeEvictedAsync()
    {
        try
        {
            foreach (var server in _connection.GetServers().Where(s => s.IsConnected && !s.IsReplica))
            {
                string? policy = null, maxMemory = null;
                foreach (var (name, value) in (await server.InfoAsync("memory").AnyContext()).SelectMany(g => g))
                {
                    if (name == "maxmemory_policy")
                        policy = value;
                    else if (name == "maxmemory")
                        maxMemory = value;
                }

                if (policy is not null and not "noeviction" && maxMemory is not null and not "0")
                {
                    Logger.LogWarning("Redis {Server} evicts keys when it is full (maxmemory-policy {Policy}); an evicted messaging key loses its messages. " +
                        "Use maxmemory-policy noeviction, or a Redis that holds only messaging data", server.EndPoint, policy);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read the Redis eviction policy");
        }
    }

    private async Task SweepInstancesAsync(RedisTopicKeys topic)
    {
        var route = await RouteAsync(topic, []).AnyContext();
        if (route.Stale.Count > 0)
            await DeleteInstancesAsync(topic, route.Stale).AnyContext();
    }

    private async Task DeleteInstancesAsync(RedisTopicKeys topic, IReadOnlyList<string> instances)
    {
        try
        {
            await Task.WhenAll(instances.Select(i => Database.KeyDeleteAsync(RedisSourceKeys.Instance(Options.Prefix, topic.Topic, i).All))).AnyContext();
            Logger.LogInformation("Removed {Count} instance subscriptions of topic {Topic} that missed their heartbeats for {Timeout}: {Instances}",
                instances.Count, topic.Topic, Options.InstanceSubscriptionTimeout, String.Join(", ", instances));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not remove stale instance subscriptions of topic {Topic}", topic.Topic);
        }
    }

    private async Task<Route> RouteAsync(RedisTopicKeys topic, IReadOnlyList<EncodedMessage> messages)
    {
        var deduplicated = messages.Where(m => m.DeduplicationId is not null).ToList();
        var keys = new RedisKey[2 + deduplicated.Count];
        keys[0] = topic.Subscriptions;
        keys[1] = topic.Instances;
        for (int i = 0; i < deduplicated.Count; i++)
            keys[i + 2] = topic.DeduplicationKey(deduplicated[i].DeduplicationId!);

        var result = (RedisResult[])(await Database.ScriptEvaluateAsync(RouteScript, keys, [(long)Options.DeduplicationWindow.TotalMilliseconds]).AnyContext())!;
        var accepted = messages;
        if (deduplicated.Count > 0)
        {
            var flags = (long[])result[3]!;
            var rejected = deduplicated.Where((_, i) => flags[i] == 0).ToHashSet();
            accepted = messages.Where(m => !rejected.Contains(m)).ToList();
        }

        return new Route((string[])result[0]!, (string[])result[1]!, (string[])result[2]!, accepted);
    }

    private async Task CopyToSubscriptionAsync(RedisSourceKeys subscription, IReadOnlyList<EncodedMessage> messages)
    {
        long backlog = await EnqueueAsync(subscription, messages, deduplicate: false, create: false, keyTtl: TimeSpan.Zero).AnyContext();
        await WarnIfOrphanedAsync(subscription, backlog).AnyContext();
    }

    // A subscription whose service is gone still gets a copy of every publish, in Redis memory, until it is deleted. It
    // looks that way when it has a backlog and nothing sent to it in the last hour has been received. Entry ids carry
    // the Redis clock, so they are compared with the real clock rather than the TimeProvider.
    private async Task WarnIfOrphanedAsync(RedisSourceKeys subscription, long backlog)
    {
        if (backlog < OrphanedSubscriptionBacklog)
            return;

        long now = Stopwatch.GetTimestamp();
        if (_orphanChecks.TryGetValue(subscription.Stream, out long checkedAt))
        {
            if (Stopwatch.GetElapsedTime(checkedAt, now) < OrphanedSubscriptionCheckInterval || !_orphanChecks.TryUpdate(subscription.Stream, now, checkedAt))
                return;
        }
        else if (!_orphanChecks.TryAdd(subscription.Stream, now))
        {
            return;
        }

        try
        {
            var group = (await Database.StreamGroupInfoAsync(subscription.Stream).AnyContext()).FirstOrDefault(g => g.Name == GroupName);
            if (group.Name is null || EntryTime(group.LastDeliveredId) is not { } lastReceived || DateTimeOffset.UtcNow - lastReceived < OrphanedSubscriptionAge)
                return;

            Logger.LogWarning("{Source} has {Count} messages, and none sent to it in the last hour has been received. If its service is gone, delete it with " +
                "IMessageAdmin.DeleteAsync; until then every publish to its topic adds a copy to it in Redis memory", subscription.Description, backlog);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not check whether {Source} is still received from", subscription.Description);
        }
    }

    // Stream entry ids start with the Redis time, in milliseconds, at which the entry was added.
    internal static DateTimeOffset? EntryTime(string? entryId)
    {
        if (entryId is null)
            return null;

        int separator = entryId.IndexOf('-');
        return Int64.TryParse(separator > 0 ? entryId.AsSpan(0, separator) : entryId, out long milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
    }

    // Without create, a source that does not exist (a deleted subscription, a removed instance subscription) is skipped.
    // Returns the number of messages in the source afterwards, or -1 when it was skipped.
    private async Task<long> EnqueueAsync(RedisSourceKeys target, IReadOnlyList<EncodedMessage> messages, bool deduplicate, bool create, TimeSpan keyTtl)
    {
        long backlog = 0;
        foreach (var chunk in ChunkForScript(messages))
        {
            var keys = new List<RedisKey>(5 + chunk.Count) { target.Stream, target.Delayed, target.DelayedData, target.Groups, target.Held };
            var values = new List<RedisValue>(4 + chunk.Count * 6) { (long)Options.DeduplicationWindow.TotalMilliseconds, (long)keyTtl.TotalMilliseconds, create ? 1 : 0, GroupName };
            foreach (var message in chunk)
            {
                bool hasDeduplicationKey = deduplicate && message.DeduplicationId is not null;
                if (hasDeduplicationKey)
                    keys.Add(target.DeduplicationKey(message.DeduplicationId!));

                bool ordered = target.OrderedGroups && message.GroupKey is not null;
                values.Add(message.DelayMilliseconds);
                values.Add(hasDeduplicationKey ? 1 : 0);
                values.Add(message.DelayMilliseconds > 0 ? Guid.NewGuid().ToString("N") : String.Empty);
                values.Add(ordered ? 1 : 0);
                values.Add(ordered ? message.GroupKey : String.Empty);
                values.Add(message.Fields);
            }

            var result = (RedisResult[])(await Database.ScriptEvaluateAsync(EnqueueScript, keys.ToArray(), values.ToArray()).AnyContext())!;
            long added = (long)result[0];
            if (added < 0)
                return -1;

            if (added > 0)
                Signal(target.Signal);
            LogReleasedGroups(target, (long)result[1]);
            backlog = (long)result[2];
        }

        return backlog;
    }

    private static IEnumerable<List<EncodedMessage>> ChunkForScript(IReadOnlyList<EncodedMessage> messages)
    {
        var chunk = new List<EncodedMessage>();
        long bytes = 0;
        foreach (var message in messages)
        {
            if (chunk.Count > 0 && (chunk.Count == ScriptBatchSize || bytes + message.Fields.Length > ScriptBatchBytes))
            {
                yield return chunk;
                chunk = [];
                bytes = 0;
            }

            chunk.Add(message);
            bytes += message.Fields.Length;
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    // Intervals are sent to Redis in whole milliseconds, where 0 is rejected or means "no wait".
    private static void ThrowIfShorterThanAMillisecond(TimeSpan value, string option)
    {
        if (value < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException("options", value, $"{option} must be at least 1 millisecond.");
    }

    private static string LoadScript(string name) =>
        Common + EmbeddedResourceLoader.GetEmbeddedResource($"Foundatio.Redis.Scripts.MessageTransport.{name}.lua");

    private sealed record Route(IReadOnlyList<string> Subscriptions, IReadOnlyList<string> Instances, IReadOnlyList<string> Stale, IReadOnlyList<EncodedMessage> Accepted);

    /// <summary>A message encoded once as length-prefixed stream fields, so a script can add it to any number of streams.</summary>
    private sealed class EncodedMessage
    {
        public EncodedMessage(OutgoingMessage message, DateTimeOffset now, long maxSize)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentException.ThrowIfNullOrEmpty(message.MessageId, nameof(message));

            long size = MessageSize.Of(message);
            if (size > maxSize)
                throw new MessageTooLargeException($"Message {message.MessageId} is {size} bytes; the Redis message transport accepts at most {maxSize}.") { Size = size, Limit = maxSize };

            DeduplicationId = message.DeduplicationId;
            GroupKey = message.GroupKey;
            DelayMilliseconds = message.DeliverAt is { } deliverAt && deliverAt > now ? (long)Math.Ceiling((deliverAt - now).TotalMilliseconds) : 0;

            var fields = new List<(byte[] Name, ReadOnlyMemory<byte> Value)>(message.Headers.Count + 3)
            {
                (Encoding.UTF8.GetBytes(IdField), Encoding.UTF8.GetBytes(message.MessageId)),
                (Encoding.UTF8.GetBytes(BodyField), message.Body)
            };
            if (message.GroupKey is not null)
                fields.Add((Encoding.UTF8.GetBytes(GroupKeyField), Encoding.UTF8.GetBytes(message.GroupKey)));
            foreach (var (name, value) in message.Headers)
                fields.Add((Encoding.UTF8.GetBytes(HeaderFieldPrefix + name), Encoding.UTF8.GetBytes(value)));

            var buffer = new byte[fields.Sum(f => 8 + f.Name.Length + f.Value.Length)];
            int position = 0;
            foreach (var (name, value) in fields)
            {
                position = Write(buffer, position, name);
                position = Write(buffer, position, value.Span);
            }

            Fields = buffer;
        }

        public string? DeduplicationId { get; }
        public string? GroupKey { get; }
        public long DelayMilliseconds { get; }
        public byte[] Fields { get; }

        private static int Write(byte[] buffer, int position, ReadOnlySpan<byte> value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(position), (uint)value.Length);
            value.CopyTo(buffer.AsSpan(position + 4));
            return position + 4 + value.Length;
        }
    }
}

internal sealed record RedisInstanceSubscription(RedisTopicKeys Topic, string Name);
