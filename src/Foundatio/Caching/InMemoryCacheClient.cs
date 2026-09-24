using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
    private bool _isDisposed;

    public InMemoryCacheClient() : this(o => o)
    {
    }

    public InMemoryCacheClient(InMemoryCacheClientOptions? options = null)
    {
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
    /// Recalculates the current memory size by summing all non-expired cache entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Note: This method iterates over the cache dictionary which may be modified concurrently.
    /// The calculated total may be temporarily inaccurate if entries are added/removed during
    /// iteration, but this is acceptable for memory tracking purposes and will self-correct
    /// on subsequent operations or recalculations.
    /// </para>
    /// <para>
    /// Expired entries are excluded from the calculation to ensure accurate memory reporting.
    /// </para>
    /// </remarks>
    private long RecalculateMemorySize()
    {
        if (!_shouldTrackMemory)
            return 0;

        // Take a snapshot of values to reduce (but not eliminate) race condition window
        var entries = _memory.Values.ToArray();
        long totalSize = 0;
        foreach (var entry in entries)
        {
            // Skip expired entries to ensure accurate memory reporting
            if (!entry.IsExpired)
                totalSize += entry.Size;
        }

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
        if (ItemExpired is null)
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
        var removed = UpdateEntry<CacheEntry?>(key, current => (null, current));

        // Return false if the entry was expired (consistent with Redis behavior)
        return Task.FromResult(removed is { IsExpired: false });
    }

    public Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        bool success = UpdateEntry(key, current =>
        {
            if (current is null || !EqualityComparer<T>.Default.Equals(current.GetValue<T>(), expected))
                return (current, false);

            // Check expiry after the comparison: a lease that expired while it was being compared no longer
            // belongs to the caller. Maintenance removes the entry and raises ItemExpired.
            return current.IsExpired ? (current, false) : (null, true);
        });

        _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);
        return Task.FromResult(success);
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
            if (UpdateEntry<bool>(key, current => (null, current is not null)))
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
        if (!UpdateEntry<bool>(key, current => (null, current is not null)))
            return 0;

        OnItemExpired(key, sendNotification);
        return 1;
    }

    /// <summary>
    /// Atomically moves the entry for <paramref name="key"/> to the state chosen by <paramref name="update"/> and
    /// keeps the tracked memory size in step.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="update">
    /// Receives the current entry (<c>null</c> when the key is missing) and returns the entry the key should hold:
    /// the same instance to leave it unchanged, a new instance to add or replace it, or <c>null</c> to remove it.
    /// It runs again with the latest entry when another writer changes the key first, so it must not have side
    /// effects; only the result of the attempt that was published is returned.
    /// </param>
    internal TResult UpdateEntry<TResult>(string key, Func<CacheEntry?, (CacheEntry? Entry, TResult Result)> update)
    {
        while (true)
        {
            _memory.TryGetValue(key, out var current);
            var (desired, result) = update(current);
            if (ReferenceEquals(desired, current))
                return result;

            bool published = (current, desired) switch
            {
                (null, not null) => _memory.TryAdd(key, desired),
                (not null, null) => _memory.TryRemove(new KeyValuePair<string, CacheEntry>(key, current)),
                _ => _memory.TryUpdate(key, desired!, current!)
            };

            if (!published)
                continue;

            UpdateMemorySize((desired?.Size ?? 0) - (current?.Size ?? 0));
            return result;
        }
    }

    /// <summary>
    /// Removes the entry for <paramref name="key"/> only if it is still the exact instance that was observed,
    /// so an entry replaced concurrently is never removed by mistake.
    /// </summary>
    internal bool TryRemoveEntry(string key, CacheEntry observed)
    {
        return UpdateEntry(key, current => ReferenceEquals(current, observed) ? (null, true) : (current, false));
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

        return SetInternalAsync(key, entry, addOnly: true);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            RemoveExpiredKey(key);
            return Task.FromResult(false);
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;

        // Fast path: when no size calculator, create entry directly (matches main branch behavior)
        if (!_hasSizeCalculator)
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone, 0));

        // Slow path: calculate size and check limits
        var entry = CreateEntry(value, expiresAt);
        if (entry is null)
            return Task.FromResult(false); // Entry exceeds limits

        return SetInternalAsync(key, entry);
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
            if (current is null)
                return (newEntry, value);

            if (GetNumericValue<T>(current) is { } currentValue && (higher ? currentValue < value : currentValue > value))
                return (current.WithValue(value, expiresAt, newEntry.Size), higher ? value - currentValue : currentValue - value);

            return (current.WithExpiration(expiresAt), T.Zero);
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
            ? ListAdd(key, new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase) { { stringValue, expiresAt } }, expiresAt)
            : ListAdd(key, new HashSet<T>(values.Where(v => v is not null)).ToDictionary(k => k, _ => expiresAt), expiresAt);

        if (added > 0)
            await StartMaintenanceAsync().AnyContext();

        return added;
    }

    private long ListAdd<T>(string key, Dictionary<T, DateTime?> items, DateTime? expiresAt) where T : notnull
    {
        if (items.Count is 0)
            return 0;

        var entry = CreateEntry(items, expiresAt);
        if (entry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        return UpdateEntry(key, current =>
        {
            if (current is null)
                return (entry, (long)items.Count);

            if (CopyListValues<T>(current) is not { } dictionary)
                throw new InvalidOperationException($"Unable to add value for key: {key}. Cache value does not contain a set");

            // Merge from the new entry's stored values, which were cloned when CloneValues is enabled
            ExpireListValues(dictionary, key);
            foreach (var kvp in (Dictionary<T, DateTime?>)entry.StoredValue!)
                dictionary[kvp.Key] = kvp.Value;

            long size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;
            return size < 0
                ? (current, 0L)
                : (current.WithValue(dictionary, GetListExpiration(dictionary), size), (long)items.Count);
        });
    }

    /// <summary>
    /// Returns a private copy of an entry's list values that can be modified and published with
    /// <see cref="CacheEntry.WithValue"/>, or <c>null</c> if the entry does not hold a list.
    /// </summary>
    private static IDictionary<T, DateTime?>? CopyListValues<T>(CacheEntry entry) where T : notnull
    {
        return entry.StoredValue switch
        {
            Dictionary<T, DateTime?> dictionary => new Dictionary<T, DateTime?>(dictionary, dictionary.Comparer),
            SortedDictionary<T, DateTime?> dictionary => new SortedDictionary<T, DateTime?>(dictionary, dictionary.Comparer),
            SortedList<T, DateTime?> dictionary => new SortedList<T, DateTime?>(dictionary, dictionary.Comparer),
            ConcurrentDictionary<T, DateTime?> dictionary => new ConcurrentDictionary<T, DateTime?>(dictionary, dictionary.Comparer),
            IDictionary<T, DateTime?> dictionary => new Dictionary<T, DateTime?>(dictionary),
            _ => null
        };
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

        var items = new HashSet<T>(values.Where(v => v is not null));
        if (items.Count is 0)
            return Task.FromResult<long>(0);

        return Task.FromResult(ListRemove(key, items));
    }

    private long ListRemove<T>(string key, HashSet<T> items) where T : notnull
    {
        long removed = UpdateEntry(key, current =>
        {
            if (current?.StoredValue is not IDictionary<T, DateTime?> { Count: > 0 } stored)
                return (current, 0L);

            // A missing-value removal needs no private copy unless expired values also need pruning.
            // Use the stored dictionary's comparer, which may differ from the input set's comparer.
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            if (!items.Any(stored.ContainsKey) && !stored.Any(kvp => kvp.Value < utcNow))
                return (current, 0L);

            var dictionary = CopyListValues<T>(current)!;
            int expired = ExpireListValues(dictionary, key);
            long removedCount = items.Count(dictionary.Remove);
            if (expired is 0 && removedCount is 0)
                return (current, 0L);

            if (dictionary.Count is 0)
                return (null, removedCount);

            long size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;
            return size < 0
                ? (current, 0L)
                : (current.WithValue(dictionary, GetListExpiration(dictionary), size), removedCount);
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

        var dictionaryCacheValue = await GetAsync<IDictionary<T, DateTime?>>(key);
        if (!dictionaryCacheValue.HasValue)
            return new CacheValue<ICollection<T>>([], false);

        // Filter out expired keys instead of mutating them via ExpireListValues.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var nonExpiredKeys = dictionaryCacheValue.Value.Where(kvp => kvp.Value is null || kvp.Value >= utcNow).Select(kvp => kvp.Key).ToArray();
        if (nonExpiredKeys.Length is 0)
            return new CacheValue<ICollection<T>>([], false);

        if (!page.HasValue)
            return new CacheValue<ICollection<T>>(nonExpiredKeys, true);

        int skip = (page.Value - 1) * pageSize;
        var pagedItems = nonExpiredKeys.Skip(skip).Take(pageSize).ToArray();
        return new CacheValue<ICollection<T>>(pagedItems, true);
    }

    private async Task<bool> SetInternalAsync(string key, CacheEntry entry, bool addOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (entry.IsExpired)
        {
            RemoveExpiredKey(key);
            return false;
        }

        Interlocked.Increment(ref _writes);

        // Add-only writes may replace an expired entry, since it is logically absent
        bool wasUpdated = UpdateEntry(key, current =>
            !addOnly || current is null || current.IsExpired ? (entry, true) : (current, false));

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

        if (!_memory.ContainsKey(key))
            return Task.FromResult(false);

        return SetAsync(key, value, expiresIn);
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
        bool success = UpdateEntry(key, current =>
        {
            if (current is null || !EqualityComparer<T>.Default.Equals(current.GetValue<T>(), expected))
                return (current, false);

            // Check expiry after the comparison: a lease that expired while it was being compared must not be
            // revived. Maintenance removes the entry and raises ItemExpired.
            if (current.IsExpired)
                return (current, false);

            replacement ??= CreateEntry(value, expiresAt);
            return replacement is null ? (current, false) : (replacement, true);
        });

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
            if (current is null)
                return (newEntry, amount);

            T newValue = (GetNumericValue<T>(current) ?? T.Zero) + amount;
            long size = _hasSizeCalculator ? CalculateEntrySize(newValue) : 0;
            return size < 0 ? (current, T.Zero) : (current.WithValue(newValue, expiresAt, size), newValue);
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
        _logger.LogTrace("StartMaintenanceAsync called with compactImmediately={CompactImmediately}", compactImmediately);

        if (_disposedCancellationTokenSource.IsCancellationRequested)
            return;

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (compactImmediately)
            await CompactAsync().AnyContext();

        if (TimeSpan.FromMilliseconds(250) < utcNow - _lastMaintenance)
        {
            _lastMaintenance = utcNow;
            _ = Task.Run(DoMaintenanceAsync, _disposedCancellationTokenSource.Token);
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
        using (await _lock.LockAsync(_disposedCancellationTokenSource.Token).AnyContext())
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

            while (ShouldCompact && removalCount < maxRemovals)
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
                    // The entry changed since it was selected; count the attempt so the loop stays bounded and pick again
                    removalCount++;
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
    private CacheEntry? CreateEntry<T>(T value, DateTime? expiresAt)
    {
        long size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
        if (size < 0)
            return null;

        return new CacheEntry(value, expiresAt, _timeProvider, _shouldClone, size);
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
        private readonly object? _cacheValue;
        private static long _instanceCount;
        private readonly bool _shouldClone;
        private readonly TimeProvider _timeProvider;

#if DEBUG
        private long _usageCount;
#endif

        public CacheEntry(object? value, DateTime? expiresAt, TimeProvider timeProvider, bool shouldClone = true, long size = 0)
        {
            _timeProvider = timeProvider;
            _shouldClone = shouldClone && TypeRequiresCloning(value?.GetType());
            _cacheValue = _shouldClone ? value.DeepClone() : value;
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
                LastAccessTicks = _timeProvider.GetUtcNow().Ticks;
#if DEBUG
                Interlocked.Increment(ref _usageCount);
#endif
                return PeekValue();
            }
        }

        /// <summary>
        /// Returns the value without recording an access, so diagnostic reads do not affect eviction order.
        /// </summary>
        internal object? PeekValue()
        {
            return _shouldClone ? _cacheValue.DeepClone() : _cacheValue;
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

        try
        {
            long size = _sizeCalculator!(value);

            // Validate the size returned by the calculator
            if (size < 0)
            {
                _logger.LogWarning("SizeCalculator returned negative size {Size} for type {EntryType}. Entry will not be cached. " +
                    "Custom SizeCalculator functions must return non-negative values.",
                    size, value?.GetType().Name ?? "null");
                return -1;
            }

            // Check if entry exceeds maximum size
            if (_maxEntrySize.HasValue && size > _maxEntrySize.Value)
            {
                if (_shouldThrowOnMaxEntrySizeExceeded)
                {
                    throw new MaxEntrySizeExceededCacheException(size, _maxEntrySize.Value, value?.GetType().Name ?? "null");
                }

                _logger.LogWarning("Cache entry size {EntrySize:N0} bytes exceeds maximum allowed size {MaxEntrySize:N0} bytes for type {EntryType}. Entry will not be cached.",
                    size, _maxEntrySize.Value, value?.GetType().Name ?? "null");

                return -1;
            }

            return size;
        }
        catch (MaxEntrySizeExceededCacheException)
        {
            throw; // Re-throw MaxEntrySizeExceededCacheException from size check
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating entry size for type {EntryType}, using fallback estimation",
                value?.GetType().Name ?? "null");

            // Fallback to simple estimation
            return value switch
            {
                null => 8,
                string stringValue => 24 + ((long)stringValue.Length * 2),
                _ => 64 // Default object overhead
            };
        }
    }

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

