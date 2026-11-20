using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
                    tasks.Add(InvalidateLocalPrefixAsync(key[..^1]));
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

        bool removed = await WriteDistributedAsync(key, key, static (c, k) => c.RemoveAsync(k)).AnyContext();
        await InvalidateLocalAsync(key).AnyContext();

        // Only notify other nodes if the key actually existed and was removed from distributed cache.
        // If removed == false, the key didn't exist, so no other node needs to be notified.
        if (removed)
            await PublishInvalidationAsync([key]).AnyContext();

        return removed;
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureSubscribedAsync().AnyContext();

        bool removed = await WriteDistributedAsync(key, (key, expected), static (c, s) => c.RemoveIfEqualAsync(s.key, s.expected)).AnyContext();

        // Always remove from local cache unconditionally. We use RemoveAsync (not RemoveIfEqualAsync)
        // because the local cache might have a stale value that doesn't match 'expected'.
        await InvalidateLocalAsync(key).AnyContext();

        // Only notify other nodes if the key was actually removed from distributed cache.
        // If removed == false, either the key didn't exist or the value didn't match.
        if (removed)
            await PublishInvalidationAsync([key]).AnyContext();

        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string>? keys = null)
    {
        await EnsureSubscribedAsync().AnyContext();

        string[]? items = keys is null ? null : GetBatchKeys(keys);
        int removed;
        try
        {
            removed = await _distributedCache.RemoveAllAsync(items).AnyContext();
        }
        catch
        {
            // A provider can treat an empty collection as a flush.
            await InvalidateAfterDistributedFailureAsync(items is { Length: > 0 } ? items : null).AnyContext();
            throw;
        }

        // A provider that removed entries for an empty collection treated it as a flush.
        string[]? localKeys = items is { Length: 0 } && removed > 0 ? null : items;
        _fence.Invalidate(localKeys);
        await _localCache.RemoveAllAsync(localKeys).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, FlushAll = localKeys is null, Keys = items }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        await EnsureSubscribedAsync().AnyContext();

        int removed;
        try
        {
            removed = await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
        }
        catch
        {
            await InvalidatePrefixAfterDistributedFailureAsync(prefix).AnyContext();
            throw;
        }

        await InvalidateLocalPrefixAsync(prefix).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await PublishInvalidationAsync([$"{prefix}*"]).AnyContext();

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

        Interlocked.Add(ref _localCacheHits, localValues.Count - (misses?.Count ?? 0));

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

            LogLocalCacheFill(ticket, kvp.Key, expiration);
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
        bool added = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.AddAsync(s.key, s.value, s.expiresIn)).AnyContext();
        if (added)
        {
            await UpdateLocalValueAsync(ticket, key, value, expiresIn, notify: false).AnyContext();
        }

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
        using var ticket = _fence.BeginWrite(key);
        bool updated = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await UpdateLocalValueAsync(ticket, key, value, expiresIn, storeLocally: updated).AnyContext();

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
        return await WriteBatchAsync(GetBatchKeys(values.Keys), cache => cache.SetAllAsync(values, expiresIn)).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        bool replaced = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.ReplaceAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await UpdateLocalValueAsync(ticket, key, value, expiresIn, storeLocally: replaced).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        bool replaced = await WriteDistributedAsync(key, (key, value, expected, expiresIn), static (c, s) => c.ReplaceIfEqualAsync(s.key, s.value, s.expected, s.expiresIn)).AnyContext();
        // A successful compare-and-set establishes the new value even if L1 held a different one.
        await UpdateLocalValueAsync(ticket, key, value, expiresIn, storeLocally: replaced).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        double newValue = await WriteDistributedAsync(key, (key, amount, expiresIn), static (c, s) => c.IncrementAsync(s.key, s.amount, s.expiresIn)).AnyContext();
        // Zero can also mean the increment failed. Evict it so the next read checks L2.
        // A successful nonzero result uses the requested expiration, including null to remove its TTL.
        await UpdateLocalValueAsync(ticket, key, newValue, expiresIn, storeLocally: newValue != 0).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        long newValue = await WriteDistributedAsync(key, (key, amount, expiresIn), static (c, s) => c.IncrementAsync(s.key, s.amount, s.expiresIn)).AnyContext();
        // Zero can also mean the increment failed. Evict it so the next read checks L2.
        // A successful nonzero result uses the requested expiration, including null to remove its TTL.
        await UpdateLocalValueAsync(ticket, key, newValue, expiresIn, storeLocally: newValue != 0).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        try
        {
            await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
        }
        catch
        {
            await InvalidateAfterDistributedFailureAsync([key]).AnyContext();
            throw;
        }

        await UpdateLocalAsync(ticket, key, expiresIn, static async (cache, k, expiration) =>
        {
            await cache.SetExpirationAsync(k, expiration).AnyContext();
            return true;
        }).AnyContext();
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);

        await EnsureSubscribedAsync().AnyContext();

        if (expirations.Count is 0)
            return;

        await WriteBatchAsync(GetBatchKeys(expirations.Keys), async cache =>
        {
            await cache.SetAllExpirationAsync(expirations).AnyContext();
            return expirations.Count;
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

        using var ticket = _fence.BeginWrite(key);
        double difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfHigherAsync(s.key, s.value, s.expiresIn)).AnyContext();
        // An unchanged result does not tell us which value is stored in L2.
        await UpdateLocalValueAsync(ticket, key, value, expiresIn, storeLocally: Math.Abs(difference) > double.Epsilon).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        long difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfHigherAsync(s.key, s.value, s.expiresIn)).AnyContext();
        // An unchanged result does not tell us which value is stored in L2.
        await UpdateLocalValueAsync(ticket, key, value, expiresIn, storeLocally: difference != 0).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        double difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfLowerAsync(s.key, s.value, s.expiresIn)).AnyContext();
        // An unchanged result does not tell us which value is stored in L2.
        await UpdateLocalValueAsync(ticket, key, value, expiresIn, storeLocally: Math.Abs(difference) > double.Epsilon).AnyContext();
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

        using var ticket = _fence.BeginWrite(key);
        long difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfLowerAsync(s.key, s.value, s.expiresIn)).AnyContext();
        // An unchanged result does not tell us which value is stored in L2.
        await UpdateLocalValueAsync(ticket, key, value, expiresIn, storeLocally: difference != 0).AnyContext();
        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        await EnsureSubscribedAsync().AnyContext();

        var (items, expectedCount) = MaterializeListValues(values);
        using var ticket = _fence.BeginWrite(key);
        // A cold writer can seed a complete local list only if the distributed key was also missing.
        bool hasLocalEntry = await _localCache.ExistsAsync(key).AnyContext();
        bool canUpdate = hasLocalEntry || (ticket.IsCurrent && await TryGetDistributedKeyExistenceAsync(key).AnyContext() is false);
        long addedCount = await WriteDistributedAsync(key, (key, items, expiresIn), static (c, s) => c.ListAddAsync(s.key, s.items, s.expiresIn)).AnyContext();
        await UpdateLocalAsync(ticket, key, (items, expectedCount, expiresIn, requireExistingList: hasLocalEntry),
            static async (cache, k, state) => await cache.ListAddAsync(k, state.items, state.expiresIn, state.requireExistingList).AnyContext() == state.expectedCount,
            canUpdate: canUpdate && addedCount == expectedCount).AnyContext();
        return addedCount;
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        await EnsureSubscribedAsync().AnyContext();

        var (items, expectedCount) = MaterializeListValues(values);
        using var ticket = _fence.BeginWrite(key);
        long removedCount = await WriteDistributedAsync(key, (key, items), static (c, s) => c.ListRemoveAsync(s.key, s.items)).AnyContext();
        await UpdateLocalAsync(ticket, key, (items, expectedCount),
            static async (cache, k, state) => await cache.ListRemoveAsync(k, state.items).AnyContext() == state.expectedCount,
            canUpdate: removedCount == expectedCount, notify: removedCount > 0).AnyContext();
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
        if (!result.HasValue || ticket.IsCurrent || await _localCache.ExistsAsync(key).AnyContext())
            return result;

        // A delayed peer notification can invalidate a read even when L2 already returned the new value.
        // Fetch once more to warm L1 without reinstalling a snapshot that may have been invalidated.
        var retry = _fence.BeginRead(key);
        if (!retry.IsCurrent)
            return result;

        try
        {
            await ReadAndStoreAsync(retry).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to refresh {Key} after an overlapping cache invalidation", key);
        }

        return result;

        async Task<CacheValue<T>> ReadAndStoreAsync(LocalCacheFence.Ticket observation)
        {
            var value = await read(_distributedCache, key).AnyContext();
            if (value.HasValue && observation.IsCurrent)
            {
                TimeSpan? expiration;
                try
                {
                    expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to get the expiration of {Key}, skipping the local update", key);
                    return value;
                }

                // A null TTL can also mean the key expired after the value was read.
                if (expiration.HasValue || await TryGetDistributedKeyExistenceAsync(key).AnyContext() is true)
                {
                    LogLocalCacheFill(observation, key, expiration);
                    await UpdateLocalAsync(observation, key, (value.Value, expiration), store).AnyContext();
                }
            }

            return value;
        }
    }

    private void LogLocalCacheFill(LocalCacheFence.Ticket ticket, string key, TimeSpan? expiration)
    {
        if (_logger.IsEnabled(LogLevel.Trace) && ticket.IsCurrent)
            _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", key, expiration);
    }

    private async ValueTask<bool?> TryGetDistributedKeyExistenceAsync(string key)
    {
        try
        {
            return await _distributedCache.ExistsAsync(key).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to check whether {Key} exists in the distributed cache, skipping the local update", key);
            return null;
        }
    }

    /// <summary>
    /// Stores a value learned from the distributed cache, or evicts the key if the ticket no longer allows it.
    /// </summary>
    private Task UpdateLocalValueAsync<T>(LocalCacheFence.Ticket ticket, string key, T value, TimeSpan? expiresIn, bool storeLocally = true, bool notify = true)
    {
        return UpdateLocalAsync(ticket, key, (value, expiresIn), static (cache, k, state) =>
            cache.SetAsync(k, state.value, state.expiresIn), canUpdate: storeLocally, notify: notify);
    }

    /// <summary>
    /// Applies a local update when the ticket is current, then evicts the key if the update was rejected or the key
    /// was invalidated while it ran. A committed write still notifies peers if the local update fails.
    /// </summary>
    private async Task UpdateLocalAsync<TState>(LocalCacheFence.Ticket ticket, string key, TState state,
        Func<InMemoryCacheClient, string, TState, Task<bool>> update, bool canUpdate = true, bool notify = true)
    {
        bool notifyPeers = notify && !ticket.IsRead;
        try
        {
            bool stored = false;
            if (canUpdate && ticket.IsCurrent)
                stored = await update(_localCache, key, state).AnyContext();
            else if (ticket.IsRead)
                return;

            if (!stored || !ticket.IsCurrent)
                await _localCache.RemoveAsync(key).AnyContext();
        }
        catch (Exception ex)
        {
            await InvalidateAfterFailureAsync([key], notifyPeers).AnyContext();
            if (!ticket.IsRead)
                throw;

            _logger.LogWarning(ex, "Unable to store {Key} in the local cache after a distributed read", key);
        }

        if (notifyPeers)
            await PublishInvalidationAsync([key]).AnyContext();
    }

    private async Task<int> WriteBatchAsync(string[] keys, Func<ICacheClient, Task<int>> write)
    {
        int count;
        using (var tickets = _fence.BeginWrites(keys))
        {
            try
            {
                count = await write(_distributedCache).AnyContext();
                int localCount = 0;
                if (count == keys.Length && tickets.AreAllCurrent())
                    localCount = await write(_localCache).AnyContext();

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
            catch
            {
                // Either cache can commit some keys before throwing. The result doesn't identify them.
                await InvalidateAfterFailureAsync(keys, notify: true).AnyContext();
                throw;
            }
        }

        await PublishInvalidationAsync(keys).AnyContext();
        return count;
    }

    /// <summary>
    /// Invalidates in-flight local updates for the key, then removes it from the local cache.
    /// </summary>
    private Task<bool> InvalidateLocalAsync(string key)
    {
        _fence.Invalidate(key);
        return _localCache.RemoveAsync(key);
    }

    private Task<int> InvalidateLocalPrefixAsync(string prefix)
    {
        // The fence stores hash slots without key names, so a prefix must invalidate every in-flight ticket.
        // The local cache still removes only matching entries.
        _fence.InvalidateAll();
        return _localCache.RemoveByPrefixAsync(prefix);
    }

    /// <summary>
    /// Runs a single-key distributed write once. The provider can commit before it throws, so a failure evicts the
    /// key locally and notifies peers before the original exception propagates. The pooled builder keeps the success
    /// path from allocating when the provider completes asynchronously.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TResult> WriteDistributedAsync<TState, TResult>(string key, TState state, Func<ICacheClient, TState, Task<TResult>> write)
    {
        try
        {
            return await write(_distributedCache, state).AnyContext();
        }
        catch
        {
            await InvalidateAfterDistributedFailureAsync([key]).AnyContext();
            throw;
        }
    }

    /// <summary>
    /// Evicts the keys, or every local entry when <paramref name="keys"/> is null, after a distributed write whose
    /// outcome is unknown. Local and publish failures are logged so the caller receives the provider's exception.
    /// </summary>
    private async Task InvalidateAfterDistributedFailureAsync(string[]? keys)
    {
        _fence.Invalidate(keys);
        if (keys is not null)
        {
            await InvalidateAfterFailureAsync(keys, notify: true).AnyContext();
            return;
        }

        try
        {
            await _localCache.RemoveAllAsync().AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing the local cache after a failed distributed removal");
        }

        await TryPublishInvalidationAsync(null).AnyContext();
    }

    private async Task InvalidatePrefixAfterDistributedFailureAsync(string prefix)
    {
        _fence.InvalidateAll();
        try
        {
            await _localCache.RemoveByPrefixAsync(prefix).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing prefix {Prefix} from the local cache after a failed distributed removal", prefix);
        }

        await TryPublishInvalidationAsync([$"{prefix}*"]).AnyContext();
    }

    internal static string[] GetBatchKeys(IEnumerable<string> keys)
    {
        string[] items = keys.ToArray();
        foreach (string key in items)
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));

        return items;
    }

    private async Task InvalidateAfterFailureAsync(string[] keys, bool notify)
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

        if (notify)
            await TryPublishInvalidationAsync(keys).AnyContext();
    }

    private async Task TryPublishInvalidationAsync(string[]? keys)
    {
        try
        {
            await PublishInvalidationAsync(keys).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing cache invalidation after a failed cache update");
        }
    }

    private Task PublishInvalidationAsync(string[]? keys)
    {
        return _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = keys, FlushAll = keys is null });
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
