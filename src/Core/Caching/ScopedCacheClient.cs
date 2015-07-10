using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Caching {
    public class ScopedCacheClient : ICacheClient {
        public ScopedCacheClient(ICacheClient client, string scope)
        {
            UnscopedCache = client ?? new NullCacheClient();
            Scope = scope;
        }

        public ICacheClient UnscopedCache { get; private set; }

        public string Scope { get; private set; }

        protected string GetScopedCacheKey(string key)
        {
            return String.Concat(Scope, ":", key);
        }

        protected IEnumerable<string> GetScopedCacheKey(IEnumerable<string> keys) {
            return keys.Select(GetScopedCacheKey);
        }

        public bool Remove(string key)
        {
            return UnscopedCache.Remove(GetScopedCacheKey(key));
        }

        public void RemoveAll(IEnumerable<string> keys)
        {
            UnscopedCache.RemoveAll(GetScopedCacheKey(keys));
        }

        public T Get<T>(string key)
        {
            return UnscopedCache.Get<T>(GetScopedCacheKey(key));
        }

        public bool TryGet<T>(string key, out T value)
        {
            return UnscopedCache.TryGet<T>(GetScopedCacheKey(key), out value);
        }

        public long Increment(string key, uint amount)
        {
            return UnscopedCache.Increment(GetScopedCacheKey(key), amount);
        }

        public long Increment(string key, uint amount, DateTime expiresAt)
        {
            return UnscopedCache.Increment(GetScopedCacheKey(key), amount, expiresAt);
        }

        public long Increment(string key, uint amount, TimeSpan expiresIn)
        {
            return UnscopedCache.Increment(GetScopedCacheKey(key), amount, expiresIn);
        }

        public long Decrement(string key, uint amount)
        {
            return UnscopedCache.Decrement(GetScopedCacheKey(key), amount);
        }

        public long Decrement(string key, uint amount, DateTime expiresAt)
        {
            return UnscopedCache.Decrement(GetScopedCacheKey(key), amount, expiresAt);
        }

        public long Decrement(string key, uint amount, TimeSpan expiresIn)
        {
            return UnscopedCache.Decrement(GetScopedCacheKey(key), amount, expiresIn);
        }

        public bool Add<T>(string key, T value)
        {
            return UnscopedCache.Add<T>(GetScopedCacheKey(key), value);
        }

        public bool Add<T>(string key, T value, DateTime expiresAt)
        {
            return UnscopedCache.Add(GetScopedCacheKey(key), value, expiresAt);
        }

        public bool Add<T>(string key, T value, TimeSpan expiresIn)
        {
            return UnscopedCache.Add(GetScopedCacheKey(key), value, expiresIn);
        }

        public bool Set<T>(string key, T value)
        {
            return UnscopedCache.Set(GetScopedCacheKey(key), key);
        }

        public bool Set<T>(string key, T value, DateTime expiresAt)
        {
            return UnscopedCache.Set(GetScopedCacheKey(key), value, expiresAt);
        }

        public bool Set<T>(string key, T value, TimeSpan expiresIn)
        {
            return UnscopedCache.Set<T>(GetScopedCacheKey(key), value, expiresIn);
        }

        public bool Replace<T>(string key, T value)
        {
            return UnscopedCache.Replace(GetScopedCacheKey(key), value);
        }

        public bool Replace<T>(string key, T value, DateTime expiresAt)
        {
            return UnscopedCache.Replace(GetScopedCacheKey(key), value, expiresAt);
        }

        public bool Replace<T>(string key, T value, TimeSpan expiresIn)
        {
            return UnscopedCache.Replace(GetScopedCacheKey(key), value, expiresIn);
        }

        public void FlushAll()
        {
            // TODO: flush only the scoped keys if possible.
            UnscopedCache.FlushAll();
        }

        public IDictionary<string, T> GetAll<T>(IEnumerable<string> keys)
        {
            return UnscopedCache.GetAll<T>(GetScopedCacheKey(keys));
        }

        public void SetAll<T>(IDictionary<string, T> values)
        {
            UnscopedCache.SetAll(values.ToDictionary(kvp => GetScopedCacheKey(kvp.Key), kvp => kvp.Value));
        }

        public DateTime? GetExpiration(string key)
        {
            return UnscopedCache.GetExpiration(GetScopedCacheKey(key));
        }

        public void SetExpiration(string key, TimeSpan expiresIn)
        {
            UnscopedCache.SetExpiration(GetScopedCacheKey(key), expiresIn);
        }

        public void SetExpiration(string key, DateTime expiresAt)
        {
            UnscopedCache.SetExpiration(GetScopedCacheKey(key), expiresAt);
        }

        public void Dispose() {}
    }
}
