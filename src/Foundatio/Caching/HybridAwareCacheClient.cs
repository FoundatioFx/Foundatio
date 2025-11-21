using System;
using System.Collections.Generic;
using System.Linq;
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

    public HybridAwareCacheClient(ICacheClient distributedCacheClient, IMessagePublisher messagePublisher, ILoggerFactory loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? distributedCacheClient.GetLoggerFactory() ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HybridAwareCacheClient>();
        _timeProvider = distributedCacheClient.GetTimeProvider() ?? TimeProvider.System;
        _resiliencePolicyProvider = distributedCacheClient.GetResiliencePolicyProvider();
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

        bool removed = await _distributedCache.RemoveAsync(key).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return removed;
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool removed = await _distributedCache.RemoveIfEqualAsync(key, expected).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        string[] items = keys?.ToArray();
        bool flushAll = items == null || items.Length == 0;
        int removed = await _distributedCache.RemoveAllAsync(items).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = items }).AnyContext();
        return removed;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        int removed = await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
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
        if (keys is null)
            return Task.FromException<IDictionary<string, CacheValue<T>>>(new ArgumentNullException(nameof(keys)));

        return _distributedCache.GetAllAsync<T>(keys);
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return _distributedCache.AddAsync(key, value, expiresIn);
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool set = await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return set;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        if (values is null || values.Count == 0)
            return 0;

        int set = await _distributedCache.SetAllAsync(values, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = values.Keys.ToArray() }).AnyContext();
        return set;
    }

    public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool replaced = await _distributedCache.ReplaceAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool replaced = await _distributedCache.ReplaceIfEqualAsync(key, value, expected, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double incremented = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return incremented;
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long incremented = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
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
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));

        return _distributedCache.GetAllExpirationAsync(keys);
    }

    public async Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        if (expirations is null)
            throw new ArgumentNullException(nameof(expirations));

        if (expirations.Count is 0)
            return;

        await _distributedCache.SetAllExpirationAsync(expirations).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = expirations.Keys.ToArray() }).AnyContext();
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (values is null)
            throw new ArgumentNullException(nameof(values));

        if (values is string stringValue)
        {
            long set = await _distributedCache.ListAddAsync(key, stringValue, expiresIn).AnyContext();
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return set;
        }
        else
        {
            long set = await _distributedCache.ListAddAsync(key, values, expiresIn).AnyContext();
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return set;
        }
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (values is null)
            throw new ArgumentNullException(nameof(values));

        if (values is string stringValue)
        {
            long removed = await _distributedCache.ListRemoveAsync(key, stringValue, expiresIn).AnyContext();
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return removed;
        }
        else
        {
            long removed = await _distributedCache.ListRemoveAsync(key, values, expiresIn).AnyContext();
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return removed;
        }
    }

    public Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (page is < 1)
            return Task.FromException<CacheValue<ICollection<T>>>(new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1"));

        return _distributedCache.GetListAsync<T>(key, page, pageSize);
    }

    public void Dispose()
    {
    }
}
