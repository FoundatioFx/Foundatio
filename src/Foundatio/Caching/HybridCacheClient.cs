using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Resilience;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching;

public interface IHybridCacheClient : ICacheClient
{
}

public class HybridCacheClient : IHybridCacheClient, IHaveTimeProvider, IHaveLogger, IHaveLoggerFactory, IHaveResiliencePolicyProvider
{
    protected readonly ICacheClient _distributedCache;
    protected readonly IMessageBus _messageBus;
    private readonly string _cacheId = Guid.NewGuid().ToString("N");
    private readonly InMemoryCacheClient _localCache;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IResiliencePolicyProvider _resiliencePolicyProvider;
    private long _localCacheHits;
    private long _invalidateCacheCalls;

    public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus, InMemoryCacheClientOptions localCacheOptions = null, ILoggerFactory loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? distributedCacheClient.GetLoggerFactory() ?? localCacheOptions?.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HybridCacheClient>();
        _timeProvider = distributedCacheClient.GetTimeProvider() ?? localCacheOptions?.TimeProvider ?? TimeProvider.System;
        _resiliencePolicyProvider = distributedCacheClient.GetResiliencePolicyProvider() ?? localCacheOptions?.ResiliencePolicyProvider;
        _distributedCache = distributedCacheClient;
        _messageBus = messageBus;
        _messageBus.SubscribeAsync<InvalidateCache>(OnRemoteCacheItemExpiredAsync).AnyContext().GetAwaiter().GetResult();
        localCacheOptions ??= new InMemoryCacheClientOptions
        {
            TimeProvider = _timeProvider,
            ResiliencePolicyProvider = _resiliencePolicyProvider,
            LoggerFactory = loggerFactory
        };
        _localCache = new InMemoryCacheClient(localCacheOptions);
    }

    public InMemoryCacheClient LocalCache => _localCache;
    public long LocalCacheHits => _localCacheHits;
    public long InvalidateCacheCalls => _invalidateCacheCalls;

    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    private Task OnRemoteCacheItemExpiredAsync(InvalidateCache message)
    {
        if (!String.IsNullOrEmpty(message.CacheId) && String.Equals(_cacheId, message.CacheId))
            return Task.CompletedTask;

        _logger.LogTrace("Invalidating local cache from remote: id={CacheId} expired={Expired} keys={Keys}", message.CacheId, message.Expired, String.Join(",", message.Keys ?? []));
        Interlocked.Increment(ref _invalidateCacheCalls);
        if (message.FlushAll)
        {
            _logger.LogTrace("Flushed local cache");
            return _localCache.RemoveAllAsync();
        }

        if (message.Keys is { Length: > 0 })
        {
            var tasks = new List<Task>(message.Keys.Length);
            var keysToRemove = new List<string>(message.Keys.Length);
            foreach (string key in message.Keys)
            {
                if (message.Expired)
                    _localCache.RemoveExpiredKey(key, false);
                else if (key.EndsWith('*'))
                    tasks.Add(_localCache.RemoveByPrefixAsync(key.Substring(0, key.Length - 1)));
                else
                    keysToRemove.Add(key);
            }

            if (keysToRemove.Count > 0)
                tasks.Add(_localCache.RemoveAllAsync(keysToRemove));

            return Task.WhenAll(tasks);
        }

        _logger.LogWarning("Unknown invalidate cache message");
        return Task.CompletedTask;
    }

    public async Task<bool> RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool removed = await _distributedCache.RemoveAsync(key).AnyContext();
        await _localCache.RemoveAsync(key).AnyContext();

        // Only notify other nodes if the key actually existed and was removed from distributed cache.
        // If removed == false, the key didn't exist, so no other node needs to be notified.
        if (removed)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool removed = await _distributedCache.RemoveIfEqualAsync(key, expected).AnyContext();

        // Always remove from local cache unconditionally. We use RemoveAsync (not RemoveIfEqualAsync)
        // because the local cache might have a stale value that doesn't match 'expected'.
        await _localCache.RemoveAsync(key).AnyContext();

        // Only notify other nodes if the key was actually removed from distributed cache.
        // If removed == false, either the key didn't exist or the value didn't match.
        if (removed)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        string[] items = keys?.ToArray();
        bool flushAll = items == null || items.Length == 0;
        int removed = await _distributedCache.RemoveAllAsync(items).AnyContext();
        await _localCache.RemoveAllAsync(items).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = items }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        int removed = await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
        await _localCache.RemoveByPrefixAsync(prefix).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [$"{prefix}*"] }).AnyContext();

        return removed;
    }

    public async Task<CacheValue<T>> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var cacheValue = await _localCache.GetAsync<T>(key).AnyContext();
        if (cacheValue.HasValue)
        {
            _logger.LogTrace("Local cache hit: {Key}", key);
            Interlocked.Increment(ref _localCacheHits);
            return cacheValue;
        }

        _logger.LogTrace("Local cache miss: {Key}", key);
        cacheValue = await _distributedCache.GetAsync<T>(key).AnyContext();
        if (cacheValue.HasValue)
        {
            var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
            _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", key, expiration);

            await _localCache.SetAsync(key, cacheValue.Value, expiration).AnyContext();
            return cacheValue;
        }

        return cacheValue.HasValue ? cacheValue : CacheValue<T>.NoValue;
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));

        var keysCollection = keys as ICollection<string> ?? keys.ToList();
        if (keysCollection.Count is 0)
            return ReadOnlyDictionary<string, CacheValue<T>>.Empty;

        var localValues = await _localCache.GetAllAsync<T>(keysCollection).AnyContext();

        // Collect keys that weren't found in local cache.
        var missedKeys = new List<string>(keysCollection.Count);
        foreach (var kvp in localValues)
        {
            if (kvp.Value.HasValue)
            {
                _logger.LogTrace("Local cache hit: {Key}", kvp.Key);
            }
            else
            {
                _logger.LogTrace("Local cache miss: {Key}", kvp.Key);
                missedKeys.Add(kvp.Key);
            }
        }
        Interlocked.Add(ref _localCacheHits, keysCollection.Count - missedKeys.Count);

        // All keys found in local cache.
        if (missedKeys.Count is 0)
            return localValues;

        var result = new Dictionary<string, CacheValue<T>>(localValues);
        var distributedResults = await _distributedCache.GetAllAsync<T>(missedKeys).AnyContext();

        // Get all expirations in a single bulk operation.
        var keysWithValues = distributedResults.Where(kvp => kvp.Value.HasValue).Select(kvp => kvp.Key).ToList();
        var expirations = keysWithValues.Count > 0
            ? await _distributedCache.GetAllExpirationAsync(keysWithValues).AnyContext()
            : ReadOnlyDictionary<string, TimeSpan?>.Empty;

        foreach (var kvp in distributedResults)
        {
            result[kvp.Key] = kvp.Value;
            if (!kvp.Value.HasValue)
                continue;

            var expiration = expirations.TryGetValue(kvp.Key, out var exp) ? exp : null;
            _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", kvp.Key, expiration);
            await _localCache.SetAsync(kvp.Key, kvp.Value.Value, expiration).AnyContext();
        }

        return result.AsReadOnly();
    }

    public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        _logger.LogTrace("Adding key {Key} to local cache with expiration: {Expiration}", key, expiresIn);
        bool added = await _distributedCache.AddAsync(key, value, expiresIn).AnyContext();
        if (added)
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();

        return added;
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        _logger.LogTrace("Setting key {Key} with expiration: {Expiration}", key, expiresIn);
        bool updated = await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
        if (updated)
        {
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        }
        else
        {
            // Remove from local cache when set fails (e.g., past expiration removes the key)
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return updated;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        
        if (values.Count is 0)
            return 0;

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAllAsync(values.Keys).AnyContext();
            return 0;
        }

        _logger.LogTrace("Setting keys {Keys} with expiration: {Expiration}", values.Keys, expiresIn);
        int setCount = await _distributedCache.SetAllAsync(values, expiresIn).AnyContext();
        if (setCount == values.Count)
        {
            await _localCache.SetAllAsync(values, expiresIn).AnyContext();
        }
        else
        {
            // Remove all keys from local cache when set fails or partially succeeds.
            // We don't know which specific keys succeeded, so remove all to force re-fetch.
            await _localCache.RemoveAllAsync(values.Keys).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = values.Keys.ToArray() }).AnyContext();
        return setCount;
    }

    public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        bool replaced = await _distributedCache.ReplaceAsync(key, value, expiresIn).AnyContext();
        if (replaced)
        {
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        }
        else
        {
            // Remove from local cache when replace fails (e.g., past expiration removes the key)
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        bool replaced = await _distributedCache.ReplaceIfEqualAsync(key, value, expected, expiresIn).AnyContext();
        if (replaced)
        {
            // Use SetAsync instead of ReplaceIfEqualAsync for local cache because we know the
            // distributed cache now has this exact value, and we need local cache to be in sync.
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        }
        else
        {
            // Remove from local cache when replace fails (e.g., past expiration removes the key)
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        double newValue = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        if (newValue is 0)
        {
            // When the result is 0, we remove from local cache rather than caching the value.
            // This handles the edge case where 0 could indicate either a legitimate zero value
            // or an error condition. Removing is conservative - the next read will fetch from
            // distributed cache if the key exists there.
            await _localCache.RemoveAsync(key).AnyContext();
        }
        else
        {
            // IncrementAsync with null expiration removes TTL (consistent with SetAsync),
            // so we can safely cache the new value locally with the same expiration
            await _localCache.SetAsync(key, newValue, expiresIn).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return newValue;
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        long newValue = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        if (newValue is 0)
        {
            // When the result is 0, we remove from local cache rather than caching the value.
            // This handles the edge case where 0 could indicate either a legitimate zero value
            // or an error condition. Removing is conservative - the next read will fetch from
            // distributed cache if the key exists there.
            await _localCache.RemoveAsync(key).AnyContext();
        }
        else
        {
            // IncrementAsync with null expiration removes TTL (consistent with SetAsync),
            // so we can safely cache the new value locally with the same expiration
            await _localCache.SetAsync(key, newValue, expiresIn).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return newValue;
    }

    public async Task<bool> ExistsAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // Check local cache first
        bool localExists = await _localCache.ExistsAsync(key).AnyContext();
        if (localExists)
        {
            _logger.LogTrace("Local cache hit: {Key}", key);
            Interlocked.Increment(ref _localCacheHits);
            return true;
        }

        _logger.LogTrace("Local cache miss: {Key}", key);
        return await _distributedCache.ExistsAsync(key).AnyContext();
    }

    public async Task<TimeSpan?> GetExpirationAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // Check if key exists in local cache first
        bool localExists = await _localCache.ExistsAsync(key).AnyContext();
        if (localExists)
        {
            _logger.LogTrace("Local cache hit: {Key}", key);
            Interlocked.Increment(ref _localCacheHits);
            return await _localCache.GetExpirationAsync(key).AnyContext();
        }

        _logger.LogTrace("Local cache miss: {Key}", key);
        return await _distributedCache.GetExpirationAsync(key).AnyContext();
    }

    public async Task<IDictionary<string, TimeSpan?>> GetAllExpirationAsync(IEnumerable<string> keys)
    {
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));

        string[] keysArray = keys.ToArray();
        if (keysArray.Length is 0)
            return ReadOnlyDictionary<string, TimeSpan?>.Empty;

        var localExpirations = await _localCache.GetAllExpirationAsync(keysArray).AnyContext();
        foreach (string key in localExpirations.Keys)
        {
            _logger.LogTrace("Local cache hit: {Key}", key);
            Interlocked.Increment(ref _localCacheHits);
        }

        if (keysArray.Length == localExpirations.Count)
            return localExpirations;

        // Get the missed keys from the distributed cache.
        string[] missedKeys = keysArray.Except(localExpirations.Keys).ToArray();
        foreach (string key in missedKeys)
        {
            _logger.LogTrace("Local cache miss: {Key}", key);
        }

        var result = new Dictionary<string, TimeSpan?>(localExpirations);
        var distributedExpirations = await _distributedCache.GetAllExpirationAsync(missedKeys).AnyContext();
        foreach (var kvp in distributedExpirations)
            result[kvp.Key] = kvp.Value;

        return result.AsReadOnly();
    }

    public async Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
        await _localCache.SetExpirationAsync(key, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);

        if (expirations.Count is 0)
            return;

        await _distributedCache.SetAllExpirationAsync(expirations).AnyContext();
        await _localCache.SetAllExpirationAsync(expirations).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = expirations.Keys.ToArray() }).AnyContext();
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        double difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();

        if (Math.Abs(difference) > double.Epsilon)
        {
            // Value was updated - we know the new value is exactly what we passed in
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        }
        else
        {
            // Value was not updated (existing value was higher or equal) - remove from local cache
            // since we don't know what the actual current value is
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        long difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();

        if (difference != 0)
        {
            // Value was updated - we know the new value is exactly what we passed in
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        }
        else
        {
            // Value was not updated (existing value was higher or equal) - remove from local cache
            // since we don't know what the actual current value is
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        double difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();

        if (Math.Abs(difference) > double.Epsilon)
        {
            // Value was updated - we know the new value is exactly what we passed in
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        }
        else
        {
            // Value was not updated (existing value was lower or equal) - remove from local cache
            // since we don't know what the actual current value is
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        long difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();

        if (difference != 0)
        {
            // Value was updated - we know the new value is exactly what we passed in
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        }
        else
        {
            // Value was not updated (existing value was lower or equal) - remove from local cache
            // since we don't know what the actual current value is
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        // Handle string specially to avoid treating it as IEnumerable<char>
        if (values is string stringValue)
        {
            long added = await _distributedCache.ListAddAsync(key, stringValue, expiresIn).AnyContext();
            if (added == 1)
            {
                // String added successfully - update local cache
                await _localCache.ListAddAsync(key, stringValue, expiresIn).AnyContext();
            }
            else
            {
                // Failed - remove to force re-fetch
                await _localCache.RemoveAsync(key).AnyContext();
            }

            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return added;
        }

        var items = values.ToArray();
        long addedCount = await _distributedCache.ListAddAsync(key, items, expiresIn).AnyContext();
        if (addedCount == items.Length)
        {
            // All items added successfully - update local cache
            await _localCache.ListAddAsync(key, items, expiresIn).AnyContext();
        }
        else
        {
            // Partial success - remove to force re-fetch
            await _localCache.RemoveAsync(key).AnyContext();
        }

        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return addedCount;
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        // Handle string specially to avoid treating it as IEnumerable<char>
        if (values is string stringValue)
        {
            long removed = await _distributedCache.ListRemoveAsync(key, stringValue).AnyContext();
            if (removed == 1)
            {
                // String removed successfully - update local cache
                await _localCache.ListRemoveAsync(key, stringValue).AnyContext();
            }
            else
            {
                // Failed - remove to force re-fetch
                await _localCache.RemoveAsync(key).AnyContext();
            }

            // Only notify other nodes if the item was actually removed from distributed cache.
            if (removed > 0)
                await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

            return removed;
        }

        var items = values.ToArray();
        long removedCount = await _distributedCache.ListRemoveAsync(key, items).AnyContext();
        if (removedCount == items.Length)
        {
            // All items removed successfully - update local cache.
            await _localCache.ListRemoveAsync(key, items).AnyContext();
        }
        else
        {
            // Partial success - remove to force re-fetch
            await _localCache.RemoveAsync(key).AnyContext();
        }

        // Only notify other nodes if items were actually removed from distributed cache.
        if (removedCount > 0)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removedCount;
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        var cacheValue = await _localCache.GetListAsync<T>(key, page, pageSize).AnyContext();
        if (cacheValue.HasValue)
        {
            _logger.LogTrace("Local cache hit: {Key}", key);
            Interlocked.Increment(ref _localCacheHits);
            return cacheValue;
        }

        _logger.LogTrace("Local cache miss: {Key}", key);
        cacheValue = await _distributedCache.GetListAsync<T>(key, page, pageSize).AnyContext();
        if (cacheValue.HasValue)
        {
            var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
            _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", key, expiration);

            await _localCache.ListAddAsync(key, cacheValue.Value, expiration).AnyContext();
        }

        return cacheValue;
    }

    public virtual void Dispose()
    {
        _localCache.Dispose();

        // TODO: unsubscribe handler from messagebus.
    }

    public class InvalidateCache
    {
        public string CacheId { get; set; }
        public string[] Keys { get; set; }
        public bool FlushAll { get; set; }
        public bool Expired { get; set; }
    }
}
