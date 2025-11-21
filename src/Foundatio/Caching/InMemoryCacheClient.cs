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
    private long _writes;
    private long _hits;
    private long _misses;
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
        if (options == null)
            options = new InMemoryCacheClientOptions();
        _shouldClone = options.CloneValues;
        _shouldThrowOnSerializationErrors = options.ShouldThrowOnSerializationError;
        _maxItems = options.MaxItems;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _resiliencePolicyProvider = options.ResiliencePolicyProvider;
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<InMemoryCacheClient>();
        _memory = new ConcurrentDictionary<string, CacheEntry>();
    }

    public InMemoryCacheClient(Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> config)
        : this(config(new InMemoryCacheClientOptionsBuilder()).Build())
    {
    }

    public int Count => _memory.Count(i => !i.Value.IsExpired);
    public int? MaxItems => _maxItems;
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
        return $"Count: {Count} Calls: {Calls} Reads: {Reads} Writes: {Writes} Hits: {Hits} Misses: {Misses}";
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
        return Task.FromResult(_memory.TryRemove(key, out _));
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
            return Task.FromResult(count);
        }

        int removed = 0;
        foreach (string key in keys)
        {
            if (String.IsNullOrEmpty(key))
                continue;

            _logger.LogTrace("RemoveAllAsync: Removing key: {Key}", key);
            if (_memory.TryRemove(key, out _))
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
        if (_memory.TryRemove(key, out _))
        {
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

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        return SetInternalAsync(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone), true);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        return SetInternalAsync(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone));
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        double difference = value;
        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone), (_, existingEntry) =>
        {
            double? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<double?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type");
            }

            if (currentValue.HasValue && currentValue.Value < value)
            {
                difference = value - currentValue.Value;
                existingEntry.Value = value;
            }
            else
            {
                difference = 0;
            }

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            return existingEntry;
        });

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        long difference = value;
        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone), (_, existingEntry) =>
        {
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
            }
            else
            {
                difference = 0;
            }

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            return existingEntry;
        });

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        double difference = value;
        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone), (_, existingEntry) =>
        {
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
            }
            else
            {
                difference = 0;
            }

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            return existingEntry;
        });

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        long difference = value;
        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone), (_, existingEntry) =>
        {
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
            }
            else
            {
                difference = 0;
            }

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            return existingEntry;
        });

        await StartMaintenanceAsync().AnyContext();

        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime? expiresAt = expiresIn.HasValue ? utcNow.SafeAdd(expiresIn.Value) : null;
        if (expiresAt < utcNow)
        {
            await ListRemoveAsync(key, values).AnyContext();
            return 0;
        }

        Interlocked.Increment(ref _writes);

        if (values is string stringValue)
        {
            var items = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
            {
                { stringValue, expiresAt }
            };

            var entry = new CacheEntry(items, expiresAt, _timeProvider, _shouldClone);
            _memory.AddOrUpdate(key, entry, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is not IDictionary<string, DateTime?> dictionary)
                    throw new InvalidOperationException($"Unable to add value for key: {existingKey}. Cache value does not contain a dictionary");

                ExpireListValues(dictionary, existingKey);

                dictionary[stringValue] = expiresAt;
                existingEntry.Value = dictionary;
                existingEntry.ExpiresAt = dictionary.Values.Contains(null) ? null : dictionary.Values.Max();

                return existingEntry;
            });

            await StartMaintenanceAsync().AnyContext();
            return items.Count;
        }
        else
        {
            var items = new HashSet<T>(values.Where(v => v is not null)).ToDictionary(k => k, _ => expiresAt);
            if (items.Count == 0)
                return 0;

            var entry = new CacheEntry(items, expiresAt, _timeProvider, _shouldClone);
            _memory.AddOrUpdate(key, entry, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is not IDictionary<T, DateTime?> dictionary)
                    throw new InvalidOperationException($"Unable to add value for key: {existingKey}. Cache value does not contain a set");

                ExpireListValues(dictionary, existingKey);

                foreach (var kvp in items)
                    dictionary[kvp.Key] = kvp.Value;

                existingEntry.Value = dictionary;
                existingEntry.ExpiresAt = dictionary.Values.Contains(null) ? null : dictionary.Values.Max();
                return existingEntry;
            });

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

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        Interlocked.Increment(ref _writes);

        long removed = 0;
        if (values is string stringValue)
        {
            var items = new HashSet<string>([stringValue]);
            _memory.TryUpdate(key, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is IDictionary<string, DateTime?> { Count: > 0 } dictionary)
                {
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
                    }
                }

                if (removed > 0)
                    _logger.LogTrace("Removed value from set with cache key: {Key}", existingKey);

                return existingEntry;
            });

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
                    }
                }

                if (removed > 0)
                    _logger.LogTrace("Removed value from set with cache key: {Key}", existingKey);

                return existingEntry;
            });

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

                    wasUpdated = true;
                    return entry;
                }

                return existingEntry;
            });

            if (wasUpdated)
                _logger.LogTrace("Added cache key: {Key}", key);
        }
        else
        {
            _memory.AddOrUpdate(key, entry, (_, _) => entry);
            _logger.LogTrace("Set cache key: {Key}", key);
        }

        await StartMaintenanceAsync(ShouldCompact).AnyContext();
        return wasUpdated;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        if (values == null || values.Count == 0)
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

        _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        Interlocked.Increment(ref _writes);

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        bool wasExpectedValue = false;
        bool success = _memory.TryUpdate(key, (_, existingEntry) =>
        {
            var currentValue = existingEntry.GetValue<T>();
            if (currentValue.Equals(expected))
            {
                existingEntry.Value = value;
                wasExpectedValue = true;

                if (expiresIn.HasValue)
                    existingEntry.ExpiresAt = expiresAt;
            }

            return existingEntry;
        });

        success = success && wasExpectedValue;
        await StartMaintenanceAsync().AnyContext();

        _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

        return success;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var result = _memory.AddOrUpdate(key, new CacheEntry(amount, expiresAt, _timeProvider, _shouldClone), (_, existingEntry) =>
        {
            double? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<double?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type");
            }

            if (currentValue.HasValue)
                existingEntry.Value = currentValue.Value + amount;
            else
                existingEntry.Value = amount;

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            return existingEntry;
        });

        await StartMaintenanceAsync().AnyContext();

        return result.GetValue<double>();
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn?.Ticks <= 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        DateTime? expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : null;
        var result = _memory.AddOrUpdate(key, new CacheEntry(amount, expiresAt, _timeProvider, _shouldClone), (_, existingEntry) =>
        {
            long? currentValue = null;
            try
            {
                currentValue = existingEntry.GetValue<long?>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to increment value, expected integer type");
            }

            if (currentValue.HasValue)
                existingEntry.Value = currentValue.Value + amount;
            else
                existingEntry.Value = amount;

            if (expiresIn.HasValue)
                existingEntry.ExpiresAt = expiresAt;

            return existingEntry;
        });

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
        return Task.FromResult<TimeSpan?>(existingEntry.ExpiresAt?.Subtract(_timeProvider.GetUtcNow().UtcDateTime));
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
                // Don't include non-existent keys in result (consistent with GetExpirationAsync returning null)
                continue;
            }

            if (existingEntry.IsExpired)
            {
                Interlocked.Increment(ref _misses);
                // Don't include expired keys in result (consistent with GetExpirationAsync returning null)
                continue;
            }

            Interlocked.Increment(ref _hits);

            // Skip keys without expiration (consistent with GetExpirationAsync behavior)
            if (existingEntry.ExpiresAt.HasValue)
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
        if (expirations is null)
            throw new ArgumentNullException(nameof(expirations));

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
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (compactImmediately)
            await CompactAsync().AnyContext();

        if (TimeSpan.FromMilliseconds(250) < utcNow - _lastMaintenance)
        {
            _lastMaintenance = utcNow;
            _ = Task.Run(DoMaintenanceAsync);
        }
    }

    private bool ShouldCompact => _maxItems.HasValue && _memory.Count > _maxItems;

    private async Task CompactAsync()
    {
        if (!ShouldCompact)
            return;

        _logger.LogTrace("CompactAsync: Compacting cache");

        string expiredKey = null;
        using (await _lock.LockAsync().AnyContext())
        {
            if (_memory.Count <= _maxItems)
                return;

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

            if (oldest.Key is null)
                return;

            _logger.LogDebug("Removing cache entry {Key} due to cache exceeding max item count limit", oldest);
            _memory.TryRemove(oldest.Key, out var cacheEntry);
            if (cacheEntry is { IsExpired: true })
                expiredKey = oldest.Key;
        }

        if (expiredKey != null)
            OnItemExpired(expiredKey);
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
            await CompactAsync().AnyContext();

        _logger.LogTrace("DoMaintenance: Finished");
    }

    public virtual void Dispose()
    {
        _memory.Clear();
        ItemExpired?.Dispose();
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

        public CacheEntry(object value, DateTime? expiresAt, TimeProvider timeProvider, bool shouldClone = true)
        {
            _timeProvider = timeProvider;
            _shouldClone = shouldClone && TypeRequiresCloning(value?.GetType());
            Value = value;
            ExpiresAt = expiresAt;
            LastModifiedTicks = _timeProvider.GetUtcNow().Ticks;
            InstanceNumber = Interlocked.Increment(ref _instanceCount);
        }

        internal long InstanceNumber { get; private set; }
        internal DateTime? ExpiresAt { get; set; }
        internal bool IsExpired => ExpiresAt.HasValue && ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime;
        internal long LastAccessTicks { get; private set; }
        internal long LastModifiedTicks { get; private set; }
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

        private bool TypeRequiresCloning(Type t)
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
}

public class ItemExpiredEventArgs : EventArgs
{
    public InMemoryCacheClient Client { get; set; }
    public string Key { get; set; }
    public bool SendNotification { get; set; }
}
