using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Caching;

public partial class InMemoryCacheClient
{
    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await ListRemoveAsync(key, values).AnyContext();
            return 0;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime? expiresAt = expiresIn.HasValue ? utcNow.SafeAdd(expiresIn.Value) : null;

        // A string argument binds T to char; it is one member, as it has always been.
        long added = values is string stringValue
            ? UpdateList(key, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stringValue }, expiresAt, remove: false)
            : UpdateList(key, GetDistinctListValues(values), expiresAt, remove: false);

        if (added > 0)
            await StartMaintenanceAsync().AnyContext();

        return added;
    }

    public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        Interlocked.Increment(ref _writes);

        long removed = values is string stringValue
            ? UpdateList(key, new HashSet<string>([stringValue]), null, remove: true)
            : UpdateList(key, GetDistinctListValues(values), null, remove: true);

        if (removed > 0 && _logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Removed value from set with cache key: {Key}", key);

        return Task.FromResult(removed);
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        if (_memory.TryGetValue(key, out var entry) && !entry.IsExpired && entry.StoredValue is CacheList<T> list)
        {
            Interlocked.Increment(ref _hits);
            entry.RecordAccess();
            var result = list.Read(_timeProvider.GetUtcNow().UtcDateTime, page, pageSize);
            if (ShouldCloneListMembers<T>())
            {
                var members = (T[])result.Value;
                for (int index = 0; index < members.Length; index++)
                    members[index] = members[index].DeepClone()!;
            }

            return result;
        }

        // A dictionary stored directly with SetAsync is readable as a list until a list update takes it over. Published
        // payloads are never changed by the cache, so safe keys can be read without cloning the whole dictionary.
        if (entry is { IsExpired: false, StoredValue: Dictionary<T, DateTime?> stored } && stored.GetType() == typeof(Dictionary<T, DateTime?>)
            && (!_shouldClone || FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T))))
        {
            Interlocked.Increment(ref _hits);
            entry.RecordAccess();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (page.HasValue)
                return ReadListPage(stored, now, page.Value, pageSize);

            var keys = ReadListValues(stored, now);
            return new CacheValue<ICollection<T>>(keys, keys.Length > 0);
        }

        var dictionaryCacheValue = await GetAsync<IDictionary<T, DateTime?>>(key).AnyContext();
        if (!dictionaryCacheValue.HasValue)
            return new CacheValue<ICollection<T>>([], false);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var nonExpiredKeys = dictionaryCacheValue.Value.Where(kvp => kvp.Value is null || kvp.Value >= utcNow).Select(kvp => kvp.Key).ToArray();
        if (nonExpiredKeys.Length is 0)
            return new CacheValue<ICollection<T>>([], false);

        if (!page.HasValue)
            return new CacheValue<ICollection<T>>(nonExpiredKeys, true);

        int skip = (page.Value - 1) * pageSize;
        return new CacheValue<ICollection<T>>(nonExpiredKeys.Skip(skip).Take(pageSize).ToArray(), true);
    }

    /// <summary>
    /// Adds or removes list members. Writers to the same list take turns on the list's lock and publish the resized
    /// entry before releasing it, so they never redo work for each other. A publish only fails when another kind of
    /// write replaced the entry; the change is then rolled back and retried against the new entry.
    /// </summary>
    private long UpdateList<T>(string key, HashSet<T> items, DateTime? expiresAt, bool remove) where T : notnull
    {
        if (items.Count is 0)
            return 0;

        Dictionary<T, DateTime?>? input = null;
        long inputSize = 0;
        if (!remove)
        {
            // Added values are sized on their own first, as they always have been, so a batch that is too large by
            // itself is rejected before it is merged. Like any other cached value, they are cloned after sizing.
            if (_hasSizeCalculator)
            {
                input = new Dictionary<T, DateTime?>(items.Count, items.Comparer);
                foreach (var item in items)
                    input[item] = expiresAt;

                inputSize = CalculateEntrySize(input);
                if (inputSize < 0)
                    return 0;
            }

            if (ShouldCloneListMembers<T>())
            {
                items = CloneListMembers(items);
                input = null;
            }

            Interlocked.Increment(ref _writes);
        }

        while (true)
        {
            _memory.TryGetValue(key, out var stored);
            bool reclaimExpired = stored is { IsExpired: true };
            var current = reclaimExpired ? null : stored;

            if (current?.StoredValue is CacheList<T> list)
            {
                lock (list.SyncRoot)
                {
                    if (_memory.TryGetValue(key, out var latest) && ReferenceEquals(latest, current) && !current.IsExpired
                        && TryUpdateList(key, current, list, items, expiresAt, remove, out long count))
                        return count;
                }

                continue;
            }

            if (current is not null && current.StoredValue is not IDictionary<T, DateTime?>)
            {
                if (remove)
                    return 0;
                throw new InvalidOperationException($"Unable to add value for key: {key}. Cache value does not contain a set");
            }

            if (current is null && remove)
            {
                if (!reclaimExpired)
                    return 0;
                if (!TryPublish(key, stored, null))
                    continue;

                OnItemExpired(key);
                return 0;
            }

            if (!TryCreateList(key, stored, current, items, expiresAt, remove, input, inputSize, out long result))
                continue;

            if (reclaimExpired)
                OnItemExpired(key);
            return result;
        }
    }

    /// <summary>
    /// Starts a list for a missing key, or takes over a dictionary stored with <c>SetAsync</c>. Nothing else can see
    /// the new list until it is published, so it needs no lock and no rollback. Returns <c>false</c> when another
    /// writer changed the key first.
    /// </summary>
    private bool TryCreateList<T>(string key, CacheEntry? stored, CacheEntry? current, HashSet<T> items, DateTime? expiresAt, bool remove, Dictionary<T, DateTime?>? input, long inputSize, out long count) where T : notnull
    {
        CacheList<T> list;
        long size;
        if (current is null)
        {
            list = input is not null ? CacheList<T>.Create(input) : CacheList<T>.Create(items, expiresAt);
            count = items.Count;
            size = inputSize;
        }
        else
        {
            list = CacheList<T>.Adopt((IDictionary<T, DateTime?>)current.StoredValue!);
            count = list.Apply(items, expiresAt, _timeProvider.GetUtcNow().UtcDateTime, remove, out bool changed, out int expired);
            list.Commit();
            LogExpiredListValues(key, expired);
            if (!changed)
            {
                if (remove)
                    count = 0;
                return true;
            }

            size = list.Count > 0 ? CalculateListSize(list) : 0;
            if (current.IsExpired)
                return false;
        }

        if (size < 0 || !remove && IsInputExpired(expiresAt))
        {
            count = 0;
            // Still reclaim an expired entry this write found, so the caller's expiration notice is accurate.
            return ReferenceEquals(stored, current) || TryPublish(key, stored, null);
        }

        CacheEntry? desired = list.Count is 0 ? null
            : current is null ? new CacheEntry(list, list.ExpiresAt, _timeProvider, _shouldClone, size, valueIsOwned: true)
            : current.WithValue(list, list.ExpiresAt, size);
        return TryPublish(key, stored, desired);
    }

    /// <summary>
    /// Updates a published list while holding its lock. Returns <c>false</c> when the entry was replaced before the
    /// update could be published, after undoing the change.
    /// </summary>
    private bool TryUpdateList<T>(string key, CacheEntry current, CacheList<T> list, HashSet<T> items, DateTime? expiresAt, bool remove, out long count) where T : notnull
    {
        count = list.Apply(items, expiresAt, _timeProvider.GetUtcNow().UtcDateTime, remove, out bool changed, out int expired);
        if (!changed)
        {
            list.Commit();
            if (remove)
                count = 0;
            return true;
        }

        CacheEntry? desired = null;
        if (list.Count > 0)
        {
            long size;
            try
            {
                size = CalculateListSize(list);
            }
            catch
            {
                list.Rollback();
                throw;
            }

            if (size < 0 || !remove && IsInputExpired(expiresAt))
            {
                list.Rollback();
                count = 0;
                return true;
            }

            // Sizing can outlive the entry; start over from a missing key rather than revive it.
            if (current.IsExpired)
            {
                list.Rollback();
                return false;
            }

            desired = current.WithValue(list, list.ExpiresAt, size);
        }

        if (!TryPublish(key, current, desired))
        {
            list.Rollback();
            return false;
        }

        list.Commit();
        LogExpiredListValues(key, expired);
        return true;
    }

    /// <summary>
    /// Sizes the live members. With <see cref="InMemoryCacheClientOptions.CloneValues"/> the calculator has always
    /// received a private copy, so it is still given one and cannot change or keep the stored list.
    /// </summary>
    private long CalculateListSize<T>(CacheList<T> list) where T : notnull
    {
        if (!_hasSizeCalculator)
            return 0;

        object values = _shouldClone
            ? CacheEntry.CloneValue(list.Values, CacheEntry.RequiresDictionaryKeyCloning(list.DictionaryType))!
            : list.Values;
        return CalculateEntrySize(values);
    }

    private bool IsInputExpired(DateTime? expiresAt) => expiresAt < _timeProvider.GetUtcNow().UtcDateTime;

    private void LogExpiredListValues(string key, int expired)
    {
        if (expired > 0 && _logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Removed {ExpiredValues} expired values for key: {Key}", expired, key);
    }

    private static HashSet<T> GetDistinctListValues<T>(IEnumerable<T> values) where T : notnull
    {
        bool canBeNull = default(T) is null;
        // Copying a set with the same comparer reuses its buckets without hashing, so it is fast and runs no callbacks.
        if (values is HashSet<T> set && set.GetType() == typeof(HashSet<T>)
            && ReferenceEquals(set.Comparer, EqualityComparer<T>.Default))
        {
            var copy = new HashSet<T>(set, set.Comparer);
            if (canBeNull)
                copy.Remove(default!);
            return copy;
        }

        var items = new HashSet<T>();
        foreach (var value in values)
        {
            if (!canBeNull || value is not null)
                items.Add(value);
        }

        return items;
    }

    /// <summary>
    /// List storage is private, so <see cref="InMemoryCacheClientOptions.CloneValues"/> only needs to copy mutable
    /// members: once when they are added and once when they are read.
    /// </summary>
    private bool ShouldCloneListMembers<T>() => _shouldClone && !FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T));

    private static HashSet<T> CloneListMembers<T>(HashSet<T> items) where T : notnull
    {
        var clones = new HashSet<T>(items.Count, items.Comparer);
        foreach (var item in items)
            clones.Add(item.DeepClone()!);
        return clones;
    }

    private static CacheValue<ICollection<T>> ReadListPage<T>(Dictionary<T, DateTime?> dictionary, DateTime utcNow, int page, int pageSize, bool checkExpiration = true) where T : notnull
    {
        var result = new ListPage<T>(dictionary.Count, page, pageSize);
        foreach (var pair in dictionary)
        {
            if (checkExpiration && pair.Value < utcNow)
                continue;
            if (result.TrySkip())
                continue;
            result.Add(pair.Key);
            if (result.IsFull)
                break;
        }
        return result.ToCacheValue();
    }

    private struct ListPage<T> where T : notnull
    {
        private int _skip;
        private readonly int _initialSkip;
        private readonly int _capacity;
        private T[]? _values;
        private int _count;
        private bool _hasValue;

        public ListPage(int count, int page, int pageSize)
        {
            _skip = Math.Max(0, unchecked((page - 1) * pageSize));
            _initialSkip = _skip;
            _capacity = Math.Min(Math.Max(0, pageSize), Math.Max(0, count - _skip));
        }

        public bool IsFull => _hasValue && _count == _capacity;
        public bool CanSkip(int count) => _capacity > 0 && _skip >= count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySkip(int count = 1)
        {
            if (!CanSkip(count))
                return false;

            _skip -= count;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T value)
        {
            _hasValue = true;
            if (_capacity is 0)
                return;

            _values ??= new T[_capacity];
            _values[_count++] = value;
        }

        public CacheValue<ICollection<T>> ToCacheValue()
        {
            var values = _values ?? [];
            if (_count != values.Length)
                Array.Resize(ref values, _count);
            return new CacheValue<ICollection<T>>(values, _hasValue || _skip < _initialSkip);
        }
    }

    private static T[] ReadListValues<T>(Dictionary<T, DateTime?> dictionary, DateTime utcNow, bool checkExpiration = true) where T : notnull
    {
        var result = new T[dictionary.Count];
        int count = 0;
        if (checkExpiration)
        {
            foreach (var pair in dictionary)
                if (pair.Value is null || pair.Value >= utcNow)
                    result[count++] = pair.Key;
        }
        else
        {
            dictionary.Keys.CopyTo(result, 0);
            count = result.Length;
        }
        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }
}
