using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetAllExpirationAsync_WithMixedExpirations_SetsExpirationsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set up keys with various initial states
            await cache.SetAsync("set-expiration-key", 1);
            await cache.SetAsync("update-expiration-key", 2, TimeSpan.FromMinutes(5));
            await cache.SetAsync("remove-expiration-key", 3, TimeSpan.FromMinutes(10));

            // Verify initial state
            Assert.Null(await cache.GetExpirationAsync("set-expiration-key"));
            Assert.NotNull(await cache.GetExpirationAsync("update-expiration-key"));
            Assert.NotNull(await cache.GetExpirationAsync("remove-expiration-key"));

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "set-expiration-key", TimeSpan.FromMinutes(15) },
                { "update-expiration-key", TimeSpan.FromMinutes(30) },
                { "remove-expiration-key", null },
                { "nonexistent-key", TimeSpan.FromMinutes(20) }
            };

            await cache.SetAllExpirationAsync(expirations);

            // Verify expiration was set on key without prior expiration
            var setExpiration = await cache.GetExpirationAsync("set-expiration-key");
            Assert.NotNull(setExpiration);
            Assert.True(setExpiration.Value > TimeSpan.FromMinutes(14));
            Assert.True(setExpiration.Value <= TimeSpan.FromMinutes(15));

            // Verify expiration was updated on key with prior expiration
            var updateExpiration = await cache.GetExpirationAsync("update-expiration-key");
            Assert.NotNull(updateExpiration);
            Assert.True(updateExpiration.Value > TimeSpan.FromMinutes(29));
            Assert.True(updateExpiration.Value <= TimeSpan.FromMinutes(30));

            // Verify null removes expiration but key still exists
            Assert.Null(await cache.GetExpirationAsync("remove-expiration-key"));
            Assert.True(await cache.ExistsAsync("remove-expiration-key"));

            // Verify non-existent key was not created
            Assert.False(await cache.ExistsAsync("nonexistent-key"));
            Assert.Null(await cache.GetExpirationAsync("nonexistent-key"));
        }
    }

    public virtual async Task SetAllExpirationAsync_WithLargeNumberOfKeys_SetsAllExpirations(int count)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var keys = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string key = $"perf-test-key-{i}";
                keys.Add(key);
                await cache.SetAsync(key, i);
            }

            var expirations = new Dictionary<string, TimeSpan?>();
            for (int i = 0; i < count; i++)
            {
                expirations[keys[i]] = TimeSpan.FromMinutes(i % 60 + 1);
            }

            var sw = Stopwatch.StartNew();
            await cache.SetAllExpirationAsync(expirations);
            sw.Stop();

            _logger.LogInformation("Set All Expiration Time ({Count} keys): {Elapsed:g}", count, sw.Elapsed);

            // Verify a sample of keys
            var key0Expiration = await cache.GetExpirationAsync(keys[0]);
            Assert.NotNull(key0Expiration);
            Assert.True(key0Expiration.Value <= TimeSpan.FromMinutes(1));

            int keySampleIndex = count / 2;
            var keySampleExpiration = await cache.GetExpirationAsync(keys[keySampleIndex]);
            Assert.NotNull(keySampleExpiration);
            Assert.True(keySampleExpiration.Value <= TimeSpan.FromMinutes(keySampleIndex % 60 + 1));
        }
    }
}
