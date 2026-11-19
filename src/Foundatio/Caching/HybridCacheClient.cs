using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
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
    private readonly CancellationTokenSource _disposedCancellationTokenSource = new();
    private readonly AsyncLazy<bool> _lazySubscription;
    private readonly LocalCacheFence _fence = new();
    private long _localCacheHits;
    private long _invalidateCacheCalls;
    private bool _isDisposed;

    public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus, InMemoryCacheClientOptions? localCacheOptions = null, ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? distributedCacheClient.GetLoggerFactory() ?? localCacheOptions?.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HybridCacheClient>();
        _timeProvider = distributedCacheClient.GetTimeProvider() ?? localCacheOptions?.TimeProvider ?? TimeProvider.System;
        _resiliencePolicyProvider = distributedCacheClient.GetResiliencePolicyProvider() ?? localCacheOptions?.ResiliencePolicyProvider ?? DefaultResiliencePolicyProvider.Instance;
        _distributedCache = distributedCacheClient;
        _messageBus = messageBus;
        _lazySubscription = new AsyncLazy<bool>(async () =>
        {
            await _messageBus.SubscribeAsync<InvalidateCache>(
                OnRemoteCacheItemExpiredAsync, _disposedCancellationTokenSource.Token).AnyContext();
            return true;
        }, AsyncLazyFlags.RetryOnFailure | AsyncLazyFlags.ExecuteOnCallingThread);
        localCacheOptions ??= new InMemoryCacheClientOptions
        {
            TimeProvider = _timeProvider,
            ResiliencePolicyProvider = _resiliencePolicyProvider,
            LoggerFactory = _loggerFactory
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

    private Task EnsureSubscribedAsync()
    {
        return _lazySubscription.Task;
    }

    private Task OnRemoteCacheItemExpiredAsync(InvalidateCache message)
    {
        if (!String.IsNullOrEmpty(message.CacheId) && String.Equals(_cacheId, message.CacheId))
            return Task.CompletedTask;

        _logger.LogTrace("Invalidating local cache from remote: id={CacheId} expired={Expired} keys={Keys}", message.CacheId, message.Expired, String.Join(",", message.Keys ?? []));
        Interlocked.Increment(ref _invalidateCacheCalls);
        if (message.FlushAll)
        {
            _fence.InvalidateAllWrites();
            _logger.LogTrace("Flushed local cache");
            return _localCache.RemoveAllAsync();
        }

        if (message.Keys is { Length: > 0 })
        {
            var tasks = new List<Task>(message.Keys.Length);
            var keysToRemove = new List<string>(message.Keys.Length);
            foreach (string key in message.Keys)
            {
                if (!message.Expired && key.EndsWith('*'))
                {
                    _fence.InvalidateAllWrites();
                    tasks.Add(_localCache.RemoveByPrefixAsync(key[..^1]));
                    continue;
                }

                _fence.InvalidateWrites(key);
                if (message.Expired)
                    _localCache.RemoveExpiredKey(key, false);
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

        await EnsureSubscribedAsync().AnyContext();

        bool removed = await _distributedCache.RemoveAsync(key).AnyContext();
        await EvictLocalAsync(key).AnyContext();

        // Only notify other nodes if the key actually existed and was removed from distributed cache.
        // If removed == false, the key didn't exist, so no other node needs to be notified.
        if (removed)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        bool removed = await _distributedCache.RemoveIfEqualAsync(key, expected).AnyContext();

        // Always remove from local cache unconditionally. We use RemoveAsync (not RemoveIfEqualAsync)
        // because the local cache might have a stale value that doesn't match 'expected'.
        await EvictLocalAsync(key).AnyContext();

        // Only notify other nodes if the key was actually removed from distributed cache.
        // If removed == false, either the key didn't exist or the value didn't match.
        if (removed)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string>? keys = null)
    {
        await EnsureSubscribedAsync().AnyContext();

        string[]? items = keys?.ToArray();
        bool flushAll = items is null || items.Length == 0;
        int removed = await _distributedCache.RemoveAllAsync(items).AnyContext();
        if (flushAll)
        {
            _fence.InvalidateAll();
        }
        else
        {
            foreach (string key in items!)
                _fence.Invalidate(key);
        }

        await _localCache.RemoveAllAsync(items).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = items }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        prefix ??= String.Empty;

        await EnsureSubscribedAsync().AnyContext();

        int removed = await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
        _fence.InvalidateAll();
        await _localCache.RemoveByPrefixAsync(prefix).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [$"{prefix}*"] }).AnyContext();

        return removed;
    }

    public async Task<CacheValue<T>> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        var cacheValue = await _localCache.GetAsync<T>(key).AnyContext();
        if (cacheValue.HasValue)
        {
            _logger.LogTrace("Local cache hit: {Key}", key);
            Interlocked.Increment(ref _localCacheHits);
            return cacheValue;
        }

        _logger.LogTrace("Local cache miss: {Key}", key);
        var ticket = _fence.BeginRead(key);
        cacheValue = await _distributedCache.GetAsync<T>(key).AnyContext();
        if (cacheValue.HasValue)
        {
            var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
            _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", key, expiration);

            await SetLocalAsync(ticket, key, cacheValue.Value, expiration).AnyContext();
            return cacheValue;
        }

        return CacheValue<T>.NoValue;
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        await EnsureSubscribedAsync().AnyContext();

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
        var tickets = new Dictionary<string, LocalCacheFence.Ticket>(missedKeys.Count, StringComparer.Ordinal);
        foreach (string key in missedKeys)
            tickets[key] = _fence.BeginRead(key);

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
            if (tickets.TryGetValue(kvp.Key, out var ticket))
                await SetLocalAsync(ticket, kvp.Key, kvp.Value.Value, expiration).AnyContext();
        }


        return result.AsReadOnly();
    }

    public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        _logger.LogTrace("Adding key {Key} to local cache with expiration: {Expiration}", key, expiresIn);
        using var ticket = _fence.BeginWrite(key);
        bool added = await _distributedCache.AddAsync(key, value, expiresIn).AnyContext();
        if (added)
            (await SetLocalAsync(ticket, key, value, expiresIn).AnyContext())?.Throw();

        return added;
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        _logger.LogTrace("Setting key {Key} with expiration: {Expiration}", key, expiresIn);
        bool updated;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            updated = await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
            if (updated)
                localError = await SetLocalAsync(ticket, key, value, expiresIn).AnyContext();
            else
                await EvictLocalAsync(key).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();

        return updated;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        await EnsureSubscribedAsync().AnyContext();

        if (values.Count is 0)
            return 0;

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAllAsync(values.Keys).AnyContext();
            return 0;
        }

        _logger.LogTrace("Setting keys {Keys} with expiration: {Expiration}", values.Keys, expiresIn);
        string[] keys = values.Keys.ToArray();
        int setCount;
        ExceptionDispatchInfo? localError = null;
        using (var tickets = _fence.BeginWrites(keys))
        {
            setCount = await _distributedCache.SetAllAsync(values, expiresIn).AnyContext();
            if (setCount == values.Count)
            {
                localError = await UpdateAllLocalAsync(tickets, keys, (values, expiresIn), static async (cache, state) =>
                    await cache.SetAllAsync(state.values, state.expiresIn).AnyContext() == state.values.Count).AnyContext();
            }
            else
            {
                // We don't know which specific keys succeeded, so remove all to force re-fetch.
                await EvictLocalAsync(keys).AnyContext();
            }
        }

        await PublishInvalidationAsync(keys, localError).AnyContext();
        return setCount;
    }

    public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        bool replaced;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            replaced = await _distributedCache.ReplaceAsync(key, value, expiresIn).AnyContext();
            if (replaced)
                localError = await SetLocalAsync(ticket, key, value, expiresIn).AnyContext();
            else
                await EvictLocalAsync(key).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return replaced;
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return false;
        }

        bool replaced;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            replaced = await _distributedCache.ReplaceIfEqualAsync(key, value, expected, expiresIn).AnyContext();
            // Store the value instead of comparing locally: the distributed cache now holds exactly this value, and
            // the local copy may be stale.
            if (replaced)
                localError = await SetLocalAsync(ticket, key, value, expiresIn).AnyContext();
            else
                await EvictLocalAsync(key).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return replaced;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        double newValue;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            newValue = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
            // A result of 0 can also mean the provider removed the key, so it is never cached
            if (newValue is 0)
                await EvictLocalAsync(key).AnyContext();
            else
                localError = await SetLocalAsync(ticket, key, newValue, expiresIn).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return newValue;
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        long newValue;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            newValue = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
            // A result of 0 can also mean the provider removed the key, so it is never cached
            if (newValue is 0)
                await EvictLocalAsync(key).AnyContext();
            else
                localError = await SetLocalAsync(ticket, key, newValue, expiresIn).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return newValue;
    }

    public async Task<bool> ExistsAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

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

        await EnsureSubscribedAsync().AnyContext();

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
        ArgumentNullException.ThrowIfNull(keys);

        await EnsureSubscribedAsync().AnyContext();

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

        await EnsureSubscribedAsync().AnyContext();

        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
            localError = await UpdateLocalAsync(ticket, key, expiresIn, static async (cache, k, expiration) =>
            {
                await cache.SetExpirationAsync(k, expiration).AnyContext();
                return true;
            }).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);

        await EnsureSubscribedAsync().AnyContext();

        if (expirations.Count is 0)
            return;

        string[] keys = expirations.Keys.ToArray();
        ExceptionDispatchInfo? localError;
        using (var tickets = _fence.BeginWrites(keys))
        {
            await _distributedCache.SetAllExpirationAsync(expirations).AnyContext();
            localError = await UpdateAllLocalAsync(tickets, keys, expirations, static async (cache, state) =>
            {
                await cache.SetAllExpirationAsync(state).AnyContext();
                return true;
            }).AnyContext();
        }

        await PublishInvalidationAsync(keys, localError).AnyContext();
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        double difference;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
            // An unchanged value means the current value is unknown, so it is evicted instead
            if (Math.Abs(difference) > double.Epsilon)
                localError = await SetLocalAsync(ticket, key, value, expiresIn).AnyContext();
            else
                await EvictLocalAsync(key).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        long difference;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
            // An unchanged value means the current value is unknown, so it is evicted instead
            if (difference != 0)
                localError = await SetLocalAsync(ticket, key, value, expiresIn).AnyContext();
            else
                await EvictLocalAsync(key).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        double difference;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
            // An unchanged value means the current value is unknown, so it is evicted instead
            if (Math.Abs(difference) > double.Epsilon)
                localError = await SetLocalAsync(ticket, key, value, expiresIn).AnyContext();
            else
                await EvictLocalAsync(key).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        if (expiresIn < CacheClientExtensions.MinimumExpiration)
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        long difference;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
            // An unchanged value means the current value is unknown, so it is evicted instead
            if (difference != 0)
                localError = await SetLocalAsync(ticket, key, value, expiresIn).AnyContext();
            else
                await EvictLocalAsync(key).AnyContext();
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        await EnsureSubscribedAsync().AnyContext();

        var (items, expectedCount) = MaterializeListValues(values);
        long addedCount;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            // A local list is always complete. When there is none, the writer can only start one if the distributed
            // list didn't exist either, otherwise the local list would hold just the new members.
            bool isLocalList = await _localCache.ExistsAsync(key).AnyContext();
            bool isNewList = !isLocalList && !await _distributedCache.ExistsAsync(key).AnyContext();
            addedCount = await _distributedCache.ListAddAsync(key, items, expiresIn).AnyContext();
            if (addedCount == expectedCount && (isLocalList || isNewList))
            {
                localError = await UpdateLocalAsync(ticket, key, (items, expectedCount, expiresIn, requireExistingList: isLocalList), static async (cache, k, state) =>
                    await cache.ListAddAsync(k, state.items, state.expiresIn, state.requireExistingList).AnyContext() == state.expectedCount).AnyContext();
            }
            else
            {
                await EvictLocalAsync(key).AnyContext();
            }
        }

        await PublishInvalidationAsync([key], localError).AnyContext();
        return addedCount;
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        await EnsureSubscribedAsync().AnyContext();

        var (items, expectedCount) = MaterializeListValues(values);
        long removedCount;
        ExceptionDispatchInfo? localError = null;
        using (var ticket = _fence.BeginWrite(key))
        {
            removedCount = await _distributedCache.ListRemoveAsync(key, items).AnyContext();
            if (removedCount == expectedCount)
            {
                localError = await UpdateLocalAsync(ticket, key, (items, expectedCount), static async (cache, k, state) =>
                    await cache.ListRemoveAsync(k, state.items).AnyContext() == state.expectedCount).AnyContext();
            }
            else
            {
                await EvictLocalAsync(key).AnyContext();
            }
        }

        // Only notify other nodes if items were actually removed from distributed cache.
        if (removedCount > 0)
            await PublishInvalidationAsync([key], localError).AnyContext();
        else
            localError?.Throw();

        return removedCount;
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        await EnsureSubscribedAsync().AnyContext();

        var cacheValue = await _localCache.GetListAsync<T>(key, page, pageSize).AnyContext();
        if (cacheValue.HasValue)
        {
            _logger.LogTrace("Local cache hit: {Key}", key);
            Interlocked.Increment(ref _localCacheHits);
            return cacheValue;
        }

        _logger.LogTrace("Local cache miss: {Key}", key);
        var ticket = _fence.BeginRead(key);
        cacheValue = await _distributedCache.GetListAsync<T>(key, page, pageSize).AnyContext();
        if (cacheValue.HasValue && page is null)
        {
            var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
            _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", key, expiration);

            await UpdateLocalAsync(ticket, key, (cacheValue.Value, expiration), static async (cache, k, state) =>
            {
                await cache.ListAddAsync(k, state.Value, state.expiration).AnyContext();
                return true;
            }).AnyContext();
        }

        return cacheValue;
    }

    /// <summary>
    /// Stores a value learned from the distributed cache, or evicts the key if the ticket no longer allows it.
    /// </summary>
    private Task<ExceptionDispatchInfo?> SetLocalAsync<T>(LocalCacheFence.Ticket ticket, string key, T value, TimeSpan? expiresIn)
    {
        return UpdateLocalAsync(ticket, key, (value, expiresIn), static (cache, k, state) => cache.SetAsync(k, state.value, state.expiresIn));
    }

    /// <summary>
    /// Applies a local update when the ticket is current, then evicts the key if the update was rejected or the key
    /// was invalidated while it ran. A write's local error is returned after eviction, so the caller can still notify
    /// peers before surfacing it. A read's local error is logged.
    /// </summary>
    private async Task<ExceptionDispatchInfo?> UpdateLocalAsync<TState>(LocalCacheFence.Ticket ticket, string key, TState state, Func<InMemoryCacheClient, string, TState, Task<bool>> update)
    {
        if (!ticket.IsCurrent)
        {
            // A read changed nothing, and the write that made it stale updates the key itself.
            if (!ticket.IsRead)
                await EvictLocalAsync(key).AnyContext();
            return null;
        }

        ExceptionDispatchInfo? error = null;
        bool updated = false;
        try
        {
            updated = await update(_localCache, key, state).AnyContext();
        }
        catch (Exception ex)
        {
            error = ExceptionDispatchInfo.Capture(ex);
        }

        if (!updated || !ticket.IsCurrent)
            await EvictLocalAsync(key).AnyContext();

        // The distributed read already succeeded, so a local failure only costs the local copy.
        if (error is not null && ticket.IsRead)
        {
            _logger.LogWarning(error.SourceException, "Unable to store {Key} in the local cache after a distributed read", key);
            return null;
        }

        return error;
    }

    private async Task<ExceptionDispatchInfo?> UpdateAllLocalAsync<TState>(LocalCacheFence.WriteBatch tickets, string[] keys, TState state, Func<InMemoryCacheClient, TState, Task<bool>> update)
    {
        if (!tickets.AreAllCurrent())
        {
            await EvictLocalAsync(keys).AnyContext();
            return null;
        }

        ExceptionDispatchInfo? error = null;
        bool updated = false;
        try
        {
            updated = await update(_localCache, state).AnyContext();
        }
        catch (Exception ex)
        {
            error = ExceptionDispatchInfo.Capture(ex);
        }

        if (!updated)
        {
            await EvictLocalAsync(keys).AnyContext();
            return error;
        }

        string[] staleKeys = keys.Where(key => !tickets.IsCurrent(key)).ToArray();
        if (staleKeys.Length > 0)
            await EvictLocalAsync(staleKeys).AnyContext();

        return error;
    }

    /// <summary>
    /// Invalidates in-flight local updates for the key, then removes it from the local cache.
    /// </summary>
    private Task<bool> EvictLocalAsync(string key)
    {
        _fence.Invalidate(key);
        return _localCache.RemoveAsync(key);
    }

    private Task<int> EvictLocalAsync(string[] keys)
    {
        foreach (string key in keys)
            _fence.Invalidate(key);

        return _localCache.RemoveAllAsync(keys);
    }

    /// <summary>
    /// Notifies peers. When a local update failed, a publish failure is logged so the local error reaches the caller.
    /// </summary>
    private async Task PublishInvalidationAsync(string[] keys, ExceptionDispatchInfo? localError)
    {
        if (localError is null)
        {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = keys }).AnyContext();
            return;
        }

        try
        {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = keys }).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing cache invalidation after a failed local cache update");
        }

        localError.Throw();
    }

    /// <summary>
    /// A string binds <typeparamref name="T"/> to <see cref="char"/> but is one list member. It is passed through
    /// unchanged, as providers store it as a single value.
    /// </summary>
    private static (IEnumerable<T> Items, int Count) MaterializeListValues<T>(IEnumerable<T> values) where T : notnull
    {
        if (values is string)
            return (values, 1);

        var items = values.ToArray();
        return (items, items.Length);
    }

    public virtual void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _disposedCancellationTokenSource.Cancel();
        _disposedCancellationTokenSource.Dispose();
        _localCache.Dispose();
    }

    public class InvalidateCache
    {
        public required string CacheId { get; set; }
        public string[]? Keys { get; set; }
        public bool FlushAll { get; set; }
        public bool Expired { get; set; }
    }
}
