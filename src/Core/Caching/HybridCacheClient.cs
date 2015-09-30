using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Messaging;
using Foundatio.Logging;

namespace Foundatio.Caching {
    public class HybridCacheClient : ICacheClient {
        private readonly string _cacheId = Guid.NewGuid().ToString("N");
        private readonly ICacheClient _distributedCache;
        private readonly InMemoryCacheClient _localCache = new InMemoryCacheClient();
        private readonly IMessageBus _messageBus;
        private long _localCacheHits;
        private long _invalidateCacheCalls;

        public HybridCacheClient(ICacheClient distributedCacheClient, IMessageBus messageBus) {
            _distributedCache = distributedCacheClient;
            _localCache.MaxItems = 100;
            _messageBus = messageBus;
            _messageBus.Subscribe<InvalidateCache>(async cache => await OnMessageAsync(cache).AnyContext());
            _localCache.ItemExpired += async (sender, args) => {
                await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { args.Key } }).AnyContext();
                Logger.Trace().Message("Item expired event: key={0}", args.Key).Write();
            };
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

            Logger.Trace().Message("Invalidating local cache from remote: id={0} keys={1}", message.CacheId, String.Join(",", message.Keys ?? new string[] { })).Write();
            Interlocked.Increment(ref _invalidateCacheCalls);
            if (message.FlushAll)
                await _localCache.RemoveAllAsync().AnyContext();
            else if (message.Keys != null && message.Keys.Length > 0) {
                foreach (var pattern in message.Keys.Where(k => k.EndsWith("*")))
                    await _localCache.RemoveByPrefixAsync(pattern.Substring(0, pattern.Length - 1)).AnyContext();

                await _localCache.RemoveAllAsync(message.Keys.Where(k => !k.EndsWith("*"))).AnyContext();
            } else
                Logger.Warn().Message("Unknown invalidate cache message").Write();
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
            if (requiresSerialization) {
                cacheValue = await _localCache.GetAsync<T>(key).AnyContext();
                if (cacheValue.HasValue) {
                    Logger.Trace().Message("Local cache hit: {0}", key).Write();
                    Interlocked.Increment(ref _localCacheHits);
                    return cacheValue;
                }
            }

            cacheValue = await _distributedCache.GetAsync<T>(key).AnyContext();
            if (requiresSerialization && cacheValue.HasValue) {
                var expiration = await _distributedCache.GetExpirationAsync(key).AnyContext();

                Logger.Trace().Message($"Setting Local cache key: {key} with expiration: {expiration}").Write();
                await _localCache.SetAsync(key, cacheValue.Value, expiration).AnyContext();
                return cacheValue;
            }

            if (cacheValue.HasValue)
                return cacheValue;

            return CacheValue<T>.NoValue;
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
                await _messageBus.PublishAsync(new InvalidateCache {CacheId = _cacheId, Keys = new[] {key}}).AnyContext();
                await _localCache.SetAsync(key, value, expiresIn).AnyContext();
            }
            return await _distributedCache.SetAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null)
                return 0;

            if (TypeRequiresSerialization(typeof(T))) {
                await _messageBus.PublishAsync(new InvalidateCache {CacheId = _cacheId, Keys = values.Keys.ToArray()}).AnyContext();
                await _localCache.SetAllAsync(values, expiresIn).AnyContext();
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

        public async Task<long> IncrementAsync(string key, int amount = 1, TimeSpan? expiresIn = null) {
            return await _distributedCache.IncrementAsync(key, amount, expiresIn).AnyContext();
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            return _distributedCache.GetExpirationAsync(key);
        }

        public async Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            await _messageBus.PublishAsync(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } }).AnyContext();
            await _localCache.SetExpirationAsync(key, expiresIn).AnyContext();
            await _distributedCache.SetExpirationAsync(key, expiresIn).AnyContext();
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
