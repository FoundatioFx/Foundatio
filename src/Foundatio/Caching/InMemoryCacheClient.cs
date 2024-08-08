using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching;

public class InMemoryCacheClient : IMemoryCacheClient, IHaveTimeProvider, IHaveLogger
{
    private readonly ConcurrentDictionary<string, CacheEntry> _memory;
    private readonly bool _shouldClone;
    private readonly bool _shouldThrowOnSerializationErrors;
    private readonly int? _maxItems;
    private long _writes;
    private long _hits;
    private long _misses;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly AsyncLock _lock = new();

    public InMemoryCacheClient() : this(o => o) { }

    public InMemoryCacheClient(InMemoryCacheClientOptions options = null)
    {
        if (options == null)
            options = new InMemoryCacheClientOptions();
        _shouldClone = options.CloneValues;
        _shouldThrowOnSerializationErrors = options.ShouldThrowOnSerializationError;
        _maxItems = options.MaxItems;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger<InMemoryCacheClient>();
        _memory = new ConcurrentDictionary<string, CacheEntry>();
    }

    public InMemoryCacheClient(Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> config)
        : this(config(new InMemoryCacheClientOptionsBuilder()).Build()) { }

    public int Count => _memory.Count;
    public int? MaxItems => _maxItems;
    public long Calls => _writes + _hits + _misses;
    public long Writes => _writes;
    public long Reads => _hits + _misses;
    public long Hits => _hits;
    public long Misses => _misses;

    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

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

    public AsyncEvent<ItemExpiredEventArgs> ItemExpired { get; } = new AsyncEvent<ItemExpiredEventArgs>();

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
                .OrderBy(kvp => kvp.Value.LastAccessTicks)
                .ThenBy(kvp => kvp.Value.InstanceNumber)
                .Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value))
                .ToList();
        }
    }

    public Task<bool> RemoveAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("RemoveAsync: Removing key: {Key}", key);
        return Task.FromResult(_memory.TryRemove(key, out _));
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        if (isTraceLogLevelEnabled)
            _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        bool wasExpectedValue = false;
        bool success = _memory.TryUpdate(key, (existingKey, existingEntry) =>
        {
            var currentValue = existingEntry.GetValue<T>();
            if (currentValue.Equals(expected))
            {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Updating ExpiresAt to DateTime.MinValue", existingKey);

                existingEntry.ExpiresAt = DateTime.MinValue;
                wasExpectedValue = true;
            }

            return existingEntry;
        });

        success = success && wasExpectedValue;

        await StartMaintenanceAsync().AnyContext();

        if (isTraceLogLevelEnabled)
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

        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        int removed = 0;
        foreach (string key in keys)
        {
            if (String.IsNullOrEmpty(key))
                continue;

            if (isTraceLogLevelEnabled) _logger.LogTrace("RemoveAllAsync: Removing key: {Key}", key);
            if (_memory.TryRemove(key, out _))
                removed++;
        }

        return Task.FromResult(removed);
    }

    public Task<int> RemoveByPrefixAsync(string prefix)
    {
        var keysToRemove = new List<string>();
        var regex = new Regex(String.Concat(prefix, "*").Replace("*", ".*").Replace("?", ".+"));
        try
        {
            foreach (string key in _memory.Keys.ToList())
                if (regex.IsMatch(key))
                    keysToRemove.Add(key);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error trying to remove items from cache with this {Prefix} prefix", prefix);
        }

        return RemoveAllAsync(keysToRemove);
    }

    internal void RemoveExpiredKey(string key, bool sendNotification = true)
    {
        // Consideration: We could reduce the amount of calls to this by updating ExpiresAt and only having maintenance remove keys.
        if (_memory.TryGetValue(key, out var existingEntry) && existingEntry.ExpiresAt < _timeProvider.GetUtcNow())
        {
            if (_memory.TryRemove(key, out var removedEntry))
            {
                if (removedEntry.ExpiresAt >= _timeProvider.GetUtcNow())
                    throw new Exception("Removed item was not expired");

                _logger.LogDebug("Removing expired cache entry {Key}", key);
                OnItemExpired(key, sendNotification);
            }
        }
    }

    public Task<CacheValue<T>> GetAsync<T>(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (!_memory.TryGetValue(key, out var existingEntry))
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult(CacheValue<T>.NoValue);
        }

        if (existingEntry.ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime)
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
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Unable to deserialize value {Value} to type {TypeFullName}", existingEntry.Value, typeof(T).FullName);

            if (_shouldThrowOnSerializationErrors)
                throw;

            return Task.FromResult(CacheValue<T>.NoValue);
        }
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        var map = new Dictionary<string, CacheValue<T>>();

        foreach (string key in keys)
            map[key] = await GetAsync<T>(key);

        return map;
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
        return SetInternalAsync(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone), true);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
        return SetInternalAsync(key, new CacheEntry(value, expiresAt, _timeProvider, _shouldClone));
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (expiresIn?.Ticks < 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        double difference = value;
        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (expiresIn?.Ticks < 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        long difference = value;
        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (expiresIn?.Ticks < 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        double difference = value;
        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (expiresIn?.Ticks < 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        long difference = value;
        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
        if (expiresAt < _timeProvider.GetUtcNow().UtcDateTime)
        {
            RemoveExpiredKey(key);
            return default;
        }

        Interlocked.Increment(ref _writes);

        if (values is string stringValue)
        {
            var items = new HashSet<string>(new[] { stringValue });
            var entry = new CacheEntry(items, expiresAt, _timeProvider, _shouldClone);
            _memory.AddOrUpdate(key, entry, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is not ICollection<string> collection)
                    throw new InvalidOperationException($"Unable to add value for key: {existingKey}. Cache value does not contain a set");

                collection.Add(stringValue);
                existingEntry.Value = collection;

                if (expiresIn.HasValue)
                    existingEntry.ExpiresAt = expiresAt;

                return existingEntry;
            });

            await StartMaintenanceAsync().AnyContext();

            return items.Count;
        }
        else
        {
            var items = new HashSet<T>(values);
            var entry = new CacheEntry(items, expiresAt, _timeProvider, _shouldClone);
            _memory.AddOrUpdate(key, entry, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is not ICollection<T> collection)
                    throw new InvalidOperationException($"Unable to add value for key: {existingKey}. Cache value does not contain a set");

                collection.AddRange(items);
                existingEntry.Value = collection;

                if (expiresIn.HasValue)
                    existingEntry.ExpiresAt = expiresAt;

                return existingEntry;
            });

            await StartMaintenanceAsync().AnyContext();

            return items.Count;
        }
    }

    public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
        if (expiresAt < _timeProvider.GetUtcNow().UtcDateTime)
        {
            RemoveExpiredKey(key);
            return default;
        }

        Interlocked.Increment(ref _writes);

        if (values is string stringValue)
        {
            var items = new HashSet<string>(new[] { stringValue });
            _memory.TryUpdate(key, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is ICollection<string> { Count: > 0 } collection)
                {
                    foreach (string value in items)
                        collection.Remove(value);

                    existingEntry.Value = collection;
                }

                if (expiresIn.HasValue)
                    existingEntry.ExpiresAt = expiresAt;

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removed value from set with cache key: {Key}", existingKey);
                return existingEntry;
            });

            return Task.FromResult<long>(items.Count);
        }
        else
        {
            var items = new HashSet<T>(values);
            _memory.TryUpdate(key, (existingKey, existingEntry) =>
            {
                if (existingEntry.Value is ICollection<T> { Count: > 0 } collection)
                {
                    foreach (var value in items)
                        collection.Remove(value);

                    existingEntry.Value = collection;
                }

                if (expiresIn.HasValue)
                    existingEntry.ExpiresAt = expiresAt;

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removed value from set with cache key: {Key}", existingKey);
                return existingEntry;
            });

            return Task.FromResult<long>(items.Count);
        }
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        var list = await GetAsync<ICollection<T>>(key);
        if (!list.HasValue || !page.HasValue)
            return list;

        int skip = (page.Value - 1) * pageSize;
        var pagedItems = list.Value.Skip(skip).Take(pageSize).ToArray();
        return new CacheValue<ICollection<T>>(pagedItems, true);
    }

    private async Task<bool> SetInternalAsync(string key, CacheEntry entry, bool addOnly = false)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "SetInternalAsync: Key cannot be null or empty");

        if (entry.ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime)
        {
            RemoveExpiredKey(key);
            return false;
        }

        Interlocked.Increment(ref _writes);

        bool wasUpdated = true;
        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        if (addOnly)
        {
            _memory.AddOrUpdate(key, entry, (existingKey, existingEntry) =>
            {
                // NOTE: This update factory method will run multiple times if the key is already in the cache, especially during lock contention.
                wasUpdated = false;

                // check to see if existing entry is expired
                if (existingEntry.ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime)
                {
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Attempting to replacing expired cache key: {Key}", existingKey);

                    wasUpdated = true;
                    return entry;
                }

                return existingEntry;
            });

            if (wasUpdated && isTraceLogLevelEnabled)
                _logger.LogTrace("Added cache key: {Key}", key);
        }
        else
        {
            _memory.AddOrUpdate(key, entry, (_, _) => entry);
            if (isTraceLogLevelEnabled) _logger.LogTrace("Set cache key: {Key}", key);
        }

        await StartMaintenanceAsync(true).AnyContext();
        return wasUpdated;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        if (values == null || values.Count == 0)
            return 0;

        var tasks = new List<Task<bool>>();
        foreach (var entry in values)
            tasks.Add(SetAsync(entry.Key, entry.Value, expiresIn));

        bool[] results = await Task.WhenAll(tasks).AnyContext();
        return results.Count(r => r);
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        if (!_memory.ContainsKey(key))
            return Task.FromResult(false);

        return SetAsync(key, value, expiresIn);
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

        Interlocked.Increment(ref _writes);

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

        return success;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (expiresIn?.Ticks < 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (expiresIn?.Ticks < 0)
        {
            RemoveExpiredKey(key);
            return -1;
        }

        Interlocked.Increment(ref _writes);

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        if (!_memory.TryGetValue(key, out var existingEntry))
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult(false);
        }

        if (existingEntry.ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime)
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult(false);
        }

        Interlocked.Increment(ref _hits);
        return Task.FromResult(true);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (!_memory.TryGetValue(key, out var existingEntry))
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult<TimeSpan?>(null);
        }

        if (existingEntry.ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime || existingEntry.ExpiresAt == DateTime.MaxValue)
        {
            Interlocked.Increment(ref _misses);
            return Task.FromResult<TimeSpan?>(null);
        }

        Interlocked.Increment(ref _hits);
        return Task.FromResult<TimeSpan?>(existingEntry.ExpiresAt.Subtract(_timeProvider.GetUtcNow().UtcDateTime));
    }

    public async Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        var expiresAt = _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn);
        if (expiresAt < _timeProvider.GetUtcNow())
        {
            RemoveExpiredKey(key);
            return;
        }

        Interlocked.Increment(ref _writes);
        if (_memory.TryGetValue(key, out var existingEntry))
        {
            existingEntry.ExpiresAt = expiresAt;
            await StartMaintenanceAsync().AnyContext();
        }
    }

    private DateTime _lastMaintenance;

    private async Task StartMaintenanceAsync(bool compactImmediately = false)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (compactImmediately)
            await CompactAsync().AnyContext();

        if (TimeSpan.FromMilliseconds(100) < now - _lastMaintenance)
        {
            _lastMaintenance = now;
            _ = Task.Run(DoMaintenanceAsync);
        }
    }

    private async Task CompactAsync()
    {
        if (!_maxItems.HasValue || _memory.Count <= _maxItems)
            return;

        string expiredKey = null;
        using (await _lock.LockAsync().AnyContext())
        {
            if (_memory.Count <= _maxItems)
                return;

            (string Key, long LastAccessTicks, long InstanceNumber) oldest = (null, Int64.MaxValue, 0);
            foreach (var kvp in _memory)
            {
                bool isExpired = kvp.Value.ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime;
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
            if (cacheEntry != null && cacheEntry.ExpiresAt < _timeProvider.GetUtcNow().UtcDateTime)
                expiredKey = oldest.Key;
        }

        if (expiredKey != null)
            OnItemExpired(expiredKey);
    }

    private async Task DoMaintenanceAsync()
    {
        _logger.LogTrace("DoMaintenance");

        var utcNow = _timeProvider.GetUtcNow().AddMilliseconds(50);

        // Remove expired items and items that are infrequently accessed as they may be updated by add.
        long lastAccessMaximumTicks = utcNow.AddMilliseconds(-300).Ticks;

        try
        {
            foreach (var kvp in _memory.ToArray())
            {
                bool lastAccessTimeIsInfrequent = kvp.Value.LastAccessTicks < lastAccessMaximumTicks;
                if (lastAccessTimeIsInfrequent && kvp.Value.ExpiresAt <= utcNow)
                {
                    _logger.LogDebug("DoMaintenance: Removing expired key {Key}", kvp.Key);
                    RemoveExpiredKey(kvp.Key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trying to find expired cache items");
        }

        await CompactAsync().AnyContext();
    }

    public void Dispose()
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

        public CacheEntry(object value, DateTime expiresAt, TimeProvider timeProvider, bool shouldClone = true)
        {
            _timeProvider = timeProvider;
            _shouldClone = shouldClone && TypeRequiresCloning(value?.GetType());
            Value = value;
            ExpiresAt = expiresAt;
            LastModifiedTicks = _timeProvider.GetUtcNow().Ticks;
            InstanceNumber = Interlocked.Increment(ref _instanceCount);
        }

        internal long InstanceNumber { get; private set; }
        internal DateTime ExpiresAt { get; set; }
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
                LastAccessTicks = _timeProvider.GetUtcNow().Ticks;
                LastModifiedTicks = _timeProvider.GetUtcNow().Ticks;
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
