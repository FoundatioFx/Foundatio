using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
        Task<long> SetAddAsync<T>(string key, IEnumerable<T> value, TimeSpan? expiresIn = null);
        Task<long> SetRemoveAsync<T>(string key, IEnumerable<T> value, TimeSpan? expiresIn = null);
        Task<CacheValue<ICollection<T>>> GetSetAsync<T>(string key);
    }
}
