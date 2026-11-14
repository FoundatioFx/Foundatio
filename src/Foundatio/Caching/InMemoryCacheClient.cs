using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Resilience;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching;

public class InMemoryCacheClient : IMemoryCacheClient, IHaveTimeProvider, IHaveLogger, IHaveLoggerFactory, IHaveResiliencePolicyProvider
{
    private readonly ConcurrentDictionary<string, CacheEntry> _memory;
    private readonly bool _shouldClone;
    private readonly bool _shouldThrowOnSerializationErrors;
    private readonly int? _maxItems;
    private readonly long? _maxMemorySize;
    private readonly bool _hasSizeCalculator;
    private readonly bool _usesBuiltInDynamicSizing;
    private readonly bool _canShareListValues;
    private readonly bool _shouldIsolateListSizing;
    private readonly bool _shouldTrackMemory;
    private Func<object, long>? _sizeCalculator;
    private readonly long? _maxEntrySize;
    private readonly bool _shouldThrowOnMaxEntrySizeExceeded;
    private long _writes;
    private long _hits;
    private long _misses;
    private long _currentMemorySize;
    private readonly TimeProvider _timeProvider;
    private readonly IResiliencePolicyProvider _resiliencePolicyProvider;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AsyncLock _lock = new();
    private readonly CancellationTokenSource _disposedCancellationTokenSource = new();
    // Cached so maintenance racing Dispose never reads Token from a disposed source
    private readonly CancellationToken _disposedCancellationToken;
    private bool _isDisposed;

    public InMemoryCacheClient() : this(o => o)
    {
    }

    public InMemoryCacheClient(InMemoryCacheClientOptions? options = null)
    {
        _disposedCancellationToken = _disposedCancellationTokenSource.Token;
        if (options is null)
            options = new InMemoryCacheClientOptions();
        _shouldClone = options.CloneValues;
        _shouldThrowOnSerializationErrors = options.ShouldThrowOnSerializationError;
        _maxItems = options.MaxItems;
        _maxMemorySize = options.MaxMemorySize;
        _maxEntrySize = options.MaxEntrySize;
        _shouldThrowOnMaxEntrySizeExceeded = options.ShouldThrowOnMaxEntrySizeExceeded;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _resiliencePolicyProvider = options.ResiliencePolicyProvider ?? DefaultResiliencePolicyProvider.Instance;
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<InMemoryCacheClient>();

        if (options.MaxMemorySize.HasValue && options.SizeCalculator is null)
            throw new ArgumentException($"{nameof(options.MaxMemorySize)} requires a {nameof(options.SizeCalculator)}. Use WithDynamicSizing() or WithFixedSizing() builder methods.", nameof(options));

        if (options.MaxEntrySize.HasValue && options.SizeCalculator is null)
            throw new ArgumentException($"{nameof(options.MaxEntrySize)} requires a {nameof(options.SizeCalculator)}. Use WithDynamicSizing() or WithFixedSizing() builder methods.", nameof(options));

        if (options.MaxEntrySize.HasValue && options.MaxMemorySize.HasValue && options.MaxEntrySize.Value > options.MaxMemorySize.Value)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(options.MaxEntrySize)} ({options.MaxEntrySize.Value:N0} bytes) cannot be greater than {nameof(options.MaxMemorySize)} ({options.MaxMemorySize.Value:N0} bytes).");

        _sizeCalculator = options.SizeCalculator;
        _hasSizeCalculator = _sizeCalculator is not null;
        _usesBuiltInDynamicSizing = _sizeCalculator?.Target is SizeCalculator calculator && _sizeCalculator == calculator.CalculateSize;
        bool usesBuiltInFixedSizing = _sizeCalculator?.Target is InMemoryCacheClientOptionsBuilder.FixedSizeCalculator fixedCalculator && _sizeCalculator == fixedCalculator.Calculate;
        _canShareListValues = !_shouldClone && (!_hasSizeCalculator || _usesBuiltInDynamicSizing || usesBuiltInFixedSizing);
        _shouldIsolateListSizing = _shouldClone && _hasSizeCalculator && !usesBuiltInFixedSizing;
        _shouldTrackMemory = _hasSizeCalculator && _maxMemorySize.HasValue;

        _memory = new ConcurrentDictionary<string, CacheEntry>();
    }

    public InMemoryCacheClient(Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> config)
        : this(config(new InMemoryCacheClientOptionsBuilder()).Build())
    {
    }

    public int Count => _memory.Count(i => !i.Value.IsExpired);
    public int? MaxItems => _maxItems;
    public long? MaxMemorySize => _maxMemorySize;
    public long CurrentMemorySize => _currentMemorySize;

    /// <summary>
    /// Safely updates the current memory size, ensuring it never goes negative and handles overflow.
    /// </summary>
    /// <remarks>
    /// This method is a no-op when memory tracking is disabled or when the delta is zero.
    /// </remarks>
    private void UpdateMemorySize(long delta)
    {
        if (!_shouldTrackMemory || delta == 0)
            return;

        long currentValue;
        long newValue;

        if (delta < 0)
        {
            // For negative deltas (removals), ensure we don't go below zero
            do
            {
                currentValue = _currentMemorySize;
                newValue = Math.Max(0, currentValue + delta);
            } while (Interlocked.CompareExchange(ref _currentMemorySize, newValue, currentValue) != currentValue);
        }
        else
        {
            // For positive deltas (additions), check for overflow and clamp to long.MaxValue
            bool overflowed = false;
            do
            {
                currentValue = _currentMemorySize;
                if (currentValue > Int64.MaxValue - delta)
                {
                    newValue = Int64.MaxValue;
                    overflowed = true;
                }
                else
                {
                    newValue = currentValue + delta;
                }
            } while (Interlocked.CompareExchange(ref _currentMemorySize, newValue, currentValue) != currentValue);

            if (overflowed)
            {
                _logger.LogWarning("Memory size counter would overflow. Clamping to {MaxValue}. Current={Current}, Delta={Delta}",
                    Int64.MaxValue, currentValue, delta);
            }
        }

        _logger.LogTrace("UpdateMemorySize: Delta={Delta}, Before={Before}, After={After}, Max={Max}",
            delta, currentValue, newValue, _maxMemorySize);
    }

    /// <summary>
    /// Recalculates the current memory size by summing all stored cache entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Note: This method iterates over the cache dictionary which may be modified concurrently.
    /// The calculated total may be temporarily inaccurate if entries are added/removed during
    /// iteration, but this is acceptable for memory tracking purposes and will self-correct
    /// on subsequent operations or recalculations.
    /// </para>
    /// <para>
    /// Expired entries that are still stored are counted, because removing them later subtracts their size again.
    /// </para>
    /// </remarks>
    internal long RecalculateMemorySize()
    {
        if (!_shouldTrackMemory)
            return 0;

        // Take a snapshot of values to reduce (but not eliminate) race condition window
        var entries = _memory.Values.ToArray();
        long totalSize = 0;
        foreach (var entry in entries)
            totalSize += entry.Size;

        Interlocked.Exchange(ref _currentMemorySize, totalSize);
        return totalSize;
    }

    public long Calls => _writes + _hits + _misses;
    public long Writes => _writes;
    public long Reads => _hits + _misses;
    public long Hits => _hits;
    public long Misses => _misses;

    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    public override string ToString()
    {
        string result = $"Count: {Count} Calls: {Calls} Reads: {Reads} Writes: {Writes} Hits: {Hits} Misses: {Misses}";
        if (_maxMemorySize.HasValue)
            result += $" Memory: {CurrentMemorySize:N0}/{MaxMemorySize:N0} bytes";

        return result;
    }

    public void ResetStats()
    {
        _writes = 0;
        _hits = 0;
        _misses = 0;
    }

    public AsyncEvent<ItemExpiredEventArgs> ItemExpired { get; } = new();

    private void OnItemExpired(string key, bool sendNotification = true)
    {
        if (!ItemExpired.HasHandlers)
            return;

        Task.Factory.StartNew(_ =>
        {
            var args = new ItemExpiredEventArgs
            {
                Client = this,
                Key = key,
                SendNotification = sendNotification
            };

            return ItemExpired.InvokeAsync(this, args);
        }, this, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    public ICollection<string> Keys
    {
        get
        {
            return _memory.ToArray()
                .Where(kvp => !kvp.Value.IsExpired)
                .OrderBy(kvp => kvp.Value.LastAccessTicks)
                .ThenBy(kvp => kvp.Value.InstanceNumber)
                .Select(kvp => kvp.Key)
                .ToList();
        }
    }

    public ICollection<KeyValuePair<string, object?>> Items
    {
        get
        {
            return _memory.ToArray()
                .Where(kvp => !kvp.Value.IsExpired)
                .OrderBy(kvp => kvp.Value.LastAccessTicks)
                .ThenBy(kvp => kvp.Value.InstanceNumber)
                .Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value.PeekValue()))
                .ToList();
        }
    }

    public Task<bool> RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _logger.LogTrace("RemoveAsync: Removing key: {Key}", key);
        var removed = UpdateStoredEntry<CacheEntry?>(key, current => (null, current));

        // Return false if the entry was expired (consistent with Redis behavior)
        return Task.FromResult(removed is { IsExpired: false });
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        bool success = UpdateLease<bool>(key, current => EqualityComparer<T>.Default.Equals(current.GetValue<T>(), expected)
            ? (null, true)
            : null, missResult: false);

        await StartMaintenanceAsync().AnyContext();

        _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);
        return success;
    }

    public Task<int> RemoveAllAsync(IEnumerable<string>? keys = null)
    {
        if (keys is null)
        {
            int count = _memory.Count;
            _memory.Clear();
            if (_shouldTrackMemory)
                Interlocked.Exchange(ref _currentMemorySize, 0);

            return Task.FromResult(count);
        }

        int removed = 0;
        foreach (string key in keys.Distinct())
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));

            _logger.LogTrace("RemoveAllAsync: Removing key: {Key}", key);
            if (UpdateStoredEntry<bool>(key, current => (null, current is not null)))
                removed++;
        }

        return Task.FromResult(removed);
    }

    public Task<int> RemoveByPrefixAsync(string prefix)
    {
        if (String.IsNullOrEmpty(prefix))
            return RemoveAllAsync();

        var keys = _memory.Keys.ToList();
        var keysToRemove = new List<string>(keys.Count);

        try
        {
            var regex = new Regex(String.Concat("^", Regex.Escape(prefix), ".*?$"), RegexOptions.Singleline);
            foreach (string key in keys)
                if (regex.IsMatch(key))
                    keysToRemove.Add(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing items from cache prefix: {Prefix}", prefix);
            throw new CacheException($"Error removing items from cache prefix: {prefix}", ex);
        }

        return RemoveAllAsync(keysToRemove);
    }

    /// <summary>
    /// Removes cache entry from expires in argument value.
    /// </summary>
    internal long RemoveExpiredKey(string key, bool sendNotification = true)
    {
        // Consideration: We could reduce the amount of calls to this by updating ExpiresAt and only having maintenance remove keys.
        ArgumentException.ThrowIfNullOrEmpty(key);

        _logger.LogTrace("Removing expired key: {Key}", key);
        if (!UpdateStoredEntry<bool>(key, current => (null, current is not null)))
            return 0;

        OnItemExpired(key, sendNotification);
        return 1;
    }

    /// <summary>
    /// Atomically moves the entry for <paramref name="key"/> to the state chosen by <paramref name="update"/> and
    /// keeps the tracked memory size in step. An expired entry is treated as missing, the same as a read: the
    /// callback receives <c>null</c>, and when the expired entry is replaced or removed, <see cref="ItemExpired"/>
    /// is raised for it.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="update">
    /// Receives the current live entry (<c>null</c> when the key is missing or expired) and returns the entry the
    /// key should hold: the same instance to leave it unchanged, a new instance to add or replace it, or
    /// <c>null</c> to remove it. It runs again when the entry expires during the callback or another writer changes the key first,
    /// so it must not have side effects; only the result of the attempt that was published is returned.
    /// </param>
    internal TResult UpdateEntry<TResult>(string key, Func<CacheEntry?, (CacheEntry? Entry, TResult Result)> update)
    {
        bool reclaimedExpired = false;
        var result = UpdateStoredEntry(key, stored =>
        {
            reclaimedExpired = stored is { IsExpired: true };
            var decision = update(reclaimedExpired ? null : stored);
            if (!reclaimedExpired && stored is { IsExpired: true })
            {
                // Sizing, cloning or conversion can outlive the observed entry. Recompute from a missing key
                // instead of publishing a decision that would revive its expired value.
                reclaimedExpired = true;
                return update(null);
            }

            return decision;
        });

        if (reclaimedExpired)
            OnItemExpired(key);

        return result;
    }

    /// <summary>
    /// Like <see cref="UpdateEntry{TResult}"/>, but the callback sees the stored entry even when it has expired, and
    /// no event is raised. Only maintenance, compaction and explicit removal need this view.
    /// </summary>
    private TResult UpdateStoredEntry<TResult>(string key, Func<CacheEntry?, (CacheEntry? Entry, TResult Result)> update)
    {
        // Retries only when another writer changed the key first, so some writer always makes progress; Dispose clears
        // the store, which lets in-flight updates finish on the next attempt.
        while (true)
        {
            _memory.TryGetValue(key, out var current);
            var (desired, result) = update(current);
            if (ReferenceEquals(desired, current) || TryPublish(key, current, desired))
                return result;
        }
    }

    /// <summary>
    /// Reclaims a lease whose entry expired while the caller was comparing it, raising <see cref="ItemExpired"/>
    /// only when the reclaiming attempt is the one that was published.
    /// </summary>
    private TResult UpdateLease<TResult>(string key, Func<CacheEntry, (CacheEntry? Entry, TResult Result)?> update, TResult missResult)
    {
        bool reclaimed = false;
        var result = UpdateEntry(key, current =>
        {
            reclaimed = false;
            if (current is null)
                return (current, missResult);

            var decision = update(current);

            // A lease that expired while it was being compared no longer belongs to the caller: reclaim it
            if (current.IsExpired)
            {
                reclaimed = true;
                return (null, missResult);
            }

            return decision ?? (current, missResult);
        });

        if (reclaimed)
            OnItemExpired(key);

        return result;
    }

    /// <summary>
    /// Publishes <paramref name="desired"/> in place of <paramref name="current"/> with a compare-and-swap, adjusting
    /// tracked memory by the size difference. Returns <c>false</c>, changing nothing, if another writer got there first.
    /// </summary>
    private bool TryPublish(string key, CacheEntry? current, CacheEntry? desired)
    {
        bool published = (current, desired) switch
        {
            (null, not null) => _memory.TryAdd(key, desired),
            (not null, null) => _memory.TryRemove(new KeyValuePair<string, CacheEntry>(key, current)),
            _ => _memory.TryUpdate(key, desired!, current!)
        };

        if (published)
            UpdateMemorySize((desired?.Size ?? 0) - (current?.Size ?? 0));

        return published;
    }

    /// <summary>
    /// Removes the entry for <paramref name="key"/> only if it is still the exact instance that was observed,
    /// so an entry replaced concurrently is never removed by mistake.
    /// </summary>
    internal bool TryRemoveEntry(string key, CacheEntry observed)
    {
        return UpdateStoredEntry(key, current => ReferenceEquals(current, observed) ? (null, true) : (current, false));
    }

    public Task<CacheValue<T>> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!_memory.TryGetValue(key, out var existingEntry))
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult(CacheValue<T>.NoValue);
        }

        if (existingEntry.IsExpired)
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult(CacheValue<T>.NoValue);
        }

        Interlocked.Increment(ref _hits);

        try
        {
            var value = existingEntry.GetValue<T>();
            return Task.FromResult(new CacheValue<T>(value, true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to deserialize value {Value} to type {TypeFullName}", existingEntry.Value, typeof(T).FullName);

            if (_shouldThrowOnSerializationErrors)
                throw;

            return Task.FromResult(CacheValue<T>.NoValue);
        }
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var map = new Dictionary<string, CacheValue<T>>();
        foreach (string key in keys)
            map[key] = await GetAsync<T>(key);

        return map;
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        return WriteAsync(key, value, expiresIn, WriteMode.Add);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        return WriteAsync(key, value, expiresIn, WriteMode.Set);
    }

    private Task<bool> WriteAsync<T>(string key, T value, TimeSpan? expiresIn, WriteMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            RemoveExpiredKey(key);
            return Task.FromResult(false);
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var entry = CreateEntry(value, expiresAt);
        if (entry is null)
            return Task.FromResult(false); // Entry exceeds limits

        return SetInternalAsync(key, entry, mode);
    }

    private enum WriteMode
    {
        /// <summary>Always write the value.</summary>
        Set,
        /// <summary>Write only when the key is missing or expired.</summary>
        Add,
        /// <summary>Write only when the key holds a live value.</summary>
        Replace
    }

    public Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        return SetIfAsync(key, value, expiresIn, higher: true);
    }

    public Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        return SetIfAsync(key, value, expiresIn, higher: true);
    }

    public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        return SetIfAsync(key, value, expiresIn, higher: false);
    }

    public Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        return SetIfAsync(key, value, expiresIn, higher: false);
    }

    private async Task<T> SetIfAsync<T>(string key, T value, TimeSpan? expiresIn, bool higher) where T : struct, INumber<T>
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            RemoveExpiredKey(key);
            return T.Zero;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(value, expiresAt);
        if (newEntry is null)
            return T.Zero;

        Interlocked.Increment(ref _writes);

        T difference = UpdateEntry(key, current =>
        {
            if (newEntry.IsExpired)
                return (current, T.Zero);

            if (current is null)
                return (newEntry, value);

            if (GetNumericValue<T>(current) is { } currentValue && (higher ? currentValue < value : currentValue > value))
            {
                var replacement = current.WithValue(value, expiresAt, newEntry.Size);
                return replacement.IsExpired ? (current, T.Zero) : (replacement, higher ? value - currentValue : currentValue - value);
            }

            var unchanged = current.WithExpiration(expiresAt);
            return (unchanged.IsExpired ? current : unchanged, T.Zero);
        });

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    private T? GetNumericValue<T>(CacheEntry entry) where T : struct
    {
        try
        {
            return entry.GetValue<T?>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read cached value as {ValueType}: {Message}", typeof(T).Name, ex.Message);
            return null;
        }
    }

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

        long added = values is string stringValue
            ? ListAddPrepared<string>(key, new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase) { { stringValue, expiresAt } }, expiresAt)
            : ListAdd(key, GetDistinctListValues(values), expiresAt);

        if (added > 0)
            await StartMaintenanceAsync().AnyContext();

        return added;
    }

    private static HashSet<T> GetDistinctListValues<T>(IEnumerable<T> values) where T : notnull
    {
        var items = new HashSet<T>();
        bool canBeNull = default(T) is null;
        foreach (var value in values)
            if (!canBeNull || value is not null)
                items.Add(value);
        return items;
    }

    private long ListAdd<T>(string key, HashSet<T> items, DateTime? expiresAt) where T : notnull
    {
        if (items.Count is 0)
            return 0;

        if (_canShareListValues && items.Count >= ListSnapshot<T>.BlockSize * ListSnapshot<T>.BlockSize)
            return ListAddPrepared<T>(key, new ListSnapshot<T>(items, expiresAt, items.Comparer), expiresAt, items);

        var values = new Dictionary<T, DateTime?>(items.Count, items.Comparer);
        foreach (var item in items)
            values.Add(item, expiresAt);
        return ListAddPrepared<T>(key, values, expiresAt);
    }

    private long ListAddPrepared<T>(string key, ICollection values, DateTime? expiresAt, HashSet<T>? sharedItems = null) where T : notnull
    {
        bool valueIsOwned = _shouldClone && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T))
            && (!_shouldIsolateListSizing || _usesBuiltInDynamicSizing);
        var entry = CreateEntry(values, expiresAt, valueIsOwned);
        if (entry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        return UpdateEntry(key, current =>
        {
            if (entry.IsExpired)
                return (current, 0L);

            if (current is null)
            {
                var replacement = _canShareListValues && values is Dictionary<T, DateTime?> initial && initial.Count > ListSnapshot<T>.BlockSize
                    ? entry.WithValue(new ListSnapshot<T>(initial), expiresAt, entry.Size)
                    : entry;
                return entry.IsExpired ? (current, 0L) : (replacement, (long)values.Count);
            }

            if (current.StoredValue is ListSnapshot<T> { Snapshot: null } stored)
            {
                ICollection<T> keys = sharedItems is not null ? sharedItems : ((Dictionary<T, DateTime?>)entry.StoredValue!).Keys;
                var list = stored.Update(keys, expiresAt, _timeProvider.GetUtcNow().UtcDateTime, remove: false, out _);
                long listSize = _usesBuiltInDynamicSizing ? CalculateEntrySize(list) : entry.Size;
                if (listSize < 0)
                    return (current, 0L);

                var replacement = current.WithValue(list, list.ExpiresAt, listSize);
                return entry.IsExpired ? (current, 0L) : (replacement, (long)values.Count);
            }

            if (CopyListValues<T>(current) is not { } dictionary)
                throw new InvalidOperationException($"Unable to add value for key: {key}. Cache value does not contain a set");

            ExpireListValues(dictionary, key);
            if (sharedItems is { } addedKeys)
            {
                EnsureListCapacity(dictionary, addedKeys, addedKeys.Comparer);
                foreach (var item in addedKeys)
                    dictionary[item] = expiresAt;
            }
            else
            {
                // Merge values after sizing and cloning, which can change the input dictionary.
                var addedValues = (Dictionary<T, DateTime?>)entry.StoredValue!;
                EnsureListCapacity(dictionary, addedValues.Keys, addedValues.Comparer);
                foreach (var kvp in addedValues)
                    dictionary[kvp.Key] = kvp.Value;
            }

            long size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;
            if (size < 0)
                return (current, 0L);

            var merged = WithListValues(current, dictionary, size);
            return entry.IsExpired ? (current, 0L) : (merged, (long)values.Count);
        });
    }

    private CacheEntry WithListValues<T>(CacheEntry current, IDictionary<T, DateTime?> dictionary, long size) where T : notnull
    {
        if (_shouldIsolateListSizing && !_usesBuiltInDynamicSizing && ShouldCloneListComparer<T>(dictionary))
            return current.WithValue(CloneDictionaryWithComparer(dictionary), GetListExpiration(dictionary), size);

        object value = _canShareListValues && dictionary is Dictionary<T, DateTime?> values && values.Count > ListSnapshot<T>.BlockSize
            ? new ListSnapshot<T>(values)
            : dictionary;
        bool clone = _shouldIsolateListSizing && (!_usesBuiltInDynamicSizing || !FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T)));
        return clone
            ? current.WithClonedValue(value, GetListExpiration(dictionary), size)
            : current.WithValue(value, GetListExpiration(dictionary), size);
    }

    private static void EnsureListCapacity<T>(IDictionary<T, DateTime?> dictionary, ICollection<T> items, IEqualityComparer<T> comparer) where T : notnull
    {
        if (dictionary is not Dictionary<T, DateTime?> values || items.Count <= values.EnsureCapacity(0) - values.Count)
            return;

        var distinctKeys = items.Count > 1 && !ReferenceEquals(values.Comparer, comparer)
            ? new HashSet<T>(values.Comparer)
            : null;
        int capacity = values.Count;
        foreach (var key in items)
            if (!values.ContainsKey(key) && (distinctKeys is null || distinctKeys.Add(key)))
                capacity = checked(capacity + 1);

        values.EnsureCapacity(capacity);
    }

    /// <summary>
    /// Returns a private copy of an entry's list values that can be modified and published with
    /// <see cref="CacheEntry.WithValue"/>, or <c>null</c> if the entry does not hold a list.
    /// </summary>
    private IDictionary<T, DateTime?>? CopyListValues<T>(CacheEntry entry) where T : notnull
    {
        bool cloneRoot = _shouldClone && (entry.StoredValue switch
        {
            Dictionary<T, DateTime?> dictionary => dictionary.GetType() != typeof(Dictionary<T, DateTime?>),
            SortedDictionary<T, DateTime?> dictionary => dictionary.GetType() != typeof(SortedDictionary<T, DateTime?>),
            SortedList<T, DateTime?> dictionary => dictionary.GetType() != typeof(SortedList<T, DateTime?>),
            ConcurrentDictionary<T, DateTime?> dictionary => dictionary.GetType() != typeof(ConcurrentDictionary<T, DateTime?>),
#if NET9_0_OR_GREATER
            OrderedDictionary<T, DateTime?> dictionary => dictionary.GetType() != typeof(OrderedDictionary<T, DateTime?>),
#endif
            _ => false
        });
        bool cloneComparer = ShouldCloneListComparer<T>(entry.StoredValue);
        bool cloneKeys = _shouldClone && !FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T));
        bool clone = cloneRoot || cloneComparer || cloneKeys;
        object? value = cloneComparer || cloneRoot && cloneKeys ? CloneDictionaryWithComparer((IDictionary<T, DateTime?>)entry.StoredValue!)
            : cloneRoot ? FastCloner.FastCloner.DeepCloneTo(entry.StoredValue, FastCloner.FastCloner.ShallowClone(entry.StoredValue))
            : cloneKeys ? entry.PeekValue() : entry.StoredValue is IListSnapshot list ? list.GetSnapshot() : entry.StoredValue;
        return value switch
        {
            Dictionary<T, DateTime?> dictionary => clone ? dictionary : new Dictionary<T, DateTime?>(dictionary, dictionary.Comparer),
            SortedDictionary<T, DateTime?> dictionary => clone ? dictionary : new SortedDictionary<T, DateTime?>(dictionary, dictionary.Comparer),
            SortedList<T, DateTime?> dictionary => clone ? dictionary : new SortedList<T, DateTime?>(dictionary, dictionary.Comparer),
            ConcurrentDictionary<T, DateTime?> dictionary => clone ? dictionary : new ConcurrentDictionary<T, DateTime?>(dictionary, dictionary.Comparer),
#if NET9_0_OR_GREATER
            OrderedDictionary<T, DateTime?> dictionary => clone ? dictionary : new OrderedDictionary<T, DateTime?>(dictionary, dictionary.Comparer),
#endif
            IDictionary<T, DateTime?> dictionary => throw new NotSupportedException($"List updates cannot preserve the comparer of {dictionary.GetType().FullName}."),
            _ => null
        };
    }

    private bool ShouldCloneListComparer<T>(object? value) where T : notnull
    {
        // Mutable comparers can reference the dictionary root or its views, even without a sizing callback.
        if (!_shouldClone)
            return false;

        object? comparer = value switch
        {
            Dictionary<T, DateTime?> dictionary => dictionary.Comparer,
            SortedDictionary<T, DateTime?> dictionary => dictionary.Comparer,
            SortedList<T, DateTime?> dictionary => dictionary.Comparer,
            ConcurrentDictionary<T, DateTime?> dictionary => dictionary.Comparer,
#if NET9_0_OR_GREATER
            OrderedDictionary<T, DateTime?> dictionary => dictionary.Comparer,
#endif
            _ => null
        };
        return comparer is not null && !FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(comparer.GetType());
    }

    private static IDictionary<TKey, TValue> CloneDictionaryWithComparer<TKey, TValue>(IDictionary<TKey, TValue> dictionary) where TKey : notnull
    {
        var clone = FastCloner.FastCloner.DeepCloneTo(dictionary, FastCloner.FastCloner.ShallowClone(dictionary))!;
        var entries = clone.ToArray();
        // Rebuild under the cloned comparer while keeping dictionary, comparer and cached views in one graph.
        clone.Clear();
        if (clone is Dictionary<TKey, TValue> values && values.GetType() == typeof(Dictionary<TKey, TValue>)
            && entries.Length <= values.EnsureCapacity(0) / 2)
            values.TrimExcess(entries.Length);
        foreach (var entry in entries)
            clone.Add(entry.Key, entry.Value);
        return clone;
    }

    private int ExpireListValues<T>(IDictionary<T, DateTime?> dictionary, string existingKey)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var expiredValueKeys = dictionary.Where(kvp => kvp.Value < utcNow).Select(kvp => kvp.Key).ToArray();
        int expiredValues = expiredValueKeys.Count(dictionary.Remove);
        if (expiredValues > 0)
            _logger.LogTrace("Removed {ExpiredValues} expired values for key: {Key}", expiredValues, existingKey);

        return expiredValues;
    }

    private static DateTime? GetListExpiration<T>(IDictionary<T, DateTime?> dictionary)
    {
        return dictionary.Values.Contains(null) ? null : dictionary.Values.Max();
    }

    public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        Interlocked.Increment(ref _writes);

        if (values is string stringValue)
            return Task.FromResult(ListRemove(key, new HashSet<string>([stringValue])));

        var items = GetDistinctListValues(values);
        if (items.Count is 0)
            return Task.FromResult<long>(0);

        return Task.FromResult(ListRemove(key, items));
    }

    private long ListRemove<T>(string key, HashSet<T> items) where T : notnull
    {
        long removed = UpdateEntry(key, current =>
        {
            if (current?.StoredValue is ListSnapshot<T> { Snapshot: null } list)
            {
                var replacement = list.Update(items, null, _timeProvider.GetUtcNow().UtcDateTime, remove: true, out long count);
                if (ReferenceEquals(list, replacement))
                    return (current, 0L);
                if (replacement.Count == 0)
                    return (null, count);

                long listSize = _usesBuiltInDynamicSizing ? CalculateEntrySize(replacement) : current.Size;
                return listSize < 0
                    ? (current, 0L)
                    : (current.WithValue(replacement, replacement.ExpiresAt, listSize), count);
            }

            object? value = current?.StoredValue is IListSnapshot snapshot ? snapshot.GetSnapshot() : current?.StoredValue;
            if (current is null || value is not IDictionary<T, DateTime?> stored)
                return (current, 0L);

            IDictionary<T, DateTime?>? dictionary = null;
            bool canInspectStored = !_shouldClone || stored.GetType() == typeof(Dictionary<T, DateTime?>)
                && (typeof(T) == typeof(string) || !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                && ReferenceEquals(((Dictionary<T, DateTime?>)stored).Comparer, EqualityComparer<T>.Default);
            if (!canInspectStored)
            {
                if (stored is not Dictionary<T, DateTime?> and not SortedDictionary<T, DateTime?>
                    and not SortedList<T, DateTime?> and not ConcurrentDictionary<T, DateTime?>
#if NET9_0_OR_GREATER
                    and not OrderedDictionary<T, DateTime?>
#endif
                    )
                {
                    var inspection = stored.GetType().IsValueType ? stored.DeepClone()
                        : FastCloner.FastCloner.DeepCloneTo(stored, FastCloner.FastCloner.ShallowClone(stored))!;
                    if (inspection.Count == 0)
                        return (current, 0L);
                    var inspectionTime = _timeProvider.GetUtcNow().UtcDateTime;
                    if (!items.Any(inspection.ContainsKey) && !inspection.Any(kvp => kvp.Value < inspectionTime))
                        return (current, 0L);
                    throw new NotSupportedException($"List updates cannot preserve the comparer of {stored.GetType().FullName}.");
                }

                // Count, lookup and enumeration callbacks must run on the owned graph too.
                dictionary = CopyListValues<T>(current)!;
            }
            else
            {
                if (stored.Count == 0)
                    return (current, 0L);
                var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
                if (!items.Any(stored.ContainsKey) && !stored.Any(kvp => kvp.Value < utcNow))
                    return (current, 0L);
            }

            dictionary ??= CopyListValues<T>(current)!;
            if (dictionary.Count == 0)
                return (current, 0L);
            int expired = ExpireListValues(dictionary, key);
            long removedCount = items.Count(dictionary.Remove);
            if (expired is 0 && removedCount is 0)
                return (current, 0L);

            if (dictionary.Count is 0)
                return (null, removedCount);

            long size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;
            return size < 0
                ? (current, 0L)
                : (WithListValues(current, dictionary, size), removedCount);
        });

        if (removed > 0)
            _logger.LogTrace("Removed value from set with cache key: {Key}", key);

        return removed;
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        if (_memory.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            var list = entry.StoredValue as ListSnapshot<T>;
            var dictionary = entry.StoredValue as Dictionary<T, DateTime?>;
            bool readKeysDirectly = _shouldClone && dictionary?.GetType() == typeof(Dictionary<T, DateTime?>)
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T));
            if (list is { Snapshot: null } || readKeysDirectly)
            {
                Interlocked.Increment(ref _hits);
                entry.RecordAccess();
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                if (page.HasValue)
                    return list is not null
                        ? list.ReadPage(now, page.Value, pageSize)
                        : ReadListPage(dictionary!, now, page.Value, pageSize);

                var values = list is not null ? list.Read(now) : ReadListValues(dictionary!, now);
                if (values.Length == 0)
                    return new CacheValue<ICollection<T>>([], false);

                return new CacheValue<ICollection<T>>(values, true);
            }
        }

        var dictionaryCacheValue = await GetAsync<IDictionary<T, DateTime?>>(key);
        if (!dictionaryCacheValue.HasValue)
            return new CacheValue<ICollection<T>>([], false);

        // Filter out expired keys instead of mutating them via ExpireListValues.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (page.HasValue && dictionaryCacheValue.Value is Dictionary<T, DateTime?> pagedDictionary && pagedDictionary.GetType() == typeof(Dictionary<T, DateTime?>))
            return ReadListPage(pagedDictionary, utcNow, page.Value, pageSize);

        var nonExpiredKeys = dictionaryCacheValue.Value.Where(kvp => kvp.Value is null || kvp.Value >= utcNow).Select(kvp => kvp.Key).ToArray();
        if (nonExpiredKeys.Length is 0)
            return new CacheValue<ICollection<T>>([], false);

        if (!page.HasValue)
            return new CacheValue<ICollection<T>>(nonExpiredKeys, true);

        int skip = (page.Value - 1) * pageSize;
        var pagedItems = nonExpiredKeys.Skip(skip).Take(pageSize).ToArray();
        return new CacheValue<ICollection<T>>(pagedItems, true);
    }

    private static CacheValue<ICollection<T>> ReadListPage<T>(Dictionary<T, DateTime?> dictionary, DateTime utcNow, int page, int pageSize, bool checkExpiration = true) where T : notnull
    {
        var result = new ListPage<T>(dictionary.Count, page, pageSize);
        if (checkExpiration)
        {
            foreach (var pair in dictionary)
            {
                if (pair.Value < utcNow)
                    continue;
                if (result.TrySkip())
                    continue;
                result.Add(pair.Key);
                if (result.IsFull)
                    break;
            }
        }
        else
        {
            foreach (var pair in dictionary)
            {
                if (result.TrySkip())
                    continue;
                result.Add(pair.Key);
                if (result.IsFull)
                    break;
            }
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
            if (_capacity == 0)
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
            foreach (var pair in dictionary)
                result[count++] = pair.Key;
        }
        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }

    private async Task<bool> SetInternalAsync(string key, CacheEntry entry, WriteMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (entry.IsExpired)
            return UpdateEntry(key, current => (current, false));

        Interlocked.Increment(ref _writes);

        bool wasUpdated = UpdateEntry(key, current => entry.IsExpired ? (current, false) : mode switch
        {
            WriteMode.Add when current is not null => (current, false),
            WriteMode.Replace when current is null => (current, false),
            _ => (entry, true)
        });

        if (wasUpdated)
            _logger.LogTrace("Set cache key: {Key}", key);

        // Check if compaction is needed AFTER memory size update
        await StartMaintenanceAsync(ShouldCompact).AnyContext();
        return wasUpdated;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count is 0)
            return 0;

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            foreach (string key in values.Keys)
                RemoveExpiredKey(key);

            return 0;
        }

        int limit = Math.Min(_maxItems.GetValueOrDefault(values.Count), values.Count);
        if (_maxItems.HasValue && values.Count > _maxItems)
        {
            _logger.LogWarning(
                "Received {TotalCount} items but max items is {MaxItems}: processing the last {Limit}",
                values.Count, _maxItems, limit);
        }

        // Use the whole dictionary when possible, otherwise copy just the slice we need.
        var work = limit >= values.Count
            ? values
            : values.Skip(values.Count - limit);

        int count = 0;
        await Parallel.ForEachAsync(work, async (pair, cancellationToken) =>
        {
            if (await SetAsync(pair.Key, pair.Value, expiresIn).AnyContext())
                Interlocked.Increment(ref count);
        }).AnyContext();

        return count;
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // Skip sizing and cloning the value when there is nothing to replace; the write itself rechecks atomically
        if (!_memory.ContainsKey(key))
            return Task.FromResult(false);

        return WriteAsync(key, value, expiresIn, WriteMode.Replace);
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            RemoveExpiredKey(key);
            return false;
        }

        _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        Interlocked.Increment(ref _writes);

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;

        // Build the replacement only once the expected value matches, so a mismatch never pays for (or throws
        // from) sizing and cloning the new value. It is built at most once, even when the update retries.
        CacheEntry? replacement = null;
        bool success = UpdateLease<bool>(key, current =>
        {
            if (!EqualityComparer<T>.Default.Equals(current.GetValue<T>(), expected))
                return null;

            // A lease that expires while it is being sized is reclaimed too, so it is never revived
            replacement ??= CreateEntry(value, expiresAt);
            return replacement is null || replacement.IsExpired ? null : (replacement, true);
        }, missResult: false);

        await StartMaintenanceAsync().AnyContext();

        _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

        return success;
    }

    public Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        return IncrementInternalAsync(key, amount, expiresIn);
    }

    public Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        return IncrementInternalAsync(key, amount, expiresIn);
    }

    private async Task<T> IncrementInternalAsync<T>(string key, T amount, TimeSpan? expiresIn) where T : struct, INumber<T>
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            RemoveExpiredKey(key);
            return T.Zero;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(amount, expiresAt);
        if (newEntry is null)
            return T.Zero;

        Interlocked.Increment(ref _writes);

        T result = UpdateEntry(key, current =>
        {
            if (newEntry.IsExpired)
                return (current, T.Zero);

            if (current is null)
                return (newEntry, amount);

            T newValue = (GetNumericValue<T>(current) ?? T.Zero) + amount;
            long size = _hasSizeCalculator ? CalculateEntrySize(newValue) : 0;
            if (size < 0)
                return (current, T.Zero);

            var replacement = current.WithValue(newValue, expiresAt, size);
            return replacement.IsExpired ? (current, T.Zero) : (replacement, newValue);
        });

        await StartMaintenanceAsync().AnyContext();

        return result;
    }

    public Task<bool> ExistsAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!_memory.TryGetValue(key, out var existingEntry))
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult(false);
        }

        if (existingEntry.IsExpired)
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult(false);
        }

        Interlocked.Increment(ref _hits);
        return Task.FromResult(true);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!_memory.TryGetValue(key, out var existingEntry))
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult<TimeSpan?>(null);
        }

        if (existingEntry.IsExpired)
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult<TimeSpan?>(null);
        }

        Interlocked.Increment(ref _hits);

        // Return null for keys with no expiration (DateTime.MaxValue means no expiration)
        if (!existingEntry.ExpiresAt.HasValue || existingEntry.ExpiresAt.Value == DateTime.MaxValue)
            return Task.FromResult<TimeSpan?>(null);

        return Task.FromResult<TimeSpan?>(existingEntry.ExpiresAt.Value.Subtract(_timeProvider.GetUtcNow().UtcDateTime));
    }

    public Task<IDictionary<string, TimeSpan?>> GetAllExpirationAsync(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        string[] keysArray = keys.ToArray();
        if (keysArray.Length is 0)
            return Task.FromResult<IDictionary<string, TimeSpan?>>(ReadOnlyDictionary<string, TimeSpan?>.Empty);

        var result = new Dictionary<string, TimeSpan?>(keysArray.Length);
        foreach (string key in keysArray)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);

            if (!_memory.TryGetValue(key, out var existingEntry))
            {
                Interlocked.Increment(ref _misses);
                // Omit non-existent keys from result
                continue;
            }

            if (existingEntry.IsExpired)
            {
                Interlocked.Increment(ref _misses);
                // Omit expired keys from result
                continue;
            }

            Interlocked.Increment(ref _hits);

            // Include keys without expiration with null value (DateTime.MaxValue means no expiration)
            if (!existingEntry.ExpiresAt.HasValue || existingEntry.ExpiresAt.Value == DateTime.MaxValue)
                result[key] = null;
            else
                result[key] = existingEntry.ExpiresAt.Value.Subtract(_timeProvider.GetUtcNow().UtcDateTime);
        }

        return Task.FromResult<IDictionary<string, TimeSpan?>>(result.AsReadOnly());
    }

    public async Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            RemoveExpiredKey(key);
            return;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = utcNow.SafeAdd(expiresIn);
        if (expiresAt < utcNow)
        {
            RemoveExpiredKey(key);
            return;
        }

        if (TrySetExpiration(key, expiresAt))
            await StartMaintenanceAsync().AnyContext();
    }

    private bool TrySetExpiration(string key, DateTime? expiresAt)
    {
        bool changed = UpdateEntry(key, current =>
        {
            var updated = current?.WithExpiration(expiresAt);
            return (updated, !ReferenceEquals(updated, current));
        });

        if (changed)
            Interlocked.Increment(ref _writes);

        return changed;
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);

        if (expirations.Count is 0)
            return;

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        int updated = 0;

        foreach (var kvp in expirations)
        {
            ArgumentException.ThrowIfNullOrEmpty(kvp.Key);

            if (kvp.Value is null)
            {
                if (TrySetExpiration(kvp.Key, null))
                    updated++;
            }
            else
            {
                if (kvp.Value.Value < CacheClientExtensions.MinimumExpiration)
                {
                    RemoveExpiredKey(kvp.Key);
                }
                else
                {
                    var expiresAt = utcNow.SafeAdd(kvp.Value.Value);
                    if (expiresAt < utcNow)
                    {
                        RemoveExpiredKey(kvp.Key);
                    }
                    else if (TrySetExpiration(kvp.Key, expiresAt))
                    {
                        updated++;
                    }
                }
            }
        }

        if (updated > 0)
            await StartMaintenanceAsync().AnyContext();
    }

    private DateTime _lastMaintenance;

    private async Task StartMaintenanceAsync(bool compactImmediately = false)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("StartMaintenanceAsync called with compactImmediately={CompactImmediately}", compactImmediately);

        if (_disposedCancellationTokenSource.IsCancellationRequested)
            return;

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (compactImmediately)
            await CompactAsync().AnyContext();

        if (TimeSpan.FromMilliseconds(250) < utcNow - _lastMaintenance)
        {
            _lastMaintenance = utcNow;
            _ = Task.Run(DoMaintenanceAsync, _disposedCancellationToken);
        }
    }

    private bool ShouldCompact => !_disposedCancellationTokenSource.IsCancellationRequested && ((_maxItems.HasValue && _memory.Count > _maxItems) || (_shouldTrackMemory && _currentMemorySize > _maxMemorySize));

    private async Task CompactAsync()
    {
        _logger.LogTrace("CompactAsync called. ShouldCompact={ShouldCompact}, CurrentMemory={CurrentMemory}, MaxMemory={MaxMemory}, Count={Count}",
            ShouldCompact, _currentMemorySize, _maxMemorySize, _memory.Count);

        if (!ShouldCompact)
            return;

        _logger.LogTrace("CompactAsync: Compacting cache");

        var expiredKeys = new List<string>();
        using (await _lock.LockAsync(_disposedCancellationToken).AnyContext())
        {
            int removalCount = 0;

            // Scale maxRemovals based on how far over the limits we are
            // Base limit of 10, but increase proportionally (up to 1000) if significantly over limit
            const int baseMaxRemovals = 10;
            const int absoluteMaxRemovals = 1000;

            int itemOverLimitFactor = 1;
            if (_maxItems is > 0 && _memory.Count > _maxItems.Value)
                itemOverLimitFactor = (int)Math.Ceiling((double)_memory.Count / _maxItems.Value);

            int memoryOverLimitFactor = 1;
            if (_shouldTrackMemory && _maxMemorySize is > 0 && _currentMemorySize > _maxMemorySize.Value)
                memoryOverLimitFactor = (int)Math.Ceiling((double)_currentMemorySize / _maxMemorySize.Value);

            int overLimitFactor = Math.Max(itemOverLimitFactor, memoryOverLimitFactor);
            if (overLimitFactor < 1)
                overLimitFactor = 1;

            int maxRemovals = baseMaxRemovals * overLimitFactor;
            if (maxRemovals > absoluteMaxRemovals)
                maxRemovals = absoluteMaxRemovals;

            int conflicts = 0;
            while (ShouldCompact && removalCount < maxRemovals && conflicts < maxRemovals)
            {
                // Check if we still need compaction
                bool needsItemCompaction = _maxItems.HasValue && _memory.Count > _maxItems;
                bool needsMemoryCompaction = _shouldTrackMemory && _currentMemorySize > _maxMemorySize;

                if (!needsItemCompaction && !needsMemoryCompaction)
                    break;

                // For memory compaction, prefer size-aware eviction
                // For item compaction, prefer traditional LRU
                var candidate = needsMemoryCompaction ? FindWorstSizeToUsageRatio() : FindLeastRecentlyUsed();

                if (candidate is not { } entryToRemove)
                    break;

                _logger.LogDebug("Removing cache entry {Key} due to cache exceeding limit (Items: {ItemCount}/{MaxItems}, Memory: {MemorySize:N0}/{MaxMemorySize:N0})",
                    entryToRemove.Key, _memory.Count, _maxItems, _currentMemorySize, _maxMemorySize);

                if (!TryRemoveEntry(entryToRemove.Key, entryToRemove.Value))
                {
                    // The entry changed since it was selected. Losing that race frees nothing, so it must not use up
                    // the removal budget; conflicts have their own cap so a pass under heavy contention still ends.
                    conflicts++;
                    continue;
                }

                if (entryToRemove.Value.IsExpired)
                    expiredKeys.Add(entryToRemove.Key);

                removalCount++;
            }

            // Log if we hit maxRemovals but still need compaction
            if (removalCount >= maxRemovals && ShouldCompact)
            {
                _logger.LogDebug("CompactAsync: Reached max removals ({MaxRemovals}) but still over limit (Items: {ItemCount}/{MaxItems}, Memory: {MemorySize:N0}/{MaxMemorySize:N0}). Will retry on next maintenance cycle.",
                    maxRemovals, _memory.Count, _maxItems, _currentMemorySize, _maxMemorySize);
            }
        }

        if (_disposedCancellationTokenSource.IsCancellationRequested)
            return;

        // Notify about expired items
        foreach (string expiredKey in expiredKeys)
            OnItemExpired(expiredKey);
    }

    private KeyValuePair<string, CacheEntry>? FindLeastRecentlyUsed()
    {
        KeyValuePair<string, CacheEntry>? oldest = null;

        foreach (var kvp in _memory)
        {
            if (kvp.Value.IsExpired)
                return kvp;

            if (oldest is not { } current ||
                kvp.Value.LastAccessTicks < current.Value.LastAccessTicks ||
                (kvp.Value.LastAccessTicks == current.Value.LastAccessTicks && kvp.Value.InstanceNumber < current.Value.InstanceNumber))
                oldest = kvp;
        }

        return oldest;
    }

    /// <summary>
    /// Finds the cache entry with the worst size-to-usage ratio for eviction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method implements a "waste score" algorithm that considers multiple factors:
    /// </para>
    /// <list type="bullet">
    /// <item>Size: Larger entries receive higher scores (logarithmic scale in KB)</item>
    /// <item>Age: Older entries receive slightly higher scores (0.5x weight)</item>
    /// <item>Access recency: Less recently accessed entries receive higher scores (2x weight)</item>
    /// </list>
    /// <para>
    /// The algorithm prioritizes evicting entries that consume more memory and haven't been
    /// accessed recently. In uniform access patterns (all entries accessed equally), the
    /// algorithm may evict entries based primarily on size and age, which means recently-accessed
    /// entries could still be evicted if they are large and old. This is intentional behavior
    /// to optimize memory usage over strict LRU ordering.
    /// </para>
    /// <para>
    /// Expired entries are always prioritized and returned immediately if found.
    /// </para>
    /// <para>
    /// <strong>Performance note:</strong> This method performs an O(n) iteration over all cache entries
    /// on each eviction. For very large caches (100k+ entries), this could become a bottleneck during
    /// high-contention compaction. However, because eviction typically removes multiple items per
    /// compaction cycle (scaled by <c>maxRemovals</c>) and compaction is throttled to run infrequently,
    /// this trade-off favors simplicity and correctness over more complex data structures like priority
    /// queues that would add memory overhead and complexity for all cache operations. For extremely
    /// large caches with frequent eviction, consider using <see cref="InMemoryCacheClientOptionsBuilder.MaxItems"/>
    /// to bound the cache size at a level where O(n) iteration remains acceptable.
    /// </para>
    /// </remarks>
    /// <returns>The entry to evict, or null if no suitable candidate found.</returns>
    private KeyValuePair<string, CacheEntry>? FindWorstSizeToUsageRatio()
    {
        KeyValuePair<string, CacheEntry>? candidate = null;
        double worstRatio = Double.MinValue; // Start with minimum value so any score can win
        long currentTime = _timeProvider.GetUtcNow().Ticks;

        _logger.LogTrace("FindWorstSizeToUsageRatio: Checking {Count} entries", _memory.Count);

        foreach (var kvp in _memory)
        {
            // Prioritize expired items first
            if (kvp.Value.IsExpired)
                return kvp;

            // Calculate a "waste score" based on size vs recent usage
            long size = kvp.Value.Size;
            long timeSinceLastAccess = currentTime - kvp.Value.LastAccessTicks;
            long timeSinceCreation = currentTime - kvp.Value.LastModifiedTicks;

            // Avoid division by zero and give preference to older, larger, less-accessed items
            double accessRecency = Math.Max(1, TimeSpan.FromTicks(timeSinceLastAccess).TotalMinutes);
            double ageInMinutes = Math.Max(1, TimeSpan.FromTicks(timeSinceCreation).TotalMinutes);

            // Calculate waste score: larger size + older age + less recent access = higher score (worse)
            // Normalize to prevent overflow and give reasonable weighting
            double sizeWeight = Math.Log10(Math.Max(1, size / 1024.0)); // Log scale for size in KB
            double ageWeight = Math.Log10(ageInMinutes);
            double accessWeight = Math.Log10(accessRecency);

            double wasteScore = sizeWeight + (ageWeight * 0.5) + (accessWeight * 2.0); // Access recency weighted more heavily
            if (wasteScore > worstRatio)
            {
                worstRatio = wasteScore;
                candidate = kvp;
            }
        }

        _logger.LogTrace("FindWorstSizeToUsageRatio: Selected {Key} with score {Score}", candidate?.Key, worstRatio);
        return candidate;
    }

    internal async Task DoMaintenanceAsync()
    {
        _logger.LogTrace("DoMaintenance: Starting");

        try
        {
            // Sample UTC once per pass, using the same strict expiration boundary as reads.
            // Concurrent enumeration is safe; remove only the observed entry, not a later replacement.
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var kvp in _memory)
            {
                if (kvp.Value.ExpiresAt is not { } expiresAt || expiresAt >= utcNow || !TryRemoveEntry(kvp.Key, kvp.Value))
                    continue;

                _logger.LogDebug("DoMaintenance: Removed expired key {Key}", kvp.Key);
                OnItemExpired(kvp.Key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trying to find expired cache items: {Message}", ex.Message);
        }

        if (ShouldCompact)
        {
            await CompactAsync().AnyContext();

            // Recalculate memory size after compaction to correct any drift
            if (_shouldTrackMemory)
                RecalculateMemorySize();
        }

        _logger.LogTrace("DoMaintenance: Finished");
    }

    public virtual void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _memory.Clear();
        _disposedCancellationTokenSource.Cancel();
        _disposedCancellationTokenSource.Dispose();
        ItemExpired?.Dispose();
        _sizeCalculator = null; // Allow GC to collect any captured closures
    }

    /// <summary>
    /// Creates a CacheEntry with pre-calculated size. Returns null if the entry should be skipped (exceeds size limits).
    /// </summary>
    private CacheEntry? CreateEntry<T>(T value, DateTime? expiresAt, bool valueIsOwned = false)
    {
        long size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
        if (size < 0)
            return null;

        return new CacheEntry(value, expiresAt, _timeProvider, _shouldClone, size, valueIsOwned);
    }

    private interface IListSnapshot
    {
        object GetSnapshot();
        string ValueTypeName { get; }
    }

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
            var expirations = expiration is { } time
                ? ImmutableSortedDictionary<DateTime, int>.Empty.Add(time, keys.Count)
                : ImmutableSortedDictionary<DateTime, int>.Empty;
            _storage = new Storage(values.Value, heads.Value, hashes.Value, comparer, keys.Count, bucketCount,
                expirations, ImmutableStack<int>.Empty, keys.Count, expiration.HasValue ? 0 : keys.Count);
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
            bool hasExpired = !storage.Expirations.IsEmpty && storage.Expirations.First().Key < utcNow;
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
            bool hasExpired = !storage.Expirations.IsEmpty && storage.Expirations.First().Key < utcNow;
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

        public ListSnapshot<T> Update(ICollection<T> items, DateTime? expiration, DateTime utcNow, bool remove, out long removed)
        {
            var storage = Volatile.Read(ref _storage);
            removed = 0;
            int count = storage.Count;
            int bucketCount = storage.BucketCount;
            Pages<Item>.Builder? values = null;
            Pages<int>.Builder? heads = null;
            Pages<int>.Builder? hashes = null;
            bool largeBatch = items.Count >= Math.Max(1, count / BlockSize);
            bool prune = storage.Exposed
                ? storage.Dictionary!.Any(pair => pair.Value < utcNow)
                : !storage.Expirations.IsEmpty && storage.Expirations.First().Key < utcNow;
            if (remove && !prune && !items.Any(key => storage.Dictionary?.ContainsKey(key) ?? Find(key, out _, out _, out _) >= 0))
                return this;
            if (!remove && !prune && (storage.Dictionary is not null || largeBatch) && items.All(IsUnchanged))
                return this;

            if (storage.Exposed || largeBatch || count < storage.NextIndex / 4)
            {
                var dictionary = CopyValues(storage);
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

            ImmutableSortedDictionary<DateTime, int>.Builder? expirations = null;
            var freeIndices = storage.FreeIndices;
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
                    if (previous.Expiration == expiration)
                        continue;
                    key = previous.Key;
                    next = previous.Next;
                    AdjustExpiration(previous.Expiration, -1);
                }
                else
                {
                    if (!freeIndices.IsEmpty)
                    {
                        index = freeIndices.Peek();
                        freeIndices = freeIndices.Pop();
                    }
                    else
                        index = nextIndex++;
                    next = heads[bucket];
                    heads[bucket] = index + 1;
                    hashes[index] = hash;
                    count++;
                }
                values[index] = new Item(key, expiration, true, next);
                AdjustExpiration(expiration, 1);
                changed = true;
            }
            return changed
                ? new ListSnapshot<T>(new Storage(values.Value, heads.Value, hashes.Value, storage.Comparer, count, bucketCount,
                    expirations?.ToImmutable() ?? storage.Expirations, freeIndices, nextIndex, permanentCount))
                : this;

            bool IsUnchanged(T key)
            {
                if (storage.Dictionary is { } dictionary)
                    return dictionary.TryGetValue(key, out var previous) && previous == expiration;
                int index = Find(key, out _, out _, out _);
                return index >= 0 && storage.Items![index].Expiration == expiration;
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
                values[index] = default;
                AdjustExpiration(item.Expiration, -1);
                freeIndices = freeIndices.Push(index);
                count--;
                changed = true;
                return true;
            }

            void AdjustExpiration(DateTime? value, int delta)
            {
                if (value is not { } time)
                {
                    permanentCount += delta;
                    return;
                }
                expirations ??= storage.Expirations.ToBuilder();
                int remaining = expirations.GetValueOrDefault(time) + delta;
                if (remaining == 0)
                    expirations.Remove(time);
                else
                    expirations[time] = remaining;
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
            public ImmutableSortedDictionary<DateTime, int> Expirations { get; } = ImmutableSortedDictionary<DateTime, int>.Empty;
            public ImmutableStack<int> FreeIndices { get; } = ImmutableStack<int>.Empty;
            public int NextIndex { get; }
            public int PermanentCount { get; }
            public DateTime? ExpiresAt => Exposed
                ? Dictionary!.Count == 0 ? null : GetListExpiration(Dictionary)
                : PermanentCount > 0 || Expirations.IsEmpty ? null : Expirations.Last().Key;

            public Storage(Dictionary<T, DateTime?> dictionary, bool exposed = false)
            {
                Dictionary = dictionary;
                Exposed = exposed;
                Comparer = dictionary.Comparer;
                NextIndex = dictionary.Count;
                if (exposed)
                    return;
                var expirations = ImmutableSortedDictionary.CreateBuilder<DateTime, int>();
                DateTime? previousExpiration = null;
                int expirationCount = 0;
                foreach (var pair in dictionary)
                {
                    if (pair.Value is { } expiration)
                    {
                        if (expiration != previousExpiration)
                        {
                            if (previousExpiration is { } previous)
                                expirations[previous] = expirations.GetValueOrDefault(previous) + expirationCount;
                            previousExpiration = expiration;
                            expirationCount = 0;
                        }
                        expirationCount++;
                    }
                    else
                        PermanentCount++;
                }
                if (previousExpiration is { } last)
                    expirations[last] = expirations.GetValueOrDefault(last) + expirationCount;
                Expirations = expirations.ToImmutable();
            }

            public Storage(Pages<Item> items, Pages<int> heads, Pages<int> hashes, IEqualityComparer<T> comparer, int count, int bucketCount,
                ImmutableSortedDictionary<DateTime, int> expirations, ImmutableStack<int> freeIndices, int nextIndex, int permanentCount)
            {
                Items = items;
                Heads = heads;
                Hashes = hashes;
                Comparer = comparer;
                _count = count;
                BucketCount = bucketCount;
                Expirations = expirations;
                FreeIndices = freeIndices;
                NextIndex = nextIndex;
                PermanentCount = permanentCount;
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

    /// <summary>
    /// A cache entry's value, expiration and size never change after it is published to the cache dictionary;
    /// changes publish a copy (<see cref="WithValue"/>, <see cref="WithExpiration"/>). This lets conditional removals
    /// compare by reference and guarantees the entry they checked is the entry they remove. Only access metadata
    /// (<see cref="LastAccessTicks"/>) is updated in place, and it does not participate in equality.
    /// </summary>
    /// <remarks>
    /// Keep this a class with reference equality. <see cref="ConcurrentDictionary{TKey,TValue}"/> compares entries with
    /// <see cref="EqualityComparer{T}.Default"/> in its compare-and-swap operations, so making this a record or
    /// implementing <see cref="IEquatable{T}"/> would let <see cref="UpdateEntry{TResult}"/> replace or remove an
    /// entry it never observed.
    /// </remarks>
    internal sealed class CacheEntry
    {
        // FastCloneState reserves array length + 1 references: four inline and up to 4096 pooled.
        private const int MaxPooledDictionaryCloneCapacity = 4099;
        private static readonly ConcurrentDictionary<Type, Func<object, bool, object>> _dictionaryCloners = new();
        private readonly object? _cacheValue;
        private static long _instanceCount;
        private readonly bool _shouldClone;
        private readonly bool _cloneDictionaryKeys;
        private readonly TimeProvider _timeProvider;

#if DEBUG
        private long _usageCount;
#endif

        public CacheEntry(object? value, DateTime? expiresAt, TimeProvider timeProvider, bool shouldClone = true, long size = 0, bool valueIsOwned = false)
        {
            _timeProvider = timeProvider;
            _shouldClone = shouldClone && TypeRequiresCloning(value?.GetType());
            _cloneDictionaryKeys = _shouldClone && RequiresDictionaryKeyCloning(value?.GetType());
            _cacheValue = _shouldClone && !valueIsOwned ? CloneValue(value, _cloneDictionaryKeys) : value;
            var utcNow = _timeProvider.GetUtcNow();
            LastAccessTicks = utcNow.Ticks;
            LastModifiedTicks = utcNow.Ticks;
            ExpiresAt = expiresAt;
            InstanceNumber = Interlocked.Increment(ref _instanceCount);
            Size = size;
        }

        private CacheEntry(CacheEntry source, object? cacheValue, DateTime? expiresAt, long size, long lastModifiedTicks)
        {
            _timeProvider = source._timeProvider;
            _shouldClone = source._shouldClone;
            _cloneDictionaryKeys = _shouldClone && (cacheValue?.GetType() == source._cacheValue?.GetType()
                ? source._cloneDictionaryKeys
                : RequiresDictionaryKeyCloning(cacheValue?.GetType()));
            _cacheValue = cacheValue;
            LastAccessTicks = source.LastAccessTicks;
            LastModifiedTicks = lastModifiedTicks;
            ExpiresAt = expiresAt;
            InstanceNumber = source.InstanceNumber;
            Size = size;
        }

        internal long InstanceNumber { get; }
        internal DateTime? ExpiresAt { get; }
        internal bool IsExpired => ExpiresAt.HasValue && ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime;
        internal long LastAccessTicks { get; private set; }
        internal long LastModifiedTicks { get; }

        /// <summary>
        /// The size of this cache entry in bytes. Set at construction time.
        /// </summary>
        internal long Size { get; }

#if DEBUG
        internal long UsageCount => _usageCount;
#endif

        internal object? Value
        {
            get
            {
                RecordAccess();
                return PeekValue();
            }
        }

        internal void RecordAccess()
        {
            LastAccessTicks = _timeProvider.GetUtcNow().Ticks;
#if DEBUG
            Interlocked.Increment(ref _usageCount);
#endif
        }

        /// <summary>
        /// Returns the value without recording an access, so diagnostic reads do not affect eviction order.
        /// </summary>
        internal object? PeekValue()
        {
            object? value = _cacheValue is IListSnapshot list ? list.GetSnapshot() : _cacheValue;
            return _shouldClone ? CloneValue(value, _cloneDictionaryKeys) : value;
        }

        private static bool RequiresDictionaryKeyCloning(Type? type)
        {
            if (type is null || !type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>))
                return false;

            var keyType = type.GenericTypeArguments[0];
            return !FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(keyType);
        }

        private static object? CloneValue(object? value, bool forceDictionaryRebuild)
        {
            if (value is null)
                return null;

            var type = value.GetType();
            if (!type.IsGenericType)
                return value.DeepClone();

            var definition = type.GetGenericTypeDefinition();
            if (definition != typeof(Dictionary<,>)
                && definition != typeof(SortedDictionary<,>)
                && definition != typeof(SortedList<,>)
                && definition != typeof(ConcurrentDictionary<,>)
#if NET9_0_OR_GREATER
                && definition != typeof(OrderedDictionary<,>)
#endif
                )
                return value.DeepClone();

            return _dictionaryCloners.GetOrAdd(type, static dictionaryType => typeof(CacheEntry)
                .GetMethod(nameof(CloneDictionary), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(dictionaryType.GenericTypeArguments)
                .CreateDelegate<Func<object, bool, object>>())(value, forceDictionaryRebuild);
        }

        private static object CloneDictionary<TKey, TValue>(object value, bool forceDictionaryRebuild) where TKey : notnull
        {
            if (value is not Dictionary<TKey, TValue> dictionary)
            {
                object? comparer = value switch
                {
                    SortedDictionary<TKey, TValue> sorted => sorted.Comparer,
                    SortedList<TKey, TValue> sorted => sorted.Comparer,
                    ConcurrentDictionary<TKey, TValue> concurrent => concurrent.Comparer,
#if NET9_0_OR_GREATER
                    OrderedDictionary<TKey, TValue> ordered => ordered.Comparer,
#endif
                    _ => null
                };
                return comparer is not null && !FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(comparer.GetType())
                    ? CloneDictionaryWithComparer((IDictionary<TKey, TValue>)value)
                    : value.DeepClone();
            }

            int capacity = dictionary.EnsureCapacity(0);
            // Match the vendored cloner's small-dictionary boundary and avoid unused storage or unpooled tracking.
            if ((dictionary.Count <= 32 || dictionary.Count <= capacity / 2 || capacity > MaxPooledDictionaryCloneCapacity)
                && !forceDictionaryRebuild
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(TValue))
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(dictionary.Comparer.GetType()))
                return new Dictionary<TKey, TValue>(dictionary, dictionary.Comparer);

            // The vendored cloner rebuilds small dictionaries, preserving their comparer and discarding
            // cached views and unused capacity. Mutable keys or comparers need the tracked adapter below.
            if (!forceDictionaryRebuild && dictionary.Count <= 32
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(dictionary.Comparer.GetType()))
                return dictionary.DeepClone();

            // Track the dictionary root so cached Keys and Values views belong to the clone.
            var clone = FastCloner.FastCloner.DeepCloneTo(dictionary, FastCloner.FastCloner.ShallowClone(dictionary))!;
            if (!forceDictionaryRebuild && ReferenceEquals(dictionary.Comparer, clone.Comparer))
                return clone;

            var entries = clone.ToArray();
            // Rehash when cloning can change hashes, preserving references back to the dictionary root.
            clone.Clear();
            if (entries.Length <= capacity / 2)
                clone.TrimExcess(entries.Length);
            foreach (var entry in entries)
                clone.Add(entry.Key, entry.Value);

            return clone;
        }

        /// <summary>
        /// The stored payload without cloning. It is shared with every reader and must never be mutated.
        /// </summary>
        internal object? StoredValue => _cacheValue;

        /// <summary>
        /// Returns a copy of this entry with a new expiration, or this entry when the expiration is unchanged.
        /// The value is shared, not cloned again.
        /// </summary>
        internal CacheEntry WithExpiration(DateTime? expiresAt)
        {
            if (expiresAt == ExpiresAt)
                return this;

            return new CacheEntry(this, _cacheValue, expiresAt, Size, LastModifiedTicks);
        }

        /// <summary>
        /// Returns a copy of this entry with a new value, size and expiration, marked as modified now.
        /// The value is stored as-is, so it must be an instance nothing else references.
        /// </summary>
        internal CacheEntry WithValue(object? value, DateTime? expiresAt, long size)
        {
            long utcNowTicks = _timeProvider.GetUtcNow().Ticks;
            return new CacheEntry(this, value, expiresAt, size, utcNowTicks) { LastAccessTicks = utcNowTicks };
        }

        internal CacheEntry WithClonedValue(object? value, DateTime? expiresAt, long size)
        {
            return WithValue(CloneValue(value, RequiresDictionaryKeyCloning(value?.GetType())), expiresAt, size);
        }

        [return: MaybeNull]
        public T GetValue<T>()
        {
            object? val = Value;
            var t = typeof(T);

            if (val is null)
                return default;

            if (t == TypeHelper.BoolType || t == TypeHelper.StringType || t == TypeHelper.CharType || t == TypeHelper.DateTimeType || t.IsNumeric())
                return (T)Convert.ChangeType(val, t);

            if (t == TypeHelper.NullableBoolType || t == TypeHelper.NullableCharType || t == TypeHelper.NullableDateTimeType || t.IsNullableNumeric())
                return (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(t)!);

            return (T)val;
        }
    }

    /// <summary>
    /// Calculates the size of a cache entry.
    /// Returns -1 if the entry should not be cached (exceeds MaxEntrySize or invalid size from calculator).
    /// Returns 0 immediately if no size calculator is configured.
    /// </summary>
    /// <param name="value">The value to calculate size for.</param>
    /// <returns>The calculated size in bytes, or -1 if the entry should be skipped.</returns>
    /// <exception cref="MaxEntrySizeExceededCacheException">Thrown when entry exceeds MaxEntrySize and ShouldThrowOnMaxEntrySizeExceeded is true.</exception>
    private long CalculateEntrySize(object? value)
    {
        // Fast bail-out: no size calculator configured
        if (!_hasSizeCalculator)
            return 0;

        if (value is null)
            return 0;

        long size;
        try
        {
            size = _sizeCalculator!(value);
        }
        catch (MaxEntrySizeExceededCacheException)
        {
            throw; // Re-throw MaxEntrySizeExceededCacheException from size check
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating entry size for type {EntryType}, using fallback estimation",
                GetEntryTypeName(value));

            // Fallback to simple estimation
            size = value switch
            {
                null => 8,
                string stringValue => 24 + ((long)stringValue.Length * 2),
                _ => 64 // Default object overhead
            };
        }

        // Validate both the calculator result and fallback estimate.
        if (size < 0)
        {
            _logger.LogWarning("SizeCalculator returned negative size {Size} for type {EntryType}. Entry will not be cached. " +
                "Custom SizeCalculator functions must return non-negative values.",
                size, GetEntryTypeName(value));
            return -1;
        }

        if (_maxEntrySize.HasValue && size > _maxEntrySize.Value)
        {
            if (_shouldThrowOnMaxEntrySizeExceeded)
                throw new MaxEntrySizeExceededCacheException(size, _maxEntrySize.Value, GetEntryTypeName(value));

            _logger.LogWarning("Cache entry size {EntrySize:N0} bytes exceeds maximum allowed size {MaxEntrySize:N0} bytes for type {EntryType}. Entry will not be cached.",
                size, _maxEntrySize.Value, GetEntryTypeName(value));
            return -1;
        }

        return size;
    }

    private static string GetEntryTypeName(object? value) => value is IListSnapshot list ? list.ValueTypeName : value?.GetType().Name ?? "null";

    private static bool TypeRequiresCloning(Type? t)
    {
        if (t == null)
            return true;

        if (t == TypeHelper.BoolType ||
            t == TypeHelper.NullableBoolType ||
            t == TypeHelper.StringType ||
            t == TypeHelper.CharType ||
            t == TypeHelper.NullableCharType ||
            t.IsNumeric() ||
            t.IsNullableNumeric())
            return false;

        return !t.GetTypeInfo().IsValueType;
    }
}

public class ItemExpiredEventArgs : EventArgs
{
    public required InMemoryCacheClient Client { get; set; }
    public required string Key { get; set; }
    public bool SendNotification { get; set; }
}
