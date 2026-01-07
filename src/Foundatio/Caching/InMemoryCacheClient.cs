using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private Func<object, long> _sizeCalculator;
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

    public InMemoryCacheClient() : this(o => o)
    {
    }

    public InMemoryCacheClient(InMemoryCacheClientOptions options = null)
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
        _resiliencePolicyProvider = options.ResiliencePolicyProvider;
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
            do
            {
                currentValue = _currentMemorySize;
                if (currentValue > Int64.MaxValue - delta)
                {
                    newValue = Int64.MaxValue;
                    _logger.LogWarning("Memory size counter would overflow. Clamping to {MaxValue}. Current={Current}, Delta={Delta}",
                        Int64.MaxValue, currentValue, delta);
                }
                else
                {
                    newValue = currentValue + delta;
                }
            } while (Interlocked.CompareExchange(ref _currentMemorySize, newValue, currentValue) != currentValue);
        }

        _logger.LogTrace("UpdateMemorySize: Delta={Delta}, Before={Before}, After={After}, Max={Max}",
            delta, currentValue, newValue, _maxMemorySize);
    }

    /// <summary>
    /// Updates memory size when an entry is added or updated.
    /// Calculates the size delta based on old and new entry sizes.
    /// </summary>
    private void UpdateMemorySizeForEntry(long newSize, long oldSize, bool wasNewEntry)
    {
        if (!_shouldTrackMemory)
            return;

        long sizeDelta = wasNewEntry ? newSize : (newSize - oldSize);
        UpdateMemorySize(sizeDelta);
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
    /// Known limitation: If an entry's value is updated during iteration (via the Value setter
    /// which resets the estimated size), the calculation could include a mix of old and newly-calculated
    /// sizes. This is acceptable for approximate memory tracking. To reduce this window, a snapshot
    /// of values is taken at the start of the iteration.
    /// </para>
    /// <para>
    /// Expired entries are excluded from the calculation to ensure accurate memory reporting.
    /// </para>
    /// </remarks>
    private long RecalculateMemorySize()
    {
        if (!_shouldTrackMemory) return 0;

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
        if (ItemExpired == null)
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

    public ICollection<KeyValuePair<string, object>> Items
    {
        get
        {
            return _memory.ToArray()
                .Where(kvp => !kvp.Value.IsExpired)
                .OrderBy(kvp => kvp.Value.LastAccessTicks)
                .ThenBy(kvp => kvp.Value.InstanceNumber)
                .Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value))
                .ToList();
        }
    }

    public Task<bool> RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _logger.LogTrace("RemoveAsync: Removing key: {Key}", key);
        if (!_memory.TryRemove(key, out var entry))
        {
            return Task.FromResult(false);
        }

        // Key was found and removed. Update memory size.
        UpdateMemorySize(-entry.Size);

        // Return false if the entry was expired (consistent with Redis behavior)
        return Task.FromResult(!entry.IsExpired);
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        bool wasExpectedValue = false;
        bool success = _memory.TryUpdate(key, (existingKey, existingEntry) =>
        {
            var currentValue = existingEntry.GetValue<T>();
            if (currentValue.Equals(expected))
            {
                _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Updating ExpiresAt to DateTime.MinValue", existingKey);
                existingEntry.ExpiresAt = DateTime.MinValue;
                wasExpectedValue = true;
            }

            return existingEntry;
        });

        success = success && wasExpectedValue;

        await StartMaintenanceAsync().AnyContext();

        _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);
        return success;
    }

    public Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        if (keys == null)
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
            if (_memory.TryRemove(key, out var removedEntry))
            {
                removed++;
                if (removedEntry != null)
                    UpdateMemorySize(-removedEntry.Size);
            }
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
        if (_memory.TryRemove(key, out var removedEntry))
        {
            // Update memory size tracking
            UpdateMemorySize(-removedEntry.Size);

            OnItemExpired(key, sendNotification);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Used by the maintenance task to remove expired keys.
    /// </summary>
    private long RemoveKeyIfExpired(string key, bool sendNotification = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_memory.TryGetValue(key, out var existingEntry) && existingEntry.IsExpired)
        {
            if (_memory.TryRemove(key, out var removedEntry))
            {
                if (!removedEntry.IsExpired)
                    throw new Exception("Removed item was not expired");

                // Update memory size tracking
                UpdateMemorySize(-removedEntry.Size);

                _logger.LogDebug("Removing expired cache entry {Key}", key);
                OnItemExpired(key, sendNotification);
                return 1;
            }
        }

        return 0;
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
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));

        var map = new Dictionary<string, CacheValue<T>>();
        foreach (string key in keys)
            map[key] = await GetAsync<T>(key);

        return map;
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
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

        if (expiresIn is { Ticks: <= 0 })
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

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return 0;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(value, expiresAt);
        if (newEntry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        double difference = value;
        long oldSize = 0;
        long newSize = 0;
        bool wasNewEntry = false;
        _memory.AddOrUpdate(key, _ =>
        {
            wasNewEntry = true;
            newSize = newEntry.Size;
            return newEntry;
        }, (_, existingEntry) =>
        {
            oldSize = existingEntry.Size;
            double? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<double?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type: {Message}", ex.Message);
            }

            if (currentValue.HasValue && currentValue.Value < value)
            {
                difference = value - currentValue.Value;
                existingEntry.Value = value;
                // Recalculate size after value change
                existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
            }
            else
            {
                difference = 0;
            }

            existingEntry.ExpiresAt = expiresAt;

            newSize = existingEntry.Size;
            return existingEntry;
        });

        UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return 0;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(value, expiresAt);
        if (newEntry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        long difference = value;
        long oldSize = 0;
        long newSize = 0;
        bool wasNewEntry = false;
        _memory.AddOrUpdate(key, _ =>
        {
            wasNewEntry = true;
            newSize = newEntry.Size;
            return newEntry;
        }, (_, existingEntry) =>
        {
            oldSize = existingEntry.Size;
            long? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<long?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type");
            }

            if (currentValue.HasValue && currentValue.Value < value)
            {
                difference = value - currentValue.Value;
                existingEntry.Value = value;
                existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
            }
            else
            {
                difference = 0;
            }

            existingEntry.ExpiresAt = expiresAt;

            newSize = existingEntry.Size;
            return existingEntry;
        });

        UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return 0;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(value, expiresAt);
        if (newEntry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        double difference = value;
        long oldSize = 0;
        long newSize = 0;
        bool wasNewEntry = false;
        _memory.AddOrUpdate(key, _ =>
        {
            wasNewEntry = true;
            newSize = newEntry.Size;
            return newEntry;
        }, (_, existingEntry) =>
        {
            oldSize = existingEntry.Size;
            double? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<double?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type");
            }

            if (currentValue.HasValue && currentValue.Value > value)
            {
                difference = currentValue.Value - value;
                existingEntry.Value = value;
                existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
            }
            else
            {
                difference = 0;
            }

            existingEntry.ExpiresAt = expiresAt;

            newSize = existingEntry.Size;
            return existingEntry;
        });

        UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return 0;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(value, expiresAt);
        if (newEntry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        long difference = value;
        long oldSize = 0;
        long newSize = 0;
        bool wasNewEntry = false;
        _memory.AddOrUpdate(key, _ =>
        {
            wasNewEntry = true;
            newSize = newEntry.Size;
            return newEntry;
        }, (_, existingEntry) =>
        {
            oldSize = existingEntry.Size;
            long? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<long?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type");
            }

            if (currentValue.HasValue && currentValue.Value > value)
            {
                difference = currentValue.Value - value;
                existingEntry.Value = value;
                existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
            }
            else
            {
                difference = 0;
            }

            existingEntry.ExpiresAt = expiresAt;

            newSize = existingEntry.Size;
            return existingEntry;
        });

        UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        if (expiresIn is { Ticks: <= 0 })
        {
            await ListRemoveAsync(key, values).AnyContext();
            return 0;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime? expiresAt = expiresIn.HasValue ? utcNow.SafeAdd(expiresIn.Value) : null;

        long oldSize = 0;
        long newSize = 0;
        bool wasNewEntry = false;

        if (values is string stringValue)
        {
            var items = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
            {
                { stringValue, expiresAt }
            };

            var entry = CreateEntry(items, expiresAt);
            if (entry is null)
                return 0;

            Interlocked.Increment(ref _writes);

            _memory.AddOrUpdate(key, k =>
            {
                wasNewEntry = true;
                newSize = entry.Size;
                return entry;
            }, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is not IDictionary<string, DateTime?> dictionary)
                    throw new InvalidOperationException($"Unable to add value for key: {existingKey}. Cache value does not contain a dictionary");

                oldSize = existingEntry.Size;

                ExpireListValues(dictionary, existingKey);

                dictionary[stringValue] = expiresAt;
                existingEntry.Value = dictionary;
                existingEntry.ExpiresAt = dictionary.Values.Contains(null) ? null : dictionary.Values.Max();
                existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;

                newSize = existingEntry.Size;
                return existingEntry;
            });

            UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

            await StartMaintenanceAsync().AnyContext();
            return items.Count;
        }
        else
        {
            var items = new HashSet<T>(values.Where(v => v is not null)).ToDictionary(k => k, _ => expiresAt);
            if (items.Count == 0)
                return 0;

            var entry = CreateEntry(items, expiresAt);
            if (entry is null)
                return 0;

            Interlocked.Increment(ref _writes);

            _memory.AddOrUpdate(key, k =>
            {
                wasNewEntry = true;
                newSize = entry.Size;
                return entry;
            }, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is not IDictionary<T, DateTime?> dictionary)
                    throw new InvalidOperationException($"Unable to add value for key: {existingKey}. Cache value does not contain a set");

                oldSize = existingEntry.Size;

                ExpireListValues(dictionary, existingKey);

                foreach (var kvp in items)
                    dictionary[kvp.Key] = kvp.Value;

                existingEntry.Value = dictionary;
                existingEntry.ExpiresAt = dictionary.Values.Contains(null) ? null : dictionary.Values.Max();
                existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;

                newSize = existingEntry.Size;
                return existingEntry;
            });

            UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

            await StartMaintenanceAsync().AnyContext();
            return items.Count;
        }
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

    public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        Interlocked.Increment(ref _writes);

        long removed = 0;
        long oldSize = 0;
        long newSize = 0;

        if (values is string stringValue)
        {
            var items = new HashSet<string>([stringValue]);
            _memory.TryUpdate(key, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is IDictionary<string, DateTime?> { Count: > 0 } dictionary)
                {
                    oldSize = existingEntry.Size;

                    int expired = ExpireListValues(dictionary, existingKey);

                    foreach (string value in items)
                    {
                        if (dictionary.Remove(value))
                            Interlocked.Increment(ref removed);
                    }

                    if (expired > 0 || removed > 0)
                    {
                        existingEntry.Value = dictionary;
                        if (dictionary.Count > 0)
                            existingEntry.ExpiresAt = dictionary.Values.Contains(null) ? null : dictionary.Values.Max();
                        else
                            existingEntry.ExpiresAt = DateTime.MinValue;

                        existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;
                        newSize = existingEntry.Size;
                    }
                    else
                    {
                        newSize = oldSize;
                    }
                }

                if (removed > 0)
                    _logger.LogTrace("Removed value from set with cache key: {Key}", existingKey);

                return existingEntry;
            });

            if (_shouldTrackMemory && oldSize != newSize)
                UpdateMemorySize(newSize - oldSize);

            return Task.FromResult(removed);
        }
        else
        {
            var items = new HashSet<T>(values.Where(v => v is not null));
            if (items.Count == 0)
                return Task.FromResult<long>(0);

            _memory.TryUpdate(key, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is IDictionary<T, DateTime?> { Count: > 0 } dictionary)
                {
                    oldSize = existingEntry.Size;

                    int expired = ExpireListValues(dictionary, existingKey);

                    foreach (var value in items)
                    {
                        if (dictionary.Remove(value))
                            Interlocked.Increment(ref removed);
                    }

                    if (expired > 0 || removed > 0)
                    {
                        existingEntry.Value = dictionary;
                        if (dictionary.Count > 0)
                            existingEntry.ExpiresAt = dictionary.Values.Contains(null) ? null : dictionary.Values.Max();
                        else
                            existingEntry.ExpiresAt = DateTime.MinValue;

                        existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(dictionary) : 0;
                        newSize = existingEntry.Size;
                    }
                    else
                    {
                        newSize = oldSize;
                    }
                }

                if (removed > 0)
                    _logger.LogTrace("Removed value from set with cache key: {Key}", existingKey);

                return existingEntry;
            });

            if (_shouldTrackMemory && oldSize != newSize)
                UpdateMemorySize(newSize - oldSize);

            return Task.FromResult(removed);
        }
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
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

        bool wasUpdated = true;
        CacheEntry oldEntry = null;

        if (addOnly)
        {
            _memory.AddOrUpdate(key, entry, (existingKey, existingEntry) =>
            {
                // NOTE: This update factory method will run multiple times if the key is already in the cache, especially during lock contention.
                wasUpdated = false;

                // check to see if existing entry is expired
                if (existingEntry.IsExpired)
                {
                    _logger.LogTrace("Attempting to replacing expired cache key: {Key}", existingKey);

                    oldEntry = existingEntry;
                    wasUpdated = true;
                    return entry;
                }

                return existingEntry;
            });

            if (wasUpdated)
                _logger.LogTrace("Added cache key: {Key}", key);
        }
        else if (_shouldTrackMemory)
        {
            // Need to capture oldEntry for memory tracking
            _memory.AddOrUpdate(key, entry, (_, existingEntry) =>
            {
                oldEntry = existingEntry;
                return entry;
            });
            _logger.LogTrace("Set cache key: {Key}", key);
        }
        else
        {
            // Fast path: no memory tracking needed
            _memory.AddOrUpdate(key, entry, (_, _) => entry);
            _logger.LogTrace("Set cache key: {Key}", key);
        }

        // Update memory size tracking (size was pre-calculated and stored in entry.Size)
        if (_shouldTrackMemory)
        {
            long sizeDelta = entry.Size - (oldEntry?.Size ?? 0);
            if (wasUpdated && sizeDelta != 0)
                UpdateMemorySize(sizeDelta);
        }

        // Check if compaction is needed AFTER memory size update
        await StartMaintenanceAsync(ShouldCompact).AnyContext();
        return wasUpdated;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is 0)
            return 0;

        if (expiresIn?.Ticks <= 0)
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

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return false;
        }

        _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        Interlocked.Increment(ref _writes);

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        bool wasExpectedValue = false;
        long oldSize = 0;
        long newSize = 0;
        bool success = _memory.TryUpdate(key, (_, existingEntry) =>
        {
            var currentValue = existingEntry.GetValue<T>();
            if (currentValue.Equals(expected))
            {
                oldSize = existingEntry.Size;
                existingEntry.Value = value;
                existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
                wasExpectedValue = true;
                newSize = existingEntry.Size;

                existingEntry.ExpiresAt = expiresAt;
            }

            return existingEntry;
        });

        success = success && wasExpectedValue;

        // Update memory size tracking if the value was replaced
        if (_shouldTrackMemory && wasExpectedValue && oldSize != newSize)
            UpdateMemorySize(newSize - oldSize);

        await StartMaintenanceAsync().AnyContext();

        _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

        return success;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            RemoveExpiredKey(key);
            return 0;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(amount, expiresAt);
        if (newEntry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        long oldSize = 0;
        long newSize = 0;
        bool wasNewEntry = false;
        var result = _memory.AddOrUpdate(key, _ =>
        {
            wasNewEntry = true;
            newSize = newEntry.Size;
            return newEntry;
        }, (_, existingEntry) =>
        {
            oldSize = existingEntry.Size;
            double? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<double?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type: {Message}", ex.Message);
            }

            if (currentValue.HasValue)
                existingEntry.Value = currentValue.Value + amount;
            else
                existingEntry.Value = amount;

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            existingEntry.Size = _hasSizeCalculator ? CalculateEntrySize(existingEntry.Value) : 0;
            newSize = existingEntry.Size;
            return existingEntry;
        });

        UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

        await StartMaintenanceAsync().AnyContext();

        return result.GetValue<double>();
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            RemoveExpiredKey(key);
            return 0;
        }

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var newEntry = CreateEntry(amount, expiresAt);
        if (newEntry is null)
            return 0;

        Interlocked.Increment(ref _writes);

        long oldSize = 0;
        long newSize = 0;
        bool wasNewEntry = false;
        var result = _memory.AddOrUpdate(key, _ =>
        {
            wasNewEntry = true;
            newSize = newEntry.Size;
            return newEntry;
        }, (_, existingEntry) =>
        {
            oldSize = existingEntry.Size;
            long? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<long?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type: {Message}", ex.Message);
            }

            if (currentValue.HasValue)
                existingEntry.Value = currentValue.Value + amount;
            else
                existingEntry.Value = amount;

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            newSize = existingEntry.Size;
            return existingEntry;
        });

        UpdateMemorySizeForEntry(newSize, oldSize, wasNewEntry);

        await StartMaintenanceAsync().AnyContext();

        return result.GetValue<long>();
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
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));

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

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = utcNow.SafeAdd(expiresIn);
        if (expiresAt < utcNow)
        {
            RemoveExpiredKey(key);
            return;
        }

        if (_memory.TryGetValue(key, out var existingEntry) && existingEntry.ExpiresAt != expiresAt)
        {
            Interlocked.Increment(ref _writes);
            existingEntry.ExpiresAt = expiresAt;
            await StartMaintenanceAsync().AnyContext();
        }
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

            if (!_memory.TryGetValue(kvp.Key, out var existingEntry))
                continue;

            if (kvp.Value is null)
            {
                if (existingEntry.ExpiresAt is null)
                    continue;

                Interlocked.Increment(ref _writes);
                existingEntry.ExpiresAt = null;
                updated++;
            }
            else
            {
                var expiresAt = utcNow.SafeAdd(kvp.Value.Value);
                if (expiresAt < utcNow)
                {
                    RemoveExpiredKey(kvp.Key);
                }
                else if (existingEntry.ExpiresAt != expiresAt)
                {
                    Interlocked.Increment(ref _writes);
                    existingEntry.ExpiresAt = expiresAt;
                    updated++;
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

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (compactImmediately)
            await CompactAsync().AnyContext();

        if (TimeSpan.FromMilliseconds(250) < utcNow - _lastMaintenance)
        {
            _lastMaintenance = utcNow;
            _ = Task.Run(DoMaintenanceAsync);
        }
    }

    private bool ShouldCompact => (_maxItems.HasValue && _memory.Count > _maxItems) || (_shouldTrackMemory && _currentMemorySize > _maxMemorySize);

    private async Task CompactAsync()
    {
        _logger.LogTrace("CompactAsync called. ShouldCompact={ShouldCompact}, CurrentMemory={CurrentMemory}, MaxMemory={MaxMemory}, Count={Count}",
            ShouldCompact, _currentMemorySize, _maxMemorySize, _memory.Count);

        if (!ShouldCompact)
            return;

        _logger.LogTrace("CompactAsync: Compacting cache");

        var expiredKeys = new List<string>();
        using (await _lock.LockAsync().AnyContext())
        {
            int removalCount = 0;
            const int maxRemovals = 10; // Safety limit to prevent infinite loops

            while (ShouldCompact && removalCount < maxRemovals)
            {
                // Check if we still need compaction
                bool needsItemCompaction = _maxItems.HasValue && _memory.Count > _maxItems;
                bool needsMemoryCompaction = _shouldTrackMemory && _currentMemorySize > _maxMemorySize;

                if (!needsItemCompaction && !needsMemoryCompaction)
                    break;

                // For memory compaction, prefer size-aware eviction
                // For item compaction, prefer traditional LRU
                string keyToRemove = needsMemoryCompaction ? FindWorstSizeToUsageRatio() : FindLeastRecentlyUsed();

                if (keyToRemove == null)
                    break;

                _logger.LogDebug("Removing cache entry {Key} due to cache exceeding limit (Items: {ItemCount}/{MaxItems}, Memory: {MemorySize:N0}/{MaxMemorySize:N0})",
                    keyToRemove, _memory.Count, _maxItems, _currentMemorySize, _maxMemorySize);

                if (_memory.TryRemove(keyToRemove, out var cacheEntry))
                {
                    // Update memory size tracking
                    if (cacheEntry != null)
                        UpdateMemorySize(-cacheEntry.Size);

                    if (cacheEntry is { IsExpired: true })
                        expiredKeys.Add(keyToRemove);

                    removalCount++;
                }
                else
                {
                    // Couldn't remove the item, break to prevent infinite loop
                    break;
                }
            }
        }

        // Notify about expired items
        foreach (string expiredKey in expiredKeys)
            OnItemExpired(expiredKey);
    }

    private string FindLeastRecentlyUsed()
    {
        (string Key, long LastAccessTicks, long InstanceNumber) oldest = (null, Int64.MaxValue, 0);

        foreach (var kvp in _memory)
        {
            bool isExpired = kvp.Value.IsExpired;
            if (isExpired ||
                kvp.Value.LastAccessTicks < oldest.LastAccessTicks ||
                (kvp.Value.LastAccessTicks == oldest.LastAccessTicks && kvp.Value.InstanceNumber < oldest.InstanceNumber))
                oldest = (kvp.Key, kvp.Value.LastAccessTicks, kvp.Value.InstanceNumber);

            if (isExpired)
                break;
        }

        return oldest.Key;
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
    /// </remarks>
    /// <returns>The key of the entry to evict, or null if no suitable candidate found.</returns>
    private string FindWorstSizeToUsageRatio()
    {
        string candidateKey = null;
        double worstRatio = Double.MinValue; // Start with minimum value so any score can win
        long currentTime = _timeProvider.GetUtcNow().Ticks;

        _logger.LogTrace("FindWorstSizeToUsageRatio: Checking {Count} entries", _memory.Count);

        foreach (var kvp in _memory)
        {
            // Prioritize expired items first
            if (kvp.Value.IsExpired)
                return kvp.Key;

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
                candidateKey = kvp.Key;
            }
        }

        _logger.LogTrace("FindWorstSizeToUsageRatio: Selected {Key} with score {Score}", candidateKey, worstRatio);
        return candidateKey;
    }

    private async Task DoMaintenanceAsync()
    {
        _logger.LogTrace("DoMaintenance: Starting");
        var utcNow = _timeProvider.GetUtcNow().SafeAddMilliseconds(50);

        // Remove expired items and items that are infrequently accessed as they may be updated by add.
        long lastAccessMaximumTicks = utcNow.SafeAddMilliseconds(-300).Ticks;

        try
        {
            foreach (var kvp in _memory.ToArray())
            {
                bool lastAccessTimeIsInfrequent = kvp.Value.LastAccessTicks < lastAccessMaximumTicks;
                if (!lastAccessTimeIsInfrequent)
                    continue;

                var expiresAt = kvp.Value.ExpiresAt;
                if (!expiresAt.HasValue)
                    continue;

                if (expiresAt < DateTime.MaxValue && expiresAt <= utcNow)
                {
                    _logger.LogDebug("DoMaintenance: Removing expired key {Key}", kvp.Key);
                    RemoveKeyIfExpired(kvp.Key);
                }
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
        _memory.Clear();
        ItemExpired?.Dispose();
        _sizeCalculator = null; // Allow GC to collect any captured closures
    }

    /// <summary>
    /// Creates a CacheEntry with pre-calculated size. Returns null if the entry should be skipped (exceeds size limits).
    /// </summary>
    private CacheEntry CreateEntry<T>(T value, DateTime? expiresAt)
    {
        long size = _hasSizeCalculator ? CalculateEntrySize(value) : 0;
        if (size < 0)
            return null;

        return new CacheEntry(value, expiresAt, _timeProvider, _shouldClone, size);
    }

    private sealed record CacheEntry
    {
        private object _cacheValue;
        private static long _instanceCount;
        private readonly bool _shouldClone;
        private readonly TimeProvider _timeProvider;

#if DEBUG
        private long _usageCount;
#endif

        public CacheEntry(object value, DateTime? expiresAt, TimeProvider timeProvider, bool shouldClone = true, long size = 0)
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

        internal long InstanceNumber { get; private set; }
        internal DateTime? ExpiresAt { get; set; }
        internal bool IsExpired => ExpiresAt.HasValue && ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime;
        internal long LastAccessTicks { get; private set; }
        internal long LastModifiedTicks { get; private set; }

        /// <summary>
        /// The size of this cache entry in bytes. Set at construction time.
        /// </summary>
        internal long Size { get; set; }

#if DEBUG
        internal long UsageCount => _usageCount;
#endif

        internal object Value
        {
            get
            {
                LastAccessTicks = _timeProvider.GetUtcNow().Ticks;
#if DEBUG
                Interlocked.Increment(ref _usageCount);
#endif
                return _shouldClone ? _cacheValue.DeepClone() : _cacheValue;
            }
            set
            {
                _cacheValue = _shouldClone ? value.DeepClone() : value;
                var utcNow = _timeProvider.GetUtcNow();
                LastAccessTicks = utcNow.Ticks;
                LastModifiedTicks = utcNow.Ticks;
            }
        }

        public T GetValue<T>()
        {
            object val = Value;
            var t = typeof(T);

            if (t == TypeHelper.BoolType || t == TypeHelper.StringType || t == TypeHelper.CharType || t == TypeHelper.DateTimeType || t == TypeHelper.ObjectType || t.IsNumeric())
                return (T)Convert.ChangeType(val, t);

            if (t == TypeHelper.NullableBoolType || t == TypeHelper.NullableCharType || t == TypeHelper.NullableDateTimeType || t.IsNullableNumeric())
                return val == null ? default : (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(t));

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
    private long CalculateEntrySize(object value)
    {
        // Fast bail-out: no size calculator configured
        if (!_hasSizeCalculator)
            return 0;

        try
        {
            long size = _sizeCalculator(value);

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

    private static bool TypeRequiresCloning(Type t)
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
    public InMemoryCacheClient Client { get; set; }
    public string Key { get; set; }
    public bool SendNotification { get; set; }
}

