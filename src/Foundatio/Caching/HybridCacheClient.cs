using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Logging;
using Foundatio.Messaging;

namespace Foundatio.Caching {
    public interface IHybridCacheClient : ICacheClient { }

    public class HybridCacheClient : IHybridCacheClient {
        private readonly string _cacheId = Guid.NewGuid().ToString("N");
        protected readonly ICacheClient _distributedCache;
        private readonly InMemoryCacheClient _localCache;
        protected readonly IMessageBus _messageBus;
        private readonly ILogger _logger;
        private long _localCacheHits;
        private long _invalidateCacheCalls;

        public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger<HybridCacheClient>();
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
            get { return _localCache.MaxItems ?? -1; }
            set { _localCache.MaxItems = value; }
        }

        private async Task OnLocalCacheItemExpiredAsync(object sender, ItemExpiredEventArgs args) {
            if (!args.SendNotification)
                return;

            _logger.Trace("Local cache expired event: key={0}", args.Key);
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { args.Key }, Expired = true }).AnyContext();
        }

        private async Task OnRemoteCacheItemExpiredAsync(InvalidateCache message) {
            if (!String.IsNullOrEmpty(message.CacheId) && String.Equals(_cacheId, message.CacheId))
                return;

            _logger.Trace(() => String.Format("Invalidating local cache from remote: id={0} expired={1} keys={2}", message.CacheId, message.Expired, String.Join(",", message.Keys ?? new string[] { })));
            Interlocked.Increment(ref _invalidateCacheCalls);
            if (message.FlushAll) {
                await _localCache.RemoveAllAsync().AnyContext();
                _logger.Trace("Fushed local cache");
            } else if (message.Keys != null && message.Keys.Length > 0) {
                var keysToRemove = new List<string>(message.Keys.Length);
                foreach (string key in message.Keys) {
                    if (message.Expired)
                        await _localCache.RemoveExpiredKeyAsync(key, false).AnyContext();
                    else if (key.EndsWith("*"))
                        await _localCache.RemoveByPrefixAsync(key.Substring(0, key.Length - 1)).AnyContext();
                    else
                        keysToRemove.Add(key);
                }

                int results = await _localCache.RemoveAllAsync(keysToRemove).AnyContext();
                _logger.Trace("Removed {0} keys from local cache", results);
            } else {
                _logger.Warn("Unknown invalidate cache message");
            }
        }

        public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            bool flushAll = keys == null || !keys.Any();
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, FlushAll = flushAll, Keys = keys?.ToArray() }).AnyContext();
            await _localCache.RemoveAllAsync(keys).AnyContext();
            return await _distributedCache.RemoveAllAsync(keys).AnyContext();
        }

        public async Task<int> RemoveByPrefixAsync(string prefix) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { prefix + "*" } }).AnyContext();
            await _localCache.RemoveByPrefixAsync(prefix).AnyContext();
            return await _distributedCache.RemoveByPrefixAsync(prefix).AnyContext();
        }

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            var cacheValue = await _localCache.GetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                _logger.Trace("Local cache hit: {0}", key);
                Interlocked.Increment(ref _localCacheHits);
                return cacheValue;
            }

            _logger.Trace("Local cache miss: {0}", key);
            cacheValue = await _distributedCache.GetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
                _logger.Trace("Setting Local cache key: {0} with expiration: {1}", key, expiration);

                await _localCache.SetAsync(key, cacheValue.Value, expiration).AnyContext();
                return cacheValue;
            }

            return cacheValue.HasValue ? cacheValue : CacheValue<T>.NoValue;
        }

        public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            return _distributedCache.GetAllAsync<T>(keys);
        }

        public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            _logger.Trace("Adding key \"{0}\" to local cache with expiration: {1}", key, expiresIn);
            bool added = await _distributedCache.AddAsync(key, value, expiresIn).AnyContext();
            if (added)
                await _localCache.SetAsync(key, value, expiresIn).AnyContext();

            return added;
        }

        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            _logger.Trace("Setting key \"{0}\" to local cache with expiration: {1}", key, expiresIn);
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.SetAsync(key, value, expiresIn).AnyContext();

            return await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null || values.Count == 0)
                return 0;

            _logger.Trace("Adding keys \"{0}\" to local cache with expiration: {1}", values.Keys, expiresIn);
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
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.SetAddAsync(key, values, expiresIn).AnyContext();
            return await _distributedCache.SetAddAsync(key, values, expiresIn).AnyContext();
        }

        public async Task<long> SetRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.SetRemoveAsync(key, values, expiresIn).AnyContext();
            return await _distributedCache.SetRemoveAsync(key, values, expiresIn).AnyContext();
        }

        public async Task<CacheValue<ICollection<T>>> GetSetAsync<T>(string key) {
            var cacheValue = await _localCache.GetSetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                _logger.Trace("Local cache hit: {0}", key);
                Interlocked.Increment(ref _localCacheHits);
                return cacheValue;
            }

            _logger.Trace("Local cache miss: {0}", key);
            cacheValue = await _distributedCache.GetSetAsync<T>(key).AnyContext();
            if (cacheValue.HasValue) {
                var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();
                _logger.Trace("Setting Local cache key: {0} with expiration: {1}", key, expiration);

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
