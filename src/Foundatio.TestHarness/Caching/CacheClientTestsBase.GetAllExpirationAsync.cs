using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetAllExpirationAsync_WithMixedKeys_ReturnsExpectedResults()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set up keys with various states:
            // - expired-key: will expire before we query
            // - valid-key: has expiration, will be returned
            // - no-expiration-key: no expiration, should not be returned
            // - nonexistent-key: never created, should not be returned
            await cache.SetAsync("expired-key", 1, TimeSpan.FromMilliseconds(50));
            await cache.SetAsync("valid-key", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("no-expiration-key", 3);

            // Wait for expired-key to expire
            await Task.Delay(100);

            // Act
            var expirations = await cache.GetAllExpirationAsync(["expired-key", "valid-key", "no-expiration-key", "nonexistent-key"]);

            // Assert
            Assert.NotNull(expirations);
            Assert.Single(expirations); // Only valid-key should be returned

            Assert.False(expirations.ContainsKey("expired-key")); // Expired
            Assert.False(expirations.ContainsKey("no-expiration-key")); // No expiration
            Assert.False(expirations.ContainsKey("nonexistent-key")); // Doesn't exist

            Assert.True(expirations.TryGetValue("valid-key", out var validKeyExpiration));
            Assert.NotNull(validKeyExpiration);
            Assert.True(validKeyExpiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(validKeyExpiration.Value <= TimeSpan.FromMinutes(10));
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
}
