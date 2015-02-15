using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Foundatio.Messaging;
using NLog.Fluent;

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
            _messageBus.Subscribe<InvalidateCache>(OnMessage);
            _localCache.ItemExpired += (sender, key) => {
                _messageBus.Publish(new InvalidateCache {CacheId = _cacheId, Keys = new[] { key }});
                Log.Trace().Message("Item expired event: key={0}", key).Write();
            };
        }

        public InMemoryCacheClient LocalCache { get { return _localCache; } }
        public long LocalCacheHits { get { return _localCacheHits; } }
        public long InvalidateCacheCalls { get { return _invalidateCacheCalls; } }

        public int LocalCacheSize {
            get { return _localCache.MaxItems ?? -1; }
            set { _localCache.MaxItems = value; }
        }

        private void OnMessage(InvalidateCache message) {
            if (!String.IsNullOrEmpty(message.CacheId) && String.Equals(_cacheId, message.CacheId))
                return;

            Log.Trace().Message("Invalidating local cache from remote: id={0} keys={1}", message.CacheId, String.Join(",", message.Keys ?? new string[] { })).Write();
            Interlocked.Increment(ref _invalidateCacheCalls);
            if (message.FlushAll)
                _localCache.FlushAll();
            else if (message.Keys != null && message.Keys.Length > 0)
                _localCache.RemoveAll(message.Keys);
            else
                Log.Warn().Message("Unknown invalidate cache message").Write();
        }

        public bool Remove(string key) {
            if (String.IsNullOrEmpty(key))
                return true;

            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Remove(key);
            return _distributedCache.Remove(key);
        }

        public void RemoveAll(IEnumerable<string> keys) {
            if (keys == null)
                return;

            var keysToRemove = keys.ToArray();
            if (keysToRemove.Length == 0)
                return;

            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = keysToRemove });
            _localCache.RemoveAll(keysToRemove);
            _distributedCache.RemoveAll(keysToRemove);
        }

        public T Get<T>(string key) {
            T value;
            if (_localCache.TryGet(key, out value)) {
                Log.Trace().Message("Local cache hit: {0}", key).Write();
                Interlocked.Increment(ref _localCacheHits);
                return value;
            }

            if (_distributedCache.TryGet(key, out value)) {
                var expiration = _distributedCache.GetExpiration(key);
                if (expiration.HasValue)
                    _localCache.Set(key, value, expiration.Value);
                else
                    _localCache.Set(key, value);
            }

            return value;
        }

        public DateTime? GetExpiration(string key) {
            var expiration = _distributedCache.GetExpiration(key);
            if (expiration.HasValue)
                return expiration.Value;

            return _distributedCache.GetExpiration(key);
        }

        public bool TryGet<T>(string key, out T value) {
            if (_localCache.TryGet(key, out value)) {
                Log.Trace().Message("Local cache hit: {0}", key).Write();
                Interlocked.Increment(ref _localCacheHits);
                return true;
            }

            if (_distributedCache.TryGet(key, out value)) {
                _localCache.Set(key, value);
                return true;
            }

            return false;
        }

        public long Increment(string key, uint amount) {
            return _distributedCache.Increment(key, amount);
        }

        public long Increment(string key, uint amount, DateTime expiresAt) {
            return _distributedCache.Increment(key, amount, expiresAt);
        }

        public long Increment(string key, uint amount, TimeSpan expiresIn) {
            return _distributedCache.Increment(key, amount, expiresIn);
        }

        public long Decrement(string key, uint amount) {
            return _distributedCache.Decrement(key, amount);
        }

        public long Decrement(string key, uint amount, DateTime expiresAt) {
            return _distributedCache.Decrement(key, amount, expiresAt);
        }

        public long Decrement(string key, uint amount, TimeSpan expiresIn) {
            return _distributedCache.Decrement(key, amount, expiresIn);
        }

        public bool Add<T>(string key, T value) {
            _localCache.Add(key, value);
            return _distributedCache.Add(key, value);
        }

        public bool Add<T>(string key, T value, DateTime expiresAt) {
            _localCache.Add(key, value, expiresAt);
            return _distributedCache.Add(key, value, expiresAt);
        }

        public bool Add<T>(string key, T value, TimeSpan expiresIn) {
            _localCache.Add(key, value, expiresIn);
            return _distributedCache.Add(key, value, expiresIn);
        }

        public bool Set<T>(string key, T value) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Set(key, value);
            return _distributedCache.Set(key, value);
        }

        public bool Set<T>(string key, T value, DateTime expiresAt) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Set(key, value, expiresAt);
            return _distributedCache.Set(key, value, expiresAt);
        }

        public bool Set<T>(string key, T value, TimeSpan expiresIn) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Set(key, value, expiresIn);
            return _distributedCache.Set(key, value, expiresIn);
        }

        public bool Replace<T>(string key, T value) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Replace(key, value);
            return _distributedCache.Replace(key, value);
        }

        public bool Replace<T>(string key, T value, DateTime expiresAt) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Set(key, value, expiresAt);
            return _distributedCache.Set(key, value, expiresAt);
        }

        public bool Replace<T>(string key, T value, TimeSpan expiresIn) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Set(key, value, expiresIn);
            return _distributedCache.Set(key, value, expiresIn);
        }

        public void FlushAll() {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, FlushAll = true });
            _localCache.FlushAll();
            _distributedCache.FlushAll();
        }

        public IDictionary<string, T> GetAll<T>(IEnumerable<string> keys) {
            return _distributedCache.GetAll<T>(keys);
        }

        public void SetAll<T>(IDictionary<string, T> values) {
            if (values == null)
                return;

            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = values.Keys.ToArray() });
            _localCache.SetAll(values);
            _distributedCache.SetAll(values);
        }

        public void SetExpiration(string key, TimeSpan expiresIn) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Remove(key);
            _distributedCache.SetExpiration(key, expiresIn);
        }

        public void SetExpiration(string key, DateTime expiresAt) {
            _messageBus.Publish(new InvalidateCache { CacheId = _cacheId, Keys = new[] { key } });
            _localCache.Remove(key);
            _distributedCache.SetExpiration(key, expiresAt);
        }

        public void Dispose() { }

        public class InvalidateCache {
            public string CacheId { get; set; }
            public string[] Keys { get; set; }
            public bool FlushAll { get; set; }
        }
    }
}
