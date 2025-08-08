using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Caching;

public class NullCacheClient : ICacheClient
{
    public static readonly NullCacheClient Instance = new();

    private long _writes;
    private long _reads;

    public long Calls => _writes + _reads;
    public long Writes => _writes;
    public long Reads => _reads;

    public override string ToString()
    {
        return $"Calls: {Calls} Reads: {Reads} Writes: {Writes}";
    }

    public void ResetStats()
    {
        _writes = 0;
        _reads = 0;
    }

    public Task<bool> RemoveAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(false);
    }

    public Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(false);
    }

    public Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        Interlocked.Increment(ref _writes);

        return Task.FromResult(0);
    }

    public Task<int> RemoveByPrefixAsync(string prefix)
    {
        Interlocked.Increment(ref _writes);

        return Task.FromResult(0);
    }

    public Task<CacheValue<T>> GetAsync<T>(string key)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<CacheValue<T>>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _reads);

        return Task.FromResult(CacheValue<T>.NoValue);
    }

    public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        if (keys is null)
            return Task.FromException<IDictionary<string, CacheValue<T>>>(new ArgumentNullException(nameof(keys)));

        Interlocked.Increment(ref _reads);

        return Task.FromResult<IDictionary<string, CacheValue<T>>>(keys.ToDictionary(k => k, k => CacheValue<T>.NoValue));
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(true);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(true);
    }

    public Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        Interlocked.Increment(ref _writes);

        return Task.FromResult(0);
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(true);
    }

    public Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(true);
    }

    public Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<double>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(amount);
    }

    public Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(amount);
    }

    public Task<bool> ExistsAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _reads);

        return Task.FromResult(false);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<TimeSpan?>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));;

        Interlocked.Increment(ref _reads);

        return Task.FromResult<TimeSpan?>(null);
    }

    public Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(0);
    }

    public Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<double>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(value);
    }

    public Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(value);
    }

    public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<double>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(value);
    }

    public Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(value);
    }

    public Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        if (values is null)
            return Task.FromException<long>(new ArgumentNullException(nameof(values)));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(0L);
    }

    public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        if (values is null)
            return Task.FromException<long>(new ArgumentNullException(nameof(values)));

        Interlocked.Increment(ref _writes);

        return Task.FromResult(0L);
    }

    public Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<CacheValue<ICollection<T>>>(new ArgumentNullException(nameof(key), "Key cannot be null or empty"));

        if (page is < 1)
            return Task.FromException<CacheValue<ICollection<T>>>(new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1"));

        Interlocked.Increment(ref _reads);

        return Task.FromResult(CacheValue<ICollection<T>>.NoValue);
    }

    public void Dispose()
    {
    }
}
