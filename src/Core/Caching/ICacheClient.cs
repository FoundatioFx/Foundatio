using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Foundatio.Caching {
    public interface ICacheClient : IDisposable {
        Task<int> RemoveAllAsync(IEnumerable<string> keys = null);
        Task<int> RemoveByPrefixAsync(string prefix);
        Task<CacheValue<T>> TryGetAsync<T>(string key);
        Task<IDictionary<string, T>> GetAllAsync<T>(IEnumerable<string> keys);
        Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null);
        Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<long> IncrementAsync(string key, int amount = 1, TimeSpan? expiresIn = null);
        Task<TimeSpan?> GetExpirationAsync(string key);
        Task SetExpirationAsync(string key, TimeSpan expiresIn);
    }

    public static class CacheClientExtensions {
        public static async Task<bool> RemoveAsync(this ICacheClient client, string key) {
            return await client.RemoveAllAsync(new[] { key }) == 1;
        }
        
        public static async Task<T> GetAsync<T>(this ICacheClient client, string key) {
            var cacheValue = await client.TryGetAsync<T>(key);
            return cacheValue.HasValue ? cacheValue.Value : default(T);
        }
        
        public static Task<long> IncrementAsync(this ICacheClient client, string key, int amount = 1, DateTime? expiresAt = null) {
            return client.IncrementAsync(key, amount, expiresAt?.Subtract(DateTime.UtcNow));
        }

        public static Task<long> DecrementAsync(this ICacheClient client, string key, int amount = 1, TimeSpan? expiresIn = null) {
            return client.IncrementAsync(key, -amount, expiresIn);
        }

        public static Task<long> DecrementAsync(this ICacheClient client, string key, int amount = 1, DateTime? expiresAt = null) {
            return client.IncrementAsync(key, -amount, expiresAt);
        }
        
        public static Task<bool> AddAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAt = null) {
            return client.AddAsync(key, value, expiresAt?.Subtract(DateTime.UtcNow));
        }

        public static Task<bool> SetAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAt = null) {
            return client.SetAsync(key, value, expiresAt?.Subtract(DateTime.UtcNow));
        }
    
        public static Task<bool> ReplaceAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAt = null) {
            return client.ReplaceAsync(key, value, expiresAt?.Subtract(DateTime.UtcNow));
        }
        
        public static Task<int> SetAllAsync(this ICacheClient client, IDictionary<string, object> values, DateTime? expiresAt = null) {
            return client.SetAllAsync(values, expiresAt?.Subtract(DateTime.UtcNow));
        }
        
        public static Task SetExpirationAsync<T>(this ICacheClient client, string key, DateTime expiresAt) {
            return client.SetExpirationAsync(key, expiresAt.Subtract(DateTime.UtcNow));
        }
    }
}
