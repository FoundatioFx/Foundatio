using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Caching;

/// <summary>
/// Runs each distributed mutation once. If the provider throws, it may already have committed the change.
/// </summary>
internal sealed class HybridCacheWriter(ICacheClient cache, Func<string[]?, string?, Task> invalidate)
{
    public static string[] GetBatchKeys(IEnumerable<string> keys)
    {
        string[] items = keys.ToArray();
        foreach (string key in items)
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));

        return items;
    }

    public ValueTask<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expiresIn), static (c, s) => c.AddAsync(s.key, s.value, s.expiresIn), key: key);

    public ValueTask<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expiresIn), static (c, s) => c.SetAsync(s.key, s.value, s.expiresIn), key: key);

    public ValueTask<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn, string[] keys) =>
        WriteAsync((values, expiresIn), static (c, s) => c.SetAllAsync(s.values, s.expiresIn), keys: keys);

    public ValueTask<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expiresIn), static (c, s) => c.ReplaceAsync(s.key, s.value, s.expiresIn), key: key);

    public ValueTask<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expected, expiresIn), static (c, s) => c.ReplaceIfEqualAsync(s.key, s.value, s.expected, s.expiresIn), key: key);

    public ValueTask<bool> RemoveAsync(string key) =>
        WriteAsync(key, static (c, k) => c.RemoveAsync(k), key: key);

    public ValueTask<bool> RemoveIfEqualAsync<T>(string key, T expected) =>
        WriteAsync((key, expected), static (c, s) => c.RemoveIfEqualAsync(s.key, s.expected), key: key);

    public ValueTask<int> RemoveAllAsync(string[]? keys) =>
        WriteAsync(keys, static (c, items) => c.RemoveAllAsync(items), keys: keys is { Length: 0 } ? null : keys);

    public ValueTask<int> RemoveByPrefixAsync(string prefix) =>
        WriteAsync(prefix, static (c, p) => c.RemoveByPrefixAsync(p), prefix: prefix ?? String.Empty);

    public ValueTask<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn) =>
        WriteAsync((key, amount, expiresIn), static (c, s) => c.IncrementAsync(s.key, s.amount, s.expiresIn), key: key);

    public ValueTask<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn) =>
        WriteAsync((key, amount, expiresIn), static (c, s) => c.IncrementAsync(s.key, s.amount, s.expiresIn), key: key);

    public ValueTask<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expiresIn), static (c, s) => c.SetIfHigherAsync(s.key, s.value, s.expiresIn), key: key);

    public ValueTask<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expiresIn), static (c, s) => c.SetIfHigherAsync(s.key, s.value, s.expiresIn), key: key);

    public ValueTask<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expiresIn), static (c, s) => c.SetIfLowerAsync(s.key, s.value, s.expiresIn), key: key);

    public ValueTask<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn) =>
        WriteAsync((key, value, expiresIn), static (c, s) => c.SetIfLowerAsync(s.key, s.value, s.expiresIn), key: key);

    public ValueTask<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn) where T : notnull =>
        WriteAsync((key, values, expiresIn), static (c, s) => c.ListAddAsync(s.key, s.values, s.expiresIn), key: key);

    public ValueTask<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull =>
        WriteAsync((key, values), static (c, s) => c.ListRemoveAsync(s.key, s.values), key: key);

    public ValueTask SetExpirationAsync(string key, TimeSpan expiresIn) =>
        WriteAsync((key, expiresIn), static (c, s) => c.SetExpirationAsync(s.key, s.expiresIn), key: key);

    public ValueTask SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations, string[] keys) =>
        WriteAsync(expirations, static (c, items) => c.SetAllExpirationAsync(items), keys: keys);

    private async ValueTask<TResult> WriteAsync<TState, TResult>(TState state, Func<ICacheClient, TState, Task<TResult>> write,
        string? key = null, string[]? keys = null, string? prefix = null)
    {
        try
        {
            return await write(cache, state).AnyContext();
        }
        catch
        {
            await invalidate(key is null ? keys : [key], prefix).AnyContext();
            throw;
        }
    }

    private async ValueTask WriteAsync<TState>(TState state, Func<ICacheClient, TState, Task> write, string? key = null, string[]? keys = null)
    {
        try
        {
            await write(cache, state).AnyContext();
        }
        catch
        {
            await invalidate(key is null ? keys : [key], null).AnyContext();
            throw;
        }
    }
}
