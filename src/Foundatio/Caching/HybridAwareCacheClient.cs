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
    private readonly HybridCacheWriter _writer;
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
        _writer = new HybridCacheWriter(distributedCacheClient, InvalidateAfterDistributedFailureAsync);
        _messagePublisher = messagePublisher;
    }

    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    public async Task<bool> RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool removed = await _writer.RemoveAsync(key).AnyContext();

        // Only notify other nodes if the key actually existed and was removed from distributed cache.
        // If removed == false, the key didn't exist, so no other node needs to be notified.
        if (removed)
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool removed = await _writer.RemoveIfEqualAsync(key, expected).AnyContext();

        // Only notify other nodes if the key was actually removed from distributed cache.
        // If removed == false, either the key didn't exist or the value didn't match.
        if (removed)
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string>? keys = null)
    {
        string[]? items = keys?.ToArray();
        bool flushAll = items is null || items.Length == 0;
        int removed = await _writer.RemoveAllAsync(items).AnyContext();

        // Only notify other nodes if keys were actually removed from distributed cache.
        if (removed > 0)
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = items }).AnyContext();

        return removed;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        int removed = await _writer.RemoveByPrefixAsync(prefix).AnyContext();

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

        return _writer.AddAsync(key, value, expiresIn).AsTask();
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool set = await _writer.SetAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return set;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count is 0)
            return 0;

        string[] keys = values.Keys.ToArray();
        int set = await _writer.SetAllAsync(values, expiresIn, keys).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = keys }).AnyContext();
        return set;
    }

    public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool replaced = await _writer.ReplaceAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        bool replaced = await _writer.ReplaceIfEqualAsync(key, value, expected, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double incremented = await _writer.IncrementAsync(key, amount, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return incremented;
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long incremented = await _writer.IncrementAsync(key, amount, expiresIn).AnyContext();
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
        ArgumentNullException.ThrowIfNull(keys);

        return _distributedCache.GetAllExpirationAsync(keys);
    }

    public async Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await _writer.SetExpirationAsync(key, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);

        if (expirations.Count is 0)
            return;

        string[] keys = expirations.Keys.ToArray();
        await _writer.SetAllExpirationAsync(expirations, keys).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = keys }).AnyContext();
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double difference = await _writer.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long difference = await _writer.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double difference = await _writer.SetIfLowerAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        long difference = await _writer.SetIfLowerAsync(key, value, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        long added = await _writer.ListAddAsync(key, values, expiresIn).AnyContext();
        await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return added;
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        long removed = await _writer.ListRemoveAsync(key, values).AnyContext();
        if (removed > 0)
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return removed;
    }

    public Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (page.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThan(page.Value, 1);

        return _distributedCache.GetListAsync<T>(key, page, pageSize);
    }

    private async Task InvalidateAfterDistributedFailureAsync(string[]? keys, string? prefix)
    {
        if (prefix is not null)
            keys = [$"{prefix}*"];

        try
        {
            await _messagePublisher.PublishAsync(new HybridCacheClient.InvalidateCache { CacheId = _cacheId, Keys = keys, FlushAll = keys is null }).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing cache invalidation after a failed distributed write");
        }
    }

    public void Dispose()
    {
    }
}
