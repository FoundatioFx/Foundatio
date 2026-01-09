using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Resilience;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Caching;

/// <summary>
/// Provides a scoped hybrid cache client that prefixes all cache keys with the specified scope.
/// </summary>
public class ScopedHybridCacheClient : ScopedCacheClient, IHybridCacheClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScopedHybridCacheClient"/> class with the specified hybrid cache client and scope.
    /// </summary>
    /// <param name="client">The underlying hybrid cache client to use.</param>
    /// <param name="scope">The scope for cache keys. When specified, all operations will be prefixed with this scope.</param>
    /// <param name="shouldDispose">Whether to dispose the underlying cache client when this instance is disposed.
    /// Defaults to false, meaning the underlying cache client will not be disposed when this instance is disposed.
    /// Set to true to have the underlying cache client automatically disposed when this instance is disposed, enabling use with 'using' statements.</param>
    public ScopedHybridCacheClient(IHybridCacheClient client, string scope, bool shouldDispose = false) : base(client, scope, shouldDispose) { }
}

/// <summary>
/// Provides a scoped cache client that prefixes all cache keys with the specified scope.
/// Can optionally dispose the underlying cache client when this instance is disposed.
/// </summary>
public class ScopedCacheClient : ICacheClient, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider
{
    private string _keyPrefix;
    private bool _isLocked;
    private readonly object _lock = new();
    private readonly bool _shouldDispose;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopedCacheClient"/> class with the specified cache client and scope.
    /// </summary>
    /// <param name="client">The underlying cache client to use.</param>
    /// <param name="scope">The scope for cache keys. When specified, all operations will be prefixed with this scope.</param>
    public ScopedCacheClient(ICacheClient client, string scope) : this(client, scope, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ScopedCacheClient"/> class with the specified cache client and scope.
    /// </summary>
    /// <param name="client">The underlying cache client to use.</param>
    /// <param name="scope">The scope for cache keys. When specified, all operations will be prefixed with this scope.</param>
    /// <param name="shouldDispose">Whether to dispose the underlying cache client when this instance is disposed.
    /// Defaults to false, meaning the underlying cache client will not be disposed when this instance is disposed.
    /// Set to true to have the underlying cache client automatically disposed when this instance is disposed, enabling use with 'using' statements.</param>
    public ScopedCacheClient(ICacheClient client, string scope, bool shouldDispose)
    {
        UnscopedCache = client ?? new NullCacheClient();
        _isLocked = scope != null;
        Scope = !String.IsNullOrWhiteSpace(scope) ? scope.Trim() : null;
        _shouldDispose = shouldDispose;

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
        ArgumentNullException.ThrowIfNull(keys);

        var unscopedKeys = keys is ICollection<string> collection ? new List<string>(collection.Count) : [];
        foreach (string key in keys.Distinct())
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));
            unscopedKeys.Add(GetUnscopedCacheKey(key));
        }

        return unscopedKeys;
    }

    protected string GetScopedCacheKey(string unscopedKey)
    {
        return unscopedKey?.Substring(_keyPrefix.Length);
    }

    public Task<bool> RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.RemoveAsync(GetUnscopedCacheKey(key));
    }

    public Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.RemoveIfEqualAsync(GetUnscopedCacheKey(key), expected);
    }

    public Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        if (keys is null)
            return RemoveByPrefixAsync(String.Empty);

        return UnscopedCache.RemoveAllAsync(GetUnscopedCacheKeys(keys));
    }

    public Task<int> RemoveByPrefixAsync(string prefix)
    {
        return UnscopedCache.RemoveByPrefixAsync(GetUnscopedCacheKey(prefix));
    }

    public Task<CacheValue<T>> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.GetAsync<T>(GetUnscopedCacheKey(key));
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var scopedDictionary = await UnscopedCache.GetAllAsync<T>(GetUnscopedCacheKeys(keys)).AnyContext();
        return scopedDictionary.ToDictionary(kvp => GetScopedCacheKey(kvp.Key), kvp => kvp.Value);
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.AddAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.SetAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        
        if (values.Count is 0)
            return Task.FromResult(0);

        var unscopedValues = new Dictionary<string, T>(values.Count);
        foreach (var kvp in values)
        {
            ArgumentException.ThrowIfNullOrEmpty(kvp.Key, nameof(values));
            unscopedValues[GetUnscopedCacheKey(kvp.Key)] = kvp.Value;
        }

        return UnscopedCache.SetAllAsync(unscopedValues, expiresIn);
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.ReplaceAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.ReplaceIfEqualAsync(GetUnscopedCacheKey(key), value, expected, expiresIn);
    }

    public Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.IncrementAsync(GetUnscopedCacheKey(key), amount, expiresIn);
    }

    public Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.IncrementAsync(GetUnscopedCacheKey(key), amount, expiresIn);
    }

    public Task<bool> ExistsAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.ExistsAsync(GetUnscopedCacheKey(key));
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.GetExpirationAsync(GetUnscopedCacheKey(key));
    }

    public async Task<IDictionary<string, TimeSpan?>> GetAllExpirationAsync(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var unscopedKeys = GetUnscopedCacheKeys(keys);
        var unscopedExpirations = await UnscopedCache.GetAllExpirationAsync(unscopedKeys).AnyContext();
        return unscopedExpirations.ToDictionary(kvp => GetScopedCacheKey(kvp.Key), kvp => kvp.Value);
    }

    public Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.SetExpirationAsync(GetUnscopedCacheKey(key), expiresIn);
    }

    public Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);

        var unscopedExpirations = new Dictionary<string, TimeSpan?>(expirations.Count);
        foreach (var kvp in expirations)
        {
            ArgumentException.ThrowIfNullOrEmpty(kvp.Key, nameof(expirations));
            unscopedExpirations[GetUnscopedCacheKey(kvp.Key)] = kvp.Value;
        }

        return UnscopedCache.SetAllExpirationAsync(unscopedExpirations);
    }

    public Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.SetIfHigherAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.SetIfHigherAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.SetIfLowerAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.SetIfLowerAsync(GetUnscopedCacheKey(key), value, expiresIn);
    }

    public Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return UnscopedCache.ListAddAsync(GetUnscopedCacheKey(key), values, expiresIn);
    }

    public Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        return UnscopedCache.ListRemoveAsync(GetUnscopedCacheKey(key), values);
    }

    public Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        return UnscopedCache.GetListAsync<T>(GetUnscopedCacheKey(key), page, pageSize);
    }

    public void Dispose()
    {
        if (_shouldDispose)
            UnscopedCache.Dispose();
    }
}
