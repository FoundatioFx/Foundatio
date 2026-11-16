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
using Foundatio.Redis;
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
/// A receive that drains its source, with room for more messages, registers its receiver as idle; each message then
/// made available wakes one idle receiver, in any process, over a pub/sub channel of its own (sharded on Redis
/// Cluster). Receivers also poll their source with a backoff, which covers a lost wake-up. A topic's instance
/// subscriptions share its cluster slot, so a publish reaches all of them with one script. Redis must not evict the
/// transport's keys: see <see href="https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Redis#redis-message-transport"/>.
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
    private const int MaxPublishAttempts = 10;
    private static readonly TimeSpan OrphanedSubscriptionAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrphanedSubscriptionCheckInterval = TimeSpan.FromMinutes(10);
    internal static readonly string DeadLetterReasonField = HeaderFieldPrefix + KnownHeaders.DeadLetterReason;

    private static readonly string Common = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.MessageTransport.Common.lua");
    private static readonly string EnqueueScript = LoadScript("Enqueue");
    private static readonly string PublishScript = LoadScript("Publish");
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
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _instanceSubscriptions = new(StringComparer.Ordinal);
    private int _evictionPolicyChecked;
    private int _shardedChannels = -1;
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

    /// <summary>The command scripts publish wake-ups with: sharded on Redis Cluster, so only the source's shard sends them.</summary>
    internal string PublishCommand => ShardedChannels ? "SPUBLISH" : "PUBLISH";

    /// <summary>The channel that wakes one receiver, named by its source's wake channel prefix and its consumer name.</summary>
    internal RedisChannel WakeChannel(string name) => ShardedChannels ? RedisChannel.Sharded(name) : RedisChannel.Literal(name);

    // Like the legacy queue and message bus, sharded channels on a cluster and classic ones otherwise, or on a cluster
    // older than Redis 7, which has no sharded pub/sub. Decided once a server is connected, since a disconnected
    // multiplexer cannot tell.
    private bool ShardedChannels
    {
        get
        {
            int sharded = Volatile.Read(ref _shardedChannels);
            if (sharded >= 0)
                return sharded == 1;

            var servers = _connection.GetServers().Where(s => s.IsConnected).ToList();
            if (servers.Count == 0)
                return false;

            bool shardedPubSub = _connection.IsCluster() && servers.All(s => s.Version.Major >= 7);
            Volatile.Write(ref _shardedChannels, shardedPubSub ? 1 : 0);
            return shardedPubSub;
        }
    }

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
            await EnqueueAsync(RedisSourceKeys.Queue(Options.Prefix, destination.Name), encoded, deduplicate: true, create: true, count: false).AnyContext();
            return;
        }

        var topic = new RedisTopicKeys(Options.Prefix, destination.Name);
        if (destination.Kind == MessageDestinationKind.Subscription)
        {
            string subscription = destination.SubscriptionName!;
            var keys = RedisSourceKeys.Subscription(Options.Prefix, topic.Topic, subscription);
            long backlog = await EnqueueAsync(keys, encoded, deduplicate: true, create: true, count: true).AnyContext();
            await Database.SetAddAsync(topic.Subscriptions, subscription).AnyContext();
            await WarnIfOrphanedAsync(keys, backlog).AnyContext();
            return;
        }

        var accepted = new List<EncodedMessage>(encoded.Count);
        try
        {
            var subscriptions = await PublishAsync(topic, encoded, accepted).AnyContext();
            if (accepted.Count > 0 && subscriptions.Count > 0)
                await Task.WhenAll(subscriptions.Select(s => CopyToSubscriptionAsync(RedisSourceKeys.Subscription(Options.Prefix, topic.Topic, s), accepted))).AnyContext();
        }
        catch
        {
            var deduplicationKeys = accepted.Where(m => m.DeduplicationId is not null).Select(m => topic.DeduplicationKey(m.DeduplicationId!)).ToArray();
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

            await Database.ScriptEvaluateAsync(PruneScript, [keys.Stream, keys.Idle], [GroupName, ReleasedConsumer, (long)ConsumerPruneIdleTime.TotalMilliseconds]).AnyContext();
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
        var result = (long[])(await Database.ScriptEvaluateAsync(HeartbeatScript, [topic.Instances, .. keys.All],
            [instance, (long)timeout.TotalMilliseconds, (long)InstanceKeyTtl.TotalMilliseconds, GroupName]).AnyContext())!;
        LogReleasedGroups(keys, result[2]);
        return result[0] == 1 || result[1] == 1;
    }

    internal async Task RemoveInstanceAsync(RedisTopicKeys topic, string instance, RedisSourceKeys keys)
    {
        await Database.SortedSetRemoveAsync(topic.Instances, instance).AnyContext();
        await Database.KeyDeleteAsync(keys.All).AnyContext();
    }

    // An instance subscription's keys outlive a missed heartbeat or two, and expire once its process is gone.
    private TimeSpan InstanceKeyTtl => Options.InstanceSubscriptionTimeout * 2;

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

    private Task SweepInstancesAsync(RedisTopicKeys topic) => PublishAsync(topic, [], []);

    private async Task DeleteInstancesAsync(RedisTopicKeys topic, IReadOnlyList<string> instances)
    {
        try
        {
            await Database.KeyDeleteAsync(instances.SelectMany(i => RedisSourceKeys.Instance(Options.Prefix, topic.Topic, i).All).ToArray()).AnyContext();
            Logger.LogInformation("Removed {Count} instance subscriptions of topic {Topic} that missed their heartbeats for {Timeout}: {Instances}",
                instances.Count, topic.Topic, Options.InstanceSubscriptionTimeout, String.Join(", ", instances));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not remove stale instance subscriptions of topic {Topic}", topic.Topic);
        }
    }

    /// <summary>
    /// Deduplicates the messages and copies the accepted ones, which it adds to <paramref name="accepted"/>, into every
    /// live instance subscription of the topic: one script per chunk, on the topic's slot. Returns the topic's durable
    /// subscriptions, read by the first script.
    /// </summary>
    /// <remarks>
    /// The script declares the keys of the instance subscriptions this transport last saw, and publishes only when they
    /// are the live ones; otherwise it returns those and is called again, so a subscription added since is never missed.
    /// </remarks>
    private async Task<IReadOnlyList<string>> PublishAsync(RedisTopicKeys topic, IReadOnlyList<EncodedMessage> messages, List<EncodedMessage> accepted)
    {
        var instances = _instanceSubscriptions.TryGetValue(topic.Topic, out var known) ? known : [];
        IReadOnlyList<string>? subscriptions = null;
        int published = 0, attempts = 0;
        do
        {
            var chunk = ChunkForPublish(messages, published, instances.Count);
            var result = (RedisResult[])(await Database.ScriptEvaluateAsync(PublishScript, PublishKeys(topic, chunk, instances), PublishValues(topic, chunk, instances)).AnyContext())!;
            var stale = (string[])result[3]!;
            if (stale.Length > 0)
                await DeleteInstancesAsync(topic, stale).AnyContext();

            if ((long)result[0] == 0)
            {
                instances = (string[])result[2]!;
                _instanceSubscriptions[topic.Topic] = instances;
                if (++attempts == MaxPublishAttempts)
                    throw new InvalidOperationException($"The instance subscriptions of topic {topic.Topic} changed during each of {MaxPublishAttempts} attempts to publish to them.");

                continue;
            }

            subscriptions ??= (string[])result[1]!;
            var flags = (long[])result[4]!;
            int flag = 0;
            foreach (var message in chunk)
            {
                if (message.DeduplicationId is null || flags[flag++] == 1)
                    accepted.Add(message);
            }

            published += chunk.Count;
            attempts = 0;
        } while (published < messages.Count);

        return subscriptions ?? [];
    }

    private RedisKey[] PublishKeys(RedisTopicKeys topic, IReadOnlyList<EncodedMessage> chunk, IReadOnlyList<string> instances)
    {
        var keys = new List<RedisKey>(2 + chunk.Count + instances.Count * 6) { topic.Subscriptions, topic.Instances };
        keys.AddRange(chunk.Where(m => m.DeduplicationId is not null).Select(m => topic.DeduplicationKey(m.DeduplicationId!)));
        foreach (string instance in instances)
        {
            var target = RedisSourceKeys.Instance(Options.Prefix, topic.Topic, instance);
            keys.AddRange([target.Stream, target.Delayed, target.DelayedData, target.Groups, target.Held, target.Idle]);
        }

        return keys.ToArray();
    }

    private RedisValue[] PublishValues(RedisTopicKeys topic, IReadOnlyList<EncodedMessage> chunk, IReadOnlyList<string> instances)
    {
        var values = new List<RedisValue>(5 + instances.Count * 2 + (instances.Count > 0 ? chunk.Count * 6 : 0))
        {
            (long)Options.DeduplicationWindow.TotalMilliseconds,
            (long)InstanceKeyTtl.TotalMilliseconds,
            PublishCommand,
            chunk.Count(m => m.DeduplicationId is not null),
            instances.Count
        };
        foreach (string instance in instances)
        {
            values.Add(instance);
            values.Add(RedisSourceKeys.Instance(Options.Prefix, topic.Topic, instance).WakeChannelPrefix);
        }

        // Without instance subscriptions only the deduplication ids are needed.
        if (instances.Count > 0)
        {
            foreach (var message in chunk)
                AddMessageValues(values, message, deduplicate: message.DeduplicationId is not null, ordered: message.GroupKey is not null);
        }

        return values.ToArray();
    }

    // Copies are written once per instance subscription, so a chunk's bytes are shared out among them.
    private static IReadOnlyList<EncodedMessage> ChunkForPublish(IReadOnlyList<EncodedMessage> messages, int start, int instances)
    {
        if (instances == 0)
            return start == 0 ? messages : messages.Skip(start).ToList();

        long budget = ScriptBatchBytes / instances;
        var chunk = new List<EncodedMessage>();
        long bytes = 0;
        for (int i = start; i < messages.Count && chunk.Count < ScriptBatchSize; i++)
        {
            if (chunk.Count > 0 && bytes + messages[i].Fields.Length > budget)
                break;

            chunk.Add(messages[i]);
            bytes += messages[i].Fields.Length;
        }

        return chunk;
    }

    private async Task CopyToSubscriptionAsync(RedisSourceKeys subscription, IReadOnlyList<EncodedMessage> messages)
    {
        long backlog = await EnqueueAsync(subscription, messages, deduplicate: false, create: false, count: true).AnyContext();
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

    // Without create, a source that does not exist (a deleted subscription) is skipped. Returns the number of messages
    // in the source afterwards when counted (0 otherwise), or -1 when it was skipped.
    private async Task<long> EnqueueAsync(RedisSourceKeys target, IReadOnlyList<EncodedMessage> messages, bool deduplicate, bool create, bool count)
    {
        long backlog = 0;
        foreach (var chunk in ChunkForScript(messages))
        {
            var keys = new List<RedisKey>(6 + chunk.Count) { target.Stream, target.Delayed, target.DelayedData, target.Groups, target.Held, target.Idle };
            var values = new List<RedisValue>(6 + chunk.Count * 6)
            {
                (long)Options.DeduplicationWindow.TotalMilliseconds, create ? 1 : 0, GroupName, target.WakeChannelPrefix, PublishCommand, count ? 1 : 0
            };
            foreach (var message in chunk)
            {
                bool hasDeduplicationKey = deduplicate && message.DeduplicationId is not null;
                if (hasDeduplicationKey)
                    keys.Add(target.DeduplicationKey(message.DeduplicationId!));

                AddMessageValues(values, message, hasDeduplicationKey, target.OrderedGroups && message.GroupKey is not null);
            }

            var result = (RedisResult[])(await Database.ScriptEvaluateAsync(EnqueueScript, keys.ToArray(), values.ToArray()).AnyContext())!;
            long added = (long)result[0];
            if (added < 0)
                return -1;

            LogReleasedGroups(target, (long)result[1]);
            backlog = (long)result[2];
        }

        return backlog;
    }

    // The six values a send script reads per message.
    private static void AddMessageValues(List<RedisValue> values, EncodedMessage message, bool deduplicate, bool ordered)
    {
        values.Add(message.DelayMilliseconds);
        values.Add(deduplicate ? 1 : 0);
        values.Add(message.DelayMilliseconds > 0 ? Guid.NewGuid().ToString("N") : String.Empty);
        values.Add(ordered ? 1 : 0);
        values.Add(ordered ? message.GroupKey : String.Empty);
        values.Add(message.Fields);
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
