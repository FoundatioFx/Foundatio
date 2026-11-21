using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Resilience;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching;

/// <summary>
/// Hybrid Aware allows you to use a distributed cache client but notify any hybrid clients to invalidate their local cache.
/// </summary>
public interface IHybridAwareCacheClient : ICacheClient
{
}

/// <summary>
/// Hybrid Aware allows you to use a distributed cache client but notify any hybrid clients to invalidate their local cache.
/// </summary>
public class HybridAwareCacheClient : IHybridAwareCacheClient, IHaveTimeProvider, IHaveLogger, IHaveLoggerFactory, IHaveResiliencePolicyProvider
{
    protected readonly ICacheClient _distributedCache;
    protected readonly IMessagePublisher _messagePublisher;
    private readonly string _cacheId = Guid.NewGuid().ToString("N");
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IResiliencePolicyProvider _resiliencePolicyProvider;

    public HybridAwareCacheClient(ICacheClient distributedCacheClient, IMessagePublisher messagePublisher, ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? distributedCacheClient.GetLoggerFactory() ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HybridAwareCacheClient>();
        _timeProvider = distributedCacheClient.GetTimeProvider() ?? TimeProvider.System;
        _resiliencePolicyProvider = distributedCacheClient.GetResiliencePolicyProvider() ?? DefaultResiliencePolicyProvider.Instance;
        _distributedCache = distributedCacheClient;
        _messagePublisher = messagePublisher;
    }

    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    public async Task<bool> RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool removed = await WriteDistributedAsync(key, key, static (c, k) => c.RemoveAsync(k)).AnyContext();

        // Only notify other nodes if the key actually existed and was removed from distributed cache.
        // If removed == false, the key didn't exist, so no other node needs to be notified.
        if (removed)
            await PublishInvalidationAsync([key]).AnyContext();

        return removed;
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool removed = await WriteDistributedAsync(key, (key, expected), static (c, s) => c.RemoveIfEqualAsync(s.key, s.expected)).AnyContext();

        // Only notify other nodes if the key was actually removed from distributed cache.
        // If removed == false, either the key didn't exist or the value didn't match.
        if (removed)
            await PublishInvalidationAsync([key]).AnyContext();

        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string>? keys = null)
    {
        string[]? items = keys is null ? null : HybridCacheClient.GetBatchKeys(keys);
        bool flushAll = items is null || items.Length == 0;
        int removed;
        try
        {
            removed = await _distributedCache.RemoveAllAsync(items).AnyContext();
        }
        catch
        {
            await PublishAfterDistributedFailureAsync(flushAll ? null : items).AnyContext();
            throw;
        }

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = items }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        int removed;
        try
        {
            removed = await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
        }
        catch
        {
            await PublishAfterDistributedFailureAsync([prefix + "*"]).AnyContext();
            throw;
        }

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [prefix + "*"] }).AnyContext();

        return removed;
    }

    public Task<CacheValue<T>> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return _distributedCache.GetAsync<T>(key);
    }

    public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return _distributedCache.GetAllAsync<T>(keys);
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var add = WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.AddAsync(s.key, s.value, s.expiresIn));
        // Below the TTL floor the provider removes any existing key and returns false without saying whether one existed.
        // Forward the add so provider behavior such as ItemExpired is kept, then always tell peers.
        return expiresIn < CacheClientExtensions.MinimumExpiration ? InvalidateAfterExpiredAddAsync(key, add) : add.AsTask();
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool set = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();

        return set;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        var (batch, keys) = HybridCacheClient.SnapshotBatch(values);
        if (keys.Length is 0)
            return 0;

        int set;
        try
        {
            set = await _distributedCache.SetAllAsync(batch, expiresIn).AnyContext();
        }
        catch
        {
            await PublishAfterDistributedFailureAsync(keys).AnyContext();
            throw;
        }

        await PublishInvalidationAsync(keys).AnyContext();
        return set;
    }

    public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool replaced = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.ReplaceAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return replaced;
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool replaced = await WriteDistributedAsync(key, (key, value, expected, expiresIn), static (c, s) => c.ReplaceIfEqualAsync(s.key, s.value, s.expected, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return replaced;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double incremented = await WriteDistributedAsync(key, (key, amount, expiresIn), static (c, s) => c.IncrementAsync(s.key, s.amount, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return incremented;
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long incremented = await WriteDistributedAsync(key, (key, amount, expiresIn), static (c, s) => c.IncrementAsync(s.key, s.amount, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return incremented;
    }

    public Task<bool> ExistsAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return _distributedCache.ExistsAsync(key);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return _distributedCache.GetExpirationAsync(key);
    }

    public Task<IDictionary<string, TimeSpan?>> GetAllExpirationAsync(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return _distributedCache.GetAllExpirationAsync(keys);
    }

    public async Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        try
        {
            await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
        }
        catch
        {
            await PublishAfterDistributedFailureAsync([key]).AnyContext();
            throw;
        }

        await PublishInvalidationAsync([key]).AnyContext();
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);

        var (batch, keys) = HybridCacheClient.SnapshotBatch(expirations);
        if (keys.Length is 0)
            return;

        try
        {
            await _distributedCache.SetAllExpirationAsync(batch).AnyContext();
        }
        catch
        {
            await PublishAfterDistributedFailureAsync(keys).AnyContext();
            throw;
        }

        await PublishInvalidationAsync(keys).AnyContext();
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfHigherAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfHigherAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfLowerAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long difference = await WriteDistributedAsync(key, (key, value, expiresIn), static (c, s) => c.SetIfLowerAsync(s.key, s.value, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        long added = await WriteDistributedAsync(key, (key, values, expiresIn), static (c, s) => c.ListAddAsync(s.key, s.values, s.expiresIn)).AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return added;
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        long removed = await WriteDistributedAsync(key, (key, values), static (c, s) => c.ListRemoveAsync(s.key, s.values)).AnyContext();
        if (removed > 0)
            await PublishInvalidationAsync([key]).AnyContext();

        return removed;
    }

    public Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (page.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThan(page.Value, 1);

        return _distributedCache.GetListAsync<T>(key, page, pageSize);
    }

    private async Task<bool> InvalidateAfterExpiredAddAsync(string key, ValueTask<bool> add)
    {
        bool added = await add.AnyContext();
        await PublishInvalidationAsync([key]).AnyContext();
        return added;
    }

    /// <summary>
    /// Runs a single-key distributed write once. The provider can commit before it throws, so a failure notifies
    /// hybrid clients before the original exception propagates. The pooled builder keeps the success path from
    /// allocating when the provider completes asynchronously.
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
            await PublishAfterDistributedFailureAsync([key]).AnyContext();
            throw;
        }
    }

    private Task PublishInvalidationAsync(string[]? keys) =>
        _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = keys, FlushAll = keys is null });

    /// <summary>
    /// Asks hybrid clients to evict the keys, or every entry when <paramref name="keys"/> is null. A publish failure is
    /// logged so the caller still receives the provider's exception.
    /// </summary>
    private async Task PublishAfterDistributedFailureAsync(string[]? keys)
    {
        try
        {
            await PublishInvalidationAsync(keys).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing cache invalidation after a failed distributed write: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
    }
}
