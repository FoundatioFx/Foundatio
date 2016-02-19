using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Caching {
    public interface ICacheClient : IDisposable {
        Task<int> RemoveAllAsync(IEnumerable<string> keys = null);
        Task<int> RemoveByPrefixAsync(string prefix);
        Task<CacheValue<T>> GetAsync<T>(string key);
        Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys);
        Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null);
        Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null);
        Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null);
        Task<bool> ExistsAsync(string key);
        Task<TimeSpan?> GetExpirationAsync(string key);
        Task SetExpirationAsync(string key, TimeSpan expiresIn);
        Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null);
        Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null);
    }

    public static class CacheClientExtensions {
        public static async Task<T> GetAsync<T>(this ICacheClient client, string key, T defaultValue) {
            var cacheValue = await client.GetAsync<T>(key).AnyContext();
            return cacheValue.HasValue ? cacheValue.Value : defaultValue;
        }

        public static Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(this ICacheClient client, params string[] keys) {
            return client.GetAllAsync<T>(keys.ToArray());
        }

        public static async Task<bool> RemoveAsync(this ICacheClient client, string key) {
            return await client.RemoveAllAsync(new[] { key }).AnyContext() == 1;
        }

        public static async Task<long> IncrementAsync(this ICacheClient client, string key, int amount = 1, TimeSpan? expiresIn = null) {
            var result = await client.IncrementAsync(key, amount, expiresIn).AnyContext();
            return (long)result;
        }

        public static Task<long> IncrementAsync(this ICacheClient client, string key, int amount = 1, DateTime? expiresAtUtc = null) {
            return IncrementAsync(client, key, amount, expiresAtUtc?.Subtract(DateTime.UtcNow));
        }

        public static Task<long> DecrementAsync(this ICacheClient client, string key, int amount = 1, TimeSpan? expiresIn = null) {
            return IncrementAsync(client, key, amount, expiresIn);
        }

        public static Task<long> DecrementAsync(this ICacheClient client, string key, int amount = 1, DateTime? expiresAtUtc = null) {
            return client.IncrementAsync(key, -amount, expiresAtUtc);
        }
        
        public static Task<bool> AddAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc = null) {
            return client.AddAsync(key, value, expiresAtUtc?.Subtract(DateTime.UtcNow));
        }

        public static Task<bool> SetAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc = null) {
            return client.SetAsync(key, value, expiresAtUtc?.Subtract(DateTime.UtcNow));
        }
    
        public static Task<bool> ReplaceAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc = null) {
            return client.ReplaceAsync(key, value, expiresAtUtc?.Subtract(DateTime.UtcNow));
        }
        
        public static Task<int> SetAllAsync(this ICacheClient client, IDictionary<string, object> values, DateTime? expiresAtUtc = null) {
            return client.SetAllAsync(values, expiresAtUtc?.Subtract(DateTime.UtcNow));
        }
        
        public static Task SetExpirationAsync(this ICacheClient client, string key, DateTime expiresAtUtc) {
            return client.SetExpirationAsync(key, expiresAtUtc.Subtract(DateTime.UtcNow));
        }
    }
}
