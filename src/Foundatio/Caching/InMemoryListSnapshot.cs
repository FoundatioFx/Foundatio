using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace Foundatio.Caching;

internal interface IListSnapshot
{
    object GetSnapshot();
}

// Updates copy one bounded block and the block directory, not the entire list. The immutable
// key index shares unchanged nodes. Raw dictionary reads retain their existing public shape.
internal sealed class ListSnapshot<T> : IListSnapshot where T : notnull
{
    public const int BlockSize = 32;
    private ImmutableDictionary<T, int>? _indices;
    private readonly Dictionary<T, DateTime?>? _initialValues;
    private readonly Item[][] _blocks;
    private readonly ImmutableSortedDictionary<DateTime, int> _expirations;
    private readonly ImmutableStack<int> _freeIndices;
    private readonly int _nextIndex;
    private readonly int _permanentCount;
    private Dictionary<T, DateTime?>? _snapshot;

    private readonly record struct Item(T Key, DateTime? Expiration, bool Present);

    public ListSnapshot(Dictionary<T, DateTime?> values)
    {
        _initialValues = values;
        var expirations = ImmutableSortedDictionary.CreateBuilder<DateTime, int>();
        _blocks = new Item[(values.Count + BlockSize - 1) / BlockSize][];
        for (int i = 0; i < _blocks.Length; i++)
            _blocks[i] = new Item[BlockSize];
        foreach (var pair in values)
        {
            _blocks[_nextIndex / BlockSize][_nextIndex % BlockSize] = new Item(pair.Key, pair.Value, true);
            _nextIndex++;
            if (pair.Value is { } expiration)
                expirations[expiration] = expirations.GetValueOrDefault(expiration) + 1;
            else
                _permanentCount++;
        }
        _expirations = expirations.ToImmutable();
        _freeIndices = ImmutableStack<int>.Empty;
    }

    private ListSnapshot(ImmutableDictionary<T, int> indices, Item[][] blocks,
        ImmutableSortedDictionary<DateTime, int> expirations, ImmutableStack<int> freeIndices, int nextIndex, int permanentCount)
    {
        _indices = indices;
        _blocks = blocks;
        _expirations = expirations;
        _freeIndices = freeIndices;
        _nextIndex = nextIndex;
        _permanentCount = permanentCount;
    }

    public int Count => _indices?.Count ?? _initialValues!.Count;
    private ImmutableDictionary<T, int> Indices
    {
        get
        {
            if (Volatile.Read(ref _indices) is { } existing)
                return existing;

            var builder = ImmutableDictionary.CreateBuilder<T, int>(_initialValues!.Comparer);
            int index = 0;
            foreach (var key in _initialValues.Keys)
                builder.Add(key, index++);
            var indices = builder.ToImmutable();
            return Interlocked.CompareExchange(ref _indices, indices, null) ?? indices;
        }
    }

    public Dictionary<T, DateTime?>? Snapshot => Volatile.Read(ref _snapshot);
    public DateTime? ExpiresAt => _permanentCount > 0 || _expirations.IsEmpty ? null : _expirations.Last().Key;

    public T[] Read(DateTime utcNow)
    {
        var result = new T[Count];
        int count = 0;
        foreach (var block in _blocks)
            foreach (var item in block)
                if (item.Present && (item.Expiration is null || item.Expiration >= utcNow))
                    result[count++] = item.Key;
        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }

    public object GetSnapshot()
    {
        if (Snapshot is { } snapshot)
            return snapshot;

        if (_initialValues is not null)
            return Interlocked.CompareExchange(ref _snapshot, _initialValues, null) ?? _initialValues;

        var dictionary = CopyValues();
        return Interlocked.CompareExchange(ref _snapshot, dictionary, null) ?? dictionary;
    }

    private Dictionary<T, DateTime?> CopyValues()
    {
        if (_initialValues is not null)
            return new Dictionary<T, DateTime?>(_initialValues, _initialValues.Comparer);

        var dictionary = new Dictionary<T, DateTime?>(Count, Indices.KeyComparer);
        foreach (var block in _blocks)
            foreach (var item in block)
                if (item.Present)
                    dictionary.Add(item.Key, item.Expiration);
        return dictionary;
    }

    public ListSnapshot<T> Update(ICollection<T> items, DateTime? expiration, DateTime utcNow, bool remove, out long removed)
    {
        removed = 0;
        bool prune = !_expirations.IsEmpty && _expirations.First().Key < utcNow;
        if (remove && !prune && !items.Any(key => _initialValues?.ContainsKey(key) ?? Indices.ContainsKey(key)))
            return this;

        // When a batch could copy every block, one dictionary copy is cheaper than
        // rebuilding many index paths. It also avoids indexing short-lived bulk lists.
        if (items.Count >= Math.Max(1, Count / BlockSize))
        {
            var dictionary = CopyValues();
            if (prune)
                foreach (var key in dictionary.Where(pair => pair.Value < utcNow).Select(pair => pair.Key).ToArray())
                    dictionary.Remove(key);
            if (remove)
                removed = items.Count(dictionary.Remove);
            else
                foreach (var key in items)
                    dictionary[key] = expiration;
            return new ListSnapshot<T>(dictionary);
        }

        var indices = Indices.ToBuilder();
        int capacity = Math.Max(_nextIndex, Count + (remove ? 0 : items.Count));
        var blocks = new Item[(capacity + BlockSize - 1) / BlockSize][];
        Array.Copy(_blocks, blocks, _blocks.Length);
        var copied = new bool[blocks.Length];
        ImmutableSortedDictionary<DateTime, int>.Builder? expirations = null;
        var freeIndices = _freeIndices;
        int nextIndex = _nextIndex;
        int permanentCount = _permanentCount;
        bool changed = false;

        if (prune)
            foreach (var block in _blocks)
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
            bool exists = indices.TryGetValue(key, out int index);
            if (exists)
            {
                var previous = blocks[index / BlockSize][index % BlockSize];
                if (previous.Expiration == expiration)
                    continue;
                key = previous.Key;
                AdjustExpiration(previous.Expiration, -1);
            }
            else if (!freeIndices.IsEmpty)
            {
                index = freeIndices.Peek();
                freeIndices = freeIndices.Pop();
            }
            else
                index = nextIndex++;

            CopyBlock(index);
            blocks[index / BlockSize][index % BlockSize] = new Item(key, expiration, true);
            indices[key] = index;
            AdjustExpiration(expiration, 1);
        }

        if (!changed)
            return this;

        int blockCount = (nextIndex + BlockSize - 1) / BlockSize;
        if (blocks.Length != blockCount)
            Array.Resize(ref blocks, blockCount);
        return new ListSnapshot<T>(indices.ToImmutable(), blocks, expirations?.ToImmutable() ?? _expirations, freeIndices, nextIndex, permanentCount);

        bool Remove(T key)
        {
            if (!indices.TryGetValue(key, out int index))
                return false;
            CopyBlock(index);
            AdjustExpiration(blocks[index / BlockSize][index % BlockSize].Expiration, -1);
            blocks[index / BlockSize][index % BlockSize] = default;
            indices.Remove(key);
            freeIndices = freeIndices.Push(index);
            return true;
        }

        void CopyBlock(int index)
        {
            int blockIndex = index / BlockSize;
            if (copied[blockIndex])
                return;
            blocks[blockIndex] = blockIndex < _blocks.Length ? (Item[])_blocks[blockIndex].Clone() : new Item[BlockSize];
            copied[blockIndex] = true;
            changed = true;
        }

        void AdjustExpiration(DateTime? value, int delta)
        {
            if (value is not { } time)
            {
                permanentCount += delta;
                return;
            }
            expirations ??= _expirations.ToBuilder();
            int count = expirations.GetValueOrDefault(time) + delta;
            if (count == 0)
                expirations.Remove(time);
            else
                expirations[time] = count;
        }
    }
}
