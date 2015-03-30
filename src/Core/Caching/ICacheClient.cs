using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Foundatio.Caching {
    public interface ICacheClient : IDisposable {
        bool Remove(string key);
        void RemoveAll(IEnumerable<string> keys);
        T Get<T>(string key);
        bool TryGet<T>(string key, out T value);
        long Increment(string key, uint amount);
        long Increment(string key, uint amount, DateTime expiresAt);
        long Increment(string key, uint amount, TimeSpan expiresIn);
        long Decrement(string key, uint amount);
        long Decrement(string key, uint amount, DateTime expiresAt);
        long Decrement(string key, uint amount, TimeSpan expiresIn);
        bool Add<T>(string key, T value);
        bool Add<T>(string key, T value, DateTime expiresAt);
        bool Add<T>(string key, T value, TimeSpan expiresIn);
        bool Set<T>(string key, T value);
        bool Set<T>(string key, T value, DateTime expiresAt);
        bool Set<T>(string key, T value, TimeSpan expiresIn);
        bool Replace<T>(string key, T value);
        bool Replace<T>(string key, T value, DateTime expiresAt);
        bool Replace<T>(string key, T value, TimeSpan expiresIn);
        void FlushAll();
        IDictionary<string, T> GetAll<T>(IEnumerable<string> keys);
        void SetAll<T>(IDictionary<string, T> values);
        DateTime? GetExpiration(string cacheKey);
        void SetExpiration(string cacheKey, TimeSpan expiresIn);
        void SetExpiration(string cacheKey, DateTime expiresAt);
    }

    public interface ICacheClient2 : IDisposable {
        Task<bool> TryGetAsync<T>(string key, out T value);
        Task<IDictionary<string, object>> GetAllAsync(IEnumerable<string> keys);
        Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<int> SetAllAsync(IDictionary<string, object> values, TimeSpan? expiresIn = null);
        Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<long> IncrementAsync(string key, int amount = 1, TimeSpan? expiresIn = null);
        Task<int> RemoveAllAsync(IEnumerable<string> keys = null);
        Task<TimeSpan?> GetExpirationAsync(string key);
        Task SetExpirationAsync(string key, TimeSpan expiresIn);
    }

    public static class CacheClientExtensions {
        public static bool Remove(this ICacheClient2 client, string key) {
            return client.RemoveAllAsync(new[] { key }).Result == 1;
        }

        public static void RemoveAll(this ICacheClient2 client, IEnumerable<string> keys) {
            client.RemoveAllAsync(keys).Wait();
        }

        public static T Get<T>(this ICacheClient2 client, string key) {
            T value;
            client.TryGetAsync(key, out value).Wait();
            return value;
        }

        public static bool TryGet<T>(this ICacheClient2 client, string key, out T value) {
            return client.TryGetAsync(key, out value).Result;
        }

        public static long Increment(this ICacheClient2 client, string key, uint amount) {
            return client.IncrementAsync(key, (int)amount).Result;
        }

        public static long Increment(this ICacheClient2 client, string key, uint amount, DateTime expiresAt) {
            return client.IncrementAsync(key, (int)amount, expiresAt.Subtract(DateTime.Now)).Result;
        }

        public static long Increment(this ICacheClient2 client, string key, uint amount, TimeSpan expiresIn) {
            return client.IncrementAsync(key, (int)amount, expiresIn).Result;
        }

        public static long Decrement(this ICacheClient2 client, string key, uint amount) {
            return client.IncrementAsync(key, -(int)amount).Result;
        }

        public static long Decrement(this ICacheClient2 client, string key, uint amount, DateTime expiresAt) {
            return client.IncrementAsync(key, -(int)amount, expiresAt.Subtract(DateTime.Now)).Result;
        }

        //public static long Decrement(this ICacheClient2 client, string key, uint amount, TimeSpan expiresIn) {}
        //public static bool Add<T>(this ICacheClient2 client, string key, T value) { }
        //public static bool Add<T>(this ICacheClient2 client, string key, T value, DateTime expiresAt) { }
        //public static bool Add<T>(this ICacheClient2 client, string key, T value, TimeSpan expiresIn) { }
        //public static bool Set<T>(this ICacheClient2 client, string key, T value) { }
        //public static bool Set<T>(this ICacheClient2 client, string key, T value, DateTime expiresAt) { }
        //public static bool Set<T>(this ICacheClient2 client, string key, T value, TimeSpan expiresIn) { }
        //public static bool Replace<T>(this ICacheClient2 client, string key, T value) { }
        //public static bool Replace<T>(this ICacheClient2 client, string key, T value, DateTime expiresAt) { }
        //public static bool Replace<T>(this ICacheClient2 client, string key, T value, TimeSpan expiresIn) { }
        //public static void FlushAll(this ICacheClient2 client) { }
        //public static IDictionary<string, T> GetAll<T>(this ICacheClient2 client, IEnumerable<string> keys) { }
        //public static void SetAll<T>(this ICacheClient2 client, IDictionary<string, T> values) { }
        //public static DateTime? GetExpiration(this ICacheClient2 client, string cacheKey) { }
        //public static void SetExpiration(this ICacheClient2 client, string cacheKey, TimeSpan expiresIn) { }
        //public static void SetExpiration(this ICacheClient2 client, string cacheKey, DateTime expiresAt) { }
    }
}
