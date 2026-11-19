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
        if (String.Equals(_cacheId, message.CacheId, StringComparison.Ordinal))
            return Task.CompletedTask;

        _logger.LogTrace("Invalidating local cache from remote: id={CacheId} expired={Expired} keys={Keys}", message.CacheId, message.Expired, String.Join(",", message.Keys ?? []));
        Interlocked.Increment(ref _invalidateCacheCalls);
        if (message.FlushAll)
        {
            _fence.InvalidateAll();
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
                    _fence.InvalidateAll();
                    tasks.Add(_localCache.RemoveByPrefixAsync(key[..^1]));
                    continue;
                }

                _fence.Invalidate(key);
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
        await InvalidateLocalAsync(key).AnyContext();

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
        await InvalidateLocalAsync(key).AnyContext();

        // Only notify other nodes if the key was actually removed from distributed cache.
        // If removed == false, either the key didn't exist or the value didn't match.
        if (removed)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string>? keys = null)
    {
        await EnsureSubscribedAsync().AnyContext();

        string[]? items = keys is null ? null : GetBatchKeys(keys);
        if (items is { Length: 0 })
            items = null;

        int removed = await _distributedCache.RemoveAllAsync(items).AnyContext();
        if (items is null)
            _fence.InvalidateAll();
        else
        {
            foreach (string key in items)
                _fence.Invalidate(key);
        }

        await _localCache.RemoveAllAsync(items).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, FlushAll = items is null, Keys = items }).AnyContext();

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
        return await ReadThroughAsync<T>(key, static (cache, k) => cache.GetAsync<T>(k),
            static (cache, k, entry) => cache.SetAsync(k, entry.Value, entry.Expiration)).AnyContext();
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        await EnsureSubscribedAsync().AnyContext();

        var keysCollection = keys as ICollection<string> ?? keys.ToList();
        if (keysCollection.Count is 0)
            return ReadOnlyDictionary<string, CacheValue<T>>.Empty;

        var localValues = await _localCache.GetAllAsync<T>(keysCollection).AnyContext();

        Dictionary<string, LocalCacheFence.Ticket>? misses = null;
        foreach (var kvp in localValues)
        {
            if (kvp.Value.HasValue)
            {
                _logger.LogTrace("Local cache hit: {Key}", kvp.Key);
            }
            else
            {
                _logger.LogTrace("Local cache miss: {Key}", kvp.Key);
                misses ??= new Dictionary<string, LocalCacheFence.Ticket>(StringComparer.Ordinal);
                misses.Add(kvp.Key, _fence.BeginRead(kvp.Key));
            }
        }
        Interlocked.Add(ref _localCacheHits, keysCollection.Count - (misses?.Count ?? 0));

        // All keys found in local cache.
        if (misses is null)
            return localValues;

        var result = new Dictionary<string, CacheValue<T>>(localValues);
        var distributedResults = await _distributedCache.GetAllAsync<T>(misses.Keys).AnyContext();

        // Get all expirations in a single bulk operation.
        var keysWithValues = distributedResults.Where(kvp => kvp.Value.HasValue).Select(kvp => kvp.Key).ToList();
        var expirations = keysWithValues.Count > 0
            ? await _distributedCache.GetAllExpirationAsync(keysWithValues).AnyContext()
            : ReadOnlyDictionary<string, TimeSpan?>.Empty;

        foreach (var kvp in distributedResults)
        {
            result[kvp.Key] = kvp.Value;
            if (!kvp.Value.HasValue || !expirations.TryGetValue(kvp.Key, out var expiration) || !misses.TryGetValue(kvp.Key, out var ticket))
                continue;

            _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", kvp.Key, expiration);
            await UpdateLocalValueAsync(ticket, kvp.Key, kvp.Value.Value, expiration).AnyContext();
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
            (await UpdateLocalValueAsync(ticket, key, value, expiresIn).AnyContext())?.Throw();

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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            updated = await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, value, expiresIn, valueKnown: updated).AnyContext();
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
        return await WriteBatchAsync(GetBatchKeys(values.Keys), (values, expiresIn),
            static (cache, state) => cache.SetAllAsync(state.values, state.expiresIn)).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            replaced = await _distributedCache.ReplaceAsync(key, value, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, value, expiresIn, valueKnown: replaced).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            replaced = await _distributedCache.ReplaceIfEqualAsync(key, value, expected, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, value, expiresIn, valueKnown: replaced).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            newValue = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, newValue, expiresIn, valueKnown: newValue != 0).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            newValue = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, newValue, expiresIn, valueKnown: newValue != 0).AnyContext();
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

        await WriteBatchAsync(GetBatchKeys(expirations.Keys), expirations, static async (cache, values) =>
        {
            await cache.SetAllExpirationAsync(values).AnyContext();
            return values.Count;
        }).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, value, expiresIn, valueKnown: Math.Abs(difference) > double.Epsilon).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, value, expiresIn, valueKnown: difference != 0).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, value, expiresIn, valueKnown: Math.Abs(difference) > double.Epsilon).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
            localError = await UpdateLocalValueAsync(ticket, key, value, expiresIn, valueKnown: difference != 0).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            // A local list is always complete. When there is none, the writer can only start one if the distributed
            // list didn't exist either, otherwise the local list would hold just the new members.
            bool hasLocalEntry = await _localCache.ExistsAsync(key).AnyContext();
            bool isNewList = !hasLocalEntry && !await _distributedCache.ExistsAsync(key).AnyContext();
            addedCount = await _distributedCache.ListAddAsync(key, items, expiresIn).AnyContext();
            localError = await UpdateLocalAsync(ticket, key, (items, expectedCount, expiresIn, requireExistingList: hasLocalEntry),
                static async (cache, k, state) => await cache.ListAddAsync(k, state.items, state.expiresIn, state.requireExistingList).AnyContext() == state.expectedCount,
                canUpdate: addedCount == expectedCount && (hasLocalEntry || isNewList)).AnyContext();
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
        ExceptionDispatchInfo? localError;
        using (var ticket = _fence.BeginWrite(key))
        {
            removedCount = await _distributedCache.ListRemoveAsync(key, items).AnyContext();
            localError = await UpdateLocalAsync(ticket, key, (items, expectedCount),
                static async (cache, k, state) => await cache.ListRemoveAsync(k, state.items).AnyContext() == state.expectedCount,
                canUpdate: removedCount == expectedCount).AnyContext();
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
        if (page.HasValue)
            return await _distributedCache.GetListAsync<T>(key, page, pageSize).AnyContext();

        return await ReadThroughAsync<ICollection<T>>(key, static (cache, k) => cache.GetListAsync<T>(k),
            static async (cache, k, entry) => await cache.ListAddAsync(k, entry.Value, entry.Expiration).AnyContext() == entry.Value.Count).AnyContext();
    }

    private async Task<CacheValue<T>> ReadThroughAsync<T>(string key, Func<ICacheClient, string, Task<CacheValue<T>>> read,
        Func<InMemoryCacheClient, string, (T Value, TimeSpan? Expiration), Task<bool>> store)
    {
        var ticket = _fence.BeginRead(key);
        var result = await ReadAndStoreAsync(ticket).AnyContext();
        if (result.HasValue && !ticket.IsCurrent && !await _localCache.ExistsAsync(key).AnyContext())
        {
            // A delayed peer notification can invalidate a read even when L2 already returned the new value.
            // Fetch once more to warm L1 without reinstalling a snapshot that may have been invalidated.
            var retry = _fence.BeginRead(key);
            if (retry.IsCurrent)
            {
                try
                {
                    await ReadAndStoreAsync(retry).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to refresh {Key} after an overlapping cache invalidation", key);
                }
            }
        }

        return result;

        async Task<CacheValue<T>> ReadAndStoreAsync(LocalCacheFence.Ticket observation)
        {
            var value = await read(_distributedCache, key).AnyContext();
            if (value.HasValue && observation.IsCurrent)
            {
                // A null TTL can also mean the key expired after the value was read.
                var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
                if (expiration.HasValue || await _distributedCache.ExistsAsync(key).AnyContext())
                    await UpdateLocalAsync(observation, key, (value.Value, expiration), store).AnyContext();
            }

            return value;
        }
    }

    /// <summary>
    /// Stores a value learned from the distributed cache, or evicts the key if the ticket no longer allows it.
    /// </summary>
    private Task<ExceptionDispatchInfo?> UpdateLocalValueAsync<T>(LocalCacheFence.Ticket ticket, string key, T value, TimeSpan? expiresIn, bool valueKnown = true)
    {
        return UpdateLocalAsync(ticket, key, (value, expiresIn), static (cache, k, state) =>
            cache.SetAsync(k, state.value, state.expiresIn), canUpdate: valueKnown);
    }

    /// <summary>
    /// Applies a local update when the ticket is current, then evicts the key if the update was rejected or the key
    /// was invalidated while it ran. A write's local error is returned after eviction, so the caller can still notify
    /// peers before surfacing it. A read's local error is logged.
    /// </summary>
    private async Task<ExceptionDispatchInfo?> UpdateLocalAsync<TState>(LocalCacheFence.Ticket ticket, string key, TState state,
        Func<InMemoryCacheClient, string, TState, Task<bool>> update, bool canUpdate = true)
    {
        try
        {
            if (ticket.IsCurrent)
            {
                if (canUpdate && await update(_localCache, key, state).AnyContext() && ticket.IsCurrent)
                    return null;
            }
            else if (ticket.IsRead)
            {
                return null;
            }

            await _localCache.RemoveAsync(key).AnyContext();
        }
        catch (Exception ex)
        {
            await EvictAfterFailureAsync([key]).AnyContext();
            if (!ticket.IsRead)
                return ExceptionDispatchInfo.Capture(ex);

            _logger.LogWarning(ex, "Unable to store {Key} in the local cache after a distributed read", key);
        }

        return null;
    }

    private async Task<int> WriteBatchAsync<TState>(string[] keys, TState state, Func<ICacheClient, TState, Task<int>> write)
    {
        int count = 0;
        ExceptionDispatchInfo? error = null;
        using (var tickets = _fence.BeginWrites(keys))
        {
            try
            {
                count = await write(_distributedCache, state).AnyContext();
                int localCount = 0;
                if (count == keys.Length && tickets.AreAllCurrent())
                    localCount = await write(_localCache, state).AnyContext();

                if (localCount != keys.Length)
                {
                    await _localCache.RemoveAllAsync(keys).AnyContext();
                }
                else
                {
                    foreach (string key in keys)
                    {
                        if (!tickets.IsCurrent(key))
                            await _localCache.RemoveAsync(key).AnyContext();
                    }
                }
            }
            catch (Exception ex)
            {
                // Either cache can commit some keys before throwing. The result doesn't identify them.
                error = ExceptionDispatchInfo.Capture(ex);
                await EvictAfterFailureAsync(keys).AnyContext();
            }
        }

        await PublishInvalidationAsync(keys, error).AnyContext();
        return count;
    }

    private static string[] GetBatchKeys(IEnumerable<string> keys)
    {
        string[] items = keys.ToArray();
        foreach (string key in items)
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));
        return items;
    }

    /// <summary>
    /// Invalidates in-flight local updates for the key, then removes it from the local cache.
    /// </summary>
    private Task<bool> InvalidateLocalAsync(string key)
    {
        _fence.Invalidate(key);
        return _localCache.RemoveAsync(key);
    }

    private async Task EvictAfterFailureAsync(string[] keys)
    {
        foreach (string key in keys)
        {
            try
            {
                await _localCache.RemoveAsync(key).AnyContext();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing {Key} from the local cache after a failed update", key);
            }
        }
    }

    private async Task PublishInvalidationAsync(string[] keys, ExceptionDispatchInfo? error)
    {
        try
        {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = keys }).AnyContext();
        }
        catch (Exception ex) when (error is not null)
        {
            _logger.LogError(ex, "Error publishing cache invalidation after a failed cache update");
        }

        error?.Throw();
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
