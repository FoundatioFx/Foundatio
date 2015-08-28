using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Foundatio.Caching {
    public class NullCacheClient : ICacheClient {
        public Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            return Task.FromResult(0);
        }

        public Task RemoveByPrefixAsync(string prefix) {
            return Task.FromResult(0);
        }

        public Task<CacheValue<T>> TryGetAsync<T>(string key) {
            return Task.FromResult(CacheValue<T>.Null);
        }

        public Task<IDictionary<string, T>> GetAllAsync<T>(IEnumerable<string> keys) { 
            return Task.FromResult<IDictionary<string, T>>(new Dictionary<string, T>());
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

        public Task<long> IncrementAsync(string key, int amount = 1, TimeSpan? expiresIn = null) {
            return Task.FromResult(0L);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            return Task.FromResult<TimeSpan?>(null);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            return Task.FromResult(0);
        }

        public void Dispose() {}
    }
}
