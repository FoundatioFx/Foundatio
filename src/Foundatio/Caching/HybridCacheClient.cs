using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching {
    public interface IHybridCacheClient : ICacheClient { }

    public class HybridCacheClient : IHybridCacheClient {
        protected readonly ICacheClient _distributedCache;
        protected readonly IMessageBus _messageBus;
        private readonly string _cacheId = Guid.NewGuid().ToString("N");
        private readonly InMemoryCacheClient _localCache;
        private readonly ILogger _logger;
        private long _localCacheHits;
        private long _invalidateCacheCalls;

        public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<HybridCacheClient>() ?? NullLogger<HybridCacheClient>.Instance;
            _distributedCache = distributedCacheClient;
            _messageBus = messageBus;
            _messageBus.SubscribeAsync<InvalidateCache>(OnRemoteCacheItemExpiredAsync).GetAwaiter().GetResult();
            _localCache = new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = loggerFactory }) { MaxItems = 100 };
            _localCache.ItemExpired.AddHandler(OnLocalCacheItemExpiredAsync);
        }

        public InMemoryCacheClient LocalCache => _localCache;
        public long LocalCacheHits => _localCacheHits;
        public long InvalidateCacheCalls => _invalidateCacheCalls;

        public int LocalCacheSize {
            get => _localCache.MaxItems ?? -1;
            set => _localCache.MaxItems = value;
        }

        private Task OnLocalCacheItemExpiredAsync(object sender, ItemExpiredEventArgs args) {
            if (!args.SendNotification)
                return Task.CompletedTask;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Local cache expired event: key={Key}", args.Key);
            return _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { args.Key }, Expired = true });
        }

        private Task OnRemoteCacheItemExpiredAsync(InvalidateCache message) {
            if (!String.IsNullOrEmpty(message.CacheId) && String.Equals(_cacheId, message.CacheId))
                return Task.CompletedTask;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Invalidating local cache from remote: id={CacheId} expired={Expired} keys={Keys}", message.CacheId, message.Expired, String.Join(",", message.Keys ?? new string[] { }));
            Interlocked.Increment(ref _invalidateCacheCalls);
            if (message.FlushAll) {
                _logger.LogTrace("Flushed local cache");
                return _localCache.RemoveAllAsync();
            }

            if (message.Keys != null && message.Keys.Length > 0) {
                var tasks = new List<Task>(message.Keys.Length);
                var keysToRemove = new List<string>(message.Keys.Length);
                foreach (string key in message.Keys) {
                    if (message.Expired)
                        tasks.Add(_localCache.RemoveExpiredKeyAsync(key, false));
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

        public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            var items = keys?.ToArray();
            bool flushAll = items == null || items.Length == 0;
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = items }).AnyContext();
            await _localCache.RemoveAllAsync(items).AnyContext();
            return await _distributedCache.RemoveAllAsync(items).AnyContext();
        }

        public async Task<int> RemoveByPrefixAsync(string prefix) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { prefix + "*" } }).AnyContext();
            await _localCache.RemoveByPrefixAsync(prefix).AnyContext();
            return await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
        }

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            var cacheValue = await _localCache.GetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Local cache hit: {Key}", key);
                Interlocked.Increment(ref _localCacheHits);
                return cacheValue;
            }

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Local cache miss: {Key}", key);
            cacheValue = await _distributedCache.GetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", key, expiration);

                await _localCache.SetAsync(key, cacheValue.Value, expiration).AnyContext();
                return cacheValue;
            }

            return cacheValue.HasValue ? cacheValue : CacheValue<T>.NoValue;
        }

        public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            return _distributedCache.GetAllAsync<T>(keys);
        }

        public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Adding key {Key} to local cache with expiration: {Expiration}", key, expiresIn);
            bool added = await _distributedCache.AddAsync(key, value, expiresIn).AnyContext();
            if (added)
                await _localCache.SetAsync(key, value, expiresIn).AnyContext();

            return added;
        }

        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Setting key {Key} to local cache with expiration: {Expiration}", key, expiresIn);
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();

            return await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null || values.Count == 0)
                return 0;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Adding keys {Keys} to local cache with expiration: {Expiration}", values.Keys, expiresIn);
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = values.Keys.ToArray() }).AnyContext();
            await _localCache.SetAllAsync(values, expiresIn).AnyContext();
            return await _distributedCache.SetAllAsync(values, expiresIn).AnyContext();
        }

        public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.ReplaceAsync(key, value, expiresIn).AnyContext();
            return await _distributedCache.ReplaceAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.RemoveAsync(key).AnyContext();
            return await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        }

        public Task<bool> ExistsAsync(string key) {
            return _distributedCache.ExistsAsync(key);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            return _distributedCache.GetExpirationAsync(key);
        }

        public async Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            await _localCache.SetExpirationAsync(key, expiresIn).AnyContext();
            await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
        }

        public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.RemoveAsync(key).AnyContext();
            return await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.RemoveAsync(key).AnyContext();
            return await _distributedCache.SetIfLowerAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<long> SetAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            var items = values?.ToArray();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.SetAddAsync(key, items, expiresIn).AnyContext();
            return await _distributedCache.SetAddAsync(key, items, expiresIn).AnyContext();
        }

        public async Task<long> SetRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            var items = values?.ToArray();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.SetRemoveAsync(key, items, expiresIn).AnyContext();
            return await _distributedCache.SetRemoveAsync(key, items, expiresIn).AnyContext();
        }

        public async Task<CacheValue<ICollection<T>>> GetSetAsync<T>(string key) {
            var cacheValue = await _localCache.GetSetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Local cache hit: {Key}", key);
                Interlocked.Increment(ref _localCacheHits);
                return cacheValue;
            }

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Local cache miss: {Key}", key);
            cacheValue = await _distributedCache.GetSetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Setting Local cache key: {Key} with expiration: {Expiration}", key, expiration);

                await _localCache.SetAddAsync(key, cacheValue.Value, expiration).AnyContext();
                return cacheValue;
            }

            return cacheValue.HasValue ? cacheValue : CacheValue<ICollection<T>>.NoValue;
        }

        public virtual void Dispose() {
            _localCache.ItemExpired.RemoveHandler(OnLocalCacheItemExpiredAsync);
            _localCache.Dispose();

            // TODO: unsubscribe handler from messagebus.
        }

        public class InvalidateCache {
            public string CacheId { get; set; }
            public string[] Keys { get; set; }
            public bool FlushAll { get; set; }
            public bool Expired { get; set; }
        }
    }
}
