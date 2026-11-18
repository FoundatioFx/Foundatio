using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly bool _usesFixedSizing;
    private readonly bool _shouldTrackMemory;
    private Func<object, long>? _sizeCalculator;
    private readonly long? _maxEntrySize;
    private readonly bool _shouldThrowOnMaxEntrySizeExceeded;
    private long _writes;
    private long _hits;
    private long _misses;
    private long _currentMemorySize;
    private readonly System.Threading.Lock _memorySizeLock = new();
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
        _usesFixedSizing = options.UsesFixedSizing;
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
    /// Recalculates the current memory size by summing all stored cache entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot and counter assignment are serialized with size-changing writes and clears,
    /// so a concurrent store cannot have its size counted twice or overwritten by the recount.
    /// </para>
    /// <para>
    /// Expired entries that are still stored are counted, because removing them later subtracts their size again.
    /// </para>
    /// </remarks>
    internal long RecalculateMemorySize()
    {
        if (!_shouldTrackMemory)
            return 0;

        lock (_memorySizeLock)
        {
            long totalSize = SumStoredEntrySizes();
            Interlocked.Exchange(ref _currentMemorySize, totalSize);
            return totalSize;
        }
    }

    private long SumStoredEntrySizes()
    {
        long total = 0;
        foreach (var entry in _memory.Values)
        {
            if (total > Int64.MaxValue - entry.Size)
                return Int64.MaxValue;

            total += entry.Size;
        }

        return total;
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

    /// <summary>
    /// Returns the live entries' values in eviction order, without recording an access.
    /// </summary>
    /// <remarks>
    /// Intended for diagnostics. Each read copies the entry table and, when <see cref="InMemoryCacheClientOptions.CloneValues"/>
    /// is set, deep-clones every value it returns.
    /// </remarks>
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

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("RemoveAsync: Removing key: {Key}", key);
        var removed = RemoveEntry(key);

        // Return false if the entry was expired (consistent with Redis behavior)
        return Task.FromResult(removed is { IsExpired: false });
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        bool success = UpdateEntry<bool>(key, current => current is not null && EqualityComparer<T>.Default.Equals(current.PeekValue<T>(), expected)
            ? (null, true)
            : (current, false));

        await StartMaintenanceAsync().AnyContext();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);
        return success;
    }

    public Task<int> RemoveAllAsync(IEnumerable<string>? keys = null)
    {
        if (keys is null)
            return Task.FromResult(ClearEntries());

        int removed = 0;
        foreach (string key in keys.Distinct())
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("RemoveAllAsync: Removing key: {Key}", key);
            if (RemoveEntry(key) is not null)
                removed++;
        }

        return Task.FromResult(removed);
    }

    private int ClearEntries()
    {
        lock (_memorySizeLock)
        {
            int count = _memory.Count;
            _memory.Clear();
            Interlocked.Exchange(ref _currentMemorySize, 0);
            return count;
        }
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

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Removing expired key: {Key}", key);
        if (RemoveEntry(key) is null)
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
    /// so it must not have side effects; only the result of the attempt that was stored is returned.
    /// </param>
    internal TResult UpdateEntry<TResult>(string key, Func<CacheEntry?, (CacheEntry? Entry, TResult Result)> update)
    {
        while (true)
        {
            _memory.TryGetValue(key, out var stored);
            bool reclaimedExpired = stored is { IsExpired: true };
            var (desired, result) = update(reclaimedExpired ? null : stored);
            if (!reclaimedExpired && stored is { IsExpired: true })
            {
                // Sizing, cloning or conversion can outlive the observed entry. Recompute from a missing key
                // instead of storing a decision that would revive its expired value.
                reclaimedExpired = true;
                (desired, result) = update(null);
            }

            if (!ReferenceEquals(desired, stored) && !TrySwapEntry(key, stored, desired))
                continue;

            if (reclaimedExpired)
                OnItemExpired(key);

            return result;
        }
    }

    /// <summary>
    /// Removes and returns the stored entry, including an expired entry, without raising an expiration event.
    /// </summary>
    private CacheEntry? RemoveEntry(string key)
    {
        while (_memory.TryGetValue(key, out var current))
        {
            if (TrySwapEntry(key, current, null))
                return current;
        }

        return null;
    }

    /// <summary>
    /// Swaps <paramref name="desired"/> in for <paramref name="current"/> with a compare-and-swap, adjusting
    /// tracked memory by the size difference. Returns <c>false</c>, changing nothing, if another writer got there first.
    /// </summary>
    private bool TrySwapEntry(string key, CacheEntry? current, CacheEntry? desired)
    {
        if (!_shouldTrackMemory || (current?.Size ?? 0) == (desired?.Size ?? 0))
            return TrySwapEntryCore(key, current, desired);

        if (!TrySwapEntry(key, current, desired, out var change))
            return false;

        LogMemorySizeChange(change);
        return true;
    }

    private readonly record struct MemorySizeChange(long Delta, long Before, long After, bool Overflowed);

    private bool TrySwapEntry(string key, CacheEntry? current, CacheEntry? desired, out MemorySizeChange change)
    {
        change = default;
        long delta = (desired?.Size ?? 0) - (current?.Size ?? 0);
        if (!_shouldTrackMemory || delta is 0)
            return TrySwapEntryCore(key, current, desired);

        long before;
        long after;
        bool overflowed;
        lock (_memorySizeLock)
        {
            if (!TrySwapEntryCore(key, current, desired))
                return false;

            before = _currentMemorySize;
            overflowed = delta > 0 && before > Int64.MaxValue - delta;
            // A saturated counter has lost the excess. Subtracting from it could undercount the remaining entries.
            if (before == Int64.MaxValue && delta < 0)
                after = SumStoredEntrySizes();
            else
                after = overflowed ? Int64.MaxValue : Math.Max(0, before + delta);
            Interlocked.Exchange(ref _currentMemorySize, after);
        }

        change = new(delta, before, after, overflowed);
        return true;
    }

    private void LogMemorySizeChange(MemorySizeChange change)
    {
        if (change.Delta is 0)
            return;

        if (change.Overflowed)
            _logger.LogWarning("Memory size counter would overflow, clamping to {MaxValue} (Current={Current}, Delta={Delta})",
                Int64.MaxValue, change.Before, change.Delta);

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("UpdateMemorySize: Delta={Delta}, Before={Before}, After={After}, Max={Max}",
                change.Delta, change.Before, change.After, _maxMemorySize);
    }

    private bool TrySwapEntryCore(string key, CacheEntry? current, CacheEntry? desired)
    {
        return (current, desired) switch
        {
            (null, not null) => _memory.TryAdd(key, desired),
            (not null, null) => _memory.TryRemove(new KeyValuePair<string, CacheEntry>(key, current)),
            _ => _memory.TryUpdate(key, desired!, current!)
        };
    }

    /// <summary>
    /// Removes the entry for <paramref name="key"/> only if it is still the exact instance that was observed,
    /// so an entry replaced concurrently is never removed by mistake.
    /// </summary>
    internal bool TryRemoveEntry(string key, CacheEntry observed)
    {
        return _memory.TryGetValue(key, out var current)
            && ReferenceEquals(current, observed)
            && TrySwapEntry(key, current, null);
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

            return (current, T.Zero);
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

        // A dictionary stored directly with SetAsync is readable as a list until a list update takes it over. Stored
        // payloads are never changed by the cache, so safe keys can be read without cloning the whole dictionary.
        if (entry is { IsExpired: false, StoredValue: Dictionary<T, DateTime?> stored } && stored.GetType() == typeof(Dictionary<T, DateTime?>)
            && (!_shouldClone || FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(T))))
        {
            Interlocked.Increment(ref _hits);
            entry.RecordAccess();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (page.HasValue)
                return CacheList<T>.ReadPage(stored, now, page.Value, pageSize);

            var keys = CacheList<T>.ReadValues(stored, now);
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
    /// Adds or removes list members. Writers to the same list take turns on the list's lock and store the resized
    /// entry before releasing it, so they never redo work for each other. The swap only fails when another kind of
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
                if (_usesFixedSizing)
                {
                    inputSize = CalculateEntrySize(items, typeof(Dictionary<T, DateTime?>));
                }
                else
                {
                    input = new Dictionary<T, DateTime?>(items.Count, items.Comparer);
                    foreach (var item in items)
                        input[item] = expiresAt;

                    inputSize = CalculateEntrySize(input);
                }

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
                long count;
                ListUpdateLog updateLog;
                lock (list.SyncRoot)
                {
                    if (!_memory.TryGetValue(key, out var latest) || !ReferenceEquals(latest, current) || current.IsExpired)
                        continue;

                    bool updated = list.CanUpdateInPlace
                        ? TryUpdateList(key, current, list, items, expiresAt, remove, out count, out updateLog)
                        : TryReplaceList(key, current, list, items, expiresAt, remove, out count, out updateLog);
                    if (!updated)
                        continue;
                }

                // A logger can write to the same key. Finish the journal and release the list lock first.
                LogMemorySizeChange(updateLog.SizeChange);
                LogExpiredListValues(key, updateLog.ExpiredMembers);
                return count;
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
                if (!TrySwapEntry(key, stored, null))
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
    /// the new list until it is stored, so it needs no lock and no rollback. Returns <c>false</c> when another
    /// writer changed the key first.
    /// </summary>
    private bool TryCreateList<T>(string key, CacheEntry? stored, CacheEntry? current, HashSet<T> items, DateTime? expiresAt, bool remove, Dictionary<T, DateTime?>? input, long inputSize, out long count) where T : notnull
    {
        CacheList<T> list;
        long size;
        if (current is null)
        {
            // With CloneValues the size calculator may keep the dictionary it was given, so it is never stored.
            list = input is not null && !_shouldClone ? CacheList<T>.Create(input) : CacheList<T>.Create(items, expiresAt);
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
            return ReferenceEquals(stored, current) || TrySwapEntry(key, stored, null);
        }

        CacheEntry? desired = list.Count is 0 ? null
            : current is null ? new CacheEntry(list, list.ExpiresAt, _timeProvider, _shouldClone, size, valueIsOwned: true)
            : current.WithValue(list, list.ExpiresAt, size);
        return TrySwapEntry(key, stored, desired);
    }

    private readonly record struct ListUpdateLog(MemorySizeChange SizeChange, int ExpiredMembers);

    /// <summary>
    /// Updates a stored list while holding its lock. Returns <c>false</c> when the entry was replaced before the
    /// update could be stored, after undoing the change.
    /// </summary>
    private bool TryUpdateList<T>(string key, CacheEntry current, CacheList<T> list, HashSet<T> items, DateTime? expiresAt, bool remove, out long count, out ListUpdateLog updateLog) where T : notnull
    {
        updateLog = default;
        bool changed;
        int expired;
        try
        {
            count = list.Apply(items, expiresAt, _timeProvider.GetUtcNow().UtcDateTime, remove, out changed, out expired);
        }
        catch
        {
            list.Rollback();
            throw;
        }

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

            // Sizing can outlive the entry; start over from a missing key so it is reclaimed rather than revived.
            if (current.IsExpired)
            {
                list.Rollback();
                return false;
            }

            if (size < 0 || !remove && IsInputExpired(expiresAt))
            {
                list.Rollback();
                count = 0;
                return true;
            }

            desired = current.WithValue(list, list.ExpiresAt, size);
        }

        if (!TrySwapEntry(key, current, desired, out var sizeChange))
        {
            list.Rollback();
            return false;
        }

        list.Commit();
        updateLog = new(sizeChange, expired);
        return true;
    }

    /// <summary>
    /// Updates a stored list through a private candidate, for comparers that run caller code and for updates
    /// nested inside another update of the same list. Nothing changes until the candidate is stored, so a
    /// throwing comparer or a rejected update leaves the list exactly as it was. Returns <c>false</c> when the entry
    /// was replaced or expired first.
    /// </summary>
    private bool TryReplaceList<T>(string key, CacheEntry current, CacheList<T> list, HashSet<T> items, DateTime? expiresAt, bool remove, out long count, out ListUpdateLog updateLog) where T : notnull
    {
        updateLog = default;
        var candidate = list.CreateCandidate();
        count = candidate.Apply(items, expiresAt, _timeProvider.GetUtcNow().UtcDateTime, remove, out bool changed, out int expired);
        candidate.Commit();
        if (!changed)
        {
            if (remove)
                count = 0;
            return true;
        }

        long size = candidate.Count > 0 ? CalculateListSize(candidate) : 0;
        if (current.IsExpired)
            return false;

        if (size < 0 || !remove && IsInputExpired(expiresAt))
        {
            count = 0;
            return true;
        }

        var desired = candidate.Count is 0 ? null : current.WithValue(candidate, candidate.ExpiresAt, size);
        if (!TrySwapEntry(key, current, desired, out var sizeChange))
            return false;

        updateLog = new(sizeChange, expired);
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

        if (_usesFixedSizing)
            return CalculateEntrySize(list);

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

        if (wasUpdated && _logger.IsEnabled(LogLevel.Trace))
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

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        Interlocked.Increment(ref _writes);

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;

        // Build the replacement only once the expected value matches, so a mismatch never pays for (or throws
        // from) sizing and cloning the new value. It is built at most once, even when the update retries.
        CacheEntry? replacement = null;
        bool success = UpdateEntry<bool>(key, current =>
        {
            if (current is null || !EqualityComparer<T>.Default.Equals(current.PeekValue<T>(), expected))
                return (current, false);

            // A lease that expires while it is being sized is reclaimed too, so it is never revived
            replacement ??= CreateEntry(value, expiresAt);
            return replacement is null || replacement.IsExpired ? (current, false) : (replacement, true);
        });

        await StartMaintenanceAsync().AnyContext();

        if (_logger.IsEnabled(LogLevel.Trace))
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
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("CompactAsync called. ShouldCompact={ShouldCompact}, CurrentMemory={CurrentMemory}, MaxMemory={MaxMemory}, Count={Count}",
                ShouldCompact, _currentMemorySize, _maxMemorySize, _memory.Count);
        }

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

            double itemRatio = _maxItems is > 0 ? (double)_memory.Count / _maxItems.Value : 0;
            double memoryRatio = _maxMemorySize is > 0 ? (double)_currentMemorySize / _maxMemorySize.Value : 0;
            double factor = Math.Ceiling(Math.Max(itemRatio, memoryRatio));
            int maxRemovals = (int)Math.Clamp(baseMaxRemovals * factor, baseMaxRemovals, absoluteMaxRemovals);

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

                if (!ShouldCompact)
                    break;

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Removing cache entry {Key} due to cache exceeding limit (Items: {ItemCount}/{MaxItems}, Memory: {MemorySize:N0}/{MaxMemorySize:N0})",
                        entryToRemove.Key, _memory.Count, _maxItems, _currentMemorySize, _maxMemorySize);
                }

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
                _logger.LogDebug("CompactAsync: Reached max removals ({MaxRemovals}) but still over limit (Items: {ItemCount}/{MaxItems}, Memory: {MemorySize:N0}/{MaxMemorySize:N0}), retrying on next maintenance cycle",
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

        if (_logger.IsEnabled(LogLevel.Trace))
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

        if (_logger.IsEnabled(LogLevel.Trace))
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

                if (_logger.IsEnabled(LogLevel.Debug))
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
        ClearEntries();
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

    private static IDictionary<TKey, TValue> CloneDictionaryWithComparer<TKey, TValue>(IDictionary<TKey, TValue> dictionary) where TKey : notnull
    {
        var clone = FastCloner.FastCloner.DeepCloneTo(dictionary, FastCloner.FastCloner.ShallowClone(dictionary))!;
        RehashClonedDictionary(clone);
        return clone;
    }

    private static void RehashClonedDictionary<TKey, TValue>(IDictionary<TKey, TValue> clone) where TKey : notnull
    {
        var entries = clone.ToArray();
        // Rebuild under the cloned comparer while keeping dictionary, comparer and cached views in one graph.
        clone.Clear();
        if (clone is Dictionary<TKey, TValue> values && values.GetType() == typeof(Dictionary<TKey, TValue>)
            && entries.Length <= values.EnsureCapacity(0) / 2)
            values.TrimExcess(entries.Length);
        foreach (var entry in entries)
            clone.Add(entry.Key, entry.Value);
    }

    /// <summary>
    /// A cache entry's value, expiration and size never change after it is stored in the cache dictionary;
    /// changes store a copy (<see cref="WithValue"/>, <see cref="WithExpiration"/>). This lets conditional removals
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
        // Larger dictionaries take the copy constructor to avoid unpooled tracking; this affects speed only.
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
            _cloneDictionaryKeys = _shouldClone && RequiresDictionaryKeyCloning(GetPayloadType(value));
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
                : RequiresDictionaryKeyCloning(GetPayloadType(cacheValue)));
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
            if (_cacheValue is ICacheList list)
                return _shouldClone ? list.Clone(_cloneDictionaryKeys) : list.Expose();

            return _shouldClone ? CloneValue(_cacheValue, _cloneDictionaryKeys) : _cacheValue;
        }

        /// <summary>Lists are read as dictionaries, so they clone like the dictionary type they return.</summary>
        private static Type? GetPayloadType(object? value) => value is ICacheList list ? list.DictionaryType : value?.GetType();

        internal static bool RequiresDictionaryKeyCloning(Type? type)
        {
            if (type is null || !type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>))
                return false;

            var keyType = type.GenericTypeArguments[0];
            return !FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(keyType);
        }

        /// <summary>
        /// Deep clones a cached value for <see cref="InMemoryCacheClientOptions.CloneValues"/>. Dictionaries take a
        /// dedicated path instead of plain <c>DeepClone()</c>; every other value is cloned with <c>DeepClone()</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The dedicated path works around bugs in the vendored FastCloner (3.5.3). They are also on <c>main</c>, which
        /// uses plain <c>DeepClone()</c>, and all but the default-hash struct key case are still in upstream 3.5.6.
        /// Most only appear after the first clone of a type in a process, because that first clone is reference tracked.
        /// </para>
        /// <list type="bullet">
        /// <item><b>Cached views.</b> A <see cref="Dictionary{TKey,TValue}"/> over 32 entries keeps its cached
        /// <c>Keys</c> and <c>Values</c> bound to the source, so the clone's <c>Keys</c> reflect later changes to the
        /// caller's dictionary. Comparers or keys that reference the dictionary also point at the source.
        /// <c>DeepCloneTo(dictionary, ShallowClone(dictionary))</c> tracks the root so they all point at the clone.</item>
        /// <item><b>Stale hashes.</b> Copied buckets keep the source's hash codes. When the comparer is cloned (an
        /// identity-based comparer, <see cref="ReferenceEqualityComparer"/>) or the keys are cloned (value types that
        /// hold references), lookups miss. Those dictionaries are rebuilt: <paramref name="forceDictionaryRebuild"/> and
        /// <see cref="RehashClonedDictionary{TKey,TValue}"/>.</item>
        /// <item><b>Crash.</b> Cloning a <see cref="ConcurrentDictionary{TKey,TValue}"/> overflows the stack, so it is
        /// copied with its constructor.</item>
        /// </list>
        /// <para>
        /// Two parts exist only for speed. <see cref="IsSafeComparer{TKey}"/> shares the stateless framework comparers
        /// instead of copying them; a copy forces a rebuild, which doubles the allocation. Dictionaries with immutable
        /// keys and values use the copy constructor, which skips unpooled reference tracking and doesn't copy unused
        /// capacity: about 85 µs and 364 KB for 10,000 entries, against about 125 µs and 823 KB.
        /// </para>
        /// <para>
        /// The key and value types are only known at runtime, so the generic cloner is created with
        /// <c>MakeGenericMethod</c> and cached per type. Without dynamic code (Native AOT) it falls back to
        /// <c>DeepClone()</c>, which is what <c>main</c> does.
        /// </para>
        /// <para>
        /// Upstream issues: <see href="https://github.com/lofcz/FastCloner/issues/62">ConcurrentDictionary stack overflow</see>,
        /// <see href="https://github.com/lofcz/FastCloner/issues/63">cached Keys/Values views</see>,
        /// <see href="https://github.com/lofcz/FastCloner/issues/64">stateful comparers and back-references</see>,
        /// <see href="https://github.com/lofcz/FastCloner/issues/65">shared value-type keys</see> and
        /// <see href="https://github.com/lofcz/FastCloner/issues/66">unused capacity and default comparers</see>.
        /// </para>
        /// <para>
        /// Replace this method with <c>value.DeepClone()</c> once FastCloner (1) tracks the root dictionary on every
        /// clone, (2) rebuilds buckets when it clones keys or comparers and deep clones value-type keys of small
        /// dictionaries, and (3) clones <see cref="ConcurrentDictionary{TKey,TValue}"/> without overflowing. If it also
        /// shares <see cref="EqualityComparer{T}.Default"/> and <see cref="Comparer{T}.Default"/>, the speed-only parts
        /// can go at the cost noted above. The regression tests are <c>SetAsync_WithCachedDictionaryViews_*</c>,
        /// <c>GetAsync_WithCachedDictionaryViews_*</c>, <c>GetAsync_WithInstanceHashComparer_*</c>,
        /// <c>GetAsync_WithReferenceHashedKeys_*</c>, <c>ListAddAsync_WithReferenceHoldingStructs_*</c> and
        /// <c>ListAddAsync_WithCustomComparer_*</c>.
        /// </para>
        /// </remarks>
        internal static object? CloneValue(object? value, bool forceDictionaryRebuild)
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

            // The key and value types are only known at runtime here. Without dynamic code the vendored cloner,
            // which instantiates the same dictionary types itself, handles the value on its own.
            if (!RuntimeFeature.IsDynamicCodeSupported)
                return value.DeepClone();

            if (!_dictionaryCloners.TryGetValue(type, out var cloner))
                cloner = _dictionaryCloners.GetOrAdd(type, CreateDictionaryCloner(type));
            return cloner(value, forceDictionaryRebuild);
        }

        [RequiresDynamicCode("Instantiates CloneDictionary for the runtime key and value types.")]
        private static Func<object, bool, object> CreateDictionaryCloner(Type dictionaryType)
        {
            return typeof(CacheEntry)
                .GetMethod(nameof(CloneDictionary), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(dictionaryType.GenericTypeArguments)
                .CreateDelegate<Func<object, bool, object>>();
        }

        /// <summary>
        /// Whether a comparer can be shared with a clone. The default comparers keep no state, but the vendored cloner
        /// does not know all of their framework types and copies them, which would make every later clone rebuild.
        /// </summary>
        private static bool IsSafeComparer<TKey>(object comparer)
        {
            var type = comparer.GetType();
            return type == EqualityComparer<TKey>.Default.GetType()
                || type == Comparer<TKey>.Default.GetType()
                || FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(type);
        }

        private static object CloneDictionary<TKey, TValue>(object value, bool forceDictionaryRebuild) where TKey : notnull
        {
            if (value is ConcurrentDictionary<TKey, TValue> concurrentValues
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(TKey))
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(TValue))
                && IsSafeComparer<TKey>(concurrentValues.Comparer))
                return new ConcurrentDictionary<TKey, TValue>(concurrentValues.ToArray(), concurrentValues.Comparer);

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
                return comparer is not null && !IsSafeComparer<TKey>(comparer)
                    ? CloneDictionaryWithComparer((IDictionary<TKey, TValue>)value)
                    : value.DeepClone();
            }

            int capacity = dictionary.EnsureCapacity(0);
            // Each path below is correct at any size. These limits mirror the vendored cloner only to pick the
            // cheapest path, so re-measure them after updating FastCloner.
            if ((dictionary.Count <= 32 || dictionary.Count <= capacity / 2 || capacity > MaxPooledDictionaryCloneCapacity)
                && !forceDictionaryRebuild
                && FastCloner.Code.FastClonerSafeTypes.CanReturnSameObject(typeof(TValue))
                && IsSafeComparer<TKey>(dictionary.Comparer))
                return new Dictionary<TKey, TValue>(dictionary, dictionary.Comparer);

            // The vendored cloner rebuilds small dictionaries, preserving their comparer and discarding
            // cached views and unused capacity. Mutable keys or comparers need the tracked adapter below.
            if (!forceDictionaryRebuild && dictionary.Count <= 32
                && IsSafeComparer<TKey>(dictionary.Comparer))
                return dictionary.DeepClone();

            // Track the dictionary root so cached Keys and Values views belong to the clone.
            var clone = FastCloner.FastCloner.DeepCloneTo(dictionary, FastCloner.FastCloner.ShallowClone(dictionary))!;
            if (!forceDictionaryRebuild && (ReferenceEquals(dictionary.Comparer, clone.Comparer) || IsSafeComparer<TKey>(dictionary.Comparer)))
                return clone;

            RehashClonedDictionary(clone);
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

        [return: MaybeNull]
        public T GetValue<T>()
        {
            RecordAccess();
            return PeekValue<T>();
        }

        /// <summary>
        /// Converts the value like <see cref="GetValue{T}"/> without recording an access, so comparisons made while
        /// deciding a conditional write do not change eviction order.
        /// </summary>
        [return: MaybeNull]
        internal T PeekValue<T>()
        {
            object? val = PeekValue();
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
    /// <param name="entryType">The logical value type used in size validation diagnostics.</param>
    /// <returns>The calculated size in bytes, or -1 if the entry should be skipped.</returns>
    /// <exception cref="MaxEntrySizeExceededCacheException">Thrown when entry exceeds MaxEntrySize and ShouldThrowOnMaxEntrySizeExceeded is true.</exception>
    private long CalculateEntrySize(object? value, Type? entryType = null)
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
                entryType?.Name ?? GetEntryTypeName(value));

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
            _logger.LogWarning("SizeCalculator returned negative size {Size} for type {EntryType}, skipping entry because custom SizeCalculator functions must return non-negative values",
                size, entryType?.Name ?? GetEntryTypeName(value));
            return -1;
        }

        if (_maxEntrySize.HasValue && size > _maxEntrySize.Value)
        {
            if (_shouldThrowOnMaxEntrySizeExceeded)
                throw new MaxEntrySizeExceededCacheException(size, _maxEntrySize.Value, entryType?.Name ?? GetEntryTypeName(value));

            _logger.LogWarning("Cache entry size {EntrySize:N0} bytes exceeds maximum allowed size {MaxEntrySize:N0} bytes for type {EntryType}, skipping entry",
                size, _maxEntrySize.Value, entryType?.Name ?? GetEntryTypeName(value));
            return -1;
        }

        return size;
    }

    private static string GetEntryTypeName(object? value) => value is ICacheList list ? list.DictionaryType.Name : value?.GetType().Name ?? "null";

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
