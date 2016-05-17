using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Foundatio.Caching {
    public class NullCacheClient : ICacheClient {
        public Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            return Task.FromResult(0);
        }

        public Task<int> RemoveByPrefixAsync(string prefix) {
            return Task.FromResult(0);
        }

        public Task<CacheValue<T>> GetAsync<T>(string key) {
            return Task.FromResult(CacheValue<T>.NoValue);
        }

        public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) { 
            return Task.FromResult<IDictionary<string, CacheValue<T>>>(keys.ToDictionary(k => k, k => CacheValue<T>.NoValue));
        }

        public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            return Task.FromResult(true);
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            return Task.FromResult(true);
        }

        public Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            return Task.FromResult(0);
        }

        public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            return Task.FromResult(true);
        }

        public Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            return Task.FromResult(amount);
        }

        public Task<bool> ExistsAsync(string key) {
            return Task.FromResult(false);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            return Task.FromResult<TimeSpan?>(null);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            return Task.FromResult(0);
        }

        public Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            return Task.FromResult(value);
        }

        public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            return Task.FromResult(value);
        }

        public void Dispose() {}
    }
}
