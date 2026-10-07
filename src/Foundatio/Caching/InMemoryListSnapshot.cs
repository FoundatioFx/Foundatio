using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Foundatio.Caching;

internal interface IListSnapshot
{
    object GetSnapshot();
}

/// <summary>
/// An immutable list value: each update returns a new snapshot and never changes this one, so readers and
/// compare-and-swap writers can share it without locks.
/// </summary>
/// <remarks>
/// A snapshot starts out backed by a plain dictionary, which makes bulk writes a single dictionary copy. The first
/// small update builds an indexed layout: items in fixed-size blocks that keep insertion order and their own
/// expiration bounds, plus a key index split into small hash partitions. Small updates then copy only the blocks and
/// partitions they touch. Expirations are stored as UTC ticks, so items read back with <see cref="DateTimeKind.Utc"/>.
/// </remarks>
internal sealed class ListSnapshot<T> : IListSnapshot where T : notnull
{
    public const int BlockSize = 32;
    private const int PartitionSize = 32;
    private const long Permanent = long.MaxValue;

    // Never handed out or changed, so reads can use it even after the layout is built for the next update
    private readonly Dictionary<T, DateTime?>? _values;
    private readonly bool _fromIncrementalChange;
    private readonly IEqualityComparer<T> _comparer;
    private readonly int _permanentCount;
    private readonly long _minExpiresAtUtcTicks;
    private readonly long _maxExpiresAtUtcTicks;
    private Layout? _layout;
    private Dictionary<T, DateTime?>? _snapshot;

    /// <param name="values">The items, owned by the snapshot from now on.</param>
    /// <param name="fromIncrementalChange">
    /// <c>true</c> when this dictionary came from a small change, so the list is being modified incrementally and the
    /// next small change should build the index; a list written once (or only in bulk) never pays for indexing.
    /// </param>
    public ListSnapshot(Dictionary<T, DateTime?> values, bool fromIncrementalChange = false)
    {
        _values = values;
        _fromIncrementalChange = fromIncrementalChange;
        _comparer = values.Comparer;
        Count = values.Count;
        _minExpiresAtUtcTicks = long.MaxValue;
        foreach (var expiresAtUtc in values.Values)
            Track(ToUtcTicks(expiresAtUtc), ref _permanentCount, ref _minExpiresAtUtcTicks, ref _maxExpiresAtUtcTicks);
    }

    private ListSnapshot(Layout layout, IEqualityComparer<T> comparer, int count)
    {
        _layout = layout;
        _comparer = comparer;
        Count = count;
        _minExpiresAtUtcTicks = long.MaxValue;
        foreach (var block in layout.Blocks)
        {
            _permanentCount += block.PermanentCount;
            _minExpiresAtUtcTicks = Math.Min(_minExpiresAtUtcTicks, block.MinExpiresAtUtcTicks);
            _maxExpiresAtUtcTicks = Math.Max(_maxExpiresAtUtcTicks, block.MaxExpiresAtUtcTicks);
        }
    }

    public int Count { get; }

    /// <summary>Whether small updates use the block and partition index rather than copying the dictionary.</summary>
    public bool IsIndexed => Volatile.Read(ref _layout) is not null;

    /// <summary>
    /// <c>false</c> once a raw read has materialized a dictionary: that dictionary may be changed by its reader when
    /// cloning is off, so from then on it, not this snapshot, holds the list, and writers copy it instead.
    /// </summary>
    public bool IsSourceOfTruth => Volatile.Read(ref _snapshot) is null;

    /// <summary>The latest item expiration, or <c>null</c> when any item is permanent or the list is empty.</summary>
    public DateTime? ExpiresAtUtc => _permanentCount > 0 || Count == 0 ? null : FromUtcTicks(_maxExpiresAtUtcTicks);

    /// <summary>A read-only dictionary over the current items, without copying them.</summary>
    public IDictionary<T, DateTime?> AsReadOnlyDictionary() => new ReadOnlyView(this);

    /// <summary>A new dictionary holding the current items, owned by the caller.</summary>
    public Dictionary<T, DateTime?> ToDictionary()
    {
        if (_values is { } values)
            return new Dictionary<T, DateTime?>(values, _comparer);

        var dictionary = new Dictionary<T, DateTime?>(Count, _comparer);
        foreach (var block in _layout!.Blocks)
            foreach (var item in block.Items)
                if (item.Present)
                    dictionary.Add(item.Key, FromUtcTicks(item.ExpiresAtUtcTicks));
        return dictionary;
    }

    public T[] Read(DateTime utcNow)
    {
        var result = new T[Count];
        int count = 0;
        long utcNowTicks = utcNow.Ticks;
        if (_values is { } values)
        {
            foreach (var pair in values)
                if (pair.Value is null || pair.Value.Value.Ticks >= utcNowTicks)
                    result[count++] = pair.Key;
        }
        else
        {
            foreach (var block in _layout!.Blocks)
                foreach (var item in block.Items)
                    if (item.Present && item.ExpiresAtUtcTicks >= utcNowTicks)
                        result[count++] = item.Key;
        }

        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }

    /// <summary>
    /// The dictionary handed to raw reads: a copy made once, so a reader changing it can't corrupt this snapshot.
    /// </summary>
    public object GetSnapshot()
    {
        if (Volatile.Read(ref _snapshot) is { } snapshot)
            return snapshot;

        var dictionary = ToDictionary();
        return Interlocked.CompareExchange(ref _snapshot, dictionary, null) ?? dictionary;
    }

    public ListSnapshot<T> Update(ICollection<T> items, DateTime? expiresAtUtc, DateTime utcNow, bool remove, out long removed)
    {
        removed = 0;
        long utcNowTicks = utcNow.Ticks;
        bool prune = _minExpiresAtUtcTicks < utcNowTicks;
        if (remove && !prune && !items.Any(ContainsKey))
            return this;

        // When a batch could touch every block, one dictionary copy is cheaper than updating blocks and partitions,
        // and it avoids indexing short-lived bulk lists. The first small change to an unindexed list also copies,
        // since a copy (~3 us at 1,000 items) is far cheaper than building the index for a list that may not change again.
        bool batch = items.Count >= Math.Max(1, Count / BlockSize);
        if (batch || (!IsIndexed && !_fromIncrementalChange))
        {
            var dictionary = ToDictionary();
            if (prune)
                foreach (var key in dictionary.Where(pair => pair.Value?.Ticks < utcNowTicks).Select(pair => pair.Key).ToArray())
                    dictionary.Remove(key);
            if (remove)
                removed = items.Count(dictionary.Remove);
            else
                foreach (var key in items)
                    dictionary[key] = expiresAtUtc;
            return new ListSnapshot<T>(dictionary, fromIncrementalChange: !batch);
        }

        var update = new LayoutUpdate(GetLayout(), _comparer, Count);
        if (prune)
            update.RemoveExpired(utcNowTicks);

        long expiresAtUtcTicks = ToUtcTicks(expiresAtUtc);
        foreach (var item in items)
        {
            if (remove)
            {
                if (update.Remove(item))
                    removed++;
            }
            else
            {
                update.Set(item, expiresAtUtcTicks);
            }
        }

        return update.Changed ? new ListSnapshot<T>(update.Build(), _comparer, update.Count) : this;
    }

    private bool ContainsKey(T key) => TryGetExpiresAtUtc(key, out _);

    private bool TryGetExpiresAtUtc(T key, out DateTime? expiresAtUtc)
    {
        if (_values is { } values)
            return values.TryGetValue(key, out expiresAtUtc);

        var layout = _layout!;
        if (layout.Partitions[layout.PartitionOf(key, _comparer)].TryGetValue(key, out int index))
        {
            expiresAtUtc = FromUtcTicks(layout.Blocks[index / BlockSize].Items[index % BlockSize].ExpiresAtUtcTicks);
            return true;
        }

        expiresAtUtc = null;
        return false;
    }

    private IEnumerable<KeyValuePair<T, DateTime?>> EnumeratePairs()
    {
        if (_values is { } values)
        {
            foreach (var pair in values)
                yield return pair;
            yield break;
        }

        foreach (var block in _layout!.Blocks)
            foreach (var item in block.Items)
                if (item.Present)
                    yield return new KeyValuePair<T, DateTime?>(item.Key, FromUtcTicks(item.ExpiresAtUtcTicks));
    }

    /// <summary>The layout for the next small update, built from the dictionary the first time one needs it.</summary>
    private Layout GetLayout()
    {
        if (Volatile.Read(ref _layout) is { } existing)
            return existing;

        var layout = Layout.Create(_values!, _comparer);
        return Interlocked.CompareExchange(ref _layout, layout, null) ?? layout;
    }

    private static long ToUtcTicks(DateTime? expiresAtUtc) => expiresAtUtc?.Ticks ?? Permanent;

    private static DateTime? FromUtcTicks(long ticks) => ticks == Permanent ? null : new DateTime(ticks, DateTimeKind.Utc);

    private static void Track(long ticks, ref int permanentCount, ref long minTicks, ref long maxTicks)
    {
        if (ticks == Permanent)
        {
            permanentCount++;
            return;
        }

        minTicks = Math.Min(minTicks, ticks);
        maxTicks = Math.Max(maxTicks, ticks);
    }

    // ExpiresAtUtcTicks is Permanent for an item with no expiration; Present is false for a free slot
    private readonly record struct Item(T Key, long ExpiresAtUtcTicks, bool Present = true);

    private sealed class Block
    {
        public Block(Item[] items)
        {
            Items = items;
            MinExpiresAtUtcTicks = long.MaxValue;
            foreach (var item in items)
                if (item.Present)
                    Track(item.ExpiresAtUtcTicks, ref PermanentCount, ref MinExpiresAtUtcTicks, ref MaxExpiresAtUtcTicks);
        }

        public readonly Item[] Items;
        public readonly int PermanentCount;
        public readonly long MinExpiresAtUtcTicks;
        public readonly long MaxExpiresAtUtcTicks;
    }

    private sealed class Layout(Block[] blocks, Dictionary<T, int>[] partitions, ImmutableStack<int> freeSlots, int nextSlot)
    {
        public Block[] Blocks { get; } = blocks;
        public Dictionary<T, int>[] Partitions { get; } = partitions;
        public ImmutableStack<int> FreeSlots { get; } = freeSlots;
        public int NextSlot { get; } = nextSlot;

        public static Layout Create(Dictionary<T, DateTime?> values, IEqualityComparer<T> comparer)
        {
            var blocks = new Block[(values.Count + BlockSize - 1) / BlockSize];
            var items = new Item[BlockSize];
            int slot = 0;
            foreach (var pair in values)
            {
                items[slot % BlockSize] = new Item(pair.Key, ToUtcTicks(pair.Value));
                if (++slot % BlockSize == 0 || slot == values.Count)
                {
                    blocks[(slot - 1) / BlockSize] = new Block(items);
                    items = new Item[BlockSize];
                }
            }

            return new Layout(blocks, BuildPartitions(blocks, values.Count, comparer), ImmutableStack<int>.Empty, values.Count);
        }

        public int PartitionOf(T key, IEqualityComparer<T> comparer) => PartitionOf(key, comparer, Partitions.Length);

        public static Dictionary<T, int>[] BuildPartitions(Block[] blocks, int count, IEqualityComparer<T> comparer)
        {
            int partitionCount = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, count / PartitionSize));
            var partitions = new Dictionary<T, int>[partitionCount];
            for (int i = 0; i < partitions.Length; i++)
                partitions[i] = new Dictionary<T, int>(PartitionSize + PartitionSize / 2, comparer);

            for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
            {
                var blockItems = blocks[blockIndex].Items;
                for (int i = 0; i < blockItems.Length; i++)
                    if (blockItems[i].Present)
                        partitions[PartitionOf(blockItems[i].Key, comparer, partitionCount)].Add(blockItems[i].Key, blockIndex * BlockSize + i);
            }

            return partitions;
        }

        private static int PartitionOf(T key, IEqualityComparer<T> comparer, int partitionCount)
        {
            if (partitionCount == 1)
                return 0;

            // Fibonacci hashing spreads keys whose hash codes share low bits (sequential or aligned values)
            uint hash = (uint)comparer.GetHashCode(key) * 0x9E3779B9u;
            return (int)(hash >> (32 - BitOperations.Log2((uint)partitionCount)));
        }
    }

    /// <summary>
    /// Applies changes to a copy of a layout, copying each block and partition the first time it is touched.
    /// </summary>
    private sealed class LayoutUpdate
    {
        private readonly Layout _source;
        private readonly IEqualityComparer<T> _comparer;
        private Block[] _blocks;
        private Item[]?[] _copiedBlocks;
        private readonly Dictionary<T, int>[] _partitions;
        private readonly bool[] _copiedPartitions;
        private ImmutableStack<int> _freeSlots;
        private int _nextSlot;

        public LayoutUpdate(Layout source, IEqualityComparer<T> comparer, int count)
        {
            _source = source;
            _comparer = comparer;
            _blocks = (Block[])source.Blocks.Clone();
            _copiedBlocks = new Item[]?[source.Blocks.Length];
            _partitions = (Dictionary<T, int>[])source.Partitions.Clone();
            _copiedPartitions = new bool[_partitions.Length];
            _freeSlots = source.FreeSlots;
            _nextSlot = source.NextSlot;
            Count = count;
        }

        public int Count { get; private set; }
        public bool Changed { get; private set; }

        public void RemoveExpired(long utcNowTicks)
        {
            for (int blockIndex = 0; blockIndex < _source.Blocks.Length; blockIndex++)
            {
                var block = _source.Blocks[blockIndex];
                if (block.MinExpiresAtUtcTicks >= utcNowTicks)
                    continue;

                for (int i = 0; i < block.Items.Length; i++)
                {
                    long ticks = block.Items[i].ExpiresAtUtcTicks;
                    if (block.Items[i].Present && ticks < utcNowTicks)
                        Remove(block.Items[i].Key);
                }
            }
        }

        public bool Remove(T key)
        {
            var partition = _partitions[_source.PartitionOf(key, _comparer)];
            if (!partition.TryGetValue(key, out int slot))
                return false;

            WritablePartition(key).Remove(key);
            WritableBlock(slot)[slot % BlockSize] = default;
            _freeSlots = _freeSlots.Push(slot);
            Count--;
            return true;
        }

        public void Set(T key, long expiresAtUtcTicks)
        {
            if (_partitions[_source.PartitionOf(key, _comparer)].TryGetValue(key, out int slot))
            {
                ref readonly var existing = ref ReadSlot(slot);
                if (existing.ExpiresAtUtcTicks == expiresAtUtcTicks)
                    return;

                WritableBlock(slot)[slot % BlockSize] = new Item(existing.Key, expiresAtUtcTicks);
                return;
            }

            if (!_freeSlots.IsEmpty)
                _freeSlots = _freeSlots.Pop(out slot);
            else
                slot = _nextSlot++;

            WritableBlock(slot)[slot % BlockSize] = new Item(key, expiresAtUtcTicks);
            WritablePartition(key).Add(key, slot);
            Count++;
        }

        public Layout Build()
        {
            for (int i = 0; i < _blocks.Length; i++)
                if (_copiedBlocks[i] is { } items)
                    _blocks[i] = new Block(items);

            // Keep partitions small as the list grows, so a change copies a bounded amount of index
            var partitions = Count > _partitions.Length * PartitionSize * 2
                ? Layout.BuildPartitions(_blocks, Count, _comparer)
                : _partitions;
            return new Layout(_blocks, partitions, _freeSlots, _nextSlot);
        }

        private ref readonly Item ReadSlot(int slot)
        {
            int blockIndex = slot / BlockSize;
            return ref (_copiedBlocks[blockIndex] ?? _blocks[blockIndex].Items)[slot % BlockSize];
        }

        private Item[] WritableBlock(int slot)
        {
            int blockIndex = slot / BlockSize;
            if (blockIndex >= _blocks.Length)
            {
                // Slots are handed out in order, so a new slot needs at most one more block
                Array.Resize(ref _blocks, blockIndex + 1);
                Array.Resize(ref _copiedBlocks, blockIndex + 1);
            }

            if (_copiedBlocks[blockIndex] is { } copied)
                return copied;

            Changed = true;
            var items = _blocks[blockIndex] is { } block ? (Item[])block.Items.Clone() : new Item[BlockSize];
            _copiedBlocks[blockIndex] = items;
            return items;
        }

        private Dictionary<T, int> WritablePartition(T key)
        {
            int index = _source.PartitionOf(key, _comparer);
            if (!_copiedPartitions[index])
            {
                _partitions[index] = new Dictionary<T, int>(_partitions[index], _comparer);
                _copiedPartitions[index] = true;
            }

            return _partitions[index];
        }
    }

    /// <summary>A read-only <see cref="IDictionary{TKey,TValue}"/> over a snapshot, used to size lists without copying them.</summary>
    private sealed class ReadOnlyView(ListSnapshot<T> snapshot) : IDictionary<T, DateTime?>, IReadOnlyDictionary<T, DateTime?>, ICollection
    {
        public int Count => snapshot.Count;
        public bool IsReadOnly => true;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => snapshot;

        public DateTime? this[T key]
        {
            get => snapshot.TryGetExpiresAtUtc(key, out var expiresAtUtc) ? expiresAtUtc : throw new KeyNotFoundException($"The key '{key}' was not found in the list.");
            set => throw ReadOnly();
        }

        public ICollection<T> Keys => new ReadOnlyCollection<T>(snapshot.EnumeratePairs().Select(pair => pair.Key).ToArray());
        public ICollection<DateTime?> Values => new ReadOnlyCollection<DateTime?>(snapshot.EnumeratePairs().Select(pair => pair.Value).ToArray());
        IEnumerable<T> IReadOnlyDictionary<T, DateTime?>.Keys => Keys;
        IEnumerable<DateTime?> IReadOnlyDictionary<T, DateTime?>.Values => Values;

        public bool ContainsKey(T key) => snapshot.ContainsKey(key);
        public bool TryGetValue(T key, out DateTime? value) => snapshot.TryGetExpiresAtUtc(key, out value);
        public bool Contains(KeyValuePair<T, DateTime?> item) => snapshot.TryGetExpiresAtUtc(item.Key, out var value) && value == item.Value;
        public IEnumerator<KeyValuePair<T, DateTime?>> GetEnumerator() => snapshot.EnumeratePairs().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void CopyTo(KeyValuePair<T, DateTime?>[] array, int arrayIndex)
        {
            foreach (var pair in snapshot.EnumeratePairs())
                array[arrayIndex++] = pair;
        }

        void ICollection.CopyTo(Array array, int index)
        {
            foreach (var pair in snapshot.EnumeratePairs())
                array.SetValue(pair, index++);
        }

        public void Add(T key, DateTime? value) => throw ReadOnly();
        public void Add(KeyValuePair<T, DateTime?> item) => throw ReadOnly();
        public bool Remove(T key) => throw ReadOnly();
        public bool Remove(KeyValuePair<T, DateTime?> item) => throw ReadOnly();
        public void Clear() => throw ReadOnly();

        private static NotSupportedException ReadOnly() => new("Cached list values are read-only.");
    }
}
