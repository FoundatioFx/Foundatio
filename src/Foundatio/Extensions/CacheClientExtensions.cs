using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Caching;

public static class CacheClientExtensions
{
    public static async Task<T> GetAsync<T>(this ICacheClient client, string key, T defaultValue)
    {
        var cacheValue = await client.GetAsync<T>(key).AnyContext();
        return cacheValue.HasValue ? cacheValue.Value : defaultValue;
    }

    public static Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(this ICacheClient client, params string[] keys)
    {
        return client.GetAllAsync<T>(keys.ToArray());
    }

    public static Task<long> IncrementAsync(this ICacheClient client, string key, long amount, DateTime? expiresAtUtc)
    {
        return client.IncrementAsync(key, amount, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<double> IncrementAsync(this ICacheClient client, string key, double amount, DateTime? expiresAtUtc)
    {
        return client.IncrementAsync(key, amount, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<long> IncrementAsync(this ICacheClient client, string key, TimeSpan? expiresIn = null)
    {
        return client.IncrementAsync(key, 1, expiresIn);
    }

    public static Task<long> DecrementAsync(this ICacheClient client, string key, TimeSpan? expiresIn = null)
    {
        return client.IncrementAsync(key, -1, expiresIn);
    }

    public static Task<long> DecrementAsync(this ICacheClient client, string key, long amount, TimeSpan? expiresIn = null)
    {
        return client.IncrementAsync(key, -amount, expiresIn);
    }

    public static Task<long> DecrementAsync(this ICacheClient client, string key, long amount, DateTime? expiresAtUtc)
    {
        return client.IncrementAsync(key, -amount, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<double> DecrementAsync(this ICacheClient client, string key, double amount, DateTime? expiresAtUtc)
    {
        return client.IncrementAsync(key, -amount, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<bool> AddAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc)
    {
        return client.AddAsync(key, value, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<bool> SetAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc)
    {
        return client.SetAsync(key, value, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<bool> ReplaceAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc)
    {
        return client.ReplaceAsync(key, value, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<bool> ReplaceIfEqualAsync<T>(this ICacheClient client, string key, T value, T expected, DateTime? expiresAtUtc)
    {
        return client.ReplaceIfEqualAsync(key, value, expected, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task<int> SetAllAsync(this ICacheClient client, IDictionary<string, object> values, DateTime? expiresAtUtc)
    {
        return client.SetAllAsync(values, expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static Task SetExpirationAsync(this ICacheClient client, string key, DateTime expiresAtUtc)
    {
        return client.SetExpirationAsync(key, expiresAtUtc.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static async Task<bool> ListAddAsync<T>(this ICacheClient client, string key, T value, TimeSpan? expiresIn = null)
    {
        return await client.ListAddAsync(key, new[] { value }, expiresIn).AnyContext() > 0;
    }

    public static async Task<bool> ListRemoveAsync<T>(this ICacheClient client, string key, T value, TimeSpan? expiresIn = null)
    {
        return await client.ListRemoveAsync(key, new[] { value }, expiresIn).AnyContext() > 0;
    }

    [Obsolete("Use ListAddAsync instead")]
    public static async Task<bool> SetAddAsync<T>(this ICacheClient client, string key, T value, TimeSpan? expiresIn = null)
    {
        return await client.ListAddAsync(key, new[] { value }, expiresIn).AnyContext() > 0;
    }

    [Obsolete("Use ListRemoveAsync instead")]
    public static async Task<bool> SetRemoveAsync<T>(this ICacheClient client, string key, T value, TimeSpan? expiresIn = null)
    {
        return await client.ListRemoveAsync(key, new[] { value }, expiresIn).AnyContext() > 0;
    }

    [Obsolete("Use ListAddAsync instead")]
    public static Task<long> SetAddAsync<T>(this ICacheClient client, string key, IEnumerable<T> value, TimeSpan? expiresIn = null)
    {
        return client.ListAddAsync(key, new[] { value }, expiresIn);
    }

    [Obsolete("Use ListRemoveAsync instead")]
    public static Task<long> SetRemoveAsync<T>(this ICacheClient client, string key, IEnumerable<T> value, TimeSpan? expiresIn = null)
    {
        return client.ListRemoveAsync(key, value, expiresIn);
    }

    [Obsolete("Use ListAddAsync instead")]
    public static Task<CacheValue<ICollection<T>>> GetSetAsync<T>(this ICacheClient client, string key)
    {
        return client.GetListAsync<T>(key);
    }

    public static Task<long> SetIfHigherAsync(this ICacheClient client, string key, DateTime value, TimeSpan? expiresIn = null)
    {
        long unixTime = value.ToUnixTimeMilliseconds();
        return client.SetIfHigherAsync(key, unixTime, expiresIn);
    }

    public static Task<long> SetIfLowerAsync(this ICacheClient client, string key, DateTime value, TimeSpan? expiresIn = null)
    {
        long unixTime = value.ToUnixTimeMilliseconds();
        return client.SetIfLowerAsync(key, unixTime, expiresIn);
    }

    public static async Task<DateTime> GetUnixTimeMillisecondsAsync(this ICacheClient client, string key, DateTime? defaultValue = null)
    {
        var unixTime = await client.GetAsync<long>(key).AnyContext();
        if (!unixTime.HasValue)
            return defaultValue ?? DateTime.MinValue;

        return unixTime.Value.FromUnixTimeMilliseconds();
    }

    public static Task<bool> SetUnixTimeMillisecondsAsync(this ICacheClient client, string key, DateTime value, TimeSpan? expiresIn = null)
    {
        return client.SetAsync(key, value.ToUnixTimeMilliseconds(), expiresIn);
    }

    public static Task<bool> SetUnixTimeMillisecondsAsync(this ICacheClient client, string key, DateTime value, DateTime? expiresAtUtc)
    {
        return client.SetAsync(key, value.ToUnixTimeMilliseconds(), expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }

    public static async Task<DateTimeOffset> GetUnixTimeSecondsAsync(this ICacheClient client, string key, DateTime? defaultValue = null)
    {
        var unixTime = await client.GetAsync<long>(key).AnyContext();
        if (!unixTime.HasValue)
            return defaultValue ?? DateTime.MinValue;

        return unixTime.Value.FromUnixTimeSeconds();
    }

    public static Task<bool> SetUnixTimeSecondsAsync(this ICacheClient client, string key, DateTime value, TimeSpan? expiresIn = null)
    {
        return client.SetAsync(key, value.ToUnixTimeSeconds(), expiresIn);
    }

    public static Task<bool> SetUnixTimeSecondsAsync(this ICacheClient client, string key, DateTime value, DateTime? expiresAtUtc)
    {
        return client.SetAsync(key, value.ToUnixTimeSeconds(), expiresAtUtc?.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime));
    }
}
