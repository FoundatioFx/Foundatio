using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Resilience;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Caching;

public class ScopedHybridCacheClient : ScopedCacheClient, IHybridCacheClient
{
    public ScopedHybridCacheClient(IHybridCacheClient client, string scope = null) : base(client, scope) { }
}

public class ScopedCacheClient : ICacheClient, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider
{
    private string _keyPrefix;
    private bool _isLocked;
    private readonly object _lock = new();

    public ScopedCacheClient(ICacheClient client, string scope = null)
    {
        UnscopedCache = client ?? new NullCacheClient();
        _isLocked = scope != null;
        Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;

        _keyPrefix = Scope != null ? String.Concat(Scope, ":") : String.Empty;
    }

    public ICacheClient UnscopedCache { get; private set; }

    public string Scope { get; private set; }

    ILogger IHaveLogger.Logger => UnscopedCache.GetLogger();
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => UnscopedCache.GetLoggerFactory();
    TimeProvider IHaveTimeProvider.TimeProvider => UnscopedCache.GetTimeProvider();
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => UnscopedCache.GetResiliencePolicyProvider();

    public void SetScope(string scope)
    {
        if (_isLocked)
            throw new InvalidOperationException("Scope can't be changed after it has been set");

        lock (_lock)
        {
            if (_isLocked)
                throw new InvalidOperationException("Scope can't be changed after it has been set");

            _isLocked = true;
            Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;
            _keyPrefix = Scope != null ? String.Concat(Scope, ":") : String.Empty;
        }
    }

    protected string GetUnscopedCacheKey(string key)
    {
        return String.Concat(_keyPrefix, key);
    }

    protected IEnumerable<string> GetUnscopedCacheKeys(IEnumerable<string> keys)
    {
        return keys?.Select(GetUnscopedCacheKey);
    }

    protected string GetScopedCacheKey(string unscopedKey)
    {
        return unscopedKey?.Substring(_keyPrefix.Length);
    }

    public Task<bool> RemoveAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        return UnscopedCache.RemoveAsync(GetUnscopedCacheKey(key));
    }

    public Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.RemoveIfEqualAsync(GetUnscopedCacheKey(key), expected);
    }

    public Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        if (keys == null)
            return RemoveByPrefixAsync(String.Empty);

        return UnscopedCache.RemoveAllAsync(GetUnscopedCacheKeys(keys));
    }

    public Task<int> RemoveByPrefixAsync(string prefix)
    {
        return UnscopedCache.RemoveByPrefixAsync(GetUnscopedCacheKey(prefix));
    }

    public Task<CacheValue<T>> GetAsync<T>(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.GetAsync<T>(GetUnscopedCacheKey(key));
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));

        var scopedDictionary = await UnscopedCache.GetAllAsync<T>(GetUnscopedCacheKeys(keys)).AnyContext();
        return scopedDictionary.ToDictionary(kvp => GetScopedCacheKey(kvp.Key), kvp => kvp.Value);
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        return UnscopedCache.AddAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        return UnscopedCache.SetAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        return UnscopedCache.SetAllAsync(values?.ToDictionary(kvp => GetUnscopedCacheKey(kvp.Key), kvp => kvp.Value), expiresIn);
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

        return UnscopedCache.ReplaceAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.ReplaceIfEqualAsync(GetUnscopedCacheKey(key), value, expected, expiresIn);
    }

    public Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.IncrementAsync(GetUnscopedCacheKey(key), amount, expiresIn);
    }

    public Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.IncrementAsync(GetUnscopedCacheKey(key), amount, expiresIn);
    }

    public Task<bool> ExistsAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.ExistsAsync(GetUnscopedCacheKey(key));
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.GetExpirationAsync(GetUnscopedCacheKey(key));
    }

    public Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.SetExpirationAsync(GetUnscopedCacheKey(key), expiresIn);
    }

    public Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.SetIfHigherAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.SetIfHigherAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.SetIfLowerAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.SetIfLowerAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return UnscopedCache.ListAddAsync(GetUnscopedCacheKey(key), values, expiresIn);
    }

    public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        return UnscopedCache.ListRemoveAsync(GetUnscopedCacheKey(key), values, expiresIn);
    }

    public Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        return UnscopedCache.GetListAsync<T>(GetUnscopedCacheKey(key), page, pageSize);
    }

    public void Dispose() { }
}
