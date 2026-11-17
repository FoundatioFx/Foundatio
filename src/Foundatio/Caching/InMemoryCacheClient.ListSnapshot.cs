using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Foundatio.Caching;

public partial class InMemoryCacheClient
{
    // Updates share untouched pages; materializing a public dictionary retires the internal storage.
    private sealed class ListSnapshot<T> : IListSnapshot, ICollection where T : notnull
    {
        public const int BlockSize = 32;
        private Storage _storage;

        private readonly struct Item(T key, DateTime? expiration, bool present, int next)
        {
            private readonly DateTime _expiration = expiration.GetValueOrDefault();
            public T Key { get; } = key;
            public bool Present { get; } = present;
            private readonly bool _hasExpiration = expiration.HasValue;
            public int Next { get; } = next;
            public DateTime? Expiration => _hasExpiration ? _expiration : null;
            public bool IsExpired(DateTime utcNow) => _hasExpiration && _expiration < utcNow;
        }

        public ListSnapshot(Dictionary<T, DateTime?> values) => _storage = new Storage(values);

        public ListSnapshot(HashSet<T> keys, DateTime? expiration, IEqualityComparer<T> comparer)
        {
            int bucketCount = 1;
            while ((long)bucketCount * 2 < keys.Count)
                bucketCount *= 2;
            var values = new Pages<Item>.Builder(null, keys.Count);
            var heads = new Pages<int>.Builder(null, bucketCount);
            var hashes = new Pages<int>.Builder(null, keys.Count);
            Item[] valueBlock = [];
            int[] hashBlock = [];
            int index = 0;
            foreach (var key in keys)
            {
                int hash = comparer.GetHashCode(key);
                int bucket = HashCode.Combine(hash) & (bucketCount - 1);
                int offset = index % BlockSize;
                if (offset == 0)
                {
                    valueBlock = values.GetWritableBlock(index);
                    hashBlock = hashes.GetWritableBlock(index);
                }
                valueBlock[offset] = new Item(key, expiration, true, heads[bucket]);
                hashBlock[offset] = hash;
                heads[bucket] = ++index;
            }
            _storage = new Storage(values.Value, heads.Value, hashes.Value, comparer, keys.Count, bucketCount,
                0, keys.Count, expiration.HasValue ? 0 : keys.Count);
        }

        private ListSnapshot(Storage storage) => _storage = storage;

        public int Count => Volatile.Read(ref _storage).Count;
        public string ValueTypeName => typeof(Dictionary<T, DateTime?>).Name;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;
        void ICollection.CopyTo(Array array, int index) => ((ICollection)CopyValues(Volatile.Read(ref _storage))).CopyTo(array, index);
        IEnumerator IEnumerable.GetEnumerator() => Enumerate(Volatile.Read(ref _storage)).GetEnumerator();
        public DateTime? ExpiresAt => Volatile.Read(ref _storage).ExpiresAt;

        public Dictionary<T, DateTime?>? Snapshot
        {
            get
            {
                var storage = Volatile.Read(ref _storage);
                return storage.Exposed ? storage.Dictionary : null;
            }
        }

        public T[] Read(DateTime utcNow)
        {
            var storage = Volatile.Read(ref _storage);
            bool hasExpired = storage.Expirations?.Range.Minimum < utcNow;
            if (storage.Dictionary is { } dictionary)
                return ReadListValues(dictionary, utcNow, storage.Exposed || hasExpired);

            var result = new T[storage.Count];
            int count = 0;
            foreach (var group in storage.Items!.Groups)
            {
                if (group is null)
                    continue;
                foreach (var block in group)
                {
                    if (block is null)
                        continue;
                    if (hasExpired)
                    {
                        foreach (ref readonly var item in block.AsSpan())
                            if (item.Present && !item.IsExpired(utcNow))
                                result[count++] = item.Key;
                    }
                    else
                    {
                        foreach (ref readonly var item in block.AsSpan())
                            if (item.Present)
                                result[count++] = item.Key;
                    }
                }
            }
            if (count != result.Length)
                Array.Resize(ref result, count);
            return result;
        }

        public CacheValue<ICollection<T>> ReadPage(DateTime utcNow, int page, int pageSize)
        {
            var storage = Volatile.Read(ref _storage);
            bool hasExpired = storage.Expirations?.Range.Minimum < utcNow;
            if (storage.Dictionary is { } dictionary)
                return ReadListPage(dictionary, utcNow, page, pageSize, storage.Exposed || hasExpired);

            var result = new ListPage<T>(storage.Count, page, pageSize);
            foreach (var group in storage.Items!.Groups)
            {
                if (group is null)
                    continue;
                foreach (var block in group)
                {
                    if (block is null)
                        continue;
                    if (result.CanSkip(block.Length))
                    {
                        int count = 0;
                        if (hasExpired)
                        {
                            foreach (ref readonly var item in block.AsSpan())
                                if (item.Present && !item.IsExpired(utcNow))
                                    count++;
                        }
                        else
                        {
                            foreach (ref readonly var item in block.AsSpan())
                                count += item.Present ? 1 : 0;
                        }
                        result.TrySkip(count);
                        continue;
                    }
                    if (hasExpired)
                    {
                        foreach (ref readonly var item in block.AsSpan())
                        {
                            if (!item.Present || item.IsExpired(utcNow))
                                continue;
                            if (result.TrySkip())
                                continue;
                            result.Add(item.Key);
                            if (result.IsFull)
                                return result.ToCacheValue();
                        }
                    }
                    else
                    {
                        foreach (ref readonly var item in block.AsSpan())
                        {
                            if (!item.Present)
                                continue;
                            if (result.TrySkip())
                                continue;
                            result.Add(item.Key);
                            if (result.IsFull)
                                return result.ToCacheValue();
                        }
                    }
                }
            }
            return result.ToCacheValue();
        }

        public object GetSnapshot()
        {
            var storage = Volatile.Read(ref _storage);
            if (storage.Exposed)
                return storage.Dictionary!;
            var dictionary = storage.Dictionary ?? CopyValues(storage);
            var exposed = new Storage(dictionary, exposed: true);
            var previous = Interlocked.CompareExchange(ref _storage, exposed, storage);
            return ReferenceEquals(previous, storage) ? dictionary : previous.Dictionary!;
        }

        private static IEnumerable<KeyValuePair<T, DateTime?>> Enumerate(Storage storage)
        {
            if (storage.Dictionary is { } dictionary)
            {
                foreach (var pair in dictionary)
                    yield return pair;
                yield break;
            }
            foreach (var group in storage.Items!.Groups)
            {
                if (group is null)
                    continue;
                foreach (var block in group)
                {
                    if (block is null)
                        continue;
                    foreach (var item in block)
                        if (item.Present)
                            yield return new KeyValuePair<T, DateTime?>(item.Key, item.Expiration);
                }
            }
        }

        private static Dictionary<T, DateTime?> CopyValues(Storage storage)
        {
            if (storage.Dictionary is { } values)
                return new Dictionary<T, DateTime?>(values, values.Comparer);
            var dictionary = new Dictionary<T, DateTime?>(storage.Count, storage.Comparer);
            foreach (var pair in Enumerate(storage))
                dictionary.Add(pair.Key, pair.Value);
            return dictionary;
        }

        private static bool ExpirationsEqual(DateTime? left, DateTime? right) =>
            left == right && left.GetValueOrDefault().Kind == right.GetValueOrDefault().Kind;

        public ListSnapshot<T> Update(ICollection<T> items, DateTime? expiration, DateTime utcNow, bool remove, out long removed)
        {
            var storage = Volatile.Read(ref _storage);
            removed = 0;
            int count = storage.Count;
            int bucketCount = storage.BucketCount;
            Pages<Item>.Builder? values = null;
            Pages<int>.Builder? heads = null;
            Pages<int>.Builder? hashes = null;
            int largeBatchThreshold = Math.Max(1, count / BlockSize);
            bool largeBatch = items.Count >= largeBatchThreshold;
            bool prune = storage.Exposed
                ? storage.Dictionary!.Any(pair => pair.Value < utcNow)
                : storage.Expirations?.Range.Minimum < utcNow;
            if (remove && !prune && !items.Any(key => storage.Dictionary?.ContainsKey(key) ?? Find(key, out _, out _, out _) >= 0))
                return this;
            if (!remove && !prune && (storage.Dictionary is not null || largeBatch))
            {
                if (IsUnchangedBatch(items, out bool smallUpdate))
                    return this;
                if (smallUpdate)
                    largeBatch = false;
            }

            bool compactExpired = remove && storage.Items is not null && storage.LatestExpiration < utcNow
                && storage.PermanentCount < count / 4 && UsesBuiltInListKeyEquality(storage.Comparer);
            if (storage.Exposed || largeBatch || count < storage.NextIndex / 4 || compactExpired)
            {
                var dictionary = compactExpired
                    ? new Dictionary<T, DateTime?>(storage.PermanentCount, storage.Comparer)
                    : CopyValues(storage);
                if (compactExpired)
                {
                    foreach (var pair in Enumerate(storage))
                        if (pair.Value is null)
                            dictionary.Add(pair.Key, null);
                }
                else if (prune)
                    foreach (var key in dictionary.Where(pair => pair.Value < utcNow).Select(pair => pair.Key).ToArray())
                        dictionary.Remove(key);
                if (remove)
                    removed = items.Count(dictionary.Remove);
                else
                    foreach (var key in items)
                        dictionary[key] = expiration;
                if (compactExpired && removed > 0)
                    dictionary = new Dictionary<T, DateTime?>(dictionary, storage.Comparer);
                if (dictionary.Count > 0 && dictionary.Count < dictionary.EnsureCapacity(0) / 4)
                    dictionary.TrimExcess();
                return new ListSnapshot<T>(dictionary);
            }

            int nextIndex = storage.NextIndex;
            int capacity = Math.Max(nextIndex, count + (remove ? 0 : items.Count));
            if (bucketCount == 0)
                bucketCount = 1;
            while ((long)bucketCount * 2 < count)
                bucketCount *= 2;
            bool rebuildIndex = storage.Items is not null && bucketCount != storage.BucketCount;
            values = new Pages<Item>.Builder(storage.Items, capacity);
            heads = new Pages<int>.Builder(rebuildIndex ? null : storage.Heads, bucketCount);
            hashes = new Pages<int>.Builder(storage.Hashes, capacity);
            if (storage.Dictionary is { } original)
            {
                int index = 0;
                foreach (var pair in original)
                {
                    int hash = storage.Comparer.GetHashCode(pair.Key);
                    int bucket = HashCode.Combine(hash) & (bucketCount - 1);
                    values[index] = new Item(pair.Key, pair.Value, true, heads[bucket]);
                    hashes[index] = hash;
                    heads[bucket] = ++index;
                }
            }
            else if (rebuildIndex)
            {
                // Grow the index without moving slots used by paging and size sampling.
                for (int index = 0; index < nextIndex; index++)
                {
                    var item = values[index];
                    if (!item.Present)
                        continue;
                    int bucket = HashCode.Combine(hashes[index]) & (bucketCount - 1);
                    values[index] = new Item(item.Key, item.Expiration, true, heads[bucket]);
                    heads[bucket] = index + 1;
                }
            }

            int freeListHead = storage.FreeListHead;
            int permanentCount = storage.PermanentCount;
            bool changed = rebuildIndex;
            if (prune)
                foreach (var block in (storage.Items ?? values.Value).Blocks)
                    foreach (var item in block)
                        if (item.Present && item.Expiration < utcNow)
                            Remove(item.Key);

            foreach (var item in items)
            {
                if (remove)
                {
                    if (Remove(item))
                        removed++;
                    continue;
                }
                T key = item;
                int index = Find(key, out _, out int bucket, out int hash);
                int next;
                if (index >= 0)
                {
                    var previous = values[index];
                    if (ExpirationsEqual(previous.Expiration, expiration))
                        continue;
                    key = previous.Key;
                    next = previous.Next;
                    if (previous.Expiration is null)
                        permanentCount--;
                }
                else
                {
                    if (freeListHead != 0)
                    {
                        index = freeListHead - 1;
                        freeListHead = values[index].Next;
                    }
                    else
                        index = nextIndex++;
                    next = heads[bucket];
                    heads[bucket] = index + 1;
                    hashes[index] = hash;
                    count++;
                }
                values[index] = new Item(key, expiration, true, next);
                if (expiration is null)
                    permanentCount++;
                changed = true;
            }
            if (!changed)
                return this;

            var updated = new Storage(values.Value, heads.Value, hashes.Value, storage.Comparer, count, bucketCount,
                freeListHead, nextIndex, permanentCount, storage);
            return count > 0 && count < nextIndex / 4
                ? new ListSnapshot<T>(CopyValues(updated))
                : new ListSnapshot<T>(updated);

            bool IsUnchangedBatch(ICollection<T> batch, out bool smallUpdate)
            {
                smallUpdate = false;
                if (storage.Dictionary is not null || batch is not HashSet<T> keys
                    || count != keys.Count || !ReferenceEquals(storage.Comparer, keys.Comparer))
                    return batch.All(IsUnchanged);

                // Equal counts and comparers let us count new keys by scanning for missing stored keys.
                int changes = 0;
                foreach (var pair in Enumerate(storage))
                    if (!ExpirationsEqual(pair.Value, expiration) || !keys.Contains(pair.Key))
                        if (storage.FreeListHead != 0 || ++changes >= largeBatchThreshold)
                            return false;
                smallUpdate = changes > 0;
                return changes == 0;
            }

            bool IsUnchanged(T key)
            {
                if (storage.Dictionary is { } dictionary)
                    return dictionary.TryGetValue(key, out var previous) && ExpirationsEqual(previous, expiration);
                int index = Find(key, out _, out _, out _);
                return index >= 0 && ExpirationsEqual(storage.Items![index].Expiration, expiration);
            }

            int Find(T key, out int previous, out int bucket, out int hash)
            {
                hash = storage.Comparer.GetHashCode(key);
                bucket = HashCode.Combine(hash) & (bucketCount - 1);
                var currentValues = values?.Value ?? storage.Items!;
                var currentHeads = heads?.Value ?? storage.Heads!;
                var currentHashes = hashes?.Value ?? storage.Hashes!;
                previous = -1;
                for (int link = currentHeads[bucket]; link != 0;)
                {
                    int index = link - 1;
                    var current = currentValues[index];
                    if (currentHashes[index] == hash && storage.Comparer.Equals(current.Key, key))
                        return index;
                    previous = index;
                    link = current.Next;
                }
                return -1;
            }

            bool Remove(T key)
            {
                int index = Find(key, out int previous, out int bucket, out _);
                if (index < 0)
                    return false;
                var item = values![index];
                if (previous < 0)
                    heads![bucket] = item.Next;
                else
                {
                    var predecessor = values[previous];
                    values[previous] = new Item(predecessor.Key, predecessor.Expiration, true, item.Next);
                }
                // Vacant slots hold the free-list link without retaining the removed key.
                values[index] = new Item(default!, null, false, freeListHead);
                if (item.Expiration is null)
                    permanentCount--;
                freeListHead = index + 1;
                count--;
                changed = true;
                return true;
            }
        }

        private sealed class Storage
        {
            public Dictionary<T, DateTime?>? Dictionary { get; }
            public bool Exposed { get; }
            public Pages<Item>? Items { get; }
            public Pages<int>? Heads { get; }
            public Pages<int>? Hashes { get; }
            public IEqualityComparer<T> Comparer { get; }
            private readonly int _count;
            public int Count => Dictionary?.Count ?? _count;
            public int BucketCount { get; }
            public ExpirationIndex? Expirations { get; }
            public int FreeListHead { get; }
            public int NextIndex { get; }
            public int PermanentCount { get; }
            public DateTime? LatestExpiration => Expirations?.Range.Maximum;
            public DateTime? ExpiresAt => Exposed
                ? Dictionary!.Count == 0 ? null : GetListExpiration(Dictionary)
                : PermanentCount > 0 ? null : LatestExpiration;

            public Storage(Dictionary<T, DateTime?> dictionary, bool exposed = false)
            {
                Dictionary = dictionary;
                Exposed = exposed;
                Comparer = dictionary.Comparer;
                NextIndex = dictionary.Count;
                if (exposed)
                    return;
                ExpirationRange range = default;
                foreach (var pair in dictionary)
                {
                    if (pair.Value is { } expiration)
                        range = range.Include(expiration);
                    else
                        PermanentCount++;
                }
                if (range.HasExpiration)
                    Expirations = new ExpirationIndex(range);
            }

            public Storage(Pages<Item> items, Pages<int> heads, Pages<int> hashes, IEqualityComparer<T> comparer, int count, int bucketCount,
                int freeListHead, int nextIndex, int permanentCount, Storage? previous = null)
            {
                Items = items;
                Heads = heads;
                Hashes = hashes;
                Comparer = comparer;
                _count = count;
                BucketCount = bucketCount;
                FreeListHead = freeListHead;
                NextIndex = nextIndex;
                PermanentCount = permanentCount;
                if (permanentCount != count)
                    Expirations = ExpirationIndex.Update(items, previous);
            }
        }

        private readonly struct ExpirationRange(DateTime minimum, DateTime maximum)
        {
            public bool HasExpiration { get; } = true;
            public DateTime Minimum { get; } = minimum;
            public DateTime Maximum { get; } = maximum;

            public ExpirationRange Include(DateTime expiration) => Include(new ExpirationRange(expiration, expiration));

            public ExpirationRange Include(ExpirationRange other)
            {
                if (!other.HasExpiration)
                    return this;
                if (!HasExpiration)
                    return other;
                return new ExpirationRange(Minimum <= other.Minimum ? Minimum : other.Minimum,
                    Maximum >= other.Maximum ? Maximum : other.Maximum);
            }

            public bool Equals(ExpirationRange other) => HasExpiration == other.HasExpiration
                && (!HasExpiration || ExpirationsEqual(Minimum, other.Minimum) && ExpirationsEqual(Maximum, other.Maximum));
        }

        private sealed class ExpirationIndex(ExpirationRange range, ExpirationIndex.Group?[]? groups = null)
        {
            public ExpirationRange Range { get; } = range;
            private Group?[]? Groups { get; } = groups;

            public static ExpirationIndex? Update(Pages<Item> items, Storage? previous)
            {
                var itemGroups = items.Groups;
                var previousItems = previous?.Items?.Groups;
                var previousIndex = previous?.Expirations;
                var previousGroups = previousIndex?.Groups;
                var groups = previousGroups;
                ExpirationRange range = default;
                for (int index = 0; index < itemGroups.Length; index++)
                {
                    var oldItems = previousItems is not null && index < previousItems.Length ? previousItems[index] : null;
                    var oldGroup = previousGroups is not null && index < previousGroups.Length ? previousGroups[index] : null;
                    var group = ReferenceEquals(itemGroups[index], oldItems) ? oldGroup
                        : itemGroups[index] is { } changed ? Group.Update(changed, oldItems, oldGroup) : null;
                    if (group is not null)
                        range = range.Include(group.Range);
                    if (ReferenceEquals(group, oldGroup))
                        continue;
                    if (ReferenceEquals(groups, previousGroups))
                    {
                        groups = new Group?[itemGroups.Length];
                        if (previousGroups is not null)
                            Array.Copy(previousGroups, groups, previousGroups.Length);
                    }
                    groups![index] = group;
                }
                if (!range.HasExpiration)
                    return null;
                return ReferenceEquals(groups, previousGroups) ? previousIndex : new ExpirationIndex(range, groups);
            }

            public sealed class Group(ExpirationRange range, ExpirationRange[] blocks)
            {
                public ExpirationRange Range { get; } = range;
                private ExpirationRange[] Blocks { get; } = blocks;

                public static Group? Update(Item[][] items, Item[][]? previousItems, Group? previous)
                {
                    var previousBlocks = previous?.Blocks;
                    var blocks = previousBlocks;
                    ExpirationRange range = default;
                    for (int index = 0; index < items.Length; index++)
                    {
                        var oldItems = previousItems is not null && index < previousItems.Length ? previousItems[index] : null;
                        var oldRange = previousBlocks is not null && index < previousBlocks.Length ? previousBlocks[index] : default;
                        var blockRange = oldRange;
                        if (!ReferenceEquals(items[index], oldItems))
                        {
                            blockRange = default;
                            if (items[index] is { } block)
                                foreach (ref readonly var item in block.AsSpan())
                                    if (item.Present && item.Expiration is { } expiration)
                                        blockRange = blockRange.Include(expiration);
                        }
                        range = range.Include(blockRange);
                        if (blockRange.Equals(oldRange))
                            continue;
                        if (ReferenceEquals(blocks, previousBlocks))
                        {
                            blocks = new ExpirationRange[items.Length];
                            if (previousBlocks is not null)
                                Array.Copy(previousBlocks, blocks, previousBlocks.Length);
                        }
                        blocks![index] = blockRange;
                    }
                    if (!range.HasExpiration)
                        return null;
                    return ReferenceEquals(blocks, previousBlocks) ? previous : new Group(range, blocks!);
                }
            }
        }

        private sealed class Pages<TValue> where TValue : struct
        {
            private readonly TValue[][][] _groups;

            private Pages(Pages<TValue>? source, int capacity)
            {
                int groups = (capacity + BlockSize * BlockSize - 1) / (BlockSize * BlockSize);
                _groups = new TValue[Math.Max(groups, source?._groups.Length ?? 0)][][];
                if (source is not null)
                    Array.Copy(source._groups, _groups, source._groups.Length);
            }

            public TValue this[int index] => _groups[index / (BlockSize * BlockSize)]?[index / BlockSize % BlockSize]?[index % BlockSize] ?? default;
            public TValue[][][] Groups => _groups;

            public IEnumerable<TValue[]> Blocks
            {
                get
                {
                    foreach (var group in _groups)
                        if (group is not null)
                            foreach (var block in group)
                                if (block is not null)
                                    yield return block;
                }
            }

            public sealed class Builder(Pages<TValue>? source, int capacity)
            {
                public Pages<TValue> Value { get; private set; } = source ?? new Pages<TValue>(null, capacity);

                public TValue this[int index]
                {
                    get => Value[index];
                    set => GetWritableBlock(index)[index % BlockSize] = value;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public TValue[] GetWritableBlock(int index)
                {
                    if (ReferenceEquals(Value, source))
                        Value = new Pages<TValue>(source, capacity);
                    int groupIndex = index / (BlockSize * BlockSize);
                    int blockIndex = index / BlockSize % BlockSize;
                    var originalGroup = source is not null && groupIndex < source._groups.Length ? source._groups[groupIndex] : null;
                    var group = Value._groups[groupIndex];
                    if (group is null || ReferenceEquals(group, originalGroup))
                    {
                        int blocks = Math.Min(BlockSize, (capacity + BlockSize - 1) / BlockSize - groupIndex * BlockSize);
                        var copy = new TValue[Math.Max(blocks, group?.Length ?? 0)][];
                        if (group is not null)
                            Array.Copy(group, copy, group.Length);
                        Value._groups[groupIndex] = group = copy;
                    }
                    var originalBlock = originalGroup is not null && blockIndex < originalGroup.Length ? originalGroup[blockIndex] : null;
                    var block = group[blockIndex];
                    if (block is null || ReferenceEquals(block, originalBlock))
                        group[blockIndex] = block = block is null ? new TValue[BlockSize] : (TValue[])block.Clone();
                    return block;
                }
            }
        }
    }
}
