using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Messaging;

namespace Foundatio.Caching {
    public class HybridCacheClient : ICacheClient {
        private readonly string _cacheId = Guid.NewGuid().ToString("N");
        private readonly ICacheClient _distributedCache;
        private readonly InMemoryCacheClient _localCache;
        private readonly IMessageBus _messageBus;
        private readonly ILogger _logger;
        private long _localCacheHits;
        private long _invalidateCacheCalls;

        public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus, ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<HybridCacheClient>();
            _distributedCache = distributedCacheClient;
            _localCache = new InMemoryCacheClient(loggerFactory);
            _localCache.MaxItems = 100;
            _messageBus = messageBus;
            _messageBus.Subscribe<InvalidateCache>(async cache => await OnMessageAsync(cache).AnyContext());
            _localCache.ItemExpired.AddHandler(async (sender, args) => {
                await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { args.Key } }).AnyContext();
                _logger.Trace("Item expired event: key={0}", args.Key);
            });
        }

        public InMemoryCacheClient LocalCache => _localCache;
        public long LocalCacheHits => _localCacheHits;
        public long InvalidateCacheCalls => _invalidateCacheCalls;

        public int LocalCacheSize {
            get { return _localCache.MaxItems ?? -1; }
            set { _localCache.MaxItems = value; }
        }

        private async Task OnMessageAsync(InvalidateCache message) {
            if (!String.IsNullOrEmpty(message.CacheId) && String.Equals(_cacheId, message.CacheId))
                return;

            _logger.Trace("Invalidating local cache from remote: id={0} keys={1}", message.CacheId, String.Join(",", message.Keys ?? new string[] { }));
            Interlocked.Increment(ref _invalidateCacheCalls);
            if (message.FlushAll) {
                await _localCache.RemoveAllAsync().AnyContext();
                _logger.Trace("Fushed local cache");
            } else if (message.Keys != null && message.Keys.Length > 0) {
                foreach (var pattern in message.Keys.Where(k => k.EndsWith("*")))
                    await _localCache.RemoveByPrefixAsync(pattern.Substring(0, pattern.Length - 1)).AnyContext();

                await _localCache.RemoveAllAsync(message.Keys.Where(k => !k.EndsWith("*"))).AnyContext();
                _logger.Trace("Removed keys from local cache");
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
            CacheValue<T> cacheValue;
            bool requiresSerialization = TypeRequiresSerialization(typeof(T));
            _logger.Trace("Type requires serialization: {0}", requiresSerialization);

            if (requiresSerialization) {
                cacheValue = await _localCache.GetAsync<T>(key).AnyContext();
                if (cacheValue.HasValue) {
                    _logger.Trace("Local cache hit: {0}", key);
                    Interlocked.Increment(ref _localCacheHits);
                    return cacheValue;
                }
            }

            _logger.Trace("Local cache miss: {0}", key);
            cacheValue = await _distributedCache.GetAsync<T>(key).AnyContext();
            if (requiresSerialization && cacheValue.HasValue) {
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
            if (TypeRequiresSerialization(typeof(T)))
                await _localCache.AddAsync(key, value, expiresIn).AnyContext();

            return await _distributedCache.AddAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (TypeRequiresSerialization(typeof(T))) {
                _logger.Trace("Adding key {0} to local cache.", key);

                await _messageBus.PublishAsync(new InvalidateCache {CacheId = _cacheId, Keys = new[] {key}}).AnyContext();
                await _localCache.SetAsync(key, value, expiresIn).AnyContext();
            }

            return await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null || values.Count == 0)
                return 0;

            if (TypeRequiresSerialization(typeof(T))) {
                _logger.Trace("Adding keys {0} to local cache.", values.Keys);

                await _localCache.SetAllAsync(values, expiresIn).AnyContext();
                await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = values.Keys.ToArray() }).AnyContext();
            }

            return await _distributedCache.SetAllAsync(values, expiresIn).AnyContext();
        }

        public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (TypeRequiresSerialization(typeof(T))) {
                await _messageBus.PublishAsync(new InvalidateCache {CacheId = _cacheId, Keys = new[] {key}}).AnyContext();
                await _localCache.ReplaceAsync(key, value, expiresIn).AnyContext();
            }

            return await _distributedCache.ReplaceAsync(key, value, expiresIn).AnyContext();
        }

        public Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            return _distributedCache.IncrementAsync(key, amount, expiresIn);
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
            await _localCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
            return await _distributedCache.SetIfHigherAsync(key, value, expiresIn).AnyContext();
        }

        public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            return _distributedCache.SetIfLowerAsync(key, value, expiresIn);
        }

        private bool TypeRequiresSerialization(Type t) {
            if (t == typeof(Int16) || t == typeof(Int32) || t == typeof(Int64) ||
                t == typeof(bool) || t == typeof(double) || t == typeof(string) ||
                t == typeof(Int16?) || t == typeof(Int32?) || t == typeof(Int64?) ||
                t == typeof(bool?) || t == typeof(double?))
                return false;

            return true;
        }

        public void Dispose() { }

        public class InvalidateCache {
            public string CacheId { get; set; }
            public string[] Keys { get; set; }
            public bool FlushAll { get; set; }
        }   
    }
}
