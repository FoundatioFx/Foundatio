﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Caching {
    public class NullCacheClient : ICacheClient {
        public static readonly NullCacheClient Instance = new();

        private long _writes;
        private long _reads;
        
        public long Calls => _writes + _reads;
        public long Writes => _writes;
        public long Reads => _reads;

        public override string ToString() {
            return $"Calls: {Calls} Reads: {Reads} Writes: {Writes}";
        }

        public void ResetStats() {
            _writes = 0;
            _reads = 0;
        }

        public Task<bool> RemoveAsync(string key) {
            Interlocked.Increment(ref _writes);
            
            return Task.FromResult(false);
        }

        public Task<bool> RemoveIfEqualAsync<T>(string key, T expected) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(false);
        }

        public Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(0);
        }

        public Task<int> RemoveByPrefixAsync(string prefix) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(0);
        }

        public Task<CacheValue<T>> GetAsync<T>(string key) {
            Interlocked.Increment(ref _reads);

            return Task.FromResult(CacheValue<T>.NoValue);
        }

        public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            Interlocked.Increment(ref _reads);

            return Task.FromResult<IDictionary<string, CacheValue<T>>>(keys.ToDictionary(k => k, k => CacheValue<T>.NoValue));
        }

        public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(true);
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(true);
        }

        public Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(0);
        }

        public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(true);
        }

        public Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(true);
        }

        public Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(amount);
        }

        public Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(amount);
        }

        public Task<bool> ExistsAsync(string key) {
            Interlocked.Increment(ref _reads);
            
            return Task.FromResult(false);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            Interlocked.Increment(ref _reads);

            return Task.FromResult<TimeSpan?>(null);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(0);
        }

        public Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(value);
        }

        public Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(value);
        }

        public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(value);
        }

        public Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(value);
        }

        public Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(default(long));
        }

        public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            Interlocked.Increment(ref _writes);

            return Task.FromResult(default(long));
        }

        public Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) {
            Interlocked.Increment(ref _reads);

            return Task.FromResult(CacheValue<ICollection<T>>.NoValue);
        }

        public void Dispose() {}
    }
}
