using System;
using System.Collections.Generic;

namespace Foundatio.Caching {
    public class NullCacheClient : ICacheClient {
        public bool Remove(string key)
        {
            return false;
        }

        public void RemoveAll(IEnumerable<string> keys)
        {
        }

        public T Get<T>(string key)
        {
            return default(T);
        }

        public bool TryGet<T>(string key, out T value)
        {
            value = default(T);
            return false;
        }

        public long Increment(string key, uint amount)
        {
            return amount;
        }

        public long Increment(string key, uint amount, DateTime expiresAt)
        {
            return amount;
        }

        public long Increment(string key, uint amount, TimeSpan expiresIn)
        {
            return amount;
        }

        public long Decrement(string key, uint amount)
        {
            return -amount;
        }

        public long Decrement(string key, uint amount, DateTime expiresAt)
        {
            return -amount;
        }

        public long Decrement(string key, uint amount, TimeSpan expiresIn)
        {
            return amount;
        }

        public bool Add<T>(string key, T value)
        {
            return true;
        }

        public bool Add<T>(string key, T value, DateTime expiresAt)
        {
            return true;
        }

        public bool Add<T>(string key, T value, TimeSpan expiresIn)
        {
            return true;
        }

        public bool Set<T>(string key, T value)
        {
            return true;
        }

        public bool Set<T>(string key, T value, DateTime expiresAt)
        {
            return true;
        }

        public bool Set<T>(string key, T value, TimeSpan expiresIn)
        {
            return true;
        }

        public bool Replace<T>(string key, T value)
        {
            return true;
        }

        public bool Replace<T>(string key, T value, DateTime expiresAt)
        {
            return true;
        }

        public bool Replace<T>(string key, T value, TimeSpan expiresIn)
        {
            return true;
        }

        public void FlushAll()
        {
        }

        public IDictionary<string, T> GetAll<T>(IEnumerable<string> keys)
        {
            return new Dictionary<string, T>();
        }

        public void SetAll<T>(IDictionary<string, T> values)
        {
        }

        public DateTime? GetExpiration(string key)
        {
            return null;
        }

        public void SetExpiration(string key, TimeSpan expiresIn)
        {
        }

        public void SetExpiration(string key, DateTime expiresAt)
        {
        }

        public void Dispose() {
        }
    }
}
