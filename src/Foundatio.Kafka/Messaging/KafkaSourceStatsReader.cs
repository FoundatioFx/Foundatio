using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Reads <see cref="MessageSourceStats"/> for <see cref="KafkaMessageTransport.GetStatsAsync"/> from consumer lag: the
/// partitions' watermarks, through the transport's admin client, and the groups' committed offsets, through a consumer
/// of each group that never subscribes (<see cref="KafkaMessageTransport.CreateOffsetReader"/>). Nothing is created or
/// joined. Those consumers are kept while their group keeps being read, up to a limit, and topic partitions are cached
/// briefly, so a dashboard polling a source costs a round trip for the watermarks and one per consumer group.
/// </summary>
internal sealed class KafkaSourceStatsReader : IAsyncDisposable
{
    /// <summary>How long one read may take before it fails.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PartitionsCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MissingTopicCacheDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly IReadOnlyDictionary<TopicPartition, long> NoOffsets = new Dictionary<TopicPartition, long>();

    private readonly KafkaMessageTransport _transport;
    private readonly IAdminClient _admin;
    private readonly ILogger _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxReaders;
    private readonly ConcurrentDictionary<string, CachedPartitions> _partitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OffsetReader> _readers = new(StringComparer.Ordinal);
    private readonly Timer _sweeper;
    private readonly object _lock = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _disposed;

    public KafkaSourceStatsReader(KafkaMessageTransport transport, IAdminClient admin)
    {
        _transport = transport;
        _admin = admin;
        _logger = transport.Logger;
        _idleTimeout = transport.Options.StatsReaderIdleTimeout;
        _maxReaders = transport.Options.MaxStatsReaders;
        var sweepInterval = TimeSpan.FromTicks(Math.Max(_idleTimeout.Ticks / 2, MinimumWait.Ticks));
        _sweeper = new Timer(_ => Sweep(), null, sweepInterval, sweepInterval);
    }

    /// <summary>How many consumers are kept open to read committed offsets.</summary>
    public int ReaderCount => _readers.Count;

    public async Task<MessageSourceStats> GetAsync(MessageSource source, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, _transport);
            _active++;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            return await ReadAsync(source, Stopwatch.GetTimestamp(), timeout.Token).AnyContext();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MessageBusException($"Could not read the stats of {source} within {ReadTimeout}.", ex);
        }
        catch (KafkaException ex)
        {
            throw new MessageBusException($"Could not read the stats of {source}: {ex.Error.Reason}", ex);
        }
        finally
        {
            lock (_lock)
            {
                if (--_active == 0 && _disposed)
                    _drained.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_active == 0)
                _drained.TrySetResult();
        }

        // Every read is bounded, and the admin client and readers must outlive the calls still using them.
        try
        {
            await _drained.Task.WaitAsync(ReadTimeout * 2).AnyContext();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out waiting for Kafka stats reads to finish");
        }

        await _sweeper.DisposeAsync().ConfigureAwait(false);
        foreach (var reader in _readers.Values)
        {
            if (!await reader.Gate.WaitAsync(ReadTimeout).AnyContext())
                continue;

            reader.Disposed = true;
            reader.Consumer.Dispose();
            reader.Gate.Release();
        }

        _readers.Clear();
    }

    private async Task<MessageSourceStats> ReadAsync(MessageSource source, long started, CancellationToken cancellationToken)
    {
        var names = _transport.Names;
        string deadLetters = names.DeadLetter(source);
        Backlog[] queued;
        if (source.Kind == MessageSourceKind.Queue)
        {
            string queue = names.Queue(source.Name);
            queued = [new Backlog(queue, queue, StartsAtEndWhenNew: false)];
        }
        else
        {
            string inbox = names.SubscriptionInbox(source.Topic!, source.Name);
            queued = [new Backlog(inbox, names.Topic(source.Topic!), StartsAtEndWhenNew: true), new Backlog(inbox, inbox, StartsAtEndWhenNew: false)];
        }

        var deadLettered = new Backlog(deadLetters, deadLetters, StartsAtEndWhenNew: false);
        Backlog[] backlogs = [.. queued, deadLettered];

        var topics = backlogs.Select(b => b.Topic).Distinct(StringComparer.Ordinal).ToList();
        while (true)
        {
            var partitionLists = await Task.WhenAll(topics.Select(t => GetPartitionsAsync(t, started, cancellationToken))).AnyContext();
            var partitions = topics.Zip(partitionLists).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
            var all = partitionLists.SelectMany(p => p).ToList();
            if (all.Count == 0)
                return new MessageSourceStats { Source = source, Queued = 0, DeadLettered = 0 };

            var low = ListOffsetsAsync(all, OffsetSpec.Earliest(), started);
            var high = ListOffsetsAsync(all, OffsetSpec.Latest(), started);
            var committed = backlogs.GroupBy(b => b.GroupId, StringComparer.Ordinal).ToDictionary(
                g => g.Key,
                g => GetCommittedAsync(g.Key, g.SelectMany(b => partitions[b.Topic]).ToList(), started, cancellationToken),
                StringComparer.Ordinal);

            try
            {
                await Task.WhenAll(committed.Values.Append(low).Append(high)).AnyContext();
            }
            catch (KafkaException ex) when (Stopwatch.GetElapsedTime(started) + RetryDelay < ReadTimeout)
            {
                // Partitions may be cached for a topic deleted since, which fails the whole request; read them again.
                _logger.LogDebug(ex, "Retrying reading the stats of {Source}: {Reason}", source, ex.Error.Reason);
                foreach (string topic in topics)
                    _partitions.TryRemove(topic, out _);

                await Task.Delay(RetryDelay, cancellationToken).AnyContext();
                continue;
            }

            long Lag(Backlog backlog) => GetLag(partitions[backlog.Topic], backlog.StartsAtEndWhenNew, low.Result, high.Result, committed[backlog.GroupId].Result);
            return new MessageSourceStats { Source = source, Queued = queued.Sum(Lag), DeadLettered = Lag(deadLettered) };
        }
    }

    /// <summary>
    /// The records after the group's committed offset of each partition, or after the earliest offset where it has
    /// none. A group with no committed offset at all that starts at the end of its topic, a subscription no receiver
    /// has created yet, has none waiting.
    /// </summary>
    private static long GetLag(IReadOnlyList<TopicPartition> partitions, bool startsAtEndWhenNew, IReadOnlyDictionary<TopicPartition, long> low,
        IReadOnlyDictionary<TopicPartition, long> high, IReadOnlyDictionary<TopicPartition, long> committed)
    {
        if (startsAtEndWhenNew && !partitions.Any(p => committed.GetValueOrDefault(p, Offset.Unset.Value) >= 0))
            return 0;

        long lag = 0;
        foreach (var partition in partitions)
        {
            // A partition missing from either is one whose topic was deleted since its partitions were cached.
            if (!low.TryGetValue(partition, out long start) || !high.TryGetValue(partition, out long end))
                continue;

            long position = committed.GetValueOrDefault(partition, Offset.Unset.Value) is >= 0 and var offset ? Math.Max(offset, start) : start;
            lag += Math.Max(0, end - position);
        }

        return lag;
    }

    private async Task<IReadOnlyList<TopicPartition>> GetPartitionsAsync(string topic, long started, CancellationToken cancellationToken)
    {
        if (_partitions.TryGetValue(topic, out var cached) && Stopwatch.GetElapsedTime(cached.ReadAt) < (cached.Partitions.Count > 0 ? PartitionsCacheDuration : MissingTopicCacheDuration))
            return cached.Partitions;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await Task.Run(() => _admin.GetMetadata(topic, Remaining(started)), cancellationToken).AnyContext();
            var topicMetadata = metadata.Topics.FirstOrDefault(t => t.Topic == topic);
            IReadOnlyList<TopicPartition>? partitions = topicMetadata?.Error.Code switch
            {
                ErrorCode.NoError when topicMetadata.Partitions.Count > 0 => topicMetadata.Partitions.Select(p => new TopicPartition(topic, p.PartitionId)).OrderBy(p => p.Partition.Value).ToList(),
                ErrorCode.UnknownTopicOrPart => [],
                _ => null
            };

            if (partitions is not null)
            {
                _partitions[topic] = new CachedPartitions(Stopwatch.GetTimestamp(), partitions);
                return partitions;
            }

            _logger.LogDebug("Retrying reading the partitions of {Topic}: {Reason}", topic, topicMetadata?.Error.Reason ?? "no metadata");
            await Task.Delay(RetryDelay, cancellationToken).AnyContext();
        }
    }

    /// <summary>The offsets of <paramref name="partitions"/>; a partition of a topic that no longer exists is left out.</summary>
    private async Task<IReadOnlyDictionary<TopicPartition, long>> ListOffsetsAsync(IReadOnlyList<TopicPartition> partitions, OffsetSpec spec, long started)
    {
        try
        {
            var result = await _admin.ListOffsetsAsync(partitions.Select(p => new TopicPartitionOffsetSpec { TopicPartition = p, OffsetSpec = spec }),
                new ListOffsetsOptions { RequestTimeout = Remaining(started) }).AnyContext();
            return ToOffsets(result.ResultInfos);
        }
        catch (ListOffsetsException ex) when (ex.Result.ResultInfos.All(r => r.TopicPartitionOffsetError.Error.Code is ErrorCode.NoError or ErrorCode.UnknownTopicOrPart))
        {
            foreach (var info in ex.Result.ResultInfos.Where(r => r.TopicPartitionOffsetError.Error.IsError))
                _partitions.TryRemove(info.TopicPartitionOffsetError.Topic, out _);

            return ToOffsets(ex.Result.ResultInfos.Where(r => !r.TopicPartitionOffsetError.Error.IsError));
        }

        static Dictionary<TopicPartition, long> ToOffsets(IEnumerable<ListOffsetsResultInfo> infos)
        {
            return infos.Select(r => r.TopicPartitionOffsetError).ToDictionary(r => r.TopicPartition, r => r.Offset.Value);
        }
    }

    private async Task<IReadOnlyDictionary<TopicPartition, long>> GetCommittedAsync(string groupId, IReadOnlyList<TopicPartition> partitions, long started, CancellationToken cancellationToken)
    {
        if (partitions.Count == 0)
            return NoOffsets;

        while (true)
        {
            var reader = GetReader(groupId);
            if (reader is null)
            {
                using var consumer = _transport.CreateOffsetReader(groupId);
                return ToOffsets(await _transport.GetCommittedOffsetsAsync(consumer, partitions, Remaining(started), cancellationToken).AnyContext());
            }

            await reader.Gate.WaitAsync(cancellationToken).AnyContext();
            try
            {
                if (reader.Disposed)
                    continue;

                ServeEvents(reader.Consumer);
                return ToOffsets(await _transport.GetCommittedOffsetsAsync(reader.Consumer, partitions, Remaining(started), cancellationToken).AnyContext());
            }
            finally
            {
                reader.LastUsed = Stopwatch.GetTimestamp();
                reader.Gate.Release();
            }
        }

        static Dictionary<TopicPartition, long> ToOffsets(List<TopicPartitionOffset> offsets) => offsets.ToDictionary(o => o.TopicPartition, o => o.Offset.Value);
    }

    /// <summary>
    /// Hands a kept reader's queued events, such as connection errors, to its handlers: librdkafka keeps them until the
    /// consumer polls, and a reader never subscribes, so polling returns nothing else.
    /// </summary>
    private void ServeEvents(IConsumer<byte[]?, byte[]?> consumer)
    {
        try
        {
            consumer.Consume(TimeSpan.Zero);
        }
        catch (KafkaException ex)
        {
            _logger.LogDebug(ex, "Error serving the events of a Kafka stats reader: {Reason}", ex.Error.Reason);
        }
    }

    /// <summary>The kept consumer of <paramref name="groupId"/>, opened if needed, or <c>null</c> when as many are open as allowed.</summary>
    private OffsetReader? GetReader(string groupId)
    {
        if (_readers.TryGetValue(groupId, out var reader))
            return reader;

        if (_readers.Count >= _maxReaders)
            return null;

        var created = new OffsetReader(_transport.CreateOffsetReader(groupId));
        reader = _readers.GetOrAdd(groupId, created);
        if (reader != created)
            created.Consumer.Dispose();

        return reader;
    }

    private void Sweep()
    {
        try
        {
            foreach (var (groupId, reader) in _readers)
            {
                if (Stopwatch.GetElapsedTime(reader.LastUsed) < _idleTimeout || !reader.Gate.Wait(0))
                    continue;

                try
                {
                    if (reader.Disposed || Stopwatch.GetElapsedTime(reader.LastUsed) < _idleTimeout)
                        continue;

                    reader.Disposed = true;
                    _readers.TryRemove(new KeyValuePair<string, OffsetReader>(groupId, reader));
                    reader.Consumer.Dispose();
                }
                finally
                {
                    reader.Gate.Release();
                }
            }

            foreach (var (topic, cached) in _partitions)
            {
                if (Stopwatch.GetElapsedTime(cached.ReadAt) >= PartitionsCacheDuration)
                    _partitions.TryRemove(new KeyValuePair<string, CachedPartitions>(topic, cached));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing idle Kafka stats readers: {Message}", ex.Message);
        }
    }

    private static TimeSpan Remaining(long started)
    {
        var remaining = ReadTimeout - Stopwatch.GetElapsedTime(started);
        return remaining > MinimumWait ? remaining : MinimumWait;
    }

    /// <summary>
    /// Records of <paramref name="Topic"/> that consumer group <paramref name="GroupId"/> has yet to commit.
    /// <paramref name="StartsAtEndWhenNew"/> is set for a subscription's share of its topic, which a new group starts
    /// reading at the end; other groups start at the earliest offset.
    /// </summary>
    private readonly record struct Backlog(string GroupId, string Topic, bool StartsAtEndWhenNew);

    private sealed record CachedPartitions(long ReadAt, IReadOnlyList<TopicPartition> Partitions);

    /// <summary>A consumer kept to read one group's committed offsets; <see cref="Gate"/> guards every use of it.</summary>
    private sealed class OffsetReader(IConsumer<byte[]?, byte[]?> consumer)
    {
        public IConsumer<byte[]?, byte[]?> Consumer { get; } = consumer;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long LastUsed { get; set; } = Stopwatch.GetTimestamp();

        public bool Disposed { get; set; }
    }
}
