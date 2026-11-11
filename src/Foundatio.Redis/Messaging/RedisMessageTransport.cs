using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    internal static readonly string DeadLetterReasonField = HeaderFieldPrefix + KnownHeaders.DeadLetterReason;

    private static readonly string Common = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.MessageTransport.Common.lua");
    private static readonly string EnqueueScript = LoadScript("Enqueue");
    private static readonly string RouteScript = LoadScript("Route");
    private static readonly string HeartbeatScript = LoadScript("Heartbeat");
    private static readonly string PruneScript = LoadScript("Prune");
    private static readonly string DeleteScript = LoadScript("Delete");
    internal static readonly string ReceiveScript = LoadScript("Receive");
    internal static readonly string SettleScript = LoadScript("Settle");
    internal static readonly string ReleaseScript = LoadScript("Release");
    private static readonly TimeSpan ConsumerPruneIdleTime = TimeSpan.FromHours(1);

    private readonly IConnectionMultiplexer _connection;
    private readonly ConcurrentDictionary<RedisMessageReceiver, byte> _receivers = new();
    private int _disposed;

    public RedisMessageTransport(IConnectionMultiplexer connection, RedisMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Options = options ?? new RedisMessageTransportOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.KeyPrefix, nameof(options));
        if (Options.KeyPrefix.AsSpan().IndexOfAny('{', '}') >= 0)
            throw new ArgumentException("The key prefix cannot contain '{' or '}'; the transport adds its own hash tags.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.InstanceSubscriptionTimeout, TimeSpan.Zero, nameof(options));
        if (Options.MaxMessageSize is <= 0 or > MaxMessageSizeLimit)
            throw new ArgumentOutOfRangeException(nameof(options), Options.MaxMessageSize, $"MaxMessageSize must be between 1 and {MaxMessageSizeLimit} bytes.");

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
    internal IDatabase Database => _connection.GetDatabase(Options.Database);

    public async Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return;

        var now = TimeProvider.GetUtcNow();
        var encoded = messages.Select(m => new EncodedMessage(m, now, Options.MaxMessageSize)).ToList();

        if (destination.Kind == MessageDestinationKind.Queue)
        {
            await EnqueueAsync(RedisSourceKeys.Queue(Options.KeyPrefix, destination.Name), encoded, deduplicate: true, create: true, keyTtl: TimeSpan.Zero).AnyContext();
            return;
        }

        var topic = new RedisTopicKeys(Options.KeyPrefix, destination.Name);
        if (destination.Kind == MessageDestinationKind.Subscription)
        {
            string subscription = destination.SubscriptionName!;
            await EnqueueAsync(RedisSourceKeys.Subscription(Options.KeyPrefix, topic.Topic, subscription), encoded, deduplicate: true, create: true, keyTtl: TimeSpan.Zero).AnyContext();
            await Database.SetAddAsync(topic.Subscriptions, subscription).AnyContext();
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
                route.Subscriptions.Select(s => EnqueueAsync(RedisSourceKeys.Subscription(Options.KeyPrefix, topic.Topic, s), route.Accepted, deduplicate: false, create: false, keyTtl: TimeSpan.Zero))
                    .Concat(route.Instances.Select(i => EnqueueAsync(RedisSourceKeys.Instance(Options.KeyPrefix, topic.Topic, i), route.Accepted, deduplicate: false, create: false, keyTtl: instanceKeyTtl)))).AnyContext();
        }
        catch
        {
            var deduplicationKeys = route.Accepted.Where(m => m.DeduplicationId is not null).Select(m => topic.DeduplicationKey(m.DeduplicationId!)).ToArray();
            if (deduplicationKeys.Length > 0)
                await Database.KeyDeleteAsync(deduplicationKeys, CommandFlags.FireAndForget).AnyContext();

            throw;
        }
    }

    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseDuration, TimeSpan.Zero, nameof(options));
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        RedisMessageReceiver receiver;
        if (source.Kind == MessageSourceKind.InstanceSubscription)
        {
            var topic = new RedisTopicKeys(Options.KeyPrefix, source.Topic!);
            string instance = RedisTransportNames.NewInstanceName(source.Name);
            var keys = RedisSourceKeys.Instance(Options.KeyPrefix, topic.Topic, instance);
            await CreateGroupAsync(keys).AnyContext();
            await HeartbeatAsync(topic, instance, keys).AnyContext();
            await SweepInstancesAsync(topic).AnyContext();
            receiver = new RedisMessageReceiver(this, source, keys, options, new RedisInstanceSubscription(topic, instance));
        }
        else
        {
            var keys = GetKeys(source);
            await CreateGroupAsync(keys).AnyContext();
            if (source.Kind == MessageSourceKind.Subscription)
                await Database.SetAddAsync(new RedisTopicKeys(Options.KeyPrefix, source.Topic!).Subscriptions, source.Name).AnyContext();

            await Database.ScriptEvaluateAsync(PruneScript, [keys.Stream], [GroupName, ReleasedConsumer, (long)ConsumerPruneIdleTime.TotalMilliseconds]).AnyContext();
            receiver = new RedisMessageReceiver(this, source, keys, options, null);
        }

        _receivers.TryAdd(receiver, 0);
        receiver.Start();
        return receiver;
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
            await Database.SetRemoveAsync(new RedisTopicKeys(Options.KeyPrefix, source.Topic!).Subscriptions, source.Name).AnyContext();

        RedisKey[] deleted = source.Kind == MessageSourceKind.DeadLetter ? keys.All : [.. keys.All, .. keys.DeadLetter.All];
        await Database.ScriptEvaluateAsync(DeleteScript, deleted).AnyContext();
    }

    /// <summary>The number of pending, delayed, held and in-flight messages in a source.</summary>
    public async Task<long> GetMessageCountAsync(MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            throw new ArgumentException("Instance subscriptions are private to their receiver.", nameof(source));

        var keys = GetKeys(source);
        var streamLength = Database.StreamLengthAsync(keys.Stream);
        var delayed = Database.SortedSetLengthAsync(keys.Delayed);
        var held = Database.HashLengthAsync(keys.Held);
        return await streamLength.AnyContext() + await delayed.AnyContext() + await held.AnyContext();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (var receiver in _receivers.Keys)
            await receiver.DisposeAsync().AnyContext();
    }

    internal void Untrack(RedisMessageReceiver receiver) => _receivers.TryRemove(receiver, out _);

    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    internal async Task HeartbeatAsync(RedisTopicKeys topic, string instance, RedisSourceKeys keys)
    {
        var timeout = Options.InstanceSubscriptionTimeout;
        await Database.ScriptEvaluateAsync(HeartbeatScript, [topic.Instances], [instance, (long)timeout.TotalMilliseconds]).AnyContext();
        await Task.WhenAll(keys.All.Select(k => Database.KeyExpireAsync(k, timeout * 2))).AnyContext();
    }

    internal async Task RemoveInstanceAsync(RedisTopicKeys topic, string instance, RedisSourceKeys keys)
    {
        await Database.SortedSetRemoveAsync(topic.Instances, instance).AnyContext();
        await Database.KeyDeleteAsync(keys.All).AnyContext();
    }

    private RedisSourceKeys GetKeys(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => RedisSourceKeys.Queue(Options.KeyPrefix, source.Name),
        MessageSourceKind.Subscription => RedisSourceKeys.Subscription(Options.KeyPrefix, source.Topic!, source.Name),
        MessageSourceKind.DeadLetter => GetKeys(source.DeadLetterOf!).DeadLetter,
        _ => throw new ArgumentException($"{source.Kind} sources are created per receiver.", nameof(source))
    };

    private async Task CreateGroupAsync(RedisSourceKeys keys)
    {
        try
        {
            await Database.StreamCreateConsumerGroupAsync(keys.Stream, GroupName, "0", createStream: true).AnyContext();
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
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
            await Task.WhenAll(instances.Select(i => Database.KeyDeleteAsync(RedisSourceKeys.Instance(Options.KeyPrefix, topic.Topic, i).All))).AnyContext();
            Logger.LogDebug("Removed {Count} stale instance subscriptions of topic {Topic}", instances.Count, topic.Topic);
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

    // Without create, a source that does not exist (a deleted subscription, a removed instance subscription) is skipped.
    private async Task EnqueueAsync(RedisSourceKeys target, IReadOnlyList<EncodedMessage> messages, bool deduplicate, bool create, TimeSpan keyTtl)
    {
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

            if ((long)await Database.ScriptEvaluateAsync(EnqueueScript, keys.ToArray(), values.ToArray()).AnyContext() < 0)
                return;
        }
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

            long size = message.Body.Length;
            foreach (var (name, value) in message.Headers)
                size += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value);
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
