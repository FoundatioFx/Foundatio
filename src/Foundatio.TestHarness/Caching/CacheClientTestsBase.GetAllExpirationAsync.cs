using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetAllExpirationAsync_WithMixedKeys_ReturnsOnlyKeysWithExpiration()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1, TimeSpan.FromMinutes(5));
            await cache.SetAsync("key2", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("key3", 3); // No expiration
            await cache.SetAsync("key4", 4, TimeSpan.FromMinutes(15));

            // Act
            var expirations = await cache.GetAllExpirationAsync(["key1", "key2", "key3", "key4", "key5"]);

            // Assert
            Assert.NotNull(expirations);
            Assert.Equal(3, expirations.Count); // key3 has no expiration, key5 doesn't exist

            Assert.True(expirations.TryGetValue("key1", out var key1Expiration));
            Assert.NotNull(key1Expiration);
            Assert.True(key1Expiration.Value > TimeSpan.FromMinutes(4));
            Assert.True(key1Expiration.Value <= TimeSpan.FromMinutes(5));

            Assert.True(expirations.TryGetValue("key2", out var key2Expiration));
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(10));

            Assert.False(expirations.ContainsKey("key3")); // No expiration
            Assert.False(expirations.ContainsKey("key5")); // Doesn't exist

            Assert.True(expirations.TryGetValue("key4", out var key4Expiration));
            Assert.NotNull(key4Expiration);
            Assert.True(key4Expiration.Value > TimeSpan.FromMinutes(14));
            Assert.True(key4Expiration.Value <= TimeSpan.FromMinutes(15));
        }
    }

    public virtual async Task GetAllExpirationAsync_WithLargeNumberOfKeys_ReturnsAllExpirations(int count)
    {
        // Arrange
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
                await cache.SetAsync(key, i, TimeSpan.FromMinutes(i % 60 + 1));
            }

            // Act
            var sw = Stopwatch.StartNew();
            var expirations = await cache.GetAllExpirationAsync(keys);
            sw.Stop();

            _logger.LogInformation("Get All Expiration Time ({Count} keys): {Elapsed:g}", count, sw.Elapsed);

            // Assert
            Assert.Equal(count, expirations.Count);
            Assert.All(expirations, kvp => Assert.NotNull(kvp.Value));
        }
    }

    public virtual async Task GetAllExpirationAsync_WithExpiredKeys_ExcludesExpiredKeys()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1, TimeSpan.FromMilliseconds(100));
            await cache.SetAsync("key2", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("key3", 3, TimeSpan.FromMilliseconds(100));

            // Wait for key1 and key3 to expire
            await Task.Delay(200);

            // Act
            var expirations = await cache.GetAllExpirationAsync(["key1", "key2", "key3"]);

            // Assert
            Assert.NotNull(expirations);
            Assert.Single(expirations); // Only key2 should be returned
            Assert.False(expirations.ContainsKey("key1")); // Expired
            Assert.True(expirations.ContainsKey("key2")); // Still valid
            Assert.False(expirations.ContainsKey("key3")); // Expired

            var key2Expiration = expirations["key2"];
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(10));
        }
    }
}
