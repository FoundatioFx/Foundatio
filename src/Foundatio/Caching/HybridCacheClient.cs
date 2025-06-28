using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Utility;
using Foundatio.Utility.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching;

public interface IHybridCacheClient : ICacheClient { }

public class HybridCacheClient : IHybridCacheClient, IHaveTimeProvider, IHaveLogger, IHaveLoggerFactory, IHaveResiliencePipelineProvider
{
    protected readonly ICacheClient _distributedCache;
    protected readonly IMessageBus _messageBus;
    private readonly string _cacheId = Guid.NewGuid().ToString("N");
    private readonly InMemoryCacheClient _localCache;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IResiliencePipelineProvider _resiliencePipelineProvider;
    private long _localCacheHits;
    private long _invalidateCacheCalls;

    public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus, InMemoryCacheClientOptions localCacheOptions = null, ILoggerFactory loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? distributedCacheClient.GetLoggerFactory() ?? localCacheOptions.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HybridCacheClient>();
        _timeProvider = distributedCacheClient.GetTimeProvider() ?? localCacheOptions?.TimeProvider ?? TimeProvider.System;
        _resiliencePipelineProvider = distributedCacheClient.GetResiliencePipelineProvider() ?? localCacheOptions?.ResiliencePipelineProvider;
        _distributedCache = distributedCacheClient;
        _messageBus = messageBus;
        _messageBus.SubscribeAsync<InvalidateCache>(OnRemoteCacheItemExpiredAsync).AnyContext().GetAwaiter().GetResult();
        if (localCacheOptions == null)
            localCacheOptions = new InMemoryCacheClientOptions { LoggerFactory = loggerFactory };
        _localCache = new InMemoryCacheClient(localCacheOptions);
        _localCache.ItemExpired.AddHandler(OnLocalCacheItemExpiredAsync);
    }

    public InMemoryCacheClient LocalCache => _localCache;
    public long LocalCacheHits => _localCacheHits;
    public long InvalidateCacheCalls => _invalidateCacheCalls;

    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePipelineProvider IHaveResiliencePipelineProvider.ResiliencePipelineProvider => _resiliencePipelineProvider;

    private Task OnLocalCacheItemExpiredAsync(object sender, ItemExpiredEventArgs args)
    {
        if (!args.SendNotification)
            return Task.CompletedTask;

        _logger.LogTrace("Local cache expired event: key={Key}", args.Key);
        return _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [args.Key], Expired = true });
    }

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

        if (message.Keys != null && message.Keys.Length > 0)
        {
            var tasks = new List<Task>(message.Keys.Length);
            var keysToRemove = new List<string>(message.Keys.Length);
            foreach (string key in message.Keys)
            {
                if (message.Expired)
                    _localCache.RemoveExpiredKey(key, false);
                else if (key.EndsWith("*"))
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
        bool removed = await _distributedCache.RemoveAsync(key).AnyContext();
        await _localCache.RemoveAsync(key).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return removed;
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        bool removed = await _distributedCache.RemoveIfEqualAsync(key, expected).AnyContext();
        await _localCache.RemoveIfEqualAsync(key, expected).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return removed;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        string[] items = keys?.ToArray();
        bool flushAll = items == null || items.Length == 0;
        int removed = await _distributedCache.RemoveAllAsync(items).AnyContext();
        await _localCache.RemoveAllAsync(items).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = items }).AnyContext();
        return removed;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        int removed = await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
        await _localCache.RemoveByPrefixAsync(prefix).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [prefix + "*"] }).AnyContext();
        return removed;
    }

    public async Task<CacheValue<T>> GetAsync<T>(string key)
    {
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

    public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        return _distributedCache.GetAllAsync<T>(keys);
    }

    public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        _logger.LogTrace("Adding key {Key} to local cache with expiration: {Expiration}", key, expiresIn);
        bool added = await _distributedCache.AddAsync(key, value, expiresIn).AnyContext();
        if (added)
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();

        return added;
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        _logger.LogTrace("Setting key {Key} to local cache with expiration: {Expiration}", key, expiresIn);
        await _localCache.SetAsync(key, value, expiresIn).AnyContext();
        bool set = await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();

        return set;
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        if (values == null || values.Count == 0)
            return 0;

        _logger.LogTrace("Adding keys {Keys} to local cache with expiration: {Expiration}", values.Keys, expiresIn);
        await _localCache.SetAllAsync(values, expiresIn).AnyContext();
        int set = await _distributedCache.SetAllAsync(values, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = values.Keys.ToArray() }).AnyContext();
        return set;
    }

    public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        await _localCache.ReplaceAsync(key, value, expiresIn).AnyContext();
        bool replaced = await _distributedCache.ReplaceAsync(key, value, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        await _localCache.ReplaceIfEqualAsync(key, value, expected, expiresIn).AnyContext();
        bool replaced = await _distributedCache.ReplaceIfEqualAsync(key, value, expected, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return replaced;
    }

    public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        double incremented = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        await _localCache.ReplaceAsync(key, incremented, expiresIn);
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return incremented;
    }

    public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        long incremented = await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        await _localCache.ReplaceAsync(key, incremented, expiresIn);
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return incremented;
    }

    public Task<bool> ExistsAsync(string key)
    {
        return _distributedCache.ExistsAsync(key);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        return _distributedCache.GetExpirationAsync(key);
    }

    public async Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        await _localCache.SetExpirationAsync(key, expiresIn).AnyContext();
        await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        await _localCache.RemoveAsync(key).AnyContext();
        double difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        await _localCache.RemoveAsync(key).AnyContext();
        long difference = await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        await _localCache.RemoveAsync(key).AnyContext();
        double difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        await _localCache.RemoveAsync(key).AnyContext();
        long difference = await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
        await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
        return difference;
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (values is string stringValue)
        {
            await _localCache.ListAddAsync(key, stringValue, expiresIn).AnyContext();
            long set = await _distributedCache.ListAddAsync(key, stringValue, expiresIn).AnyContext();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return set;
        }
        else
        {
            var items = values?.ToArray();
            await _localCache.ListAddAsync(key, items, expiresIn).AnyContext();
            long set = await _distributedCache.ListAddAsync(key, items, expiresIn).AnyContext();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return set;
        }
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (values is string stringValue)
        {
            await _localCache.ListRemoveAsync(key, stringValue, expiresIn).AnyContext();
            long removed = await _distributedCache.ListRemoveAsync(key, stringValue, expiresIn).AnyContext();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return removed;
        }
        else
        {
            var items = values?.ToArray();
            await _localCache.ListRemoveAsync(key, items, expiresIn).AnyContext();
            long removed = await _distributedCache.ListRemoveAsync(key, items, expiresIn).AnyContext();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = [key] }).AnyContext();
            return removed;
        }
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
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
        _localCache.ItemExpired.RemoveHandler(OnLocalCacheItemExpiredAsync);
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
