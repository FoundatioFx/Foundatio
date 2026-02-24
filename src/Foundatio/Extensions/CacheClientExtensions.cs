using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Caching;

public static class CacheClientExtensions
{
    /// <summary>
    /// Minimum meaningful cache expiration. Values below this threshold are treated as already-expired
    /// because sub-millisecond TTLs are truncated to zero by external providers (e.g., Redis PSETEX
    /// converts TimeSpan to milliseconds via integer cast, so 0.9ms becomes 0ms and is rejected).
    /// 5ms provides a safe margin above the 1ms integer-truncation boundary while remaining far
    /// below any real-world cache TTL.
    /// </summary>
    public static readonly TimeSpan MinimumExpiration = TimeSpan.FromMilliseconds(5);
    
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
        return client.IncrementAsync(key, amount, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task<double> IncrementAsync(this ICacheClient client, string key, double amount, DateTime? expiresAtUtc)
    {
        return client.IncrementAsync(key, amount, client.ToExpiresIn(expiresAtUtc));
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
        return client.IncrementAsync(key, -amount, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task<double> DecrementAsync(this ICacheClient client, string key, double amount, DateTime? expiresAtUtc)
    {
        return client.IncrementAsync(key, -amount, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task<bool> AddAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc)
    {
        return client.AddAsync(key, value, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task<bool> SetAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc)
    {
        return client.SetAsync(key, value, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task<bool> ReplaceAsync<T>(this ICacheClient client, string key, T value, DateTime? expiresAtUtc)
    {
        return client.ReplaceAsync(key, value, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task<bool> ReplaceIfEqualAsync<T>(this ICacheClient client, string key, T value, T expected, DateTime? expiresAtUtc)
    {
        return client.ReplaceIfEqualAsync(key, value, expected, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task<int> SetAllAsync(this ICacheClient client, IDictionary<string, object> values, DateTime? expiresAtUtc)
    {
        return client.SetAllAsync(values, client.ToExpiresIn(expiresAtUtc));
    }

    public static Task SetExpirationAsync(this ICacheClient client, string key, DateTime expiresAtUtc)
    {
        return client.SetExpirationAsync(key, client.ToExpiresIn(expiresAtUtc) ?? TimeSpan.MaxValue);
    }

    public static async Task<bool> ListAddAsync<T>(this ICacheClient client, string key, T value, TimeSpan? expiresIn = null)
    {
        return await client.ListAddAsync(key, [value], expiresIn).AnyContext() > 0;
    }

    public static async Task<bool> ListRemoveAsync<T>(this ICacheClient client, string key, T value)
    {
        return await client.ListRemoveAsync(key, [value]).AnyContext() > 0;
    }

    [Obsolete("Use ListRemoveAsync without expiresIn parameter")]
    public static async Task<long> ListRemoveAsync<T>(this ICacheClient client, string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        return await client.ListRemoveAsync(key, values).AnyContext();
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
        return client.SetAsync(key, value.ToUnixTimeMilliseconds(), client.ToExpiresIn(expiresAtUtc));
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
        return client.SetAsync(key, value.ToUnixTimeSeconds(), client.ToExpiresIn(expiresAtUtc));
    }

    /// <summary>
    /// Converts a DateTime expiration to a TimeSpan relative to now.
    /// DateTime.MaxValue is treated as null (no expiration).
    /// Returns TimeSpan.Zero when the computed TTL is below <see cref="MinimumExpiration"/>,
    /// so downstream guards treat it as already-expired.
    /// </summary>
    private static TimeSpan? ToExpiresIn(this ICacheClient client, DateTime? expiresAtUtc)
    {
        if (!expiresAtUtc.HasValue || expiresAtUtc.Value == DateTime.MaxValue)
            return null;

        var expiresIn = expiresAtUtc.Value.Subtract(client.GetTimeProvider().GetUtcNow().UtcDateTime);

        if (expiresIn < MinimumExpiration)
            return TimeSpan.Zero;

        return expiresIn;
    }
}
